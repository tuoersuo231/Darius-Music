using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CurrentLyrics.Services;

/// <summary>What sits behind the lyric column.</summary>
public enum BackdropMode { Transparent, Cover, Image, Color }

/// <summary>What happens when a track ends.</summary>
public enum PlayMode { Sequential, RepeatOne }

/// <summary>Mirror of CurrentMusic's DesktopLyricsSettings (see
/// feature/lyrics/desktop/DesktopLyricsSettings.kt) plus the few desktop-only extras.</summary>
public sealed class AppSettings
{
    public bool Enabled { get; set; } = true;
    public bool Translation { get; set; } = true;
    public double FontSize { get; set; } = 64;      // current-line size, 28..120
    public double Opacity { get; set; } = 0.92;     // .35..1
    public long OffsetMs { get; set; } = 0;          // global lyric offset
    public Dictionary<string, long> SongOffsets { get; set; } = new();  // per-song override
    public double Height { get; set; } = 0.05;      // legacy island slot
    public double LineWidth { get; set; } = 680;    // wrap width of the lyric column
    public double LineGap { get; set; } = 6;        // extra gap between cues
    public double Anchor { get; set; } = 0.5;       // vertical reading anchor, 0..1
    public bool Backdrop { get; set; } = false;     // legacy soft scrim
    public BackdropMode BackdropMode { get; set; } = BackdropMode.Cover;
    public string BackdropColor { get; set; } = "0E1118";
    public string BackdropImage { get; set; } = "";
    public double BackdropBlur { get; set; } = 24;  // frosted glass strength, 0..60
    public double BackdropOpacity { get; set; } = 0.78;
    /// <summary>0 = fit the lyrics; otherwise the requested size in DIPs (never below the text).</summary>
    public double BackdropWidth { get; set; }
    public double BackdropHeight { get; set; }
    /// <summary>Horizontal fade band ceiling, as a percentage of the backdrop width: 1/7.</summary>
    public const double MaxFadeHPercent = 100.0 / 7.0;

    /// <summary>Vertical fade band ceiling, as a percentage of the backdrop height: 1/5.</summary>
    public const double MaxFadeVPercent = 20.0;

    public bool BackdropFadeEnabled { get; set; }
    public double BackdropFadeStrength { get; set; } = 65;   // 0..100
    public double BackdropFadeH { get; set; } = 8;          // % of width, max 1/7
    public double BackdropFadeV { get; set; } = 8;          // % of height, max 1/5
    public bool Perspective { get; set; } = false;  // 3D tilted lyric plane
    public double TiltAngle { get; set; } = -32;    // rotation around Y in degrees
    public double VanishingOffset { get; set; } = 1.6; // 交点距离，单位=列宽
    public int VisibleLines { get; set; } = 7;      // total cues on screen (odd)
    /// <summary>streaming quality requested from the proxy: standard | exhigh | lossless | jymaster</summary>
    public bool CustomPosition { get; set; }             // user dragged the overlay
    public double WindowX { get; set; }                  // dragged position (DIPs)
    public double WindowY { get; set; }
    public double CurrentScale { get; set; } = 1.56; // current line vs neighbours
    public bool GlowEnabled { get; set; } = true;
    public double GlowStrength { get; set; } = 0.7; // 0..2
    public double GlowSpread { get; set; } = 1;     // .5..2
    public string TextColor { get; set; } = "FFFFFF";
    public string GlowColor { get; set; } = "E0E0E0";

    // Desktop-only extras
    public bool Locked { get; set; } = false;       // true = mouse click-through
    public bool Topmost { get; set; } = true;
    /// <summary>Keep the overlay above *other topmost windows* too (games, players, Task Manager), by
    /// re-asserting HWND_TOPMOST whenever another window is activated. Transparent backdrop only — an
    /// opaque lyric panel parked over every application is not something anyone wants.</summary>
    public bool OverlayAboveApps { get; set; } = false;

    // ---- 快捷键（「一个修饰键 + 一个按键」，F1~F12 可单开；见 Services/Hotkeys.cs） ----
    /// <summary>One source of truth for the five defaults; the settings page and the QA modes read
    /// these instead of repeating the strings.</summary>
    public const string DefaultKeyAboveApps = "Ctrl+Q";
    public const string DefaultKeyLyrics = "F8";
    /// <summary>Ctrl+P since 1.2.60. It used to be a bare 「P」, which was window-local — pressing it
    /// while another application had focus did nothing, i.e. play/pause felt broken.</summary>
    public const string DefaultKeyPlayPause = "Ctrl+P";
    public const string DefaultKeyNext = "F6";
    public const string DefaultKeyPrevious = "F7";

    public string KeyAboveApps { get; set; } = DefaultKeyAboveApps;
    public string KeyLyrics { get; set; } = DefaultKeyLyrics;
    public string KeyPlayPause { get; set; } = DefaultKeyPlayPause;
    public string KeyNext { get; set; } = DefaultKeyNext;
    public string KeyPrevious { get; set; } = DefaultKeyPrevious;
    /// <summary>背景色「实色 ↔ 全透明」切换。默认空 = 不绑定，由用户自己在快捷键页设置。</summary>
    public string KeyTransparentBg { get; set; } = "";

    // ---- 界面外观（整个窗口共用） ----
    public string ThemeAccent { get; set; } = Palette.DefaultAccent;   // 主色
    public string ThemeSurface { get; set; } = Palette.DefaultSurface; // 背景色
    public double ThemeOpacity { get; set; } = 100;   // 背景不透明度 0..100
    public double ThemeBlur { get; set; } = 0;        // 背景磨砂 0..60 px
    public string ThemeImage { get; set; } = "";      // 自定义背景图，空 = 不用图
    public string UpdateFeed { get; set; } = "";      // 更新清单来源：文件夹或 http 地址

    // ---- 正在播放窗口（与桌面歌词完全独立的一套参数，互不影响） ----
    public double PlayerFontSize { get; set; } = 34;      // 歌词字号
    public int PlayerVisibleLines { get; set; } = 5;      // 可见行数
    public double PlayerCurrentScale { get; set; } = 1.35; // 当前行放大
    public double PlayerLineGap { get; set; } = 12;       // 行间距
    public double PlayerLineWidth { get; set; }           // 0 = 跟随窗口宽度
    public bool PlayerTranslation { get; set; } = false;  // 显示翻译
    public bool PlayerGlow { get; set; } = true;          // 辉光
    /// <summary>3D 倾斜歌词（播放器页自己的一套，和桌面歌词的 Perspective 无关）</summary>
    public bool PlayerPerspective { get; set; }
    public double PlayerTiltAngle { get; set; } = -32;     // 绕 Y 轴角度
    public double PlayerVanishingOffset { get; set; } = 1.6; // 交点距离，单位=列宽
    public double PlayerGlowStrength { get; set; } = 0.7; // 辉光强度 0..2
    public double PlayerGlowSpread { get; set; } = 1;      // 辉光范围 0.5..2（半径系数由它推）
    public string PlayerGlowColor { get; set; } = "E0E0E0"; // 辉光颜色
    public string PlayerTextColor { get; set; } = "FFFFFF"; // 歌词文字颜色
    public double PlayerBackdropBlur { get; set; } = 42;  // 背景模糊
    public double PlayerBackdropOpacity { get; set; } = 0.62; // 背景暗度（越大越暗）
    public double Scale { get; set; } = 1.0;        // global size multiplier
    public string FontFamily { get; set; } = "Microsoft YaHei UI";
    public double Volume { get; set; } = 0.85;

    /// <summary>Last Volume-Mixer level for this app (0..1), restored on the next launch.</summary>
    public float MixerVolume { get; set; } = -1f;
    public string MusicFolder { get; set; } = "";
    public PlayMode PlayMode { get; set; } = PlayMode.Sequential;
    /// <summary>Audio files picked by hand (one by one) rather than by folder scan.</summary>
    public List<string> LibraryFiles { get; set; } = new();

    /// <summary>Files the user removed from a folder-scanned library. They stay on disk, but the
    /// scan skips them so a removal sticks until it is undone (see 恢复被隐藏的曲目).</summary>
    public List<string> HiddenFiles { get; set; } = new();
    public string AdbPath { get; set; } = "";
    public string WsaSerial { get; set; } = "127.0.0.1:58526";
    public bool AutoImportWsa { get; set; } = true;
    public bool ShowOnStartup { get; set; } = true;

    /// <summary>NetEase cookie pasted from a browser (MUSIC_U=...). Empty means anonymous, which
    /// still searches and plays free tracks, but drops lossless and VIP-only ones.</summary>
    public string NeteaseCookie { get; set; } = "";

    /// <summary>Preferred quality tier for online tracks: standard / higher / exhigh / lossless.</summary>
    public string NeteaseQuality { get; set; } = "exhigh";

    /// <summary>Where 下载到本地 puts online tracks. Empty means the default
    /// (我的音乐\Darius Music). Deliberately separate from <see cref="MusicFolder"/>: dumping
    /// downloads into the scanned library folder mixes them in with the user's own files.</summary>
    public string DownloadFolder { get; set; } = "";

    /// <summary>Show a finished download in 本地音乐 straight away; off leaves it on disk only.</summary>
    public bool AddDownloadedToLibrary { get; set; } = true;

    [JsonIgnore]
    public string ResolvedDownloadFolder
    {
        get
        {
            var chosen = (DownloadFolder ?? "").Trim().Trim('"');
            return chosen.Length > 0 ? chosen : DefaultDownloadFolder;
        }
    }

    [JsonIgnore]
    public static string DefaultDownloadFolder
    {
        get
        {
            try
            {
                var music = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
                if (!string.IsNullOrWhiteSpace(music)) return Path.Combine(music, "Darius Music");
            }
            catch { }
            return Path.Combine(AppPaths.DataDir, "downloads");
        }
    }

    [JsonIgnore]
    public double DisplayFontSize => Math.Clamp(FontSize, 20, 140);

    [JsonIgnore]
    public double DisplayOpacity => Math.Clamp(Opacity, 0.35, 1);

    [JsonIgnore]
    public double GlowRadiusScale => 0.4 + (Math.Clamp(GlowSpread, 0.5, 2) - 0.5) * 0.5;

    /// <summary>Same curve as <see cref="GlowRadiusScale"/>, driven by the player page's own spread.</summary>
    [JsonIgnore]
    public double PlayerGlowRadiusScale => 0.4 + (Math.Clamp(PlayerGlowSpread, 0.5, 2) - 0.5) * 0.5;

    /// <summary>
    /// Copies every public setting across. The settings window used to whitelist fields by hand,
    /// which silently dropped anything added later (the whole backdrop group reset on each slider
    /// move). Reflection keeps this correct for future fields too.
    /// </summary>
    public void CopyFrom(AppSettings other)
    {
        foreach (var property in typeof(AppSettings).GetProperties(
                     System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
        {
            if (!property.CanRead || !property.CanWrite) continue;
            property.SetValue(this, property.GetValue(other));
        }
    }

    public AppSettings Normalized() => new()
    {
        Enabled = Enabled,
        Translation = Translation,
        FontSize = Math.Clamp(FontSize, 20, 140),
        LineWidth = Math.Clamp(LineWidth, 240, 1600),
        LineGap = Math.Clamp(LineGap, 0, 60),
        Anchor = Math.Clamp(Anchor, 0, 1),
        Backdrop = Backdrop,
        BackdropMode = BackdropMode,
        BackdropColor = Sanitize(BackdropColor, "0E1118"),
        BackdropImage = BackdropImage ?? "",
        BackdropBlur = Math.Clamp(BackdropBlur, 0, 60),
        BackdropOpacity = Math.Clamp(BackdropOpacity, 0.1, 1),
        BackdropWidth = Math.Clamp(BackdropWidth, 0, 4000),
        BackdropHeight = Math.Clamp(BackdropHeight, 0, 4000),
        BackdropFadeEnabled = BackdropFadeEnabled,
        BackdropFadeStrength = Math.Clamp(BackdropFadeStrength, 0, 100),
        BackdropFadeH = Math.Clamp(BackdropFadeH, 0, MaxFadeHPercent),
        BackdropFadeV = Math.Clamp(BackdropFadeV, 0, MaxFadeVPercent),
        Perspective = Perspective,
        TiltAngle = Math.Clamp(TiltAngle, -88, 0),
        VanishingOffset = Math.Clamp(VanishingOffset, 0.25, 6.0),
        VisibleLines = Math.Clamp(VisibleLines, 3, 15),
        CustomPosition = CustomPosition,
        WindowX = double.IsFinite(WindowX) ? WindowX : 0,
        WindowY = double.IsFinite(WindowY) ? WindowY : 0,
        CurrentScale = Math.Clamp(CurrentScale, 1, 2.6),
        Opacity = Math.Clamp(Opacity, 0.35, 1),
        OffsetMs = Math.Clamp(OffsetMs, -60_000, 60_000),
        SongOffsets = (SongOffsets ?? new()).ToDictionary(kv => kv.Key, kv => Math.Clamp(kv.Value, -60_000, 60_000)),
        Height = Math.Clamp(Height, 0, 1),
        GlowEnabled = GlowEnabled,
        GlowStrength = Math.Clamp(GlowStrength, 0, 2),
        GlowSpread = Math.Clamp(GlowSpread, 0.5, 2),
        TextColor = Sanitize(TextColor, "FFFFFF"),
        GlowColor = Sanitize(GlowColor, "E0E0E0"),
        Locked = Locked,
        Topmost = Topmost,
        OverlayAboveApps = OverlayAboveApps && BackdropMode == BackdropMode.Transparent,
        // 快捷键本身以前根本没有参与 Normalized()，而 Normalized() 在每次 Load 和 Save 都会被调用 —
        // 于是用户改过的键当场写进文件成了默认值，重启后又变回默认值，看起来就是「改不了」。
        // 同时这里顺带规范化：旧版残留的裸字母（"P"）不再合法，会换成新的默认值 "Ctrl+P"。
        KeyAboveApps = HotkeyText.NormalizeOrDefault(KeyAboveApps, DefaultKeyAboveApps),
        KeyLyrics = HotkeyText.NormalizeOrDefault(KeyLyrics, DefaultKeyLyrics),
        KeyPlayPause = HotkeyText.NormalizeOrDefault(KeyPlayPause, DefaultKeyPlayPause),
        KeyNext = HotkeyText.NormalizeOrDefault(KeyNext, DefaultKeyNext),
        KeyPrevious = HotkeyText.NormalizeOrDefault(KeyPrevious, DefaultKeyPrevious),
        // Empty is legal here: this one has no default chord on purpose.
        KeyTransparentBg = HotkeyText.NormalizeOrDefault(KeyTransparentBg, ""),
        ThemeAccent = Palette.Normalize(ThemeAccent, Palette.DefaultAccent),
        ThemeSurface = Palette.Normalize(ThemeSurface, Palette.DefaultSurface),
        ThemeOpacity = Math.Clamp(ThemeOpacity, 0, 100),
        ThemeBlur = Math.Clamp(ThemeBlur, 0, 60),
        ThemeImage = ThemeImage ?? "",
        UpdateFeed = UpdateFeed ?? "",
        PlayerFontSize = Math.Clamp(PlayerFontSize, 18, 96),
        PlayerVisibleLines = Math.Clamp(PlayerVisibleLines, 3, 20),
        PlayerCurrentScale = Math.Clamp(PlayerCurrentScale, 1.0, 2.4),
        PlayerLineGap = Math.Clamp(PlayerLineGap, 0, 60),
        PlayerLineWidth = Math.Clamp(PlayerLineWidth, 0, 1600),
        PlayerTranslation = PlayerTranslation,
        PlayerGlow = PlayerGlow,
        PlayerPerspective = PlayerPerspective,
        PlayerTiltAngle = Math.Clamp(PlayerTiltAngle, -88, 0),
        PlayerVanishingOffset = Math.Clamp(PlayerVanishingOffset, 0.25, 6.0),
        PlayerGlowStrength = Math.Clamp(PlayerGlowStrength, 0, 2),
        PlayerGlowSpread = Math.Clamp(PlayerGlowSpread, 0.5, 2),
        PlayerGlowColor = Sanitize(PlayerGlowColor, "E0E0E0"),
        PlayerTextColor = Sanitize(PlayerTextColor, "FFFFFF"),
        PlayerBackdropBlur = Math.Clamp(PlayerBackdropBlur, 0, 80),
        PlayerBackdropOpacity = Math.Clamp(PlayerBackdropOpacity, 0, 1),
        Scale = Math.Clamp(Scale, 1.0, 4.0),
        FontFamily = string.IsNullOrWhiteSpace(FontFamily) ? "Microsoft YaHei UI" : FontFamily,
        Volume = Math.Clamp(Volume, 0, 1),
        MixerVolume = float.IsFinite(MixerVolume) ? Math.Clamp(MixerVolume, 0f, 1f) : -1f,
        MusicFolder = MusicFolder ?? "",
        PlayMode = PlayMode,
        LibraryFiles = (LibraryFiles ?? new List<string>())
            .Where(f => !string.IsNullOrWhiteSpace(f)).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
        HiddenFiles = (HiddenFiles ?? new List<string>())
            .Where(f => !string.IsNullOrWhiteSpace(f)).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
        AdbPath = AdbPath ?? "",
        WsaSerial = string.IsNullOrWhiteSpace(WsaSerial) ? "127.0.0.1:58526" : WsaSerial,
        AutoImportWsa = AutoImportWsa,
        ShowOnStartup = ShowOnStartup,
        // These four were added later and were missing here, so every launch silently reset the
        // NetEase cookie/quality back to defaults (Normalized() rebuilds a fresh instance).
        NeteaseCookie = NeteaseCookie ?? "",
        NeteaseQuality = NeteaseApi.QualityChoices.Any(q => q.Value == NeteaseQuality) ? NeteaseQuality : "exhigh",
        DownloadFolder = (DownloadFolder ?? "").Trim(),
        AddDownloadedToLibrary = AddDownloadedToLibrary,
    };

    public AppSettings Clone() => (AppSettings)MemberwiseClone();

    private static string Sanitize(string value, string fallback)
    {
        var v = (value ?? "").Trim().TrimStart('#');
        if (v.Length != 6 || !v.All(Uri.IsHexDigit)) return fallback;
        return v.ToUpperInvariant();
    }
}

public static class SettingsStore
{
    private static string FilePath => Path.Combine(AppPaths.DataDir, "settings.json");

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                var loaded = JsonSerializer.Deserialize<AppSettings>(json, Options);
                if (loaded != null) return loaded.Normalized();
            }
        }
        catch { }
        return new AppSettings().Normalized();
    }

    public static void Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataDir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(settings.Normalized(), Options));
        }
        catch { }
    }
}
