using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Xml.Linq;
using CurrentLyrics.Models;

namespace CurrentLyrics.Services;

/// <summary>
/// AMLL 逐字歌词（Apple Music 的 TTML 社区库），**按网易云曲目 id 直取**：
/// <c>amll-ttml-db/ncm-lyrics/&lt;id&gt;.ttml</c>。比按歌名搜索可靠得多 —— id 本身就是匹配结果，
/// 而且 TTML 里除了每个字的起止时间，还带中文译文（<c>ttm:role="x-translation"</c>）和罗马音（<c>x-roman</c>）。
///
/// 这是 <c>sxsxhhi/music-tag-lyrics</c> 那个多源阶梯里的一级（<c>core/fetchers/amll.py</c>），
/// 排在网络源的最后、但质量最高：命中就是官方逐字。
/// </summary>
public static class AmllLyrics
{
    private const string BaseUrl = "https://cdn.jsdelivr.net/gh/amll-dev/amll-ttml-db@main/ncm-lyrics/";
    private const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/124.0.0.0 Safari/537.36";

    private static readonly HttpClient Http = CreateHttp();

    /// <summary>Songs known to be absent from the DB, so a replay does not ask again.</summary>
    private static readonly HashSet<long> Misses = new();

    private static HttpClient CreateHttp()
    {
        var client = new HttpClient(new HttpClientHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.All,
        })
        {
            Timeout = TimeSpan.FromSeconds(12),
        };
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UserAgent);
        return client;
    }

    private static string CacheDir => Path.Combine(AppPaths.DataDir, "lyrics");

    public static async Task<LyricDocument?> EnsureAsync(long ncmId, LyricDocument? baseDoc,
        TimeSpan? budget = null)
    {
        var cached = TryCached(ncmId, baseDoc);
        if (cached != null) return cached;
        if (ncmId <= 0 || Misses.Contains(ncmId)) return null;

        var work = Task.Run(() =>
        {
            try
            {
                var url = BaseUrl + ncmId.ToString(CultureInfo.InvariantCulture) + ".ttml";
                using var response = Http.GetAsync(url).GetAwaiter().GetResult();
                if (!response.IsSuccessStatusCode) return null;
                var ttml = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                var doc = Parse(ttml);
                if (doc == null) return null;
                // Cache write is best-effort: a read-only data folder must not turn a perfectly good
                // TTML into "this source found nothing" (that is how the 打上花火 run looked).
                try
                {
                    Directory.CreateDirectory(CacheDir);
                    File.WriteAllText(Path.Combine(CacheDir, ncmId + ".ttml"), ttml);
                }
                catch { }
                return doc;
            }
            catch
            {
                return null;
            }
        });

        var finished = await Task.WhenAny(work, Task.Delay(budget ?? TimeSpan.FromSeconds(8)))
            .ConfigureAwait(false);
        if (finished != work) return null;
        var result = await work.ConfigureAwait(false);
        if (result == null)
        {
            Misses.Add(ncmId);
            return null;
        }
        CarryTranslations(result, baseDoc);
        return result;
    }

    /// <summary>Disk only — the TTML already downloaded for this song, if any.</summary>
    public static LyricDocument? TryCached(long ncmId, LyricDocument? baseDoc)
    {
        if (ncmId <= 0) return null;
        var file = Path.Combine(CacheDir, ncmId + ".ttml");
        try
        {
            if (!File.Exists(file)) return null;
            var doc = Parse(File.ReadAllText(file));
            if (doc == null) return null;
            if (baseDoc != null && !AgreesWith(doc, baseDoc))
            {
                try { File.Delete(file); } catch { }
                return null;
            }
            CarryTranslations(doc, baseDoc);
            return doc;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Same guard as the QRC source: the DB can hold a different language version.</summary>
    private static bool AgreesWith(LyricDocument candidate, LyricDocument baseDoc)
    {
        double overlap = QqLyric.LineOverlap(candidate, baseDoc);
        return overlap < 0 || overlap >= 0.15;
    }

    /// <summary>Fill missing translations from the NetEase document (AMLL usually has its own).</summary>
    private static void CarryTranslations(LyricDocument target, LyricDocument? baseDoc) =>
        QqLyric.CarryTranslations(target, baseDoc);

    // ---------- TTML ----------

    /// <summary>
    /// Reads Apple-style lyric TTML: one <c>&lt;p&gt;</c> per line, one <c>&lt;span begin end&gt;</c> per
    /// word, plus optional <c>ttm:role="x-translation"</c> / <c>"x-roman"</c> spans inside the same
    /// paragraph. Whitespace between spans is formatting, not lyric text — the spans carry their own.
    /// </summary>
    public static LyricDocument? Parse(string ttml)
    {
        if (string.IsNullOrWhiteSpace(ttml)) return null;
        XDocument xml;
        try { xml = XDocument.Parse(ttml); }
        catch { return null; }

        var lines = new List<LyricLine>();
        foreach (var paragraph in xml.Descendants().Where(e => e.Name.LocalName == "p"))
        {
            long start = Attribute(paragraph, "begin", -1);
            if (start < 0) continue;
            long end = Attribute(paragraph, "end", -1);

            var words = new List<LyricWord>();
            var text = new StringBuilder();
            var translation = "";
            var roman = "";
            foreach (var span in paragraph.Elements().Where(e => e.Name.LocalName == "span"))
            {
                var content = span.Value;
                var role = Role(span);
                if (role == "x-translation" || role == "translation")
                {
                    translation = content.Trim();
                    continue;
                }
                if (role == "x-roman" || role == "roman")
                {
                    roman = content.Trim();
                    continue;
                }

                long wordStart = Attribute(span, "begin", -1);
                long wordEnd = Attribute(span, "end", -1);
                if (wordStart < 0 || content.Length == 0)
                {
                    text.Append(content);
                    continue;
                }
                words.Add(new LyricWord
                {
                    Text = content,
                    StartMs = wordStart,
                    EndMs = wordEnd > wordStart ? wordEnd : wordStart,
                    StartOffset = text.Length,
                    EndOffset = text.Length + content.Length,
                });
                text.Append(content);
            }

            if (words.Count == 0)
            {
                // A paragraph without timed spans still carries the line text.
                var plain = text.Length > 0 ? text.ToString() : paragraph.Value;
                if (plain.Trim().Length == 0) continue;
                lines.Add(new LyricLine { StartMs = start, EndMs = end > start ? end : start, Text = plain.Trim() });
                continue;
            }

            if (end <= start) end = words[^1].EndMs;
            for (int i = 0; i < words.Count; i++)
            {
                if (words[i].EndMs <= words[i].StartMs)
                    words[i].EndMs = i + 1 < words.Count ? words[i + 1].StartMs : words[i].StartMs + 120;
            }
            lines.Add(new LyricLine
            {
                StartMs = start,
                EndMs = Math.Max(end, words[^1].EndMs),
                Text = text.ToString(),
                Translation = translation,
                Romanization = roman,
                Words = words,
            });
        }

        if (lines.Count == 0) return null;
        lines.Sort((a, b) => a.StartMs.CompareTo(b.StartMs));
        return new LyricDocument { Lines = lines };
    }

    private static string Role(XElement span) =>
        (span.Attributes().FirstOrDefault(a => a.Name.LocalName == "role")?.Value ?? "").Trim();

    private static long Attribute(XElement element, string name, long fallback)
    {
        var attribute = element.Attributes().FirstOrDefault(a => a.Name.LocalName == name);
        return attribute == null ? fallback : ParseTime(attribute.Value, fallback);
    }

    /// <summary>Accepts <c>1.372</c>, <c>0:22.402</c> and <c>0:01:02.402</c>.</summary>
    internal static long ParseTime(string value, long fallback = -1)
    {
        var text = value.Trim();
        if (text.Length == 0) return fallback;
        var parts = text.Split(':');
        double seconds;
        try
        {
            switch (parts.Length)
            {
                case 1:
                    seconds = double.Parse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture);
                    break;
                case 2:
                    seconds = double.Parse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture) * 60 +
                              double.Parse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture);
                    break;
                default:
                    seconds = double.Parse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture) * 3600 +
                              double.Parse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture) * 60 +
                              double.Parse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture);
                    break;
            }
        }
        catch
        {
            return fallback;
        }
        return (long)Math.Round(seconds * 1000);
    }
}
