using System;
using System.Collections.Generic;

namespace CurrentLyrics.Services;

/// <summary>
/// DTW over the cross-attention matrix (row = text token, column = 20 ms encoder frame).
/// The path is monotonic, so token times can never go backwards.
/// </summary>
public static class WhisperDtw
{
    /// <summary>
    /// attn is [nTok * stride] row-major, but only the first nFrames columns carry real audio
    /// (the encoder always emits 1500 frames; a short window only fills the head of the array).
    /// Returns inclusive frame ranges per token.
    /// </summary>
    public static (int Start, int End)[] Align(float[] attn, int nTok, int nFrames, int stride, int speechFrames = -1)
    {
        var result = new (int Start, int End)[nTok];
        if (nTok <= 0 || nFrames <= 0 || stride < nFrames) return result;

        // Weighted median of each token's attention row, then force strict monotonicity.
        // Plain DTW degenerates here: whisper spreads a lot of mass over the window, so
        // -log(attn) barely discriminates and the cheapest path collapses onto one frame.
        var pos = new int[nTok];
        var weight = new double[nFrames];
        for (int t = 0; t < nTok; t++)
        {
            int rb = t * stride;
            double total = 0;
            for (int f = 0; f < nFrames; f++)
            {
                double w = attn[rb + f];
                if (w < 0) w = 0;
                weight[f] = w;
                total += w;
            }
            int median = 0;
            if (total > 0)
            {
                double acc = 0;
                for (int f = 0; f < nFrames; f++)
                {
                    acc += weight[f];
                    if (acc >= total * 0.5) { median = f; break; }
                }
            }
            pos[t] = median;
        }
        for (int t = 1; t < nTok; t++)
            if (pos[t] <= pos[t - 1]) pos[t] = pos[t - 1] + 1;
        for (int t = nTok - 1; t >= 0; t--)
            if (pos[t] > nFrames - 1) pos[t] = nFrames - 1;
        // the monotonic pass from the left can overflow the last point; pull back
        for (int t = nTok - 2; t >= 0; t--)
            if (pos[t] >= pos[t + 1]) pos[t] = pos[t + 1] - 1;
        for (int t = 0; t < nTok; t++)
            if (pos[t] < 0) pos[t] = 0;

        for (int t = 0; t < nTok; t++)
        {
            int start = t == 0 ? pos[0] : (pos[t - 1] + pos[t]) / 2;
            int end = t == nTok - 1 ? pos[t] : (pos[t] + pos[t + 1]) / 2;
            if (end < start) end = start;
            result[t] = (start, end);
        }
        return result;
    }


    /// <summary>Classic DTW with a monotonic path; the end of the path is anchored on the last
    /// token's attention peak (the cheapest cell instead collapses on the first tokens).</summary>
    public static (int Start, int End)[] AlignDtw(float[] attn, int nTok, int nFrames, int stride)
    {
        var result = new (int Start, int End)[nTok];
        if (nTok <= 0 || nFrames <= 0 || stride < nFrames) return result;
        const double Eps = 1e-7;
        int width = nFrames + 1;
        var dir = new byte[nTok * width];
        var prev = new double[width];
        var cur = new double[width];

        double Cost(int t, int f)
        {
            double v = attn[t * stride + f];
            if (v < Eps) v = Eps;
            return -Math.Log(v);
        }

        prev[0] = double.PositiveInfinity;
        for (int f = 1; f <= nFrames; f++) { prev[f] = Cost(0, f - 1); dir[f] = 0; }

        for (int t = 1; t < nTok; t++)
        {
            cur[0] = double.PositiveInfinity;
            int rb = t * width;
            for (int f = 1; f <= nFrames; f++)
            {
                double c = Cost(t, f - 1);
                double diag = prev[f - 1], up = prev[f], left = cur[f - 1];
                double best = diag; byte d = 0;
                if (up < best) { best = up; d = 1; }
                if (left < best) { best = left; d = 2; }
                cur[f] = c + best;
                dir[rb + f] = d;
            }
            var swap = prev; prev = cur; cur = swap;
        }

        int lastRow = (nTok - 1) * stride;
        int endF = 1; double peak = double.NegativeInfinity;
        for (int f = 0; f < nFrames; f++)
            if (attn[lastRow + f] > peak) { peak = attn[lastRow + f]; endF = f + 1; }

        var starts = new int[nTok];
        var ends = new int[nTok];
        for (int i = 0; i < nTok; i++) { starts[i] = -1; ends[i] = -1; }
        int ti = nTok - 1, fi = endF;
        while (ti >= 0 && fi >= 1)
        {
            if (ends[ti] < 0) ends[ti] = fi - 1;
            starts[ti] = fi - 1;
            byte d = dir[ti * width + fi];
            if (d == 0) { ti--; fi--; }
            else if (d == 1) ti--;
            else fi--;
        }
        for (int i = 0; i < nTok; i++)
        {
            if (starts[i] < 0) starts[i] = 0;
            if (ends[i] < starts[i]) ends[i] = starts[i];
            result[i] = (starts[i], ends[i]);
        }
        return result;
    }

    /// <summary>
    /// Map token ranges onto characters. A CJK char may be split across several byte-level
    /// tokens (the extra tokens decode to empty text), so those are merged into the character
    /// that completes them.
    /// </summary>
    public static (string Ch, int Start, int End)[] ToCharacters(float[] attn, int nTok, int nFrames, int stride,
        IReadOnlyList<string> pieces, int speechFrames, bool useDtw = false)
    {
        var groups = new List<List<int>>();
        var pending = new List<int>();

        for (int i = 0; i < pieces.Count && i < nTok; i++)
        {
            pending.Add(i);
            if (pieces[i].Length == 0) continue;
            groups.Add(new List<int>(pending));
            pending.Clear();
        }
        if (pending.Count > 0)
        {
            if (groups.Count == 0) groups.Add(new List<int>(pending));
            else groups[^1].AddRange(pending);
        }
        if (groups.Count == 0) return Array.Empty<(string, int, int)>();

        int nChar = groups.Count;
        var charAttn = new float[nChar * stride];
        for (int c = 0; c < nChar; c++)
        {
            var list = groups[c];
            for (int f = 0; f < nFrames; f++)
            {
                float acc = 0f;
                foreach (var t in list) acc += attn[t * stride + f];
                charAttn[c * stride + f] = acc / list.Count;
            }
            float sum = 0f;
            for (int f = 0; f < nFrames; f++) sum += charAttn[c * stride + f];
            if (sum > 0)
                for (int f = 0; f < nFrames; f++) charAttn[c * stride + f] /= sum;
        }

        var spans = Align(charAttn, nChar, nFrames, stride, speechFrames);
        var chars = new (string Ch, int Start, int End)[nChar];
        var text = new System.Text.StringBuilder();
        foreach (var g in groups)
            foreach (var t in g)
                if (t < pieces.Count) text.Append(pieces[t]);
        int c0 = 0;
        for (int c = 0; c < nChar; c++)
        {
            int len = 0;
            foreach (var t in groups[c]) if (t < pieces.Count) len += pieces[t].Length;
            var ch = len > 0 && c0 + len <= text.Length ? text.ToString(c0, len) : "";
            c0 += len;
            chars[c] = (ch, spans[c].Start, spans[c].End);
        }
        return chars;
    }
}
