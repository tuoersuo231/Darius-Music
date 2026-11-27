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

    /// <summary>条数。24 在 1166 px 宽下每根 ~48 px 偏粗；48 每根 ~24 px 更接近皮肤。</summary>
    public int Bars { get; set; } = 24;

    /// <summary>整条的高度（DIP）。</summary>
    public double Height { get; set; } = 72;

    /// <summary>条的颜色，#RRGGBB 或 RRGGBB。</summary>
    public string Color { get; set; } = "#C77DFF";

    /// <summary>条与条之间的空隙（DIP）。</summary>
    public double Gap { get; set; } = 3;

    /// <summary>条的圆角半径（DIP），0 = 直角。</summary>
    public double CornerRadius { get; set; } = 1.5;

    /// <summary>整条的不透明度，0..1。</summary>
    public double Opacity { get; set; } = 1.0;

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

    /// <summary>实心条的衰减时间（ms）。小 = 跟手，大 = 粘。</summary>
    public double FastMs { get; set; } = 150;

    /// <summary>残影的衰减时间（ms）。应该明显大于 FastMs。</summary>
    public double SlowMs { get; set; } = 800;

    /// <summary>画不画残影（慢衰减那一截的淡色）。</summary>
    public bool Ghost { get; set; } = true;

    /// <summary>残影的不透明度（0..255）。</summary>
    public int GhostAlpha { get; set; } = 70;

    /// <summary>镜像布局：中间低频、两边高频。false = 从左到右由低到高。</summary>
    public bool Mirror { get; set; } = true;

    /// <summary>
    /// 画在哪一层。
    /// <c>overlay</c>（默认）= 浮在界面之上、贴住窗口底边，不参与布局，看得见但会盖住播放条那一条；
    /// <c>backdrop</c> = 夹在壁纸和面板之间（真正的"UI 后层"），面板不透明时会被整块挡住，
    /// 要把「界面外观 → 主题不透明度」降到 60% 左右才透得出来。
    /// </summary>
    public string Layer { get; set; } = "overlay";

    /// <summary>Layer 是否等于 backdrop。</summary>
    [JsonIgnore]
    public bool IsBackdrop =>
        string.Equals((Layer ?? "").Trim(), "backdrop", StringComparison.OrdinalIgnoreCase);

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
        Height = double.IsNaN(Height) || Height < 8 ? 72 : Math.Min(400, Height);
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
        Layer = IsBackdrop ? "backdrop" : "overlay";
        return this;
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
}