using System.IO;

namespace CurrentLyrics.Services;

/// <summary>
/// 安装布局：启动器在根目录，每个版本一个独立目录，`current.txt` 指针决定加载哪个版本。
/// <code>
/// Darius Music\            ← 根（Root）
///   Darius Music.exe       ← 启动器（不变）
///   current.txt            ← v1.0.2
///   versions\v1.0.2\…      ← 程序本体
///   data\                  ← 用户数据（跨版本共享）
///   downloads\             ← 下载中转
/// </code>
/// 直接跑在 versions 里的 exe 时，data 仍然落在根目录，换版本不会丢设置。
/// </summary>
public static class Layout
{
    public const string AppExeName = "Darius Music.exe";

    private static string? _root;
    private static bool _detected;

    /// <summary>exe 所在的目录（版本目录或根目录）。</summary>
    public static string BaseDir => AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);

    /// <summary>是否运行在 `versions\v*` 目录里（即新版布局）。</summary>
    public static bool IsVersioned => VersionsDir is not null;

    /// <summary>`<root>\versions`，非新版布局时为 null。</summary>
    public static string? VersionsDir { get; private set; }

    /// <summary>安装根目录：`versions` 的上一级；老布局时就是 exe 目录。</summary>
    public static string Root
    {
        get
        {
            if (_detected) return _root!;
            _detected = true;
            var dir = new DirectoryInfo(BaseDir);
            var probe = dir;
            string? versions = null;
            while (probe?.Parent != null)
            {
                if (string.Equals(probe.Name, "versions", StringComparison.OrdinalIgnoreCase))
                {
                    versions = probe.FullName;
                    _root = probe.Parent.FullName;
                    break;
                }
                probe = probe.Parent;
            }
            VersionsDir = versions;
            _root ??= BaseDir;
            return _root;
        }
    }

    public static string PointerPath => Path.Combine(Root, "current.txt");
    public static string DownloadsDir => Path.Combine(Root, "downloads");
    public static string DataDir => Path.Combine(Root, "data");
    public static string LauncherPath => Path.Combine(Root, AppExeName);
    public static string ConfigPath => Path.Combine(Root, "config.json");

    /// <summary>某个版本的程序本体。</summary>
    public static string AppExeOf(string version) =>
        Path.Combine(VersionsDir ?? Root, version, AppExeName);

    public static string? CurrentVersion()
    {
        try
        {
            var path = PointerPath;
            if (!File.Exists(path)) return null;
            var text = File.ReadAllText(path).Trim();
            return text.Length == 0 ? null : text;
        }
        catch
        {
            return null;
        }
    }
}
