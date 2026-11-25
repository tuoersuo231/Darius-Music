using System.IO;
using System.Windows.Media;

namespace CurrentLyrics;

/// <summary>Resolves the lyric typeface. "霞鹜文楷" ships with the app (extracted from the
/// CurrentMusic APK); "系统" falls back to Microsoft YaHei UI, the Windows equivalent of the
/// Android system CJK face the app uses by default.</summary>
public static class FontResolver
{
    private static readonly Dictionary<string, FontFamily> Cache = new();
    public const string WenKai = "LXGW WenKai";
    public const string System = "系统字体";

    public static string FontDirectory => Path.Combine(AppContext.BaseDirectory, "Fonts");

    public static FontFamily Resolve(string name)
    {
        name ??= WenKai;
        if (Cache.TryGetValue(name, out var cached)) return cached;

        FontFamily family;
        if (name.Contains("LXGW", StringComparison.OrdinalIgnoreCase) || name.Contains("霞鹜"))
        {
            var file = Path.Combine(FontDirectory, "LXGWWenKai-Medium.ttf");
            if (File.Exists(file))
            {
                // Private font: reference the file folder and pick the family inside it.
                family = new FontFamily(new Uri(FontDirectory + Path.DirectorySeparatorChar, UriKind.Absolute),
                    "./#LXGW WenKai Medium");
            }
            else
            {
                family = new FontFamily("Microsoft YaHei UI");
            }
        }
        else
        {
            family = new FontFamily("Microsoft YaHei UI");
        }

        Cache[name] = family;
        return family;
    }
}
