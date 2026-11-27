using NAudio.CoreAudioApi;
using NAudio.Dsp;
using NAudio.Wave;

namespace CurrentLyrics.Services;

/// <summary>
/// 系统输出的频谱（WASAPI loopback）。WinRT <c>MediaPlayer</c> 不暴露内部采样，所以只能抓回环；
/// 顺带把别的程序的声音也算进来 —— 对"桌面装饰"来说正好。
///
/// 所有可调参数都在 <see cref="SpectrumSettings"/>（settings.json 的 <c>Spectrum</c> 节点）。
/// FFT 固定 1024 点、50% overlap，这是皮肤 Wave.ini 的值，一般不用动。
/// </summary>
public sealed class SpectrumService : IDisposable
{
    private const int FftSize = 1024;
    private const int FftLog2 = 10;

    private readonly object _gate = new();
    private readonly List<float> _pending = new(8192);
    private readonly Complex[] _buffer = new Complex[FftSize];

    private SpectrumSettings _options = SpectrumOptions.Current;
    private float[] _fast = Array.Empty<float>();
    private float[] _slow = Array.Empty<float>();
    private (int Start, int End)[] _ranges = Array.Empty<(int, int)>();
    private int _bars;
    private int _bands;
    private double[] _boostCurve = Array.Empty<double>();
    private float _fastDecay = 1f;
    private float _slowDecay = 1f;
    private double[] _db = Array.Empty<double>();
    private float _refDb = float.NaN;   // 慢跟随的"当前最响的那条"
    private float _gainDb;              // 自动增益（dB）

    /// <summary>
    /// 一路回环。机器上经常不止一个活动的输出设备（内置声卡 + USB 音箱 + 耳机），
    /// 而 Windows 11 允许把某个程序的输出单独指到其中某一个 —— 只抓"默认设备"的话，
    /// 程序自己播的歌可能根本不在那上面，频谱会一直是 0。
    /// 所以每块声卡都抓，只用当前最响的那一路喂 FFT。
    /// </summary>
    private sealed class Tap
    {
        public WasapiLoopbackCapture Capture = null!;
        public WaveFormat Format = null!;
        public MMDevice Device = null!;
        public string Name = "";
        public string Error = "";
        public double Energy;
        public int NonZero;
    }

    private readonly List<Tap> _taps = new();
    private Tap? _active;
    private ProcessLoopbackCapture? _process;
    private bool _ownOnly;
    private bool _requestedOwnOnly;
    private string _processError = "";
    private WaveFormat? _format;
    private double _sampleRate = 48000;
    private long _frames;
    private long _bytes;
    private float _peak;

    public SpectrumService() => Configure(SpectrumOptions.Current);

    /// <summary>条数（= 数组长度）。设置里改过 Bars 之后由 <see cref="Configure"/> 重新分配。</summary>
    public int BarCount { get { lock (_gate) return _bars; } }

    public bool Running { get { lock (_gate) return _taps.Count > 0 || _process != null; } }
    /// <summary>诊断用：正在喂数据的那块输出设备叫什么。</summary>
    public string ActiveDevice
    {
        get
        {
            lock (_gate)
                return _process != null ? "本程序（进程回环）" : _active?.Name ?? "-";
        }
    }

    /// <summary>诊断用：每块被监听的声卡和它最近的能量。</summary>
    public string TapDiagnostics
    {
        get
        {
            lock (_gate)
            {
                if (_process != null)
                    return _process.Running ? "process-loopback=ok" : "process-loopback=" + _process.Error;
                var prefix = _processError.Length > 0 ? "process=ERR:" + _processError + " | " : "";
                return prefix + (_taps.Count == 0
                    ? "none"
                    : string.Join(" | ", _taps.Select(t =>
                        t.Error.Length > 0
                            ? $"{t.Name}=ERR:{t.Error}"
                            : $"{t.Name}={t.Energy:0.0000}/peak={MasterPeak(t):0.0000}/nz={t.NonZero}/{Fmt(t)}")));
            }
        }
    }
    public long BytesCaptured { get { lock (_gate) return _bytes; } }
    public long Frames { get { lock (_gate) return _frames; } }
    public float Peak { get { lock (_gate) return _peak; } }

    /// <summary>诊断用：这块设备的系统级峰值表（它自己有声音时 &gt; 0）。</summary>
    private static double MasterPeak(Tap tap)
    {
        try { return tap.Device?.AudioMeterInformation?.MasterPeakValue ?? 0; }
        catch { return 0; }
    }

    /// <summary>诊断用：采样格式，比如 "float32 48000 2ch"。</summary>
    private static string Fmt(Tap tap)
    {
        var f = tap.Format;
        if (f == null) return "?";
        string enc = f.Encoding == WaveFormatEncoding.IeeeFloat ? "float"
            : f.Encoding == WaveFormatEncoding.Extensible ? "ext"
            : "pcm";
        return $"{enc}{f.BitsPerSample} {f.SampleRate} {f.Channels}ch";
    }

    /// <summary>把 settings.json 里那份参数装进来。条数变了会重建缓冲。</summary>
    public void Configure(SpectrumSettings options)
    {
        bool modeChanged;
        lock (_gate)
        {
            _options = options;
            modeChanged = _requestedOwnOnly != options.OwnAudioOnly;
            _requestedOwnOnly = options.OwnAudioOnly;
            _bars = Math.Clamp(options.Bars, 4, 128);
            // 镜像模式下左右两半共用一个频段数组：N 根柱子只对应 (N+1)/2 个频段。
            // 以前这里按 N 建频段，结果镜像时只画得到 600~2200 Hz，右半边的频段永远没被读出来。
            _bands = options.Mirror ? Math.Max(1, (_bars + 1) / 2) : _bars;
            if (_fast.Length != _bands)
            {
                _fast = new float[_bands];
                _slow = new float[_bands];
            }
            if (_db.Length != _bands) _db = new double[_bands];
            RebuildBoost();
            RebuildRanges();
            UpdateDecay();
        }
        // 换"只抓本程序"和"抓整块声卡"必须重开一路回环，放到锁外面做，别在持锁时 join 后台线程。
        if (modeChanged && (_taps.Count > 0 || _process != null))
        {
            Stop();
            Start();
        }
    }

    /// <summary>
    /// 逐条增益原来是 pow(BoostPerBar, i)，条数一多就爆：78 根柱子 = 39 个频段，
    /// 1.316^38 ≈ +90 dB，低频那半个频谱被压成 0，画出来就是"中间一片死水、只有边上会动"。
    /// 现在先按滑块算"整条总共抬多少 dB"，再平均摊到每个频段上，跟条数无关。
    /// BoostPerBar 仍记 1.0~1.6：1.14 ≈ 整条 +5.6 dB，到 1.6 封顶 +24 dB。
    /// </summary>
    private void RebuildBoost()
    {
        int bands = Math.Max(1, _bands);
        double boost = Math.Clamp(_options.BoostPerBar, 1.0, 1.6);
        double totalDb = Math.Clamp((boost - 1.0) * 40.0, 0, 24);
        var curve = new double[bands];
        double perBand = bands > 1 ? Math.Pow(10, totalDb / 20.0 / (bands - 1)) : 1.0;
        double value = 1.0;
        for (int i = 0; i < bands; i++)
        {
            curve[i] = value;
            value *= perBand;
        }
        _boostCurve = curve;
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_taps.Count > 0 || _process != null || !_options.Enabled) return;
            // 只要本程序自己的声音：用进程回环；拿不到就退回设备回环（至少还能动）。
            if (_options.OwnAudioOnly)
            {
                var process = new ProcessLoopbackCapture();
                process.Data += OnProcessData;
                if (process.Start(Environment.ProcessId))
                {
                    _process = process;
                    _ownOnly = true;
                    _processError = "";
                    if (process.Format != null)
                    {
                        _format = process.Format;
                        _sampleRate = process.Format.SampleRate;
                        RebuildRanges();
                        UpdateDecay();
                    }
                    return;
                }
                // 失败原因留给诊断；然后照常走设备回环。
                _processError = process.Error;
                process.Data -= OnProcessData;
                process.Dispose();
                _ownOnly = false;
            }
            var taps = new List<Tap>();
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
                {
                    try
                    {
                        var loop = new WasapiLoopbackCapture(device);
                        taps.Add(new Tap { Capture = loop, Format = loop.WaveFormat, Device = device, Name = device.FriendlyName });
                    }
                    catch { }
                }
            }
            catch { }
            if (taps.Count == 0)
            {
                // 枚举失败就退回"默认设备"，至少不比以前差。
                try
                {
                    var loop = new WasapiLoopbackCapture();
                    taps.Add(new Tap { Capture = loop, Format = loop.WaveFormat, Name = "默认设备" });
                }
                catch { }
            }
            if (taps.Count == 0) return;

            _sampleRate = taps[0].Format.SampleRate;
            RebuildRanges();
            UpdateDecay();
            foreach (var tap in taps)
            {
                tap.Capture.DataAvailable += (_, e) => OnData(tap, e);
                try { tap.Capture.StartRecording(); }
                catch (Exception ex) { tap.Error = ex.GetType().Name + ":" + ex.Message; }
            }
            _taps.AddRange(taps);
            _active = taps[0];
            _format = taps[0].Format;
        }
    }

    public void Stop()
    {
        List<Tap> taps;
        ProcessLoopbackCapture? process;
        lock (_gate)
        {
            taps = new List<Tap>(_taps);
            _taps.Clear();
            _active = null;
            process = _process;
            _process = null;
        }
        if (process != null)
        {
            process.Data -= OnProcessData;
            try { process.Dispose(); } catch { }
        }
        foreach (var tap in taps)
        {
            try { tap.Capture.StopRecording(); } catch { }
            try { tap.Capture.Dispose(); } catch { }
        }
    }

    /// <summary>进程回环送来的 PCM（在它自己的后台线程上）。</summary>
    private void OnProcessData(byte[] buffer, int count)
    {
        lock (_gate)
        {
            var format = _format;
            if (format == null || count <= 0) return;
            _bytes += count;
            Ingest(buffer, count, format);
        }
    }

    /// <summary>当前各条。快衰减那组画实心条，慢衰减那组画残影。</summary>
    public float[] Read(bool slow = false)
    {
        lock (_gate) return (slow ? _slow : _fast).ToArray();
    }

    private void OnData(Tap tap, WaveInEventArgs e)
    {
        lock (_gate)
        {
            _bytes += e.BytesRecorded;
            var format = tap.Format;
            if (format == null || e.BytesRecorded <= 0) return;

            tap.Energy = tap.Energy * 0.6 + EstimateEnergy(e.Buffer, e.BytesRecorded, format) * 0.4;
            // 缓冲区里到底有没有字（非 0 字节个数），用来区分"抓到的就是静音"和"解码解错了"。
            int nz = 0;
            for (int i = 0; i + 1 < e.BytesRecorded; i += 64)
                if (e.Buffer[i] != 0 || e.Buffer[i + 1] != 0) nz++;
            tap.NonZero = nz;

            // 换成当前最响的那一路。带 2 倍迟滞，免得两块都没声时来回跳。
            var best = _active;
            foreach (var other in _taps)
                if (best == null || other.Energy > best.Energy) best = other;
            if (best != null && best != _active && best.Energy > (_active?.Energy ?? 0) * 2 + 1e-9)
            {
                _active = best;
                _format = best.Format;
                if (Math.Abs(best.Format.SampleRate - _sampleRate) > 1)
                {
                    _sampleRate = best.Format.SampleRate;
                    RebuildRanges();
                    UpdateDecay();
                }
                _pending.Clear();   // 换设备就别把两边的采样拼在一起
            }
            if (tap != _active) return;
            Ingest(e.Buffer, e.BytesRecorded, format);
        }
    }

    /// <summary>把一块 PCM 塞进 FFT 管线。调用方必须已经持有 _gate。</summary>
    private void Ingest(byte[] buffer, int count, WaveFormat format)
    {
        int channels = Math.Max(1, format.Channels);
        bool isFloat = format.Encoding == WaveFormatEncoding.IeeeFloat;
        int bytesPerSample = isFloat ? 4 : Math.Max(1, format.BitsPerSample / 8);
        int stride = bytesPerSample * channels;
        if (stride <= 0) return;

        for (int offset = 0; offset + stride <= count; offset += stride)
        {
            float value = 0;
            for (int c = 0; c < channels; c++)
                value += ReadSample(buffer, offset + c * bytesPerSample, isFloat);
            _pending.Add(value / channels);
        }

        int consumed = 0;
        while (_pending.Count - consumed >= FftSize)
        {
            Analyse(consumed);
            consumed += FftSize / 2;   // 50% overlap，和皮肤一致
        }
        if (consumed > 0) _pending.RemoveRange(0, consumed);
        // 攒得太多说明没人来取（比如 UI 被关掉了），别让它无限长大。
        if (_pending.Count > FftSize * 8) _pending.RemoveRange(0, _pending.Count - FftSize * 8);
    }

    /// <summary>粗略能量（抽样算均方），只用来判断"哪块声卡在响"，不用精确。</summary>
    private static double EstimateEnergy(byte[] buffer, int count, WaveFormat format)
    {
        bool isFloat = format.Encoding == WaveFormatEncoding.IeeeFloat;
        int bytesPerSample = isFloat ? 4 : Math.Max(1, format.BitsPerSample / 8);
        int channels = Math.Max(1, format.Channels);
        int stride = bytesPerSample * channels;
        if (stride <= 0) return 0;
        double sum = 0;
        int n = 0;
        for (int offset = 0; offset + stride <= count; offset += stride * 8)
        {
            double value = 0;
            for (int c = 0; c < channels; c++)
                value += ReadSample(buffer, offset + c * bytesPerSample, isFloat);
            value /= channels;
            sum += value * value;
            n++;
        }
        return n == 0 ? 0 : sum / n;
    }

    private static float ReadSample(byte[] buffer, int offset, bool isFloat)
    {
        if (offset + (isFloat ? 4 : 2) > buffer.Length) return 0;
        return isFloat
            ? BitConverter.ToSingle(buffer, offset)
            : BitConverter.ToInt16(buffer, offset) / 32768f;
    }

    private void Analyse(int offset)
    {
        for (int i = 0; i < FftSize; i++)
        {
            double window = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (FftSize - 1));
            _buffer[i].X = (float)(_pending[offset + i] * window);
            _buffer[i].Y = 0;
        }
        FastFourierTransform.FFT(true, FftLog2, _buffer);

        double floorDb = _options.FloorDb;
        double spanDb = Math.Max(1.0, _options.SpanDb);
        float peak = 0;
        double frameMax = double.NegativeInfinity;

        for (int bar = 0; bar < _bands; bar++)
        {
            var (start, end) = _ranges[bar];
            // 取频段内的 RMS 而不是"最大那根 bin"：低频道段只有一两根 bin、高频有十几根，
            // 取最大值会让高频凭空高出 10~15 dB —— 再叠加逐条增益，中间（低频）那几根就永远算成 0，
            // 看起来就是"频谱中间断掉一块"。RMS 和频段宽窄无关，各段才在一个量级上。
            double sumPower = 0;
            int bins = 0;
            for (int bin = start; bin < end && bin < FftSize / 2; bin++)
            {
                double re = _buffer[bin].X;
                double im = _buffer[bin].Y;
                sumPower += re * re + im * im;
                bins++;
            }
            double loudest = bins > 0 ? Math.Sqrt(sumPower / bins) : 0;
            // 高频天生比低频弱，按条号补回来，否则右边一半永远是 0。
            loudest *= _boostCurve.Length > bar ? _boostCurve[bar] : 1.0;
            double db = 20 * Math.Log10(loudest + 1e-12);
            _db[bar] = db;
            if (db > frameMax) frameMax = db;
        }

        // 自动增益：音量小的时候（比如系统音量只有 30%），最响的那条可能只到 -46 dB，
        // 按固定的 FloorDb/SpanDb 映射就永远是 0 —— 用户看到的就是"柱子很矮 / 干脆看不见"。
        // 这里让最响的那条自动爬到 FloorDb + SpanDb*0.92，参考电平快上慢落，听起来才不抽。
        double gain = 0;
        if (_options.AutoGain && frameMax > -90)
        {
            if (float.IsNaN(_refDb)) _refDb = (float)frameMax;
            else if (frameMax > _refDb) _refDb = (float)frameMax;
            else _refDb -= (float)((_refDb - frameMax) * 0.004);
            if (_refDb < -110) _refDb = -110;
            double target = floorDb + spanDb * 0.92;
            float want = (float)Math.Clamp(target - _refDb, -30, 40);
            _gainDb += (want - _gainDb) * 0.01f;
            gain = _gainDb;
        }

        for (int bar = 0; bar < _bands; bar++)
        {
            float value = (float)Math.Clamp((_db[bar] + gain - floorDb) / spanDb, 0, 1);
            if (value > peak) peak = value;

            _fast[bar] = Math.Max(value, _fast[bar] - _fastDecay);
            _slow[bar] = Math.Max(value, _slow[bar] - _slowDecay);
        }
        _peak = Math.Max(peak, _peak * 0.92f);
        _frames++;
    }

    /// <summary>
    /// 每帧该掉多少。旧版按"50 fps"硬算，但 FFT 实际是 sampleRate/(FftSize/2) ≈ 94 次/秒，
    /// 于是 150 ms 的衰减实际只用了 80 ms —— 皮肤那种"粘住再掉"的感觉就没了。
    /// </summary>
    private void UpdateDecay()
    {
        double fftRate = _sampleRate / (FftSize / 2.0);
        if (fftRate < 1) fftRate = 1;
        _fastDecay = (float)(1.0 / Math.Max(1.0, _options.FastMs / 1000.0 * fftRate));
        _slowDecay = (float)(1.0 / Math.Max(1.0, _options.SlowMs / 1000.0 * fftRate));
    }

    /// <summary>对数分频：低频条窄、高频条宽，和皮肤 600–8000 Hz 的听感一致。</summary>
    private void RebuildRanges()
    {
        int bars = Math.Max(1, _bands);
        var ranges = new (int, int)[bars];
        double binHz = _sampleRate / FftSize;
        double low = Math.Max(20, Math.Min(_options.FreqMin, _options.FreqMax - 20));
        double high = Math.Max(low + 20, _options.FreqMax);
        double logMin = Math.Log(low);
        double logMax = Math.Log(high);
        for (int i = 0; i < bars; i++)
        {
            double lo = Math.Exp(logMin + (logMax - logMin) * i / bars);
            double hi = Math.Exp(logMin + (logMax - logMin) * (i + 1) / bars);
            int start = Math.Max(1, (int)Math.Floor(lo / binHz));
            int end = Math.Max(start + 1, (int)Math.Ceiling(hi / binHz));
            ranges[i] = (start, end);
        }
        _ranges = ranges;
    }

    public void Dispose() => Stop();
}