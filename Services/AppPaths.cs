using System.IO;

namespace CurrentLyrics.Services;

/// <summary>Where the app keeps its own data (settings, cover cache, pulled WSA databases).
/// Portable first: next to the executable, falling back to %APPDATA% when that folder is
/// read-only.</summary>
public static class AppPaths
{
    public static string DataDir { get; } = Resolve();

    private static string Resolve()
    {
        // QA / portable runs can point the data folder elsewhere so a screenshot pass never touches
        // the settings the user is actually living with.
        var custom = Environment.GetEnvironmentVariable("DARIUS_DATA_DIR");
        if (!string.IsNullOrWhiteSpace(custom))
        {
            try
            {
                Directory.CreateDirectory(custom);
                return custom;
            }
            catch
            {
                // Fall through to the normal candidates.
            }
        }
        foreach (var candidate in new[]
                 {
                     // 新版布局：data 永远在安装根目录，换版本（versions\v*）不会丢设置。
                     Services.Layout.DataDir,
                     Path.Combine(AppContext.BaseDirectory, "data"),
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CurrentLyrics"),
                     Path.Combine(Path.GetTempPath(), "CurrentLyrics"),
                 })
        {
            try
            {
                Directory.CreateDirectory(candidate);
                var probe = Path.Combine(candidate, ".write-test");
                File.WriteAllText(probe, "ok");
                File.Delete(probe);
                return candidate;
            }
            catch
            {
                // Try the next location.
            }
        }
        return AppContext.BaseDirectory;
    }
}
