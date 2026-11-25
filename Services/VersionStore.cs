using System.IO;
using System.IO.Compression;
using System.Text.Json;

namespace CurrentLyrics.Services;

/// <summary>
/// 版本目录管理：安装新版本、原子切换指针、回滚、清理旧版本。
/// 运行中的程序从不覆盖自己的文件——只新建 `versions\vX\` 并改 `current.txt`。
/// </summary>
public static class VersionStore
{
    public static bool Enabled => Layout.IsVersioned;

    public static string? Current() => Layout.CurrentVersion();

    public static IEnumerable<string> Installed()
    {
        if (Layout.VersionsDir is not { } dir || !Directory.Exists(dir)) yield break;
        foreach (var path in Directory.GetDirectories(dir))
        {
            var name = Path.GetFileName(path);
            if (name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) continue;
            if (File.Exists(Path.Combine(path, Layout.AppExeName))) yield return name;
        }
    }

    /// <summary>指针写入：同卷内 File.Replace 是原子的，写一半断电也不会把指针弄坏。</summary>
    public static void WritePointer(string version)
    {
        Directory.CreateDirectory(Layout.Root);
        var target = Layout.PointerPath;
        var temp = target + ".tmp";
        File.WriteAllText(temp, version);
        if (File.Exists(target))
        {
            File.Replace(temp, target, null, true);
        }
        else
        {
            File.Move(temp, target);
        }
    }

    /// <summary>把 zip 装成一个版本目录。先解到 `.tmp`，完整后改名，失败不留残缺目录。</summary>
    public static void Install(string zipPath, string version, IProgress<double>? progress = null)
    {
        if (Layout.VersionsDir is not { } versions) throw new InvalidOperationException("当前不是版本目录布局");
        Directory.CreateDirectory(versions);
        var final = Path.Combine(versions, version);
        var temp = final + ".tmp";
        if (Directory.Exists(temp)) Directory.Delete(temp, true);
        Directory.CreateDirectory(temp);

        using (var zip = ZipFile.OpenRead(zipPath))
        {
            int total = Math.Max(1, zip.Entries.Count), done = 0;
            foreach (var entry in zip.Entries)
            {
                var rel = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
                var target = Path.GetFullPath(Path.Combine(temp, rel));
                if (!target.StartsWith(temp, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("压缩包里有越界路径: " + entry.FullName);

                if (entry.FullName.EndsWith("/") || entry.Name.Length == 0)
                {
                    Directory.CreateDirectory(target);
                }
                else
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    entry.ExtractToFile(target, true);
                }
                progress?.Report((double)(++done) / total);
            }
        }

        if (!File.Exists(Path.Combine(temp, Layout.AppExeName)))
            throw new InvalidDataException("更新包里没有 " + Layout.AppExeName);

        if (Directory.Exists(final)) Directory.Delete(final, true);
        Directory.Move(temp, final);
    }

    /// <summary>回滚到上一个还完整的版本（当前版本除外）。</summary>
    public static string? Rollback()
    {
        var current = Current();
        var candidate = Installed()
            .Where(v => !string.Equals(v, current, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(VersionKey)
            .FirstOrDefault();
        if (candidate == null) return null;
        WritePointer(candidate);
        return candidate;
    }

    /// <summary>只保留最新的 <paramref name="keep"/> 个「旧版本」目录，当前版本永远保留。</summary>
    public static void Trim(int keep = 1)
    {
        if (Layout.VersionsDir is not { } dir) return;
        var current = Current();
        var all = Installed().OrderByDescending(VersionKey).ToList();
        int others = 0;
        foreach (var version in all)
        {
            if (string.Equals(version, current, StringComparison.OrdinalIgnoreCase)) continue;
            if (others < keep)
            {
                others++;
                continue;
            }
            try { Directory.Delete(Path.Combine(dir, version), true); } catch { }
        }
        try
        {
            var downloads = Layout.DownloadsDir;
            if (Directory.Exists(downloads))
                foreach (var file in Directory.GetFiles(downloads))
                    try { File.Delete(file); } catch { }
        }
        catch { }
    }

    /// <summary>版本号排序键：v1.0.10 要排在 v1.0.9 之后。</summary>
    public static (int, int, int) VersionKey(string version)
    {
        var text = version.TrimStart('v', 'V');
        var parts = text.Split('.');
        int Get(int i) => i < parts.Length && int.TryParse(parts[i], out var n) ? n : 0;
        return (Get(0), Get(1), Get(2));
    }

    public static string Tag(string version) => version.StartsWith("v", StringComparison.OrdinalIgnoreCase) ? version : "v" + version;

    /// <summary>`config.json` 里的 HMAC 密钥（可选）。有就验签，没有就只验 SHA256。</summary>
    public static string? HmacKey()
    {
        try
        {
            if (!File.Exists(Layout.ConfigPath)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(Layout.ConfigPath));
            if (doc.RootElement.TryGetProperty("hmacKey", out var key) && key.ValueKind == JsonValueKind.String)
            {
                var raw = key.GetString();
                return string.IsNullOrWhiteSpace(raw) ? null : raw.Trim();
            }
        }
        catch { }
        return null;
    }
}
