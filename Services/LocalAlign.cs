using System.IO;
using System.Text;
using System.Text.Json;
using CurrentLyrics.Models;

namespace CurrentLyrics.Services;

/// <summary>
/// D 方案的本体：三源都没词级时间时，用本机 GPU 跑一次强制对齐。
///
/// 成本：一行一个窗口（固定 8 s 形状，避免 DirectML 每个形状重编译），一行约 57 ms；
/// 整首 5 分钟的歌解码 1~2 s + 对齐 5 s 左右，跑完把结果写 <c>data\lyrics\&lt;id&gt;.align.json</c>，
/// 下次播放直接读盘。没有模型 / 没有 GPU / 探测失败时**什么都不做**，界面保持原来的均分。
/// </summary>
public static class LocalAlign
{
    private const int WindowSeconds = 8;
    private const int CacheVersion = 2;

    private static readonly object Gate = new();
    private static SenseVoiceModel? _model;
    private static bool _sessionBroken;
    private static readonly SemaphoreSlim Worker = new(1, 1);
    private static readonly HashSet<long> Attempted = new();
    private static int _generation;
    private static int _idleToken;

    /// <summary>模型算完先留着（连着放同一张专辑时省 3 秒），空闲这么久再放掉 —— 894 MB 的权重
    /// 常驻在音乐播放器里不合适。可用 DARIUS_ALIGN_IDLE_MS 调整。</summary>
    private static int IdleReleaseMs =>
        int.TryParse(Environment.GetEnvironmentVariable("DARIUS_ALIGN_IDLE_MS"), out var ms) && ms >= 0 ? ms : 60000;

    private static string AlignDir => Path.Combine(AppPaths.DataDir, "align");

    /// <summary>模型位置：data\align\model.onnx；QA 可以用 DARIUS_ALIGN_MODEL 指到别处。</summary>
    private static string ModelPath =>
        Environment.GetEnvironmentVariable("DARIUS_ALIGN_MODEL") is { Length: > 0 } custom && File.Exists(custom)
            ? custom
            : Path.Combine(AlignDir, "model.onnx");

    private static string TokensPath => Path.Combine(Path.GetDirectoryName(ModelPath)!, "tokens.txt");
    private static string DisabledMarker => Path.Combine(AppPaths.DataDir, "align.disabled");
    private static string LogPath => Path.Combine(AppPaths.DataDir, "align.log");

    /// <summary>模型在不在、有没有被永久禁用。缺模型不算失败，只是没装。</summary>
    public static bool Available => !File.Exists(DisabledMarker) && File.Exists(ModelPath) && File.Exists(TokensPath);

    /// <summary>缓存里的对齐结果（同一首歌第二次播放直接命中）。</summary>
    public static LyricDocument? TryCached(long songId, LyricDocument? baseDoc)
    {
        if (songId <= 0 || baseDoc == null) return null;
        var path = CachePath(songId);
        try
        {
            if (!File.Exists(path)) return null;
            var cache = JsonSerializer.Deserialize<CacheFile>(File.ReadAllText(path, Encoding.UTF8));
            if (cache == null || cache.V != CacheVersion || cache.Id != songId) return null;
            if (cache.Model != ModelTag()) return null;          // 换了模型就重算

            var doc = Clone(baseDoc);
            int applied = 0;
            foreach (var entry in cache.Lines)
            {
                if (entry.I < 0 || entry.I >= doc.Lines.Count) continue;
                var line = doc.Lines[entry.I];
                if (line.Text != entry.T) continue;              // 歌词文本变了（换了源），不套用
                if (entry.W.Count < 2) continue;
                line.Words = entry.W.Select(w => new LyricWord
                {
                    Text = w.T, StartMs = w.S, EndMs = w.E, StartOffset = w.O, EndOffset = w.O + w.T.Length,
                }).ToList();
                line.WordsEstimated = false;
                applied++;
            }
            if (applied < 3) return null;
            return doc;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 排一次对齐。调用方保证"AMLL / 酷狗 / 腾讯都空"才走到这里。
    /// 只保留最新的一次请求：换歌后旧任务算完也不会写回界面。
    /// </summary>
    public static void Schedule(long songId, Song song, LyricDocument baseDoc, string quality,
        Action<LyricDocument> ready)
    {
        if (!Available) return;
        if (song.Source == SongSource.Local && !File.Exists(song.Path)) return;
        if (song.Source != SongSource.Local && song.NeteaseId <= 0) return;
        if (baseDoc.Lines.Count < 4) return;
        // 本进程内每首只算一次：失败（解码失败/算不动）就不要每次播放都再烧 12 秒。
        lock (Gate) { if (!Attempted.Add(songId)) return; }
        LogRaw($"schedule id={songId} src={(song.Source == SongSource.Local ? "local" : "cloud")} " +
               $"path='{Path.GetFileName(song.Path)}' model='{Path.GetFileName(ModelPath)}'");

        int generation = Interlocked.Increment(ref _generation);
        Interlocked.Increment(ref _idleToken);      // 有新任务，取消已经排上的释放
        _ = Task.Run(async () =>
        {
            await Worker.WaitAsync().ConfigureAwait(false);
            var watch = System.Diagnostics.Stopwatch.StartNew();
            string? tempAudio = null;
            try
            {
                if (generation != Volatile.Read(ref _generation)) return;   // 已经换歌了
                var model = EnsureModel();
                if (model == null) return;
                LogRaw($"trace id={songId} model ok ({watch.ElapsedMilliseconds}ms)");

                // 在线行没有本地文件：只抓一份音频到临时目录，解完就删。
                var audioPath = song.Source == SongSource.Local ? song.Path : null;
                if (audioPath == null || !File.Exists(audioPath))
                {
                    audioPath = await NeteaseDownload.DownloadAudioToTempAsync(song, quality).ConfigureAwait(false);
                    tempAudio = audioPath;
                    if (audioPath == null)
                    {
                        Log(songId, song, 0, 0, watch.ElapsedMilliseconds, "no-audio");
                        return;
                    }
                    LogRaw($"trace id={songId} downloaded {new FileInfo(audioPath).Length / 1024}KB " +
                           $"({watch.ElapsedMilliseconds}ms)");
                    // QA hook: keep the temp audio so offline tools can re-run alignment on the exact
                    // same bytes the online path used.
                    var keepDir = Environment.GetEnvironmentVariable("DARIUS_KEEP_AUDIO");
                    if (!string.IsNullOrWhiteSpace(keepDir))
                    {
                        try
                        {
                            Directory.CreateDirectory(keepDir);
                            var target = Path.Combine(keepDir, songId + Path.GetExtension(audioPath));
                            File.Copy(audioPath, target, true);
                            LogRaw($"trace id={songId} kept-audio {target}");
                        }
                        catch (Exception kex) { LogRaw($"trace id={songId} keep-audio failed {kex.Message}"); }
                    }
                }

                var pcm = AudioDecode.LoadMono16k(audioPath);
                LogRaw($"trace id={songId} decoded {pcm.Length} samples ({watch.ElapsedMilliseconds}ms)");
                if (pcm.Length < SenseVoiceFeatures.SampleRate)
                {
                    Log(songId, song, 0, 0, watch.ElapsedMilliseconds, "decode-empty");
                    return;
                }

                var doc = Clone(baseDoc);
                int alignedLines = 0, chars = 0;
                int consecutiveSlow = 0;

                for (int index = 0; index < doc.Lines.Count; index++)
                {
                    if (generation != Volatile.Read(ref _generation)) return;
                    var line = doc.Lines[index];
                    // 注意 LrcParser.Parse 会给每行先塞"均分"的字（WordsEstimated=true），
                    // 那不是真词级时间 —— HasWordTiming 才代表真时间。
                    if (line.HasWordTiming) continue;
                    if (line.Text.Trim().Length == 0) continue;
                    // 「词：xxx / 曲：xxx」这类制作信息没人唱，对齐出来只会给它们配一串随机高亮。
                    if (line.Text.Contains('：') || line.Text.Contains(':') ||
                        line.Text.StartsWith("词") || line.Text.StartsWith("曲") || line.Text.StartsWith("编"))
                        continue;

                    var lineWatch = System.Diagnostics.Stopwatch.StartNew();
                    var words = AlignLine(model, line, pcm);
                    lineWatch.Stop();

                    if (lineWatch.ElapsedMilliseconds > 3000)
                    {
                        consecutiveSlow++;
                        if (consecutiveSlow >= 2)
                        {
                            // 这台机器算不动：本会话停用，不写永久标记（下次启动还能再试）。
                            Log(songId, song, alignedLines, chars, watch.ElapsedMilliseconds, "too-slow");
                            return;
                        }
                    }
                    else
                    {
                        consecutiveSlow = 0;
                    }

                    if (words.Count < 2) continue;
                    line.Words = words;
                    line.WordsEstimated = false;
                    alignedLines++;
                    chars += words.Count;
                }

                if (alignedLines < 3) return;

                SaveCache(songId, doc);
                Log(songId, song, alignedLines, chars, watch.ElapsedMilliseconds, "ok");
                ready(doc);
            }
            catch (Exception ex)
            {
                Log(songId, song, 0, 0, watch.ElapsedMilliseconds, ex.GetType().Name);
            }
            finally
            {
                if (tempAudio != null)
                {
                    try { File.Delete(tempAudio); } catch { }
                }
                Worker.Release();
                StartIdleRelease();
            }
        });
    }

    /// <summary>空闲一段时间后把会话放掉。期间只要有新任务排队，这次释放就作废。</summary>
    private static void StartIdleRelease()
    {
        int token = Volatile.Read(ref _idleToken);
        int delay = IdleReleaseMs;
        if (delay <= 0)
        {
            lock (Gate) { _model?.Dispose(); _model = null; }
            return;
        }
        _ = Task.Delay(delay).ContinueWith(_ =>
        {
            if (Volatile.Read(ref _idleToken) != token) return;
            lock (Gate)
            {
                _model?.Dispose();
                _model = null;
            }
        });
    }

    /// <summary>一行：按字符映射 token → 固定形状窗口 → Viterbi → 回到行内字的偏移。</summary>
    private static List<LyricWord> AlignLine(SenseVoiceModel model, LyricLine line, float[] pcm)
    {
        var clusters = LrcParser.Clusters(line.Text);
        if (clusters.Count == 0) return new List<LyricWord>();

        // 逐字符（跳过空白）建立 token 序列；一个字符映射不到就跳过它，不影响其它字。
        var charOffsets = new List<int>();
        var ids = new List<int>();
        for (int i = 0; i < line.Text.Length; i++)
        {
            if (char.IsWhiteSpace(line.Text[i])) continue;
            int id = model.IdForChar(line.Text[i].ToString());
            if (id <= 0) continue;
            charOffsets.Add(i);
            ids.Add(id);
        }
        if (ids.Count < 2) return new List<LyricWord>();

        // 窗口：行前后各 300 ms，长度按档取（4/6/8/…/30 s）。分档是为了让 DirectML 只编译这几种形状，
        // 否则每行一个新长度都要重编译一次（实测每行多 ~140 ms）。
        int total = pcm.Length;
        int startSample = Math.Max(0, (int)((line.StartMs - 300) * 16));
        int endSample = Math.Min(total, (int)((line.EndMs + 300) * 16));
        if (endSample <= startSample) return new List<LyricWord>();

        int needed = (int)Math.Ceiling((endSample - startSample) / (double)SenseVoiceFeatures.SampleRate);
        int seconds = 4;
        foreach (var bucket in new[] { 4, 6, 8, 10, 12, 14, 16, 20, 24, 30 })
        {
            seconds = bucket;
            if (bucket >= needed) break;
        }
        int target = seconds * SenseVoiceFeatures.SampleRate;
        var window = new float[target];
        int copy = Math.Min(target, endSample - startSample);
        Array.Copy(pcm, startSample, window, 0, copy);

        var columns = new int[ids.Count + 1];
        columns[0] = 0;
        for (int i = 0; i < ids.Count; i++) columns[i + 1] = ids[i];

        var logits = model.RunWindow(window, columns, out int frames);
        if (frames == 0) return new List<LyricWord>();

        var spans = ForceAlign.Align(logits, frames, columns.Length, 0, ids.ToArray());
        if (spans.Length != ids.Count) return new List<LyricWord>();

        // 60 ms 一帧，前面有 4 个 query 帧。
        const double frameMs = 60.0;
        const double lead = 4;
        var charTimes = new long[charOffsets.Count];
        for (int i = 0; i < charOffsets.Count; i++)
            charTimes[i] = startSample / 16 + (long)Math.Round((spans[i].StartFrame - lead) * frameMs);

        FitToLine(charTimes, line);

        // 把字时间摊回 karaoke 簇（CJK 一字一簇，拉丁按词）。
        var starts = new long[clusters.Count];
        for (int c = 0; c < clusters.Count; c++)
        {
            var (_, offset, _, _) = clusters[c];
            long start = line.StartMs;
            for (int i = 0; i < charOffsets.Count; i++)
            {
                if (charOffsets[i] >= offset) { start = charTimes[i]; break; }
            }
            starts[c] = start;
        }

        var words = new List<LyricWord>(clusters.Count);
        for (int c = 0; c < clusters.Count; c++)
        {
            var (text, offset, length, _) = clusters[c];
            long end;
            if (c + 1 < clusters.Count && starts[c + 1] > starts[c])
            {
                end = Math.Min(starts[c + 1], starts[c] + 1200);
            }
            else
            {
                // 行尾字：不要用 line.EndMs —— 行标签到下一句之间常常夹着间奏，
                // 那样会造出十几秒一个字的假时长（实测出现过 13.5 s）。
                long typical = c > 0 ? Math.Max(120, words[c - 1].EndMs - words[c - 1].StartMs) : 300;
                end = starts[c] + Math.Min(typical, 800);
                if (line.EndMs > starts[c]) end = Math.Min(end, Math.Max(starts[c] + 80, line.EndMs - 60));
            }
            if (end - starts[c] < 80) end = starts[c] + 80;
            words.Add(new LyricWord
            {
                Text = text,
                StartMs = starts[c],
                EndMs = end,
                StartOffset = offset,
                EndOffset = offset + length,
            });
        }
        return words;
    }

    /// <summary>
    /// CTC 的峰落在元音上，比字头晚几十毫秒；唱长音或间奏里，整行还会整体后移，最后几个字
    /// 常常拖到"下一行已经切过去"之后 —— 观感就是跟不上、动画没跑完就换行。这里做四件事：
    /// 提前一个固定量、去掉单字跳点、强制单调、把整行装回 [行首, 行尾] 窗口。
    /// </summary>
    private static void FitToLine(long[] times, LyricLine line)
    {
        // QA：DARIUS_ALIGN_NOFIT=1 关掉全部后处理，用来分别量"原始 Viterbi"和"后处理"的账。
        if (Environment.GetEnvironmentVariable("DARIUS_ALIGN_NOFIT") == "1") return;
        int n = times.Length;
        if (n == 0) return;

        const int lead = 40;
        for (int i = 0; i < n; i++) times[i] -= lead;

        if (n >= 5)
        {
            var copy = (long[])times.Clone();
            for (int i = 1; i < n - 1; i++)
                times[i] = Median3(copy[i - 1], copy[i], copy[i + 1]);
        }

        for (int i = 1; i < n; i++)
            if (times[i] < times[i - 1] + 50) times[i] = times[i - 1] + 50;

        long start = line.StartMs;
        // 最后一个字不只要"开始"在换行前，它的高亮要**跑完**。留出这行平均字长（120~400 ms）
        // 再加 60 ms 余量 —— 否则语速快的地方，最后一个字刚亮就被下一行切掉（实测差 ~1.5 个字）。
        long avg = n > 1 ? Math.Max(120, Math.Min(400, (times[n - 1] - times[0]) / (n - 1))) : 300;
        long limit = Math.Max(start + 200, line.EndMs) - avg - 60;
        long span = times[n - 1] - times[0];
        long room = limit - start;

        if (times[n - 1] > limit && span > 0 && room > 0)
        {
            long shift = limit - times[n - 1];
            if (times[0] + shift >= start)
            {
                for (int i = 0; i < n; i++) times[i] += shift;      // 整体前移，保持内部节奏
            }
            else
            {
                double scale = (double)room / span;                 // 前移会撞行首，才轻度压缩
                for (int i = 0; i < n; i++) times[i] = start + (long)Math.Round((times[i] - times[0]) * scale);
            }
        }

        if (times[0] < start)
        {
            long shift = start - times[0];
            for (int i = 0; i < n; i++) times[i] += shift;
        }
    }

    private static long Median3(long a, long b, long c)
    {
        if (a > b) (a, b) = (b, a);
        if (b > c) (b, c) = (c, b);
        return a > b ? a : b;
    }

    private static SenseVoiceModel? EnsureModel()
    {
        lock (Gate)
        {
            if (_model != null) return _model;
            if (_sessionBroken) return null;
            try
            {
                _model = SenseVoiceModel.Load(ModelPath, TokensPath, 0, gpuOnly: true);
                LogRaw($"model loaded ({_model.LoadMs}ms, {ModelTag()})");
                return _model;
            }
            catch (Exception strict)
            {
                // 严格模式被 784 个 shape 算子挡住，退到"允许回退"；声学计算仍然全在 GPU 上。
                try
                {
                    _model = SenseVoiceModel.Load(ModelPath, TokensPath, 0, gpuOnly: false);
                    LogRaw("model loaded with shape ops on CPU: " + strict.Message);
                    return _model;
                }
                catch (Exception ex)
                {
                    _sessionBroken = true;
                    WriteDisabled("session: " + ex.GetType().Name + " " + ex.Message);
                    LogRaw("model failed: " + ex.Message);
                    return null;
                }
            }
        }
    }

    private static string ModelTag()
    {
        try
        {
            var info = new FileInfo(ModelPath);
            return info.Exists ? $"{info.Length}-{info.LastWriteTimeUtc.Ticks}" : "none";
        }
        catch { return "none"; }
    }

    private static string CachePath(long songId) => Path.Combine(AppPaths.DataDir, "lyrics", songId + ".align.json");

    private static void SaveCache(long songId, LyricDocument doc)
    {
        try
        {
            var cache = new CacheFile { V = CacheVersion, Id = songId, Model = ModelTag() };
            for (int i = 0; i < doc.Lines.Count; i++)
            {
                var line = doc.Lines[i];
                // 只存真正对齐过的行；LrcParser 预填的"均分"字（WordsEstimated）不写进缓存。
                if (!line.HasWordTiming || line.Words.Count < 2) continue;
                cache.Lines.Add(new CacheLine
                {
                    I = i,
                    S = line.StartMs,
                    T = line.Text,
                    W = line.Words.Select(w => new CacheWord
                    {
                        T = w.Text, S = w.StartMs, E = w.EndMs, O = w.StartOffset,
                    }).ToList(),
                });
            }
            var path = CachePath(songId);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(cache), new UTF8Encoding(false));
        }
        catch { }
    }

    private static LyricDocument Clone(LyricDocument source)
    {
        var doc = new LyricDocument { Title = source.Title, Artist = source.Artist, OffsetMs = source.OffsetMs };
        foreach (var line in source.Lines)
        {
            doc.Lines.Add(new LyricLine
            {
                StartMs = line.StartMs,
                EndMs = line.EndMs,
                Text = line.Text,
                Translation = line.Translation,
                Romanization = line.Romanization,
                TranslationOnly = line.TranslationOnly,
                Words = line.Words.Select(w => new LyricWord
                {
                    Text = w.Text, StartMs = w.StartMs, EndMs = w.EndMs,
                    StartOffset = w.StartOffset, EndOffset = w.EndOffset,
                }).ToList(),
                WordsEstimated = line.WordsEstimated,
            });
        }
        return doc;
    }

    private static void WriteDisabled(string reason)
    {
        try { File.WriteAllText(DisabledMarker, DateTime.Now.ToString("s") + " " + reason, Encoding.UTF8); }
        catch { }
    }

    private static void Log(long songId, Song song, int lines, int chars, long elapsedMs, string outcome)
    {
        LogRaw($"{DateTime.Now:MM-dd HH:mm:ss} id={songId} '{song.DisplayTitle}' " +
               $"-> lines={lines} words={chars} ({elapsedMs}ms) {outcome}");
    }

    private static void LogRaw(string message)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataDir);
            File.AppendAllText(LogPath, message + Environment.NewLine, Encoding.UTF8);
        }
        catch { }
    }

    private sealed class CacheFile
    {
        public int V { get; set; }
        public long Id { get; set; }
        public string Model { get; set; } = "";
        public List<CacheLine> Lines { get; set; } = new();
    }

    private sealed class CacheLine
    {
        public int I { get; set; }
        public long S { get; set; }
        public string T { get; set; } = "";
        public List<CacheWord> W { get; set; } = new();
    }

    private sealed class CacheWord
    {
        public string T { get; set; } = "";
        public long S { get; set; }
        public long E { get; set; }
        public int O { get; set; }
    }
}
