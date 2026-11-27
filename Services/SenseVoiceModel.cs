using System.IO;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace CurrentLyrics.Services;

/// <summary>
/// SenseVoice（FunAudioLLM，CTC 非自回归）的本地推理壳。只注册 DirectML 执行提供程序；
/// 开了 session.disable_cpu_ep_fallback，任何一个算子落不到 GPU 上都会在建会话时就抛异常 ——
/// 这正是"没有 GPU 就什么都不做"的硬闸，而不是跑起来再降级到 CPU。
/// </summary>
public sealed class SenseVoiceModel : IDisposable
{
    private readonly InferenceSession _session;
    private readonly string[] _tokens;
    private readonly Dictionary<string, int> _tokenIds = new(StringComparer.Ordinal);

    public int LfrM { get; }
    public int LfrN { get; }
    public int FeatureDim => SenseVoiceFeatures.NumMels * LfrM;
    public float[] NegMean { get; }
    public float[] InvStd { get; }
    public int Language { get; }
    public int TextNorm { get; }
    public int BlankId { get; }
    public long LoadMs { get; init; }
    public string ExecutionProvider { get; }

    private SenseVoiceModel(InferenceSession session, string[] tokens, int lfrM, int lfrN,
        float[] negMean, float[] invStd, int language, int textNorm, int blank, string ep)
    {
        _session = session;
        _tokens = tokens;
        LfrM = lfrM;
        LfrN = lfrN;
        NegMean = negMean;
        InvStd = invStd;
        Language = language;
        TextNorm = textNorm;
        BlankId = blank;
        ExecutionProvider = ep;
        for (int i = 0; i < tokens.Length; i++) _tokenIds.TryAdd(tokens[i], i);
    }

    /// <summary>建会话。gpuOnly=true 时禁止 CPU 回退：模型里有 GPU 不支持的算子就直接失败。</summary>
    public static SenseVoiceModel Load(string modelPath, string tokensPath, int deviceId = 0, bool gpuOnly = true,
        bool verbose = false, bool profile = false)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var options = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            EnableCpuMemArena = false,
            EnableProfiling = profile,
        };
        if (verbose) options.LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_VERBOSE;
        options.AppendExecutionProvider_DML(deviceId);
        if (gpuOnly) options.AddSessionConfigEntry("session.disable_cpu_ep_fallback", "1");

        var session = new InferenceSession(modelPath, options);
        watch.Stop();

        var meta = session.ModelMetadata.CustomMetadataMap;
        int lfrM = meta.TryGetValue("lfr_window_size", out var m) ? int.Parse(m) : 7;
        int lfrN = meta.TryGetValue("lfr_window_shift", out var n) ? int.Parse(n) : 6;
        var negMean = meta.TryGetValue("neg_mean", out var nm) ? SenseVoiceFeatures.ParseCsv(nm) : Array.Empty<float>();
        var invStd = meta.TryGetValue("inv_stddev", out var isd) ? SenseVoiceFeatures.ParseCsv(isd) : Array.Empty<float>();

        int language = meta.TryGetValue("lang_ja", out var ja) ? int.Parse(ja) : 0;
        int textNorm = meta.TryGetValue("without_itn", out var wo) ? int.Parse(wo) : 15;

        var tokens = LoadTokens(tokensPath);
        return new SenseVoiceModel(session, tokens, lfrM, lfrN, negMean, invStd, language, textNorm, 0,
            "DmlExecutionProvider")
        {
            LoadMs = watch.ElapsedMilliseconds,
        };
    }

    private static string[] LoadTokens(string path)
    {
        var lines = File.ReadAllLines(path, System.Text.Encoding.UTF8);
        int max = 0;
        foreach (var line in lines)
        {
            int sp = line.LastIndexOf(' ');
            if (sp > 0 && int.TryParse(line[(sp + 1)..], out var id) && id > max) max = id;
        }
        var tokens = new string[max + 1];
        for (int i = 0; i < tokens.Length; i++) tokens[i] = "";
        foreach (var line in lines)
        {
            int sp = line.LastIndexOf(' ');
            if (sp <= 0) continue;
            if (!int.TryParse(line[(sp + 1)..], out var id) || id < 0 || id >= tokens.Length) continue;
            tokens[id] = line[..sp];
        }
        return tokens;
    }

    public string TokenText(int id) => id >= 0 && id < _tokens.Length ? _tokens[id] : "";

    public int TokenCount => _tokens.Length;

    /// <summary>按字符找 token id：先试 ▁ 前缀（词首），再试裸字符。找不到返回 -1。</summary>
    public int IdForChar(string ch)
    {
        if (_tokenIds.TryGetValue("▁" + ch, out var id)) return id;
        if (_tokenIds.TryGetValue(ch, out id)) return id;
        return -1;
    }

    /// <summary>整段 16k 单声道采样（int16 量级）→ LFR+CMVN 后的特征行。</summary>
    public float[][] ExtractFeatures(float[] pcm)
    {
        var fbank = SenseVoiceFeatures.Fbank(pcm);
        var lfr = SenseVoiceFeatures.ApplyLfr(fbank, LfrM, LfrN);
        if (NegMean.Length == FeatureDim && lfr.Length > 0) SenseVoiceFeatures.ApplyCmvn(lfr, NegMean, InvStd);
        return lfr;
    }

    /// <summary>
    /// 只算需要的 token 列（对齐一行只需要这一行的字 + blank），避免每帧 25055 个 logit 的拷贝。
    /// 返回 row-major [帧数, ids.Length] 的 logit，帧时间 = 模型自己决定的子采样率。
    /// </summary>
    public float[] RunWindow(float[] pcm, int[] ids, out int frames)
    {
        var features = ExtractFeatures(pcm);
        frames = features.Length;
        if (frames == 0 || ids.Length == 0) return Array.Empty<float>();

        int dim = FeatureDim;
        var flat = new float[frames * dim];
        for (int i = 0; i < frames; i++) Array.Copy(features[i], 0, flat, i * dim, dim);

        var x = new DenseTensor<float>(flat, new[] { 1, frames, dim });
        var xLength = new DenseTensor<int>(new[] { frames }, new[] { 1 });
        var lang = new DenseTensor<int>(new[] { Language }, new[] { 1 });
        var norm = new DenseTensor<int>(new[] { TextNorm }, new[] { 1 });

        using var results = _session.Run(new[]
        {
            NamedOnnxValue.CreateFromTensor("x", x),
            NamedOnnxValue.CreateFromTensor("x_length", xLength),
            NamedOnnxValue.CreateFromTensor("language", lang),
            NamedOnnxValue.CreateFromTensor("text_norm", norm),
        });

        var logits = results.First().AsTensor<float>();
        var dims = logits.Dimensions.ToArray();
        int outFrames = dims[1], vocab = dims[2];
        frames = outFrames;

        var buffer = logits is DenseTensor<float> dense ? dense.Buffer.Span : logits.ToArray();
        var picked = new float[outFrames * ids.Length];
        for (int t = 0; t < outFrames; t++)
        {
            int rowBase = t * vocab;
            int outBase = t * ids.Length;
            for (int k = 0; k < ids.Length; k++)
            {
                int id = ids[k];
                picked[outBase + k] = id >= 0 && id < vocab ? buffer[rowBase + id] : -30f;
            }
        }
        return picked;
    }

    /// <summary>贪心 CTC：只为自检前端是否接对（拼出来的文本应该像人话）。</summary>
    public string GreedyText(float[] pcm)
    {
        var features = ExtractFeatures(pcm);
        if (features.Length == 0) return "";
        int dim = FeatureDim;
        var flat = new float[features.Length * dim];
        for (int i = 0; i < features.Length; i++) Array.Copy(features[i], 0, flat, i * dim, dim);

        var x = new DenseTensor<float>(flat, new[] { 1, features.Length, dim });
        var xLength = new DenseTensor<int>(new[] { features.Length }, new[] { 1 });
        var lang = new DenseTensor<int>(new[] { Language }, new[] { 1 });
        var norm = new DenseTensor<int>(new[] { TextNorm }, new[] { 1 });

        using var results = _session.Run(new[]
        {
            NamedOnnxValue.CreateFromTensor("x", x),
            NamedOnnxValue.CreateFromTensor("x_length", xLength),
            NamedOnnxValue.CreateFromTensor("language", lang),
            NamedOnnxValue.CreateFromTensor("text_norm", norm),
        });

        var logits = results.First().AsTensor<float>();
        var dims = logits.Dimensions.ToArray();
        int frames = dims[1], vocab = dims[2];
        var buffer = logits is DenseTensor<float> dense ? dense.Buffer.Span : logits.ToArray();

        var text = new System.Text.StringBuilder();
        int prev = -1;
        for (int t = 0; t < frames; t++)
        {
            int best = 0;
            float bestValue = float.NegativeInfinity;
            int rowBase = t * vocab;
            for (int v = 0; v < vocab; v++)
            {
                float value = buffer[rowBase + v];
                if (value > bestValue) { bestValue = value; best = v; }
            }
            if (best != BlankId && best != prev)
            {
                var piece = TokenText(best);
                text.Append(piece.Replace("▁", " "));
            }
            prev = best;
        }
        return text.ToString().Trim();
    }

    public void Dispose() => _session.Dispose();

    /// <summary>结束性能采样，返回 ORT 写的 JSON 路径（每算子一条，用来查哪些落在了 CPU 上）。</summary>
    public string? EndProfiling()
    {
        try { return _session.EndProfiling(); }
        catch { return null; }
    }
}
