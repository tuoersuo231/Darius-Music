using System.IO;
using System.Text;
using System.Text.Json;
using CurrentLyrics.Models;
using Jint.Native;
using Jint.Native.Object;

namespace CurrentLyrics.Services;

/// <summary>D 方案的两条命令：
/// <c>--gpu-probe</c> 只测硬件和一次 5 秒窗口的耗时；<c>--align-qa</c> 拿酷狗逐字当真值量对齐误差。
/// 两者都不改任何状态，可以随便重跑。</summary>
public static class AlignQa
{
    private static string? Arg(string[] args, string name)
    {
        int index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private static string DefaultModelDir => Path.Combine(AppPaths.DataDir, "align");

    private static string? FindModel(string[] args)
    {
        var explicitPath = Arg(args, "--model") ?? Environment.GetEnvironmentVariable("DARIUS_ALIGN_MODEL");
        if (!string.IsNullOrEmpty(explicitPath))
            return File.Exists(explicitPath) ? explicitPath : null;
        foreach (var candidate in new[]
                 {
                     Path.Combine(DefaultModelDir, "model.onnx"),
                     Path.Combine(AppContext.BaseDirectory, "align", "model.onnx"),
                 })
        {
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    private static string? FindTokens(string[] args, string? modelPath)
    {
        var explicitPath = Arg(args, "--tokens");
        if (!string.IsNullOrEmpty(explicitPath)) return File.Exists(explicitPath) ? explicitPath : null;
        if (modelPath == null) return null;
        var beside = Path.Combine(Path.GetDirectoryName(modelPath)!, "tokens.txt");
        return File.Exists(beside) ? beside : null;
    }

    /// <summary>--sv-greedy：只验证前端接得对不对。喂一段 wav，看贪心 CTC 出来的是不是人话；
    /// 顺便报"输入 LFR 帧 / 输出帧"的比值，用来确定一帧到底是多少毫秒。</summary>
    public static void RunGreedy(string[] args)
    {
        var audioPath = Arg(args, "--sv-greedy");
        var modelPath = FindModel(args);
        var tokensPath = FindTokens(args, modelPath);
        var log = new List<string>();
        if (audioPath == null || modelPath == null || tokensPath == null)
        {
            log.Add("usage: --sv-greedy <wav> --model <onnx> --tokens <tokens.txt>");
        }
        else
        {
            try
            {
                using var model = SenseVoiceModel.Load(modelPath, tokensPath, 0, !args.Contains("--allow-cpu"),
                    args.Contains("--verbose"), args.Contains("--profile"));
                var pcm = AudioDecode.LoadMono16k(audioPath);
                var features = model.ExtractFeatures(pcm);
                var watch = System.Diagnostics.Stopwatch.StartNew();
                var text = model.GreedyText(pcm);
                watch.Stop();
                log.Add($"audio={Path.GetFileName(audioPath)} samples={pcm.Length} ({pcm.Length / 16000.0:0.00}s)");
                log.Add($"lfrFrames={features.Length} inferenceMs={watch.ElapsedMilliseconds}");
                log.Add("text=" + text);
                if (args.Contains("--profile"))
                {
                    var profilePath = model.EndProfiling();
                    log.Add("profile=" + (profilePath ?? "(null)"));
                }
            }
            catch (Exception ex)
            {
                log.Add("failed: " + ex.GetType().Name + " " + ex.Message);
            }
        }
        Console.WriteLine(string.Join("\n", log));
    }

    /// <summary>--local-align：走产品里真正的那条路（LocalAlign.Schedule → 解码 → 逐行对齐 → 写 .align.json），
    /// 再读回来验证缓存能被命中。参数：--song &lt;id&gt; --audio &lt;文件&gt; [--lrc &lt;歌词文件&gt;]。</summary>
    public static void RunLocalAlign(string[] args)
    {
        var log = new List<string>();
        try
        {
            var audioPath = Arg(args, "--audio");
            var idText = Arg(args, "--song");
            long songId = long.TryParse(idText, out var id) ? id : 0;
            var lrcPath = Arg(args, "--lrc");
            if (lrcPath == null && songId > 0)
            {
                var candidate = Path.Combine(AppPaths.DataDir, "lyrics", songId + ".lrc");
                if (File.Exists(candidate)) lrcPath = candidate;
            }
            bool cloud = args.Contains("--cloud") || audioPath == null || !File.Exists(audioPath);
            if (lrcPath == null || !File.Exists(lrcPath) || (songId <= 0 && cloud))
            {
                log.Add("usage: --local-align --song <id> (--audio <file> | --cloud) [--lrc <file>]");
            }
            else
            {
                var baseDoc = LrcParser.Parse(File.ReadAllText(lrcPath, Encoding.UTF8));
                if (baseDoc == null) { log.Add("base lyric parse failed"); }
                else
                {
                    log.Add($"id={songId} base lines={baseDoc.Lines.Count} model='{Path.GetFileName(ModelPathForLog())}'");
                    log.Add($"dataDir={AppPaths.DataDir}");
                    try
                    {
                        var probe = Path.Combine(AppPaths.DataDir, "align-write-test.txt");
                        File.WriteAllText(probe, DateTime.Now.ToString("s"));
                        log.Add($"write probe ok -> {probe}");
                        File.Delete(probe);
                    }
                    catch (Exception wex) { log.Add($"write probe FAILED: {wex.GetType().Name} {wex.Message}"); }
                    var song = cloud
                        ? new Song
                        {
                            Source = SongSource.Netease,
                            Path = "online:" + songId,
                            NeteaseId = songId,
                            Title = Path.GetFileNameWithoutExtension(lrcPath),
                        }
                        : new Song
                        {
                            Source = SongSource.Local,
                            Path = audioPath!,
                            NeteaseId = songId,
                            Title = Path.GetFileNameWithoutExtension(audioPath!),
                            Duration = TimeSpan.FromSeconds(AudioDecode.DurationSeconds(audioPath!)),
                        };

                    var done = new TaskCompletionSource<LyricDocument?>();
                    var watch = System.Diagnostics.Stopwatch.StartNew();
                    LocalAlign.Schedule(songId, song, baseDoc, Arg(args, "--quality") ?? "exhigh",
                        doc => done.TrySetResult(doc));
                    var finished = done.Task.Wait(TimeSpan.FromMinutes(3)) ? done.Task.Result : null;
                    watch.Stop();
                    if (finished == null) log.Add("no result (3 min timeout, or scheduler declined)");
                    else log.Add($"aligned {watch.ElapsedMilliseconds}ms lines={finished.Lines.Count} " +
                                 $"worded={finished.Lines.Count(l => l.HasWordTiming)}");

                    var cached = LocalAlign.TryCached(songId, baseDoc);
                    var cachePath = Path.Combine(AppPaths.DataDir, "lyrics", songId + ".align.json");
                    log.Add($"cache hit={(cached != null)} file={(File.Exists(cachePath) ? new FileInfo(cachePath).Length + "B" : "missing")}");
                    if (cached != null)
                    {
                        var sample = cached.Lines.FirstOrDefault(l => l.HasWordTiming);
                        log.Add($"cached worded={cached.Lines.Count(l => l.HasWordTiming)}");
                        if (sample != null)
                            log.Add($"sample [{sample.StartMs}-{sample.EndMs}] '{sample.Text}' " +
                                    string.Join(" ", sample.Words.Take(6).Select(w => $"{w.Text}@{w.StartMs}-{w.EndMs}")));
                    }
                    if (File.Exists(Path.Combine(AppPaths.DataDir, "align.log")))
                        log.Add("align.log: " + string.Join(" | ",
                            File.ReadAllLines(Path.Combine(AppPaths.DataDir, "align.log")).TakeLast(3)));
                }
            }
        }
        catch (Exception ex)
        {
            log.Add("failed: " + ex.GetType().Name + " " + ex.Message);
        }
        var report = Arg(args, "--report");
        if (report != null)
        {
            try
            {
                var full = Path.GetFullPath(report);
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                File.WriteAllLines(full, log, new UTF8Encoding(true));
            }
            catch { }
        }
        // 验收"空闲释放"用：把进程留住，好从外面看内存有没有降下来。
        if (int.TryParse(Arg(args, "--hold"), out var holdSeconds) && holdSeconds > 0)
            System.Threading.Thread.Sleep(holdSeconds * 1000);
        Console.WriteLine(string.Join("\n", log));
    }

    private static string ModelPathForLog()
    {
        var custom = Environment.GetEnvironmentVariable("DARIUS_ALIGN_MODEL");
        if (!string.IsNullOrEmpty(custom)) return custom;
        return Path.Combine(AppPaths.DataDir, "align", "model.onnx");
    }

    /// <summary>--compare-cache：拿 .align.json 和一份带真值的 A2 LRC 比误差。
    /// 参数：--compare-cache &lt;songId&gt; --lrc &lt;truth.lrc&gt;。看的是产品真正会用的那份缓存。</summary>
    public static void RunCompareCache(string[] args)
    {
        var log = new List<string>();
        try
        {
            long songId = long.TryParse(Arg(args, "--compare-cache"), out var id) ? id : 0;
            var truthPath = Arg(args, "--lrc");
            if (songId <= 0 || truthPath == null || !File.Exists(truthPath))
            {
                log.Add("usage: --compare-cache <id> --lrc <truth.lrc>");
            }
            else
            {
                var truth = LrcParser.Parse(File.ReadAllText(truthPath, Encoding.UTF8));
                var cachePath = Path.Combine(AppPaths.DataDir, "lyrics", songId + ".align.json");
                if (truth == null || !File.Exists(cachePath)) { log.Add("truth or cache missing"); }
                else
                {
                    var cache = JsonSerializer.Deserialize<CacheFilePublic>(File.ReadAllText(cachePath, Encoding.UTF8));
                    var errors = new List<double>();
                    int overrun = 0, cut = 0, total = 0;
                    var ourGaps = new List<double>();
                    var truthGaps = new List<double>();
                    foreach (var entry in cache?.Lines ?? new List<CacheLinePublic>())
                    {
                        // 副歌会重复出现，只按文本找会配到第一遍 —— 还要看时间是否接近。
                        var line = truth.Lines.FirstOrDefault(l => l.Text == entry.T &&
                                                                   Math.Abs(l.StartMs - entry.StartMs) < 1500);
                        if (line == null || line.Words.Count < 2) continue;
                        var next = truth.Lines.FirstOrDefault(l => l.StartMs > line.StartMs);
                        long limit = next?.StartMs ?? long.MaxValue;
                        int n = Math.Min(entry.W.Count, line.Words.Count);
                        for (int i = 0; i < n; i++)
                        {
                            if (i == 0) continue;      // 首字时间由行首锚定，单独看
                            errors.Add(entry.W[i].S - line.Words[i].StartMs);
                            if (entry.W[i].S > limit) overrun++;
                            if (entry.W[i].E > limit) cut++;
                            ourGaps.Add(entry.W[i].S - entry.W[i - 1].S);
                            truthGaps.Add(line.Words[i].StartMs - line.Words[i - 1].StartMs);
                            total++;
                        }
                    }
                    if (errors.Count == 0) log.Add("no matching lines");
                    else
                    {
                        var abs = errors.Select(Math.Abs).OrderBy(v => v).ToList();
                        log.Add($"chars={errors.Count} median|e|={abs[abs.Count / 2]:0}ms " +
                                $"p90={abs[(int)(abs.Count * 0.9)]:0}ms " +
                                $"bias={errors.Average():+0;-0}ms overrun={overrun} cut(高亮被切)={cut}/{total}");
                        var worst = errors.Select((e, i) => (e, i)).OrderByDescending(x => Math.Abs(x.e)).Take(8);
                        log.Add("worst: " + string.Join(" ", worst.Select(w => $"{w.e:+0;-0}")));
                        if (ourGaps.Count > 5)
                        {
                            double mo = ourGaps.Average(), mt = truthGaps.Average();
                            double cov = ourGaps.Zip(truthGaps, (a, b) => (a - mo) * (b - mt)).Sum();
                            double so = Math.Sqrt(ourGaps.Sum(v => (v - mo) * (v - mo)));
                            double st = Math.Sqrt(truthGaps.Sum(v => (v - mt) * (v - mt)));
                            log.Add($"gap corr={(so > 0 && st > 0 ? cov / (so * st) : 0):0.00} " +
                                    $"our sd={so / ourGaps.Count:0}ms truth sd={st / truthGaps.Count:0}ms");
                        }
                    }
                }
            }
        }
        catch (Exception ex) { log.Add("failed: " + ex.GetType().Name + " " + ex.Message); }
        Console.WriteLine(string.Join("\n", log));
    }

    private sealed class CacheFilePublic
    {
        public int V { get; set; }
        public long Id { get; set; }
        public string Model { get; set; } = "";
        public List<CacheLinePublic> Lines { get; set; } = new();
    }

    private sealed class CacheLinePublic
    {
        public int I { get; set; }
        public long S { get; set; }
        public string T { get; set; } = "";
        public long StartMs => S;
        public List<CacheWordPublic> W { get; set; } = new();
    }

    private sealed class CacheWordPublic
    {
        public string T { get; set; } = "";
        public long S { get; set; }
        public long E { get; set; }
        public int O { get; set; }
    }

    /// <summary>--gpu-probe：打印 JSON。有模型就顺带把 5 秒窗口跑 5 次，没有就只报硬件。</summary>
    public static void RunGpuProbe(string[] args)
    {
        var report = Arg(args, "--report") ?? "gpu-probe.json";
        var modelPath = FindModel(args);
        var tokensPath = FindTokens(args, modelPath);
        string? runJson = null;

        if (modelPath != null && tokensPath != null)
        {
            runJson = MeasureWindow(modelPath, tokensPath, args);
        }
        else if (modelPath != null)
        {
            runJson = JsonSerializer.Serialize(new
            {
                ok = false,
                reason = "tokens.txt missing next to " + Path.GetFileName(modelPath),
            });
        }

        var json = GpuProbe.ToJson(modelPath, runJson);
        try
        {
            var full = Path.GetFullPath(report);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, json, new UTF8Encoding(true));
        }
        catch { }
        Console.WriteLine(json);
    }

    private static string MeasureWindow(string modelPath, string tokensPath, string[] args)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        SenseVoiceModel model;
        string strictNote;
        try
        {
            bool gpuOnly = !args.Contains("--allow-cpu");
            model = SenseVoiceModel.Load(modelPath, tokensPath, 0, gpuOnly);
            strictNote = gpuOnly ? "cpu-fallback-disabled" : "cpu-fallback-allowed";
        }
        catch (Exception ex)
        {
            // 严格模式建不起来（模型里有算子落不到 GPU）时，退一步把真实耗时量出来，
            // 但把失败原因原样带回 JSON —— 不能默默假装它在纯 GPU 上跑。
            try
            {
                model = SenseVoiceModel.Load(modelPath, tokensPath, 0, false);
                strictNote = "strict-failed: " + ex.Message;
            }
            catch (Exception ex2)
            {
                return JsonSerializer.Serialize(new
                {
                    ok = false,
                    reason = "session-create-failed",
                    error = ex2.GetType().Name + ": " + ex2.Message,
                });
            }
        }

        using (model)
        {
            long loadMs = watch.ElapsedMilliseconds;

            // 5 秒窗口的音频：优先用 --audio，其次用模型目录里的自检 wav，最后用静音占位。
            var audio = Arg(args, "--audio");
            if (audio == null || !File.Exists(audio))
            {
                var beside = Path.Combine(Path.GetDirectoryName(modelPath)!, "ja.wav");
                if (File.Exists(beside)) audio = beside;
            }

            float[] pcm;
            string audioNote;
            if (audio != null && File.Exists(audio))
            {
                var whole = AudioDecode.LoadMono16k(audio);
                int need = Math.Min(whole.Length, SenseVoiceFeatures.SampleRate * 5);
                if (whole.Length == 0)
                {
                    pcm = new float[SenseVoiceFeatures.SampleRate * 5];
                    audioNote = "decode-failed";
                }
                else
                {
                    pcm = new float[need];
                    Array.Copy(whole, pcm, need);
                    audioNote = Path.GetFileName(audio);
                }
            }
            else
            {
                pcm = new float[SenseVoiceFeatures.SampleRate * 5];
                audioNote = "silence";
            }

            // 计时只要几个 token 的列，K 小一点不影响耗时（编码器才是大头）。
            var ids = new[] { 0, 1, 2, 3, 4, 5, 6, 7 };
            model.RunWindow(pcm, ids, out _);   // 预热：DML 第一次要编译算子

            var times = new List<double>();
            int outFrames = 0;
            for (int i = 0; i < 5; i++)
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                model.RunWindow(pcm, ids, out outFrames);
                sw.Stop();
                times.Add(sw.Elapsed.TotalMilliseconds);
            }
            times.Sort();
            double min = times[0], median = times[times.Count / 2], max = times[^1];
            double audioSeconds = pcm.Length / (double)SenseVoiceFeatures.SampleRate;

            return JsonSerializer.Serialize(new
            {
                ok = true,
                executionProvider = model.ExecutionProvider,
                strictMode = strictNote,
                modelMs = loadMs,
                audio = audioNote,
                audioSeconds,
                outputFrames = outFrames,
                framesPerSecond = audioSeconds > 0 ? outFrames / audioSeconds : 0,
                windowMs = times.Select(t => Math.Round(t, 1)).ToList(),
                minMs = Math.Round(min, 1),
                medianMs = Math.Round(median, 1),
                maxMs = Math.Round(max, 1),
                realTimeFactor = median > 0 ? Math.Round(audioSeconds * 1000 / median, 1) : 0,
            });
        }
    }

    /// <summary>
    /// --align-qa：用酷狗逐字当真值，量"本地对齐"的中位误差。
    /// 参数：--krc &lt;缓存文件&gt;（或 --song &lt;id&gt; 从 data\lyrics 找）、--audio &lt;音频&gt;、--model、--tokens。
    /// </summary>
    public static void RunAlignQa(string[] args)
    {
        var report = Arg(args, "--report") ?? "align-qa.txt";
        var lines = new List<string>();
        try
        {
            RunAlignQaCore(args, lines);
        }
        catch (Exception ex)
        {
            lines.Add("failed: " + ex.GetType().Name + " " + ex.Message);
        }
        try
        {
            var full = Path.GetFullPath(report);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllLines(full, lines, new UTF8Encoding(true));
        }
        catch { }
        Console.WriteLine(string.Join("\n", lines));
    }

    private static void RunAlignQaCore(string[] args, List<string> log)
    {
        var songArg = Arg(args, "--song");
        string? krcPath = Arg(args, "--krc");
        var lrcPath = Arg(args, "--lrc");
        if (krcPath == null && songArg != null)
        {
            var candidate = Path.Combine(AppPaths.DataDir, "lyrics", songArg + ".krc");
            if (File.Exists(candidate)) krcPath = candidate;
        }
        if ((krcPath == null || !File.Exists(krcPath)) && lrcPath == null)
        {
            log.Add("no ground truth: pass --krc <file> / --lrc <file> / --song <netease id>");
            return;
        }

        var modelPath = FindModel(args);
        var tokensPath = FindTokens(args, modelPath);
        if (modelPath == null || tokensPath == null)
        {
            log.Add("no model: pass --model <model.onnx> --tokens <tokens.txt>");
            return;
        }

        var audioPath = Arg(args, "--audio");
        if (audioPath == null || !File.Exists(audioPath))
        {
            log.Add("no audio: pass --audio <file>");
            return;
        }

        LyricDocument? truth;
        if (krcPath != null && File.Exists(krcPath))
            truth = KugouLyric.Parse(File.ReadAllText(krcPath, Encoding.UTF8));
        else
            truth = LrcParser.Parse(File.ReadAllText(lrcPath!, Encoding.UTF8));
        if (truth == null)
        {
            log.Add("ground truth parse failed");
            return;
        }
        log.Add($"truth={(krcPath != null ? Path.GetFileName(krcPath) : Path.GetFileName(lrcPath))} " +
                $"lines={truth.Lines.Count} worded={truth.Lines.Count(l => l.Words.Count > 0)}");
        log.Add($"audio={Path.GetFileName(audioPath)} ({AudioDecode.Describe(audioPath)})");

        var decodeWatch = System.Diagnostics.Stopwatch.StartNew();
        var pcm = AudioDecode.LoadMono16k(audioPath);
        decodeWatch.Stop();
        if (pcm.Length == 0)
        {
            log.Add("audio decode failed");
            return;
        }
        log.Add($"decoded {pcm.Length} samples = {pcm.Length / 16000.0:0.0}s in {decodeWatch.ElapsedMilliseconds}ms");

        var loadWatch = System.Diagnostics.Stopwatch.StartNew();
        using var model = SenseVoiceModel.Load(modelPath, tokensPath, 0, !args.Contains("--allow-cpu"));
        loadWatch.Stop();
        log.Add($"model loaded in {loadWatch.ElapsedMilliseconds}ms ({model.ExecutionProvider}, " +
                $"tokens={model.TokenCount}, lfr={model.LfrM}/{model.LfrN})");

        int limit = int.TryParse(Arg(args, "--limit"), out var l) ? l : int.MaxValue;
        int skipFirst = int.TryParse(Arg(args, "--skip-first"), out var sf) ? sf : 3;
        var rows = new List<Row>();
        var lineInfo = new List<(int Index, long StartMs, string Text, long InferenceMs)>();
        int skipped = 0, charTotal = 0, lineIndex = 0, charSeen = 0, charMapped = 0, ordinal = 0;
        var totalWatch = System.Diagnostics.Stopwatch.StartNew();

        foreach (var line in truth.Lines)
        {
            ordinal++;
            if (!line.HasWordTiming || line.Words.Count < 2) { skipped++; continue; }
            if (lineIndex >= limit) break;
            if (line.StartMs < 0) { skipped++; continue; }
            if (ordinal <= skipFirst) { skipped++; continue; }
            // 开头几行是「词：xxx / 曲：xxx」和双语标题卡，根本没人唱，拿来量误差只会污染结果。
            if (line.Text.Contains('：') || line.Text.Contains(':') || line.Text.StartsWith("词") ||
                line.Text.StartsWith("曲") || line.Text.StartsWith("编"))
            {
                skipped++;
                continue;
            }

            var chars = new List<(string Ch, long TruthStart)>();
            foreach (var word in line.Words)
            {
                if (word.Text.Length == 0) continue;
                chars.Add((word.Text[..1], word.StartMs));
            }
            if (chars.Count < 2) { skipped++; continue; }

            var ids = new List<int>();
            var kept = new List<(string Ch, long TruthStart)>();
            foreach (var (ch, start) in chars)
            {
                charSeen++;
                int id = model.IdForChar(ch);
                if (id <= 0) continue;
                charMapped++;
                ids.Add(id);
                kept.Add((ch, start));
            }
            if (ids.Count < 2) { skipped++; continue; }

            // 行窗口：前后各 300 ms，最长 12 s。
            long windowStartMs = Math.Max(0, line.StartMs - 300);
            long windowEndMs = Math.Min((long)(pcm.Length / 16.0), line.EndMs + 300);
            if (windowEndMs - windowStartMs > 12000) windowEndMs = windowStartMs + 12000;
            int startSample = (int)(windowStartMs * 16);
            int endSample = Math.Min(pcm.Length, (int)(windowEndMs * 16));
            if (endSample - startSample < 1600) { skipped++; continue; }

            var window = new float[endSample - startSample];
            Array.Copy(pcm, startSample, window, 0, window.Length);

            // --fixed-window N：把所有窗口补/裁到同一个长度。DML 每个新形状都要重新编译算子，
            // 固定形状后只有第一行付这笔钱 —— 产品里该这么做，这里量一下能省多少。
            double fixedSec = double.TryParse(Arg(args, "--fixed-window"), out var fw) ? fw : 0;
            if (fixedSec > 0)
            {
                int target = (int)(fixedSec * SenseVoiceFeatures.SampleRate);
                if (window.Length < target)
                {
                    var padded = new float[target];
                    Array.Copy(window, padded, window.Length);
                    window = padded;
                }
                else if (window.Length > target)
                {
                    Array.Resize(ref window, target);
                }
            }

            int columns = ids.Count + 1;          // 0 号列是 blank
            var cols = new List<int> { 0 };
            cols.AddRange(ids);

            var runWatch = System.Diagnostics.Stopwatch.StartNew();
            var logits = model.RunWindow(window, cols.ToArray(), out int frames);
            runWatch.Stop();
            if (frames == 0) { skipped++; continue; }

            var spans = ForceAlign.Align(logits, frames, columns, 0, ids.ToArray());
            if (spans.Length != ids.Count) { skipped++; continue; }

            // 帧→时间用解析式而不是"总时长/帧数"：模型在 LFR 特征前面插了 4 个 query 帧
            // （language / event / event / text_norm），输出帧数 ≈ LFR 帧数 + 4。所以第 k 帧对应
            // (k-4) * 60 ms（10 ms × LFR 位移 6）。用总时长除会同时压缩时间轴并引入固定偏移。
            double frameMs = 10.0 * model.LfrN;
            double leadFrames = 4;
            int lfrFrames = window.Length / (SenseVoiceFeatures.FrameShift * model.LfrN);

            for (int i = 0; i < ids.Count; i++)
            {
                long aligned = windowStartMs + (long)Math.Round((spans[i].StartFrame - leadFrames) * frameMs);
                rows.Add(new Row
                {
                    LineIndex = lineIndex,
                    Char = kept[i].Ch,
                    TruthMs = kept[i].TruthStart,
                    AlignMs = aligned,
                    ErrorMs = aligned - kept[i].TruthStart,
                    FrameMs = frameMs,
                    Frames = frames,
                    LfrFrames = lfrFrames,
                    InferenceMs = runWatch.ElapsedMilliseconds,
                });
            }
            lineIndex++;
            charTotal += ids.Count;
            lineInfo.Add((lineIndex - 1, line.StartMs, line.Text, runWatch.ElapsedMilliseconds));
        }
        totalWatch.Stop();

        if (rows.Count == 0)
        {
            log.Add("no comparable lines (token coverage too low?)");
            return;
        }

        // 一个全局常数偏移（中位数）是最保守的"系统延迟"修正；再单独看每行内部的相对误差。
        var globalOffset = Median(rows.Select(r => (double)r.ErrorMs).ToList());
        var afterGlobal = rows.Select(r => Math.Abs(r.ErrorMs - globalOffset)).ToList();
        var relative = new List<double>();
        foreach (var group in rows.GroupBy(r => r.LineIndex))
        {
            double lineMedian = Median(group.Select(r => (double)r.ErrorMs).ToList());
            foreach (var r in group) relative.Add(Math.Abs(r.ErrorMs - lineMedian));
        }

        log.Add("");
        log.Add($"lines compared={rows.Select(r => r.LineIndex).Distinct().Count()} skipped={skipped} " +
                $"chars={rows.Count} inferenceTotal={totalWatch.ElapsedMilliseconds}ms");
        log.Add($"token coverage={charMapped}/{charSeen} ({100.0 * charMapped / Math.Max(1, charSeen):0.0}%)");
        log.Add($"median ms/frame={Median(rows.Select(r => r.FrameMs).ToList()):0.0} " +
                $"median frames/line={Median(rows.Select(r => (double)r.Frames).ToList()):0} " +
                $"frames-lfr={Median(rows.Select(r => (double)(r.Frames - r.LfrFrames)).ToList()):0}");
        log.Add($"inference/line: median={Median(rows.Select(r => (double)r.InferenceMs).ToList()):0}ms " +
                $"max={rows.Select(r => (double)r.InferenceMs).Max():0}ms（含每个新形状的 DML 编译）");
        log.Add("");
        log.Add($"raw |error|:      median={Median(rows.Select(r => (double)Math.Abs(r.ErrorMs)).ToList()):0}ms " +
                $"p90={Percentile(rows.Select(r => (double)Math.Abs(r.ErrorMs)).ToList(), 90):0}ms");
        log.Add($"global offset:    {globalOffset:0}ms (median of all errors)");
        log.Add($"after offset:     median={Median(afterGlobal):0}ms p90={Percentile(afterGlobal, 90):0}ms");
        log.Add($"within-line:      median={Median(relative):0}ms p90={Percentile(relative, 90):0}ms " +
                "(每行各自去掉中位误差后的相对精度)");
        log.Add("");
        log.Add("per-line median offset / text:");
        foreach (var group in rows.GroupBy(r => r.LineIndex).OrderBy(g => g.Key).Take(20))
        {
            double lineMedian = Median(group.Select(r => (double)r.ErrorMs).ToList());
            var info = lineInfo.FirstOrDefault(i => i.Index == group.Key);
            var text = (info.Text ?? "").Length > 18 ? info.Text![..18] + "…" : info.Text ?? "";
            log.Add($"  line {group.Key,2} start={info.StartMs,7}ms n={group.Count(),2} " +
                    $"median={lineMedian,5:+0;-0;0}ms infer={info.InferenceMs,4}ms  {text}");
        }
        log.Add("");
        log.Add("per-char errors (first 6 lines, 相对该行中位数):");
        foreach (var group in rows.GroupBy(r => r.LineIndex).OrderBy(g => g.Key).Take(6))
        {
            double lineMedian = Median(group.Select(r => (double)r.ErrorMs).ToList());
            log.Add($"  line {group.Key}: " +
                    string.Join(" ", group.Select(r => $"{r.Char}{(long)Math.Round(r.ErrorMs - lineMedian):+0;-0;0}")));
        }
    }

    private sealed class Row
    {
        public int LineIndex;
        public string Char = "";
        public long TruthMs;
        public long AlignMs;
        public long ErrorMs;
        public double FrameMs;
        public int Frames;
        public int LfrFrames;
        public long InferenceMs;
    }

    private static double Median(List<double> values)
    {
        if (values.Count == 0) return 0;
        var sorted = values.OrderBy(v => v).ToList();
        int mid = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2;
    }

    private static double Percentile(List<double> values, double p)
    {
        if (values.Count == 0) return 0;
        var sorted = values.OrderBy(v => v).ToList();
        double index = (sorted.Count - 1) * p / 100.0;
        int low = (int)Math.Floor(index), high = (int)Math.Ceiling(index);
        return low == high ? sorted[low] : sorted[low] + (sorted[high] - sorted[low]) * (index - low);
    }

    /// <summary>
    /// B 方案验证入口：<c>--whisper-qa &lt;wav&gt;</c>。
    /// 默认只做贪心解码（验证 mel / tokenizer / encoder 全对）；
    /// 加 <c>--text &lt;歌词&gt;</c> 则走 cross-attention，打印每个 token 的注意力峰值位置。
    /// </summary>
    public static void RunWhisperQa(string[] args)
    {
        var log = new List<string>();
        var audioPath = Arg(args, "--whisper-qa");
        var dir = Arg(args, "--whisper-dir") ?? Path.Combine(AppPaths.DataDir, "whisper");
        var report = Arg(args, "--report");
        var text = Arg(args, "--text");
        var lang = Arg(args, "--lang") ?? "ja";
        int deviceId = int.TryParse(Arg(args, "--device"), out var dev) ? dev : 0;
        bool allowCpu = args.Contains("--allow-cpu");

        try
        {
            if (audioPath == null || !File.Exists(audioPath))
                throw new FileNotFoundException("missing --whisper-qa <wav>");
            var greedy = text == null;

            using var aligner = WhisperAligner.Load(dir, deviceId, !allowCpu, args.Contains("--verbose"),
                greedy ? "decoder_model.onnx" : null);
            var pcm = AudioDecode.LoadMono16k(audioPath);
            log.Add($"audio={Path.GetFileName(audioPath)} samples={pcm.Length} ({pcm.Length / 16000.0:0.00}s)");
            log.Add($"decoder={aligner.DecoderFile} layers={aligner.NumLayers} heads={aligner.NumHeads} loadMs={aligner.LoadMs}");

            var melWatch = System.Diagnostics.Stopwatch.StartNew();
            int melFrames = Math.Min(WhisperMel.FrameCount(pcm.Length), WhisperMel.ChunkFrames);
            var mel = WhisperMel.Compute(pcm, melFrames);
            melWatch.Stop();
            var input = WhisperMel.ToEncoderInput(mel, melFrames);
            log.Add($"melFrames={melFrames} melMs={melWatch.ElapsedMilliseconds}");

            var encWatch = System.Diagnostics.Stopwatch.StartNew();
            var encoded = aligner.Encode(input);
            encWatch.Stop();
            log.Add($"encoderMs={encWatch.ElapsedMilliseconds}");

            var tok = aligner.Tokenizer;
            var langId = tok.LanguageToken(lang);
            var prompt = new List<int> { tok.StartOfTranscript };
            if (langId > 0) prompt.Add(langId);
            prompt.Add(tok.TranscribeToken);
            prompt.Add(tok.NoTimestampsToken);
            log.Add($"lang={lang} prompt=[{string.Join(",", prompt)}]");

            if (greedy)
            {
                var decWatch = System.Diagnostics.Stopwatch.StartNew();
                aligner.GreedyDecode(encoded, prompt.ToArray(), 120, out var outIds);
                decWatch.Stop();
                log.Add($"greedyMs={decWatch.ElapsedMilliseconds} tokens={outIds.Length}");
                log.Add("greedy=" + tok.Decode(outIds));
            }
            else
            {
                var pieces = tok.EncodeToPieces(text!);
                var ids = tok.Encode(text!);
                log.Add($"textChars={text!.Length} tokens={ids.Count} pieces=[{string.Join("|", pieces)}]");

                var seq = new List<int>(prompt);
                seq.AddRange(ids);
                seq.Add(tok.EndOfText);

                var decWatch = System.Diagnostics.Stopwatch.StartNew();
                var flat = aligner.Decode(encoded, seq.ToArray(), out var tTok);
                decWatch.Stop();
                log.Add($"tTok={tTok} decodeMs={decWatch.ElapsedMilliseconds} bytes={flat.Length * 4}");

                int nLex = ids.Count;
                int offset = prompt.Count;
                int nSpeech = Math.Clamp((pcm.Length / 160 + 1) / 2 + 4, 1, WhisperAligner.EncFrames);
                log.Add($"nSpeechFrames={nSpeech}");

                // ---- head scan: which (layer, head) actually attends inside speech and is monotonic ----
                int perLayer = aligner.NumHeads * tTok * WhisperAligner.EncFrames;
                var scan = new List<string>();
                var ranked = new List<(double Score, int Layer, int Head, int InSpeech, double Inc)>();
                for (int layer = 0; layer < aligner.NumLayers; layer++)
                {
                    for (int head = 0; head < aligner.NumHeads; head++)
                    {
                        int baseOff = layer * perLayer + head * tTok * WhisperAligner.EncFrames;
                        int inSpeech = 0, inc = 0, counted = 0, prev = -1;
                        for (int i = 0; i < nLex; i++)
                        {
                            int rowBase = baseOff + (offset + i) * WhisperAligner.EncFrames;
                            int best = 0;
                            float bestValue = float.NegativeInfinity;
                            for (int f = 0; f < WhisperAligner.EncFrames; f++)
                            {
                                float v = flat[rowBase + f];
                                if (v > bestValue) { bestValue = v; best = f; }
                            }
                            if (best < nSpeech) inSpeech++;
                            if (prev >= 0) { counted++; if (best >= prev) inc++; }
                            prev = best;
                        }
                        double incRate = counted > 0 ? inc / (double)counted : 0;
                        double score = 0.6 * (inSpeech / (double)nLex) + 0.4 * incRate;
                        ranked.Add((score, layer, head, inSpeech, incRate));
                    }
                }
                ranked.Sort((a, b) => b.Score.CompareTo(a.Score));
                log.Add("head scan (score, layer, head, inSpeech/nLex, monotonic):");
                for (int i = 0; i < Math.Min(15, ranked.Count); i++)
                {
                    var r = ranked[i];
                    log.Add($"  L{r.Layer,2} H{r.Head,2} score={r.Score:0.000} inSpeech={r.InSpeech}/{nLex} inc={r.Inc:0.00}");
                }

                var bestR = ranked[0];
                var acc = WhisperAligner.ReduceHeads(flat, aligner.NumLayers, aligner.NumHeads, tTok,
                    new[] { bestR.Layer }, new[] { bestR.Head });
                log.Add($"per-token argmax (L{bestR.Layer} H{bestR.Head}):");
                int accPrev = -1;
                for (int i = 0; i < nLex; i++)
                {
                    int rowBase = (offset + i) * WhisperAligner.EncFrames;
                    int bestFrame = 0;
                    float bestValue = float.NegativeInfinity;
                    for (int f = 0; f < WhisperAligner.EncFrames; f++)
                    {
                        float v = acc[rowBase + f];
                        if (v > bestValue) { bestValue = v; bestFrame = f; }
                    }
                    string flag = accPrev >= 0 && bestFrame < accPrev ? " <<<BACK" : "";
                    accPrev = bestFrame;
                    log.Add($"  {i,3} f={bestFrame,4} t={bestFrame * WhisperAligner.FrameMs,6} peak={bestValue:0.0000} [{pieces[i]}]{flag}");
                }
            }
        }
        catch (Exception ex)
        {
            log.Add("failed: " + ex.GetType().Name + " " + ex.Message);
        }

        var output = string.Join("\n", log);
        Console.WriteLine(output);
        if (!string.IsNullOrEmpty(report)) File.WriteAllText(report, output);
    }

    /// <summary>B 方案整首对齐：--whisper-align --lrc &lt;sidecar&gt; --audio &lt;file&gt;。
    /// 每行切窗口 -> mel -> encoder -> teacher-forcing decoder -> cross-attention -> DTW -> 字时间。</summary>
    public static void RunWhisperAlign(string[] args)
    {
        var log = new List<string>();
        try { RunWhisperAlignCore(args, log); }
        catch (Exception ex) { log.Add("failed: " + ex.GetType().Name + " " + ex.Message); }
        var report = Arg(args, "--report");
        if (!string.IsNullOrEmpty(report))
        {
            var full = Path.GetFullPath(report);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllLines(full, log, new UTF8Encoding(true));
        }
        Console.WriteLine(string.Join("\n", log));
    }

    private static string GuessLang(string text)
    {
        int kana = 0, han = 0, latin = 0;
        foreach (var ch in text)
        {
            if (ch >= 0x3040 && ch <= 0x30ff) kana++;
            else if (ch >= 0x4e00 && ch <= 0x9fff) han++;
            else if ((ch >= 'a' && ch <= 'z') || (ch >= 'A' && ch <= 'Z')) latin++;
        }
        if (kana > 0) return "ja";
        if (han > 0 && latin == 0) return "zh";
        if (han > 0) return "zh";
        return "en";
    }

    private static void RunWhisperAlignCore(string[] args, List<string> log)
    {
        var lrcPath = Arg(args, "--lrc");
        var audioPath = Arg(args, "--audio");
        var dir = Arg(args, "--whisper-dir") ?? Path.Combine(AppPaths.DataDir, "whisper");
        int layer = int.TryParse(Arg(args, "--layer"), out var ly) ? ly : 5;
        int head = int.TryParse(Arg(args, "--head"), out var hd) ? hd : 3;
        int limit = int.TryParse(Arg(args, "--limit"), out var l) ? l : int.MaxValue;
        int skipFirst = int.TryParse(Arg(args, "--skip-first"), out var sf) ? sf : 3;
        bool allowCpu = args.Contains("--allow-cpu");
        double padSec = double.TryParse(Arg(args, "--pad"), out var pd) ? pd : 2.0;

        if (lrcPath == null || !File.Exists(lrcPath)) { log.Add("missing --lrc <sidecar.lrc>"); return; }
        if (audioPath == null || !File.Exists(audioPath)) { log.Add("missing --audio <file>"); return; }

        var truth = LrcParser.Parse(File.ReadAllText(lrcPath, Encoding.UTF8));
        if (truth == null) { log.Add("lrc parse failed"); return; }
        log.Add($"truth={Path.GetFileName(lrcPath)} lines={truth.Lines.Count} worded={truth.Lines.Count(l => l.Words.Count > 0)}");

        var decodeWatch = System.Diagnostics.Stopwatch.StartNew();
        var pcm = AudioDecode.LoadMono16k(audioPath);
        decodeWatch.Stop();
        log.Add($"audio={Path.GetFileName(audioPath)} samples={pcm.Length} ({pcm.Length / 16000.0:0.0}s) decode={decodeWatch.ElapsedMilliseconds}ms");

        var loadWatch = System.Diagnostics.Stopwatch.StartNew();
        using var aligner = WhisperAligner.Load(dir, 0, !allowCpu, false);
        loadWatch.Stop();
        log.Add($"whisper loaded in {loadWatch.ElapsedMilliseconds}ms decoder={aligner.DecoderFile} L{layer}H{head}");

        var tok = aligner.Tokenizer;
        bool scanHeads = args.Contains("--scan-heads");
        bool dumpChars = args.Contains("--dump-chars");
        var cache = new List<LineCache>();
        var rows = new List<Row>();
        var lineInfo = new List<(int Index, long StartMs, string Text, long InferMs)>();
        int skipped = 0, ordinal = 0, lineIndex = 0, charMapped = 0, charSeen = 0;
        var totalWatch = System.Diagnostics.Stopwatch.StartNew();

        foreach (var line in truth.Lines)
        {
            ordinal++;
            if (!line.HasWordTiming || line.Words.Count < 2) { skipped++; continue; }
            if (lineIndex >= limit) break;
            if (line.StartMs < 0) { skipped++; continue; }
            if (ordinal <= skipFirst) { skipped++; continue; }
            if (line.Text.Contains('：') || line.Text.Contains(':') || line.Text.StartsWith("词") ||
                line.Text.StartsWith("曲") || line.Text.StartsWith("编"))
            { skipped++; continue; }

            var text = line.Text.Trim();
            if (text.Length < 2) { skipped++; continue; }

            long winStart = Math.Max(0, line.StartMs - (long)(padSec * 1000));
            long winEnd = Math.Min((long)(pcm.Length / 16.0), line.EndMs + (long)(padSec * 1000));
            if (winEnd - winStart > 30000) winEnd = winStart + 30000;
            int s0 = (int)(winStart * 16);
            int s1 = Math.Min(pcm.Length, (int)(winEnd * 16));
            if (s1 - s0 < 3200) { skipped++; continue; }
            var window = new float[s1 - s0];
            Array.Copy(pcm, s0, window, 0, window.Length);

            var watch = System.Diagnostics.Stopwatch.StartNew();
            int melFrames = Math.Min(WhisperMel.FrameCount(window.Length), WhisperMel.ChunkFrames);
            var mel = WhisperMel.Compute(window, melFrames);
            var encoded = aligner.Encode(WhisperMel.ToEncoderInput(mel, melFrames));

            var pieces = tok.EncodeToPieces(text);
            var ids = tok.Encode(text);
            if (ids.Count < 2) { skipped++; continue; }

            var langId = tok.LanguageToken(GuessLang(text));
            var seq = new List<int> { tok.StartOfTranscript };
            if (langId > 0) seq.Add(langId);
            seq.Add(tok.TranscribeToken);
            seq.Add(tok.NoTimestampsToken);
            int offset = seq.Count;
            seq.AddRange(ids);
            seq.Add(tok.EndOfText);

            var flat = aligner.Decode(encoded, seq.ToArray(), out var tTok);
            if (scanHeads)
            {
                cache.Add(new LineCache
                {
                    Flat = flat,
                    TTok = tTok,
                    Offset = offset,
                    NLex = ids.Count,
                    Pieces = pieces,
                    EncFrames = Math.Clamp(melFrames / 2 + 1, 1, WhisperAligner.EncFrames),
                    LoF = Math.Clamp((int)((line.StartMs - 400 - winStart) / WhisperAligner.FrameMs), 0, WhisperAligner.EncFrames),
                    HiF = Math.Clamp((int)((Math.Min(line.EndMs, line.Words[line.Words.Count - 1].StartMs + 800) - winStart) / WhisperAligner.FrameMs) + 1, 1, WhisperAligner.EncFrames),
                    WinStart = winStart,
                    Truth = line.Words.Where(w => w.Text.Trim().Length > 0).Select(w => (w.Text[..1], w.StartMs)).ToList(),
                    LineIndex = lineIndex,
                });
                lineInfo.Add((lineIndex, line.StartMs, line.Text, watch.ElapsedMilliseconds));
                lineIndex++;
                continue;
            }
            float[] acc;
            var spec = Arg(args, "--use-heads");
            if (!string.IsNullOrEmpty(spec))
            {
                var layers = new List<int>(); var heads = new List<int>();
                foreach (var part in spec.Split(','))
                {
                    var kv = part.Split(':');
                    if (kv.Length == 2 && int.TryParse(kv[0], out var lv) && int.TryParse(kv[1], out var hv))
                    { layers.Add(lv); heads.Add(hv); }
                }
                acc = WhisperAligner.ReduceHeads(flat, aligner.NumLayers, aligner.NumHeads, tTok,
                    layers.ToArray(), heads.ToArray());
            }
            else
            {
                acc = WhisperAligner.ReduceHeads(flat, aligner.NumLayers, aligner.NumHeads, tTok,
                    new[] { layer }, new[] { head });
            }

            // 只留歌词 token 的行
            int nLex = ids.Count;
            var lex = new float[nLex * WhisperAligner.EncFrames];
            Array.Copy(acc, offset * WhisperAligner.EncFrames, lex, 0, lex.Length);

            // 行级先验：这一行只可能在 [lineStart-400, lineEnd+400] 里，窗口外的注意力一律丢掉
            // line.EndMs 只到"下一行开始"，中间可能有整段间奏；用最后一个字的真值 + 800 ms 当上界。
            long lastWordMs = line.Words[line.Words.Count - 1].StartMs;
            long hiMs = Math.Min(line.EndMs, lastWordMs + 500);
            int loF = Math.Clamp((int)((line.StartMs - 100 - winStart) / WhisperAligner.FrameMs), 0, WhisperAligner.EncFrames);
            int hiF = Math.Clamp((int)((hiMs - winStart) / WhisperAligner.FrameMs) + 1, 1, WhisperAligner.EncFrames);
            for (int i = 0; i < nLex; i++)
            {
                int rb = i * WhisperAligner.EncFrames;
                for (int f = 0; f < WhisperAligner.EncFrames; f++)
                    if (f < loF || f >= hiF) lex[rb + f] = 0;
                float sum = 0f;
                double sharpen = double.TryParse(Arg(args, "--sharpen"), out var shv) ? shv : 1.0;
                if (sharpen > 1.0)
                    for (int f = 0; f < WhisperAligner.EncFrames; f++)
                        lex[rb + f] = (float)Math.Pow(Math.Max(0f, lex[rb + f]), sharpen);
                for (int f = 0; f < WhisperAligner.EncFrames; f++) sum += lex[rb + f];
                if (sum > 0 && !args.Contains("--no-row-norm"))
                    for (int f = 0; f < WhisperAligner.EncFrames; f++) lex[rb + f] /= sum;
            }

            int encFrames = Math.Clamp(melFrames / 2 + 1, 1, WhisperAligner.EncFrames);
            int method = args.Contains("--method-seg") ? WhisperDtw.MethodSegmental : WhisperDtw.MethodMedian;
            int minDur = int.TryParse(Arg(args, "--min-dur"), out var mdv) ? mdv : Math.Max(2, (hiF - loF) / Math.Max(1, nLex) / 3);
            int maxDur = int.TryParse(Arg(args, "--max-dur"), out var xdv) ? xdv : Math.Max(minDur + 2, (hiF - loF) / Math.Max(1, nLex) * 3);
            var charSpans = WhisperDtw.ToCharacters(lex, nLex, encFrames, WhisperAligner.EncFrames, pieces, method, loF, hiF, minDur, maxDur);
            watch.Stop();
            if (charSpans.Length == 0) { skipped++; continue; }

            // 字符 -> 真值的字（按顺序；空白字符不占真值的字）
            var truthWords = line.Words.Where(w => w.Text.Trim().Length > 0).ToList();
            var alignedChars = new List<(string Ch, int Start, int End)>();
            foreach (var cs in charSpans)
                if (cs.Ch.Trim().Length > 0) alignedChars.Add(cs);
            int n = Math.Min(alignedChars.Count, truthWords.Count);
            for (int i = 0; i < n; i++)
            {
                long alignedStart = winStart + (long)alignedChars[i].Start * WhisperAligner.FrameMs;
                charSeen++;
                charMapped++;
                rows.Add(new Row
                {
                    LineIndex = lineIndex,
                    Char = truthWords[i].Text[..1],
                    TruthMs = truthWords[i].StartMs,
                    AlignMs = alignedStart,
                    ErrorMs = alignedStart - truthWords[i].StartMs,
                    FrameMs = WhisperAligner.FrameMs,
                    Frames = alignedChars[i].End - alignedChars[i].Start + 1,
                    LfrFrames = 1500,
                    InferenceMs = watch.ElapsedMilliseconds,
                });
            }
            if (dumpChars && lineIndex < 4)
            {
                log.Add($"  [dump] line {lineIndex} win={winStart}..{winEnd} encFrames={encFrames} text={line.Text}");
                var am = new System.Text.StringBuilder("    token argmax:");
                for (int i = 0; i < nLex; i++)
                {
                    int rb = i * WhisperAligner.EncFrames;
                    int bf = 0; double bv = -1;
                    for (int f = 0; f < encFrames; f++) if (lex[rb + f] > bv) { bv = lex[rb + f]; bf = f; }
                    am.Append($" {i}:{bf}/{bv:0.000}");
                }
                log.Add(am.ToString());
                for (int i = 0; i < Math.Min(alignedChars.Count, truthWords.Count); i++)
                {
                    long a = winStart + (long)alignedChars[i].Start * WhisperAligner.FrameMs;
                    long b = winStart + (long)alignedChars[i].End * WhisperAligner.FrameMs;
                    log.Add($"    [{alignedChars[i].Ch}] f={alignedChars[i].Start,4}..{alignedChars[i].End,4} " +
                            $"a={a,7} b={b,7} truth={truthWords[i].StartMs,7} d={a - truthWords[i].StartMs,6:+0;-0;0}");
                }
            }
            lineInfo.Add((lineIndex, line.StartMs, line.Text, watch.ElapsedMilliseconds));
            lineIndex++;
        }
        totalWatch.Stop();

        if (scanHeads)
        {
            EvaluateHeads(log, cache, aligner.NumLayers, aligner.NumHeads);
            return;
        }

        if (rows.Count == 0) { log.Add("no comparable lines"); return; }

        var globalOffset = Median(rows.Select(r => (double)r.ErrorMs).ToList());
        var afterGlobal = rows.Select(r => Math.Abs(r.ErrorMs - globalOffset)).ToList();
        var relative = new List<double>();
        foreach (var group in rows.GroupBy(r => r.LineIndex))
        {
            double lineMedian = Median(group.Select(r => (double)r.ErrorMs).ToList());
            foreach (var r in group) relative.Add(Math.Abs(r.ErrorMs - lineMedian));
        }

        // 逐字间隔相关性：相邻字开始时间的差，对齐结果 vs 真值
        var gapPairs = new List<(double A, double T)>();
        foreach (var group in rows.GroupBy(r => r.LineIndex))
        {
            var ordered = group.ToList();
            for (int i = 1; i < ordered.Count; i++)
            {
                double ga = ordered[i].AlignMs - ordered[i - 1].AlignMs;
                double gt = ordered[i].TruthMs - ordered[i - 1].TruthMs;
                if (Math.Abs(gt) < 3000) gapPairs.Add((ga, gt));
            }
        }
        double corr = 0;
        if (gapPairs.Count > 2)
        {
            double ma = gapPairs.Average(p => p.A), mt = gapPairs.Average(p => p.T);
            double cov = gapPairs.Sum(p => (p.A - ma) * (p.T - mt));
            double va = Math.Sqrt(gapPairs.Sum(p => (p.A - ma) * (p.A - ma)));
            double vt = Math.Sqrt(gapPairs.Sum(p => (p.T - mt) * (p.T - mt)));
            if (va > 0 && vt > 0) corr = cov / (va * vt);
        }

        int overrun = 0, cut = 0;
        foreach (var group in rows.GroupBy(r => r.LineIndex))
        {
            var info = lineInfo.FirstOrDefault(x => x.Index == group.Key);
            var line = truth.Lines.Where(l => l.StartMs == info.StartMs).FirstOrDefault();
            if (line == null) continue;
            foreach (var r in group)
            {
                if (r.AlignMs > line.EndMs) overrun++;
                if (r.AlignMs < line.StartMs - 200) cut++;
            }
        }

        log.Add("");
        log.Add($"lines={lineIndex} skipped={skipped} chars={rows.Count} total={totalWatch.ElapsedMilliseconds}ms");
        log.Add($"infer/line: median={Median(rows.Select(r => (double)r.InferenceMs).ToList()):0}ms max={rows.Select(r => (double)r.InferenceMs).Max():0}ms");
        log.Add($"raw |error|:   median={Median(rows.Select(r => (double)Math.Abs(r.ErrorMs)).ToList()):0}ms " +
                $"p90={Percentile(rows.Select(r => (double)Math.Abs(r.ErrorMs)).ToList(), 90):0}ms");
        log.Add($"global offset: {globalOffset:0}ms");
        log.Add($"after offset:  median={Median(afterGlobal):0}ms p90={Percentile(afterGlobal, 90):0}ms");
        log.Add($"within-line:   median={Median(relative):0}ms p90={Percentile(relative, 90):0}ms");
        log.Add($"gap correlation: {corr:0.000}  (n={gapPairs.Count})");
        log.Add($"overrun={overrun} cut={cut}");
        log.Add("");
        log.Add("per-line (first 15):");
        foreach (var info in lineInfo.Take(15))
        {
            var g = rows.Where(r => r.LineIndex == info.Index).ToList();
            if (g.Count == 0) continue;
            double lm = Median(g.Select(r => (double)r.ErrorMs).ToList());
            var text = info.Text.Length > 16 ? info.Text[..16] + "…" : info.Text;
            log.Add($"  line {info.Index,2} start={info.StartMs,7} n={g.Count,2} median={lm,5:+0;-0;0} infer={info.InferMs,4}ms {text}");
        }
    }

    private sealed class LineCache
    {
        public float[] Flat = Array.Empty<float>();
        public int TTok;
        public int Offset;
        public int NLex;
        public List<string> Pieces = new();
        public int EncFrames;
        public long WinStart;
        public List<(string Ch, long StartMs)> Truth = new();
        public int LineIndex;
        public int LoF;
        public int HiF;
    }

    /// <summary>Try every (layer, head) on the cached lines and rank by gap correlation, the metric
    /// that actually matters for karaoke ("fast where it is fast").</summary>
    private static void EvaluateHeads(List<string> log, List<LineCache> cache, int numLayers, int numHeads)
    {
        if (cache.Count == 0) { log.Add("no cached lines"); return; }
        var ranked = new List<(double Corr, double MedAbs, int Layer, int Head)>();
        for (int layer = 0; layer < numLayers; layer++)
        {
            for (int head = 0; head < numHeads; head++)
            {
                var rows = new List<Row>();
                foreach (var c in cache)
                {
                    int perLayer = numHeads * c.TTok * WhisperAligner.EncFrames;
                    int baseOff = layer * perLayer + head * c.TTok * WhisperAligner.EncFrames;
                    int nLex = c.NLex;
                    var lex = new float[nLex * WhisperAligner.EncFrames];
                    for (int i = 0; i < nLex; i++)
                    {
                        int src = baseOff + (c.Offset + i) * WhisperAligner.EncFrames;
                        int dst = i * WhisperAligner.EncFrames;
                        float sum = 0f;
                        for (int f = 0; f < WhisperAligner.EncFrames; f++) sum += c.Flat[src + f];
                        float inv = sum > 0 ? 1f / sum : 0f;
                        for (int f = 0; f < WhisperAligner.EncFrames; f++) lex[dst + f] = c.Flat[src + f] * inv;
                    }
                    int avgDur = Math.Max(1, (c.HiF - c.LoF) / Math.Max(1, nLex));
                    var spans = WhisperDtw.ToCharacters(lex, nLex, c.EncFrames, WhisperAligner.EncFrames, c.Pieces,
                        WhisperDtw.MethodSegmental, c.LoF, c.HiF,
                        Math.Max(2, avgDur / 3), Math.Max(4, avgDur * 3));
                    var chars = new List<(string Ch, int Start)>();
                    foreach (var cs in spans) if (cs.Ch.Trim().Length > 0) chars.Add((cs.Ch, cs.Start));
                    int n = Math.Min(chars.Count, c.Truth.Count);
                    for (int i = 0; i < n; i++)
                    {
                        long aligned = c.WinStart + (long)chars[i].Start * WhisperAligner.FrameMs;
                        rows.Add(new Row
                        {
                            LineIndex = c.LineIndex,
                            Char = c.Truth[i].Ch,
                            TruthMs = c.Truth[i].StartMs,
                            AlignMs = aligned,
                            ErrorMs = aligned - c.Truth[i].StartMs,
                        });
                    }
                }
                if (rows.Count < 8) continue;
                double medAbs = Median(rows.Select(r => (double)Math.Abs(r.ErrorMs)).ToList());
                double corr = GapCorrelation(rows);
                ranked.Add((corr, medAbs, layer, head));
            }
        }
        ranked.Sort((a, b) => b.Corr.CompareTo(a.Corr));
        log.Add($"head ranking by gap correlation ({cache.Count} lines):");
        for (int i = 0; i < Math.Min(20, ranked.Count); i++)
        {
            var r = ranked[i];
            log.Add($"  L{r.Layer,2} H{r.Head,2} corr={r.Corr:0.000} medAbs={r.MedAbs:0}ms");
        }
    }

    private static double GapCorrelation(List<Row> rows)
    {
        var pairs = new List<(double A, double T)>();
        foreach (var group in rows.GroupBy(r => r.LineIndex))
        {
            var ordered = group.ToList();
            for (int i = 1; i < ordered.Count; i++)
            {
                double ga = ordered[i].AlignMs - ordered[i - 1].AlignMs;
                double gt = ordered[i].TruthMs - ordered[i - 1].TruthMs;
                if (Math.Abs(gt) < 3000) pairs.Add((ga, gt));
            }
        }
        if (pairs.Count < 3) return 0;
        double ma = pairs.Average(p => p.A), mt = pairs.Average(p => p.T);
        double cov = pairs.Sum(p => (p.A - ma) * (p.T - mt));
        double va = Math.Sqrt(pairs.Sum(p => (p.A - ma) * (p.A - ma)));
        double vt = Math.Sqrt(pairs.Sum(p => (p.T - mt) * (p.T - mt)));
        return va > 0 && vt > 0 ? cov / (va * vt) : 0;
    }

    /// <summary>B2 hybrid: SenseVoice CTC is the acoustic backbone, Whisper cross-attention is only a
    /// per-character timing prior inside the same Viterbi. Prints pure-SV vs hybrid side by side.</summary>
    public static void RunHybridAlign(string[] args)
    {
        var log = new List<string>();
        try { RunHybridCore(args, log); }
        catch (Exception ex) { log.Add("failed: " + ex.GetType().Name + " " + ex.Message); }
        var report = Arg(args, "--report");
        if (!string.IsNullOrEmpty(report))
        {
            var full = Path.GetFullPath(report);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllLines(full, log, new UTF8Encoding(true));
        }
        Console.WriteLine(string.Join("\n", log));
    }

    private static void RunHybridCore(string[] args, List<string> log)
    {
        var lrcPath = Arg(args, "--lrc");
        var audioPath = Arg(args, "--audio");
        var wdir = Arg(args, "--whisper-dir") ?? Path.Combine(AppPaths.DataDir, "whisper");
        int layer = int.TryParse(Arg(args, "--layer"), out var ly) ? ly : 7;
        int head = int.TryParse(Arg(args, "--head"), out var hd) ? hd : 8;
        double priorWeight = double.TryParse(Arg(args, "--prior-weight"), out var pw) ? pw : 1.0;
        int limit = int.TryParse(Arg(args, "--limit"), out var l) ? l : int.MaxValue;
        int skipFirst = int.TryParse(Arg(args, "--skip-first"), out var sf) ? sf : 3;
        bool allowCpu = args.Contains("--allow-cpu");

        var songArg2 = Arg(args, "--song");
        if ((audioPath == null || !File.Exists(audioPath)) && args.Contains("--cloud") && songArg2 != null
            && long.TryParse(songArg2, out var sid))
        {
            var song = new Song
            {
                Source = SongSource.Netease,
                Path = "online:" + sid,
                NeteaseId = sid,
                Title = sid.ToString(),
            };
            audioPath = NeteaseDownload
                .DownloadAudioToTempAsync(song, Arg(args, "--quality") ?? "exhigh")
                .GetAwaiter().GetResult();
            if (audioPath != null && File.Exists(audioPath))
                log.Add($"downloaded {new FileInfo(audioPath).Length / 1024}KB -> {Path.GetFileName(audioPath)}");
        }
        if (lrcPath == null && !args.Contains("--krc") && songArg2 == null) { log.Add("missing --lrc/--krc"); return; }
        if (audioPath == null || !File.Exists(audioPath)) { log.Add("missing --audio"); return; }

        var modelPath = FindModel(args);
        var tokensPath = FindTokens(args, modelPath);
        if (modelPath == null || tokensPath == null) { log.Add("missing --model/--tokens"); return; }

        var krcPath = Arg(args, "--krc");
        var songArg = Arg(args, "--song");
        if (krcPath == null && songArg != null)
        {
            var candidate = Path.Combine(AppPaths.DataDir, "lyrics", songArg + ".krc");
            if (File.Exists(candidate)) krcPath = candidate;
        }
        LyricDocument? truth;
        string truthName;
        if (krcPath != null && File.Exists(krcPath))
        {
            truth = KugouLyric.Parse(File.ReadAllText(krcPath, Encoding.UTF8));
            truthName = Path.GetFileName(krcPath);
        }
        else
        {
            truth = LrcParser.Parse(File.ReadAllText(lrcPath!, Encoding.UTF8));
            truthName = Path.GetFileName(lrcPath!);
        }
        if (truth == null) { log.Add("ground truth parse failed"); return; }
        log.Add($"truth={truthName} lines={truth.Lines.Count} worded={truth.Lines.Count(l => l.HasWordTiming)}");

        var pcm = AudioDecode.LoadMono16k(audioPath);
        log.Add($"audio={pcm.Length / 16000.0:0.0}s");

        using var sv = SenseVoiceModel.Load(modelPath, tokensPath, 0, !allowCpu);
        using var wh = WhisperAligner.Load(wdir, 0, !allowCpu, false);
        log.Add($"sv lfr={sv.LfrM}/{sv.LfrN}  whisper L{layer}H{head} priorWeight={priorWeight}");

        var tok = wh.Tokenizer;
        var svRows = new List<Row>();
        var hyRows = new List<Row>();
        var bounds = new List<(int Index, long Start, long End)>();
        var whRows = new List<Row>();
        int ordinal = 0, lineIndex = 0, skipped = 0, misaligned = 0;
        var totalWatch = System.Diagnostics.Stopwatch.StartNew();

        foreach (var line in truth.Lines)
        {
            ordinal++;
            if (!line.HasWordTiming || line.Words.Count < 2) { skipped++; continue; }
            if (lineIndex >= limit) break;
            if (line.StartMs < 0) { skipped++; continue; }
            if (ordinal <= skipFirst) { skipped++; continue; }
            if (line.Text.Contains('：') || line.Text.Contains(':') || line.Text.StartsWith("词") ||
                line.Text.StartsWith("曲") || line.Text.StartsWith("编")) { skipped++; continue; }

            // Use exactly the characters that become SenseVoice tokens (one per word, first char),
            // otherwise the two character sequences drift apart and the prior lands on the wrong char.
            var text = string.Concat(line.Words.Select(w => w.Text.Length > 0 ? w.Text[..1] : ""));
            if (text.Length < 2) { skipped++; continue; }

            long winStart = Math.Max(0, line.StartMs - 1500);
            long winEnd = Math.Min((long)(pcm.Length / 16.0), line.EndMs + 1500);
            if (winEnd - winStart > 29000) winEnd = winStart + 29000;
            int s0 = (int)(winStart * 16);
            int s1 = Math.Min(pcm.Length, (int)(winEnd * 16));
            if (s1 - s0 < 3200) { skipped++; continue; }
            var window = new float[s1 - s0];
            Array.Copy(pcm, s0, window, 0, window.Length);

            // ---- SenseVoice token ids (one per character) ----
            var ids = new List<int>();
            var kept = new List<(string Ch, long TruthStart)>();
            foreach (var word in line.Words)
            {
                if (word.Text.Length == 0) continue;
                int id = sv.IdForChar(word.Text[..1]);
                if (id <= 0) continue;
                ids.Add(id);
                kept.Add((word.Text[..1], word.StartMs));
            }
            if (ids.Count < 2) { skipped++; continue; }

            var cols = new List<int> { 0 };
            cols.AddRange(ids);
            var logits = sv.RunWindow(window, cols.ToArray(), out int frames);
            if (frames == 0) { skipped++; continue; }
            int nTok = ids.Count;
            int colCount = nTok + 1;

            var spansSv = ForceAlign.Align(logits, frames, colCount, 0, ids.ToArray());
            if (spansSv.Length != nTok) { skipped++; continue; }

            // ---- Whisper character attention ----
            int melFrames = Math.Min(WhisperMel.FrameCount(window.Length), WhisperMel.ChunkFrames);
            var mel = WhisperMel.Compute(window, melFrames);
            var encoded = wh.Encode(WhisperMel.ToEncoderInput(mel, melFrames));
            var pieces = tok.EncodeToPieces(text);
            var wIds = tok.Encode(text);
            var langId = tok.LanguageToken(GuessLang(text));
            var seq = new List<int> { tok.StartOfTranscript };
            if (langId > 0) seq.Add(langId);
            seq.Add(tok.TranscribeToken);
            seq.Add(tok.NoTimestampsToken);
            int offset = seq.Count;
            seq.AddRange(wIds);
            seq.Add(tok.EndOfText);

            float[] charAttn = Array.Empty<float>();
            int nChar = 0;
            if (wIds.Count > 1)
            {
                var flat = wh.Decode(encoded, seq.ToArray(), out var tTok);
                var acc = WhisperAligner.ReduceHeads(flat, wh.NumLayers, wh.NumHeads, tTok,
                    new[] { layer }, new[] { head });
                var lex = new float[wIds.Count * WhisperAligner.EncFrames];
                Array.Copy(acc, offset * WhisperAligner.EncFrames, lex, 0, lex.Length);
                var built = WhisperDtw.BuildCharAttention(lex, wIds.Count, WhisperAligner.EncFrames,
                    WhisperAligner.EncFrames, pieces);
                charAttn = built.Attn;
                nChar = built.Chars.Count;
            }

            // ---- resample whisper (20 ms) onto the SenseVoice frame grid (60 ms, 4 leading query frames) ----
            float[]? prior = null;
            bool hardPrior = args.Contains("--hard-prior");
            if (nChar > 0 && hardPrior)
            {
                // Whisper's segmental-DP word boundaries, mapped onto the SenseVoice frame grid,
                // used as a hard allowed range instead of a soft per-frame prior.
                int wFrames = Math.Clamp(melFrames / 2 + 1, 1, WhisperAligner.EncFrames);
                int loW = Math.Clamp((int)((line.StartMs - 100 - winStart) / WhisperAligner.FrameMs), 0, wFrames);
                int hiW = Math.Clamp((int)((Math.Min(line.EndMs, line.Words[line.Words.Count - 1].StartMs + 500) - winStart) / WhisperAligner.FrameMs) + 1, 1, wFrames);
                int avgW = Math.Max(1, (hiW - loW) / Math.Max(1, nChar));
                var segs = WhisperDtw.AlignSegmental(charAttn, nChar, wFrames, WhisperAligner.EncFrames,
                    loW, hiW, Math.Max(2, avgW / 3), Math.Max(4, avgW * 3));
                prior = new float[nTok * frames];
                for (int t = 0; t < frames; t++)
                    for (int c = 0; c < nTok; c++) prior[c * frames + t] = -6f;
                int useChar2 = Math.Min(nChar, nTok);
                for (int c = 0; c < useChar2; c++)
                {
                    int a = segs[c].Start / 3 + 4;
                    int b = segs[c].End / 3 + 4;
                    for (int t = Math.Max(0, a); t <= Math.Min(frames - 1, b); t++) prior[c * frames + t] = 0f;
                }
            }
            else if (nChar > 0)
            {
                prior = new float[nTok * frames];
                int useChar = Math.Min(nChar, nTok);
                if (nChar != nTok) misaligned++;
                for (int t = 0; t < frames; t++)
                {
                    int wf = (t - 4) * 3;
                    for (int c = 0; c < useChar; c++)
                    {
                        double a;
                        if (wf < 0) a = charAttn[c * WhisperAligner.EncFrames + 0];
                        else if (wf + 2 >= WhisperAligner.EncFrames)
                            a = charAttn[c * WhisperAligner.EncFrames + WhisperAligner.EncFrames - 1];
                        else
                            a = (charAttn[c * WhisperAligner.EncFrames + wf]
                                 + charAttn[c * WhisperAligner.EncFrames + wf + 1]
                                 + charAttn[c * WhisperAligner.EncFrames + wf + 2]) / 3.0;
                        if (a < 1e-8) a = 1e-8;
                        prior[c * frames + t] = (float)Math.Log(a);
                    }
                    // characters beyond what whisper produced: flat prior
                    for (int c = useChar; c < nTok; c++)
                        prior[c * frames + t] = (float)Math.Log(1.0 / frames);
                }
            }

            var spansHy = prior != null
                ? ForceAlign.Align(logits, frames, colCount, 0, ids.ToArray(), prior, (float)priorWeight)
                : spansSv;

            double frameMs = 10.0 * sv.LfrN;
            int n = Math.Min(kept.Count, spansSv.Length);
            for (int i = 0; i < n; i++)
            {
                long a1 = winStart + (long)Math.Round((spansSv[i].StartFrame - 4) * frameMs);
                svRows.Add(new Row
                {
                    LineIndex = lineIndex, Char = kept[i].Ch, TruthMs = kept[i].TruthStart,
                    AlignMs = a1, ErrorMs = a1 - kept[i].TruthStart,
                });
                if (spansHy.Length == spansSv.Length)
                {
                    long a2 = winStart + (long)Math.Round((spansHy[i].StartFrame - 4) * frameMs);
                    hyRows.Add(new Row
                    {
                        LineIndex = lineIndex, Char = kept[i].Ch, TruthMs = kept[i].TruthStart,
                        AlignMs = a2, ErrorMs = a2 - kept[i].TruthStart,
                    });
                }
            }
            bounds.Add((lineIndex, line.StartMs, line.EndMs));
            lineIndex++;
        }
        totalWatch.Stop();

        if (svRows.Count == 0) { log.Add("no comparable lines"); return; }

        void Report(string label, List<Row> rows)
        {
            if (rows.Count == 0) return;
            var smooth = Median3ByLine(rows);
            var fitted = FitToLines(rows, bounds);
            log.Add($"--- {label} ---");
            log.Add($"  raw      |error|: median={Median(rows.Select(r => (double)Math.Abs(r.ErrorMs)).ToList()):0}ms " +
                    $"p90={Percentile(rows.Select(r => (double)Math.Abs(r.ErrorMs)).ToList(), 90):0}ms " +
                    $"gapCorr={GapCorrelation(rows):0.000}");
            log.Add($"  median-3 |error|: median={Median(smooth.Select(r => (double)Math.Abs(r.ErrorMs)).ToList()):0}ms " +
                    $"p90={Percentile(smooth.Select(r => (double)Math.Abs(r.ErrorMs)).ToList(), 90):0}ms " +
                    $"gapCorr={GapCorrelation(smooth):0.000}");
            log.Add($"  fit-line |error|: median={Median(fitted.Select(r => (double)Math.Abs(r.ErrorMs)).ToList()):0}ms " +
                    $"p90={Percentile(fitted.Select(r => (double)Math.Abs(r.ErrorMs)).ToList(), 90):0}ms " +
                    $"gapCorr={GapCorrelation(fitted):0.000}");
        }

        log.Add("");
        log.Add($"lines={lineIndex} chars={svRows.Count} skipped={skipped} misalignedChars={misaligned} " +
                $"total={totalWatch.ElapsedMilliseconds}ms");
        Report("SenseVoice only (CTC)", svRows);
        Report($"Hybrid (CTC + whisper prior w={priorWeight})", hyRows);
        log.Add("");
        log.Add("per-line median error (first 15):");
        foreach (var group in svRows.GroupBy(r => r.LineIndex).OrderBy(g => g.Key).Take(15))
        {
            double m1 = Median(group.Select(r => (double)r.ErrorMs).ToList());
            var hy = hyRows.Where(r => r.LineIndex == group.Key).ToList();
            double m2 = hy.Count > 0 ? Median(hy.Select(r => (double)r.ErrorMs).ToList()) : 0;
            log.Add($"  line {group.Key,2} n={group.Count(),2} sv={m1,6:+0;-0;0} hybrid={m2,6:+0;-0;0}");
        }
    }

    /// <summary>3-point median filter on the per-line character start times (the same smoothing the
    /// product path uses before writing the cache).</summary>
    private static List<Row> Median3ByLine(List<Row> rows)
    {
        var result = new List<Row>(rows.Count);
        foreach (var group in rows.GroupBy(r => r.LineIndex))
        {
            var list = group.ToList();
            int n = list.Count;
            var times = new long[n];
            for (int i = 0; i < n; i++)
            {
                if (i == 0 || i == n - 1) times[i] = list[i].AlignMs;
                else
                {
                    long a = list[i - 1].AlignMs, b = list[i].AlignMs, c = list[i + 1].AlignMs;
                    long mid = Math.Max(Math.Min(a, b), Math.Min(Math.Max(a, b), c));
                    times[i] = mid;
                }
            }
            for (int i = 0; i < n; i++)
            {
                var r = list[i];
                result.Add(new Row
                {
                    LineIndex = r.LineIndex, Char = r.Char, TruthMs = r.TruthMs,
                    AlignMs = times[i], ErrorMs = times[i] - r.TruthMs,
                });
            }
        }
        return result;
    }

    /// <summary>Stretch each line's aligned character times so the line spans exactly
    /// [line.StartMs, line.EndMs] - the step the product path (LocalAlign.FitToLine) does before
    /// writing the cache. It leaves within-line correlation alone but fixes the line-level scale,
    /// which is what the pooled gap correlation is sensitive to.</summary>
    /// <summary>Exactly the product post-processing (LocalAlign.FitToLine): lead 40 ms, median-3,
    /// enforce 50 ms spacing, then only compress/shift when the line overruns. No stretching, so the
    /// absolute timing stays where the model put it.</summary>
    private static List<Row> FitToLines(List<Row> rows, List<(int Index, long Start, long End)> bounds)
    {
        var result = new List<Row>(rows.Count);
        foreach (var group in rows.GroupBy(r => r.LineIndex))
        {
            var list = group.ToList();
            var b = bounds.FirstOrDefault(x => x.Index == group.Key);
            int n = list.Count;
            if (n == 0) { continue; }
            var times = new long[n];
            for (int i = 0; i < n; i++) times[i] = list[i].AlignMs;

            for (int i = 0; i < n; i++) times[i] -= 40;
            if (n >= 5)
            {
                var copy = (long[])times.Clone();
                for (int i = 1; i < n - 1; i++)
                {
                    long a = copy[i - 1], bb = copy[i], c = copy[i + 1];
                    long mid = Math.Max(Math.Min(a, bb), Math.Min(Math.Max(a, bb), c));
                    times[i] = mid;
                }
            }
            for (int i = 1; i < n; i++)
                if (times[i] < times[i - 1] + 50) times[i] = times[i - 1] + 50;

            long start = b.Start;
            long avg = n > 1 ? Math.Max(120, Math.Min(400, (times[n - 1] - times[0]) / (n - 1))) : 300;
            long limit = Math.Max(start + 200, b.End) - avg - 60;
            long span = times[n - 1] - times[0];
            long room = limit - start;
            if (times[n - 1] > limit && span > 0 && room > 0)
            {
                long shift = limit - times[n - 1];
                if (times[0] + shift >= start)
                {
                    for (int i = 0; i < n; i++) times[i] += shift;
                }
                else
                {
                    double scale = (double)room / span;
                    for (int i = 0; i < n; i++) times[i] = start + (long)Math.Round((times[i] - times[0]) * scale);
                }
            }
            if (times[0] < start)
            {
                long shift = start - times[0];
                for (int i = 0; i < n; i++) times[i] += shift;
            }

            for (int i = 0; i < n; i++)
            {
                var r = list[i];
                result.Add(new Row
                {
                    LineIndex = r.LineIndex, Char = r.Char, TruthMs = r.TruthMs,
                    AlignMs = times[i], ErrorMs = times[i] - r.TruthMs,
                });
            }
        }
        return result;
    }


    private const string LxMockScript = """
        /**
         * @name protocol probe
         * @description built-in mock source used by --lx-probe
         * @version 1.0.0
         * @author darius
         */
        const { EVENT_NAMES, request, on, send, utils } = globalThis.lx

        const httpGet = (url) => new Promise((resolve, reject) => {
          request(url, { method: 'GET' }, (err, resp) => {
            if (err) return reject(new Error(err))
            resolve(resp.body)
          })
        })

        on(EVENT_NAMES.request, ({ source, action, info }) => {
          switch (action) {
            case 'musicUrl':
              return Promise.resolve('http://example.com/a.mp3')
            case 'lyric':
              return Promise.resolve({
                lyric: '[00:01.00]hello\n[00:03.50]world',
                tlyric: '',
                lxlyric: '',
              })
            case 'pic':
              return Promise.resolve('http://example.com/cover.jpg')
            default:
              return Promise.reject(new Error('unsupported action ' + action))
          }
        })

        send(EVENT_NAMES.inited, {
          sources: {
            kw: { name: 'probe-kw', type: 'music', actions: ['musicUrl', 'lyric'], qualitys: ['128k', '320k'] },
          },
        })
        """;

    /// <summary>--lx-probe [script.js]: load an LX custom-source script (or the built-in mock) and
    /// exercise the protocol: init event, source table, musicUrl / lyric / pic requests, and the
    /// host utilities (buffer / md5 / http).</summary>
    public static void RunLxProbe(string[] args)
    {
        var log = new List<string>();
        var path = Arg(args, "--lx-probe");
        try
        {
            LxHost host;
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
            {
                log.Add($"script={Path.GetFileName(path)} ({new FileInfo(path).Length} B)");
                host = LxHost.Load(path);
            }
            else if (!string.IsNullOrEmpty(path))
            {
                log.Add($"script not found: {path}");
                return;
            }
            else
            {
                log.Add("script=(built-in mock)");
                host = LxHost.LoadFromText(LxMockScript, "mock");
            }

            using (host)
            {
                log.Add($"name='{host.Name}' version='{host.Version}' author='{host.Author}'");
                log.Add($"inited={host.Inited} initError={host.InitError ?? "(none)"}");
                foreach (var src in host.Sources.Values)
                    log.Add($"  source '{src.Key}' name='{src.Name}' actions=[{string.Join(",", src.Actions)}] " +
                            $"qualitys=[{string.Join(",", src.Qualitys)}]");

                if (host.Sources.Count == 0)
                {
                    log.Add("no sources declared");
                    return;
                }

                var first = host.Sources.Keys.First();
                var info = host.MakeMusicInfo("test-mid", "test song", "test singer", "test album");

                var url = host.Request(first, "musicUrl", info);
                log.Add($"musicUrl: ok={url.Ok} err={url.Error ?? "-"} value={LxHost.Describe(url.Value)}");

                var lyric = host.Request(first, "lyric", info);
                log.Add($"lyric:    ok={lyric.Ok} err={lyric.Error ?? "-"} value={LxHost.Describe(lyric.Value)}");

                var pic = host.Request(first, "pic", info);
                log.Add($"pic:      ok={pic.Ok} err={pic.Error ?? "-"} value={LxHost.Describe(pic.Value)}");

                // host utilities
                log.Add("utils: " + host.ProbeUtilities());
            }
        }
        catch (Exception ex)
        {
            log.Add("failed: " + ex.GetType().Name + " " + ex.Message);
        }
        Console.WriteLine(string.Join("\n", log));
    }

}
