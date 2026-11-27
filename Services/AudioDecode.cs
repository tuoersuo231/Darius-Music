using System.IO;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace CurrentLyrics.Services;

/// <summary>把音频文件整段解成 16 kHz 单声道 float（int16 量级 [-32768, 32767]）。
///
/// 走 Media Foundation（NAudio 的 MediaFoundationReader）：mp3 / flac / m4a / wav 都能解，
/// 和播放器用的是同一套系统解码器，不需要额外装东西。重采样用 WDL，48000→16000 这类整数比
/// 几乎没有额外误差。</summary>
public static class AudioDecode
{
    public const int TargetRate = 16000;

    /// <summary>失败返回空数组（调用方据此"什么都不做"），不抛。</summary>
    public static float[] LoadMono16k(string path)
    {
        try
        {
            using var reader = new MediaFoundationReader(path);
            if (reader.WaveFormat.SampleRate <= 0) return Array.Empty<float>();

            ISampleProvider samples = reader.ToSampleProvider();
            if (samples.WaveFormat.Channels == 2) samples = new StereoToMonoSampleProvider(samples);
            else if (samples.WaveFormat.Channels > 2)
                samples = new MultiplexingSampleProvider(new[] { samples }, 1);
            if (samples.WaveFormat.SampleRate != TargetRate)
                samples = new WdlResamplingSampleProvider(samples, TargetRate);

            var chunk = new float[TargetRate * 4];
            var all = new List<float>(TargetRate * 240);
            int read;
            while ((read = samples.Read(chunk, 0, chunk.Length)) > 0)
            {
                for (int i = 0; i < read; i++) all.Add(chunk[i]);
            }
            if (all.Count == 0) return Array.Empty<float>();

            var result = all.ToArray();
            // 模型 metadata 的 normalize_samples=0：采样要按 int16 量级喂进去。
            for (int i = 0; i < result.Length; i++) result[i] *= 32768f;
            return result;
        }
        catch
        {
            return Array.Empty<float>();
        }
    }

    /// <summary>同一段音频的最长时长（秒），用来判断窗口是否越界。</summary>
    public static double DurationSeconds(string path)
    {
        try
        {
            using var reader = new MediaFoundationReader(path);
            return reader.TotalTime.TotalSeconds;
        }
        catch { return 0; }
    }

    public static string Describe(string path)
    {
        try
        {
            using var reader = new MediaFoundationReader(path);
            var f = reader.WaveFormat;
            return $"{f.SampleRate} Hz / {f.Channels} ch / {f.BitsPerSample} bit / {reader.TotalTime:mm\\:ss\\.fff}";
        }
        catch (Exception ex) { return "decode failed: " + ex.GetType().Name; }
    }
}
