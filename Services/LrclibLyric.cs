using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using CurrentLyrics.Models;

namespace CurrentLyrics.Services;

/// <summary>
/// LRCLIB（lrclib.net）：社区歌词库，免注册、免 key，曲库以海外为主（日语／英语／韩语）。
/// 一部分记录带 <c>hasWordSync</c>：<c>/api/get/{id}</c> 会多返回一个 <c>lyricsfile</c>（YAML），
/// 其中 <c>lines[].words[]</c> 是带毫秒的<b>词段时间</b>。
///
/// 这是逐字阶梯的第四级。前三家（AMLL / 酷狗 KRC / 腾讯 QRC）全是中国平台，《打上花火》实测
/// <c>amll- kugou- qq-</c> 三家全灭，而 LRCLIB 上这首歌有 16 条带词级的记录 —— 补的正是这块。
///
/// 词粒度比 KRC 粗（按短语切，一行 2～4 段），所以排在 QRC 之后；拿到之后把每段时间按字符摊开，
/// 界面上的逐字动画和单字源视觉一致。英文词不拆（逐字母高亮很奇怪），CJK 词才拆成单字。
/// </summary>
public static class LrclibLyric
{
    private const string ApiBase = "https://lrclib.net/api/";
    private const string UserAgent = "DariusMusic/1.2.66 (word-level lyric fallback; lrclib.net)";

    private static readonly HttpClient Http = CreateHttp();

    /// <summary>Songs LRCLIB has nothing usable for, so a replay does not search again.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<long, byte> Misses = new();

    private static HttpClient CreateHttp()
    {
        var client = new HttpClient(new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
        })
        {
            Timeout = TimeSpan.FromSeconds(15),
        };
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UserAgent);
        return client;
    }

    private static string CacheDir => Path.Combine(AppPaths.DataDir, "lyrics");

    private static void Trace(string message)
    {
        try
        {
            Directory.CreateDirectory(CacheDir);
            File.AppendAllText(Path.Combine(CacheDir, "lrclib.log"),
                $"{DateTime.Now:HH:mm:ss} {message}{Environment.NewLine}");
        }
        catch { }
    }

    // ---------- public ----------

    public static async Task<LyricDocument?> EnsureAsync(long songId, string title, string artist,
        int expectedSeconds, LyricDocument? baseDoc, TimeSpan? budget = null)
    {
        var cached = TryCached(songId, baseDoc);
        if (cached != null) return cached;
        if (songId <= 0 || string.IsNullOrWhiteSpace(title) || Misses.ContainsKey(songId)) return null;

        var work = Task.Run(() => Fetch(songId, title, artist, expectedSeconds, baseDoc));
        var finished = await Task.WhenAny(work, Task.Delay(budget ?? TimeSpan.FromSeconds(12)))
            .ConfigureAwait(false);
        if (finished != work) return null;
        var result = await work.ConfigureAwait(false);
        if (result == null)
        {
            Misses.TryAdd(songId, 0);
            return null;
        }
        QqLyric.CarryTranslations(result, baseDoc);
        return result;
    }

    /// <summary>Disk only — the lyricsfile downloaded for this song earlier, if any.</summary>
    public static LyricDocument? TryCached(long songId, LyricDocument? baseDoc)
    {
        if (songId <= 0) return null;
        var file = Path.Combine(CacheDir, songId + ".lrclib");
        try
        {
            if (!File.Exists(file)) return null;
            var raw = File.ReadAllText(file, Encoding.UTF8);
            var doc = Parse(raw);
            if (doc == null) return null;
            if (!AgreesWith(doc, baseDoc))
            {
                try { File.Delete(file); } catch { }
                return null;
            }
            QqLyric.CarryTranslations(doc, baseDoc);
            return doc;
        }
        catch
        {
            return null;
        }
    }

    // ---------- network ----------

    private sealed record Hit(long Id, string Track, string Artist, double Duration,
        bool WordSync, bool Instrumental);

    internal static LyricDocument? Fetch(long songId, string title, string artist, int expectedSeconds,
        LyricDocument? baseDoc)
    {
        var hits = Search(title, artist);
        if (hits.Count == 0) return null;

        // 只要带词级的记录（没词级的行级时间不如网易云那份，还少翻译）。
        var ranked = hits
            .Where(h => h.WordSync && !h.Instrumental && h.Id > 0)
            .OrderBy(h => expectedSeconds > 0 ? Math.Abs(h.Duration - expectedSeconds) : 0)
            .Take(4)
            .ToList();
        if (ranked.Count == 0) return null;

        foreach (var hit in ranked)
        {
            // 时长差太远就是另一个版本（伴奏／Live／翻唱），直接跳过。
            if (expectedSeconds > 0 && Math.Abs(hit.Duration - expectedSeconds) > 25) continue;
            var pair = FetchFile(hit.Id);
            if (pair == null) continue;
            var (doc, raw) = pair.Value;
            if (doc == null) continue;
            if (!AgreesWith(doc, baseDoc)) continue;

            var shift = QqLyric.EstimateShift(doc, baseDoc);
            if (shift != 0) QqLyric.ApplyShift(doc, shift);
            if (songId > 0)
            {
                var target = Path.Combine(CacheDir, songId + ".lrclib");
                try
                {
                    Directory.CreateDirectory(CacheDir);
                    File.WriteAllText(target, raw, Encoding.UTF8);
                }
                catch (Exception e)
                {
                    // 只记失败：写盘失败会让每次播放都重新联网，值得留一行线索。
                    Trace($"write {target} failed {e.GetType().Name} {e.Message}");
                }
            }
            return doc;
        }
        return null;
    }

    /// <summary>
    /// Last resort for a track NetEase has no text for at all: LRCLIB's <c>syncedLyrics</c> is plain LRC.
    /// No per-word timing, but a scrollable lyric beats an empty panel.
    /// </summary>
    public static async Task<LyricDocument?> EnsurePlainAsync(long songId, string title, string artist,
        int expectedSeconds, TimeSpan? budget = null)
    {
        if (songId <= 0 || string.IsNullOrWhiteSpace(title)) return null;
        var file = Path.Combine(CacheDir, songId + ".lrclib.lrc");
        try
        {
            if (File.Exists(file))
            {
                var cached = LrcParser.Parse(File.ReadAllText(file, Encoding.UTF8));
                if (cached.Lines.Count > 0) return cached;
            }
        }
        catch { }

        var work = Task.Run(() =>
        {
            try
            {
                foreach (var hit in Search(title, artist)
                             .Where(h => !h.Instrumental)
                             .OrderBy(h => expectedSeconds > 0
                                 ? Math.Abs(h.Duration - expectedSeconds)
                                 : 0)
                             .Take(3))
                {
                    var lrc = SyncedLyrics(hit.Id);
                    if (string.IsNullOrWhiteSpace(lrc)) continue;
                    var doc = LrcParser.Parse(lrc);
                    if (doc.Lines.Count < 3) continue;
                    try
                    {
                        Directory.CreateDirectory(CacheDir);
                        File.WriteAllText(file, lrc, Encoding.UTF8);
                    }
                    catch { }
                    return doc;
                }
            }
            catch { }
            return (LyricDocument?)null;
        });

        var finished = await Task.WhenAny(work, Task.Delay(budget ?? TimeSpan.FromSeconds(10)))
            .ConfigureAwait(false);
        return finished == work ? await work.ConfigureAwait(false) : null;
    }

    private static string SyncedLyrics(long id)
    {
        try
        {
            var text = Http.GetStringAsync(ApiBase + "get/" + id.ToString(CultureInfo.InvariantCulture))
                .GetAwaiter().GetResult();
            if (string.IsNullOrWhiteSpace(text)) return "";
            return JsonNode.Parse(text) is JsonObject root ? Text(root, "syncedLyrics") : "";
        }
        catch
        {
            return "";
        }
    }

    private static List<Hit> Search(string title, string artist)
    {
        var hits = new List<Hit>();
        var seen = new HashSet<long>();
        foreach (var url in SearchUrls(title, artist))
        {
            try
            {
                var text = Http.GetStringAsync(url).GetAwaiter().GetResult();
                if (string.IsNullOrWhiteSpace(text)) continue;
                if (JsonNode.Parse(text) is not JsonArray list) continue;
                foreach (var node in list)
                {
                    if (node is not JsonObject item) continue;
                    long id = Integer(item, "id");
                    if (id <= 0 || !seen.Add(id)) continue;
                    hits.Add(new Hit(
                        id,
                        Text(item, "trackName"),
                        Text(item, "artistName"),
                        Number(item, "duration"),
                        TryFlag(item, "hasWordSync"),
                        TryFlag(item, "instrumental")));
                }
            }
            catch { }
            if (hits.Count >= 20) break;
        }
        return hits;
    }

    /// <summary>几条查询串依次试：先精确，再退化到全文搜索，最后用未清洗的标题。</summary>
    private static IEnumerable<string> SearchUrls(string title, string artist)
    {
        var clean = CleanTitle(title);
        var singer = CleanArtist(artist);
        if (singer.Length > 0)
        {
            yield return ApiBase + "search?track_name=" + Uri.EscapeDataString(clean) +
                         "&artist_name=" + Uri.EscapeDataString(singer);
            yield return ApiBase + "search?q=" + Uri.EscapeDataString(clean + " " + singer);
        }
        yield return ApiBase + "search?q=" + Uri.EscapeDataString(clean);
        if (!string.Equals(clean, title.Trim(), StringComparison.Ordinal))
            yield return ApiBase + "search?q=" + Uri.EscapeDataString(title.Trim());
    }

    /// <summary>JsonNode.ToString() emits JSON text (bool as true, strings with quotes), so every
    /// field is read through GetValue&lt;T&gt;(). Comparing hasWordSync's text against "True" made
    /// every hit look like it had no word timing.</summary>
    private static bool TryFlag(JsonObject item, string name)
    {
        try { return item[name] is JsonValue value && value.GetValue<bool>(); }
        catch { return false; }
    }

    private static string Text(JsonObject item, string name)
    {
        try { return item[name] is JsonValue value ? value.GetValue<string>() ?? "" : ""; }
        catch { return ""; }
    }

    private static double Number(JsonObject item, string name)
    {
        try { return item[name] is JsonValue value ? value.GetValue<double>() : 0; }
        catch { return 0; }
    }

    private static long Integer(JsonObject item, string name)
    {
        try { return item[name] is JsonValue value ? value.GetValue<long>() : 0; }
        catch { return 0; }
    }

    private static (LyricDocument? Doc, string Raw)? FetchFile(long id)
    {
        try
        {
            var text = Http.GetStringAsync(ApiBase + "get/" + id.ToString(CultureInfo.InvariantCulture))
                .GetAwaiter().GetResult();
            if (string.IsNullOrWhiteSpace(text)) return null;
            var root = JsonNode.Parse(text) as JsonObject;
            var raw = root == null ? "" : Text(root, "lyricsfile");
            if (string.IsNullOrWhiteSpace(raw)) return null;
            return (Parse(raw), raw);
        }
        catch
        {
            return null;
        }
    }

    // ---------- YAML (the `lyricsfile` field) ----------

    /// <summary>
    /// Reads the subset of YAML that LRCLIB's <c>lyricsfile</c> uses:
    /// <code>
    /// lines:
    /// - text: full line
    ///   start_ms: 19780
    ///   end_ms: 28649
    ///   words:
    ///   - text: 'word '
    ///     start_ms: 19780
    /// </code>
    /// Everything before <c>lines:</c> and after <c>plain:</c> is ignored.
    /// </summary>
    public static LyricDocument? Parse(string yaml)
    {
        if (string.IsNullOrWhiteSpace(yaml)) return null;

        var raw = new List<LyricLine>();
        LyricLine? line = null;
        LyricWord? word = null;
        bool inLines = false;

        foreach (var source in yaml.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var text = source.TrimEnd();
            if (text.Length == 0) continue;
            if (!inLines)
            {
                if (text.Trim() == "lines:") inLines = true;
                continue;
            }
            if (text.StartsWith("plain:", StringComparison.Ordinal)) break;

            int indent = text.Length - text.TrimStart().Length;
            var body = text.TrimStart();

            if (indent == 0)
            {
                if (!body.StartsWith("- ", StringComparison.Ordinal)) continue;
                line = new LyricLine { StartMs = -1, EndMs = -1 };
                raw.Add(line);
                word = null;
                ApplyLine(line, body[2..]);
                continue;
            }
            if (line == null) continue;
            if (body == "words:")
            {
                word = null;
                continue;
            }
            if (body.StartsWith("- ", StringComparison.Ordinal))
            {
                word = new LyricWord { StartMs = -1, EndMs = -1 };
                line.Words.Add(word);
                ApplyWord(word, body[2..]);
                continue;
            }
            if (word != null) ApplyWord(word, body);
            else ApplyLine(line, body);
        }

        if (raw.Count == 0) return null;

        var lines = new List<LyricLine>();
        foreach (var source in raw)
        {
            if (source.StartMs < 0) continue;
            source.Words.RemoveAll(w => w.StartMs < 0 || w.Text.Length == 0);

            if (source.Words.Count == 0)
            {
                var plain = (source.Text ?? "").Trim();
                if (plain.Length == 0) continue;
                source.Text = plain;
                if (source.EndMs <= source.StartMs) source.EndMs = source.StartMs + 4000;
                lines.Add(source);
                continue;
            }

            if (string.IsNullOrEmpty(source.Text)) source.Text = string.Concat(source.Words.Select(w => w.Text));
            LocateWords(source);
            FillWordEnds(source);
            source.Words = SplitChars(source);
            if (source.EndMs <= source.StartMs) source.EndMs = Math.Max(source.Words[^1].EndMs, source.StartMs + 400);
            lines.Add(source);
        }

        if (lines.Count == 0) return null;
        if (lines.Count(l => l.HasWordTiming) < 3) return null;   // 太碎的记录不如不用
        lines.Sort((a, b) => a.StartMs.CompareTo(b.StartMs));
        return new LyricDocument { Lines = lines };
    }

    /// <summary>给每个词在整行文本里定位字符区间，用于高亮推算。</summary>
    private static void LocateWords(LyricLine line)
    {
        int cursor = 0;
        foreach (var item in line.Words)
        {
            int at = cursor < line.Text.Length
                ? line.Text.IndexOf(item.Text, cursor, StringComparison.Ordinal)
                : -1;
            if (at < 0) at = Math.Min(cursor, line.Text.Length);
            item.StartOffset = at;
            item.EndOffset = Math.Min(line.Text.Length, at + item.Text.Length);
            cursor = item.EndOffset;
        }
    }

    /// <summary>LRCLIB 只给每个词的起点，终点取下一个词的起点；最后一个词用行尾。</summary>
    private static void FillWordEnds(LyricLine line)
    {
        long lineEnd = line.EndMs > line.StartMs ? line.EndMs : line.Words[^1].StartMs + 800;
        for (int i = 0; i < line.Words.Count; i++)
        {
            var item = line.Words[i];
            long next = i + 1 < line.Words.Count ? line.Words[i + 1].StartMs : lineEnd;
            if (item.EndMs <= item.StartMs) item.EndMs = next > item.StartMs ? next : item.StartMs + 120;
            if (item.EndMs < item.StartMs) item.EndMs = item.StartMs;
        }
    }

    /// <summary>
    /// 一句话被切成 2～4 段时，段内再按字符摊平：日文/中文能出真正的逐字动画。
    /// 英文不拆 —— 逐字母高亮像打字机，单词级才是对的。
    /// </summary>
    private static List<LyricWord> SplitChars(LyricLine line)
    {
        var result = new List<LyricWord>(line.Words.Count * 4);
        foreach (var item in line.Words)
        {
            var elements = TextElements(item.Text);
            if (elements.Count <= 1 || item.EndMs <= item.StartMs || !HasCjk(item.Text))
            {
                result.Add(item);
                continue;
            }
            long span = item.EndMs - item.StartMs;
            int offset = item.StartOffset;
            for (int i = 0; i < elements.Count; i++)
            {
                long start = item.StartMs + span * i / elements.Count;
                long end = item.StartMs + span * (i + 1) / elements.Count;
                result.Add(new LyricWord
                {
                    Text = elements[i],
                    StartMs = start,
                    EndMs = end,
                    StartOffset = offset,
                    EndOffset = Math.Min(line.Text.Length, offset + elements[i].Length),
                });
                offset += elements[i].Length;
            }
        }
        return result;
    }

    private static bool HasCjk(string text)
    {
        foreach (var c in text)
        {
            if (c >= 0x3040 && c <= 0x30FF) return true;   // 假名
            if (c >= 0x3400 && c <= 0x9FFF) return true;   // 汉字
            if (c >= 0xAC00 && c <= 0xD7AF) return true;   // 谚文
            if (c >= 0xF900 && c <= 0xFAFF) return true;   // 兼容汉字
        }
        return false;
    }

    private static List<string> TextElements(string text)
    {
        var list = new List<string>(text.Length);
        var walker = StringInfo.GetTextElementEnumerator(text);
        while (walker.MoveNext()) list.Add((string)walker.Current!);
        return list;
    }

    private static void ApplyLine(LyricLine line, string body)
    {
        var (key, value) = Split(body);
        switch (key)
        {
            case "text": line.Text = Unquote(value); break;
            case "start_ms": line.StartMs = ParseLong(value); break;
            case "end_ms": line.EndMs = ParseLong(value); break;
        }
    }

    private static void ApplyWord(LyricWord word, string body)
    {
        var (key, value) = Split(body);
        switch (key)
        {
            case "text": word.Text = Unquote(value); break;
            case "start_ms": word.StartMs = ParseLong(value); break;
            case "end_ms": word.EndMs = ParseLong(value); break;
        }
    }

    /// <summary>只认已知字段名，这样 <c>text: 7:30の...</c> 里的冒号不会被当成键的分隔。</summary>
    private static (string Key, string Value) Split(string body)
    {
        int colon = body.IndexOf(':');
        if (colon <= 0) return (body.Trim(), "");
        var key = body[..colon].Trim();
        return key is "text" or "start_ms" or "end_ms" or "words"
            ? (key, body[(colon + 1)..].Trim())
            : (body.Trim(), "");
    }

    private static string Unquote(string value)
    {
        if (value.Length >= 2 && value[0] == '\'' && value[^1] == '\'')
            return value[1..^1].Replace("''", "'");
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
        {
            var builder = new StringBuilder(value.Length);
            for (int i = 1; i < value.Length - 1; i++)
            {
                char c = value[i];
                if (c == '\\' && i + 1 < value.Length - 1)
                {
                    char next = value[++i];
                    builder.Append(next switch
                    {
                        'n' => '\n',
                        't' => '\t',
                        '"' => '"',
                        '\\' => '\\',
                        _ => next,
                    });
                }
                else builder.Append(c);
            }
            return builder.ToString();
        }
        return value;
    }

    private static long ParseLong(string value) =>
        long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : -1;

    // ---------- identity guard ----------

    /// <summary>
    /// 和 <see cref="QqLyric.AgreesWith"/> 同一套判定（先比整行，过不了再退到二元组覆盖率）。
    /// LRCLIB 和网易云几乎从不按同一处断句：网易云是「あの日見渡した渚を／今も思い出すんだ」两行，
    /// LRCLIB 合成一行，只比整行的话会把一份完全正确的逐字歌词当成「另一首歌」扔掉。
    /// </summary>
    private static bool AgreesWith(LyricDocument candidate, LyricDocument? baseDoc) =>
        QqLyric.AgreesWith(candidate, baseDoc);

    // ---------- title / artist cleanup ----------

    /// <summary>「打上花火 (Movie Ver.)」→「打上花火」：只裁末尾的括号块，标题中间的不动。</summary>
    internal static string CleanTitle(string title)
    {
        var text = (title ?? "").Trim();
        foreach (var pair in new[] { ("（", "）"), ("(", ")"), ("【", "】"), ("[", "]") })
        {
            int open = text.IndexOf(pair.Item1, StringComparison.Ordinal);
            if (open > 0 && text.EndsWith(pair.Item2, StringComparison.Ordinal))
                text = text[..open].Trim();
        }
        foreach (var tag in new[] { " feat", " FEAT", " ft.", " Feat" })
        {
            int at = text.IndexOf(tag, StringComparison.Ordinal);
            if (at > 0) text = text[..at].Trim();
        }
        return text;
    }

    /// <summary>「DAOKO / 米津玄師」→「DAOKO」：多歌手用分隔符切开，取第一个。</summary>
    internal static string CleanArtist(string artist)
    {
        var text = (artist ?? "").Trim();
        foreach (var separator in new[] { "/", "、", ",", "，", "&", ";", "；" })
        {
            int at = text.IndexOf(separator, StringComparison.Ordinal);
            if (at > 0) text = text[..at].Trim();
        }
        return text;
    }
}