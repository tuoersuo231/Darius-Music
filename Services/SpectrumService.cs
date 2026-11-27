using NAudio.Dsp;
using NAudio.Wave;

namespace CurrentLyrics.Services;

/// <summary>
/// 系统输出的频谱（WASAPI loopback）。WinRT <c>MediaPlayer</c> 不暴露内部采样，所以只能抓回环；
/// 顺带把别的程序的声音也算进来 —— 对"桌面装饰"来说正好。
///
/// 参数照着 Rainmeter「简约频谱」皮肤（Wave.ini）来：FFT 1024、600–10000 Hz 对数分条、
/// 快衰减 150 ms（实心条）+ 慢衰减 800 ms（残影），两组叠加就是皮肤里那种"掉落"感。
/// </summary>
public sealed class SpectrumService : IDisposable
{
    public const int BarCount = 24;

    private const int FftSize = 1024;
    private const int FftLog2 = 10;
    private const double FreqMin = 600;
    // 皮肤是 600–10000 Hz，但流行歌的能量到 7~8 kHz 就基本没了，硬撑到 10 kHz 会让右边几根
    // 永远是 0。收到 8000 之后 24 条的能量分布明显均匀。
    private const double FreqMax = 8000;

    // 50 fps 下每帧该掉多少：150 ms 掉光 / 800 ms 掉光。
    private const float FastDecay = 1f / (0.15f * 50f);
    private const float SlowDecay = 1f / (0.80f * 50f);

    private readonly object _gate = new();
    private readonly float[] _fast = new float[BarCount];
    private readonly float[] _slow = new float[BarCount];
    private readonly List<float> _pending = new(8192);
    private readonly Complex[] _buffer = new Complex[FftSize];

    private (int Start, int End)[] _ranges = Array.Empty<(int, int)>();
    private WasapiLoopbackCapture? _capture;
    private WaveFormat? _format;
    private double _sampleRate = 48000;
    private long _frames;
    private long _bytes;
    private float _peak;

    public SpectrumService() => RebuildRanges();

    public bool Running { get { lock (_gate) return _capture != null; } }
    public long BytesCaptured { get { lock (_gate) return _bytes; } }
    public long Frames { get { lock (_gate) return _frames; } }
    public float Peak { get { lock (_gate) return _peak; } }

    public void Start()
    {
        lock (_gate)
        {
            if (_capture != null) return;
            try
            {
                var capture = new WasapiLoopbackCapture();
                _format = capture.WaveFormat;
                _sampleRate = _format.SampleRate;
                RebuildRanges();
                capture.DataAvailable += OnData;
                capture.StartRecording();
                _capture = capture;
            }
            catch
            {
                _capture = null;
                _format = null;
            }
        }
    }

    public void Stop()
    {
        WasapiLoopbackCapture? capture;
        lock (_gate)
        {
            capture = _capture;
            _capture = null;
        }
        if (capture == null) return;
        try { capture.StopRecording(); } catch { }
        try { capture.Dispose(); } catch { }
    }

    /// <summary>当前 24 条。快衰减那组画实心条，慢衰减那组画残影。</summary>
    public float[] Read(bool slow = false)
    {
        lock (_gate) return (slow ? _slow : _fast).ToArray();
    }

    private void OnData(object? sender, WaveInEventArgs e)
    {
        lock (_gate)
        {
            _bytes += e.BytesRecorded;
            var format = _format;
            if (format == null || e.BytesRecorded <= 0) return;

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

        float peak = 0;
        for (int bar = 0; bar < BarCount; bar++)
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
            // 高频天生比低频弱 20~30 dB，按条号补回来（每条约 +0.8 dB），否则右边一半永远是 0。
            loudest *= Math.Pow(1.14, bar);
            // 对数压动态范围。常数按实测：低频条 log10≈-2.9、高频条≈-4.0，
            // 落在 -4.2..-2.5 这段，所以下限 -4.2、跨度 1.7。
            float value = (float)Math.Clamp((Math.Log10(loudest + 1e-9) + 4.2) / 1.7, 0, 1);
            if (value > peak) peak = value;

            _fast[bar] = Math.Max(value, _fast[bar] - FastDecay);
            _slow[bar] = Math.Max(value, _slow[bar] - SlowDecay);
        }
        _peak = Math.Max(peak, _peak * 0.92f);
        _frames++;
    }

    /// <summary>对数分频：低频条窄、高频条宽，和皮肤 600–10000 Hz 的听感一致。</summary>
    private void RebuildRanges()
    {
        var ranges = new (int, int)[BarCount];
        double binHz = _sampleRate / FftSize;
        double logMin = Math.Log(FreqMin);
        double logMax = Math.Log(FreqMax);
        for (int i = 0; i < BarCount; i++)
        {
            double lo = Math.Exp(logMin + (logMax - logMin) * i / BarCount);
            double hi = Math.Exp(logMin + (logMax - logMin) * (i + 1) / BarCount);
            int start = Math.Max(1, (int)Math.Floor(lo / binHz));
            int end = Math.Max(start + 1, (int)Math.Ceiling(hi / binHz));
            ranges[i] = (start, end);
        }
        _ranges = ranges;
    }

    public void Dispose() => Stop();
}