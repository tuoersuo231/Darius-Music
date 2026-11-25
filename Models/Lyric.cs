namespace CurrentLyrics.Models;

/// <summary>A timed syllable / character cue, mirroring CurrentMusic's LyricWord.</summary>
public sealed class LyricWord
{
    public string Text = "";
    public long StartMs;
    public long EndMs;
    public int StartOffset;
    public int EndOffset;
    public float GlowWeight = -1f;   // lazily computed from duration when &lt; 0
}

public sealed class LyricLine
{
    public long StartMs;
    public long EndMs;
    public string Text = "";
    public string Translation = "";
    public string Romanization = "";
    public List<LyricWord> Words = new();
    public bool WordsEstimated;
    /// <summary>Second-language echo of the sung line (e.g. the Chinese line in a Japanese LRC).</summary>
    public bool TranslationOnly;

    public bool HasWordTiming => Words.Count > 0 && !WordsEstimated;
    public override string ToString() => $"[{StartMs}] {Text}";
}

public sealed class LyricDocument
{
    public List<LyricLine> Lines = new();
    public string Title = "";
    public string Artist = "";
    public long OffsetMs;

    public bool HasWordTiming => Lines.Any(l => l.HasWordTiming);

    /// <summary>Index of the line whose interval contains <paramref name="positionMs"/>; keeps the
    /// last finished line through silence, like LyricsSynchronizer.scrollTarget.</summary>
    public int LineIndexAt(long positionMs, out float lineProgress)
    {
        lineProgress = 0f;
        if (Lines.Count == 0) return -1;
        int best = -1;
        for (int i = 0; i < Lines.Count; i++)
            if (Lines[i].StartMs <= positionMs) best = i; else break;
        if (best < 0) return -1;
        var line = Lines[best];
        if (line.EndMs > line.StartMs)
            lineProgress = Math.Clamp((positionMs - line.StartMs) / (float)(line.EndMs - line.StartMs), 0f, 1f);
        return best;
    }
}
