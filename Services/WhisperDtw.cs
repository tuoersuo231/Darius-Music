using System;
using System.Collections.Generic;

namespace CurrentLyrics.Services;

/// <summary>
/// Turning a cross-attention matrix into per-token times.
/// Row = text token, column = 20 ms encoder frame.
/// </summary>
public static class WhisperDtw
{
    public const int MethodMedian = 0;
    public const int MethodSegmental = 1;

    /// <summary>
    /// Per-token weighted median of the attention row, then forced monotonicity.
    /// Simple and stable, but every token is decided independently, so durations get
    /// flattened (that is why the gap correlation stays low).
    /// </summary>
    public static (int Start, int End)[] AlignMedian(float[] attn, int nTok, int nFrames, int stride)
    {
        var result = new (int Start, int End)[nTok];
        if (nTok <= 0 || nFrames <= 0 || stride < nFrames) return result;

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

    /// <summary>
    /// Segmental DP: every token gets one contiguous run of frames, the runs are monotonic,
    /// they fill the [lo, hi) window, and each run is between minDur and maxDur frames long.
    ///
    /// This is the CTC-style constraint that per-token medians lack: a token cannot collapse
    /// into a single frame, and the total length has to add up. Maximises the attention mass
    /// collected inside each run.
    /// </summary>
    public static (int Start, int End)[] AlignSegmental(float[] attn, int nTok, int nFrames, int stride,
        int lo, int hi, int minDur, int maxDur)
    {
        var result = new (int Start, int End)[nTok];
        if (nTok <= 0 || nFrames <= 0) return result;
        lo = Math.Clamp(lo, 0, Math.Max(0, nFrames - 1));
        hi = Math.Clamp(hi, lo + 1, nFrames);
        int span = hi - lo;
        if (span <= nTok)
        {
            for (int i = 0; i < nTok; i++)
            {
                int s = lo + span * i / nTok;
                int e = Math.Max(s, lo + span * (i + 1) / nTok - 1);
                result[i] = (s, e);
            }
            return result;
        }

        int avg = Math.Max(1, span / nTok);
        if (minDur < 1) minDur = 1;
        if (minDur > avg) minDur = Math.Max(1, avg / 2);
        if (maxDur <= minDur) maxDur = minDur + 1;
        if (maxDur > span) maxDur = span;

        // prefix[t][j] = sum of attention[t] over frames [lo, lo+j)
        var prefix = new double[nTok * (span + 1)];
        for (int t = 0; t < nTok; t++)
        {
            int rb = t * stride;
            int pb = t * (span + 1);
            double acc = 0;
            prefix[pb] = 0;
            for (int f = 0; f < span; f++)
            {
                acc += Math.Max(0f, attn[rb + lo + f]);
                prefix[pb + f + 1] = acc;
            }
        }

        const double Neg = double.NegativeInfinity;
        int rowLen = span + 1;
        var D = new double[(nTok + 1) * rowLen];
        var from = new int[(nTok + 1) * rowLen];
        for (int i = 0; i < D.Length; i++) { D[i] = Neg; from[i] = -1; }
        for (int j = 0; j <= span; j++) D[j] = 0;   // any start is allowed

        for (int t = 1; t <= nTok; t++)
        {
            int prow = (t - 1) * rowLen;
            int crow = t * rowLen;
            int pb = (t - 1) * rowLen;
            int remaining = nTok - t;
            for (int j = 0; j <= span; j++)
            {
                double best = Neg;
                int bestFrom = -1;
                int dHi = Math.Min(maxDur, j);
                for (int d = minDur; d <= dHi; d++)
                {
                    int i2 = j - d;
                    double prev = D[prow + i2];
                    if (prev == Neg) continue;
                    // the tokens that still have to fit need at least minDur each
                    if (i2 + (t - 1) * minDur > span) continue;
                    if (remaining > 0 && j + remaining * minDur > span) continue;
                    double seg = prefix[pb + j] - prefix[pb + i2];
                    double v = prev + seg;
                    if (v > best) { best = v; bestFrom = i2; }
                }
                if (bestFrom >= 0) { D[crow + j] = best; from[crow + j] = bestFrom; }
            }
        }

        int endJ = -1;
        double endV = Neg;
        int lastRow = nTok * rowLen;
        for (int j = 0; j <= span; j++)
            if (D[lastRow + j] > endV) { endV = D[lastRow + j]; endJ = j; }
        if (endJ < 0)
        {
            for (int i = 0; i < nTok; i++)
            {
                int s = lo + span * i / nTok;
                int e = Math.Max(s, lo + span * (i + 1) / nTok - 1);
                result[i] = (s, e);
            }
            return result;
        }

        var cuts = new int[nTok + 1];
        cuts[nTok] = endJ;
        int jj = endJ;
        for (int t = nTok; t >= 1; t--)
        {
            jj = from[t * rowLen + jj];
            if (jj < 0) jj = 0;
            cuts[t - 1] = jj;
        }
        for (int t = 0; t < nTok; t++)
        {
            int s = lo + cuts[t];
            int e = lo + cuts[t + 1] - 1;
            if (e < s) e = s;
            result[t] = (s, e);
        }
        return result;
    }

    /// <summary>
    /// Map token ranges onto characters. A CJK char may be split across several byte-level
    /// tokens (the extra tokens decode to empty text), so those are merged into the character
    /// that completes them.
    /// </summary>
    public static (string Ch, int Start, int End)[] ToCharacters(float[] attn, int nTok, int nFrames, int stride,
        IReadOnlyList<string> pieces, int method, int lo = 0, int hi = -1, int minDur = 2, int maxDur = 60)
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

        var spans = method == MethodSegmental
            ? AlignSegmental(charAttn, nChar, nFrames, stride, lo, hi < 0 ? nFrames : hi, minDur, maxDur)
            : AlignMedian(charAttn, nChar, nFrames, stride);

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
