using System.IO;
using System.Text;
using System.Windows.Media.Imaging;
using CurrentLyrics.Models;

namespace CurrentLyrics.Services;

/// <summary>Dependency-free tag reader for the formats a desktop library actually contains:
/// ID3v2 (mp3), FLAC (Vorbis comments + PICTURE) and ID3v1. Everything else falls back to
/// the file name so the library never shows an empty row.</summary>
public static class TagReader
{
    private static readonly string[] AudioExtensions =
        { ".mp3", ".flac", ".m4a", ".wav", ".ogg", ".aac", ".wma", ".opus", ".aiff", ".ape" };

    public static bool IsAudio(string path) =>
        AudioExtensions.Contains(System.IO.Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    public static Song Read(string path)
    {
        var song = new Song { Path = path };
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 64 * 1024);
            Span<byte> head = stackalloc byte[12];
            int read = fs.Read(head);
            fs.Position = 0;
            if (read >= 10 && head[0] == 'I' && head[1] == 'D' && head[2] == '3')
                ReadId3v2(fs, song);
            else if (read >= 4 && head[0] == 'f' && head[1] == 'L' && head[2] == 'a' && head[3] == 'C')
                ReadFlac(fs, song);
            else if (read >= 4 && head[0] == 'O' && head[1] == 'g' && head[2] == 'g' && head[3] == 'S')
                ReadOgg(fs, song);
            else if (read >= 4 && head[0] == 'R' && head[1] == 'I' && head[2] == 'F' && head[3] == 'F')
                ReadWav(fs, song);
            else if (read >= 12 && head[0] == 'F' && head[1] == 'O' && head[2] == 'R' && head[3] == 'M')
                ReadAiff(fs, song);
            else if (read >= 12 && head[4] == 'f' && head[5] == 't' && head[6] == 'y' && head[7] == 'p')
                ReadMp4(fs, song);
            else if (read >= 4 && head[0] == 'M' && head[1] == 'A' && head[2] == 'C' && head[3] == ' ')
                ReadApe(fs, song);
            else if (read >= 16 && head[0] == 0x30 && head[1] == 0x26 && head[2] == 0xB2 && head[3] == 0x75)
                ReadAsf(fs, song);

            if (string.IsNullOrWhiteSpace(song.Title) || string.IsNullOrWhiteSpace(song.Artist))
                ReadId3v1(fs, song);
        }
        catch (Exception ex)
        {
            // A damaged file must never take the whole scan down.
            try
            {
                System.IO.File.AppendAllText(
                    System.IO.Path.Combine(AppPaths.DataDir, "tag-errors.log"),
                    $"{path} :: {ex.GetType().Name}: {ex.Message}\n");
            }
            catch { }
        }

        if (string.IsNullOrWhiteSpace(song.Title)) ParseFromFileName(path, song);
        if (song.Cover == null) song.Cover = LoadSidecarCover(path);
        return song;
    }

    /// <summary>
    /// Many formats carry no embedded art (wav, wma, ape, some m4a/ogg). Look for the usual sidecar
    /// images next to the track: "<name>.jpg", "<name>.png", "cover.jpg", "folder.jpg", "front.png".
    /// </summary>
    private static BitmapSource? LoadSidecarCover(string audioPath)
    {
        try
        {
            var dir = System.IO.Path.GetDirectoryName(audioPath);
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return null;
            var stem = System.IO.Path.GetFileNameWithoutExtension(audioPath);
            string[] names = { stem, "cover", "folder", "front", "album" };
            string[] exts = { ".jpg", ".jpeg", ".png", ".webp" };
            foreach (var name in names)
            {
                foreach (var ext in exts)
                {
                    var candidate = System.IO.Path.Combine(dir, name + ext);
                    if (!File.Exists(candidate)) continue;
                    var info = new FileInfo(candidate);
                    if (info.Length is 0 or > 12_000_000) continue;
                    var image = MakeImage(File.ReadAllBytes(candidate));
                    if (image != null) return image;
                }
            }
        }
        catch { }
        return null;
    }

    private static void ParseFromFileName(string path, Song song)
    {
        var name = System.IO.Path.GetFileNameWithoutExtension(path);
        var dash = name.IndexOf(" - ", StringComparison.Ordinal);
        if (dash > 0)
        {
            song.Artist = name[..dash].Trim();
            song.Title = name[(dash + 3)..].Trim();
        }
        else
        {
            song.Title = name.Trim();
        }
    }

    // ---------- ID3v2 ----------
    private static void ReadId3v2(FileStream fs, Song song)
    {
        var header = new byte[10];
        fs.ReadExactly(header, 0, 10);
        int major = header[3];
        bool unsync = (header[5] & 0x80) != 0;
        int size = (header[6] & 0x7F) << 21 | (header[7] & 0x7F) << 14 | (header[8] & 0x7F) << 7 | (header[9] & 0x7F);
        var data = new byte[size];
        fs.ReadExactly(data, 0, size);
        if (unsync) data = RemoveUnsync(data);

        int pos = 0;
        int extended = 0;
        if ((header[5] & 0x40) != 0)
        {
            if (major >= 4) extended = (data[0] & 0x7F) << 21 | (data[1] & 0x7F) << 14 | (data[2] & 0x7F) << 7 | (data[3] & 0x7F);
            else extended = (data[0] << 24 | data[1] << 16 | data[2] << 8 | data[3]) + 4;
            pos = extended;
        }

        while (pos + 10 <= data.Length)
        {
            var id = Encoding.ASCII.GetString(data, pos, 4);
            if (id[0] == '\0' || !char.IsLetterOrDigit(id[0])) break;
            int frameSize = major >= 4
                ? (data[pos + 4] & 0x7F) << 21 | (data[pos + 5] & 0x7F) << 14 | (data[pos + 6] & 0x7F) << 7 | (data[pos + 7] & 0x7F)
                : data[pos + 4] << 24 | data[pos + 5] << 16 | data[pos + 6] << 8 | data[pos + 7];
            if (frameSize <= 0 || pos + 10 + frameSize > data.Length) break;
            var body = new ReadOnlySpan<byte>(data, pos + 10, frameSize);
            switch (id)
            {
                case "TIT2": song.Title = DecodeText(body); break;
                case "TPE1": song.Artist = DecodeText(body); break;
                case "TALB": song.Album = DecodeText(body); break;
                case "TLEN":
                    if (long.TryParse(DecodeText(body), out var ms) && ms > 0) song.Duration = TimeSpan.FromMilliseconds(ms);
                    break;
                case "APIC":
                    var cover = DecodeApic(body);
                    if (cover != null && song.Cover == null) song.Cover = cover;
                    break;
            }

            pos += 10 + frameSize;
        }
    }

    private static byte[] RemoveUnsync(byte[] data)
    {
        var outBuf = new List<byte>(data.Length);
        for (int i = 0; i < data.Length; i++)
        {
            outBuf.Add(data[i]);
            if (data[i] == 0xFF && i + 1 < data.Length && data[i + 1] == 0x00) i++;
        }
        return outBuf.ToArray();
    }

    private static string DecodeText(ReadOnlySpan<byte> body)
    {
        if (body.Length == 0) return "";
        byte enc = body[0];
        var payload = body[1..];
        string text = enc switch
        {
            0 => Encoding.Latin1.GetString(payload),
            1 => Encoding.Unicode.GetString(payload),
            2 => Encoding.BigEndianUnicode.GetString(payload),
            3 => Encoding.UTF8.GetString(payload),
            _ => Encoding.UTF8.GetString(payload)
        };
        return text.Trim('\0', ' ', '\r', '\n');
    }

    private static BitmapSource? DecodeApic(ReadOnlySpan<byte> body)
    {
        try
        {
            if (body.Length < 4) return null;
            byte enc = body[0];
            int p = 1;
            while (p < body.Length && body[p] != 0) p++;   // mime
            p++;
            if (p >= body.Length) return null;
            p++;                                            // picture type
            // description, terminated per encoding
            if (enc is 1 or 2)
            {
                while (p + 1 < body.Length && !(body[p] == 0 && body[p + 1] == 0)) p += 2;
                p += 2;
            }
            else
            {
                while (p < body.Length && body[p] != 0) p++;
                p++;
            }
            if (p >= body.Length) return null;
            return MakeImage(body[p..].ToArray());
        }
        catch { return null; }
    }

    // ---------- FLAC ----------
    private static void ReadFlac(FileStream fs, Song song)
    {
        fs.Position = 4;
        var hdr = new byte[4];
        while (true)
        {
            if (fs.Read(hdr, 0, 4) != 4) break;
            bool last = (hdr[0] & 0x80) != 0;
            int type = hdr[0] & 0x7F;
            int len = hdr[1] << 16 | hdr[2] << 8 | hdr[3];
            var block = new byte[len];
            fs.ReadExactly(block, 0, len);
            switch (type)
            {
                case 0 when len >= 18:
                    long sampleRate = (block[10] & 0xFF) << 12 | (block[11] & 0xFF) << 4 | (block[12] & 0xF0) >> 4;
                    long total = ((long)(block[13] & 0x0F) << 32) | (long)block[14] << 24 | (long)block[15] << 16 | (long)block[16] << 8 | block[17];
                    if (sampleRate > 0 && total > 0) song.Duration = TimeSpan.FromSeconds((double)total / sampleRate);
                    break;
                case 4: ReadVorbisComment(block, song); break;
                case 6: ReadFlacPicture(block, song); break;
            }
            if (last) break;
        }
    }

    private static void ReadVorbisComment(byte[] block, Song song)
    {
        int p = 0;
        int vendorLen = BitConverter.ToInt32(block, p); p += 4 + vendorLen;
        int count = BitConverter.ToInt32(block, p); p += 4;
        for (int i = 0; i < count && p + 4 <= block.Length; i++)
        {
            int len = BitConverter.ToInt32(block, p); p += 4;
            if (len < 0 || p + len > block.Length) break;
            var entry = Encoding.UTF8.GetString(block, p, len);
            p += len;
            int eq = entry.IndexOf('=');
            if (eq <= 0) continue;
            var key = entry[..eq].ToUpperInvariant();
            var val = entry[(eq + 1)..].Trim();
            switch (key)
            {
                case "TITLE": if (song.Title.Length == 0) song.Title = val; break;
                case "ARTIST": if (song.Artist.Length == 0) song.Artist = val; break;
                case "ALBUM": if (song.Album.Length == 0) song.Album = val; break;
            }
        }
    }

    private static void ReadFlacPicture(byte[] block, Song song)
    {
        try
        {
            // FLAC's PICTURE block stores its integers big-endian (the Vorbis comment block next to
            // it is little-endian, which is why only the cover was missing).
            int p = 0;
            p += 4;                                   // picture type
            int mimeLen = Be32(block, p); p += 4 + mimeLen;
            int descLen = Be32(block, p); p += 4 + descLen;
            p += 16;                                  // w,h,depth,colors
            int dataLen = Be32(block, p); p += 4;
            if (dataLen <= 0 || p + dataLen > block.Length) return;
            var bytes = new byte[dataLen];
            Array.Copy(block, p, bytes, 0, dataLen);
            if (song.Cover == null) song.Cover = MakeImage(bytes);
        }
        catch { }
    }

    // ---------- Ogg / WAV / MP4 (best effort) ----------
    private static void ReadOgg(FileStream fs, Song song)
    {
        // Vorbis comments live in the second page; a straight search is enough for tagging.
        var buffer = new byte[Math.Min(fs.Length, 512 * 1024)];
        fs.Position = 0;
        int n = fs.Read(buffer, 0, buffer.Length);
        var text = Encoding.UTF8.GetString(buffer, 0, n);
        int idx = text.IndexOf("vorbis", StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return;
        var tail = text[(idx + 7)..];
        foreach (var line in tail.Split('\u0001', '\u0003'))
        {
            if (line.StartsWith("TITLE=", StringComparison.OrdinalIgnoreCase)) song.Title = line[6..].Trim();
            else if (line.StartsWith("ARTIST=", StringComparison.OrdinalIgnoreCase)) song.Artist = line[7..].Trim();
            else if (line.StartsWith("ALBUM=", StringComparison.OrdinalIgnoreCase)) song.Album = line[6..].Trim();
            else if (line.StartsWith("METADATA_BLOCK_PICTURE=", StringComparison.OrdinalIgnoreCase) && song.Cover == null)
            {
                song.Cover = DecodeOggPicture(line[23..].Trim());
            }
        }
    }

    /// <summary>OGG stores its cover as a base64 FLAC PICTURE block.</summary>
    private static BitmapSource? DecodeOggPicture(string base64)
    {
        try
        {
            var bytes = Convert.FromBase64String(base64);
            return DecodeFlacPictureBlock(bytes);
        }
        catch { return null; }
    }

    private static BitmapSource? DecodeFlacPictureBlock(byte[] block)
    {
        try
        {
            int p = 4;                                   // picture type
            int mimeLen = Be32(block, p); p += 4 + mimeLen;
            int descLen = Be32(block, p); p += 4 + descLen;
            p += 16;                                     // w,h,depth,colors
            int dataLen = Be32(block, p); p += 4;
            if (dataLen <= 0 || p + dataLen > block.Length) return null;
            var image = new byte[dataLen];
            Array.Copy(block, p, image, 0, dataLen);
            return MakeImage(image);
        }
        catch { return null; }
    }

    /// <summary>AIFF/AIFC: walk FORM chunks; a trailing "ID3 " chunk holds a normal ID3v2 tag.</summary>
    private static void ReadAiff(FileStream fs, Song song)
    {
        try
        {
            long length = fs.Length;
            var chunk = new byte[8];
            fs.Position = 12;
            while (fs.Position + 8 <= length)
            {
                if (fs.Read(chunk, 0, 8) != 8) break;
                var id = Encoding.ASCII.GetString(chunk, 0, 4);
                long size = ((long)chunk[4] << 24) | ((long)chunk[5] << 16) | ((long)chunk[6] << 8) | chunk[7];
                if (size < 0 || fs.Position + size > length) break;
                if (id == "ID3 ")
                {
                    ReadId3v2(fs, song);
                    return;
                }
                fs.Position += size + (size & 1);
            }
        }
        catch { }
    }

    private static void ReadWav(FileStream fs, Song song)
    {
        long byteRate = 0, dataSize = 0;
        var chunk = new byte[8];
        fs.Position = 12;
        while (fs.Read(chunk, 0, 8) == 8)
        {
            var id = Encoding.ASCII.GetString(chunk, 0, 4);
            long size = BitConverter.ToUInt32(chunk, 4);
            if (id == "fmt ")
            {
                var fmt = new byte[16];
                fs.ReadExactly(fmt, 0, 16);
                byteRate = BitConverter.ToUInt32(fmt, 8);
                if (size > 16) fs.Position += size - 16;
            }
            else if (id == "data")
            {
                dataSize = size;
                break;
            }
            else
            {
                fs.Position += size + (size & 1);
            }
        }
        if (byteRate > 0 && dataSize > 0)
            song.Duration = TimeSpan.FromSeconds((double)dataSize / byteRate);
    }

    private static void ReadMp4(FileStream fs, Song song)
    {
        try
        {
            long length = fs.Length;
            var buffer = new byte[Math.Min(length, 4 * 1024 * 1024)];
            fs.Position = 0;
            int n = fs.Read(buffer, 0, buffer.Length);
            var text = Encoding.UTF8.GetString(buffer, 0, n);
            song.Title = ExtractAtom(text, "\u00A9nam") ?? song.Title;
            song.Artist = ExtractAtom(text, "\u00A9ART") ?? song.Artist;
            song.Album = ExtractAtom(text, "\u00A9alb") ?? song.Album;
            if (song.Cover == null)
            {
                song.Cover = FindCovr(buffer, n);
            }
        }
        catch { }
    }

    /// <summary>Reads the iTunes "covr" atom: [size][covr][data: type/flags/bytes][image].</summary>
    private static BitmapSource? FindCovr(byte[] buffer, int length)
    {
        try
        {
            for (int i = 0; i + 8 < length; i++)
            {
                if (buffer[i] != (byte)'c' || buffer[i + 1] != (byte)'o' || buffer[i + 2] != (byte)'v' || buffer[i + 3] != (byte)'r')
                    continue;
                int p = i + 4;                       // start of the child atoms (size + 'data')
                int end = Math.Min(length, i + 4 + 0x1000000);
                while (p + 8 <= end)
                {
                    int size = (buffer[p] << 24) | (buffer[p + 1] << 16) | (buffer[p + 2] << 8) | buffer[p + 3];
                    if (size < 16 || p + size > end) break;
                    if (buffer[p + 4] == (byte)'d' && buffer[p + 5] == (byte)'a' && buffer[p + 6] == (byte)'t' && buffer[p + 7] == (byte)'a')
                    {
                        int payload = p + 16;        // 8 atom header + 8 (type/flags/reserved)
                        int payloadLen = size - 16;
                        if (payloadLen > 1024 && payload + payloadLen <= length)
                        {
                            var bytes = new byte[payloadLen];
                            Array.Copy(buffer, payload, bytes, 0, payloadLen);
                            return MakeImage(bytes);
                        }
                    }
                    p += size;
                }
            }
        }
        catch { }
        return null;
    }

    private static string? ExtractAtom(string text, string atom)
    {
        int i = text.IndexOf(atom, StringComparison.Ordinal);
        if (i < 0) return null;
        int start = i + 8;
        if (start + 4 > text.Length) return null;
        int len = (text[start] << 24) | (text[start + 1] << 16) | (text[start + 2] << 8) | text[start + 3];
        if (len <= 0 || len > 4096 || start + 8 + len > text.Length) return null;
        var val = text.Substring(start + 8, len).Trim('\0', ' ');
        return val.Length == 0 ? null : val;
    }

    // ---------- Monkey's Audio (APEv2) ----------
    private static void ReadApe(FileStream fs, Song song)
    {
        try
        {
            long length = fs.Length;
            if (length < 64) return;
            var footer = new byte[32];
            fs.Position = length - 32;
            if (fs.Read(footer, 0, 32) != 32) return;
            if (Encoding.ASCII.GetString(footer, 0, 8) != "APETAGEX") return;
            int tagSize = BitConverter.ToInt32(footer, 12);
            int count = BitConverter.ToInt32(footer, 16);
            if (tagSize <= 32 || tagSize > length) return;
            long start = length - tagSize;
            if (start < 0) return;
            fs.Position = start;
            var block = new byte[tagSize - 32];
            if (fs.Read(block, 0, block.Length) < 16) return;
            int p = 0;
            for (int i = 0; i < count && p + 8 <= block.Length; i++)
            {
                int valueSize = BitConverter.ToInt32(block, p); p += 4;
                int flags = BitConverter.ToInt32(block, p); p += 4;
                if (valueSize < 0 || p >= block.Length) break;
                int keyEnd = Array.IndexOf(block, (byte)0, p);
                if (keyEnd < 0) break;
                var key = Encoding.ASCII.GetString(block, p, keyEnd - p);
                p = keyEnd + 1;
                if (valueSize < 0 || p + valueSize > block.Length) break;
                if (key.Equals("Title", StringComparison.OrdinalIgnoreCase) && song.Title.Length == 0)
                    song.Title = Encoding.UTF8.GetString(block, p, valueSize);
                else if (key.Equals("Artist", StringComparison.OrdinalIgnoreCase) && song.Artist.Length == 0)
                    song.Artist = Encoding.UTF8.GetString(block, p, valueSize);
                else if (key.Equals("Album", StringComparison.OrdinalIgnoreCase) && song.Album.Length == 0)
                    song.Album = Encoding.UTF8.GetString(block, p, valueSize);
                else if ((flags & 2) != 0 && key.StartsWith("Cover Art", StringComparison.OrdinalIgnoreCase) && song.Cover == null)
                {
                    // "<file name>\0<image bytes>"
                    int start2 = Array.IndexOf(block, (byte)0, p);
                    if (start2 > 0 && start2 < p + valueSize)
                    {
                        int imgStart = start2 + 1;
                        int imgLen = p + valueSize - imgStart;
                        if (imgLen > 1024)
                        {
                            var image = new byte[imgLen];
                            Array.Copy(block, imgStart, image, 0, imgLen);
                            song.Cover = MakeImage(image);
                        }
                    }
                }
                p += valueSize;
            }
        }
        catch { }
    }

    // ---------- ASF / WMA ----------
    private static void ReadAsf(FileStream fs, Song song)
    {
        try
        {
            long length = fs.Length;
            var buffer = new byte[Math.Min(length, 4 * 1024 * 1024)];
            fs.Position = 0;
            int n = fs.Read(buffer, 0, buffer.Length);
            // Content Description Object: title / author as UTF-16LE, five length-prefixed strings.
            var cd = new byte[] { 0x33, 0x26, 0xB2, 0x75, 0x8E, 0x66, 0xCF, 0x11, 0xA6, 0xD9, 0x00, 0xAA, 0x00, 0x62, 0xCE, 0x6C };
            int idx = IndexOf(buffer, n, cd);
            if (idx >= 0)
            {
                int p = idx + 16 + 8;                     // GUID + object size
                if (p + 10 <= n)
                {
                    int l1 = BitConverter.ToInt16(buffer, p);
                    int l2 = BitConverter.ToInt16(buffer, p + 2);
                    int l3 = BitConverter.ToInt16(buffer, p + 4);
                    int l4 = BitConverter.ToInt16(buffer, p + 6);
                    int l5 = BitConverter.ToInt16(buffer, p + 8);
                    int q = p + 10;
                    song.Title = ReadUtf16(buffer, q, l1) ?? song.Title; q += l1;
                    song.Artist = ReadUtf16(buffer, q, l2) ?? song.Artist; q += l2;
                    q += l3 + l4 + l5;
                }
            }
            if (song.Cover == null)
            {
                // WM/Picture: name followed by a picture struct (type, size, mime, description, data).
                var marker = Encoding.Unicode.GetBytes("WM/Picture");
                int mi = IndexOf(buffer, n, marker);
                if (mi > 0 && mi + marker.Length + 8 < n)
                {
                    int p = mi + marker.Length;
                    p += 2;                               // UTF-16 null
                    if (p + 5 < n)
                    {
                        p += 1;                           // picture type
                        int dataLen = BitConverter.ToInt32(buffer, p); p += 4;
                        if (dataLen > 0 && p + dataLen <= n)
                        {
                            var image = new byte[dataLen];
                            Array.Copy(buffer, p, image, 0, dataLen);
                            song.Cover = MakeImage(image);
                        }
                    }
                }
            }
        }
        catch { }
    }

    private static int IndexOf(byte[] haystack, int length, byte[] needle)
    {
        for (int i = 0; i + needle.Length <= length; i++)
        {
            int k = 0;
            while (k < needle.Length && haystack[i + k] == needle[k]) k++;
            if (k == needle.Length) return i;
        }
        return -1;
    }

    private static string? ReadUtf16(byte[] buffer, int offset, int byteLength)
    {
        if (byteLength <= 0 || offset < 0 || offset + byteLength > buffer.Length) return null;
        var text = Encoding.Unicode.GetString(buffer, offset, byteLength).TrimEnd('\0');
        return text.Length == 0 ? null : text;
    }

    // ---------- ID3v1 ----------
    private static void ReadId3v1(FileStream fs, Song song)
    {
        try
        {
            if (fs.Length < 128) return;
            fs.Position = fs.Length - 128;
            var buf = new byte[128];
            fs.ReadExactly(buf, 0, 128);
            if (buf[0] != 'T' || buf[1] != 'A' || buf[2] != 'G') return;
            var title = Encoding.Latin1.GetString(buf, 3, 30).Trim('\0', ' ');
            var artist = Encoding.Latin1.GetString(buf, 33, 30).Trim('\0', ' ');
            var album = Encoding.Latin1.GetString(buf, 63, 30).Trim('\0', ' ');
            if (song.Title.Length == 0) song.Title = FixLatin1(title);
            if (song.Artist.Length == 0) song.Artist = FixLatin1(artist);
            if (song.Album.Length == 0) song.Album = FixLatin1(album);
        }
        catch { }
    }

    private static string FixLatin1(string s) => s.Replace('\0', ' ').Trim();

    private static int Be32(byte[] buffer, int offset)
        => (buffer[offset] << 24) | (buffer[offset + 1] << 16) | (buffer[offset + 2] << 8) | buffer[offset + 3];

    public static BitmapSource? MakeImage(byte[] bytes)
    {
        try
        {
            var image = new BitmapImage();
            using var ms = new MemoryStream(bytes);
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = ms;
            image.DecodePixelWidth = 512;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch { return null; }
    }
}
