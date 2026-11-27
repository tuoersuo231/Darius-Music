using System.IO;

namespace CurrentLyrics.Services;

/// <summary>
/// SenseVoice 的输入特征：Kaldi 风格 80 维 fbank（25 ms 窗 / 10 ms 移，hamming，snip_edges）+
/// LFR(7,6) 拼成 560 维、60 ms 一帧，再用模型自带的 CMVN 归一化。
///
/// 参数不是猜的，来自 sherpa-onnx 的 SenseVoice 实现：window_type=hamming、high_freq=0、
/// snip_edges=true、frame_shift 10 ms、LFR 位移 = 子采样倍率。
/// </summary>
public static class SenseVoiceFeatures
{
    public const int SampleRate = 16000;
    public const int FrameLength = 400;   // 25 ms
    public const int FrameShift = 160;    // 10 ms
    public const int FftSize = 512;
    public const int NumBins = 80;
    public const int NumMels = 80;
    public const int LowFreq = 20;
    public const int HighFreq = 0;        // 0 表示到 nyquist

    private static readonly float[] Window = BuildHamming(FrameLength);
    private static readonly float[][] MelWeights = BuildMelBanks();

    private static float[] BuildHamming(int n)
    {
        var w = new float[n];
        for (int i = 0; i < n; i++) w[i] = (float)(0.54 - 0.46 * Math.Cos(2.0 * Math.PI * i / (n - 1)));
        return w;
    }

    private static float MelScale(float hz) => 1127f * MathF.Log(1f + hz / 700f);
    private static float MelScaleInverse(float mel) => 700f * (MathF.Exp(mel / 1127f) - 1f);

    /// <summary>Kaldi 的三角滤波器组：mel 轴上等距，不做面积归一化。</summary>
    private static float[][] BuildMelBanks()
    {
        int bins = FftSize / 2 + 1;                 // 257
        float fftBinWidth = (float)SampleRate / FftSize;
        float high = HighFreq <= 0 ? SampleRate / 2f : HighFreq;
        float melLow = MelScale(LowFreq);
        float melHigh = MelScale(high);
        float delta = (melHigh - melLow) / (NumMels + 1);

        var banks = new float[NumMels][];
        for (int m = 0; m < NumMels; m++)
        {
            var w = new float[bins];
            float left = MelScaleInverse(melLow + m * delta) / fftBinWidth;
            float center = MelScaleInverse(melLow + (m + 1) * delta) / fftBinWidth;
            float right = MelScaleInverse(melLow + (m + 2) * delta) / fftBinWidth;
            for (int i = 0; i < bins; i++)
            {
                float f = i;
                if (f >= left && f <= center) w[i] = (f - left) / MathF.Max(center - left, 1e-9f);
                else if (f > center && f <= right) w[i] = (right - f) / MathF.Max(right - center, 1e-9f);
            }
            banks[m] = w;
        }
        return banks;
    }

    /// <summary>返回 [帧数, 80] 的 fbank，已取 log。输入采样必须是 int16 量级（[-32768,32767]），
    /// 与模型 metadata 的 normalize_samples=0 一致。</summary>
    public static float[][] Fbank(float[] samples)
    {
        int frames = samples.Length < FrameLength ? 0 : 1 + (samples.Length - FrameLength) / FrameShift;
        if (frames <= 0) return Array.Empty<float[]>();

        var re = new float[FftSize];
        var im = new float[FftSize];
        var outFrames = new float[frames][];
        var frame = new float[FrameLength];

        for (int t = 0; t < frames; t++)
        {
            int start = t * FrameShift;
            // 1) 去直流（Kaldi: dither=0 -> remove_dc_offset -> preemphasize -> window）
            double mean = 0;
            for (int i = 0; i < FrameLength; i++) mean += samples[start + i];
            mean /= FrameLength;
            for (int i = 0; i < FrameLength; i++) frame[i] = samples[start + i] - (float)mean;

            // 2) 预加重，x[-1] 取 x[0]
            float prev = frame[0];
            for (int i = 0; i < FrameLength; i++)
            {
                float cur = frame[i];
                frame[i] = cur - 0.97f * prev;
                prev = cur;
            }

            // 3) hamming 窗 + 补零到 512
            Array.Clear(re, 0, FftSize);
            Array.Clear(im, 0, FftSize);
            for (int i = 0; i < FrameLength; i++) re[i] = frame[i] * Window[i];

            Fft(re, im);

            var mel = new float[NumMels];
            for (int m = 0; m < NumMels; m++)
            {
                var w = MelWeights[m];
                float sum = 0;
                for (int k = 0; k < w.Length; k++)
                {
                    if (w[k] == 0) continue;
                    sum += w[k] * (re[k] * re[k] + im[k] * im[k]);
                }
                mel[m] = MathF.Log(MathF.Max(sum, 1.1920929E-07f));
            }
            outFrames[t] = mel;
        }
        return outFrames;
    }

    /// <summary>LFR：把 lfrM 帧拼成一行，步长 lfrN；开头补 (m-1)/2 个首帧，末尾不足时重复末帧。</summary>
    public static float[][] ApplyLfr(float[][] frames, int lfrM, int lfrN)
    {
        int t = frames.Length;
        if (t == 0) return Array.Empty<float[]>();
        int outCount = (int)Math.Ceiling((double)t / lfrN);
        int pad = (lfrM - 1) / 2;
        int dim = frames[0].Length;
        var outRows = new float[outCount][];

        for (int i = 0; i < outCount; i++)
        {
            var row = new float[dim * lfrM];
            for (int j = 0; j < lfrM; j++)
            {
                int src = i * lfrN + j - pad;
                if (src < 0) src = 0;
                if (src >= t) src = t - 1;
                Array.Copy(frames[src], 0, row, j * dim, dim);
            }
            outRows[i] = row;
        }
        return outRows;
    }

    /// <summary>按模型 metadata 里的 neg_mean / inv_stddev 做 CMVN，就地改。</summary>
    public static void ApplyCmvn(float[][] rows, float[] negMean, float[] invStd)
    {
        foreach (var row in rows)
        {
            for (int i = 0; i < row.Length; i++)
                row[i] = (row[i] + negMean[i]) * invStd[i];
        }
    }

    private static void Fft(float[] re, float[] im)
    {
        int n = re.Length;
        // 位反转置换
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j)
            {
                (re[i], re[j]) = (re[j], re[i]);
                (im[i], im[j]) = (im[j], im[i]);
            }
        }
        for (int len = 2; len <= n; len <<= 1)
        {
            double angle = -2 * Math.PI / len;
            float wRe = (float)Math.Cos(angle), wIm = (float)Math.Sin(angle);
            for (int i = 0; i < n; i += len)
            {
                float curRe = 1, curIm = 0;
                for (int k = 0; k < len / 2; k++)
                {
                    int a = i + k, b = i + k + len / 2;
                    float uRe = re[a], uIm = im[a];
                    float vRe = re[b] * curRe - im[b] * curIm;
                    float vIm = re[b] * curIm + im[b] * curRe;
                    re[a] = uRe + vRe; im[a] = uIm + vIm;
                    re[b] = uRe - vRe; im[b] = uIm - vIm;
                    float nextRe = curRe * wRe - curIm * wIm;
                    curIm = curRe * wIm + curIm * wRe;
                    curRe = nextRe;
                }
            }
        }
    }

    /// <summary>把 "a,b,c" 形式的 metadata 拆成 float[]。</summary>
    public static float[] ParseCsv(string csv)
    {
        var parts = csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var result = new float[parts.Length];
        for (int i = 0; i < parts.Length; i++)
            result[i] = float.TryParse(parts[i], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0f;
        return result;
    }
}
