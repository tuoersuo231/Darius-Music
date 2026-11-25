using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using CurrentLyrics.Models;

namespace CurrentLyrics.Services;

/// <summary>LRC reader covering the shapes CurrentMusic accepts: plain lines, repeated line
/// timestamps, A2 inline word timing (&lt;mm:ss.xx&gt;), bilingual pairs and [offset:].</summary>
public static partial class LrcParser
{
    [GeneratedRegex(@"([\[<])(\d+):(\d+)(?:[.:](\d+))?([\]>])", RegexOptions.Compiled)]
    private static partial Regex TimestampRegex();

    [GeneratedRegex(@"^\[([A-Za-z][\w-]*):(.*)\]$", RegexOptions.Compiled)]
    private static partial Regex MetaRegex();

    private static readonly string[] SidecarSuffixes =
        { ".lrc", ".zh.lrc", ".trans.lrc", ".translation.lrc", ".orig.lrc" };

    public static LyricDocument Parse(string raw)
    {
        var doc = new LyricDocument();
        var lines = new List<LyricLine>();
        long offsetMs = 0;

        foreach (var rawLine in raw.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var trimmed = rawLine.Trim();
            if (trimmed.Length == 0) continue;

            var meta = MetaRegex().Match(trimmed);
            if (meta.Success)
            {
                var key = meta.Groups[1].Value.ToLowerInvariant();
                var val = meta.Groups[2].Value.Trim();
                switch (key)
                {
                    case "ti": doc.Title = val; break;
                    case "ar": doc.Artist = val; break;
                    case "offset":
                        if (long.TryParse(val, out var off)) offsetMs = off;
                        break;
                    case "al":
                    case "by":
                    case "length":
                    case "re":
                    case "ve":
                        break;
                    default:
                        // Unknown bracketed metadata is not lyric text.
                        break;
                }
                continue;
            }

            var stamps = new List<long>();
            int cursor = 0;
            while (cursor < trimmed.Length)
            {
                var m = TimestampRegex().Match(trimmed, cursor);
                if (!m.Success || m.Index != cursor) break;
                // Only a '[' ... ']' pair starts a line cue.
                if (m.Groups[1].Value != "[") break;
                stamps.Add(ToMs(m));
                cursor = m.Index + m.Length;
                // Spaces between repeated timestamps are not lyric text.
                while (cursor < trimmed.Length && trimmed[cursor] == ' ') cursor++;
            }
            if (stamps.Count == 0)
            {
                // Continuation of a multi-line lyric without its own timestamp: ignore safely.
                continue;
            }

            var body = trimmed[cursor..].TrimEnd();
            var (text, words) = ParseBody(body);
            if (text.Trim().Length == 0 && words.Count == 0) continue;

            foreach (var start in stamps)
            {
                lines.Add(new LyricLine
                {
                    StartMs = start,
                    EndMs = start,
                    Text = text,
                    Words = words.Select(w => new LyricWord
                    {
                        Text = w.Text, StartMs = w.StartMs, EndMs = w.EndMs,
                        StartOffset = w.StartOffset, EndOffset = w.EndOffset
                    }).ToList()
                });
            }
        }

        if (offsetMs != 0)
        {
            foreach (var line in lines)
            {
                line.StartMs = Math.Max(0, line.StartMs - offsetMs);
                foreach (var w in line.Words)
                {
                    w.StartMs = Math.Max(0, w.StartMs - offsetMs);
                    w.EndMs = Math.Max(0, w.EndMs - offsetMs);
                }
            }
        }

        lines.Sort((a, b) => a.StartMs.CompareTo(b.StartMs));
        lines = MergeBilingual(lines);
        for (int i = 0; i < lines.Count; i++)
        {
            var next = i + 1 < lines.Count ? lines[i + 1].StartMs : long.MaxValue;
            if (lines[i].EndMs <= lines[i].StartMs)
                lines[i].EndMs = next == long.MaxValue ? lines[i].StartMs + 6000 : Math.Max(lines[i].StartMs + 600, next);
            else if (next != long.MaxValue && lines[i].EndMs > next)
                lines[i].EndMs = next;

            if (lines[i].Words.Count == 0 && lines[i].Text.Trim().Length > 0)
                SynthesizeWords(lines[i]);
        }

        MarkTranslationOnly(lines);
        doc.Lines = lines;
        return doc;
    }

    private static (string text, List<LyricWord> words) ParseBody(string body)
    {
        // A2: "[mm:ss.xx]<mm:ss.xx>word<mm:ss.xx>word"
        if (!body.Contains('<')) return (body, new List<LyricWord>());

        var words = new List<LyricWord>();
        var sb = new StringBuilder();
        int cursor = 0;
        long? currentStart = null;
        int textStart = 0;

        while (cursor < body.Length)
        {
            if (body[cursor] == '<')
            {
                var m = TimestampRegex().Match(body, cursor);
                if (m.Success && m.Index == cursor)
                {
                    long ms = ToMs(m);
                    if (currentStart is long cs) words.Add(new LyricWord { Text = sb.ToString(), StartMs = cs, EndMs = ms, StartOffset = textStart, EndOffset = textStart + sb.Length });
                    sb.Clear();
                    textStart = sb.Length;
                    currentStart = ms;
                    cursor = m.Index + m.Length;
                    continue;
                }
            }
            sb.Append(body[cursor]);
            cursor++;
        }
        if (currentStart is long last)
            words.Add(new LyricWord { Text = sb.ToString(), StartMs = last, EndMs = last + 800, StartOffset = textStart, EndOffset = textStart + sb.Length });

        // Rebuild offsets against the flattened text actually shown.
        var text = string.Concat(words.Select(w => w.Text));
        int pos = 0;
        foreach (var w in words)
        {
            w.StartOffset = pos;
            pos += w.Text.Length;
            w.EndOffset = pos;
        }
        return (text, words.Where(w => w.Text.Length > 0).ToList());
    }

    private static long ToMs(Match m)
    {
        long minutes = long.Parse(m.Groups[2].Value);
        long seconds = long.Parse(m.Groups[3].Value);
        var frac = m.Groups[4].Value;
        long ms = 0;
        if (frac.Length > 0)
        {
            if (frac.Length == 1) ms = long.Parse(frac) * 100;
            else if (frac.Length == 2) ms = long.Parse(frac) * 10;
            else ms = long.Parse(frac[..3]);
        }
        return minutes * 60_000 + seconds * 1000 + ms;
    }

    /// <summary>Two cues with the same start time are a lyric + translation pair.</summary>
    private static List<LyricLine> MergeBilingual(List<LyricLine> lines)
    {
        var result = new List<LyricLine>(lines.Count);
        foreach (var line in lines)
        {
            var previous = result.LastOrDefault();
            // Same timestamp = original + translation, even when both sides are CJK (Japanese lyric
            // with a Chinese translation used to slip through and render as a duplicate line).
            // Both sides word-timed means two *sung* lines that merely start close together (karaoke
            // LRCs put the credits ~40 ms apart) — merging those silently ate the first lyric line.
            bool bothSung = previous is { Words.Count: > 0 } && line.Words.Count > 0;
            if (previous != null && !bothSung
                && Math.Abs(previous.StartMs - line.StartMs) <= 60
                && previous.Translation.Length == 0
                && !string.Equals(previous.Text.Trim(), line.Text.Trim(), StringComparison.Ordinal))
            {
                // The karaoke-timed line is the sung one; the untimed twin is its translation.
                if (line.Words.Count > 0 && previous.Words.Count == 0)
                {
                    line.Translation = previous.Text;
                    line.StartMs = previous.StartMs;
                    result[result.Count - 1] = line;
                }
                else
                {
                    previous.Translation = line.Text;
                }
                continue;
            }
            result.Add(line);
        }
        return result;
    }

    /// <summary>
    /// Most LRC files in this library only start at the first sung line (11–31 s in), so the overlay
    /// looked like it had no opening at all. When a file has nothing before its first cue, prepend a
    /// two-line card built from the audio tags ("title - artist", then the album). The .lrc file
    /// itself is never touched, and files that already carry credits (first cue &lt; 3 s) are left alone.
    /// </summary>
    public static LyricDocument? WithOpeningCard(LyricDocument? doc, string? title, string? artist, string? album)
    {
        if (doc == null || doc.Lines.Count == 0) return doc;
        long firstCue = long.MaxValue;
        foreach (var l in doc.Lines) if (l.StartMs < firstCue) firstCue = l.StartMs;
        if (firstCue < 3000) return doc;

        var card = new List<LyricLine>(2);
        string head = string.Join(" - ", new[] { title, artist }
            .Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s!.Trim()));
        if (head.Length > 0)
            card.Add(new LyricLine { StartMs = 0, EndMs = 900, Text = head });
        // A normal line duration, not "until the first cue": a 30 s end time would make the glow
        // weight (derived from duration) behave like a held note.
        if (!string.IsNullOrWhiteSpace(album))
            card.Add(new LyricLine { StartMs = 900, EndMs = 2600, Text = album!.Trim() });
        if (card.Count == 0) return doc;

        var lines = new List<LyricLine>(doc.Lines.Count + card.Count);
        lines.AddRange(card);
        lines.AddRange(doc.Lines);
        return new LyricDocument { Lines = lines, Title = doc.Title, Artist = doc.Artist, OffsetMs = doc.OffsetMs };
    }

    /// <summary>
    /// A Japanese/English lyric file often ships the Chinese translation as its own timestamped line.
    /// When the document is kana-based, han-only lines are echoes of the sung line, not lyrics — but
    /// only if they really are echoes. Credit blocks ("词：米津玄師", "Guitar：真壁陽平") are han-only
    /// too, and blanket-marking them made the intro credits vanish from the overlay.
    /// </summary>
    private static void MarkTranslationOnly(List<LyricLine> lines)
    {
        bool hasKana = lines.Any(l => l.Text.Any(IsKana));
        if (!hasKana) return;
        for (int i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            if (line.Translation.Length > 0) continue;
            bool hasHan = line.Text.Any(c => c >= '\u4e00' && c <= '\u9fff');
            bool lineKana = line.Text.Any(IsKana);
            if (!hasHan || lineKana) continue;
            // "label：value" is a credit row, never a translation of a sung line.
            if (LooksLikeCredit(line.Text)) continue;
            // Only an echo when a sung (kana) line sits right next to it in time.
            if (!HasKanaNeighbour(lines, i, 1500)) continue;
            line.TranslationOnly = true;
        }
    }

    /// <summary>A short "词：…" / "Produce: …" style label before the first colon.</summary>
    private static bool LooksLikeCredit(string text)
    {
        int colon = text.IndexOfAny(new[] { '：', ':' });
        return colon >= 0 && colon <= 24;
    }

    private static bool HasKanaNeighbour(List<LyricLine> lines, int index, long windowMs)
    {
        for (int step = -1; step <= 1; step += 2)
        {
            for (int j = index + step; j >= 0 && j < lines.Count; j += step)
            {
                if (Math.Abs(lines[j].StartMs - lines[index].StartMs) > windowMs) break;
                if (lines[j].Text.Any(IsKana)) return true;
            }
        }
        return false;
    }

    private static bool IsKana(char c) =>
        (c >= '\u3040' && c <= '\u30ff') || (c >= '\uff66' && c <= '\uff9f');

    private static bool IsLikelyTranslation(string original, string candidate)
    {
        if (candidate.Length == 0) return false;
        bool origCjk = original.Any(IsCjk);
        bool candCjk = candidate.Any(IsCjk);
        // "中文 + 英文" or two different scripts read as a translation pair.
        if (origCjk != candCjk) return true;
        return false;
    }

    private static bool IsCjk(char c) =>
        (c >= '\u3400' && c <= '\u9fff') || (c >= '\u3040' && c <= '\u30ff') || (c >= '\uac00' && c <= '\ud7af');

    /// <summary>Spread a line without word timing across its clusters so the sweep still runs.</summary>
    private static void SynthesizeWords(LyricLine line)
    {
        var clusters = Clusters(line.Text);
        if (clusters.Count == 0) return;
        long span = Math.Max(400, line.EndMs - line.StartMs);
        double weight = clusters.Sum(c => c.weight);
        long cursor = line.StartMs;
        foreach (var (text, start, length, weightPart) in clusters)
        {
            long dur = (long)(span * (weightPart / weight));
            line.Words.Add(new LyricWord
            {
                Text = text, StartMs = cursor, EndMs = cursor + dur,
                StartOffset = start, EndOffset = start + length
            });
            cursor += dur;
        }
        line.WordsEstimated = true;
    }

    /// <summary>Split into karaoke clusters: one per CJK glyph, one per Latin word.</summary>
    public static List<(string text, int start, int length, double weight)> Clusters(string text)
    {
        var result = new List<(string, int, int, double)>();
        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (IsCjk(c) || IsFullWidthPunctuation(c))
            {
                result.Add((text[i].ToString(), i, 1, 1.0));
                i++;
                continue;
            }
            int start = i;
            while (i < text.Length && !char.IsWhiteSpace(text[i]) && !IsCjk(text[i])) i++;
            var word = text[start..i];
            // Weight by syllable-ish length so long English words get more time.
            double w = Math.Max(0.7, word.Count(ch => "aeiouyAEIOUY".Contains(ch)) + word.Count(ch => ch is ' '));
            result.Add((word, start, i - start, w));
        }
        return result;
    }

    private static bool IsFullWidthPunctuation(char c) =>
        c is '，' or '。' or '！' or '？' or '、' or '；' or '：' or '“' or '”' or '‘' or '’' or '（' or '）' or '《' or '》' or '…' or '—';

    public static string? FindSidecar(string audioPath)
    {
        var dir = System.IO.Path.GetDirectoryName(audioPath) ?? "";
        var stem = System.IO.Path.GetFileNameWithoutExtension(audioPath);
        foreach (var suffix in SidecarSuffixes)
        {
            var candidate = System.IO.Path.Combine(dir, stem + suffix);
            if (File.Exists(candidate)) return candidate;
        }
        var lyricsDir = System.IO.Path.Combine(dir, "Lyrics");
        if (Directory.Exists(lyricsDir))
        {
            foreach (var suffix in new[] { ".lrc", ".LRC" })
            {
                var candidate = System.IO.Path.Combine(lyricsDir, stem + suffix);
                if (File.Exists(candidate)) return candidate;
            }
        }
        return null;
    }

    public static LyricDocument? LoadFor(string audioPath)
    {
        var path = FindSidecar(audioPath);
        if (path == null) return null;
        try
        {
            var bytes = File.ReadAllBytes(path);
            var text = DecodeText(bytes);
            var doc = Parse(text);
            return doc.Lines.Count > 0 ? doc : null;
        }
        catch { return null; }
    }

    private static string DecodeText(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        try
        {
            return new UTF8Encoding(false, true).GetString(bytes);
        }
        catch
        {
            return Encoding.GetEncoding("GB18030").GetString(bytes);
        }
    }

    /// <summary>Backs the built-in demo document used by the visual QA renderer.</summary>
    public static LyricDocument Demo()
    {
        const string lrc = """
[00:00.00]Darius Music
[00:06.40]<00:06.40>让<00:06.90>封<00:07.40>面<00:07.90>、<00:08.20>歌<00:08.70>词<00:09.20>与<00:09.60>音<00:10.10>乐<00:10.60>一<00:11.10>起<00:11.60>流<00:12.10>动
[00:06.40]Let cover, lyrics and music flow together
[00:13.20]<00:13.20>圆<00:13.80>形<00:14.40>封<00:15.00>面<00:15.60>随<00:16.20>歌<00:16.80>声<00:17.40>展<00:18.00>开
[00:13.20]A round cover unfolds with the voice
[00:19.00]<00:19.00>逐<00:19.50>字<00:20.00>高<00:20.50>亮<00:21.00>，<00:21.30>辉<00:21.80>光<00:22.30>在<00:22.80>延<00:23.30>音<00:24.60>处<00:25.10>绽<00:25.60>放
[00:19.00]Every glyph lights up, and glows where a note is held
[00:26.40]桌面之上，歌词自成一个世界
[00:26.40]Above the desktop, lyrics become a world of their own
""";
        return Parse(lrc);
    }
}
