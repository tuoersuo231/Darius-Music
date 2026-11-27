using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CurrentLyrics.Models;

namespace CurrentLyrics.Services;

/// <summary>
/// 第二歌词源（腾讯 QRC）：网易云的 <c>yrc</c> 只有一部分歌有，没有的歌以前只能把整句时长均分，
/// 逐字动画就是匀速的。QRC 里每个字都带 <c>(开始,时长)</c>，跟演唱走，所以拿它补。
///
/// 只有一件事要判断好：**同一首歌要匹配对**。按「歌名 歌手」搜，歌名相同再比歌手，
/// 分不够就不用（宁可匀速，也不显示别的歌的词）。
/// </summary>
public static class QqLyric
{
    private const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/124.0.0.0 Safari/537.36";

    private static readonly HttpClient Http = CreateHttp();
    private static readonly Regex LineTag = new(@"^\[(\d+),(\d+)\](.*)$", RegexOptions.Compiled);
    private static readonly Regex WordTag = new(@"\((\d+),(\d+)\)", RegexOptions.Compiled);
    private static readonly Regex MetaTag = new(@"^\[[A-Za-z]+:", RegexOptions.Compiled);
    private static readonly Regex Cdata = new(
        @"<(?<tag>[a-z]+)[^>]*>\s*<!\[CDATA\[(?<data>.*?)\]\]>", RegexOptions.Singleline | RegexOptions.Compiled);

    public sealed record Hit(long Id, string Mid, string Name, string Singer, string Interval);

    /// <summary>One candidate's decoded and parsed result, plus the raw payload for the cache.</summary>
    public sealed record Payload(LyricDocument Doc, string Lyric, string Translation, Hit Hit);

    private static HttpClient CreateHttp()
    {
        var client = new HttpClient(new HttpClientHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.All,
        })
        {
            Timeout = TimeSpan.FromSeconds(15),
        };
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UserAgent);
        client.DefaultRequestHeaders.TryAddWithoutValidation("Referer", "https://y.qq.com/");
        client.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
        return client;
    }

    private static string CacheDir => System.IO.Path.Combine(AppPaths.DataDir, "lyrics");

    // ---------- cache ----------

    /// <summary>
    /// Returns a word-timed document for this song, from cache when possible. <paramref name="baseDoc"/>
    /// (the NetEase one) is only used to carry translations over: QRC usually has none for
    /// translated songs, while the NetEase response does.
    /// </summary>
    public static async Task<LyricDocument?> EnsureAsync(long songId, string title, string artist,
        LyricDocument? baseDoc, int expectedSeconds = 0, TimeSpan? budget = null)
    {
        var cached = TryCached(songId, baseDoc);
        if (cached != null) return cached;

        // The search + lyric round trip runs on its own task so a slow (or blocked) Tencent endpoint
        // cannot hold the lyrics back: after the budget the NetEase text is shown and this task keeps
        // going in the background, writing the cache for the next play.
        var work = Task.Run(() =>
        {
            try
            {
                var payload = Fetch(title, artist, expectedSeconds, baseDoc);
                if (payload == null) return null;
                var doc = payload.Doc;
                // The shift is measured once against the NetEase timeline and stored, so a later load
                // (even without the base document at hand) puts the words back in the same place.
                long shift = EstimateShift(doc, baseDoc);
                if (shift != 0) ApplyShift(doc, shift);
                Directory.CreateDirectory(CacheDir);
                File.WriteAllText(System.IO.Path.Combine(CacheDir, songId + ".qrc"), payload.Lyric);
                if (payload.Translation.Length > 0)
                    File.WriteAllText(System.IO.Path.Combine(CacheDir, songId + ".qrc.ts"), payload.Translation);
                File.WriteAllText(System.IO.Path.Combine(CacheDir, songId + ".qrc.shift"),
                    shift.ToString(CultureInfo.InvariantCulture));
                return doc;
            }
            catch
            {
                return null;
            }
        });

        var finished = await Task.WhenAny(work, Task.Delay(budget ?? TimeSpan.FromSeconds(6)))
            .ConfigureAwait(false);
        if (finished != work) return null;
        var result = await work.ConfigureAwait(false);
        if (result != null) CarryTranslations(result, baseDoc);
        return result;
    }

    /// <summary>Disk only — no request. Null when this song has no cached QRC.</summary>
    public static LyricDocument? TryCached(long songId, LyricDocument? baseDoc)
    {
        var qrcFile = System.IO.Path.Combine(CacheDir, songId + ".qrc");
        var tsFile = System.IO.Path.Combine(CacheDir, songId + ".qrc.ts");
        try
        {
            if (!File.Exists(qrcFile)) return null;
            var doc = Build(File.ReadAllText(qrcFile), ReadText(tsFile));
            if (doc == null) return null;
            // A cache written before the "same lyric?" gate existed can hold another language's words;
            // re-check it here and drop it so the next play fetches a valid one.
            if (baseDoc != null && !AgreesWith(doc, baseDoc))
            {
                TryDelete(qrcFile);
                TryDelete(tsFile);
                TryDelete(System.IO.Path.Combine(CacheDir, songId + ".qrc.shift"));
                return null;
            }
            var shiftText = ReadText(System.IO.Path.Combine(CacheDir, songId + ".qrc.shift"));
            if (long.TryParse(shiftText.Trim(), out var shift)) ApplyShift(doc, shift);
            else ApplyShift(doc, EstimateShift(doc, baseDoc));
            CarryTranslations(doc, baseDoc);
            return doc;
        }
        catch
        {
            return null;
        }
    }

    private static string ReadText(string path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path) : ""; }
        catch { return ""; }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { }
    }

    /// <summary>QRC lines carry no translation; when a line's time matches a translated NetEase line,
    /// borrow that text so the overlay keeps showing it.</summary>
    internal static void CarryTranslations(LyricDocument? target, LyricDocument? baseDoc)    {
        if (target == null || baseDoc == null || baseDoc.Lines.Count == 0) return;
        if (target.Lines.Any(l => l.Translation.Length > 0)) return;
        int cursor = 0;
        foreach (var line in target.Lines)
        {
            while (cursor < baseDoc.Lines.Count && baseDoc.Lines[cursor].StartMs < line.StartMs - 320) cursor++;
            for (int i = Math.Max(0, cursor - 1); i < Math.Min(baseDoc.Lines.Count, cursor + 4); i++)
            {
                var candidate = baseDoc.Lines[i];
                if (Math.Abs(candidate.StartMs - line.StartMs) > 320) continue;
                if (candidate.Translation.Length == 0) continue;
                line.Translation = candidate.Translation;
                break;
            }
        }
    }

    // ---------- aligning to the audio that is actually playing ----------

    /// <summary>
    /// The lyric comes from Tencent's master, the audio from NetEase's. Same song, but the two labels
    /// trim silence differently, so QRC times can sit a few hundred milliseconds away from the local
    /// LRC's. Estimate that constant shift from the lines whose text is unique on both sides (repeated
    /// chorus lines are ambiguous) and move the QRC times onto the NetEase timeline.
    ///
    /// A shift is only applied when the estimate is trustworthy: enough pairs and a tight cluster.
    /// Returns the shift that was applied (0 when none).
    /// </summary>
    /// <summary>How far the candidate's timeline sits from the NetEase one, and how sure that estimate is.</summary>
    public readonly record struct Shift(long Offset, int Pairs, double TightShare, long Iqr);

    internal static long AlignToBase(LyricDocument doc, LyricDocument? baseDoc)
    {
        var estimate = Estimate(doc, baseDoc);
        long shift = estimate.Iqr <= 450 && Math.Abs(estimate.Offset) >= 80 ? estimate.Offset : 0;
        if (shift != 0) ApplyShift(doc, shift);
        return shift;
    }

    /// <summary>Median offset of the text-matched lines, or 0 when the estimate is not trustworthy.</summary>
    internal static long EstimateShift(LyricDocument doc, LyricDocument? baseDoc)
    {
        var estimate = Estimate(doc, baseDoc);
        return estimate.Iqr <= 450 && Math.Abs(estimate.Offset) >= 80 ? estimate.Offset : 0;
    }

    /// <summary>
    /// Pairs the two documents by line text (only texts that occur exactly once on both sides —
    /// chorus lines repeat and would pair arbitrarily) and reports the median delta plus the share of
    /// pairs that agree with it. A low share means the two versions are arranged differently.
    /// </summary>
    public static Shift Estimate(LyricDocument doc, LyricDocument? baseDoc)
    {
        if (baseDoc == null || baseDoc.Lines.Count < 8) return new Shift(0, 0, 0, 0);

        var mine = UniqueLines(doc.Lines);
        var theirs = UniqueLines(baseDoc.Lines);
        var deltas = new List<long>();
        foreach (var (key, line) in mine)
            if (theirs.TryGetValue(key, out var other)) deltas.Add(line.StartMs - other.StartMs);
        if (deltas.Count < 8) return new Shift(0, deltas.Count, 0, 0);

        var sorted = deltas.OrderBy(d => d).ToList();
        long median = sorted[sorted.Count / 2];
        int tight = deltas.Count(d => Math.Abs(d - median) <= 150);
        long iqr = sorted[(int)(sorted.Count * 0.75)] - sorted[(int)(sorted.Count * 0.25)];
        return new Shift(median, deltas.Count, tight / (double)deltas.Count, iqr);
    }

    internal static void ApplyShift(LyricDocument doc, long shift)
    {
        if (shift == 0) return;
        foreach (var line in doc.Lines)
        {
            line.StartMs = Math.Max(0, line.StartMs - shift);
            line.EndMs = Math.Max(line.StartMs, line.EndMs - shift);
            foreach (var word in line.Words)
            {
                word.StartMs = Math.Max(0, word.StartMs - shift);
                word.EndMs = Math.Max(word.StartMs, word.EndMs - shift);
            }
        }
    }

    /// <summary>Line text → line, keeping only texts that occur exactly once (length 4+).</summary>
    private static Dictionary<string, LyricLine> UniqueLines(List<LyricLine> lines)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var map = new Dictionary<string, LyricLine>(StringComparer.Ordinal);
        foreach (var line in lines)
        {
            var key = TextKey(line.Text);
            if (key.Length < 4) continue;
            counts[key] = counts.GetValueOrDefault(key) + 1;
            map[key] = line;
        }
        foreach (var (key, count) in counts)
            if (count != 1) map.Remove(key);
        return map;
    }

    private static string TextKey(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var ch in text)
            if (!char.IsWhiteSpace(ch) && !char.IsPunctuation(ch) && !char.IsSymbol(ch))
                builder.Append(ch);
        return builder.ToString();
    }

    // ---------- network ----------

    /// <summary>
    /// Search "title artist" and return the best candidate whose lyric is plausibly the same song.
    /// <paramref name="baseDoc"/> is the NetEase document, when there is one: a candidate whose text
    /// shares nothing with it is a different recording — typically the same title in another language
    /// ("願い～あの頃のキミへ～ (MIREI ver.)" is the English version of the Japanese track) — and is
    /// rejected so the overlay never shows another language's words.
    /// </summary>
    public static Payload? Fetch(string title, string artist, int expectedSeconds = 0, LyricDocument? baseDoc = null)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;
        // "夜曲（Cover）" / "Lemon (Single Version)" search badly; the bracket text is not part of
        // what the other side indexes.
        var keyword = Brackets(title).Trim();
        if (keyword.Length == 0) keyword = title.Trim();

        // Two searches: the precise one, then the title alone. Ranked, not "first hit wins": an entry
        // with the right title but the wrong text must not stop a later candidate from being tried.
        var ranked = new List<Hit>();
        foreach (var hits in new[]
                 {
                     Search((keyword + " " + artist).Trim(), 10),
                     Search(keyword, 10),
                 })
        {
            foreach (var hit in Rank(hits, title, artist, expectedSeconds))
                if (ranked.All(h => h.Id != hit.Id)) ranked.Add(hit);
        }
        if (ranked.Count == 0) return null;

        foreach (var hit in ranked.Take(5))
        {
            var xml = LyricXml(hit.Id);
            var lyric = Decode(xml.GetValueOrDefault("content", ""));
            if (lyric.Length == 0) continue;
            var translation = Decode(xml.GetValueOrDefault("contentts", ""));
            var roma = Decode(xml.GetValueOrDefault("contentroma", ""));
            var doc = Build(lyric, translation.Length > 0 ? translation : roma);
            if (doc == null) continue;
            if (!AgreesWith(doc, baseDoc)) continue;
            return new Payload(doc, lyric, translation.Length > 0 ? translation : roma, hit);
        }
        return null;
    }

    /// <summary>Share of unique line texts the two documents have in common (0..1, -1 = unknown).</summary>
    internal static double LineOverlap(LyricDocument a, LyricDocument? b)
    {
        if (b == null) return -1;
        var mine = UniqueLines(a.Lines).Keys;
        var theirs = UniqueLines(b.Lines).Keys;
        if (mine.Count < 5 || theirs.Count < 5) return -1;
        var bigger = mine.Count >= theirs.Count ? mine : theirs;
        var smaller = mine.Count >= theirs.Count ? theirs : mine;
        int shared = 0;
        foreach (var key in smaller) if (bigger.Contains(key)) shared++;
        return shared / (double)smaller.Count;
    }

    /// <summary>True when the candidate is the same lyric as the NetEase one <em>and</em> sits on a
    /// similar timeline. Two independent signals:
    /// the share of unique lines the two documents have in common ("the English MIREI ver." of a
    /// Japanese track shares none), and the interquartile spread of line-start deltas (a live or
    /// re-arranged version scatters by whole seconds, a re-mastered one barely moves).</summary>
    internal static bool AgreesWith(LyricDocument candidate, LyricDocument? baseDoc)
    {
        double overlap = LineOverlap(candidate, baseDoc);
        if (overlap < 0) return true;                 // nothing to compare against
        if (overlap >= 0.75) return true;             // clearly the same words
        var shift = Estimate(candidate, baseDoc);
        return overlap >= 0.35 && shift.Pairs >= 8 && shift.Iqr <= 450;
    }

    public static List<Hit> Search(string keyword, int limit)
    {
        var url = "https://c.y.qq.com/soso/fcgi-bin/client_search_cp?format=json&cr=1&new_json=1" +
                  "&p=1&n=" + limit.ToString(CultureInfo.InvariantCulture) +
                  "&w=" + Uri.EscapeDataString(keyword);
        var hits = new List<Hit>();
        try
        {
            var text = Http.GetStringAsync(url).GetAwaiter().GetResult();
            var root = JsonNode.Parse(text);
            var list = root?["data"]?["song"]?["list"] as JsonArray;
            if (list == null) return hits;
            foreach (var node in list)
            {
                if (node is not JsonObject song) continue;
                long id = song["id"]?.GetValue<long>() ?? 0;
                if (id <= 0) id = long.TryParse(song["songid"]?.ToString(), out var parsed) ? parsed : 0;
                var name = song["title"]?.GetValue<string>() ?? song["name"]?.GetValue<string>() ?? "";
                var singers = string.Join("/", (song["singer"] as JsonArray ?? new JsonArray())
                    .Select(s => s?["name"]?.GetValue<string>() ?? "").Where(s => s.Length > 0));
                var interval = song["interval"]?.GetValue<int>() ?? 0;
                var intervalText = interval > 0
                    ? TimeSpan.FromSeconds(interval).ToString(@"mm\:ss")
                    : "";
                if (id > 0) hits.Add(new Hit(id, song["mid"]?.GetValue<string>() ?? "", name, singers, intervalText));
            }
        }
        catch { }
        return hits;
    }

    /// <summary>The raw <c>lrctype=4</c> response, split into its lyric / translation / romanisation CDATA.</summary>
    public static Dictionary<string, string> LyricXml(long songId)
    {
        var result = new Dictionary<string, string>();
        var url = "https://c.y.qq.com/qqmusic/fcgi-bin/lyric_download.fcg?version=15&miniversion=82" +
                  "&lrctype=4&musicid=" + songId.ToString(CultureInfo.InvariantCulture);
        try
        {
            var text = Http.GetStringAsync(url).GetAwaiter().GetResult();
            foreach (Match m in Cdata.Matches(text))
                result[m.Groups["tag"].Value] = m.Groups["data"].Value.Trim();
        }
        catch { }
        return result;
    }

    /// <summary>QRC is hex-encrypted and may come wrapped in a small XML envelope
    /// (<c>&lt;QrcInfos&gt;…&lt;Lyric_1 LyricContent="…"/&gt;…</c>); a plain payload passes through.</summary>
    private static string Decode(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        if (raw.Contains('[') || raw.Contains(':')) return Unwrap(raw);
        return Unwrap(QrcCodec.Decode(raw));
    }

    private static string Unwrap(string text)
    {
        if (text.Length == 0) return "";
        const string key = "LyricContent=\"";
        int start = text.IndexOf(key, StringComparison.Ordinal);
        if (start >= 0)
        {
            start += key.Length;
            int end = text.LastIndexOf("\"/>", StringComparison.Ordinal);
            text = end > start ? text[start..end] : text[start..];
        }
        if (!text.Contains('&')) return text;
        return text.Replace("&quot;", "\"")
            .Replace("&apos;", "'")
            .Replace("&lt;", "<")
            .Replace("&gt;", ">")
            .Replace("&amp;", "&");
    }

    // ---------- matching ----------

    private static readonly Regex BracketTail = new(@"[(（\[【].*?[)）\]】]", RegexOptions.Compiled);

    /// <summary>Removes bracketed qualifiers ("（Cover）", "(Live)") for the search term.</summary>
    private static string Brackets(string text) => BracketTail.Replace(text ?? "", " ");

    internal static string Normalize(string text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        text = BracketTail.Replace(text, "");
        var sb = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            if (char.IsWhiteSpace(ch)) continue;
            // 波浪线和各种横线一律忽略：酷狗写「願い〜あの頃のキミへ〜」（U+301C），网易云写「～」（U+FF5E），
            // 不归一化就永远匹配不上。
            if (ch is '·' or '-' or '_' or '\'' or '’' or '/' or '、' or
                '～' or '〜' or '~' or '－' or '—' or '–' or '‐' or '‑' or '−') continue;
            sb.Append(char.ToLowerInvariant(ch));
        }
        return sb.ToString();
    }

    internal static Hit? Pick(List<Hit> hits, string title, string artist, int expectedSeconds = 0) =>
        Rank(hits, title, artist, expectedSeconds).FirstOrDefault();

    /// <summary>Candidates above the acceptance score, best first.</summary>
    internal static List<Hit> Rank(List<Hit> hits, string title, string artist, int expectedSeconds = 0)
    {
        var target = Normalize(title);
        if (target.Length == 0) return new List<Hit>();
        var artistNorm = Normalize(artist);
        var scored = new List<(int score, Hit hit)>();
        foreach (var hit in hits)
        {
            var name = Normalize(hit.Name);
            int score = 0;
            if (name == target) score += 100;
            else if (name.Contains(target) || target.Contains(name)) score += 50;
            else continue;

            var singers = hit.Singer.Split('/', StringSplitOptions.RemoveEmptyEntries);
            foreach (var singer in singers)
            {
                var part = Normalize(singer);
                if (part.Length == 0) continue;
                if (artistNorm.Contains(part) || part.Contains(artistNorm) && artistNorm.Length > 0)
                {
                    score += 40;
                    break;
                }
                // "周杰伦/费玉清" vs "周杰伦" — a shared name is enough.
                if (artistNorm.Length > 0 && part.Length >= 2 && artistNorm.Contains(part[..Math.Min(3, part.Length)]))
                {
                    score += 30;
                    break;
                }
            }

            // Same title, different song: a 1:49 cover must not win over the 4:49 original just
            // because it came first in the list.
            int seconds = Seconds(hit.Interval);
            if (expectedSeconds > 0 && seconds > 0)
            {
                int gap = Math.Abs(seconds - expectedSeconds);
                if (gap <= 5) score += 40;
                else if (gap <= 15) score += 15;
                else if (gap > 60) score -= 80;
                else score -= 10;
            }

            if (score >= 90) scored.Add((score, hit));
        }
        return scored.OrderByDescending(s => s.score).Select(s => s.hit).ToList();
    }

    private static int Seconds(string interval)
    {
        if (string.IsNullOrEmpty(interval)) return 0;
        var parts = interval.Split(':');
        if (parts.Length == 2 && int.TryParse(parts[0], out var m) && int.TryParse(parts[1], out var s))
            return m * 60 + s;
        return 0;
    }

    // ---------- QRC → LyricDocument ----------

    /// <summary>
    /// QRC line: <c>[行开始,行时长]字(字开始,字时长)字(…)…</c>，字的时间是**绝对**毫秒。
    /// 少于 20% 的行带字时间就当作没有（有些歌 lrctype=4 也只回行级），交给原来的均分。
    /// </summary>
    public static LyricDocument? Build(string qrc, string translation)
    {
        var lines = ParseQrc(qrc, translation.Length == 0);
        if (lines == null || lines.Count == 0) return null;
        if (translation.Length > 0)
        {
            var extra = ParseTranslation(translation);
            if (extra != null) AttachTranslations(lines, extra);
        }
        int timed = lines.Count(l => l.Words.Count > 0 && !l.WordsEstimated);
        if (timed < 3 || timed * 5 < lines.Count) return null;
        return new LyricDocument { Lines = lines };
    }

    /// <summary>Translation comes back either as QRC rows or as plain <c>[mm:ss.xx]</c> LRC.</summary>
    private static List<LyricLine>? ParseTranslation(string text)
    {
        var qrc = ParseQrc(text, false);
        if (qrc != null) return qrc;
        var lrc = LrcParser.Parse(text);
        return lrc.Lines.Count > 0 ? lrc.Lines : null;
    }

    private static List<LyricLine>? ParseQrc(string qrc, bool omitTranslation)
    {
        if (string.IsNullOrWhiteSpace(qrc)) return null;
        var lines = new List<LyricLine>();
        foreach (var raw in qrc.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            var m = LineTag.Match(line);
            if (!m.Success)
            {
                // [ti:]/[ar:]/[offset:] and the odd "<LyricContent=…>" wrapper are not cues.
                continue;
            }
            long start = long.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            long duration = long.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
            var body = m.Groups[3].Value;
            var (text, words) = SplitWords(body, start);
            if (text.Trim().Length == 0 && words.Count == 0) continue;
            lines.Add(new LyricLine
            {
                StartMs = start,
                EndMs = duration > 0 ? start + duration : start,
                Text = text,
                Words = words,
            });
        }
        if (lines.Count == 0) return null;

        lines.Sort((a, b) => a.StartMs.CompareTo(b.StartMs));
        for (int i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            long next = i + 1 < lines.Count ? lines[i + 1].StartMs : long.MaxValue;
            if (line.EndMs <= line.StartMs)
                line.EndMs = next == long.MaxValue ? line.StartMs + 6000 : next;
            else if (next != long.MaxValue && line.EndMs > next)
                line.EndMs = next;
            if (line.Words.Count > 0)
                foreach (var word in line.Words)
                {
                    word.StartMs = Math.Clamp(word.StartMs, line.StartMs, Math.Max(line.StartMs, line.EndMs));
                    word.EndMs = Math.Clamp(word.EndMs, word.StartMs, Math.Max(word.StartMs, line.EndMs));
                }
        }
        return lines;
    }

    private static (string text, List<LyricWord> words) SplitWords(string body, long lineStart)
    {
        var matches = WordTag.Matches(body);
        if (matches.Count == 0) return (body.Trim(), new List<LyricWord>());

        var times = new List<(long start, long duration)>(matches.Count);
        var segments = new List<string>(matches.Count);
        int cursor = 0;
        foreach (Match m in matches)
        {
            segments.Add(body[cursor..m.Index]);
            times.Add((long.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture),
                       long.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture)));
            cursor = m.Index + m.Length;
        }
        var trailing = body[cursor..];

        var words = new List<LyricWord>(times.Count);
        for (int i = 0; i < times.Count; i++)
        {
            var text = segments[i];
            if (i == times.Count - 1) text += trailing;
            if (text.Length == 0) continue;
            words.Add(new LyricWord
            {
                Text = text,
                StartMs = times[i].start,
                EndMs = i + 1 < times.Count ? times[i + 1].start : times[i].start + times[i].duration,
            });
        }
        if (words.Count == 0) return (body.Trim(), new List<LyricWord>());

        // Word end times must not collapse onto each other when a QRC row has 0-length cues.
        for (int i = 0; i < words.Count; i++)
        {
            if (words[i].EndMs <= words[i].StartMs)
                words[i].EndMs = i + 1 < words.Count ? words[i + 1].StartMs : words[i].StartMs + 120;
        }

        var builder = new StringBuilder();
        int offset = 0;
        foreach (var word in words)
        {
            word.StartOffset = offset;
            builder.Append(word.Text);
            offset += word.Text.Length;
            word.EndOffset = offset;
        }
        return (builder.ToString(), words);
    }

    private static void AttachTranslations(List<LyricLine> lines, List<LyricLine> translation)
    {
        int cursor = 0;
        foreach (var line in lines)
        {
            while (cursor < translation.Count && translation[cursor].StartMs < line.StartMs - 250) cursor++;
            for (int i = Math.Max(0, cursor - 1); i < Math.Min(translation.Count, cursor + 3); i++)
            {
                var candidate = translation[i];
                if (Math.Abs(candidate.StartMs - line.StartMs) > 250) continue;
                var text = candidate.Text.Trim();
                if (text.Length == 0 || text == "//") continue;
                if (string.Equals(text, line.Text.Trim(), StringComparison.Ordinal)) continue;
                line.Translation = text;
                break;
            }
        }
    }
}
