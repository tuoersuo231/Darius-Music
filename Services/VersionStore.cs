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
        // 正在运行的这一个永远不删：哪怕 keepVersions 被写成 0，也不能把自己拆了。
        var running = Path.GetFileName(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
        var all = Installed().OrderByDescending(VersionKey).ToList();
        int others = 0;
        foreach (var version in all)
        {
            if (string.Equals(version, current, StringComparison.OrdinalIgnoreCase)) continue;
            if (string.Equals(version, running, StringComparison.OrdinalIgnoreCase)) continue;
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

    /// <summary>`config.json` 里的 keepVersions：除了当前版本，还留几个旧版本（默认 1，用于回滚）。</summary>
    public static int KeepVersions(int fallback = 1)
    {
        try
        {
            if (!File.Exists(Layout.ConfigPath)) return fallback;
            using var doc = JsonDocument.Parse(File.ReadAllText(Layout.ConfigPath));
            if (doc.RootElement.TryGetProperty("keepVersions", out var value) &&
                value.ValueKind == JsonValueKind.Number &&
                value.TryGetInt32(out var keep))
                return Math.Max(0, keep);
        }
        catch { }
        return fallback;
    }

    /// <summary>
    /// 启动时顺手清一遍安装目录。为什么程序里也要做一份：
    /// 真正负责删旧版本的是启动器，而它只在**程序退出之后**才 Trim —— 用户更新完直接关机、
    /// 或者从任务管理器结束进程、或者绕过启动器双击版本目录里的 exe，那一份旧版本就一直留着
    /// （100 MB 一份）。另外 `current.txt.*.bak` 是旧版启动器留下的，没人清。
    /// </summary>
    public static void CleanInstallFolder()
    {
        // VersionsDir 是在第一次访问 Layout.Root 时才探测出来的，所以必须先碰一下 Root。
        _ = Layout.Root;
        if (Layout.VersionsDir is not { } versions) return;
        try { Trim(KeepVersions()); } catch { }
        try
        {
            // 半成品版本目录：装在临时目录时崩了会留下 vX.tmp。
            if (Directory.Exists(versions))
                foreach (var dir in Directory.GetDirectories(versions, "*.tmp"))
                    try { Directory.Delete(dir, true); } catch { }

            // 旧版启动器切指针时留下的备份（current.txt.v1.2.87.bak 之类），纯残留。
            foreach (var file in Directory.GetFiles(Layout.Root, "current.txt.*.bak"))
                try { File.Delete(file); } catch { }
        }
        catch { }
    }
}
