namespace CurrentLyrics.Services;

/// <summary>CTC 强制对齐（Viterbi）。给定逐帧 logit 和"这一行该唱哪些 token"，
/// 找一条只能前进的路径，把每个 token 钉到具体的帧上。
///
/// 用原始 logit 就够：每帧所有路径都恰好取一个发射值，逐帧常数不改变路径排序，
/// 所以不必先做 log_softmax（也就不必保留完整的 25055 维）。</summary>
public static class ForceAlign
{
    public readonly struct Span
    {
        public readonly int StartFrame;
        public readonly int EndFrame;
        public Span(int start, int end) { StartFrame = start; EndFrame = end; }
    }

    /// <summary>logits: 行优先 [frames, cols]；blankCol 是 blank 的列；tokenIds 是这行 token 的真实 id，
    /// 它们按顺序放在 1..N 列（0 列留给 blank）。返回每个 token 的帧区间；不可达时返回空数组。</summary>
    public static Span[] Align(float[] logits, int frames, int cols, int blankCol, int[] tokenIds)
    {
        int n = tokenIds.Length;
        if (n == 0 || frames <= 0) return Array.Empty<Span>();

        int states = 2 * n + 1;
        var prev = new float[states];
        var cur = new float[states];
        Array.Fill(prev, float.NegativeInfinity);

        prev[0] = logits[blankCol];
        prev[1] = logits[1];

        var back = new int[frames * states];

        for (int t = 1; t < frames; t++)
        {
            int row = t * cols;
            int backRow = t * states;
            Array.Fill(cur, float.NegativeInfinity);

            for (int s = 0; s < states; s++)
            {
                float emission = (s % 2 == 0) ? logits[row + blankCol] : logits[row + (s + 1) / 2];

                float best = prev[s];
                int from = s;

                if (s > 0 && prev[s - 1] > best) { best = prev[s - 1]; from = s - 1; }

                // 跳过一个 blank：两个相邻 token 不同时才允许（相同 token 中间必须有 blank）
                if (s > 1 && s % 2 == 1)
                {
                    int token = (s - 1) / 2;
                    if (tokenIds[token] != tokenIds[token - 1] && prev[s - 2] > best)
                    {
                        best = prev[s - 2];
                        from = s - 2;
                    }
                }

                back[backRow + s] = from;
                cur[s] = best + emission;
            }

            (prev, cur) = (cur, prev);
        }

        int finalState = states - 1;
        if (prev[states - 2] > prev[finalState]) finalState = states - 2;
        if (!float.IsFinite(prev[finalState]) || prev[finalState] < -1e30f) return Array.Empty<Span>();

        var path = new int[frames];
        int stateNow = finalState;
        for (int t = frames - 1; t >= 0; t--)
        {
            path[t] = stateNow;
            stateNow = back[t * states + stateNow];
        }

        var spans = new Span[n];
        var seen = new bool[n];
        for (int t = 0; t < frames; t++)
        {
            int s = path[t];
            if (s % 2 == 0) continue;
            int token = (s - 1) / 2;
            if (!seen[token])
            {
                seen[token] = true;
                spans[token] = new Span(t, t);
            }
            else
            {
                spans[token] = new Span(spans[token].StartFrame, t);
            }
        }
        return spans;
    }
}
