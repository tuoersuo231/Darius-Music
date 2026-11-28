using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Media;

namespace CurrentLyrics.Services;

/// <summary>
/// 底部频谱的全部可调参数。存在**独立文件**里：
/// <code>&lt;数据目录&gt;\spectrum.json</code>（正常安装就是 <c>E:\Darius Music\data\spectrum.json</c>）。
///
/// 第一次启动时如果这个文件不存在，程序会写一份默认值出来 —— 用户不需要去几百行的
/// settings.json 里翻。改完**重启**生效。
/// </summary>
public sealed class SpectrumSettings
{
    /// <summary>关掉整条不显示，也不开 WASAPI 回环（省一点 CPU）。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// 只抓本程序自己的声音（进程回环），别的播放器/网页不会让频谱跳。
    /// 关掉 = 抓整块声卡的混音，系统里任何声音都会动。
    /// </summary>
    public bool OwnAudioOnly { get; set; } = true;

    /// <summary>条数。24 在 1166 px 宽下每根 ~48 px 偏粗；48 每根 ~24 px 更接近皮肤。</summary>
    public int Bars { get; set; } = 24;

    /// <summary>整条的高度（DIP）。</summary>
    public double Height { get; set; } = 72;

    /// <summary>条的颜色，#RRGGBB 或 RRGGBB。</summary>
    public string Color { get; set; } = "#C77DFF";

    /// <summary>最近用过的颜色（#RRGGBB），色盘里点过的会自动记到最前面。</summary>
    public List<string> RecentColors { get; set; } = new()
    {
        "C77DFF", "00E5FF", "7DFF9E", "FFD166", "FF7D9C", "FFFFFF",
    };

    /// <summary>条与条之间的空隙（DIP）。</summary>
    public double Gap { get; set; } = 3;

    /// <summary>条的圆角半径（DIP），0 = 直角。</summary>
    public double CornerRadius { get; set; } = 1.5;

    /// <summary>整条的不透明度，0..1。</summary>
    public double Opacity { get; set; } = 1.0;

    /// <summary>
    /// 面板让出多少透明（0..80，百分比）。只在 <c>Layer=backdrop</c> 时有用：
    /// 频谱藏在面板后面，面板按这个值变半透明，柱子才透得出来。
    /// 注意：面板越透，壁纸的颜色会渗进柱子，颜色会偏；<c>overlay</c> 层不会。
    /// </summary>
    public int PanelSeeThrough { get; set; } = 0;

    /// <summary>频段下限（Hz）。600 是皮肤的值；调到 60 会有明显重低音。</summary>
    public double FreqMin { get; set; } = 600;

    /// <summary>频段上限（Hz）。皮肤写 10000，但流行歌到 8k 就没能量了。</summary>
    public double FreqMax { get; set; } = 8000;

    /// <summary>每往右一条的增益倍数（补偿高频天然衰减）。1.14 大约每档 +1.2 dB。</summary>
    public double BoostPerBar { get; set; } = 1.14;

    /// <summary>归一化下限（dB）。低于它算 0。越小条越容易起来。</summary>
    public double FloorDb { get; set; } = -46;

    /// <summary>归一化跨度（dB）。调大 → 条变矮、层次更细；调小 → 条更满、更激烈。</summary>
    public double SpanDb { get; set; } = 26;

    /// <summary>
    /// 自动增益：让"当前最响的那条"自动顶到 <c>FloorDb + SpanDb*0.92</c>。
    /// 系统音量小、歌本身录得轻的时候，柱子不会缩成一条线。参考电平快上慢落，所以副歌不会爆。
    /// </summary>
    public bool AutoGain { get; set; } = true;

    /// <summary>实心条的衰减时间（ms）。小 = 跟手，大 = 粘。</summary>
    public double FastMs { get; set; } = 150;

    /// <summary>残影的衰减时间（ms）。应该明显大于 FastMs。</summary>
    public double SlowMs { get; set; } = 800;

    /// <summary>画不画残影（慢衰减那一截的淡色）。</summary>
    public bool Ghost { get; set; } = true;

    /// <summary>残影的不透明度（0..255）。</summary>
    public int GhostAlpha { get; set; } = 70;

    /// <summary>
    /// 落点（峰值小点）：柱子顶上那根按**慢衰减**下落的小横条。
    /// 和残影不是一回事 —— 残影是变矮的一整根柱子，落点是脱开柱顶的一个小点，
    /// 所以快回落时它会在柱子上面悬一下再掉下来。原版皮肤也是两路独立数据。
    /// </summary>
    public bool Peak { get; set; } = true;

    /// <summary>落点粗细（DIP）。原版是 2 px 的小横条。</summary>
    public double PeakThickness { get; set; } = 3;

    /// <summary>
    /// 量化级数。0/1 = 平滑（连续值，现在的手感）；20 = 原版「段落」，每档 5%。
    /// 只量化实心柱 —— 残影和落点保持连续，原版同样是两路（mcalc 量化、Lcalc 原始）。
    /// </summary>
    public int Steps { get; set; } = 0;

    /// <summary>镜像布局：中间低频、两边高频。false = 从左到右由低到高。</summary>
    public bool Mirror { get; set; } = true;

    /// <summary>
    /// 画在哪一层。**1.3.16 起自动判断**，这个字段只剩"上次用的是哪层"的记录价值：
    /// 面板本身够透（背景色带 alpha，或有背景图时面板自动变透）就夹在壁纸和面板之间，
    /// 面板不透明就浮在界面之上 —— 否则夹在中间等于看不见（用户：关掉这个开关频谱就消失了）。
    /// </summary>
    public string Layer { get; set; } = "overlay";

    /// <summary>实际生效的层级，由 <see cref="SpectrumOptions.Backdrop"/> 决定（自动判断）。</summary>
    [JsonIgnore]
    public bool IsBackdrop => SpectrumOptions.Backdrop;

    // ---------- 文件 ----------

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        // 让中文注释性的字段名保持原样，不要变成 \uXXXX。
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string FilePath => Path.Combine(AppPaths.DataDir, "spectrum.json");

    /// <summary>读配置；文件不存在就写一份默认值出来，让用户知道有哪些能调。</summary>
    public static SpectrumSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var loaded = JsonSerializer.Deserialize<SpectrumSettings>(File.ReadAllText(FilePath), Json);
                if (loaded != null)
                {
                    var clean = loaded.Sanitized();
                    // 补写一遍：老文件里没有这一版新增的字段（比如 Layer），用户打开文件看不见就没法调。
                    try { clean.Write(); } catch { }
                    return clean;
                }
            }
        }
        catch { }
        var fresh = new SpectrumSettings();
        try { fresh.Write(); } catch { }
        return fresh;
    }

    public void Write()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataDir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json), new UTF8Encoding(false));
        }
        catch { }
    }

    /// <summary>手改过的文件可能填 0、负数、NaN —— 夹一下，别让界面算出空数组或 0 高度。</summary>
    private SpectrumSettings Sanitized()
    {
        Bars = Math.Clamp(Bars <= 0 ? 24 : Bars, 4, 128);
        Height = double.IsNaN(Height) || Height < 8 ? 72 : Math.Min(2000, Height);
        Gap = double.IsNaN(Gap) || Gap < 0 ? 0 : Math.Min(40, Gap);
        CornerRadius = double.IsNaN(CornerRadius) || CornerRadius < 0 ? 0 : Math.Min(20, CornerRadius);
        Opacity = double.IsNaN(Opacity) || Opacity <= 0 ? 1 : Math.Min(1, Opacity);
        FreqMin = double.IsNaN(FreqMin) || FreqMin < 20 ? 600 : Math.Min(18000, FreqMin);
        FreqMax = double.IsNaN(FreqMax) || FreqMax <= FreqMin ? FreqMin + 200 : Math.Min(20000, FreqMax);
        BoostPerBar = double.IsNaN(BoostPerBar) || BoostPerBar < 1 ? 1 : Math.Min(1.6, BoostPerBar);
        FloorDb = double.IsNaN(FloorDb) ? -46 : Math.Clamp(FloorDb, -120, 0);
        SpanDb = double.IsNaN(SpanDb) || SpanDb < 1 ? 26 : Math.Min(120, SpanDb);
        FastMs = double.IsNaN(FastMs) || FastMs < 20 ? 150 : Math.Min(2000, FastMs);
        SlowMs = double.IsNaN(SlowMs) || SlowMs < FastMs ? Math.Max(FastMs, 800) : Math.Min(4000, SlowMs);
        GhostAlpha = Math.Clamp(GhostAlpha <= 0 ? 70 : GhostAlpha, 0, 255);
        PeakThickness = double.IsNaN(PeakThickness) || PeakThickness < 1 ? 3 : Math.Min(16, PeakThickness);
        Steps = Math.Clamp(Steps, 0, 60);
        // Layer 1.3.16 起只是"上次落在哪一层"的记录，层级由面板透明度自动判断，这里不再改写它。
        PanelSeeThrough = Math.Clamp(PanelSeeThrough, 0, 80);

        // 颜色和"最近用过"都规整成 6 位大写十六进制，去重、限长。
        var recents = new List<string>();
        foreach (var raw in RecentColors ?? new List<string>())
        {
            var hex = NormalizeHex(raw);
            if (hex.Length != 6 || recents.Contains(hex)) continue;
            recents.Add(hex);
            if (recents.Count >= 8) break;
        }
        var current = NormalizeHex(Color);
        if (current.Length == 6)
        {
            recents.RemoveAll(h => string.Equals(h, current, StringComparison.OrdinalIgnoreCase));
            recents.Insert(0, current);
            Color = "#" + current;
        }
        if (recents.Count > 8) recents.RemoveRange(8, recents.Count - 8);
        RecentColors = recents.Count > 0 ? recents : new List<string> { "C77DFF" };
        return this;
    }

    /// <summary>把 "#aabbcc" / "AABBCC" 规整成 6 位大写十六进制；不合法返回空串。</summary>
    public static string NormalizeHex(string? hex)
    {
        var text = (hex ?? "").Trim().TrimStart('#');
        if (text.Length != 6) return "";
        foreach (var ch in text)
            if (!Uri.IsHexDigit(ch)) return "";
        return text.ToUpperInvariant();
    }

    /// <summary>把 #RRGGBB / RRGGBB 解析成颜色；解析不了就用默认紫粉。</summary>
    public Color ResolveColor()
    {
        var text = (Color ?? "").Trim().TrimStart('#');
        if (text.Length == 6 &&
            byte.TryParse(text.AsSpan(0, 2), System.Globalization.NumberStyles.HexNumber, null, out var r) &&
            byte.TryParse(text.AsSpan(2, 2), System.Globalization.NumberStyles.HexNumber, null, out var g) &&
            byte.TryParse(text.AsSpan(4, 2), System.Globalization.NumberStyles.HexNumber, null, out var b))
            return System.Windows.Media.Color.FromRgb(r, g, b);
        return System.Windows.Media.Color.FromRgb(0xC7, 0x7D, 0xFF);
    }
}

/// <summary>当前生效的那一份。AppSettings.Load() 之后填进来，控件和服务都读这里。</summary>
public static class SpectrumOptions
{
    public static SpectrumSettings Current { get; set; } = new();

    /// <summary>
    /// 频谱该夹在壁纸和面板之间吗？**永远是 true**（1.3.17 起）：
    /// 用户要求"一定要在 UI 后一层，不能挡住 UI"，并且不接受背景色/背景图影响这个选择。
    /// 柱子被面板挡住时靠「面板透出」让面板让一点透明。
    /// </summary>
    public const bool Backdrop = true;
}