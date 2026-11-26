using System.Windows;
using System.Windows.Media;

namespace CurrentLyrics.Services;

/// <summary>
/// Rebuilds the whole colour ramp from two user choices (accent + surface) and swaps it into the
/// application resources. Every themed brush is referenced with DynamicResource, so the window
/// recolours live without a restart.
/// </summary>
public static class Palette
{
    public const string DefaultAccent = "4C8DFF";
    public const string DefaultSurface = "E8EEF8";

    /// <summary>Presets offered in the settings window.</summary>
    public static readonly (string Hex, string Name)[] Accents =
    {
        ("4C8DFF", "蓝"), ("7C5CFF", "紫"), ("12B886", "绿"),
        ("FF7A59", "橙"), ("FF5C8A", "粉"), ("22B8CF", "青"),
        ("E8B84B", "金"), ("8A94A6", "灰"),
    };

    public static readonly (string Hex, string Name)[] Surfaces =
    {
        ("FFFFFF", "白"), ("E8EEF8", "雾蓝"), ("F4EFE6", "米白"),
        ("E7F2EE", "淡青"), ("F6EDF2", "藕粉"), ("EEF0F4", "浅灰"),
    };

    /// <summary>Sentinel stored in <c>ThemeImage</c> for "use the playing track's cover".</summary>
    public const string CoverToken = "@cover";

    /// <summary>
    /// Six hex digits = an opaque colour. Eight = AARRGGBB, where the alpha is how much of that colour
    /// the panels paint over the cover image: "00RRGGBB" is the 背景色 = 透明 option (the picture shows
    /// through the side bar and the cards).
    /// </summary>
    public static string Normalize(string hex, string fallback)
    {
        var text = (hex ?? "").Trim().TrimStart('#');
        if (text.Length != 6 && text.Length != 8) return fallback;
        foreach (var c in text)
            if (!Uri.IsHexDigit(c)) return fallback;
        return text.ToUpperInvariant();
    }

    public static Color Parse(string hex) => hex.Length == 8
        ? Color.FromArgb(
            Convert.ToByte(hex[..2], 16),
            Convert.ToByte(hex.Substring(2, 2), 16),
            Convert.ToByte(hex.Substring(4, 2), 16),
            Convert.ToByte(hex.Substring(6, 2), 16))
        : Color.FromRgb(
            Convert.ToByte(hex[..2], 16),
            Convert.ToByte(hex.Substring(2, 2), 16),
            Convert.ToByte(hex.Substring(4, 2), 16));

    /// <summary>Alpha of a surface colour, 255 when it is a plain six-digit colour.</summary>
    public static byte SurfaceAlpha(string hex) => hex.Length == 8
        ? Convert.ToByte(hex[..2], 16)
        : (byte)0xFF;

    public static string Hex(Color color) => $"{color.R:X2}{color.G:X2}{color.B:X2}";

    /// <summary>
    /// One light paper palette. The user's 背景色 only supplies hue and saturation — lightness is
    /// pinned high, so the interface stays readable no matter what is picked, and the colour is
    /// carried by the side bar and the top band instead of bleeding into every panel.
    /// </summary>
    public static void Apply(string accentHex, string surfaceHex, bool glassy = false)
    {
        var resources = Application.Current?.Resources;
        if (resources == null) return;

        var seed = Parse(Normalize(surfaceHex, DefaultSurface));
        byte surfaceAlpha = SurfaceAlpha(Normalize(surfaceHex, DefaultSurface));
        RgbToHsl(seed, out var hue, out var sat, out var seedLum);
        // A pick from the corner of the square is "black", not "black green": only colours with no
        // measurable channel at all are forced neutral, so a dark red keeps its red.
        int maxChannel = Math.Max(seed.R, Math.Max(seed.G, seed.B));
        if (sat < 0.015f || maxChannel < 12) { sat = 0f; hue = 0.60f; }
        float s = Math.Min(sat, 0.62f);

        // The interaction colour follows the same pick: a saturated, contrasting version of the hue.
        // Neutral picks (white / grey / black) have no hue to follow, so they keep the brand blue.
        bool lightFace = RelLum(seed) >= 0.22;
        var accent = Color.FromRgb(0x2F, 0x6B, 0xFF);
        if (s >= 0.06f)
            accent = HslToRgb(hue, Math.Clamp(s * 1.45f, 0.45f, 0.92f), lightFace ? 0.455f : 0.620f);
        RgbToHsl(accent, out var accentHue, out var accentSat, out var accentLum);
        var accentHi = HslToRgb(accentHue, accentSat, Math.Min(0.92f, accentLum + 0.12f));
        var accentLo = HslToRgb(accentHue, accentSat, Math.Max(0.10f, accentLum - 0.14f));

        // One continuous ramp, no modes: the panel is exactly the colour that was picked and every
        // other layer is a fixed step away from it. Dragging the picker therefore walks the whole
        // window from white to black smoothly instead of flipping between two layouts.
        Color Step(float delta) => HslToRgb(hue, s, (float)Math.Clamp(seedLum + delta, 0f, 1f));

        float sign = lightFace ? -1f : 1f;      // deeper shading goes away from the face colour

        // RGB only: the alpha belongs to the panels (plate), never to the ink blended out of it.
        var card = Color.FromRgb(seed.R, seed.G, seed.B);
        // Flat surface: the side bar, header band, list and player bar are all the picked colour
        // itself. Depth comes from hairlines and the small steps below, never from a second base
        // colour — otherwise those blocks read as a different tint from the rest.
        var window = card;
        var panel = Step(-sign * 0.022f);
        var raised = Step(-sign * 0.034f);
        var inset = Step(sign * 0.048f);
        var hairline = Step(sign * 0.090f);
        var hairlineSoft = Step(sign * 0.052f);
        var hover = Step(sign * 0.042f);
        var cardEdge = card;
        var washCore = card;

        // Selection is a stronger step of the same colour rather than a foreign accent tint, so the
        // ink colour that was chosen for this panel stays readable on top of it.
        var selected = Step(sign * 0.085f);
        var accentSoft = Step(-sign * 0.035f);

        // Ink is mixed out of the face colour, so the second and third text levels keep their
        // contrast whether the panel is white, mid grey or black.
        var ink = lightFace ? Color.FromRgb(0x12, 0x14, 0x18) : Color.FromRgb(0xF2, 0xF3, 0xF6);
        var text = Blend(ink, card, 0.02);
        var dim = Blend(ink, card, 0.30);
        var faint = Blend(ink, card, 0.48);

        // With a photo behind the window the cards go translucent so the picture actually reads.
        // An explicit alpha in the 背景色 (the 透明 option: "00RRGGBB") wins and is used verbatim: 00 means
        // the window really is see-through, with no tint of our own on top.
        byte plate = surfaceAlpha < 0xFF ? surfaceAlpha : glassy ? (byte)0xDA : (byte)0xFF;

        Set(resources, "WindowBrush", Solid(window));
        Set(resources, "SidebarBrush", Solid(WithAlpha(window, plate)));
        Set(resources, "CardBrush", Solid(WithAlpha(card, plate)));
        Set(resources, "SurfaceBrush", Solid(WithAlpha(card, plate)));
        Set(resources, "SurfaceRaisedBrush", Solid(WithAlpha(panel, plate)));
        Set(resources, "InsetBrush", Solid(WithAlpha(inset, plate)));
        // No hairlines anywhere (user request, 1.2.66): the app is meant to read as floating text and
        // artwork, with or without a background colour. Structure comes from spacing, not from rules.
        Set(resources, "HairlineBrush", Solid(Colors.Transparent));
        Set(resources, "HairlineSoftBrush", Solid(Colors.Transparent));
        // Selection follows the panels too: at plate=0 there is no fill at all, and the row keeps its
        // SemiBold weight (nav buttons get the accent foreground in code) so "which one is selected" is
        // still readable.
        Set(resources, "NavActiveBrush", Solid(WithAlpha(selected, plate)));
        Set(resources, "RowHoverBrush", Solid(WithAlpha(hover, plate)));
        Set(resources, "RowSelectedBrush", Solid(WithAlpha(selected, plate)));
        Set(resources, "AccentBrush", Solid(accent));
        Set(resources, "AccentHiBrush", Solid(accentHi));
        Set(resources, "AccentSoftBrush", Solid(WithAlpha(accentSoft, plate)));
        Set(resources, "AccentColor", accent);
        Set(resources, "TextBrush", Solid(text));
        Set(resources, "TextDimBrush", Solid(dim));
        Set(resources, "TextFaintBrush", Solid(faint));

        var gradient = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(1, 1),
            GradientStops =
            {
                new GradientStop(accentHi, 0),
                new GradientStop(accent, 0.55),
                new GradientStop(accentLo, 1),
            },
        };
        gradient.Freeze();
        Set(resources, "AccentGradientBrush", gradient);

        var cardGradient = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(0, 1),
            GradientStops =
            {
                new GradientStop(WithAlpha(card, plate), 0),
                new GradientStop(WithAlpha(cardEdge, plate), 1),
            },
        };
        cardGradient.Freeze();
        Set(resources, "CardGradientBrush", cardGradient);

        var wash = new RadialGradientBrush
        {
            GradientOrigin = new Point(0.12, 0.02),
            Center = new Point(0.12, 0.02),
            RadiusX = 1.1,
            RadiusY = 0.9,
            GradientStops =
            {
                new GradientStop(washCore, 0),
                new GradientStop(window, 0.62),
            },
        };
        wash.Freeze();
        Set(resources, "WindowWashBrush", wash);

        var scrim = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(0, 1),
            GradientStops =
            {
                new GradientStop(WithAlpha(window, 0x1F), 0),
                new GradientStop(WithAlpha(window, 0x8C), 0.55),
                new GradientStop(WithAlpha(window, 0xFF), 1),
            },
        };
        scrim.Freeze();
        Set(resources, "HeaderScrimBrush", scrim);

        // The hairline above the player bar: the accent at 40%, fading out at both ends.
        var accentLine = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(1, 0),
            GradientStops =
            {
                new GradientStop(WithAlpha(accent, 0x00), 0),
                new GradientStop(WithAlpha(accent, 0x6E), 0.3),
                new GradientStop(WithAlpha(accent, 0x00), 0.78),
            },
        };
        accentLine.Freeze();
        Set(resources, "AccentLineBrush", accentLine);
    }
    private static void Set(ResourceDictionary resources, string key, object value)
    {
        resources[key] = value;
    }

    /// <summary>QA: the resolved colours, so a screenshot can be checked against the actual ramp.</summary>
    public static string Describe()
    {
        var resources = Application.Current?.Resources;
        if (resources == null) return "";
        var keys = new[] { "WindowBrush", "SidebarBrush", "CardBrush", "SurfaceRaisedBrush", "InsetBrush", "RowHoverBrush", "RowSelectedBrush", "NavActiveBrush", "TextBrush", "AccentSoftBrush", "HairlineBrush" };
        var parts = new List<string>();
        foreach (var key in keys)
            parts.Add(key + "=" + (resources[key] is SolidColorBrush brush ? brush.Color.ToString() : "?"));
        return string.Join(" ", parts);
    }

    private static Color DeriveAccent(Color surface)
    {
        RgbToHsl(surface, out var h, out var s, out _);
        return HslToRgb(h, Math.Max(0.62f, Math.Min(0.86f, s + 0.5f)), 0.62f);
    }

    private static Color WithAlpha(Color color, byte alpha) => Color.FromArgb(alpha, color.R, color.G, color.B);

    private static SolidColorBrush Solid(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    /// <summary>Moves a colour up the ladder by <paramref name="percent"/> of full lightness.</summary>
    private static Color Shift(Color color, float percent)
    {
        float h = 0, s = 0, l = 0;
        RgbToHsl(color, out h, out s, out l);
        double target = Math.Clamp(l + percent / 100.0 * (1 - l) * 0.55 + percent / 100.0 * 0.35, 0, 1);
        var lowered = Math.Abs(target - l) < 0.0001 ? l : target;
        return HslToRgb(h, s, (float)lowered);
    }

    private static Color Lighten(Color color, double amount)
    {
        float h, s, l;
        RgbToHsl(color, out h, out s, out l);
        return HslToRgb(h, s, (float)Math.Clamp(l + amount, 0, 1));
    }

    private static Color Blend(Color a, Color b, double t)
    {
        t = Math.Clamp(t, 0, 1);
        return Color.FromRgb(
            (byte)(a.R + (b.R - a.R) * t),
            (byte)(a.G + (b.G - a.G) * t),
            (byte)(a.B + (b.B - a.B) * t));
    }

    private static double Luminance(Color c) => (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0;

    /// <summary>Perceptual luminance in 0..1, used to decide ink colour by real contrast.</summary>
    private static double RelLum(Color c)
    {
        static double Ch(byte v)
        {
            double s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Ch(c.R) + 0.7152 * Ch(c.G) + 0.0722 * Ch(c.B);
    }

    private static void RgbToHsl(Color color, out float h, out float s, out float l)
    {
        float r = color.R / 255f, g = color.G / 255f, b = color.B / 255f;
        float max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        l = (max + min) / 2f;
        if (Math.Abs(max - min) < 0.0001f)
        {
            h = 0; s = 0;
            return;
        }
        float d = max - min;
        s = l > 0.5f ? d / (2f - max - min) : d / (max + min);
        if (Math.Abs(max - r) < 0.0001f) h = (g - b) / d + (g < b ? 6f : 0f);
        else if (Math.Abs(max - g) < 0.0001f) h = (b - r) / d + 2f;
        else h = (r - g) / d + 4f;
        h /= 6f;
    }

    private static Color HslToRgb(float h, float s, float l)
    {
        if (s <= 0.0001f)
        {
            byte v = (byte)Math.Round(Math.Clamp(l, 0, 1) * 255);
            return Color.FromRgb(v, v, v);
        }
        float q = l < 0.5f ? l * (1 + s) : l + s - l * s;
        float p = 2 * l - q;
        return Color.FromRgb(
            (byte)Math.Round(Component(p, q, h + 1f / 3f) * 255),
            (byte)Math.Round(Component(p, q, h) * 255),
            (byte)Math.Round(Component(p, q, h - 1f / 3f) * 255));
    }

    private static float Component(float p, float q, float t)
    {
        if (t < 0) t += 1;
        if (t > 1) t -= 1;
        if (t < 1f / 6f) return p + (q - p) * 6 * t;
        if (t < 1f / 2f) return q;
        if (t < 2f / 3f) return p + (q - p) * (2f / 3f - t) * 6;
        return p;
    }
}
