using System.IO;
using System.Net.Http;
using CurrentLyrics.Models;

namespace CurrentLyrics.Services;

/// <summary>
/// Saves a cloud track as a normal audio file next to its cover and lyrics, so it becomes an ordinary
/// local library entry. The CDN object is already an unencrypted mp3/flac, which is why this is just a
/// GET plus a couple of sidecar files — no tagging library needed.
/// </summary>
public static class NeteaseDownload
{
    private static readonly HttpClient Http = CreateHttp();

    public sealed record Result(bool Ok, string? Path, string? Error);

    private static HttpClient CreateHttp()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/124.0.0.0 Safari/537.36");
        return client;
    }

    /// <summary>Downloads <paramref name="song"/> into <paramref name="folder"/>; sidecars are written
    /// beside it so the existing local reader (which looks for a same-named .lrc) picks them up.</summary>
    public static async Task<Result> SaveAsync(Song song, string folder, string quality,
        IProgress<string>? progress = null, CancellationToken token = default, string? urlOverride = null)
    {
        if (song.Source != SongSource.Netease || song.NeteaseId <= 0)
            return new Result(false, null, "只有在线曲目可以这样下载");
        if (string.IsNullOrWhiteSpace(folder)) return new Result(false, null, "没有可用的下载目录");

        progress?.Report("获取播放地址…");
        // Playback has usually just resolved this track (possibly through a 落雪 script); reusing that
        // URL avoids both a pointless request and the gateway's rate shaping, which shows up as an
        // empty 200.
        var url = !string.IsNullOrEmpty(urlOverride)
            ? urlOverride
            : song.StreamExpiresAt > DateTime.UtcNow ? song.StreamUrl : null;
        if (string.IsNullOrEmpty(url))
        {
            var resolved = await Task.Run(() => NeteaseApi.ResolveAsync(song.NeteaseId, quality, token), token);
            if (resolved.Url == null) return new Result(false, null, resolved.Error ?? "拿不到下载地址");
            url = resolved.Url;
        }

        Directory.CreateDirectory(folder);
        var tempDir = Path.Combine(AppPaths.DataDir, "downloads", "tmp");
        Directory.CreateDirectory(tempDir);
        var temp = Path.Combine(tempDir, Guid.NewGuid().ToString("N") + ".part");

        try
        {
            progress?.Report("下载音频…");
            using (var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token))
            {
                response.EnsureSuccessStatusCode();
                await using var source = await response.Content.ReadAsStreamAsync(token);
                await using var target = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, true);
                await source.CopyToAsync(target, 1 << 16, token);
            }

            var ext = DetectFormat(temp);
            if (ext == null) return new Result(false, null, "下载到的文件格式无法识别");
            var name = FileNameFor(song);
            var final = UniquePath(folder, name, ext);
            File.Move(temp, final, overwrite: false);

            // Cover and lyrics are optional: a missing one must not lose the audio that already landed.
            progress?.Report("保存封面与歌词…");
            var cover = await TryGetBytesAsync(song.CoverUrl, 12 * 1024 * 1024, token);
            if (cover != null)
            {
                try { File.WriteAllBytes(Path.ChangeExtension(final, ".jpg"), cover); } catch { }
            }
            var lrc = await TryGetLyricsAsync(song, token);
            if (!string.IsNullOrWhiteSpace(lrc))
            {
                try { File.WriteAllText(Path.ChangeExtension(final, ".lrc"), lrc); } catch { }
            }
            return new Result(true, final, null);
        }
        catch (OperationCanceledException)
        {
            return new Result(false, null, "下载已取消");
        }
        catch (Exception e)
        {
            return new Result(false, null, "下载失败：" + e.Message);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
        }
    }

    /// <summary>Same naming the phone client uses, so a file mirrored from either side keeps its id.</summary>
    private static string FileNameFor(Song song)
    {
        var title = Sanitize(song.DisplayTitle);
        var artist = Sanitize(song.DisplayArtist);
        return artist.Length == 0 ? $"{title} [{song.NeteaseId}]" : $"{title} - {artist} [{song.NeteaseId}]";
    }

    private static string Sanitize(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new System.Text.StringBuilder(value.Length);
        foreach (var ch in value)
            builder.Append(Array.IndexOf(invalid, ch) >= 0 ? '_' : ch);
        var text = builder.ToString().Trim().TrimEnd('.');
        return text.Length > 90 ? text[..90].TrimEnd() : text;
    }

    private static string UniquePath(string folder, string name, string ext)
    {
        var path = Path.Combine(folder, name + "." + ext);
        for (int i = 2; File.Exists(path); i++)
            path = Path.Combine(folder, $"{name} ({i}).{ext}");
        return path;
    }

    private static string? DetectFormat(string file)
    {
        Span<byte> head = stackalloc byte[12];
        using var stream = File.OpenRead(file);
        int read = stream.Read(head);
        if (read < 4) return null;
        if (head[0] == 'f' && head[1] == 'L' && head[2] == 'a' && head[3] == 'C') return "flac";
        if (head[0] == 'O' && head[1] == 'g' && head[2] == 'g' && head[3] == 'S') return "ogg";
        if (read >= 12 && head[0] == 'R' && head[1] == 'I' && head[2] == 'F' && head[3] == 'F') return "wav";
        if (read >= 8 && head[4] == 'f' && head[5] == 't' && head[6] == 'y' && head[7] == 'p') return "m4a";
        if (head[0] == 'I' && head[1] == 'D' && head[2] == '3') return "mp3";
        if (head[0] == 0xFF && (head[1] & 0xF6) == 0xF0) return "aac";
        if (head[0] == 0xFF && (head[1] & 0xE0) == 0xE0) return "mp3";
        return null;
    }

    private static async Task<byte[]?> TryGetBytesAsync(string? url, int limit, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        try
        {
            using var response = await Http.GetAsync(url, token);
            if (!response.IsSuccessStatusCode) return null;
            var bytes = await response.Content.ReadAsByteArrayAsync(token);
            return bytes.Length is > 0 and <= 32 * 1024 * 1024 ? bytes : null;
        }
        catch
        {
            return null;
        }
    }

    private static async Task<string?> TryGetLyricsAsync(Song song, CancellationToken token)
    {
        var cached = Path.Combine(AppPaths.DataDir, "lyrics", song.NeteaseId + ".lrc");
        var marker = Path.Combine(AppPaths.DataDir, "lyrics", song.NeteaseId + ".v2");
        try
        {
            if (File.Exists(cached))
            {
                var text = File.ReadAllText(cached);
                // A cache written before the word-level request carries line stamps only; writing that
                // into the download's sidecar would make the saved copy sweep at a constant rate.
                var doc = LrcParser.Parse(text);
                if (doc.Lines.Count > 0 && (File.Exists(marker) || LrcParser.HasWordTiming(doc))) return text;
            }
        }
        catch { }
        var text2 = await Task.Run(() => NeteaseApi.LyricAsync(song.NeteaseId, token), token);
        if (string.IsNullOrWhiteSpace(text2)) return null;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(cached)!);
            File.WriteAllText(cached, text2);
            File.WriteAllText(marker, "word-level request v2");
        }
        catch { }
        return text2;
    }
}
