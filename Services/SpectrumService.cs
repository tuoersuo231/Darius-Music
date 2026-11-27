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
    private float _fastDecay = 1f;
    private float _slowDecay = 1f;

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
        public string Name = "";
        public string Error = "";
        public double Energy;
    }

    private readonly List<Tap> _taps = new();
    private Tap? _active;
    private WaveFormat? _format;
    private double _sampleRate = 48000;
    private long _frames;
    private long _bytes;
    private float _peak;

    public SpectrumService() => Configure(SpectrumOptions.Current);

    /// <summary>条数（= 数组长度）。设置里改过 Bars 之后由 <see cref="Configure"/> 重新分配。</summary>
    public int BarCount { get { lock (_gate) return _bars; } }

    public bool Running { get { lock (_gate) return _taps.Count > 0; } }
    /// <summary>诊断用：正在喂数据的那块输出设备叫什么。</summary>
    public string ActiveDevice { get { lock (_gate) return _active?.Name ?? "-"; } }

    /// <summary>诊断用：每块被监听的声卡和它最近的能量。</summary>
    public string TapDiagnostics
    {
        get
        {
            lock (_gate)
                return _taps.Count == 0
                    ? "none"
                    : string.Join(" | ", _taps.Select(t =>
                        t.Error.Length > 0
                            ? $"{t.Name}=ERR:{t.Error}"
                            : $"{t.Name}={t.Energy:0.0000}"));
        }
    }
    public long BytesCaptured { get { lock (_gate) return _bytes; } }
    public long Frames { get { lock (_gate) return _frames; } }
    public float Peak { get { lock (_gate) return _peak; } }

    /// <summary>把 settings.json 里那份参数装进来。条数变了会重建缓冲。</summary>
    public void Configure(SpectrumSettings options)
    {
        lock (_gate)
        {
            _options = options;
            _bars = Math.Clamp(options.Bars, 4, 128);
            if (_fast.Length != _bars)
            {
                _fast = new float[_bars];
                _slow = new float[_bars];
            }
            RebuildRanges();
            UpdateDecay();
        }
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_taps.Count > 0 || !_options.Enabled) return;
            var taps = new List<Tap>();
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
                {
                    try
                    {
                        var loop = new WasapiLoopbackCapture(device);
                        taps.Add(new Tap { Capture = loop, Format = loop.WaveFormat, Name = device.FriendlyName });
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
        lock (_gate)
        {
            taps = new List<Tap>(_taps);
            _taps.Clear();
            _active = null;
        }
        foreach (var tap in taps)
        {
            try { tap.Capture.StopRecording(); } catch { }
            try { tap.Capture.Dispose(); } catch { }
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

            int channels = Math.Max(1, format.Channels);
            bool isFloat = format.Encoding == WaveFormatEncoding.IeeeFloat;
            int bytesPerSample = isFloat ? 4 : Math.Max(1, format.BitsPerSample / 8);
            int stride = bytesPerSample * channels;
            if (stride <= 0) return;

            for (int offset = 0; offset + stride <= e.BytesRecorded; offset += stride)
            {
                float value = 0;
                for (int c = 0; c < channels; c++)
                    value += ReadSample(e.Buffer, offset + c * bytesPerSample, isFloat);
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
        double boost = Math.Max(1.0, _options.BoostPerBar);
        float peak = 0;

        for (int bar = 0; bar < _bars; bar++)
        {
            var (start, end) = _ranges[bar];
            double loudest = 0;
            for (int bin = start; bin < end && bin < FftSize / 2; bin++)
            {
                double re = _buffer[bin].X;
                double im = _buffer[bin].Y;
                double magnitude = Math.Sqrt(re * re + im * im);
                if (magnitude > loudest) loudest = magnitude;
            }
            // 高频天生比低频弱 20~30 dB，按条号补回来（每条约 +1.2 dB），否则右边一半永远是 0。
            loudest *= Math.Pow(boost, bar);
            double db = 20 * Math.Log10(loudest + 1e-12);
            float value = (float)Math.Clamp((db - floorDb) / spanDb, 0, 1);
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
        int bars = Math.Max(1, _bars);
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