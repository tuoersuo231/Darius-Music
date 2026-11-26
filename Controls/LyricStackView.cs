using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CurrentLyrics.Models;
using CurrentLyrics.Services;

using CurrentLyrics.Views;

namespace CurrentLyrics.Controls;

/// <summary>
/// Multi-line lyric column, modelled on CurrentMusic's desktop lyric page: the current cue sits
/// at a fixed reading anchor at full size with per-word karaoke ink, neighbours shrink and fade
/// with distance, and a cue change scrolls the whole stack by one row with a short ease-out.
/// </summary>
/// <summary>One visual row-segment of a word: its box plus the ink that belongs to it.</summary>
public sealed class WordSegment
{
    public Rect Box;
    public Geometry? Ink;
    /// <summary>Baked copy of <see cref="Ink"/> (device pixels) and where to draw it — the karaoke
    /// fill blits this instead of filling the glyph outlines again every frame.</summary>
    public BitmapSource? Baked;
    public Rect BakedRect;
    /// <summary>Device pixels per DIP the image was rasterised at. The image is baked as soon as the
    /// segment exists, not when the reading line is baked: the reading-line bake is prepared one frame
    /// ahead for a cue that has no word segments yet, so relying on that bake left every segment
    /// without an image and the karaoke fill silently drew nothing.</summary>
    public double BakedScale;
    /// <summary>Text colour baked into <see cref="Baked"/>. The glyph pixels carry the colour, so a
    /// colour change has to re-rasterise the image — this is what tells it apart from a fresh one.</summary>
    public Color BakedColor;
}

public sealed class LyricStackView : FrameworkElement
{

    /// <summary>Scroll + grow timing. The 380/300 ms pair felt rushed, then 620/420 ms; the user asked
    /// for the scroll to be 100 ms quicker, so 500/420: the outgoing row is still shrinking while it
    /// glides, and the whole move finishes before the grow does, so two full-size rows never cross in
    /// place. The per-row correction (see _offsetFix) rides this same constant, so every row lands
    /// exactly on its new slot when the scroll reaches 0.</summary>
    private const double ScrollDurationMs = 500;
    private const double MorphDurationMs = 420;
    private const double MorphDelayMs = 40;
    private const double LineHeightRatio = 1.45;
    /// <summary>How early the column adopts the next cue, in milliseconds. This shifts ONLY which cue
    /// is the reading line: the karaoke fill, the glow and the word timings all keep reading the real
    /// playback position (`_positionMs`), so nothing except the moment of the line change moves.</summary>
    private const long CueLeadMs = 100;

    private LyricDocument? _doc;
    private long _positionMs;
    private int _current = -1;
    private int _previous = -1;
    private double _scrollFrom;
    private double _scrollDelta;
    private double _scrollStart;
    private double _morphStart;
    private bool _morphing;

    private double _fontSize = 64;
    private double _columnWidth = 680;

    /// <summary>Device pixels per DIP, refreshed once per paint. Baked text is blitted on this pixel
    /// grid: a fractional destination makes WPF resample the whole line, which is what makes blitted
    /// text look softer than the vector original.</summary>
    private double _pxPerDip = 1;
    private double Snap(double value) => _pxPerDip > 0.1 ? Math.Round(value * _pxPerDip) / _pxPerDip : value;

    /// <summary>Baked-text renderer switch (A/B for QA). true =每行预烘成位图，逐帧只贴图；
    /// false = 老的字形轮廓填充。`LYRIC_NOBAKE=1` 可在不改代码的情况下做同一帧的 A/B。</summary>
    internal static bool UseBakedText = Environment.GetEnvironmentVariable("LYRIC_NOBAKE") != "1";
    /// <summary>Re-rasterise the line images when the text colour changes. `LYRIC_NOCOLOR=1` restores
    /// the old behaviour (the colour was baked in but never re-baked) for an A/B on one binary.</summary>
    internal static bool RecolorOnTextColorChange = Environment.GetEnvironmentVariable("LYRIC_NOCOLOR") != "1";
    private double _lineGap = 6;
    private double _anchor = 0.5;
    private string _fontName = FontResolver.WenKai;
    private bool _translation = true;
    private bool _glowEnabled = true;
    private double _glowStrength = 1;
    private double _glowRadius = 18;
    private Color _glowColor = Color.FromRgb(0xE0, 0xE0, 0xE0);
    private Color _textColor = Colors.White;
    private bool _perspective;
    private double _neighbourScale = 0.64;
    private int _visibleLines = 7;
    private double _tilt = -32;
    private double _vpOffset = 1.6;
    private double _perspectiveShift;
    /// <summary>Horizontal shift the column was showing when the cue changed. The shift is derived from
    /// the CURRENT line's ink width, so recomputing it on the switch frame moved the whole column
    /// sideways in one frame (measured 36.47 → 43.26 px between two lines). It now eases from this
    /// value to the new line's over the same 620 ms as the scroll.</summary>
    private double _shiftFrom;
    /// <summary>The column's horizontal position, computed once and then held. Re-centring it per cue
    /// moved the whole column sideways at every line change (a step, then — after the first easing
    /// attempt — a slow drift back), which is the "位置突变" the user kept seeing. A seek or a
    /// settings/document change re-locks it; a normal cue change does not. LYRIC_LINECENTER=1 restores
    /// the old per-line centring for an A/B.</summary>
    private double _shiftLock = double.NaN;
    /// <summary>Per-row correction for the frame the cue changes. The vertical layout is measured from
    /// the READING row: the anchor contributes its full height, every neighbour only
    /// Height*1/CurrentScale. When the anchor moves by one row, the same cue is therefore laid out
    /// (1 - 1/CurrentScale)*|hOld - hNew|/2 higher or lower than it was — measured 12.4 DIP at the
    /// user's 38 px font, in ONE frame, on top of the scroll. That is the "字的位置突然变化" that
    /// survived every previous attempt: a single uniform scroll value cannot absorb a per-row
    /// mismatch, and the perspective branch below never even applied this correction (it drew
    /// layoutOffset + scroll directly). Every visible row's on-screen offset is now captured BEFORE
    /// the anchor moves and blended to the new layout over the same 620 ms as the scroll, so the
    /// column starts exactly where it was and nothing moves discontinuously.
    /// Reference implementation: CurrentMusicX (流声音乐) MonetLyricsRail — `monetTrackPositions`
    /// computes each row's target from the TARGET geometry and every row owns a spring
    /// (stiffness 173, damping ratio 1.30), so a line change is per-row interpolation, never a
    /// layout swap. It also never fades the column out.</summary>
    private readonly Dictionary<int, double> _offsetFix = new();
    private double _offsetFixMax;
    /// <summary>Largest pre-switch layout mismatch among the rows visible at the last cue change, in
    /// DIP. This is the size of the one-frame jump the correction removes; --cue-perf reports it.</summary>
    public double DebugOffsetFix => _offsetFixMax;
    private double _offsetFixStart = double.NaN;
    private static readonly bool LineCentredShift = Environment.GetEnvironmentVariable("LYRIC_LINECENTER") == "1";
    private double _perspectiveFit = 1;
    private double _perspectivePitch = 1;
    private double _scaleCenterX;
    private LyricPlane _plane;

    private bool _backdrop = true;
    private BackdropMode _backdropMode = BackdropMode.Cover;
    private Color _backdropColor = Color.FromRgb(0x0E, 0x11, 0x18);
    private double _backdropBlur = 24;
    private double _backdropOpacity = 0.82;
    private bool _fadeEnabled;
    private double _fadeStrength = 65;
    private double _fadeH = 8;
    private double _fadeV = 8;

    private readonly Dictionary<int, LineRender> _cache = new();

    private BitmapSource? _backdropImage;
    private BitmapSource? _backdropImageOverride;

    /// <summary>Custom image from the settings; takes precedence over the playing cover.</summary>
    public BitmapSource? BackdropImageOverride
    {
        get => _backdropImageOverride;
        set { _backdropImageOverride = value; _backdropImage = null; InvalidateVisual(); }
    }

    /// <summary>The playing song's cover, used when no custom image is configured.</summary>
    public void SetCoverImage(BitmapSource? cover)
    {
        if (ReferenceEquals(_backdropImage, cover)) return;
        _backdropImage = cover;
        if (_backdropMode is BackdropMode.Cover or BackdropMode.Image) InvalidateVisual();
    }

    private static Color ParseColor(string hex, Color fallback)
    {
        try
        {
            var text = (hex ?? "").Trim().TrimStart('#');
            if (text.Length == 6)
            {
                return Color.FromRgb(
                    Convert.ToByte(text[..2], 16),
                    Convert.ToByte(text.Substring(2, 2), 16),
                    Convert.ToByte(text.Substring(4, 2), 16));
            }
        }
        catch { }
        return fallback;
    }

    /// <summary>Smallest box that still contains the whole lyric column (no clipping).</summary>
    public Size ContentMinimum
    {
        get
        {
            double lineHeight = _fontSize * 1.32 + _lineGap;
            double height = _visibleLines * lineHeight + 28;
            double width = _columnWidth + 36;
            return new Size(Math.Max(220, width), Math.Max(120, height));
        }
    }

    /// <summary>
    /// How many cues fit in <paramref name="height"/> at these text metrics. The current cue is drawn
    /// at <paramref name="currentScale"/> and its neighbours at its reciprocal, so the block is one
    /// tall row plus (n-1) short rows separated by the line gap.
    /// </summary>
    public static int LinesThatFit(double height, double fontSize, double lineGap, double currentScale,
                                   bool perspective)
    {
        if (height < 60 || fontSize < 4) return 3;
        double rowHeight = fontSize * LineHeightRatio * (perspective ? 1.16 : 1.0);
        double scale = Math.Clamp(currentScale, 1.0, 2.6);
        double step = rowHeight / scale + Math.Max(0, lineGap);
        if (step < 2) return 3;
        int fit = 1 + (int)Math.Floor(Math.Max(0, height - rowHeight * scale) / step);
        return Math.Clamp(fit, 3, MaxVisibleLines);
    }

    /// <summary>Hard ceiling for the visible line count (also the per-frame draw budget).</summary>
    public const int MaxVisibleLines = 20;

    public double DebugFadeH => _fadeH;
    public double DebugFadeV => _fadeV;
    public double DebugScrollDelta => _scrollDelta;
    /// <summary>Row pitch actually used this frame (the distance from the reading row to the next one).
    /// --cue-perf samples it per frame: a pitch that flips between two values on alternating frames is
    /// what makes the whole column look like it jumps down and back for a few frames.</summary>
    public double DebugPitch => _doc == null || _current < 0
        ? 0
        : OffsetOf(Math.Min(_doc.Lines.Count - 1, _current + 1), _current);
    public double DebugMorph => MorphProgress;
    /// <summary>Whole-column horizontal shift for this frame. It is derived from the CURRENT line's
    /// warped ink bounds, so it changes the instant the reading line changes — that is the sideways
    /// jump the user reported. Dumped per frame by --cue-perf.</summary>
    public double DebugShift => _perspectiveShift;

    public bool DebugMorphing => _morphing;

    /// <summary>True while a scroll or morph is still running, so the host knows to keep ticking.</summary>
    public bool HasActiveAnimation => _scrollDelta != 0 || _morphing || _offsetFix.Count > 0;
    public bool DebugFadeEnabled => _fadeEnabled;
    public double DebugFadeStrength => _fadeStrength;
    public string DebugMode => _backdropMode.ToString();
    public bool DebugHasImage => (_backdropImage ?? _backdropImageOverride) != null;

    /// <summary>The host window paints the backdrop as its own masked element.</summary>
    public bool BackdropDrawnByHost { get; set; }

    /// <summary>When the host composites the frame, this element must not paint itself.</summary>
    public bool SuppressOwnRender { get; set; }

    public bool HasDocument => _doc != null && _doc.Lines.Count > 0;

    public LyricStackView()
    {
        // Soft fade at the top/bottom of the column instead of a hard cut.
        var mask = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
        mask.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 0.0));
        mask.GradientStops.Add(new GradientStop(Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF), 0.14));
        mask.GradientStops.Add(new GradientStop(Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF), 0.86));
        mask.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 1.0));
        mask.Freeze();
        OpacityMask = mask;
    }
    private readonly Dictionary<bool, Typeface> _typefaces = new();

    /// <summary>Raised when the active cue changes (the host can fade or reposition).</summary>
    public event Action? LineChanged;

    public int CurrentIndex => _current;
    public LyricLine? CurrentLine =>
        _doc != null && _current >= 0 && _current < _doc.Lines.Count ? _doc.Lines[_current] : null;

    public void ApplySettings(AppSettings settings, double dpiScale)
    {
        bool fontChanged = _fontName != settings.FontFamily;
        // Everything BuildLine() bakes into the geometry, captured before the assignments below.
        double oldFont = _fontSize, oldColumn = _columnWidth;
        bool oldPerspective = _perspective;
        double oldTilt = _tilt, oldVp = _vpOffset, oldGlowRadius = _glowRadius;
        bool oldTranslation = _translation;
        bool oldGlow = _glowEnabled && _glowStrength > 0.001;
        Color oldGlowColor = _glowColor;
        Color oldTextColor = _textColor;

        _fontSize = settings.DisplayFontSize * dpiScale;
        _columnWidth = settings.LineWidth * dpiScale;
        _lineGap = settings.LineGap * dpiScale;
        _anchor = 0.5;   // the host window centres the column; Anchor moves the window
        _translation = settings.Translation;
        _glowEnabled = settings.GlowEnabled;
        _glowStrength = settings.GlowStrength;
        _glowColor = DesktopLyricsWindow.ParseColor(settings.GlowColor);
        _textColor = DesktopLyricsWindow.ParseColor(settings.TextColor);
        _backdrop = settings.Backdrop;
        _backdropMode = settings.BackdropMode;
        _backdropColor = ParseColor(settings.BackdropColor, Color.FromRgb(0x0E, 0x11, 0x18));
        _backdropBlur = settings.BackdropBlur;
        _backdropOpacity = settings.BackdropOpacity;
        _fadeEnabled = settings.BackdropFadeEnabled;
        _fadeStrength = settings.BackdropFadeStrength;
        _fadeH = settings.BackdropFadeH;
        _fadeV = settings.BackdropFadeV;
        if (_backdropImageOverride == null) _backdropImage = null;
        _perspective = settings.Perspective;
        _neighbourScale = 1.0 / Math.Clamp(settings.CurrentScale, 1.0, 2.6);
        _visibleLines = Math.Clamp(settings.VisibleLines, 3, MaxVisibleLines);
        _tilt = Math.Clamp(settings.TiltAngle, -88, 0);
        _vpOffset = Math.Clamp(settings.VanishingOffset, 0.25, 6.0);
        _glowRadius = Math.Max(4, 0.26 * _fontSize * settings.GlowRadiusScale);
        _fontName = settings.FontFamily;

        // Only a change that BuildLine() actually bakes in may throw the geometry away. Clearing it
        // for every tick (tilt, glow strength, colour, opacity...) forced a full rebuild of every
        // visible line on each slider step, which is where the stutter came from.
        // The glow colour is deliberately NOT one of them: it only tints the cached halo masks, so a
        // colour drag repaints instead of re-rasterising thirteen words (that is what froze the glow
        // while the colour was being picked).
        bool glowColorChanged = _glowColor != oldGlowColor;
        // 辉光半径不在这里：它只影响光晕蒙版，文字和逐字高亮都不变。以前它算"几何变化"，于是拖
        // 「辉光范围」滑条每一步都丢掉整列几何 —— 朗读行会先掉回没建逐字的渲染（只剩两三个亮词、
        // 光晕也没了），下一帧再长回来，看上去就是字幕在闪。现在只就地重糊光晕（见 RefreshGlowHalos）。
        bool glowRadiusChanged = Math.Abs(_glowRadius - oldGlowRadius) > 0.01;
        bool geometryChanged =
            fontChanged ||
            _perspective != oldPerspective ||
            Math.Abs(_fontSize - oldFont) > 0.01 ||
            Math.Abs(_columnWidth - oldColumn) > 0.01 ||
            Math.Abs(_tilt - oldTilt) > 0.01 ||
            Math.Abs(_vpOffset - oldVp) > 0.001 ||
            _translation != oldTranslation ||
            (_glowEnabled && _glowStrength > 0.001) != oldGlow ||
            (glowColorChanged && DebugGlowColorRebuild);

        if (geometryChanged) InvalidateCache();
        else if (glowColorChanged) RecolorHalos();
        if (glowRadiusChanged && !geometryChanged) InvalidateGlowHalos();
        // 文字颜色是烘进位图的（字形像素本身就是那个颜色），所以它不重建几何、但必须重烘贴图。
        // 以前这里什么也没做：改色只改了 _textColor，逐帧贴的还是旧颜色的图片，于是「文字颜色没反应」。
        // LYRIC_NOCOLOR=1 退回旧行为，给同一份二进制做 A/B。
        if (_textColor != oldTextColor && RecolorOnTextColorChange)
            QueueRecolor();
        if (fontChanged)
        {
            _typefaces.Clear();
            _current = -1;
        }
        InvalidateVisual();
    }

    public void SetDocument(LyricDocument? doc, long positionMs)
    {
        if (!ReferenceEquals(doc, _doc))
        {
            _doc = doc;
            _cache.Clear();
            _stale.Clear();
            _rebuilding = false;
            _current = -1;
            _previous = -1;
            _scrollDelta = 0;
            _morphing = false;
            _offsetFix.Clear();
        }
        Tick(positionMs);
    }

    public void Tick(long positionMs)
    {
        _positionMs = positionMs;
        if (_doc == null || _doc.Lines.Count == 0)
        {
            InvalidateVisual();
            return;
        }

        int index = _doc.LineIndexAt(positionMs + CueLeadMs, out _);
        if (index < 0) index = 0;

        AdvanceAnimations();

        if (index != _current)
        {
            // Where the incoming cue sits in the old layout decides the scroll distance.
            double previousOffset = _current >= 0 ? OffsetOf(index, _current) : 0;
            double pitch = _perspective ? _perspectivePitch : 1.0;
            // Capture where every row is drawn RIGHT NOW, while the anchor is still the old one. Rows
            // whose layout distance changes with the anchor (all of them, by the anchor's own scaled vs
            // unscaled half-height) then start from the pixel they were on instead of snapping.
            var before = new Dictionary<int, double>();
            if (_current >= 0)
            {
                int span = Math.Max(1, _visibleLines / 2) + 4;
                for (int k2 = Math.Max(0, index - span); k2 <= Math.Min(_doc.Lines.Count - 1, index + span); k2++)
                    before[k2] = OffsetNow(k2, pitch);
            }
            _previous = _current < 0 ? index : _current;
            _current = index;
            _scrollFrom = previousOffset;
            _scrollDelta = previousOffset;
            _scrollStart = Now;
            _morphStart = Now;
            _morphing = _previous != _current;
            _offsetFix.Clear();
            foreach (var kv in before) _offsetFix[kv.Key] = kv.Value;
            _offsetFixStart = Now;
            _offsetFixMax = 0;
            foreach (var kv in _offsetFix)
            {
                double jumped = OffsetOf(kv.Key, _current) * pitch + _scrollDelta;
                _offsetFixMax = Math.Max(_offsetFixMax, Math.Abs(kv.Value - jumped));
            }
            // A seek re-locks the column; a normal one-line step must not move it sideways at all.
            if (_previous < 0 || Math.Abs(index - _previous) > 1) _shiftLock = double.NaN;
            LineChanged?.Invoke();
        }

        InvalidateVisual();
    }

    /// <summary>Vertical offset a row is drawn with in the layout that is current right now, including
    /// any per-row correction still in flight from the previous cue change. Used to capture the
    /// on-screen position of every row before the anchor moves.</summary>
    private double OffsetNow(int index, double pitch)
    {
        double offset = OffsetOf(index, _current) * pitch + _scrollDelta;
        if (_offsetFix.Count > 0 && _offsetFix.TryGetValue(index, out double was))
        {
            double ft = double.IsNaN(_offsetFixStart) ? 1 : Math.Clamp((Now - _offsetFixStart) / ScrollDurationMs, 0, 1);
            if (ft < 1) offset += (was - offset) * (1 - BackdropBlur.Smooth(ft));
        }
        return offset;
    }

    /// <summary>Per-frame advance for the composition-driven path. The player clock only fires every
    /// 16 ms, so repainting from it capped the karaoke at 60 Hz; this is called once per rendered
    /// frame instead, and skips the repaint when neither the position nor an animation moved — a
    /// 179 Hz call rate must not become 179 full repaints per second.</summary>
    public void Advance(long positionMs)
    {
        bool animating = _scrollDelta != 0 || _morphing || _offsetFix.Count > 0;
        if (positionMs == _positionMs && !animating) return;
        Tick(positionMs);
    }

    /// <summary>Forces animated values to rest (used by the PNG previews).</summary>
    public void Snap()
    {
        _scrollDelta = 0;
        _morphing = false;
        _offsetFix.Clear();
        InvalidateVisual();
    }

    /// <summary>Phase clock for the scroll/grow animations, in milliseconds. It has to be high
    /// resolution: Environment.TickCount64 only steps every 15.6 ms on this machine (measured 65
    /// distinct values per second), so a transition sampled from it had ~65 distinct positions per
    /// second however fast the compositor painted — the "帧数被锁住" the user could see. Stopwatch
    /// reads QueryPerformanceCounter, which is monotonic and sub-microsecond.</summary>
    private static readonly System.Diagnostics.Stopwatch PhaseClock = System.Diagnostics.Stopwatch.StartNew();
    /// <summary>A/B switch: LYRIC_LOWRESCLOCK=1 puts the animation phase back on TickCount64 (the old
    /// 15.6 ms-quantised behaviour) so the same binary can be measured both ways.</summary>
    private static readonly bool LowResClock = Environment.GetEnvironmentVariable("LYRIC_LOWRESCLOCK") == "1";
    private static double Now => LowResClock ? Environment.TickCount64 : PhaseClock.Elapsed.TotalMilliseconds;

    private void AdvanceAnimations()
    {
        if (_scrollDelta != 0)
        {
            double t = Math.Clamp((Now - _scrollStart) / ScrollDurationMs, 0, 1);
            // Smoothstep again: at 620 ms the user prefers the symmetric feel, and a quart ease at
            // this length reads as a lurch.
            _scrollDelta = t >= 1 ? 0 : _scrollFrom * (1 - BackdropBlur.Smooth(t));
        }
        // The per-row correction expires with the scroll it was seeded from. At t >= 1 its blend factor
        // is 0 anyway, so dropping it here cannot move a pixel — and it must not depend on a row having
        // been drawn, or a fully culled window would keep the overlay repainting forever.
        if (_offsetFix.Count > 0 && (double.IsNaN(_offsetFixStart) || Now - _offsetFixStart >= ScrollDurationMs))
            _offsetFix.Clear();
        if (_morphing && Now - _morphStart >= MorphDelayMs + MorphDurationMs) _morphing = false;
    }

    private static double EaseOut(double t) => 1 - Math.Pow(1 - t, 3);

    private double MorphProgress => !_morphing || _previous < 0 || _previous == _current
        ? 1
        : BackdropBlur.Smooth(Math.Clamp((Now - _morphStart - MorphDelayMs) / MorphDurationMs, 0, 1));

    /// <summary>Where a row sits between the two looks: 0 = still the reading line (dim unsung text,
    /// bright sung text, glow), 1 = an ordinary neighbour (one flat bright image). A cue change used to
    /// swap the row from one look to the other in a single frame — the row that had just finished
    /// flashed 30% → 100% white for ~100 ms while it slid away, which read as a flicker rather than as
    /// a transition. The two bakes are now cross-faded over the morph, so both ends match exactly.</summary>
    private double MorphBlend
    {
        get
        {
            if (!_morphing || _previous < 0 || _previous == _current) return 1;
            return BackdropBlur.Smooth(Math.Clamp((Now - _morphStart) / MorphDurationMs, 0, 1));
        }
    }

    /// <summary>
    /// Global horizontal shift so the *current* cue's projected centre sits exactly at the window
    /// centre; without it the projection drags the reading line to one side.
    /// </summary>
    private double ComputePerspectiveShift(double width)
    {
        var line = GetLine(_current);
        if (line == null) return 0;
        var bounds = (line.WarpedInk ?? line.Ink).Bounds;
        if (bounds.IsEmpty) return 0;
        double x0 = (width - _columnWidth) / 2;
        return width / 2 - (bounds.Left + bounds.Width / 2) - x0;
    }

    /// <summary>
    /// The column's horizontal centring, eased across a cue change. Without the ease the shift is a
    /// step function of the reading line (it is computed from that line's ink width), so every line
    /// change snapped the whole column sideways — the "字的位置突然变化" the user reported. A NaN
    /// start (first cue ever) means there is nothing to ease from.
    /// </summary>
    private double AnimatedPerspectiveShift(double width)
    {
        if (!double.IsNaN(_shiftLock)) return _shiftLock;
        _shiftLock = ComputePerspectiveShift(width);
        return _shiftLock;
    }

    private double HeightOf(int index)
    {
        if (index < 0 || _doc == null || index >= _doc.Lines.Count) return _fontSize * LineHeightRatio;
        if (_cache.TryGetValue(index, out var r)) return (r.Height + r.ExtraHeight) * (_perspective ? 1.16 : 1.0);
        if (_stale.TryGetValue(index, out var old)) return (old.Height + old.ExtraHeight) * (_perspective ? 1.16 : 1.0);
        return _fontSize * LineHeightRatio;
    }

    /// <summary>Vertical offset of a cue from the anchor cue, in the layout of <paramref name="anchorIndex"/>.</summary>
    private double OffsetOf(int index, int anchorIndex)
    {
        if (index == anchorIndex || _doc == null) return 0;
        double y = 0;
        int step = index > anchorIndex ? 1 : -1;
        int previous = anchorIndex;
        for (int i = anchorIndex + step; ; i += step)
        {
            if (i < 0 || i >= _doc.Lines.Count) break;
            double hPrev = HeightOf(previous) * (previous == anchorIndex ? 1 : _neighbourScale);
            double hThis = HeightOf(i) * (i == anchorIndex ? 1 : _neighbourScale);
            y += step * (hPrev / 2 + _lineGap + hThis / 2);
            if (i == index) break;
            previous = i;
        }
        return y;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        // A VisualBrush measures its source with an infinite constraint; never echo that back.
        double w = double.IsInfinity(availableSize.Width) || double.IsNaN(availableSize.Width)
            ? _columnWidth + 40
            : availableSize.Width;
        double h = double.IsInfinity(availableSize.Height) || double.IsNaN(availableSize.Height)
            ? Math.Max(480, _fontSize * 10)
            : availableSize.Height;
        return new Size(Math.Max(w, 1), Math.Max(h, 1));
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (SuppressOwnRender) return;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        AdvanceAnimations();
        DrawCore(dc, RenderSize.Width, RenderSize.Height);
        clock.Stop();
        RenderCount++;
        LastRenderMs = clock.Elapsed.TotalMilliseconds;
        RenderMsTotal += clock.Elapsed.TotalMilliseconds;
    }

    /// <summary>
    /// The column is drawn from <see cref="RenderSize"/>, so a resize must repaint it. Without this
    /// an element that was first laid out at 0x0 (collapsed, or before the window had a size) keeps
    /// the empty drawing it produced then — the overlay shows its backdrop and no lyrics.
    /// </summary>
    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        InvalidateVisual();
    }

    /// <summary>Diagnostics: how often WPF actually repainted the column, and how long each paint took.</summary>
    public long RenderCount { get; private set; }
    public double LastRenderMs { get; private set; }
    public double RenderMsTotal { get; private set; }
    public void ResetRenderStats()
    {
        RenderCount = 0;
        RenderMsTotal = 0;
        LastRenderMs = 0;
    }

    /// <summary>What the last paint actually placed, as "index:y/alpha" pairs. This is the only honest
    /// way to see a one-frame position blip: the y values the loop computed, not a correlation of the
    /// pixels afterwards.</summary>
    private readonly List<string> _drawnRows = new();
    public string DebugRows => string.Join(";", _drawnRows);
    /// <summary>Only records the drawn rows when a QA run asks for it (LYRIC_DEBUG_ROWS=1): the list
    /// allocates a string per row per paint, which is 2500 strings/s at 170 fps.</summary>
    private static readonly bool CaptureRows = Environment.GetEnvironmentVariable("LYRIC_DEBUG_ROWS") == "1";

    // ---- per-frame work accounting (QA only, LYRIC_DEBUG_ROWS=1) ----
    // The stutter at a cue change is one frame doing several jobs at once. These are the exact ms of
    // each job in the frame that is being built, so the spike can be attributed instead of guessed.
    private double _wkBuild, _wkBake, _wkWords, _wkHalo, _wkRows;
    private int _wkBuiltLines, _wkBakedWordParts, _bakeCounter, _wkInlineBakes;
    public string DebugFrameWork =>
        $"build={_wkBuild:0.00}/{_wkBuiltLines} bake={_wkBake:0.00} words={_wkWords:0.00}/{_wkBakedWordParts} " +
        $"halo={_wkHalo:0.00} rows={_wkRows:0.00}/{_wkInlineBakes} fix={_offsetFix.Count}/{_offsetFixMax:0.0}";

    /// <summary>
    /// Draws the lyric column into any context at an explicit size. The overlay window composites the
    /// backdrop and these glyphs into one bitmap, so the alpha ramp lives in a single image instead of
    /// being split across elements inside a layered window.
    /// </summary>
    public void DrawInto(DrawingContext dc, double width, double height)
    {
        AdvanceAnimations();
        DrawCore(dc, width, height);
    }

    private void DrawCore(DrawingContext dc, double width, double height)
    {
        if (_doc == null || _doc.Lines.Count == 0) return;
        if (width < 2 || height < 2) return;
        _pxPerDip = VisualTreeHelper.GetDpi(this).DpiScaleX;
        // Per-frame budget for re-rasterising line images after a text-colour change (see QueueRecolor).
        _recolorBudget = _recolorPending ? 2 : 0;
        _recolorStale = false;

        // Warm the cache for the visible window before laying anything out. While a settings change is
        // still being rebuilt, only a few lines are built per frame and the previous geometry is drawn
        // in the meantime: rebuilding a dozen warped, glowing lines at once cost 50-130 ms per frame,
        // which is what made dragging the settings sliders stutter.
        int half = Math.Max(1, _visibleLines / 2);
        // One row beyond the visible window on each side: the row that is on its way out of the
        // window used to be cut by this range the moment the cue changed (it vanished in a single
        // frame instead of fading through the edge band). Drawing it one row further lets
        // TextEdgeFade take it down to ~0.1 while it slides out.
        int drawHalf = half + 2;
        int warmFrom = Math.Max(0, _current - drawHalf - 1);
        int warmTo = Math.Min(_doc.Lines.Count - 1, _current + drawHalf + 1);
        // Jobs for the cue that becomes current next: build its geometry first, then bake its reading
        // image on the following frame. Either of them landing on the switch frame itself was the hitch.
        int _upcoming = _current + 1;
        bool _upcomingValid = _upcoming >= 0 && _upcoming < _doc.Lines.Count;
        LineRender? _upcomingRender = null;
        if (_upcomingValid) _cache.TryGetValue(_upcoming, out _upcomingRender);
        bool _bakeDue = !_rebuilding && _upcomingRender != null && _upcomingRender.BakedCurrent == null;
        bool builtLine = false;
        long wk0 = System.Diagnostics.Stopwatch.GetTimestamp();
        if (_rebuilding)
        {
            int budget = RebuildLinesPerFrame;
            bool complete = true;
            for (int i = warmFrom; i <= warmTo; i++)
            {
                if (_cache.ContainsKey(i)) continue;
                if (budget-- <= 0) { complete = false; break; }
                BuildLine(i);
                builtLine = true;
            }
            if (complete)
            {
                _rebuilding = false;
                _stale.Clear();
                // The karaoke ink is skipped during the rebuild; build the reading line's now that the
                // column is consistent again. Neighbours are built on demand if they become current,
                // so a drag never ends on a frame that rebuilds three lines at once.
                if (_current >= 0 && _current < _doc.Lines.Count &&
                    _cache.TryGetValue(_current, out var ready) && ready.WordSegments.Count == 0 &&
                    ready.Line.Words.Count > 0)
                    BuildWordPartsFor(ready);
                // 拖滑条期间攒下的那次重建，现在接着来：新一轮会用最新的倾斜/交点参数。
                if (_rebuildAgain)
                {
                    _rebuildAgain = false;
                    InvalidateCache();
                }
            }
        }
        else
        {
            // One job per frame, ordered by deadline. A cue change needs two things that used to land
            // on the frame of the change itself (a measured 21 ms frame): the incoming line's geometry
            // and its reading-line bake. Both are prepared here as early as the cache allows, so the
            // switch frame only has to draw. Centre first for the rest, otherwise the warm window
            // builds outwards and the far rows are still missing when the cue arrives.
            bool jobDone = false;
            if (_upcomingValid && _upcomingRender == null)
            {
                BuildLine(_upcoming);
                builtLine = true;
                jobDone = true;
            }
            if (!jobDone && !_bakeDue)
            {
                // Next: the row that will ENTER the window when the cue changes (it sits one row past
                // the warm window now). Building it on the switch frame was an 8 ms BuildLine landing
                // exactly on the visible hitch.
                int entering = Math.Min(_doc.Lines.Count - 1, _current + drawHalf + 2);
                if (entering > _current && !_cache.ContainsKey(entering))
                {
                    BuildLine(entering);
                    builtLine = true;
                    jobDone = true;
                }
                else
                {
                    for (int k = 0; k <= warmTo - warmFrom; k++)
                    {
                        int i = _current + (k % 2 == 0 ? k / 2 : -(k / 2 + 1));
                        if (i < warmFrom || i > warmTo || _cache.ContainsKey(i)) continue;
                        BuildLine(i);
                        builtLine = true;
                        break;
                    }
                }
            }
        }

        DrawFrostedBackdrop(dc, width, height);
        _wkBuild = System.Diagnostics.Stopwatch.GetElapsedTime(wk0).TotalMilliseconds;
        _wkBuiltLines = builtLine ? 1 : 0;
        _wkBake = 0; _wkWords = 0; _wkHalo = 0; _wkBakedWordParts = 0;

        // The reading-line image for the cue that becomes current NEXT. Baking it on the frame the cue
        // actually changes cost that frame 21 ms (bake + the first draw of a fresh bitmap), which is the
        // hitch right after every line change; it is prepared here instead, frames ahead of time.
        long bk0 = System.Diagnostics.Stopwatch.GetTimestamp();
        if (_bakeDue && _upcomingRender != null && _upcomingRender.BakedCurrent == null)
        {
            try
            {
                double dpiBake = VisualTreeHelper.GetDpi(this).DpiScaleX;
                if (!(dpiBake > 0.1)) dpiBake = 1.0;
                _upcomingRender.BakedCurrent = BakeLine(_upcomingRender, 0, true, dpiBake);
            }
            catch { }
        }
        _wkBake = System.Diagnostics.Stopwatch.GetElapsedTime(bk0).TotalMilliseconds;

        // Karaoke ink is the last thing a cue change still had to build. Two words a frame, reading line
        // first and the following cue after it, so the frame that changes cue has nothing left to do.
        long ww0 = System.Diagnostics.Stopwatch.GetTimestamp();
        BuildWordPartsAhead();
        _wkWords = System.Diagnostics.Stopwatch.GetElapsedTime(ww0).TotalMilliseconds;
        // Glow radius moved: re-blur a couple of word masks per frame instead of rebuilding the line.
        // One mask at the largest radius (radius 12 → 150×160 px, three box passes) is ~3 ms, so the
        // budget is two: a drag keeps the column around 140 fps instead of dropping to ~70.
        long hh0 = System.Diagnostics.Stopwatch.GetTimestamp();
        RefreshGlowHalos(2);
        _wkHalo = System.Diagnostics.Stopwatch.GetElapsedTime(hh0).TotalMilliseconds;

        double anchorY = height * _anchor;
        double progress = MorphProgress;
        double scroll = _scrollDelta;
        if (_perspective)
        {
            _plane = LyricPlane.For(_columnWidth, _tilt, _vpOffset, _fanReferenceWidth);
            _perspectiveFit = _plane.Fit;
            _perspectivePitch = 1.0;  // the projection already scales the pitch; compressing it again overlapped multi-row cues
            // Scaling a warped row must pivot on the warped image of the column centre, otherwise
            // the row drifts sideways by a few pixels.
            _scaleCenterX = _plane.MapX(_columnWidth / 2);
        }
        else
        {
            _plane = default;
            _perspectiveFit = 1;
            _perspectivePitch = 1;
            _scaleCenterX = _columnWidth / 2;
        }
        _perspectiveShift = !_perspective ? 0
            : LineCentredShift ? ComputePerspectiveShift(width)
            : AnimatedPerspectiveShift(width);

        if (CaptureRows) _drawnRows.Clear();
        long rw0 = System.Diagnostics.Stopwatch.GetTimestamp();
        int bakeStart = _bakeCounter;
        for (int i = Math.Max(0, _current - drawHalf); i <= Math.Min(_doc.Lines.Count - 1, _current + drawHalf); i++)
        {
            if (!_translation && _doc.Lines[i].TranslationOnly) continue;
            var render = GetLine(i);
            if (render == null) continue;

            double scale = ScaleFor(i, progress);
            double alpha = AlphaFor(i, progress);
            // The scroll animation must stay a pure translation: shearing it too would tilt the
            // reading line every time a cue changes.
            double layoutOffset = OffsetOf(i, _current) * (_perspective ? _perspectivePitch : 1.0);
            double offset = layoutOffset + scroll;
            if (_offsetFix.Count > 0 && _offsetFix.TryGetValue(i, out double was))
            {
                double ft = double.IsNaN(_offsetFixStart) ? 1 : Math.Clamp((Now - _offsetFixStart) / ScrollDurationMs, 0, 1);
                if (ft < 1) offset += (was - offset) * (1 - BackdropBlur.Smooth(ft));
            }
            double lineHeight = render.Height * scale;
            if (Math.Abs(offset) > height) continue;
            // Only skip rows that are effectively invisible. At 0.012 the outermost neighbour was
            // dropped while it was still on screen and still fading, which is what read as a pop.
            // 0.05 keeps the fade (it passes 0.44 → 0.29 → 0.19 → 0.13 → 0.08 → 0.05 while sliding out)
            // but stops paying for a full sheared bitmap blit per invisible row: four of the fifteen
            // rows sat at the 0.08 distance floor.
            if (alpha <= 0.05) continue;

            double y = Snap(anchorY + offset - lineHeight / 2);
            // Soft edge for the text itself. It used to ride on EdgeFadeFactor, which returns 1.0 for a
            // transparent backdrop — so with 背景=透明 a long wrapped line was simply cut off by the
            // window edge (user report: 上下文较长时出现明显的断层). The band now follows the font size,
            // and the factor is taken from the whole box instead of its centre, so tall wrapped lines
            // fade out as they leave instead of ending in a step.
            alpha *= TextEdgeFade(y, lineHeight, height);
            double x = Snap((width - _columnWidth) / 2 + _perspectiveShift);
            if (_perspective)
            {
                // Clean projective placement: the row's baseline is the projection of a 3D
                // horizontal line, so every baseline passes through the vanishing point and the
                // reading row (d = 0) comes out exactly horizontal.
                double sLeft = _plane.Scale(0);
                double sRight = _plane.Scale(_columnWidth);
                double d = Snap(offset);
                double y0 = Snap(anchorY + d * sLeft);
                double y1 = Snap(anchorY + d * sRight);
                double x0 = x;
                double x1 = x + _plane.MapX(_columnWidth);
                double shear = Math.Abs(x1 - x0) > 1 ? (y1 - y0) / (x1 - x0) : 0;

                dc.PushTransform(new TranslateTransform(x0, y0));
                dc.PushTransform(new MatrixTransform(1, shear, 0, 1, 0, 0));
                if (Math.Abs(scale - 1) > 0.001)
                    dc.PushTransform(new ScaleTransform(scale, scale, _scaleCenterX, 0));
                if (CaptureRows) _drawnRows.Add($"{i}:{y0:0.0}/{alpha:0.00}/{scale:0.000}");
                DrawRow(dc, render, i, alpha, Math.Abs(i - _current));
                if (Math.Abs(scale - 1) > 0.001) dc.Pop();
                dc.Pop();
                dc.Pop();
            }

            // Flat fallback only: the perspective branch above already drew this row.
            if (_perspective) continue;

            dc.PushTransform(new TranslateTransform(x, y));
            if (Math.Abs(scale - 1) > 0.001) dc.PushTransform(new ScaleTransform(scale, scale, _scaleCenterX, 0));

            if (CaptureRows) _drawnRows.Add($"{i}:{y:0.0}/{alpha:0.00}/{scale:0.000}");
            DrawRow(dc, render, i, alpha, Math.Abs(i - _current));

            if (Math.Abs(scale - 1) > 0.001) dc.Pop();
            dc.Pop();
        }
        _wkRows = System.Diagnostics.Stopwatch.GetElapsedTime(rw0).TotalMilliseconds;
        _wkInlineBakes = _bakeCounter - bakeStart;

        // The rebuild is spread over frames, so keep asking for the next one: without this the column
        // would sit on the previous geometry until something else invalidated it.
        if (_rebuilding)
            Dispatcher.BeginInvoke(new Action(InvalidateVisual), System.Windows.Threading.DispatcherPriority.Render);
        // Same for a colour change: the neighbours are re-baked a couple per frame, so keep repainting
        // until every visible line carries the new colour (otherwise a paused player would keep the old
        // colour on the lines the budget did not reach).
        _recolorPending = _recolorStale;
        if (_recolorPending)
            Dispatcher.BeginInvoke(new Action(InvalidateVisual), System.Windows.Threading.DispatcherPriority.Render);
        }

    private void DrawFrostedBackdrop(DrawingContext dc, double width, double height)
    {
        if (BackdropDrawnByHost) return;
        if (_backdropMode == BackdropMode.Transparent) return;

        var radius = Math.Min(18, Math.Min(width, height) / 6);
        var rect = new Rect(0, 0, width, height);
        var geometry = new RectangleGeometry(rect, radius, radius);
        geometry.Freeze();
        dc.PushClip(geometry);

        BitmapSource? image = _backdropMode switch
        {
            BackdropMode.Image => _backdropImageOverride,
            BackdropMode.Cover => _backdropImage ?? _backdropImageOverride,
            _ => null,
        };

        double opacity = Math.Clamp(_backdropOpacity, 0.1, 1);
        dc.PushOpacity(opacity);

        if (image != null)
        {
            var frosted = BackdropBlur.FrostToSize(image, (int)Math.Round(_backdropBlur),
                (int)Math.Round(width), (int)Math.Round(height),
                _fadeEnabled ? _fadeStrength : 0, _fadeH, _fadeV, 0.42);
            if (frosted != null) dc.DrawImage(frosted, rect);
        }
        else
        {
            var solid = new SolidColorBrush(_backdropColor);
            solid.Freeze();
            dc.DrawRectangle(solid, null, rect);
        }

        dc.Pop();
        dc.Pop();
    }

    /// <summary>
    /// Smooth edge mask: opaque white in the middle, fading to transparent at each edge. Built in
    /// normalised (0..1) space so the gradient is rendered at the element's real resolution.
    /// </summary>
    private Brush BuildFadeMask(double width, double height)
    {
        double strength = Math.Clamp(_fadeStrength, 0, 100) / 100.0;
        double bandX = Math.Clamp(_fadeH, 0, AppSettings.MaxFadeHPercent) / 100.0;
        double bandY = Math.Clamp(_fadeV, 0, AppSettings.MaxFadeVPercent) / 100.0;
        var clear = Color.FromArgb((byte)(255 * (1 - strength)), 255, 255, 255);

        var group = new DrawingGroup();
        group.Children.Add(new GeometryDrawing(Brushes.White, null, new RectangleGeometry(new Rect(0, 0, 1, 1))));

        if (bandX > 0.002)
        {
            group.Children.Add(new GeometryDrawing(
                new LinearGradientBrush(clear, Colors.White, 0) { MappingMode = BrushMappingMode.RelativeToBoundingBox },
                null, new RectangleGeometry(new Rect(0, 0, bandX, 1))));
            group.Children.Add(new GeometryDrawing(
                new LinearGradientBrush(Colors.White, clear, 0) { MappingMode = BrushMappingMode.RelativeToBoundingBox },
                null, new RectangleGeometry(new Rect(1 - bandX, 0, bandX, 1))));
        }
        if (bandY > 0.002)
        {
            group.Children.Add(new GeometryDrawing(
                new LinearGradientBrush(clear, Colors.White, 90) { MappingMode = BrushMappingMode.RelativeToBoundingBox },
                null, new RectangleGeometry(new Rect(0, 0, 1, bandY))));
            group.Children.Add(new GeometryDrawing(
                new LinearGradientBrush(Colors.White, clear, 90) { MappingMode = BrushMappingMode.RelativeToBoundingBox },
                null, new RectangleGeometry(new Rect(0, 1 - bandY, 1, bandY))));
        }

        var brush = new DrawingBrush(group)
        {
            Stretch = Stretch.Fill,
            Viewbox = new Rect(0, 0, 1, 1),
            ViewboxUnits = BrushMappingMode.RelativeToBoundingBox,
            Viewport = new Rect(0, 0, 1, 1),
            ViewportUnits = BrushMappingMode.RelativeToBoundingBox,
            TileMode = TileMode.None,
        };
        brush.Freeze();
        return brush;
    }

    /// <summary>
    /// Alpha multiplier that mirrors the picture's edge fade. It only scales the already-computed
    /// alpha — no extra Push/Pop and no early `continue` — so the row drawing stays balanced.
    /// </summary>
    /// <summary>
    /// Alpha ramp for a cue that runs into the top or bottom edge of the window. Unlike the backdrop
    /// feather (<see cref="EdgeFadeFactor"/>) this one is always on: the text has to dissolve at the
    /// edge whether or not there is a frost pill behind it. The band is taken from the font size and
    /// the box's own extent, so a long cue that wraps over two or three rows fades as a whole.
    /// It reaches exactly 0 at the edge (it used to stop at 0.10): with the old floor the outermost
    /// row stayed at 8% × 0.10 and was then dropped by the alpha cutoff in one frame — the top line
    /// of the column used to disappear in a single frame instead of fading out.
    /// </summary>
    private double TextEdgeFade(double top, double lineHeight, double height)
    {
        if (height <= 1) return 1;
        double band = Math.Max(_fontSize * 1.15, height * 0.08);
        if (band <= 1) return 1;
        double bottom = top + Math.Max(1, lineHeight);
        double entering = Math.Clamp(bottom / band, 0, 1);          // 0 while the box is still above
        double leaving = Math.Clamp((height - top) / band, 0, 1);   // 0 once the box left below
        double factor = Math.Min(entering, leaving);
        return BackdropBlur.Smooth(Math.Clamp(factor, 0, 1));
    }

    private double EdgeFadeFactor(double centreY, double height)
    {
        if (!_fadeEnabled || _backdropMode == BackdropMode.Transparent || _fadeStrength <= 1) return 1.0;
        double strength = Math.Clamp(_fadeStrength, 0, 100) / 100.0;
        double band = height * Math.Clamp(_fadeV, 0, AppSettings.MaxFadeVPercent) / 100.0;
        if (band <= 0.5) return 1.0;
        double distance = Math.Min(centreY, height - centreY);
        if (distance >= band) return 1.0;
        double factor = 1.0 - strength * BackdropBlur.Smooth(1.0 - Math.Max(0, distance) / band);
        return Math.Clamp(factor, 0.02, 1.0);
    }

    private double ScaleFor(int index, double progress)
    {
        double target = index == _current ? 1 : _neighbourScale;
        if (!_morphing || _previous < 0 || _previous == _current) return target;
        if (index != _previous && index != _current) return _neighbourScale;

        // Two full-size rows crossing in place is what read as clumsy: the outgoing row used to keep
        // its size for the whole 420 ms while the new one grew into it. Now the outgoing row shrinks
        // first and quickly (finishes in ~55% of the morph) and the incoming row grows after the
        // slide has started, so the eye follows one row up instead of watching two rows overlap.
        // Smoothstep, not ease-out: ease-out leaves t=0 with 3x the average speed, so the very first
        // frame after a cue change already shrank the row by ~3% of its size — with CurrentScale 1.71
        // that is a visible one-frame jump of every glyph (~10 px at the ends of a long line), then the
        // rest of the shrink looked like "moving back". Smoothstep starts and ends at zero velocity.
        if (index == _previous)
        {
            double t = Math.Clamp((Now - _morphStart) / (MorphDurationMs * 0.55), 0, 1);
            return 1 + (_neighbourScale - 1) * BackdropBlur.Smooth(t);
        }
        double incoming = Math.Clamp((Now - _morphStart - MorphDelayMs) / MorphDurationMs, 0, 1);
        return _neighbourScale + (1 - _neighbourScale) * BackdropBlur.Smooth(incoming);
    }

    private double AlphaFor(int index, double progress)
    {
        // 邻行亮度：0.52 时同样的字形看着比朗读行「粗」一档（灰字在深色底上，抗锯齿边更显眼）。
        // 降到 0.44，笔画看上去立细。
        static double ByDistance(int distance) => distance == 0
            ? 1.0
            : Math.Clamp(0.44 * Math.Pow(0.66, distance - 1), 0.08, 0.44);

        double target = ByDistance(Math.Abs(index - _current));
        if (!_morphing || _previous < 0 || _previous == _current) return target;
        if (index != _previous && index != _current)
            return ByDistance(Math.Abs(index - _current));

        // The leaving row dims as it slides away and the arriving row lights up as it grows, so only
        // one row ever reads as "the line being sung". Both ramps use smoothstep over the whole morph
        // and end at the neighbour values: an ease-out dropped the finished line to its resting
        // brightness within two frames, which is the "flash then nothing" the switch looked like.
        if (index == _previous)
        {
            double t = Math.Clamp((Now - _morphStart) / MorphDurationMs, 0, 1);
            return 1 + (target - 1) * BackdropBlur.Smooth(t);
        }
        double incoming = Math.Clamp((Now - _morphStart - MorphDelayMs * 0.5) / (MorphDurationMs * 0.8), 0, 1);
        // Start exactly at a neighbour's opacity: on the switch frame this row is still drawn with the
        // neighbour bake, and a jump from 0.52 to 0.42 darkened it for no reason.
        const double from = 0.52;
        return from + (1 - from) * BackdropBlur.Smooth(incoming);
    }

    private void DrawBackdrop(DrawingContext dc, double width, double height)
    {
        var brush = new RadialGradientBrush
        {
            Center = new Point(0.5, _anchor),
            GradientOrigin = new Point(0.5, _anchor),
            RadiusX = 0.62,
            RadiusY = 0.60,
        };
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(0x46, 0x04, 0x06, 0x0A), 0));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(0x26, 0x04, 0x06, 0x0A), 0.55));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, 0x04, 0x06, 0x0A), 1));
        brush.Freeze();
        dc.DrawRectangle(brush, null, new Rect(0, 0, width, height));
    }

    /// <summary>Softens the neighbour cues with a cheap multi-tap smear (the reference player blurs
    /// lines by distance). Off by default so the desktop overlay keeps its exact look.</summary>
    public bool SoftNeighbours { get; set; }

    /// <summary>
    /// Column width at which the 3D tilt looks "as designed" (0 = true geometry, the desktop
    /// overlay's behaviour). Below it the fan is scaled by width/reference so the same 倾斜角度 looks
    /// the same in any window size — otherwise a shorter row makes the identical drop look steeper.
    /// </summary>
    public double FanReferenceWidth
    {
        get => _fanReferenceWidth;
        set
        {
            if (Math.Abs(value - _fanReferenceWidth) < 0.5) return;
            _fanReferenceWidth = value;
            InvalidateCache();
            InvalidateVisual();
        }
    }
    private double _fanReferenceWidth;

    /// <summary>Diagnostics: the glow switch plus how many halos the current line actually built —
    /// the number that used to be 0/12 whenever 3D 倾斜歌词 was on.</summary>
    public string DebugGlow
    {
        get
        {
            int words = 0, halos = 0, lineHalo = 0;
            if (_cache.TryGetValue(_current, out var render))
            {
                words = render.Line.Words.Count;
                halos = render.Halos.Count(h => h != null);
                lineHalo = render.LineHalo != null ? 1 : 0;
            }
            return $"glow={(_glowEnabled ? 1 : 0)} strength={_glowStrength:0.00} radius={_glowRadius:0.0} " +
                   $"color=#{_glowColor.R:X2}{_glowColor.G:X2}{_glowColor.B:X2} recolored={DebugRecoloredHalos} " +
                   $"perspective={(_perspective ? 1 : 0)} halos={halos}/{words} lineHalo={lineHalo}";
        }
    }

    /// <summary>Diagnostics: the projection of the last paint (near/far glyph scale, compression,
    /// per-row tilt angle so two window widths can be compared numerically).</summary>
    public string DebugPlane
    {
        get
        {
            if (!_perspective) return "flat";
            double near = _plane.Scale(_columnWidth);
            double far = _plane.Scale(0);
            double pitch = _fontSize * 1.45 + _lineGap;
            double slope = _columnWidth > 1 ? pitch * (near - far) / _columnWidth : 0;
            return $"strength={_plane.Strength:0.000} apex={_plane.Camera:0} far={far:0.000} mid={_plane.Scale(_columnWidth / 2):0.000} " +
                   $"near={near:0.000} ratio={(far < 1e-6 ? 0 : near / far):0.00} " +
                   $"tilt={Math.Atan(slope) * 180 / Math.PI:0.00}deg";
        }
    }

    /// <summary>QA: builds every word of the reading line at once. The incremental builder needs dozens
    /// of real frames, which an offscreen harness does not get.</summary>
    internal void DebugBuildAllWordParts()
    {
        var render = GetLine(_current);
        if (render == null) return;
        BuildWordPartsFor(render);
        InvalidateVisual();
    }

    /// <summary>Diagnostics: which family/face actually renders the lyrics. The family name alone is
    /// not enough — a family that ships a real Medium face renders a whole weight heavier than a font
    /// that only has a Medium file (WPF then picks the closest face, which is that only face).</summary>
    public string DebugFont
    {
        get
        {
            var tf = TypefaceFor(true);
            string face = "?";
            try
            {
                if (tf.TryGetGlyphTypeface(out var glyph))
                    face = string.Join("/", glyph.FamilyNames.Values.Distinct()) + " weight=" + glyph.Weight +
                           " style=" + glyph.Style;
            }
            catch { }
            return $"font='{_fontName}' resolved={tf.FontFamily.Source} {face}";
        }
    }

    /// <summary>Diagnostics: karaoke state of the reading line — how many words the clock says have
    /// started, how many have ink built, and how many fragments actually carry a baked image (a zero
    /// there means the fill has nothing to blit and the line stays at the dim base).</summary>
    public string DebugKaraoke
    {
        get
        {
            var render = GetLine(_current);
            if (render == null) return "karaoke: cur=null";
            int started = 0, parts = 0, withInk = 0, withImage = 0;
            var words = render.Line.Words;
            for (int i = 0; i < words.Count; i++)
            {
                if (LyricTiming.WordProgress(words[i], _positionMs) > 0) started++;
                if (i >= render.WordSegments.Count) continue;
                foreach (var part in render.WordSegments[i])
                {
                    parts++;
                    if (part.Ink != null) withInk++;
                    if (part.Baked != null) withImage++;
                }
            }
            return $"karaoke: pos={_positionMs} cur={_current} words={words.Count} built={render.WordBuilt} " +
                   $"started={started} parts={parts} ink={withInk} imgs={withImage}";
        }
    }

    /// <summary>Diagnostics: what the reading line actually has to draw, in one line.</summary>
    public string DebugInkState
    {
        get
        {
            if (_doc == null) return "doc=null";
            var render = GetLine(_current);
            if (render == null) return $"cur={_current} render=null";
            var bounds = (render.WarpedInk ?? render.Ink).Bounds;
            return $"cur={_current} text={render.Line.Text.Length}ch words={render.Line.Words.Count} " +
                   $"segs={render.WordSegments.Count} h={render.Height:0.0} " +
                   $"ink={(bounds.IsEmpty ? "empty" : $"{bounds.Width:0}x{bounds.Height:0}")}";
        }
    }

    // ---------- baked text: per-frame blits instead of glyph-outline fills ----------

    /// <summary>One line pre-rasterised at device resolution. Per frame the column draws this image —
    /// plus a clipped copy for the sung part — instead of filling every glyph outline again. The
    /// outline fills cost ~8 ms per frame at 1226×782; the blits cost about 0.3 ms.</summary>
    private sealed class BakedText
    {
        public BitmapSource Base = null!;
        public BitmapSource? Sung;
        public Rect Rect;
        public double Scale = 1;
        public int Distance = -1;
        public bool Current;
        /// <summary>Text colour baked into both images; a mismatch means "stale, re-rasterise".</summary>
        public Color Color;
    }

    private BakedText? EnsureBaked(LineRender render, int distance, bool current)
    {
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        if (!(dpi > 0.1)) dpi = 1.0;
        var ready = current ? render.BakedCurrent : render.BakedPlain;
        if (ready != null && Math.Abs(ready.Scale - dpi) < 0.02 &&
            (ready.Color == _textColor || !RecolorOnTextColorChange)) return ready;
        // The reading line always re-rasterises at once (it is the one the colour is being judged on);
        // the neighbours share a small per-frame budget so a colour drag does not re-rasterise the whole
        // column in one frame (~40 ms) — they show the previous colour for a few frames instead.
        if (!current && ready != null && _recolorBudget <= 0)
        {
            _recolorStale = true;
            return ready;
        }
        if (!current) _recolorBudget--;
        // Both roles are normally baked when the line is built (see BuildLine); this is the safety
        // net for a line that reached the screen before that pass ran.
        int bucket = current ? 0 : (distance <= 1 ? distance : 2);
        var baked = BakeLine(render, bucket, current, dpi);
        if (current) render.BakedCurrent = baked; else render.BakedPlain = baked;
        return baked;
    }

    /// <summary>
    /// 文字颜色变了。几何不用重建（颜色不进 BuildLine），but the cached line images have the colour
    /// baked into their pixels, so they all have to be rasterised again. Immediate for the reading line,
    /// two neighbours per frame after that, with a repaint kept queued until nothing stale is drawn.
    /// </summary>
    private void QueueRecolor()
    {
        _recolorPending = true;
        _recolorBudget = 2;
        InvalidateVisual();
    }

    private int _recolorBudget;
    private bool _recolorPending;
    private bool _recolorStale;

    private static BitmapSource BakeImage(DrawingVisual visual, Rect rect, double scale)
    {
        var bitmap = new RenderTargetBitmap(
            Math.Max(1, (int)Math.Ceiling(rect.Width * scale)),
            Math.Max(1, (int)Math.Ceiling(rect.Height * scale)), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    private BakedText? BakeLine(LineRender render, int distance, bool current, double scale)
    {
        _bakeCounter++;
        var baseGeometry = render.WarpedInk ?? render.Ink;
        // The sung image has to live in the SAME space as the base image: the karaoke clips are
        // warped quads, so baking the unwarped ink here made the bright copy land offset from the
        // dim one (visible as doubled text on the reading line).
        var sungGeometry = baseGeometry;
        var box = baseGeometry.Bounds;
        if (box.IsEmpty || box.Width < 0.5 || box.Height < 0.5) return null;
        var sungBox = sungGeometry.Bounds;
        if (!sungBox.IsEmpty) box = Rect.Union(box, sungBox);
        if (_translation && render.Translation is { } tg && !tg.Bounds.IsEmpty)
            box = Rect.Union(box, tg.Bounds);

        // Padding covers the drop shadow (2.5 px down), the nine-tap neighbour smear, and antialiasing
        // slack, so nothing is ever clipped at the image border.
        double smear = current ? 0 : SoftRadius(distance);
        double pad = Math.Max(6.0, _fontSize * 0.35) + smear;
        if (!(scale > 0.1)) scale = 1;
        // Snap the image box to whole device pixels and derive its DIP size from the pixel size, so
        // the blit is exactly 1:1 and the text stays as crisp as the vector original.
        int pixelWidth = Math.Max(1, (int)Math.Round((box.Width + pad * 2) * scale));
        int pixelHeight = Math.Max(1, (int)Math.Round((box.Height + pad * 2) * scale));
        var rect = new Rect(Math.Round((box.X - pad) * scale) / scale, Math.Round((box.Y - pad) * scale) / scale,
                            pixelWidth / scale, pixelHeight / scale);

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.PushTransform(new ScaleTransform(scale, scale));
            dc.PushTransform(new TranslateTransform(-rect.X, -rect.Y));
            if (current)
            {
                var shadowBrush = new SolidColorBrush(Color.FromArgb(0x62, 0, 0, 0));
                shadowBrush.Freeze();
                dc.PushTransform(new TranslateTransform(0, 2.5));
                dc.DrawGeometry(shadowBrush, null, baseGeometry);
                dc.Pop();

                var dim = _textColor;
                dim.A = (byte)(0.30 * 255);
                var dimBrush = new SolidColorBrush(dim);
                dimBrush.Freeze();
                dc.DrawGeometry(dimBrush, null, baseGeometry);

                if (_translation && render.Translation is { } trans)
                {
                    var transBrush = TranslationBrush();
                    var transInk = _perspective && render.Plane.Camera > 0
                        ? render.Plane.Warp(trans, render.Baseline) ?? trans
                        : trans;
                    dc.DrawGeometry(transBrush, null, transInk);
                }
            }
            else
            {
                var ink = _textColor;
                ink.A = 255;
                var brush = new SolidColorBrush(ink);
                brush.Freeze();
                if (smear < 0.35)
                {
                    dc.DrawGeometry(brush, null, baseGeometry);
                }
                else
                {
                    var soft = new SolidColorBrush(Color.FromArgb((byte)(0.42 * 255), ink.R, ink.G, ink.B));
                    soft.Freeze();
                    for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        if (dx == 0 && dy == 0) continue;
                        dc.PushTransform(new TranslateTransform(dx * smear, dy * smear));
                        dc.DrawGeometry(soft, null, baseGeometry);
                        dc.Pop();
                    }
                    dc.DrawGeometry(brush, null, baseGeometry);
                }
            }
            dc.Pop();
            dc.Pop();
        }
        var baseImage = BakeImage(visual, rect, scale);

        BitmapSource? sungImage = null;
        if (current)
        {
            var sungVisual = new DrawingVisual();
            using (var dc = sungVisual.RenderOpen())
            {
                dc.PushTransform(new ScaleTransform(scale, scale));
                dc.PushTransform(new TranslateTransform(-rect.X, -rect.Y));
                var ink = _textColor;
                ink.A = 255;
                var brush = new SolidColorBrush(ink);
                brush.Freeze();
                dc.DrawGeometry(brush, null, sungGeometry);
                dc.Pop();
                dc.Pop();
            }
            sungImage = BakeImage(sungVisual, rect, scale);

            // Per-word ink baked separately: the karaoke fill keeps its exact per-word clips, it just
            // blits a small image instead of filling that word's glyph outlines again every frame.
            foreach (var parts in render.WordSegments)
            {
                foreach (var part in parts) EnsurePartBaked(part, scale);
            }
        }

        return new BakedText
        {
            Base = baseImage,
            Sung = sungImage,
            Rect = rect,
            Scale = scale,
            Distance = distance,
            Current = current,
            Color = _textColor,
        };
    }

    /// <summary>Draws the baked line. The caller already pushed the row transform, so the image lands
    /// exactly where the geometry used to.</summary>
    /// <summary>Rasterises one word fragment at device scale (~60×70 px). Idempotent and lazy: the
    /// reading-line bake is prepared a frame early, before a cue has any word segments, so the segments
    /// have to carry their own image instead of borrowing one from that bake.</summary>
    private WordSegment EnsurePartBaked(WordSegment part, double scale)
    {
        if (part.Ink == null) return part;
        if (!(scale > 0.1)) scale = 1;
        if (part.Baked != null && Math.Abs(part.BakedScale - scale) < 0.02 &&
            (part.BakedColor == _textColor || !RecolorOnTextColorChange)) return part;

        var partBox = part.Ink.Bounds;
        if (partBox.IsEmpty || partBox.Width < 0.3 || partBox.Height < 0.3) return part;
        // Same pixel-grid snapping as BakeLine: a fractional blit destination would resample the image.
        int pixelWidth = Math.Max(1, (int)Math.Round((partBox.Width + 4) * scale));
        int pixelHeight = Math.Max(1, (int)Math.Round((partBox.Height + 4) * scale));
        var partRect = new Rect(Math.Round((partBox.X - 2) * scale) / scale, Math.Round((partBox.Y - 2) * scale) / scale,
                                pixelWidth / scale, pixelHeight / scale);
        var partVisual = new DrawingVisual();
        using (var pdc = partVisual.RenderOpen())
        {
            pdc.PushTransform(new ScaleTransform(scale, scale));
            pdc.PushTransform(new TranslateTransform(-partRect.X, -partRect.Y));
            var partInk = _textColor;
            partInk.A = 255;
            var partBrush = new SolidColorBrush(partInk);
            partBrush.Freeze();
            pdc.DrawGeometry(partBrush, null, part.Ink);
            pdc.Pop();
            pdc.Pop();
        }
        part.Baked = BakeImage(partVisual, partRect, scale);
        _bakeCounter++;
        part.BakedRect = partRect;
        part.BakedScale = scale;
        part.BakedColor = _textColor;
        return part;
    }

    /// <summary>QA switch: 1 = skip the per-row opacity layer (rows draw at full alpha). Used only to
    /// measure how much of the frame cost is WPF's intermediate surface per translucent row.</summary>
    private static readonly bool NoRowOpacity = Environment.GetEnvironmentVariable("LYRIC_NOOPACITY") == "1";

    private bool DrawBaked(DrawingContext dc, LineRender render, double alpha, int distance, bool current)
    {
        var baked = EnsureBaked(render, distance, current);
        if (baked == null) return false;
        bool fade = alpha < 0.995 && !NoRowOpacity;
        if (fade) dc.PushOpacity(Math.Clamp(alpha, 0, 1));
        dc.DrawImage(baked.Base, baked.Rect);
        if (current && baked.Sung != null) DrawSungParts(dc, render, baked);
        if (fade) dc.Pop();
        return true;
    }

    /// <summary>Karaoke fill as a clip over the baked ink: one image draw per word fragment instead of
    /// one glyph-outline fill per word fragment.</summary>
    private void DrawSungParts(DrawingContext dc, LineRender render, BakedText baked)
    {
        var words = render.Line.Words;
        if (words.Count == 0)
        {
            dc.DrawImage(baked.Sung!, baked.Rect);
            return;
        }
        for (int i = 0; i < words.Count && i < render.WordSegments.Count; i++)
        {
            var word = words[i];
            float p = LyricTiming.WordProgress(word, _positionMs);
            if (p <= 0) continue;
            var parts = render.WordSegments[i];
            if (parts.Count == 0) continue;
            double total = 0;
            foreach (var part in parts) total += part.Box.Width;
            double remaining = total * p;
            if (remaining <= 0.2) continue;
            foreach (var part in parts)
            {
                double w = Math.Min(part.Box.Width, remaining);
                if (w <= 0) break;
                if (part.Ink == null)
                {
                    remaining -= part.Box.Width;
                    continue;
                }
                // Safety net for a segment whose image was not baked yet (first frame of a cue that a
                // seek made current). One small tile, not the whole line.
                EnsurePartBaked(part, baked.Scale);
                if (part.Baked == null)
                {
                    remaining -= part.Box.Width;
                    continue;
                }
                if (w < part.Box.Width - 0.5)
                {
                    double pad = _fontSize * 0.35;
                    var cut = new Rect(part.Box.X, part.Box.Y - pad, w, part.Box.Height + pad * 2);
                    Geometry clip = render.WarpedInk != null
                        ? render.Plane.Quad(cut, render.Baseline)
                        : new RectangleGeometry(cut);
                    dc.PushClip(clip);
                    dc.DrawImage(part.Baked, part.BakedRect);
                    dc.Pop();
                }
                else
                {
                    dc.DrawImage(part.Baked, part.BakedRect);
                }
                remaining -= part.Box.Width;
            }
        }
    }

    /// <summary>QA switch: 1 = the old behaviour, where a row flipped from the reading-line bake to the
    /// neighbour bake in a single frame (that flip is the flash this fix removes).</summary>
    private static readonly bool NoMorphFade = Environment.GetEnvironmentVariable("LYRIC_NOFADE") == "1";

    /// <summary>Draws one row with the look its role asks for, cross-fading the two bakes while a cue
    /// change moves the rows past each other.</summary>
    private void DrawRow(DrawingContext dc, LineRender render, int index, double alpha, int distance)
    {
        if (NoMorphFade)
        {
            if (index == _current) DrawCurrentLine(dc, render, alpha);
            else DrawPlainLine(dc, render, alpha, distance);
            return;
        }
        bool morphing = _morphing && _previous >= 0 && _previous != _current;
        double neighbour;
        if (!morphing) neighbour = index == _current ? 0 : 1;
        else if (index == _previous) neighbour = MorphBlend;
        else if (index == _current) neighbour = 1 - MorphBlend;
        else neighbour = 1;

        if (neighbour <= 0.002)
        {
            DrawCurrentLine(dc, render, alpha);
            return;
        }
        if (neighbour >= 0.998)
        {
            DrawPlainLine(dc, render, alpha, distance);
            return;
        }
        // Both images land on the same pixels, so the sum is the ordinary layer blend of the two looks.
        dc.PushOpacity(1 - neighbour);
        DrawCurrentLine(dc, render, alpha);
        dc.Pop();
        dc.PushOpacity(neighbour);
        DrawPlainLine(dc, render, alpha, distance);
        dc.Pop();
    }

    private void DrawPlainLine(DrawingContext dc, LineRender render, double alpha, int distance = 99)
    {
        if (UseBakedText && DrawBaked(dc, render, alpha, distance, current: false)) return;
        var c = _textColor;
        c.A = (byte)Math.Clamp(alpha * 255, 0, 255);
        var brush = new SolidColorBrush(c);
        brush.Freeze();
        var geometry = render.WarpedInk ?? render.Ink;

        double radius = SoftRadius(distance);
        if (radius < 0.35)
        {
            dc.DrawGeometry(brush, null, geometry);
            return;
        }

        // Nine taps around the glyph outline: with low neighbour alpha this reads as a gaussian blur
        // while staying a pure geometry draw (no per-line bitmaps, no cache).
        var soft = new SolidColorBrush(Color.FromArgb((byte)Math.Max(1, (int)(c.A * 0.42)), c.R, c.G, c.B));
        soft.Freeze();
        for (int dy = -1; dy <= 1; dy++)
        for (int dx = -1; dx <= 1; dx++)
        {
            if (dx == 0 && dy == 0) continue;
            dc.PushTransform(new TranslateTransform(dx * radius, dy * radius));
            dc.DrawGeometry(soft, null, geometry);
            dc.Pop();
        }
        dc.DrawGeometry(brush, null, geometry);
    }

    /// <summary>Blur radius in DIPs for a cue <paramref name="distance"/> lines from the current one.</summary>
    private double SoftRadius(int distance)
    {
        if (!SoftNeighbours || distance < 2) return 0;
        double factor = distance switch
        {
            1 => 0.014,
            2 => 0.024,
            3 => 0.040,
            4 => 0.058,
            _ => 0.072,
        };
        return _fontSize * factor;
    }

    private void DrawCurrentLine(DrawingContext dc, LineRender render, double alpha)
    {
        // A jump (seek) can make a line current before its karaoke ink was built; do it once here.
        // This has to run before the baked path too: the per-word bakes are built from these segments.
        if (render.WordSegments.Count == 0 && render.Line.Words.Count > 0) BuildWordPartsFor(render, 3);
        if (_glowEnabled && _glowStrength > 0.001)
        {
            // Word-timed cues glow per word; plain LRC files carry no word timing at all, so those
            // lines used to render with no halo whatsoever — the glow switch looked dead on every
            // NetEase-sourced lyric. Bloom the whole line instead.
            if (render.Line.Words.Count == 0) DrawLineGlow(dc, render, alpha);
            else if (render.Halos.Length > 0) DrawGlow(dc, render);
        }
        if (UseBakedText && DrawBaked(dc, render, alpha, 0, current: true)) return;

        byte mainAlpha = (byte)Math.Clamp(255 * Math.Min(1, alpha), 0, 255);
        // Soft drop shadow so the lyric stays legible over a bright wallpaper.
        byte shadowAlpha = (byte)(0x62 * Math.Min(1, alpha));
        var shadow = new SolidColorBrush(Color.FromArgb(shadowAlpha, 0, 0, 0));
        shadow.Freeze();
        var inkGeometry = render.WarpedInk ?? render.Ink;
        dc.PushTransform(new TranslateTransform(0, 2.5));
        dc.DrawGeometry(shadow, null, inkGeometry);
        dc.Pop();

        var dim = _textColor;
        dim.A = (byte)(0.30 * mainAlpha);
        var dimBrush = new SolidColorBrush(dim);
        dimBrush.Freeze();
        dc.DrawGeometry(dimBrush, null, inkGeometry);

        var ink = _textColor;
        ink.A = mainAlpha;
        var inkBrush = new SolidColorBrush(ink);
        inkBrush.Freeze();

        var words = render.Line.Words;
        if (words.Count == 0)
        {
            // No per-word timing (plain LRC lines, the opening card): the whole cue is "sung" the
            // moment it becomes current. Without this the line only ever got the 0.30 dim base and
            // looked like an inactive neighbour.
            dc.DrawGeometry(inkBrush, null, inkGeometry);
        }
        for (int i = 0; i < words.Count && i < render.WordSegments.Count; i++)
        {
            var word = words[i];
            float p = LyricTiming.WordProgress(word, _positionMs);
            if (p <= 0) continue;
            var parts = render.WordSegments[i];
            if (parts.Count == 0) continue;

            double total = 0;
            foreach (var part in parts) total += part.Box.Width;
            double remaining = total * p;
            if (remaining <= 0.2) continue;

            foreach (var part in parts)
            {
                double w = Math.Min(part.Box.Width, remaining);
                if (w <= 0) break;
                if (part.Ink == null)
                {
                    remaining -= part.Box.Width;
                    continue;
                }
                if (w < part.Box.Width - 0.5)
                {
                    double pad = _fontSize * 0.35;
                    var cut = new Rect(part.Box.X, part.Box.Y - pad, w, part.Box.Height + pad * 2);
                    Geometry clip = render.WarpedInk != null
                        ? render.Plane.Quad(cut, render.Baseline)
                        : new RectangleGeometry(cut);
                    dc.PushClip(clip);
                    dc.DrawGeometry(inkBrush, null, part.Ink);
                    dc.Pop();
                }
                else
                {
                    dc.DrawGeometry(inkBrush, null, part.Ink);
                }
                remaining -= part.Box.Width;
            }
        }

        if (_translation && render.Translation is { } trans)
        {
            var brush = TranslationBrush((byte)(0xB4 * alpha / 1.0));
            var transInk = _perspective && render.Plane.Camera > 0
                ? render.Plane.Warp(trans, render.Baseline) ?? trans
                : trans;
            dc.DrawGeometry(brush, null, transInk);
        }
    }

    /// <summary>The translation line follows the lyric colour at 70% instead of being hard-coded
    /// white — white is what it used to be, so the default look does not change, but a coloured text
    /// no longer leaves a white line in the middle of it.</summary>
    private SolidColorBrush TranslationBrush(byte alpha = 0xB4)
    {
        var colour = _textColor;
        colour.A = alpha;
        var brush = new SolidColorBrush(colour);
        brush.Freeze();
        return brush;
    }

    /// <summary>Bloom for a cue that has no per-word timing: one halo behind the whole line.</summary>
    private void DrawLineGlow(DrawingContext dc, LineRender render, double lineAlpha)
    {
        // 整行光晕（没有逐字时间的纯 LRC）是又大又糊的一张蒙版，半径变了才重糊一次，别每帧都做。
        if (render.LineHalo == null || Math.Abs(render.LineHaloRadius - _glowRadius) > 0.5)
        {
            render.LineHalo = BuildLineHalo(render);
            render.LineHaloRadius = _glowRadius;
        }
        if (render.LineHalo is not { } halo) return;
        double alpha = Math.Clamp(0.7 * _glowStrength, 0, 1) * Math.Clamp(lineAlpha, 0, 1);
        if (alpha <= 0.002) return;
        dc.PushOpacity(alpha);
        dc.DrawImage(halo.Image, new Rect(halo.X, halo.Y, halo.Image.PixelWidth, halo.Image.PixelHeight));
        dc.Pop();
    }

    private GlowHalo? BuildLineHalo(LineRender render)
    {
        var ink = render.WarpedInk ?? render.Ink;
        if (ink == null || ink.IsEmpty()) return null;
        var bounds = ink.Bounds;
        if (bounds.Width <= 0.5 || bounds.Height <= 0.5) return null;
        try
        {
            return GlowHalo.Build(ink, bounds, _glowRadius, _glowColor);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Halos whose tint was swapped in place by the last colour change (diagnostics).</summary>
    public long DebugRecoloredHalos { get; private set; }

    /// <summary>QA only: pretend a colour change needs the geometry, so both latencies can be measured.</summary>
    public bool DebugGlowColorRebuild { get; set; }

    /// <summary>
    /// Hands a new glow colour to every cached bloom without rebuilding any geometry: the alpha mask
    /// inside each halo is reused, so this costs a few hundred kilobytes of byte arithmetic rather
    /// than a rasterise + three blur passes per word. That is what makes the colour live under the
    /// mouse instead of arriving after a rebuild — and why the glow no longer blinks out mid-drag.
    /// </summary>
    private void RecolorHalos()
    {
        long count = 0;
        foreach (var render in _cache.Values) count += RecolorLine(render);
        foreach (var render in _stale.Values) count += RecolorLine(render);
        DebugRecoloredHalos = count;
    }

    /// <summary>Marks every cached halo as needing a re-blur at the current radius. The masks are rebuilt
    /// a few words per frame by <see cref="RefreshGlowHalos"/>, so dragging the glow radius keeps the
    /// text and the karaoke fill exactly where they were.</summary>
    private void InvalidateGlowHalos()
    {
        foreach (var render in _cache.Values) render.HaloRadius = Array.Empty<double>();
        foreach (var render in _stale.Values) render.HaloRadius = Array.Empty<double>();
        InvalidateVisual();
    }

    /// <summary>A word halo is a blurred mask: only its radius and colour are baked in, so a radius change
    /// re-blurs a handful of words per frame instead of rebuilding the line. Building all thirteen at
    /// once is ~20 ms, which is why the radius used to ride on the whole-line rebuild — and that rebuild
    /// is what blanked the glow and collapsed the karaoke for a frame or two while the slider moved.</summary>
    private void RefreshGlowHalos(int budget)
    {
        if (!_glowEnabled || _glowStrength <= 0.001) return;
        if (!_cache.TryGetValue(_current, out var render)) return;
        var shapes = render.HaloShapes;
        if (shapes.Length == 0) return;
        if (render.HaloRadius.Length != shapes.Length)
        {
            render.HaloRadius = new double[shapes.Length];
            for (int i = 0; i < render.HaloRadius.Length; i++) render.HaloRadius[i] = double.NaN;
        }
        int done = 0;
        for (int i = 0; i < shapes.Length && done < budget; i++)
        {
            if (Math.Abs(render.HaloRadius[i] - _glowRadius) < 0.5) continue;
            if (shapes[i] is not { } shape)
            {
                render.HaloRadius[i] = _glowRadius;
                continue;
            }
            try { render.Halos[i] = GlowHalo.Build(shape, render.HaloBounds[i], _glowRadius, _glowColor); }
            catch { }
            render.HaloRadius[i] = _glowRadius;
            done++;
        }
        if (done > 0) InvalidateVisual();
    }

    private long RecolorLine(LineRender render)
    {
        long count = 0;
        for (int i = 0; i < render.Halos.Length; i++)
        {
            if (render.Halos[i] is not { } halo) continue;
            render.Halos[i] = halo.Recolor(_glowColor);
            count++;
        }
        if (render.LineHalo is { } lineHalo)
        {
            render.LineHalo = lineHalo.Recolor(_glowColor);
            count++;
        }
        return count;
    }

    private void DrawGlow(DrawingContext dc, LineRender render)
    {
        var words = render.Line.Words;
        for (int i = 0; i < words.Count && i < render.Halos.Length; i++)
        {
            if (render.Halos[i] is not { } halo) continue;
            var word = words[i];
            float strength = LyricTiming.GlowStrength(word, _positionMs);
            if (strength <= 0.002f) continue;
            float p = LyricTiming.WordProgress(word, _positionMs);
            float reveal = LyricTiming.Smooth(p / 0.7f);
            float weight = LyricTiming.GlowWeight(word);
            double alpha = Math.Clamp(0.85 * weight * strength * reveal * _glowStrength, 0, 1);
            if (alpha <= 0.002) continue;

            dc.PushOpacity(alpha);
            dc.DrawImage(halo.Image, new Rect(halo.X, halo.Y, halo.Image.PixelWidth, halo.Image.PixelHeight));
            dc.Pop();
        }
    }

    // ---------- layout cache ----------
    private Typeface TypefaceFor(bool medium)
    {
        if (_typefaces.TryGetValue(medium, out var tf)) return tf;
        // 正文字重用 Normal, 不用 Medium: 文楷只有一个 Medium 字面 (WPF 挑最接近的 -> 原样),
        // 但黑体系统字体一般有真正的 Medium 面, 于是播放页笔画比桌面歌词粗一档. 用户报的
        // 「字体笔画粗」就是这个, 不是字号大.
        tf = new Typeface(FontResolver.Resolve(_fontName), FontStyles.Normal,
            FontWeights.Normal, FontStretches.Normal);
        _typefaces[medium] = tf;
        return tf;
    }

    private LineRender? GetLine(int index)
    {
        if (_doc == null || index < 0 || index >= _doc.Lines.Count) return null;
        // 重建期间整列只画上一份几何：以前先查 _cache，于是重建到哪一行、哪一行就先换成新姿态，
        // 拖 3D 倾斜/交点的时候整列一半旧一半新，看着就是文字在闪。等整窗建完再一次性换。
        if (_rebuilding && _stale.TryGetValue(index, out var stale)) return stale;
        if (_cache.TryGetValue(index, out var cached)) return cached;
        return BuildLine(index);
    }

    /// <summary>Number of lines rebuilt per frame while a settings change is settling.</summary>
    private const int RebuildLinesPerFrame = 1;

    private bool _rebuilding;
    private readonly Dictionary<int, LineRender> _stale = new();

    /// <summary>Drops the geometry cache but keeps the previous result as a stand-in, so the rebuild
    /// can be spread over several frames instead of one long stall.</summary>
    private void InvalidateCache()
    {
        // Geometry changed (font/tilt/width/settings): the frozen horizontal position is stale.
        _shiftLock = double.NaN;
        // 重建期间又要重建（拖 3D 倾斜/交点时每帧都在改）：让它先把这一轮跑完，否则每帧都从零开始，
        // 整列永远换不上新姿态（旧代码是每帧丢一次 → 一半旧一半新 → 文字闪）。
        if (_rebuilding)
        {
            _rebuildAgain = true;
            return;
        }
        // Merge, do not replace: a drag invalidates again before the previous rebuild finished, and
        // dropping the older placeholders here would force a full rebuild on the next frame.
        foreach (var kv in _cache) _stale[kv.Key] = kv.Value;
        _cache.Clear();
        _rebuilding = _stale.Count > 0;
        _rebuildAgain = false;
    }

    private bool _rebuildAgain;

    private LineRender? BuildLine(int index)
    {
        if (_doc == null || index < 0 || index >= _doc.Lines.Count) return null;
        if (_cache.TryGetValue(index, out var already)) return already;

        var line = _doc.Lines[index];
        if (string.IsNullOrWhiteSpace(line.Text)) return null;

        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var ft = new FormattedText(line.Text, CultureInfo.CurrentUICulture, System.Windows.FlowDirection.LeftToRight,
            TypefaceFor(true), _fontSize, Brushes.White, dpi)
        {
            MaxTextWidth = _columnWidth,
            MaxLineCount = 12,
            TextAlignment = TextAlignment.Center,
            LineHeight = _fontSize * LineHeightRatio,
        };

        var render = new LineRender
        {
            Line = line,
            Height = ft.Height,
            Width = ft.WidthIncludingTrailingWhitespace,
            Ink = ft.BuildGeometry(new Point(0, 0)),
            Formatted = ft,
        };

        if (_perspective)
        {
            render.Plane = LyricPlane.For(_columnWidth, _tilt, _vpOffset, _fanReferenceWidth);
            // Anchor the projection on the *middle* of the cue, so the horizontal reference line
            // passes between its rows and every row of the cue lands on the same plane.
            render.Baseline = render.Ink.Bounds.Top + render.Ink.Bounds.Height / 2;
            render.InkHeight = render.Ink.Bounds.Height;
            if (Environment.GetEnvironmentVariable("LYRIC_WORDS") == "1")
            {
                try
                {
                    var sb = new System.Text.StringBuilder();
                    sb.AppendLine($"line='{line.Text}' start={line.StartMs} end={line.EndMs} words={line.Words.Count}");
                    foreach (var w in line.Words.Take(12))
                        sb.AppendLine($"   '{w.Text}' {w.StartMs}->{w.EndMs} off {w.StartOffset}..{w.EndOffset}");
                    sb.AppendLine($"   render width={render.Width:0} height={render.Height:0} rects={render.WordRects.Count}");
                    foreach (var rs in render.WordRects.Take(12))
                        sb.AppendLine("      " + string.Join(" | ", rs.Select(r => $"{r.X:0},{r.Y:0} {r.Width:0}x{r.Height:0}")));
                    System.IO.File.AppendAllText(System.IO.Path.Combine(AppPaths.DataDir, "words.log"), sb.ToString());
                }
                catch { }
            }
            if (Environment.GetEnvironmentVariable("LYRIC_DEBUG") == "1")
            {
                try
                {
                    var b = render.Ink.Bounds;
                    System.IO.File.AppendAllText(System.IO.Path.Combine(AppPaths.DataDir, "warp.log"),
                        $"line={line.Text} bounds=({b.Left:0},{b.Top:0})-({b.Right:0},{b.Bottom:0}) baseline={render.Baseline:0} fit={render.Plane.Fit:0.000} cam={render.Plane.Camera:0} strength={render.Plane.Strength:0.000} far={render.Plane.Scale(0):0.000} near={render.Plane.Scale(_columnWidth):0.000}\n");
                }
                catch { }
            }
            render.WarpedInk = render.Plane.Warp(render.Ink, render.Baseline) ?? render.Ink;
        }

        // Per-word karaoke ink is only ever drawn for the cue that is being sung (and its immediate
        // neighbours, which become current next). Building it for every cached line — a boolean
        // geometry intersection per word on a warped path — was most of a 20 ms line rebuild.
        if (!_rebuilding && line.Words.Count > 0 && index == _current) BuildWordParts(render, ft);

        if (_translation && line.Translation.Length > 0)
        {
            var tft = new FormattedText(line.Translation, CultureInfo.CurrentUICulture, System.Windows.FlowDirection.LeftToRight,
                TypefaceFor(false), _fontSize * 0.52, Brushes.White, dpi)
            {
                MaxTextWidth = _columnWidth,
                MaxLineCount = 2,
                TextAlignment = TextAlignment.Center,
            };
            var geo = tft.BuildGeometry(new Point(0, render.Height + _fontSize * 0.22));
            render.Translation = geo;
            render.TranslationHeight = tft.Height;
            render.ExtraHeight = _fontSize * 0.22 + tft.Height + 14;
        }

        _cache[index] = render;
        // Only the plain image is baked here; the reading-line image is baked one per frame by the
        // pending pass in DrawCore. Both in one frame was still an 8-12 ms stall on the frame that
        // built a new line, which is exactly when a cue change happens.
        try
        {
            double dpiBake = VisualTreeHelper.GetDpi(this).DpiScaleX;
            if (!(dpiBake > 0.1)) dpiBake = 1.0;
            render.BakedPlain = BakeLine(render, 2, false, dpiBake);
        }
        catch { }
        return render;
    }

    /// <summary>Builds the per-word karaoke ink and the glow halos for one line. Only ever needed for
    /// the cue being sung, so it is done on demand instead of for every cached line.</summary>
    private void BuildWordPartsFor(LineRender render, int budget = int.MaxValue)
    {
        if (render.Formatted == null) return;
        BuildWordParts(render, render.Formatted, budget);
    }

    /// <summary>How many words of karaoke ink one frame may add. A whole cue costs ~20 ms there (one
    /// layout query per character plus a boolean intersection per word), and it used to run on the
    /// single frame where the cue became current — the switch visibly hitched instead of sliding. Two
    /// words a frame keeps every frame under ~2 ms and still finishes a cue seconds before it is sung.</summary>
    private const int WordPartsPerFrame = 2;

    /// <summary>Spends this frame's word budget on the reading line, then on the cue after it — the one
    /// that becomes current next, and normally has seconds to spare.</summary>
    private void BuildWordPartsAhead()
    {
        if (_doc == null) return;
        if (ExtendWordParts(_current)) return;
        ExtendWordParts(_current + 1);
    }

    private bool ExtendWordParts(int index)
    {
        if (_doc == null || index < 0 || index >= _doc.Lines.Count) return false;
        if (!_cache.TryGetValue(index, out var render)) return false;
        if (render.Formatted == null) return false;
        if (render.Line.Words.Count == 0) return false;
        if (render.WordBuilt >= render.Line.Words.Count) return false;
        BuildWordParts(render, render.Formatted, WordPartsPerFrame);
        return true;
    }

    private void BuildWordParts(LineRender render, FormattedText ft, int budget = int.MaxValue)
    {
        var line = render.Line;
        var words = line.Words;
        if (words.Count == 0) return;

        // A word can straddle a wrap; keep one rect per text row so the karaoke edge never
        // paints a tall band across both rows.
        if (render.WordSegments.Count == 0)
        {
            for (int i = 0; i < words.Count; i++) render.WordSegments.Add(new List<WordSegment>(0));
            render.WordRects.Clear();
            for (int i = 0; i < words.Count; i++) render.WordRects.Add(new List<Rect>());
            render.Halos = _glowEnabled && _glowStrength > 0.001
                ? new GlowHalo?[words.Count]
                : Array.Empty<GlowHalo?>();
            if (render.Halos.Length > 0)
            {
                render.HaloShapes = new Geometry?[words.Count];
                render.HaloBounds = new Rect[words.Count];
                render.HaloRadius = new double[words.Count];
                for (int i = 0; i < render.HaloRadius.Length; i++) render.HaloRadius[i] = double.NaN;
            }
        }

        // Keep only the ink that belongs to each word/row, so the karaoke cut can never paint a
        // neighbouring glyph.
        double margin = _fontSize * 0.30;
        var sourceInk = render.WarpedInk ?? render.Ink;
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        if (!(dpi > 0.1)) dpi = 1.0;

        int remaining = words.Count - render.WordBuilt;
        int end = budget >= remaining ? words.Count : render.WordBuilt + Math.Max(1, budget);
        for (int i = render.WordBuilt; i < end; i++)
        {
            var w = words[i];
            int start = Math.Clamp(w.StartOffset, 0, line.Text.Length);
            int stop = Math.Clamp(w.EndOffset, start, line.Text.Length);
            var rects = RowRects(ft, start, stop, _fontSize);
            if (i < render.WordRects.Count) render.WordRects[i] = rects;

            var parts = i < render.WordSegments.Count ? render.WordSegments[i] : new List<WordSegment>();
            foreach (var r in rects)
            {
                var padded = new Rect(r.X, r.Y - margin, r.Width, r.Height + margin * 2);
                Geometry box = render.WarpedInk != null
                    ? render.Plane.Quad(padded, render.Baseline)
                    : new RectangleGeometry(padded);
                try
                {
                    var ink = Geometry.Combine(sourceInk, box, GeometryCombineMode.Intersect, null);
                    ink.Freeze();
                    parts.Add(EnsurePartBaked(new WordSegment { Box = r, Ink = ink }, dpi));
                }
                catch
                {
                    parts.Add(new WordSegment { Box = r, Ink = null });
                }
            }

            // Halos are needed for the tilted plane too — it used to build them only when the line was
            // flat, so with 3D 倾斜歌词 on the "歌词辉光" switch did nothing whatsoever.
            if (i < render.Halos.Length && rects.Count > 0)
            {
                var union = rects[0];
                for (int k = 1; k < rects.Count; k++) union.Union(rects[k]);
                if (union.Width > 0.3)
                {
                    // On the tilted plane the halo is cut from the warped ink with the same quad the
                    // karaoke edge uses, so the bloom follows the tilted glyphs instead of sitting in a
                    // flat rectangle behind them.
                    Geometry shape;
                    Rect bounds;
                    if (render.WarpedInk != null)
                    {
                        var pad = new Rect(union.X, union.Y - margin, union.Width, union.Height + margin * 2);
                        var quad = render.Plane.Quad(pad, render.Baseline);
                        if (quad != null && quad.Bounds.Width > 0.3)
                        {
                            bounds = quad.Bounds;
                            shape = Geometry.Combine(sourceInk, quad, GeometryCombineMode.Intersect, null);
                            try
                            {
                                shape.Freeze();
                                render.HaloShapes[i] = shape;
                                render.HaloBounds[i] = bounds;
                                render.Halos[i] = GlowHalo.Build(shape, bounds, _glowRadius, _glowColor);
                                render.HaloRadius[i] = _glowRadius;
                            }
                            catch { }
                        }
                    }
                    else
                    {
                        bounds = union;
                        var geo = new RectangleGeometry(union);
                        geo.Freeze();
                        shape = Geometry.Combine(sourceInk, geo, GeometryCombineMode.Intersect, null);
                        try
                        {
                            shape.Freeze();
                            render.HaloShapes[i] = shape;
                            render.HaloBounds[i] = bounds;
                            render.Halos[i] = GlowHalo.Build(shape, bounds, _glowRadius, _glowColor);
                            render.HaloRadius[i] = _glowRadius;
                        }
                        catch { }
                    }
                }
            }
        }
        render.WordBuilt = Math.Max(render.WordBuilt, end);
    }

    /// <summary>Character boxes of a range, merged per text row (words may wrap).</summary>
    private static List<Rect> RowRects(FormattedText formatted, int start, int end, double fontSize)
    {
        var rows = new List<Rect>();
        Rect? current = null;
        for (int i = start; i < end; i++)
        {
            var box = WordBox(formatted, i, 1, fontSize * 1.2);
            if (box.IsEmpty) continue;
            if (current is { } c && Math.Abs(box.Y - c.Y) <= fontSize * 0.45)
            {
                current = Rect.Union(c, box);
            }
            else
            {
                if (current is { } finished) rows.Add(finished);
                current = box;
            }
        }
        if (current is { } last) rows.Add(last);
        return rows;
    }

    /// <summary>Exact on-screen box of a character range inside the wrapped paragraph.</summary>
    private static Rect WordBox(FormattedText formatted, int start, int length, double fallbackHeight)
    {
        if (length <= 0) return Rect.Empty;
        try
        {
            var geo = formatted.BuildHighlightGeometry(new Point(0, 0), start, length);
            if (geo != null && !geo.IsEmpty())
            {
                var b = geo.Bounds;
                return new Rect(b.X, b.Y, Math.Max(b.Width, 0.1), Math.Max(b.Height, fallbackHeight * 0.6));
            }
        }
        catch { }
        return Rect.Empty;
    }

    private sealed class LineRender
    {
        public LyricLine Line = null!;
        public Geometry Ink = null!;
        public FormattedText? Formatted;
        public double Width;
        public double Height;
        public double ExtraHeight;
        public List<List<Rect>> WordRects = new();
        public List<List<WordSegment>> WordSegments = new();
        /// <summary>How many words of the cue already have karaoke ink. The segments are built a few
        /// words per frame (see BuildWordParts), so the list is pre-sized and its tail stays empty.</summary>
        public int WordBuilt;
        public GlowHalo?[] Halos = Array.Empty<GlowHalo?>();
        /// <summary>Shape and box each word halo was cut from, kept so a radius change can re-blur the
        /// masks only instead of rebuilding the line.</summary>
        public Geometry?[] HaloShapes = Array.Empty<Geometry?>();
        public Rect[] HaloBounds = Array.Empty<Rect>();
        /// <summary>Radius each halo mask was baked at (NaN = never built).</summary>
        public double[] HaloRadius = Array.Empty<double>();
        /// <summary>Whole-line halo for cues without per-word timing (plain LRC): there is nothing to
        /// hang a per-word halo on, so the entire line blooms instead.</summary>
        public GlowHalo? LineHalo;
        public double LineHaloRadius = double.NaN;
        public Geometry? Translation;
        public double TranslationHeight;
        public Geometry? WarpedInk;
        public LyricPlane Plane;
        public double Baseline;
        public double InkHeight;
        /// <summary>Per-line pre-rasterised images (see BakeLine). Both roles are baked when the line is
        /// built, so a cue change never has to rasterise anything.</summary>
        public BakedText? BakedPlain;
        public BakedText? BakedCurrent;
    }
}
