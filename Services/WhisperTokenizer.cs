using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CurrentLyrics.Services;

/// <summary>
/// Whisper 的 BPE 分词器（GPT-2 byte-level）锛?
/// 歌词鏂囨湰 鈫?UTF-8 瀛楄妭 鈫?byte鈫抲nicode 鏄犲皠 鈫?棰勫垏鍒?鈫?merges 鍚堝苟 鈫?token id銆?
/// </summary>
public sealed class WhisperTokenizer
{
    private readonly Dictionary<string, int> _vocab;
    private readonly Dictionary<string, int> _ranks = new();
    private readonly Dictionary<byte, char> _byteEncoder = new();

    // GPT-2 / Whisper 的预切分（和 tokenizer.json 里的 ByteLevel pattern 等价）
    private static readonly Regex PreToken = new(
        @"'s|'t|'re|'ve|'m|'ll|'d| ?\p{L}+| ?\p{N}+| ?[^\s\p{L}\p{N}]+|\s+(?!\S)|\s+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public int EndOfText { get; private set; } = 50257;
    public int StartOfTranscript { get; private set; } = 50258;
    public int TranslateToken { get; private set; } = 50358;
    public int TranscribeToken { get; private set; } = 50359;
    public int NoTimestampsToken { get; private set; } = 50363;

    private WhisperTokenizer(Dictionary<string, int> vocab)
    {
        _vocab = vocab;
        BuildByteEncoder();
    }

    public static WhisperTokenizer Load(string modelDir)
    {
        var vocabPath = Path.Combine(modelDir, "vocab.json");
        var mergesPath = Path.Combine(modelDir, "merges.txt");
        if (!File.Exists(vocabPath)) throw new FileNotFoundException("whisper vocab.json 涓嶅湪", vocabPath);

        var vocab = JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllText(vocabPath))
                    ?? throw new InvalidDataException("vocab.json 解析失败");

        // 特殊 token（<|ja|> / <|transcribe|> / <|notimestamps|> ...）不在 vocab.json 里，在 added_tokens.json
        var addedPath = Path.Combine(modelDir, "added_tokens.json");
        if (File.Exists(addedPath))
        {
            var added = JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllText(addedPath));
            if (added != null)
                foreach (var kv in added) vocab[kv.Key] = kv.Value;
        }

        var tok = new WhisperTokenizer(vocab);

        if (File.Exists(mergesPath))
        {
            int rank = 0;
            foreach (var line in File.ReadLines(mergesPath))
            {
                var s = line.Trim();
                if (s.Length == 0 || s.StartsWith("#", StringComparison.Ordinal)) continue;
                var parts = s.Split(' ');
                if (parts.Length != 2) continue;
                tok._ranks[parts[0] + "\u0001" + parts[1]] = rank++;
            }
        }

        tok.ResolveSpecialTokens();
        return tok;
    }

    private void ResolveSpecialTokens()
    {
        if (_vocab.TryGetValue("<|endoftext|>", out var eot)) EndOfText = eot;
        if (_vocab.TryGetValue("<|startoftranscript|>", out var sot)) StartOfTranscript = sot;
        if (_vocab.TryGetValue("<|translate|>", out var tr)) TranslateToken = tr;
        if (_vocab.TryGetValue("<|transcribe|>", out var tc)) TranscribeToken = tc;
        if (_vocab.TryGetValue("<|notimestamps|>", out var nt)) NoTimestampsToken = nt;
    }

    /// <summary>语言 token，比如 "ja" 鈫?&lt;|ja|&gt; 鐨?id锛涙病鏈夎繑鍥?-1銆?/summary>
    public int LanguageToken(string lang)
    {
        return _vocab.TryGetValue($"<|{lang}|>", out var id) ? id : -1;
    }

    private void BuildByteEncoder()
    {
        var bs = new List<int>();
        for (int i = '!'; i <= '~'; i++) bs.Add(i);
        for (int i = '\u00a1'; i <= '\u00ac'; i++) bs.Add(i);
        for (int i = '\u00ae'; i <= '\u00ff'; i++) bs.Add(i);
        var cs = new List<int>(bs);
        int n = 0;
        for (int b = 0; b < 256; b++)
        {
            if (bs.Contains(b)) continue;
            bs.Add(b);
            cs.Add(256 + n);
            n++;
        }
        for (int i = 0; i < bs.Count; i++) _byteEncoder[(byte)bs[i]] = (char)cs[i];
    }

    private string MapBytes(ReadOnlySpan<byte> bytes)
    {
        var sb = new StringBuilder(bytes.Length);
        foreach (var b in bytes) sb.Append(_byteEncoder[b]);
        return sb.ToString();
    }

    private List<string> Bpe(string word)
    {
        if (word.Length == 1) return new List<string> { word };
        var parts = new List<string>(word.Length);
        foreach (var ch in word) parts.Add(ch.ToString());

        while (parts.Count > 1)
        {
            int bestRank = int.MaxValue;
            int bestIndex = -1;
            for (int i = 0; i < parts.Count - 1; i++)
            {
                var key = parts[i] + "\u0001" + parts[i + 1];
                if (_ranks.TryGetValue(key, out var r) && r < bestRank)
                {
                    bestRank = r;
                    bestIndex = i;
                }
            }
            if (bestIndex < 0) break;

            var merged = parts[bestIndex] + parts[bestIndex + 1];
            var next = new List<string>(parts.Count - 1);
            for (int i = 0; i < parts.Count; i++)
            {
                if (i == bestIndex)
                {
                    next.Add(merged);
                    i++;
                }
                else next.Add(parts[i]);
            }
            parts = next;
        }
        return parts;
    }

    /// <summary>Encode text to token ids (no special tokens).</summary>
    public List<int> Encode(string text)
    {
        var ids = new List<int>();
        foreach (var piece in EncodePieces(text)) ids.Add(piece.Id);
        return ids;
    }

    /// <summary>Per-token source text. CJK chars are often split across several
    /// byte-level tokens, so decoding must be incremental UTF-8.</summary>
    public List<string> EncodeToPieces(string text)
    {
        var result = new List<string>();
        var decoder = Encoding.UTF8.GetDecoder();
        foreach (var piece in EncodePieces(text))
        {
            int n = decoder.GetCharCount(piece.Bytes, 0, piece.Bytes.Length, false);
            var chars = new char[n];
            decoder.GetChars(piece.Bytes, 0, piece.Bytes.Length, chars, 0, false);
            result.Add(new string(chars));
        }
        return result;
    }

    private readonly struct Piece
    {
        public readonly int Id;
        public readonly byte[] Bytes;
        public Piece(int id, byte[] bytes) { Id = id; Bytes = bytes; }
    }

    private List<Piece> EncodePieces(string text)
    {
        var result = new List<Piece>();
        if (string.IsNullOrEmpty(text)) return result;
        var inv = BuildByteDecoder();
        var bytes = Encoding.UTF8.GetBytes(text);
        var mapped = MapBytes(bytes);
        foreach (Match m in PreToken.Matches(mapped))
        {
            foreach (var piece in Bpe(m.Value))
            {
                if (!_vocab.TryGetValue(piece, out var id)) continue;
                var raw = new byte[piece.Length];
                for (int i = 0; i < piece.Length; i++)
                    raw[i] = inv.TryGetValue(piece[i], out var b) ? b : (byte)' ';
                result.Add(new Piece(id, raw));
            }
        }
        return result;
    }

    private Dictionary<char, byte>? _byteDecoderCache;

    private Dictionary<char, byte> BuildByteDecoder()
    {
        if (_byteDecoderCache != null) return _byteDecoderCache;
        var inv = new Dictionary<char, byte>();
        foreach (var kv in _byteEncoder) inv[kv.Value] = kv.Key;
        _byteDecoderCache = inv;
        return inv;
    }

    private readonly Dictionary<int, string> _idToPiece = new();

    /// <summary>Decode token ids back to text (special tokens are skipped).</summary>
    public string Decode(IEnumerable<int> ids)
    {
        if (_idToPiece.Count == 0)
            foreach (var kv in _vocab) _idToPiece[kv.Value] = kv.Key;
        var inv = BuildByteDecoder();
        var decoder = Encoding.UTF8.GetDecoder();
        var sb = new StringBuilder();
        foreach (var id in ids)
        {
            if (!_idToPiece.TryGetValue(id, out var piece)) continue;
            var raw = new byte[piece.Length];
            for (int i = 0; i < piece.Length; i++)
                raw[i] = inv.TryGetValue(piece[i], out var b) ? b : (byte)' ';
            int n = decoder.GetCharCount(raw, 0, raw.Length, false);
            var chars = new char[n];
            decoder.GetChars(raw, 0, raw.Length, chars, 0, false);
            sb.Append(chars);
        }
        return sb.ToString();
    }
}
