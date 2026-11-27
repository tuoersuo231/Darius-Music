using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace CurrentLyrics.Services;

/// <summary>
/// B 方案：Whisper 交叉注意力 + DTW 的强制对齐銆?
/// 编码器 20 ms 涓€甯э紙1500 甯?= 30 s锛夛紝瑙ｇ爜鍣?teacher-forcing 璺戞暣琛?token锛?
/// 杈撳嚭姣忓眰 cross-attention [heads, T_tok, 1500]锛屽啀鐢?DTW 鎶婃瘡涓?token 钀藉埌鍏蜂綋甯т笂銆?
/// 鍜?SenseVoice 閭ｅ楜瀹屽叏鐙珛锛屼换浣曚竴姝ュけ璐ラ兘涓嶅奖鍝嶇幇鏈夌殑鍔熻兘銆?
/// </summary>
public sealed class WhisperAligner : IDisposable
{
    public const int SampleRate = 16000;
    public const int MelFrames = WhisperMel.ChunkFrames;   // 3000 mel 甯?= 30 s
    public const int EncFrames = WhisperMel.EncoderFrames; // 1500 缂栫爜甯?= 20 ms
    public const int EncDim = 768;
    public const int FrameMs = 20;

    private readonly InferenceSession _encoder;
    private readonly InferenceSession _decoder;

    public WhisperTokenizer Tokenizer { get; }
    public int NumLayers { get; }
    public int NumHeads { get; }
    public long LoadMs { get; }
    public string ModelDir { get; }
    public string DecoderFile { get; }
    public bool HasLogits { get; }

    private WhisperAligner(InferenceSession encoder, InferenceSession decoder,
        WhisperTokenizer tokenizer, int layers, int heads, long loadMs, string dir, string decoderFile)
    {
        _encoder = encoder;
        _decoder = decoder;
        Tokenizer = tokenizer;
        NumLayers = layers;
        NumHeads = heads;
        LoadMs = loadMs;
        ModelDir = dir;
        DecoderFile = decoderFile;
        HasLogits = decoder.OutputMetadata.ContainsKey("logits");
    }

    public static WhisperAligner Load(string dir, int deviceId = 0, bool gpuOnly = false, bool verbose = false,
        string? decoderFile = null)
    {
        var encPath = Path.Combine(dir, "encoder_model.onnx");
        var decPath = decoderFile != null ? Path.Combine(dir, decoderFile) : Path.Combine(dir, "decoder_ctx.onnx");
        if (!File.Exists(decPath)) decPath = Path.Combine(dir, "decoder_model.onnx");
        if (!File.Exists(encPath) || !File.Exists(decPath))
            throw new FileNotFoundException($"whisper 模型不完整：{dir}");

        var sw = System.Diagnostics.Stopwatch.StartNew();

        var encOpt = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL, EnableCpuMemArena = false };
        var decOpt = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL, EnableCpuMemArena = false };
        if (verbose)
        {
            encOpt.LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_VERBOSE;
            decOpt.LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_VERBOSE;
        }
        encOpt.AppendExecutionProvider_DML(deviceId);
        decOpt.AppendExecutionProvider_DML(deviceId);
        if (gpuOnly)
        {
            encOpt.AddSessionConfigEntry("session.disable_cpu_ep_fallback", "1");
            decOpt.AddSessionConfigEntry("session.disable_cpu_ep_fallback", "1");
        }

        var encoder = new InferenceSession(encPath, encOpt);
        var decoder = new InferenceSession(decPath, decOpt);
        sw.Stop();

        var crossNames = decoder.OutputMetadata.Keys
            .Where(k => k.StartsWith("cross_attentions", StringComparison.Ordinal))
            .OrderBy(k => int.Parse(k.Split('.').Last()))
            .ToArray();

        int layers = crossNames.Length;
        int heads = 12;
        if (layers > 0)
        {
            var dims = decoder.OutputMetadata[crossNames[0]].Dimensions;
            if (dims.Length == 4 && dims[1] > 0) heads = dims[1];
        }

        var tok = WhisperTokenizer.Load(dir);
        return new WhisperAligner(encoder, decoder, tok, layers, heads, sw.ElapsedMilliseconds, dir,
            Path.GetFileName(decPath));
    }

    /// <summary>mel [80*3000] 鈫?encoded [1500*768]銆?/summary>
    public float[] Encode(float[] melInput)
    {
        var tensor = new DenseTensor<float>(melInput, new[] { 1, WhisperMel.NumMels, MelFrames });
        using var results = _encoder.Run(new[] { NamedOnnxValue.CreateFromTensor("input_features", tensor) });
        var value = results[0].AsTensor<float>();
        var dense = value as DenseTensor<float>;
        return dense != null ? dense.Buffer.ToArray() : value.ToArray();
    }

    /// <summary>
    /// teacher-forcing 跑一整行 token锛岃繑鍥炴瘡灞?cross-attention锛?
    /// 姣忓眰 flat 闀垮害 = NumHeads * T * 1500锛屽竷灞€ [head][token][frame]銆?
    /// </summary>
    public float[] Decode(float[] encoded, int[] tokenIds, out int tTok)
    {
        tTok = tokenIds.Length;
        var ids = new long[tokenIds.Length];
        for (int i = 0; i < ids.Length; i++) ids[i] = tokenIds[i];

        var idsTensor = new DenseTensor<long>(ids, new[] { 1, ids.Length });
        var encTensor = new DenseTensor<float>(encoded, new[] { 1, EncFrames, EncDim });

        using var results = _decoder.Run(new[]
        {
            NamedOnnxValue.CreateFromTensor("input_ids", idsTensor),
            NamedOnnxValue.CreateFromTensor("encoder_hidden_states", encTensor),
        });

        var all = new float[NumLayers][];
        for (int i = 0; i < NumLayers; i++)
        {
            var name = $"cross_attentions.{i}";
            var entry = results.FirstOrDefault(r => r.Name == name) ?? results[i];
            var t = entry.AsTensor<float>();
            all[i] = t is DenseTensor<float> d ? d.Buffer.ToArray() : t.ToArray();
        }

        // 鎷兼垚涓€涓?flat锛歲ayer 蹇?* (heads*tTok*1500)
        int perLayer = NumHeads * tTok * EncFrames;
        var flat = new float[NumLayers * perLayer];
        for (int i = 0; i < NumLayers; i++) Array.Copy(all[i], 0, flat, i * perLayer, perLayer);
        return flat;
    }

    /// <summary>贪心解码（只在验证 mel / tokenizer 时用，产品不走这条）。</summary>
    public string GreedyDecode(float[] encoded, int[] prompt, int maxNew, out int[] outIds)
    {
        if (!HasLogits) throw new InvalidOperationException("当前 decoder 没有 logits 输出，不能做贪心解码");
        var ids = new List<long>(prompt.Length);
        foreach (var p in prompt) ids.Add(p);

        var encTensor = new DenseTensor<float>(encoded, new[] { 1, EncFrames, EncDim });
        for (int step = 0; step < maxNew; step++)
        {
            var idsTensor = new DenseTensor<long>(ids.ToArray(), new[] { 1, ids.Count });
            using var results = _decoder.Run(new[]
            {
                NamedOnnxValue.CreateFromTensor("input_ids", idsTensor),
                NamedOnnxValue.CreateFromTensor("encoder_hidden_states", encTensor),
            });
            var logits = results.First(r => r.Name == "logits").AsTensor<float>();
            var dims = logits.Dimensions.ToArray();
            int vocab = dims[2];
            int rowBase = (dims[1] - 1) * vocab;
            var dense = logits as DenseTensor<float>;
            var span = dense != null ? dense.Buffer.Span : logits.ToArray();
            int best = 0;
            float bestValue = float.NegativeInfinity;
            for (int v = 0; v < vocab; v++)
            {
                float value = span[rowBase + v];
                if (value > bestValue) { bestValue = value; best = v; }
            }
            if (best == Tokenizer.EndOfText) break;
            ids.Add(best);
        }
        outIds = ids.Skip(prompt.Length).Select(x => (int)x).ToArray();
        return "";
    }

    /// <summary>
    /// 鍙栨寚瀹氬眰/澶寸殑 attention 骞冲潎锛岃緭鍑?[T_tok, 1500]锛岃绗煎綊涓€鍖栧埌 0~1銆?
    /// </summary>
    public static float[] ReduceHeads(float[] flat, int layers, int heads, int tTok, int[] useLayers, int[]? useHeads = null)
    {
        int perLayer = heads * tTok * EncFrames;
        var acc = new float[tTok * EncFrames];
        int count = 0;
        foreach (var layer in useLayers)
        {
            if (layer < 0 || layer >= layers) continue;
            int baseOff = layer * perLayer;
            var headSet = useHeads ?? Enumerable.Range(0, heads).ToArray();
            foreach (var h in headSet)
            {
                if (h < 0 || h >= heads) continue;
                int off = baseOff + h * tTok * EncFrames;
                for (int i = 0; i < acc.Length; i++) acc[i] += flat[off + i];
                count++;
            }
        }
        if (count > 1) for (int i = 0; i < acc.Length; i++) acc[i] /= count;
        return acc;
    }

    public void Dispose()
    {
        _encoder.Dispose();
        _decoder.Dispose();
    }
}
