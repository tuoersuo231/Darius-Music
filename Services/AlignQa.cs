using System.IO;
using System.Text;
using System.Text.Json;
using CurrentLyrics.Models;

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
            if (line.Words.Count < 2) { skipped++; continue; }
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
}
