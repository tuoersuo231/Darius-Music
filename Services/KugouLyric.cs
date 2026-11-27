using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CurrentLyrics.Models;

namespace CurrentLyrics.Services;

/// <summary>
/// 酷狗 KRC 歌词：搜索 → 按 hash 取 KRC 候选 → 下载 → 解密（16 字节异或 + zlib）→ 解析。
///
/// KRC 是逐字的（每行 <c>[行开始,行时长]&lt;字偏移,字时长,?&gt;字…</c>），而且**能按 audio hash 对**，
/// 比按歌名搜准得多：同一首歌的现场版、重编曲在库里是不同 hash，不会串。
/// 这是 <c>sxsxhhi/music-tag-lyrics</c> 阶梯里的 T4（<c>core/fetchers/kugou.py</c> + <c>core/decryptors/krc_xor.py</c>）。
/// </summary>
public static class KugouLyric
{
    private const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/124.0.0.0 Safari/537.36";

    /// <summary>KRC 固定异或密钥（16 字节）。</summary>
    private static readonly byte[] XorKey =
    {
        0x40, 0x47, 0x61, 0x77, 0x5e, 0x32, 0x74, 0x47,
        0x51, 0x36, 0x31, 0x2d, 0xce, 0xd2, 0x6e, 0x69,
    };

    private static readonly Regex LineTag = new(@"^\[(\d+),(\d+)\]", RegexOptions.Compiled);
    private static readonly Regex WordTag = new(@"<(\d+),(\d+),(-?\d+)>", RegexOptions.Compiled);
    /// <summary>Same tags without capture groups — <c>Regex.Split</c> would otherwise splice the
    /// numbers back into the text.</summary>
    private static readonly Regex WordSplit = new(@"<\d+,\d+,-?\d+>", RegexOptions.Compiled);
    private static readonly Regex LanguageTag = new(@"\[language:([A-Za-z0-9+/=]+)\]", RegexOptions.Compiled);

    private static readonly HttpClient Http = CreateHttp();

    /// <summary>Songs with no KRC, so a replay does not search again.</summary>
    private static readonly HashSet<long> Misses = new();

    public sealed record Hit(string Hash, string Name, string Singer, int Seconds);

    private static HttpClient CreateHttp()
    {
        var client = new HttpClient(new HttpClientHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.All,
            // The search host serves an invalid chain over https on some networks; plain http is what
            // the reference implementation uses and it answers reliably.
            AllowAutoRedirect = true,
        })
        {
            Timeout = TimeSpan.FromSeconds(15),
        };
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UserAgent);
        return client;
    }

    private static string CacheDir => Path.Combine(AppPaths.DataDir, "lyrics");

    public static async Task<LyricDocument?> EnsureAsync(long songId, string title, string artist,
        int expectedSeconds, LyricDocument? baseDoc, TimeSpan? budget = null)
    {
        var cached = TryCached(songId, baseDoc);
        if (cached != null) return cached;
        if (songId <= 0 || Misses.Contains(songId)) return null;

        var work = Task.Run(() =>
        {
            try
            {
                var doc = Fetch(title, artist, expectedSeconds, baseDoc);
                if (doc == null) return null;
                // Best-effort cache: a read-only data folder must not make this source report a miss.
                try
                {
                    Directory.CreateDirectory(CacheDir);
                    File.WriteAllText(Path.Combine(CacheDir, songId + ".krc"), Serialize(doc));
                }
                catch { }
                return doc;
            }
            catch
            {
                return null;
            }
        });

        var finished = await Task.WhenAny(work, Task.Delay(budget ?? TimeSpan.FromSeconds(12)))
            .ConfigureAwait(false);
        if (finished != work) return null;
        var result = await work.ConfigureAwait(false);
        if (result == null)
        {
            Misses.Add(songId);
            return null;
        }
        QqLyric.CarryTranslations(result, baseDoc);
        return result;
    }

    /// <summary>Disk only — the KRC parsed earlier for this song, if any.</summary>
    public static LyricDocument? TryCached(long songId, LyricDocument? baseDoc)
    {
        if (songId <= 0) return null;
        var file = Path.Combine(CacheDir, songId + ".krc");
        try
        {
            if (!File.Exists(file)) return null;
            var doc = Parse(File.ReadAllText(file));
            if (doc == null) return null;
            if (baseDoc != null && !QqLyric.AgreesWith(doc, baseDoc))
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

    public static LyricDocument? Fetch(string title, string artist, int expectedSeconds,
        LyricDocument? baseDoc)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;
        var keyword = title.Trim();
        var hits = Search((keyword + " " + artist).Trim(), 8);
        if (hits.Count == 0) hits = Search(keyword, 8);

        foreach (var hit in Rank(hits, title, artist, expectedSeconds).Take(4))
        {
            var doc = FetchByHash(hit.Hash, hit.Seconds, baseDoc);
            if (doc != null) return doc;
        }
        return null;
    }

    public static List<Hit> Search(string keyword, int limit)
    {
        var hits = new List<Hit>();
        var url = "http://mobilecdn.kugou.com/api/v3/search/song?format=json&showtype=1&page=1&pagesize=" +
                  limit.ToString(CultureInfo.InvariantCulture) + "&keyword=" + Uri.EscapeDataString(keyword);
        try
        {
            var text = Http.GetStringAsync(url).GetAwaiter().GetResult();
            var root = JsonNode.Parse(text);
            var list = root?["data"]?["info"] as JsonArray;
            if (list == null) return hits;
            foreach (var node in list)
            {
                if (node is not JsonObject song) continue;
                var hash = song["hash"]?.GetValue<string>() ?? "";
                if (hash.Length == 0) continue;
                hits.Add(new Hit(
                    hash,
                    song["songname"]?.GetValue<string>() ?? "",
                    song["singername"]?.GetValue<string>() ?? "",
                    song["duration"]?.GetValue<int>() ?? 0));
            }
        }
        catch { }
        return hits;
    }

    private static LyricDocument? FetchByHash(string hash, int seconds, LyricDocument? baseDoc)
    {
        var candidates = new List<(string Id, string Key, int Score)>();
        var searchUrl = "https://krcs.kugou.com/search?ver=1&man=yes&client=mobi&keyword=&duration=" +
                        seconds.ToString(CultureInfo.InvariantCulture) + "&hash=" + Uri.EscapeDataString(hash);
        try
        {
            var text = Http.GetStringAsync(searchUrl).GetAwaiter().GetResult();
            var list = JsonNode.Parse(text)?["candidates"] as JsonArray;
            if (list == null) return null;
            foreach (var node in list)
            {
                if (node is not JsonObject candidate) continue;
                var id = candidate["id"]?.ToString() ?? "";
                var key = candidate["accesskey"]?.GetValue<string>() ?? "";
                if (id.Length == 0 || key.Length == 0) continue;
                var score = int.TryParse(candidate["score"]?.ToString(), out var s) ? s : 0;
                candidates.Add((id, key, score));
            }
        }
        catch { return null; }

        foreach (var (id, key, _) in candidates.OrderByDescending(c => c.Score).Take(3))
        {
            var doc = Download(id, key, baseDoc);
            if (doc != null) return doc;
        }
        return null;
    }

    private static LyricDocument? Download(string id, string accessKey, LyricDocument? baseDoc)
    {
        var url = "https://krcs.kugou.com/download?ver=1&client=mobi&id=" + Uri.EscapeDataString(id) +
                  "&accesskey=" + Uri.EscapeDataString(accessKey) + "&fmt=krc&charset=utf8";
        try
        {
            var text = Http.GetStringAsync(url).GetAwaiter().GetResult();
            var content = JsonNode.Parse(text)?["content"]?.GetValue<string>() ?? "";
            if (content.Length == 0) return null;
            var raw = Convert.FromBase64String(content);
            var krc = Decrypt(raw);
            if (krc.Length == 0) return null;
            var doc = Parse(krc);
            if (doc == null) return null;
            AttachTranslations(doc, krc);
            if (baseDoc != null && !QqLyric.AgreesWith(doc, baseDoc)) return null;

            // Same alignment step as the QRC source: put the words on the NetEase timeline, and keep
            // the estimate beside the cache so a later load lands in the same place.
            var shift = QqLyric.EstimateShift(doc, baseDoc);
            if (shift != 0) QqLyric.ApplyShift(doc, shift);
            return doc;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>KRC: optional "krc1" magic, 16-byte xor, then zlib.</summary>
    internal static string Decrypt(byte[] raw)
    {
        if (raw.Length <= 4) return "";
        var offset = raw[0] == 'k' && raw[1] == 'r' && raw[2] == 'c' && raw[3] == '1' ? 4 : 0;
        if (raw.Length <= offset) return "";

        var plain = new byte[raw.Length - offset];
        for (int i = 0; i < plain.Length; i++) plain[i] = (byte)(raw[offset + i] ^ XorKey[i % 16]);
        try
        {
            using var input = new MemoryStream(plain);
            using var inflate = new ZLibStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            var chunk = new byte[8192];
            try
            {
                int read;
                while ((read = inflate.Read(chunk, 0, chunk.Length)) > 0) output.Write(chunk, 0, read);
            }
            catch { }
            return output.Length == 0 ? "" : Encoding.UTF8.GetString(output.ToArray());
        }
        catch
        {
            return "";
        }
    }

    // ---------- parse ----------

    /// <summary>`[行开始,行时长]&lt;字偏移,字时长,?&gt;字…`；字偏移相对行开始。</summary>
    public static LyricDocument? Parse(string krc)
    {
        if (string.IsNullOrWhiteSpace(krc)) return null;
        var lines = new List<LyricLine>();
        foreach (var raw in krc.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var text = raw.Trim();
            if (text.Length == 0) continue;
            // 缓存里译文跟在制表符后面（歌词正文不会出现制表符）。
            var tab = text.IndexOf('\t');
            var translation = "";
            if (tab >= 0)
            {
                translation = text[(tab + 1)..].Trim();
                text = text[..tab];
            }
            var lineTag = LineTag.Match(text);
            if (!lineTag.Success) continue;     // [ti:]/[ar:]/[kana:]/[language:]/[offset:]
            long start = long.Parse(lineTag.Groups[1].Value, CultureInfo.InvariantCulture);
            long duration = long.Parse(lineTag.Groups[2].Value, CultureInfo.InvariantCulture);
            var body = text[lineTag.Length..];

            var tags = WordTag.Matches(body);
            if (tags.Count == 0) continue;
            // KRC 的时间标签在字**前面**：`<0,351,0>願<351,169,0>い…`，所以第 i 个标签对应它后面
            // 那段文字（按标签切开后是第 i+1 段）。按"前面的文字"取会把每个字的时间错开一位、
            // 并且丢掉行尾最后一个字。
            var pieces = WordSplit.Split(body);
            var words = new List<LyricWord>(tags.Count);
            var builder = new StringBuilder();
            for (int i = 0; i < tags.Count && i + 1 < pieces.Length; i++)
            {
                var segment = pieces[i + 1];
                if (segment.Length == 0) continue;
                long wordStart = long.Parse(tags[i].Groups[1].Value, CultureInfo.InvariantCulture);
                long wordDuration = long.Parse(tags[i].Groups[2].Value, CultureInfo.InvariantCulture);
                words.Add(new LyricWord
                {
                    Text = segment,
                    StartMs = start + wordStart,
                    EndMs = start + wordStart + Math.Max(wordDuration, 0),
                    StartOffset = builder.Length,
                    EndOffset = builder.Length + segment.Length,
                });
                builder.Append(segment);
            }
            if (words.Count == 0) continue;
            for (int i = 0; i < words.Count; i++)
            {
                if (words[i].EndMs <= words[i].StartMs)
                    words[i].EndMs = i + 1 < words.Count ? words[i + 1].StartMs : words[i].StartMs + 120;
            }
            var lineText = builder.ToString();
            if (lineText.Trim().Length == 0) continue;
            lines.Add(new LyricLine
            {
                StartMs = start,
                EndMs = duration > 0 ? start + duration : words[^1].EndMs,
                Text = lineText,
                Translation = translation,
                Words = words,
            });
        }
        if (lines.Count < 3) return null;
        lines.Sort((a, b) => a.StartMs.CompareTo(b.StartMs));
        int timed = lines.Count(l => l.Words.Count > 0);
        if (timed * 5 < lines.Count) return null;
        return new LyricDocument { Lines = lines };
    }

    /// <summary>
    /// KRC 把译文放在 <c>[language:&lt;base64 JSON&gt;]</c> 里：<c>content[0].lyricContent[i][0]</c> 是第 i 行的
    /// 中文译文（空数组表示这行没有）。只有条目数与解析出的行数完全一致时才按序号挂 —— 差一个就整体
    /// 放弃，宁可没译文也不yht错位。
    /// </summary>
    private static void AttachTranslations(LyricDocument doc, string krc)
    {
        try
        {
            var match = LanguageTag.Match(krc);
            if (!match.Success) return;
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(match.Groups[1].Value));
            var entries = JsonNode.Parse(json)?["content"]?[0]?["lyricContent"] as JsonArray;
            if (entries == null || entries.Count != doc.Lines.Count) return;
            for (int i = 0; i < entries.Count; i++)
            {
                if (entries[i] is not JsonArray parts || parts.Count == 0) continue;
                var text = parts[0]?.GetValue<string>() ?? "";
                if (text.Trim().Length == 0) continue;
                doc.Lines[i].Translation = text.Trim();
            }
        }
        catch { }
    }

    /// <summary>Round-trips the cache: one A2 line per lyric line, translations included.</summary>
    private static string Serialize(LyricDocument doc)
    {
        var builder = new StringBuilder();
        foreach (var line in doc.Lines)
        {
            builder.Append('[').Append(line.StartMs).Append(',').Append(Math.Max(0, line.EndMs - line.StartMs))
                .Append(']');
            foreach (var word in line.Words)
                builder.Append('<').Append(Math.Max(0, word.StartMs - line.StartMs)).Append(',')
                    .Append(Math.Max(0, word.EndMs - word.StartMs)).Append(",0>").Append(word.Text);
            if (line.Translation.Length > 0) builder.Append('\t').Append(line.Translation);
            builder.Append('\n');
        }
        return builder.ToString();
    }

    // ---------- matching ----------

    internal static List<Hit> Rank(List<Hit> hits, string title, string artist, int expectedSeconds)
    {
        var target = QqLyric.Normalize(title);
        if (target.Length == 0) return new List<Hit>();
        var artistNorm = QqLyric.Normalize(artist);
        var scored = new List<(int Score, Hit Hit)>();
        foreach (var hit in hits)
        {
            var name = QqLyric.Normalize(hit.Name);
            int score = name == target ? 100 : name.Contains(target) || target.Contains(name) ? 50 : 0;
            if (score == 0) continue;
            var singer = QqLyric.Normalize(hit.Singer);
            if (artistNorm.Length > 0 && singer.Length > 0 &&
                (singer.Contains(artistNorm) || artistNorm.Contains(singer))) score += 40;
            if (expectedSeconds > 0 && hit.Seconds > 0)
            {
                int gap = Math.Abs(hit.Seconds - expectedSeconds);
                if (gap <= 5) score += 40;
                else if (gap <= 15) score += 15;
                else if (gap > 60) score -= 80;
            }
            if (score >= 90) scored.Add((score, hit));
        }
        return scored.OrderByDescending(s => s.Score).Select(s => s.Hit).ToList();
    }
}
