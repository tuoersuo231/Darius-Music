using CurrentLyrics.Models;

namespace CurrentLyrics.Controls;

/// <summary>
/// Timing curves ported from CurrentMusic's LyricsEffects.kt / LyricsSynchronizer.kt:
/// per-word progress, karaoke glow envelope and the held-note weight.
/// </summary>
public static class LyricTiming
{
    public static float WordProgress(LyricWord word, long position)
    {
        if (word.EndMs <= word.StartMs) return position >= word.EndMs ? 1f : 0f;
        return (float)Math.Clamp((position - word.StartMs) / (double)(word.EndMs - word.StartMs), 0, 1);
    }

    public static float Smooth(float t)
    {
        t = Math.Clamp(t, 0, 1);
        return t * t * (3f - 2f * t);
    }

    /// <summary>lyricGlowStrength: bloom in during the first half of the word and then stay lit.
    /// It used to fade out again as the word ended, so at any instant exactly one syllable carried a
    /// halo for a few hundred milliseconds — with the switch on, the line never looked lit at all.
    /// Holding the value makes the whole sung prefix of the line glow.</summary>
    public static float GlowStrength(LyricWord word, long position)
    {
        if (word.EndMs <= word.StartMs) return position >= word.StartMs ? 1f : 0f;
        if (position <= word.StartMs) return 0f;
        float progress = (position - word.StartMs) / (float)(word.EndMs - word.StartMs);
        return Smooth(Math.Min(1f, progress / 0.45f));
    }

    /// <summary>lyricGlowWeight: every sung syllable earns a halo, held notes earn the brightest one.
    /// The floor is what makes 歌词辉光 switched on actually visible: the old curve only left zero
    /// past 550 ms per syllable, so ordinary pop lines (250-500 ms per syllable) never glowed at all
    /// and the switch looked dead.</summary>
    public static float GlowWeight(LyricWord word)
    {
        if (word.GlowWeight >= 0) return word.GlowWeight;
        int cjk = word.Text.Count(IsCjk);
        int vowels = CountVowelGroups(word.Text);
        int syllables = Math.Max(1, cjk + vowels);
        double duration = (word.EndMs - word.StartMs) / (double)syllables;
        word.GlowWeight = (float)(0.5 + 0.5 * Math.Clamp((duration - 300.0) / 800.0, 0, 1));
        return word.GlowWeight;
    }

    private static int CountVowelGroups(string text)
    {
        int groups = 0;
        bool inGroup = false;
        foreach (var ch in text)
        {
            bool vowel = "aeiouyAEIOUY".Contains(ch);
            if (vowel && !inGroup) { groups++; inGroup = true; }
            else if (!vowel) inGroup = false;
        }
        return groups;
    }

    private static bool IsCjk(char c) =>
        (c >= '\u3400' && c <= '\u9fff') || (c >= '\u3040' && c <= '\u30ff') || (c >= '\uac00' && c <= '\ud7af');
}
