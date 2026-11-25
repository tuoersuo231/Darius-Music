using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using CurrentLyrics.Models;

namespace CurrentLyrics.Services;

/// <summary>
/// Reads the play queue that the Android CurrentMusic build keeps in WSA. The app stores its
/// queue as a JSON payload inside music.db; we copy the database out over adb and lift the
/// payload straight out of the file bytes, so no sqlite dependency is needed.
/// </summary>
public static class WsaBridge
{
    public const string DefaultSerial = "127.0.0.1:58526";
    private const string Package = "com.bileizhen.currentmusic";

    public static string? FindAdb(string? preferred = null)
    {
        if (!string.IsNullOrWhiteSpace(preferred) && File.Exists(preferred)) return preferred;

        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            var candidate = Path.Combine(dir.Trim(), "adb.exe");
            if (File.Exists(candidate)) return candidate;
        }

        var known = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Android", "Sdk", "platform-tools", "adb.exe"),
            @"C:\platform-tools\adb.exe",
            @"D:\platform-tools\adb.exe",
            @"E:\platform-tools\adb.exe",
        };
        foreach (var path in known)
            if (File.Exists(path)) return path;

        // WSABuilds keeps platform-tools under the user's Downloads folder.
        try
        {
            var users = Directory.GetDirectories(@"C:\Users");
            foreach (var user in users)
            {
                var downloads = Path.Combine(user, "Downloads");
                if (!Directory.Exists(downloads)) continue;
                foreach (var dir in Directory.GetDirectories(downloads, "WSABuilds", SearchOption.TopDirectoryOnly))
                {
                    var candidate = Path.Combine(dir, "platform-tools", "adb.exe");
                    if (File.Exists(candidate)) return candidate;
                }
            }
        }
        catch { }

        return null;
    }

    public static bool IsDeviceOnline(string adb, string serial)
    {
        var output = Run(adb, $"devices");
        return output.Contains(serial, StringComparison.OrdinalIgnoreCase) && output.Contains("device");
    }

    /// <summary>Reads the queue payload straight out of the app's database via exec-out, so no
    /// local staging path (and no adb path-encoding) is involved.</summary>
    public static Task<(List<Song> songs, string? error)> ReadQueueAsync(string adb, string serial)
    {
        try
        {
            var databases = $"/data/data/{Package}/databases";
            // exec-out passes argv straight through, so the command must be one quoted argument.
            var db = ExecOut(adb, "-s " + serial + " exec-out su -c \"cat " + databases + "/music.db\"");
            var wal = ExecOut(adb, "-s " + serial + " exec-out su -c \"cat " + databases + "/music.db-wal\"");
            if (db.Length == 0) return Task.FromResult<(List<Song> songs, string? error)>((new List<Song>(), "读不到流声音乐的数据库（检查 adb 与 root）"));

            var songs = ParseQueueBytes(new[] { db, wal });
            if (songs.Count == 0) return Task.FromResult<(List<Song> songs, string? error)>((songs, "流声音乐的播放队列是空的"));
            return Task.FromResult<(List<Song> songs, string? error)>((songs, null));
        }
        catch (Exception e)
        {
            return Task.FromResult<(List<Song> songs, string? error)>((new List<Song>(), e.Message));
        }
    }

    private static byte[] ExecOut(string adb, string arguments)
    {
        try
        {
            var info = new ProcessStartInfo(adb, arguments)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var process = Process.Start(info);
            if (process == null) return Array.Empty<byte>();
            using var buffer = new MemoryStream();
            process.StandardOutput.BaseStream.CopyTo(buffer);
            process.StandardError.ReadToEnd();
            process.WaitForExit(20000);
            LastLog += $"[{arguments}] => {buffer.Length} bytes\n";
            return buffer.ToArray();
        }
        catch (Exception e)
        {
            LastLog += $"[{arguments}] !! {e.Message}\n";
            return Array.Empty<byte>();
        }
    }

    internal static List<Song> ParseQueueBytes(IEnumerable<byte[]> blobs)
    {
        var result = new List<Song>();
        foreach (var bytes in blobs)
        {
            if (bytes.Length == 0) continue;
            var text = Encoding.UTF8.GetString(bytes);
            int searchFrom = 0;
            while (true)
            {
                int start = text.IndexOf("{\"songs\":[", searchFrom, StringComparison.Ordinal);
                if (start < 0) break;
                var json = SliceJson(text, start);
                searchFrom = start + 1;
                if (json == null) continue;
                try
                {
                    using var doc = JsonDocument.Parse(json);
                    if (!doc.RootElement.TryGetProperty("songs", out var arr) || arr.ValueKind != JsonValueKind.Array) continue;
                    foreach (var item in arr.EnumerateArray())
                    {
                        var song = MapSong(item);
                        if (song != null && result.All(s => s.NeteaseId != song.NeteaseId)) result.Add(song);
                    }
                }
                catch { }
            }
        }
        return result;
    }

    internal static List<Song> ParseQueueFiles(string dir)
    {
        var result = new List<Song>();
        foreach (var name in new[] { "music.db", "music.db-wal" })
        {
            var path = Path.Combine(dir, name);
            if (!File.Exists(path)) continue;
            var bytes = File.ReadAllBytes(path);
            var text = Encoding.UTF8.GetString(bytes);
            int searchFrom = 0;
            while (true)
            {
                int start = text.IndexOf("{\"songs\":[", searchFrom, StringComparison.Ordinal);
                if (start < 0) break;
                var json = SliceJson(text, start);
                searchFrom = start + 1;
                if (json == null) continue;
                try
                {
                    using var doc = JsonDocument.Parse(json);
                    if (!doc.RootElement.TryGetProperty("songs", out var arr) || arr.ValueKind != JsonValueKind.Array) continue;
                    foreach (var item in arr.EnumerateArray())
                    {
                        var song = MapSong(item);
                        if (song != null && result.All(s => s.NeteaseId != song.NeteaseId)) result.Add(song);
                    }
                }
                catch { }
            }
        }
        return result;
    }

    private static Song? MapSong(JsonElement e)
    {
        try
        {
            long id = e.TryGetProperty("id", out var idEl) ? idEl.GetInt64() : 0;
            if (id == 0) return null;
            string name = e.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            string artists = e.TryGetProperty("artists", out var a) ? a.GetString() ?? "" : "";
            string album = e.TryGetProperty("album", out var al) ? al.GetString() ?? "" : "";
            string? cover = e.TryGetProperty("cover", out var c) ? c.GetString() : null;
            long ms = e.TryGetProperty("durationMs", out var d) ? d.GetInt64() : 0;
            return new Song
            {
                Source = SongSource.Netease,
                Path = $"netease:{id}",
                NeteaseId = id,
                Title = name,
                Artist = artists,
                Album = album,
                CoverUrl = string.IsNullOrWhiteSpace(cover) ? null : cover + "?param=200y200",
                Duration = TimeSpan.FromMilliseconds(ms),
            };
        }
        catch { return null; }
    }

    /// <summary>Returns the balanced JSON object starting at <paramref name="start"/>.</summary>
    private static string? SliceJson(string text, int start)
    {
        int depth = 0;
        bool inString = false, escape = false;
        for (int i = start; i < text.Length; i++)
        {
            char ch = text[i];
            if (inString)
            {
                if (escape) escape = false;
                else if (ch == '\\') escape = true;
                else if (ch == '"') inString = false;
                continue;
            }
            if (ch == '"') inString = true;
            else if (ch == '{') depth++;
            else if (ch == '}')
            {
                depth--;
                if (depth == 0) return text.Substring(start, i - start + 1);
            }
        }
        return null;
    }

    internal static string LastLog = "";

    private static string Run(string fileName, string arguments)
    {
        try
        {
            var info = new ProcessStartInfo(fileName, arguments)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var process = Process.Start(info);
            if (process == null) return "";
            var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            process.WaitForExit(20000);
            LastLog += $"[{arguments}] => {output.Trim()}\n";
            return output;
        }
        catch (Exception e)
        {
            LastLog += $"[{arguments}] !! {e.Message}\n";
            return "";
        }
    }
}
