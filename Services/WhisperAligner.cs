using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace CurrentLyrics.Services;

/// <summary>
/// Plan B: forced alignment from Whisper cross-attention.
/// The encoder emits one frame per 20 ms (1500 frames = 30 s); the decoder is run once over the whole
/// line with teacher forcing and returns cross_attentions[heads, T_tok, 1500] for every layer.
/// Fully independent from the SenseVoice path: if anything here fails the old path is unaffected.
/// </summary>
public sealed class WhisperAligner : IDisposable
{
    public const int SampleRate = 16000;
    public const int MelFrames = WhisperMel.ChunkFrames;   // 3000 mel frames = 30 s
    public const int EncFrames = WhisperMel.EncoderFrames; // 1500 encoder frames = 20 ms each
    public int EncDim { get; } = 768;
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
        WhisperTokenizer tokenizer, int layers, int heads, int encDim, long loadMs, string dir, string decoderFile)
    {
        _encoder = encoder;
        _decoder = decoder;
        Tokenizer = tokenizer;
        NumLayers = layers;
        NumHeads = heads;
        EncDim = encDim;
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

        int encDim = 768;
        var encDims = encoder.OutputMetadata.First().Value.Dimensions;
        if (encDims.Length == 3 && encDims[2] > 0) encDim = encDims[2];

        var tok = WhisperTokenizer.Load(dir);
        return new WhisperAligner(encoder, decoder, tok, layers, heads, encDim, sw.ElapsedMilliseconds, dir,
            Path.GetFileName(decPath));
    }

    public float[] Encode(float[] melInput)
    {
        var tensor = new DenseTensor<float>(melInput, new[] { 1, WhisperMel.NumMels, MelFrames });
        using var results = _encoder.Run(new[] { NamedOnnxValue.CreateFromTensor("input_features", tensor) });
        var value = results[0].AsTensor<float>();
        var dense = value as DenseTensor<float>;
        return dense != null ? dense.Buffer.ToArray() : value.ToArray();
    }

    /// <summary>
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
