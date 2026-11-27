using System.IO;
using System.IO.Compression;
using System.Text;

namespace CurrentLyrics.Services;

/// <summary>
/// 腾讯 QRC 歌词解码：hex 字符串 → 3DES(EDE3, ECB) → zlib → UTF-8。
///
/// <para>这里<b>不能</b>用 <see cref="System.Security.Cryptography.TripleDES"/>，也不要用教科书 DES：
/// QRC 用的是从原 C++ 插件移植过来的变体（S 盒有两处不同，而且按小端 32 位字取位），标准实现解出来
/// 是别的字节（试过：首字节 C8，inflate 直接报 incorrect header）。下面这份是把 lx-music 的
/// <c>qrcDecode.js</c> 逐函数搬过来的，位运算顺序与它完全一致 —— 只有这种写法能解出正确文本。</para>
/// </summary>
public static class QrcCodec
{
    /// <summary>QRC 固定密钥 <c>!@#)(*$%123ZXC!@!@#)(NHL</c>（24 字节）。</summary>
    private static readonly byte[] Key =
    {
        0x21, 0x40, 0x23, 0x29, 0x28, 0x2a, 0x24, 0x25, 0x31, 0x32, 0x33, 0x5a,
        0x58, 0x43, 0x21, 0x40, 0x21, 0x40, 0x23, 0x29, 0x28, 0x4e, 0x48, 0x4c,
    };

    /// <summary>八个 S 盒，每个 4 行 × 16 列；S2/S4 与标准 DES 不同，别换。</summary>
    private static readonly byte[][] SBox =
    {
        new byte[]
        {
            14, 4, 13, 1, 2, 15, 11, 8, 3, 10, 6, 12, 5, 9, 0, 7,
            0, 15, 7, 4, 14, 2, 13, 1, 10, 6, 12, 11, 9, 5, 3, 8,
            4, 1, 14, 8, 13, 6, 2, 11, 15, 12, 9, 7, 3, 10, 5, 0,
            15, 12, 8, 2, 4, 9, 1, 7, 5, 11, 3, 14, 10, 0, 6, 13,
        },
        new byte[]
        {
            15, 1, 8, 14, 6, 11, 3, 4, 9, 7, 2, 13, 12, 0, 5, 10,
            3, 13, 4, 7, 15, 2, 8, 15, 12, 0, 1, 10, 6, 9, 11, 5,
            0, 14, 7, 11, 10, 4, 13, 1, 5, 8, 12, 6, 9, 3, 2, 15,
            13, 8, 10, 1, 3, 15, 4, 2, 11, 6, 7, 12, 0, 5, 14, 9,
        },
        new byte[]
        {
            10, 0, 9, 14, 6, 3, 15, 5, 1, 13, 12, 7, 11, 4, 2, 8,
            13, 7, 0, 9, 3, 4, 6, 10, 2, 8, 5, 14, 12, 11, 15, 1,
            13, 6, 4, 9, 8, 15, 3, 0, 11, 1, 2, 12, 5, 10, 14, 7,
            1, 10, 13, 0, 6, 9, 8, 7, 4, 15, 14, 3, 11, 5, 2, 12,
        },
        new byte[]
        {
            7, 13, 14, 3, 0, 6, 9, 10, 1, 2, 8, 5, 11, 12, 4, 15,
            13, 8, 11, 5, 6, 15, 0, 3, 4, 7, 2, 12, 1, 10, 14, 9,
            10, 6, 9, 0, 12, 11, 7, 13, 15, 1, 3, 14, 5, 2, 8, 4,
            3, 15, 0, 6, 10, 10, 13, 8, 9, 4, 5, 11, 12, 7, 2, 14,
        },
        new byte[]
        {
            2, 12, 4, 1, 7, 10, 11, 6, 8, 5, 3, 15, 13, 0, 14, 9,
            14, 11, 2, 12, 4, 7, 13, 1, 5, 0, 15, 10, 3, 9, 8, 6,
            4, 2, 1, 11, 10, 13, 7, 8, 15, 9, 12, 5, 6, 3, 0, 14,
            11, 8, 12, 7, 1, 14, 2, 13, 6, 15, 0, 9, 10, 4, 5, 3,
        },
        new byte[]
        {
            12, 1, 10, 15, 9, 2, 6, 8, 0, 13, 3, 4, 14, 7, 5, 11,
            10, 15, 4, 2, 7, 12, 9, 5, 6, 1, 13, 14, 0, 11, 3, 8,
            9, 14, 15, 5, 2, 8, 12, 3, 7, 0, 4, 10, 1, 13, 11, 6,
            4, 3, 2, 12, 9, 5, 15, 10, 11, 14, 1, 7, 6, 0, 8, 13,
        },
        new byte[]
        {
            4, 11, 2, 14, 15, 0, 8, 13, 3, 12, 9, 7, 5, 10, 6, 1,
            13, 0, 11, 7, 4, 9, 1, 10, 14, 3, 5, 12, 2, 15, 8, 6,
            1, 4, 11, 13, 12, 3, 7, 14, 10, 15, 6, 8, 0, 5, 9, 2,
            6, 11, 13, 8, 1, 4, 10, 7, 9, 5, 0, 15, 14, 2, 3, 12,
        },
        new byte[]
        {
            13, 2, 8, 4, 6, 15, 11, 1, 10, 9, 3, 14, 5, 0, 12, 7,
            1, 15, 13, 8, 10, 3, 7, 4, 12, 5, 6, 11, 0, 14, 9, 2,
            7, 11, 4, 1, 9, 12, 14, 2, 0, 6, 10, 13, 15, 3, 5, 8,
            2, 1, 14, 7, 4, 10, 8, 13, 15, 12, 9, 0, 3, 5, 6, 11,
        },
    };

    private static readonly int[] KeyRndShift = { 1, 1, 2, 2, 2, 2, 2, 2, 1, 2, 2, 2, 2, 2, 2, 1 };

    private static readonly int[] KeyPermC =
    {
        56, 48, 40, 32, 24, 16, 8, 0, 57, 49, 41, 33, 25, 17, 9, 1,
        58, 50, 42, 34, 26, 18, 10, 2, 59, 51, 43, 35,
    };

    private static readonly int[] KeyPermD =
    {
        62, 54, 46, 38, 30, 22, 14, 6, 61, 53, 45, 37, 29, 21, 13, 5,
        60, 52, 44, 36, 28, 20, 12, 4, 27, 19, 11, 3,
    };

    private static readonly int[] KeyCompression =
    {
        13, 16, 10, 23, 0, 4, 2, 27, 14, 5, 20, 9, 22, 18, 11, 3, 25, 7, 15, 6, 26, 19, 12, 1,
        40, 51, 30, 36, 46, 54, 29, 39, 50, 44, 32, 47, 43, 48, 38, 55, 33, 52, 45, 41, 49, 35, 28, 31,
    };

    /// <summary>Decodes one QRC hex payload; empty string when the input is not a QRC blob.</summary>
    public static string Decode(string? hex) => Inflate(Decrypt(hex));

    /// <summary>The 3DES layer alone (exposed for the QA decoder probe).</summary>
    public static byte[] Decrypt(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return Array.Empty<byte>();
        hex = hex.Trim();
        if (hex.Length % 2 != 0) return Array.Empty<byte>();

        byte[] data;
        try { data = Convert.FromHexString(hex); }
        catch { return Array.Empty<byte>(); }
        if (data.Length < 8) return Array.Empty<byte>();

        var schedule = TripleDesKeySetup(Key, decrypt: true);
        int usable = data.Length - data.Length % 8;
        var plain = new byte[usable];
        var buffer = new byte[8];
        var current = new byte[8];
        for (int i = 0; i < usable; i += 8)
        {
            Array.Copy(data, i, current, 0, 8);
            TripleDesCrypt(current, schedule, buffer);
            Array.Copy(buffer, 0, plain, i, 8);
        }
        return plain;
    }

    /// <summary>zlib inflate that keeps whatever it got: QRC 的尾巴经常是不完整的流（对应上游的
    /// Z_SYNC_FLUSH），整段丢弃会把歌词全丢掉。</summary>
    private static string Inflate(byte[] data)
    {
        if (data.Length == 0) return "";
        try
        {
            using var input = new MemoryStream(data);
            using var inflate = new ZLibStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            var chunk = new byte[8192];
            try
            {
                int read;
                while ((read = inflate.Read(chunk, 0, chunk.Length)) > 0) output.Write(chunk, 0, read);
            }
            catch { }
            if (output.Length == 0) return "";
            return Encoding.UTF8.GetString(output.ToArray());
        }
        catch
        {
            return "";
        }
    }

    // ---------- DES, transcribed from lx-music's qrcDecode.js ----------

    /// <summary>Reads bit <paramref name="b"/> of the 64-bit window over <paramref name="a"/> into place
    /// <paramref name="c"/>; the byte order is the little-endian-word layout the original plugin used.</summary>
    private static uint BitNum(byte[] a, int b, int c)
    {
        int index = (b / 32) * 4 + 3 - (b % 32) / 8;
        return (uint)(((a[index] >> (7 - b % 8)) & 1) << c);
    }

    private static uint BitNumIntr(uint a, int b, int c) => ((a >> (31 - b)) & 1U) << c;

    private static uint BitNumIntl(uint a, int b, int c) => ((a << b) & 0x80000000U) >> c;

    private static int SboxBit(int a) => (a & 32) | ((a & 31) >> 1) | ((a & 1) << 4);

    private static (uint s0, uint s1) InitialPermutation(byte[] input)
    {
        uint s0 = BitNum(input, 57, 31) | BitNum(input, 49, 30) | BitNum(input, 41, 29) | BitNum(input, 33, 28) |
                  BitNum(input, 25, 27) | BitNum(input, 17, 26) | BitNum(input, 9, 25) | BitNum(input, 1, 24) |
                  BitNum(input, 59, 23) | BitNum(input, 51, 22) | BitNum(input, 43, 21) | BitNum(input, 35, 20) |
                  BitNum(input, 27, 19) | BitNum(input, 19, 18) | BitNum(input, 11, 17) | BitNum(input, 3, 16) |
                  BitNum(input, 61, 15) | BitNum(input, 53, 14) | BitNum(input, 45, 13) | BitNum(input, 37, 12) |
                  BitNum(input, 29, 11) | BitNum(input, 21, 10) | BitNum(input, 13, 9) | BitNum(input, 5, 8) |
                  BitNum(input, 63, 7) | BitNum(input, 55, 6) | BitNum(input, 47, 5) | BitNum(input, 39, 4) |
                  BitNum(input, 31, 3) | BitNum(input, 23, 2) | BitNum(input, 15, 1) | BitNum(input, 7, 0);
        uint s1 = BitNum(input, 56, 31) | BitNum(input, 48, 30) | BitNum(input, 40, 29) | BitNum(input, 32, 28) |
                  BitNum(input, 24, 27) | BitNum(input, 16, 26) | BitNum(input, 8, 25) | BitNum(input, 0, 24) |
                  BitNum(input, 58, 23) | BitNum(input, 50, 22) | BitNum(input, 42, 21) | BitNum(input, 34, 20) |
                  BitNum(input, 26, 19) | BitNum(input, 18, 18) | BitNum(input, 10, 17) | BitNum(input, 2, 16) |
                  BitNum(input, 60, 15) | BitNum(input, 52, 14) | BitNum(input, 44, 13) | BitNum(input, 36, 12) |
                  BitNum(input, 28, 11) | BitNum(input, 20, 10) | BitNum(input, 12, 9) | BitNum(input, 4, 8) |
                  BitNum(input, 62, 7) | BitNum(input, 54, 6) | BitNum(input, 46, 5) | BitNum(input, 38, 4) |
                  BitNum(input, 30, 3) | BitNum(input, 22, 2) | BitNum(input, 14, 1) | BitNum(input, 6, 0);
        return (s0, s1);
    }

    private static void InversePermutation(uint s0, uint s1, byte[] output)
    {
        output[3] = (byte)(BitNumIntr(s1, 7, 7) | BitNumIntr(s0, 7, 6) | BitNumIntr(s1, 15, 5) | BitNumIntr(s0, 15, 4) |
                           BitNumIntr(s1, 23, 3) | BitNumIntr(s0, 23, 2) | BitNumIntr(s1, 31, 1) | BitNumIntr(s0, 31, 0));
        output[2] = (byte)(BitNumIntr(s1, 6, 7) | BitNumIntr(s0, 6, 6) | BitNumIntr(s1, 14, 5) | BitNumIntr(s0, 14, 4) |
                           BitNumIntr(s1, 22, 3) | BitNumIntr(s0, 22, 2) | BitNumIntr(s1, 30, 1) | BitNumIntr(s0, 30, 0));
        output[1] = (byte)(BitNumIntr(s1, 5, 7) | BitNumIntr(s0, 5, 6) | BitNumIntr(s1, 13, 5) | BitNumIntr(s0, 13, 4) |
                           BitNumIntr(s1, 21, 3) | BitNumIntr(s0, 21, 2) | BitNumIntr(s1, 29, 1) | BitNumIntr(s0, 29, 0));
        output[0] = (byte)(BitNumIntr(s1, 4, 7) | BitNumIntr(s0, 4, 6) | BitNumIntr(s1, 12, 5) | BitNumIntr(s0, 12, 4) |
                           BitNumIntr(s1, 20, 3) | BitNumIntr(s0, 20, 2) | BitNumIntr(s1, 28, 1) | BitNumIntr(s0, 28, 0));
        output[7] = (byte)(BitNumIntr(s1, 3, 7) | BitNumIntr(s0, 3, 6) | BitNumIntr(s1, 11, 5) | BitNumIntr(s0, 11, 4) |
                           BitNumIntr(s1, 19, 3) | BitNumIntr(s0, 19, 2) | BitNumIntr(s1, 27, 1) | BitNumIntr(s0, 27, 0));
        output[6] = (byte)(BitNumIntr(s1, 2, 7) | BitNumIntr(s0, 2, 6) | BitNumIntr(s1, 10, 5) | BitNumIntr(s0, 10, 4) |
                           BitNumIntr(s1, 18, 3) | BitNumIntr(s0, 18, 2) | BitNumIntr(s1, 26, 1) | BitNumIntr(s0, 26, 0));
        output[5] = (byte)(BitNumIntr(s1, 1, 7) | BitNumIntr(s0, 1, 6) | BitNumIntr(s1, 9, 5) | BitNumIntr(s0, 9, 4) |
                           BitNumIntr(s1, 17, 3) | BitNumIntr(s0, 17, 2) | BitNumIntr(s1, 25, 1) | BitNumIntr(s0, 25, 0));
        output[4] = (byte)(BitNumIntr(s1, 0, 7) | BitNumIntr(s0, 0, 6) | BitNumIntr(s1, 8, 5) | BitNumIntr(s0, 8, 4) |
                           BitNumIntr(s1, 16, 3) | BitNumIntr(s0, 16, 2) | BitNumIntr(s1, 24, 1) | BitNumIntr(s0, 24, 0));
    }

    private static uint DesF(uint state, byte[] key)
    {
        uint t1 = BitNumIntl(state, 31, 0) | ((state & 0xF0000000U) >> 1) | BitNumIntl(state, 4, 5) |
                  BitNumIntl(state, 3, 6) | ((state & 0x0F000000U) >> 3) | BitNumIntl(state, 8, 11) |
                  BitNumIntl(state, 7, 12) | ((state & 0x00F00000U) >> 5) | BitNumIntl(state, 12, 17) |
                  BitNumIntl(state, 11, 18) | ((state & 0x000F0000U) >> 7) | BitNumIntl(state, 16, 23);

        uint t2 = BitNumIntl(state, 15, 0) | ((state & 0x0000F000U) << 15) | BitNumIntl(state, 20, 5) |
                  BitNumIntl(state, 19, 6) | ((state & 0x00000F00U) << 13) | BitNumIntl(state, 24, 11) |
                  BitNumIntl(state, 23, 12) | ((state & 0x000000F0U) << 11) | BitNumIntl(state, 28, 17) |
                  BitNumIntl(state, 27, 18) | ((state & 0x0000000FU) << 9) | BitNumIntl(state, 0, 23);

        var lrg = new int[6];
        lrg[0] = (int)((t1 >> 24) & 0xFF);
        lrg[1] = (int)((t1 >> 16) & 0xFF);
        lrg[2] = (int)((t1 >> 8) & 0xFF);
        lrg[3] = (int)((t2 >> 24) & 0xFF);
        lrg[4] = (int)((t2 >> 16) & 0xFF);
        lrg[5] = (int)((t2 >> 8) & 0xFF);
        for (int i = 0; i < 6; i++) lrg[i] ^= key[i];

        uint s = ((uint)SBox[0][SboxBit(lrg[0] >> 2)] << 28) |
                 ((uint)SBox[1][SboxBit(((lrg[0] & 0x03) << 4) | (lrg[1] >> 4))] << 24) |
                 ((uint)SBox[2][SboxBit(((lrg[1] & 0x0F) << 2) | (lrg[2] >> 6))] << 20) |
                 ((uint)SBox[3][SboxBit(lrg[2] & 0x3F)] << 16) |
                 ((uint)SBox[4][SboxBit(lrg[3] >> 2)] << 12) |
                 ((uint)SBox[5][SboxBit(((lrg[3] & 0x03) << 4) | (lrg[4] >> 4))] << 8) |
                 ((uint)SBox[6][SboxBit(((lrg[4] & 0x0F) << 2) | (lrg[5] >> 6))] << 4) |
                 SBox[7][SboxBit(lrg[5] & 0x3F)];

        return BitNumIntl(s, 15, 0) | BitNumIntl(s, 6, 1) | BitNumIntl(s, 19, 2) | BitNumIntl(s, 20, 3) |
               BitNumIntl(s, 28, 4) | BitNumIntl(s, 11, 5) | BitNumIntl(s, 27, 6) | BitNumIntl(s, 16, 7) |
               BitNumIntl(s, 0, 8) | BitNumIntl(s, 14, 9) | BitNumIntl(s, 22, 10) | BitNumIntl(s, 25, 11) |
               BitNumIntl(s, 4, 12) | BitNumIntl(s, 17, 13) | BitNumIntl(s, 30, 14) | BitNumIntl(s, 9, 15) |
               BitNumIntl(s, 1, 16) | BitNumIntl(s, 7, 17) | BitNumIntl(s, 23, 18) | BitNumIntl(s, 13, 19) |
               BitNumIntl(s, 31, 20) | BitNumIntl(s, 26, 21) | BitNumIntl(s, 2, 22) | BitNumIntl(s, 8, 23) |
               BitNumIntl(s, 18, 24) | BitNumIntl(s, 12, 25) | BitNumIntl(s, 29, 26) | BitNumIntl(s, 5, 27) |
               BitNumIntl(s, 21, 28) | BitNumIntl(s, 10, 29) | BitNumIntl(s, 3, 30) | BitNumIntl(s, 24, 31);
    }

    private static void DesCrypt(byte[] input, byte[][] schedule, byte[] output)
    {
        var (s0, s1) = InitialPermutation(input);
        for (int i = 0; i < 15; i++)
        {
            uint previous = s1;
            s1 = DesF(s1, schedule[i]) ^ s0;
            s0 = previous;
        }
        s0 = DesF(s1, schedule[15]) ^ s0;
        InversePermutation(s0, s1, output);
    }

    private static byte[][] KeySchedule(byte[] key, bool decrypt)
    {
        var schedule = new byte[16][];
        for (int i = 0; i < 16; i++) schedule[i] = new byte[6];

        uint c = 0, d = 0;
        for (int i = 0; i < 28; i++)
        {
            c |= BitNum(key, KeyPermC[i], 31 - i);
            d |= BitNum(key, KeyPermD[i], 31 - i);
        }
        for (int i = 0; i < 16; i++)
        {
            int shift = KeyRndShift[i];
            c = ((c << shift) | (c >> (28 - shift))) & 0xFFFFFFF0U;
            d = ((d << shift) | (d >> (28 - shift))) & 0xFFFFFFF0U;
            int target = decrypt ? 15 - i : i;
            for (int j = 0; j < 24; j++)
                schedule[target][j / 8] |= (byte)BitNumIntr(c, KeyCompression[j], 7 - j % 8);
            for (int j = 24; j < 48; j++)
                schedule[target][j / 8] |= (byte)BitNumIntr(d, KeyCompression[j] - 27, 7 - j % 8);
        }
        return schedule;
    }

    private static byte[][][] TripleDesKeySetup(byte[] key, bool decrypt)
    {
        if (!decrypt)
        {
            return new[]
            {
                KeySchedule(key[..8], decrypt: false),
                KeySchedule(key[8..16], decrypt: true),
                KeySchedule(key[16..24], decrypt: false),
            };
        }
        return new[]
        {
            KeySchedule(key[16..24], decrypt: true),
            KeySchedule(key[8..16], decrypt: false),
            KeySchedule(key[0..8], decrypt: true),
        };
    }

    private static void TripleDesCrypt(byte[] input, byte[][][] schedule, byte[] output)
    {
        var buffer = new byte[8];
        DesCrypt(input, schedule[0], buffer);
        DesCrypt(buffer, schedule[1], output);
        DesCrypt(output, schedule[2], buffer);
        Array.Copy(buffer, 0, output, 0, 8);
    }
}
