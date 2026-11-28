using System.IO;
using Microsoft.Win32;

namespace CurrentLyrics.Services;

/// <summary>
/// 1.3.18 起安装形态变了（从“根目录 + versions\ + data\”变成 Inno 装到
/// <c>%LocalAppData%\Programs</c>、数据在 <c>%LocalAppData%\Darius Music\data</c>）。
/// 新目录是空的时候，把旧安装/绿色版里的 data 搬过来，用户不会丢歌单和设置。
///
/// 只复制、不删除源目录；已经有 settings.json 就什么都不做（不会覆盖用户现在用的数据）。
/// </summary>
public static class DataMigration
{
    /// <summary>这次启动做了什么，空 = 没搬。</summary>
    public static string? Report { get; private set; }

    private const string Marker = "migrated-from.txt";

    public static void TryImportOnce()
    {
        if (Environment.GetEnvironmentVariable("DARIUS_NO_MIGRATE") == "1") return;
        try
        {
            var target = AppPaths.DataDir;
            if (File.Exists(Path.Combine(target, "settings.json"))) return;   // 已经有自己的数据
            // 先把"上次用的数据目录"读出来，再写注册表 —— 否则会把自己刚写的新目录当成候选。
            var known = ReadKnownDataDir();
            foreach (var source in Candidates(known))
            {
                if (string.IsNullOrWhiteSpace(source)) continue;
                if (string.Equals(Path.GetFullPath(source), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!File.Exists(Path.Combine(source, "settings.json"))) continue;

                var (files, bytes) = CopySmallFiles(source, target);
                if (files == 0) continue;
                try { File.WriteAllText(Path.Combine(target, Marker), source + Environment.NewLine); } catch { }
                Report = $"已导入旧配置：{source}（{files} 个文件，{bytes / 1024.0 / 1024.0:0.0} MB）";
                break;
            }
            RememberInstall();
            if (Report is { Length: > 0 })
            {
                try
                {
                    File.AppendAllText(Path.Combine(target, "migration.log"),
                        $"{DateTime.Now:MM-dd HH:mm:ss} {Report}{Environment.NewLine}");
                }
                catch { }
            }
        }
        catch { }
    }

    private static string? ReadKnownDataDir()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\DariusMusic");
            return key?.GetValue("DataDir") as string;
        }
        catch { return null; }
    }

    /// <summary>
    /// 把安装位置记进注册表：以后迁移/卸载找得到，也方便排查“数据到底在哪个目录”。
    /// </summary>
    public static void RememberInstall()
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(@"Software\DariusMusic");
            if (key == null) return;
            key.SetValue("InstallDir", AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar), RegistryValueKind.String);
            key.SetValue("DataDir", AppPaths.DataDir, RegistryValueKind.String);
            key.SetValue("Version", typeof(DataMigration).Assembly.GetName().Version?.ToString() ?? "", RegistryValueKind.String);
        }
        catch { }
    }

    /// <summary>可能藏着旧数据的地方，从最可能到最不可能。</summary>
    private static IEnumerable<string> Candidates(string? known)
    {
        // 1) 上次记下的数据目录（自己的注册表；换过安装位置时最准）
        if (!string.IsNullOrWhiteSpace(known) && Directory.Exists(known)) yield return known.TrimEnd('\\');

        // 2) 各固定盘根目录的 "Darius Music\data"（绿色版解压默认就是这个文件夹名）
        foreach (var drive in DriveInfo.GetDrives())
        {
            if (drive.DriveType != DriveType.Fixed || !drive.IsReady) continue;
            var root = Path.Combine(drive.RootDirectory.FullName, "Darius Music", "data");
            if (Directory.Exists(root)) yield return root;
        }

        // 3) 用户目录下的几个常见位置
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        foreach (var name in new[] { "Desktop", "Downloads", "Music", "Desktop\\Darius Music", "Downloads\\Darius Music" })
        {
            var dir = Path.Combine(profile, name, "data");
            if (Directory.Exists(dir)) yield return dir;
        }
    }

    /// <summary>
    /// 只搬真正"用户资产"的东西：根目录的 json（设置/歌单/频谱/快捷键）、lyrics\ 和 lx\ 脚本。
    /// 封面缓存（几百 MB）和本地对齐模型（近 1 GB）不搬 —— 那些是缓存，重新生成/下载就行，
    /// 搬一次要多花几十秒还占地方。
    /// </summary>
    private static (int Files, long Bytes) CopySmallFiles(string source, string target)
    {
        int files = 0;
        long bytes = 0;
        foreach (var file in new DirectoryInfo(source).EnumerateFiles("*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(source, file.FullName);
            if (!IsUserAsset(rel)) continue;
            if (file.Length > 32L * 1024 * 1024) continue;
            var dest = Path.Combine(target, rel);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                if (!File.Exists(dest)) file.CopyTo(dest, false);
                files++;
                bytes += file.Length;
            }
            catch { }
            if (bytes > 256L * 1024 * 1024) break;
        }
        return (files, bytes);
    }

    private static bool IsUserAsset(string rel)
    {
        var sep = Path.DirectorySeparatorChar;
        if (!rel.Contains(sep))                                    // 根目录的文件
            return rel.EndsWith(".json", StringComparison.OrdinalIgnoreCase) &&
                   !rel.StartsWith("gpu", StringComparison.OrdinalIgnoreCase);
        return rel.StartsWith("lyrics" + sep, StringComparison.OrdinalIgnoreCase) ||
               rel.StartsWith("lx" + sep, StringComparison.OrdinalIgnoreCase) ||
               rel.StartsWith("hotkeys" + sep, StringComparison.OrdinalIgnoreCase);
    }
}
