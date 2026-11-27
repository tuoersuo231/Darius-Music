using System.Windows.Media;

namespace CurrentLyrics.Services;

/// <summary>
/// 底部频谱的全部可调参数。首次运行会写进 <c>settings.json</c> 的 <c>Spectrum</c> 节点，
/// 改完重启程序生效（没有做热加载）。
///
/// 想要更"跳"就把 <see cref="BoostPerBar"/> 调大、<see cref="SpanDb"/> 调小；
/// 想要更"稳"就反过来。想要更细密就把 <see cref="Bars"/> 加到 48。
/// </summary>
public sealed class SpectrumSettings
{
    /// <summary>关掉就整条不显示（也不开 WASAPI 回环，省一点 CPU）。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>条数。24 在 1166 px 宽下每根 ~48 px，偏粗；48 每根 ~24 px 更接近皮肤。</summary>
    public int Bars { get; set; } = 24;

    /// <summary>整条的高度（DIP）。72 是现在的默认值。</summary>
    public double Height { get; set; } = 72;

    /// <summary>条的颜色，十六进制（#RRGGBB 或 RRGGBB）。</summary>
    public string Color { get; set; } = "#C77DFF";

    /// <summary>条与条之间的空隙（DIP）。</summary>
    public double Gap { get; set; } = 3;

    /// <summary>条的圆角半径（DIP），0 = 直角。</summary>
    public double CornerRadius { get; set; } = 1.5;

    /// <summary>整条的不透明度，0..1。</summary>
    public double Opacity { get; set; } = 1.0;

    /// <summary>频段下限（Hz）。600 是皮肤的值；调低到 60 会更"重低音"。</summary>
    public double FreqMin { get; set; } = 600;

    /// <summary>频段上限（Hz）。皮肤写 10000，但流行歌到 8k 就没能量了，硬撑会让右边几根永远是 0。</summary>
    public double FreqMax { get; set; } = 8000;

    /// <summary>每往右一条的增益倍数（补偿高频天然衰减）。1.14 大约每条约 +1.2 dB。</summary>
    public double BoostPerBar { get; set; } = 1.14;

    /// <summary>归一化下限（dB）。低于它的算 0。越小条越"容易起来"。</summary>
    public double FloorDb { get; set; } = -46;

    /// <summary>归一化跨度（dB）。从 <see cref="FloorDb"/> 到 FloorDb+SpanDb 映射到 0..1。
    /// 调大 → 条变矮、层次更细；调小 → 条更满、更激烈。</summary>
    public double SpanDb { get; set; } = 26;

    /// <summary>实心条的衰减时间（ms）。小 = 更"跟手"，大 = 更"粘"。</summary>
    public double FastMs { get; set; } = 150;

    /// <summary>残影的衰减时间（ms）。应该明显大于 <see cref="FastMs"/>。</summary>
    public double SlowMs { get; set; } = 800;

    /// <summary>画不画残影（慢衰减那一截的淡色）。</summary>
    public bool Ghost { get; set; } = true;

    /// <summary>残影的不透明度（0..255）。</summary>
    public int GhostAlpha { get; set; } = 70;

    /// <summary>照皮肤的做法：中间低频、两边高频的镜像布局。false 就是从低到高从左排到右。</summary>
    public bool Mirror { get; set; } = true;

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