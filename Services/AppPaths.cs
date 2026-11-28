using System.IO;

namespace CurrentLyrics.Services;

/// <summary>
/// 用户数据放在哪。三种安装形态各有一套规则（1.3.18 起）：
/// <list type="bullet">
/// <item>老布局（根目录 + <c>versions\v*</c>）：数据在根目录的 <c>data\</c>，换版本不丢设置。</item>
/// <item>绿色版 zip：exe 旁边有 <c>portable.txt</c> 才用 exe 旁边的 <c>data\</c>。</item>
/// <item>正常安装（Inno Setup）：<c>%LocalAppData%\Darius Music\data</c> —— 和程序目录分开，
/// 升级/卸载不跟程序文件打架，也不需要管理员权限。</item>
/// </list>
/// </summary>
public static class AppPaths
{
    public static string DataDir { get; } = Resolve();


    /// <summary>安装形态：只有 Inno 装出来的才有卸载项和固定目录；绿色版靠 portable.txt 判断。</summary>
    public static bool IsPortable =>
        !Services.Layout.IsVersioned && File.Exists(Path.Combine(AppContext.BaseDirectory, "portable.txt"));

    private static string Resolve()
    {
        // QA / 便携模式可以用环境变量指到别处，截图测试永远不会碰到用户真实设置。
        var custom = Environment.GetEnvironmentVariable("DARIUS_DATA_DIR");
        if (!string.IsNullOrWhiteSpace(custom) && Ensure(custom) is { } forced) return forced;

        // 老布局：根目录的 data\（和以前完全一致，用户升级过来什么都不用搬）。
        if (Services.Layout.IsVersioned && Ensure(Services.Layout.DataDir) is { } legacy) return legacy;

        // 绿色版：只有显式放了 portable.txt 才把数据写在程序旁边。
        if (File.Exists(Path.Combine(AppContext.BaseDirectory, "portable.txt")) &&
            Ensure(Path.Combine(AppContext.BaseDirectory, "data")) is { } portable) return portable;

        // 正常安装：exe 在 <LocalAppData>\Programs\Darius Music\，数据放上一层的 Darius Music\data。
        // 直接由 exe 路径推出来，不依赖 GetFolderPath（它在某些受限/提权上下文里会返回空）。
        var baseDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        var installedMarker = Path.Combine("Programs", "Darius Music");
        if (baseDir.EndsWith(installedMarker, StringComparison.OrdinalIgnoreCase))
        {
            var localRoot = Path.GetDirectoryName(Path.GetDirectoryName(baseDir));
            if (!string.IsNullOrWhiteSpace(localRoot) &&
                Ensure(Path.Combine(localRoot, "Darius Music", "data")) is { } installed)
                return installed;
        }

        // 兜底：用系统 API 拿 LocalAppData（绿色版放在别处时走这条）。
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData))
            localAppData = Environment.GetEnvironmentVariable("LOCALAPPDATA") ?? "";
        if (!string.IsNullOrWhiteSpace(localAppData) &&
            Ensure(Path.Combine(localAppData, "Darius Music", "data")) is { } installed2)
            return installed2;

        // 兜底：临时目录里也得能跑起来。
        if (Ensure(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Darius Music")) is { } roaming)
            return roaming;
        return AppContext.BaseDirectory;
    }

    /// <summary>建目录并做一次写测试；不可写返回 null（调用方试下一个候选）。</summary>
    private static string? Ensure(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            var probe = Path.Combine(path, ".write-test");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return path;
        }
        catch (Exception e)
        {
            // 落选的原因要留痕：数据目录选错是"设置丢了"这类问题里最难查的一环。
            Trace(path + " -> " + e.GetType().Name + ": " + e.Message);
            return null;
        }
    }

    private static void Trace(string line)
    {
        try
        {
            File.AppendAllText(Path.Combine(Path.GetTempPath(), "darius-apppaths.log"),
                DateTime.Now.ToString("MM-dd HH:mm:ss") + " " + line + Environment.NewLine);
        }
        catch { }
    }
}
