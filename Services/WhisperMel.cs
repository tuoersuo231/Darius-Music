using System;
using System.Threading.Tasks;

namespace CurrentLyrics.Services;

/// <summary>
/// Whisper 的输入特征：log-mel 80 缁达紝16 kHz / 25 ms 窗 / 10 ms 鎸?锛岃緭鍑?0 ms 涓€甯с€?
/// 鍜?SenseVoice 鐨?fbank 涓嶅悓锛歨ann 绐?+ 201 鏉?DFT + librosa Slaney mel锛岀劧鍚?log10 鍐嶅帇鍒?8 涓?鏁伴噺绾э紝
/// 鏈€鍚?<c>(x + 4) / 4</c> 褰掍竴鍖栧埌 [-1, 1]銆?
/// </summary>
public static class WhisperMel
{
    public const int SampleRate = 16000;
    public const int FftSize = 400;
    public const int Hop = 160;            // 10 ms
    public const int NumMels = 80;
    public const int NumBins = FftSize / 2 + 1;   // 201
    public const int EncoderFrames = 1500;        // 30 s
    public const int ChunkFrames = 3000;          // 30 s 的 mel 帧数

    private static readonly float[] Window = BuildHann(FftSize);
    private static readonly float[] CosTable = BuildTrig(true);
    private static readonly float[] SinTable = BuildTrig(false);
    private static readonly float[] MelFilters = BuildMelFilterbank();

    private static float[] BuildHann(int n)
    {
        // torch.hann_window(400) 是 periodic Hann。
        // AudioDecode 给的是 int16 量级（[-32768, 32767]），whisper 要 [-1, 1]，所以这里一并缩掉。
        var w = new float[n];
        for (int i = 0; i < n; i++)
            w[i] = (float)((0.5 - 0.5 * Math.Cos(2.0 * Math.PI * i / n)) / 32768.0);
        return w;
    }

    private static float[] BuildTrig(bool cos)
    {
        var t = new float[NumBins * FftSize];
        for (int k = 0; k < NumBins; k++)
        {
            double w = 2.0 * Math.PI * k / FftSize;
            for (int n = 0; n < FftSize; n++)
                t[k * FftSize + n] = (float)(cos ? Math.Cos(w * n) : Math.Sin(w * n));
        }
        return t;
    }

    // ---- librosa.filters.mel(sr=16000, n_fft=400, n_mels=80, htk=False, norm='slaney') ----
    private const double MinLogHz = 1000.0;
    private const double FSp = 200.0 / 3.0;

    private static double HzToMel(double hz)
    {
        if (hz < MinLogHz) return hz / FSp;
        double minLogMel = MinLogHz / FSp;
        double logStep = Math.Log(6.4) / 27.0;
        return minLogMel + Math.Log(hz / MinLogHz) / logStep;
    }

    private static double MelToHz(double mel)
    {
        double minLogMel = MinLogHz / FSp;
        if (mel < minLogMel) return mel * FSp;
        double logStep = Math.Log(6.4) / 27.0;
        return MinLogHz * Math.Exp(logStep * (mel - minLogMel));
    }

    private static float[] BuildMelFilterbank()
    {
        // 每个滤波器在 mel 轴上 3 个点 (f[i], f[i+1], f[i+2])，三角
        double minMel = HzToMel(0.0);
        double maxMel = HzToMel(SampleRate / 2.0);
        var melF = new double[NumMels + 2];
        var hzF = new double[NumMels + 2];
        for (int i = 0; i < NumMels + 2; i++)
        {
            melF[i] = minMel + (maxMel - minMel) * i / (NumMels + 1);
            hzF[i] = MelToHz(melF[i]);
        }

        var w = new float[NumMels * NumBins];
        for (int m = 0; m < NumMels; m++)
        {
            double low = hzF[m];
            double center = hzF[m + 1];
            double high = hzF[m + 2];
            // Slaney 归一化：面积（hz 宽度）归一
            double enorm = 2.0 / (high - low);
            for (int k = 0; k < NumBins; k++)
            {
                double hz = k * (double)SampleRate / FftSize;
                double v = 0.0;
                if (hz > low && hz <= center) v = (hz - low) / (center - low);
                else if (hz > center && hz < high) v = (high - hz) / (high - center);
                w[m * NumBins + k] = (float)(v * enorm);
            }
        }
        return w;
    }

    /// <summary>帧数：center=true 时是 samples/hop + 1。</summary>
    public static int FrameCount(int samples) => samples / Hop + 1;

    /// <summary>
    /// 算 log-mel。返回 [NumMels * frames]，布局是 mel 主序（mel * frames + t），
    /// 正好是 ONNX 输入 [1, 80, T] 的内存顺序。
    /// </summary>
    public static float[] Compute(float[] pcm, int frames)
    {
        var power = new float[frames * NumBins];
        Parallel.For(0, frames, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1) }, t =>
        {
            int center = t * Hop;
            Span<float> re = stackalloc float[NumBins];
            Span<float> im = stackalloc float[NumBins];
            for (int n = 0; n < FftSize; n++)
            {
                int idx = center - FftSize / 2 + n;
                // reflect padding（torch.stft 的 center=True, pad_mode='reflect'）
                if (idx < 0) idx = -idx;
                else if (idx >= pcm.Length) idx = 2 * pcm.Length - 2 - idx;
                if (idx < 0) idx = 0;
                if (idx >= pcm.Length) idx = pcm.Length - 1;
                float x = pcm[idx] * Window[n];
                for (int k = 0; k < NumBins; k++)
                {
                    re[k] += x * CosTable[k * FftSize + n];
                    im[k] -= x * SinTable[k * FftSize + n];
                }
            }
            for (int k = 0; k < NumBins; k++)
                power[t * NumBins + k] = re[k] * re[k] + im[k] * im[k];
        });

        var mel = new float[NumMels * frames];
        float globalMax = float.NegativeInfinity;
        // mel 过滤 + log10
        Parallel.For(0, frames, t =>
        {
            for (int m = 0; m < NumMels; m++)
            {
                float acc = 0f;
                int off = m * NumBins;
                int poff = t * NumBins;
                for (int k = 0; k < NumBins; k++) acc += MelFilters[off + k] * power[poff + k];
                if (acc < 1e-10f) acc = 1e-10f;
                mel[m * frames + t] = (float)Math.Log10(acc);
            }
        });
        for (int i = 0; i < mel.Length; i++) if (mel[i] > globalMax) globalMax = mel[i];
        float floor = globalMax - 8f;
        for (int i = 0; i < mel.Length; i++)
        {
            float v = mel[i];
            if (v < floor) v = floor;
            mel[i] = (v + 4f) / 4f;
        }
        return mel;
    }

    /// <summary>
    /// 把 mel 摆进 [1, 80, 3000] 的输入张量；尾部补 0（和 whisper 的 pad_or_trim 一致）。
    /// </summary>
    public static float[] ToEncoderInput(float[] mel, int frames)
    {
        int used = Math.Min(frames, ChunkFrames);
        var input = new float[NumMels * ChunkFrames];
        for (int m = 0; m < NumMels; m++)
            Array.Copy(mel, m * frames, input, m * ChunkFrames, used);
        return input;
    }

    /// <summary>整段 pcm 的 mel，返回帧数。</summary>
    public static float[] ComputeAll(float[] pcm, out int frames)
    {
        frames = Math.Min(FrameCount(pcm.Length), ChunkFrames);
        return Compute(pcm, frames);
    }
}
