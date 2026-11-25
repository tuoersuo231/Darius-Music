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
}

public sealed class LyricStackView : FrameworkElement
{

    private const double ScrollDurationMs = 620;
    private const double MorphDurationMs = 420;
    private const double LineHeightRatio = 1.45;

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
    public double DebugMorph => MorphProgress;
    public bool DebugMorphing => _morphing;

    /// <summary>True while a scroll or morph is still running, so the host knows to keep ticking.</summary>
    public bool HasActiveAnimation => _scrollDelta != 0 || _morphing;
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
        _tilt = Math.Clamp(settings.TiltAngle, -60, 0);
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
        bool geometryChanged =
            fontChanged ||
            _perspective != oldPerspective ||
            Math.Abs(_fontSize - oldFont) > 0.01 ||
            Math.Abs(_columnWidth - oldColumn) > 0.01 ||
            Math.Abs(_tilt - oldTilt) > 0.01 ||
            Math.Abs(_vpOffset - oldVp) > 0.001 ||
            Math.Abs(_glowRadius - oldGlowRadius) > 0.01 ||
            _translation != oldTranslation ||
            (_glowEnabled && _glowStrength > 0.001) != oldGlow ||
            (glowColorChanged && DebugGlowColorRebuild);

        if (geometryChanged) InvalidateCache();
        else if (glowColorChanged) RecolorHalos();
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

        int index = _doc.LineIndexAt(positionMs, out _);
        if (index < 0) index = 0;

        AdvanceAnimations();

        if (index != _current)
        {
            // Where the incoming cue sits in the old layout decides the scroll distance.
            double previousOffset = _current >= 0 ? OffsetOf(index, _current) : 0;
            _previous = _current < 0 ? index : _current;
            _current = index;
            _scrollFrom = previousOffset;
            _scrollDelta = previousOffset;
            _scrollStart = Now;
            _morphStart = Now;
            _morphing = _previous != _current;
            LineChanged?.Invoke();
        }

        InvalidateVisual();
    }

    /// <summary>Forces animated values to rest (used by the PNG previews).</summary>
    public void Snap()
    {
        _scrollDelta = 0;
        _morphing = false;
        InvalidateVisual();
    }

    private static double Now => Environment.TickCount64;

    private void AdvanceAnimations()
    {
        if (_scrollDelta != 0)
        {
            double t = Math.Clamp((Now - _scrollStart) / ScrollDurationMs, 0, 1);
            // Smoothstep, not ease-out: a cubic ease-out covers ~29% of the distance in the first
            // 45 ms, which reads as a jump followed by a slide. Both ends of this one are gentle.
            _scrollDelta = t >= 1 ? 0 : _scrollFrom * (1 - BackdropBlur.Smooth(t));
        }
        if (_morphing && Now - _morphStart >= MorphDurationMs) _morphing = false;
    }

    private static double EaseOut(double t) => 1 - Math.Pow(1 - t, 3);

    private double MorphProgress => !_morphing || _previous < 0 || _previous == _current
        ? 1
        : EaseOut(Math.Clamp((Now - _morphStart) / MorphDurationMs, 0, 1));

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

        // Warm the cache for the visible window before laying anything out. While a settings change is
        // still being rebuilt, only a few lines are built per frame and the previous geometry is drawn
        // in the meantime: rebuilding a dozen warped, glowing lines at once cost 50-130 ms per frame,
        // which is what made dragging the settings sliders stutter.
        int half = Math.Max(1, _visibleLines / 2);
        int warmFrom = Math.Max(0, _current - half - 1);
        int warmTo = Math.Min(_doc.Lines.Count - 1, _current + half + 1);
        if (_rebuilding)
        {
            int budget = RebuildLinesPerFrame;
            bool complete = true;
            for (int i = warmFrom; i <= warmTo; i++)
            {
                if (_cache.ContainsKey(i)) continue;
                if (budget-- <= 0) { complete = false; break; }
                BuildLine(i);
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
            }
        }
        else
        {
            for (int i = warmFrom; i <= warmTo; i++) BuildLine(i);
        }

        DrawFrostedBackdrop(dc, width, height);

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
        _perspectiveShift = _perspective ? ComputePerspectiveShift(width) : 0;

        for (int i = Math.Max(0, _current - half); i <= Math.Min(_doc.Lines.Count - 1, _current + half); i++)
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
            double lineHeight = render.Height * scale;
            if (Math.Abs(offset) > height) continue;
            if (alpha <= 0.012) continue;

            double y = anchorY + offset - lineHeight / 2;
            // Soft edge for the text itself. It used to ride on EdgeFadeFactor, which returns 1.0 for a
            // transparent backdrop — so with 背景=透明 a long wrapped line was simply cut off by the
            // window edge (user report: 上下文较长时出现明显的断层). The band now follows the font size,
            // and the factor is taken from the whole box instead of its centre, so tall wrapped lines
            // fade out as they leave instead of ending in a step.
            alpha *= TextEdgeFade(y, lineHeight, height);
            double x = (width - _columnWidth) / 2 + _perspectiveShift;
            if (_perspective)
            {
                // Clean projective placement: the row's baseline is the projection of a 3D
                // horizontal line, so every baseline passes through the vanishing point and the
                // reading row (d = 0) comes out exactly horizontal.
                double sLeft = _plane.Scale(0);
                double sRight = _plane.Scale(_columnWidth);
                double d = layoutOffset + scroll;
                double y0 = anchorY + d * sLeft;
                double y1 = anchorY + d * sRight;
                double x0 = x;
                double x1 = x + _plane.MapX(_columnWidth);
                double shear = Math.Abs(x1 - x0) > 1 ? (y1 - y0) / (x1 - x0) : 0;

                dc.PushTransform(new TranslateTransform(x0, y0));
                dc.PushTransform(new MatrixTransform(1, shear, 0, 1, 0, 0));
                if (Math.Abs(scale - 1) > 0.001)
                    dc.PushTransform(new ScaleTransform(scale, scale, _scaleCenterX, 0));
                if (i == _current) DrawCurrentLine(dc, render, alpha);
                else DrawPlainLine(dc, render, alpha, Math.Abs(i - _current));
                if (Math.Abs(scale - 1) > 0.001) dc.Pop();
                dc.Pop();
                dc.Pop();
            }

            // Flat fallback only: the perspective branch above already drew this row.
            if (_perspective) continue;

            dc.PushTransform(new TranslateTransform(x, y));
            if (Math.Abs(scale - 1) > 0.001) dc.PushTransform(new ScaleTransform(scale, scale, _scaleCenterX, 0));

            if (i == _current) DrawCurrentLine(dc, render, alpha);
            else DrawPlainLine(dc, render, alpha, Math.Abs(i - _current));

            if (Math.Abs(scale - 1) > 0.001) dc.Pop();
            dc.Pop();
        }

        // The rebuild is spread over frames, so keep asking for the next one: without this the column
        // would sit on the previous geometry until something else invalidated it.
        if (_rebuilding)
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
    /// </summary>
    private double TextEdgeFade(double top, double lineHeight, double height)
    {
        if (height <= 1) return 1;
        double band = Math.Max(_fontSize * 0.9, height * 0.06);
        if (band <= 1) return 1;
        double bottom = top + Math.Max(1, lineHeight);
        double entering = Math.Clamp(bottom / band, 0, 1);          // 0 while the box is still above
        double leaving = Math.Clamp((height - top) / band, 0, 1);   // 0 once the box left below
        double factor = Math.Min(entering, leaving);
        return 0.10 + 0.90 * BackdropBlur.Smooth(Math.Clamp(factor, 0, 1));
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
        double from = index == _previous ? 1 : _neighbourScale;
        return from + (target - from) * progress;
    }
    private double AlphaFor(int index, double progress)
    {
        static double ByDistance(int distance) => distance == 0
            ? 1.0
            : Math.Clamp(0.52 * Math.Pow(0.66, distance - 1), 0.08, 0.52);

        double target = ByDistance(Math.Abs(index - _current));
        if (!_morphing || _previous < 0 || _previous == _current) return target;
        double from = ByDistance(Math.Abs(index - _previous));
        return from + (target - from) * progress;
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

    private void DrawPlainLine(DrawingContext dc, LineRender render, double alpha, int distance = 99)
    {
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
        byte mainAlpha = (byte)Math.Clamp(255 * Math.Min(1, alpha), 0, 255);
        // A jump (seek) can make a line current before its karaoke ink was built; do it once here.
        if (render.WordSegments.Count == 0 && render.Line.Words.Count > 0) BuildWordPartsFor(render);

        // Soft drop shadow so the lyric stays legible over a bright wallpaper.
        byte shadowAlpha = (byte)(0x62 * Math.Min(1, alpha));
        var shadow = new SolidColorBrush(Color.FromArgb(shadowAlpha, 0, 0, 0));
        shadow.Freeze();
        var inkGeometry = render.WarpedInk ?? render.Ink;
        dc.PushTransform(new TranslateTransform(0, 2.5));
        dc.DrawGeometry(shadow, null, inkGeometry);
        dc.Pop();

        if (_glowEnabled && _glowStrength > 0.001)
        {
            // Word-timed cues glow per word; plain LRC files carry no word timing at all, so those
            // lines used to render with no halo whatsoever — the glow switch looked dead on every
            // NetEase-sourced lyric. Bloom the whole line instead.
            if (render.Line.Words.Count == 0) DrawLineGlow(dc, render, alpha);
            else if (render.Halos.Length > 0) DrawGlow(dc, render);
        }

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
            var brush = new SolidColorBrush(Color.FromArgb((byte)(0xB4 * alpha / 1.0), 0xFF, 0xFF, 0xFF));
            brush.Freeze();
            var transInk = _perspective && render.Plane.Camera > 0
                ? render.Plane.Warp(trans, render.Baseline) ?? trans
                : trans;
            dc.DrawGeometry(brush, null, transInk);
        }
    }

    /// <summary>Bloom for a cue that has no per-word timing: one halo behind the whole line.</summary>
    private void DrawLineGlow(DrawingContext dc, LineRender render, double lineAlpha)
    {
        render.LineHalo ??= BuildLineHalo(render);
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
        tf = new Typeface(FontResolver.Resolve(_fontName), FontStyles.Normal,
            medium ? FontWeights.Medium : FontWeights.Normal, FontStretches.Normal);
        _typefaces[medium] = tf;
        return tf;
    }

    private LineRender? GetLine(int index)
    {
        if (_doc == null || index < 0 || index >= _doc.Lines.Count) return null;
        if (_cache.TryGetValue(index, out var cached)) return cached;
        // Mid-rebuild: keep drawing the previous geometry until the whole visible window has been
        // rebuilt, so a settings change never shows half the column at the old size and half at the new.
        if (_rebuilding && _stale.TryGetValue(index, out var stale)) return stale;
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
        // Merge, do not replace: a drag invalidates again before the previous rebuild finished, and
        // dropping the older placeholders here would force a full rebuild on the next frame.
        foreach (var kv in _cache) _stale[kv.Key] = kv.Value;
        _cache.Clear();
        _rebuilding = _stale.Count > 0;
    }

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
        if (!_rebuilding && line.Words.Count > 0 && Math.Abs(index - _current) <= 1) BuildWordParts(render, ft);

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
        return render;
    }

    /// <summary>Builds the per-word karaoke ink and the glow halos for one line. Only ever needed for
    /// the cue being sung, so it is done on demand instead of for every cached line.</summary>
    private void BuildWordPartsFor(LineRender render)
    {
        if (render.Formatted == null) return;
        BuildWordParts(render, render.Formatted);
    }

    private void BuildWordParts(LineRender render, FormattedText ft)
    {
        if (render.WordSegments.Count > 0) return;
        var line = render.Line;
        var words = line.Words;

        // A word can straddle a wrap; keep one rect per text row so the karaoke edge never
        // paints a tall band across both rows.
        var boxes = new List<List<Rect>>(words.Count);
        for (int i = 0; i < words.Count; i++)
        {
            var w = words[i];
            int start = Math.Clamp(w.StartOffset, 0, line.Text.Length);
            int end = Math.Clamp(w.EndOffset, start, line.Text.Length);
            boxes.Add(RowRects(ft, start, end, _fontSize));
        }
        render.WordRects = boxes;

        // Keep only the ink that belongs to each word/row, so the karaoke cut can never paint a
        // neighbouring glyph.
        double margin = _fontSize * 0.30;
        var segments = new List<List<WordSegment>>(boxes.Count);
        var sourceInk = render.WarpedInk ?? render.Ink;
        foreach (var rects in boxes)
        {
            var parts = new List<WordSegment>(rects.Count);
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
                    parts.Add(new WordSegment { Box = r, Ink = ink });
                }
                catch
                {
                    parts.Add(new WordSegment { Box = r, Ink = null });
                }
            }
            segments.Add(parts);
        }
        render.WordSegments = segments;

        // Halos are needed for the tilted plane too — it used to build them only when the line was
        // flat, so with 3D 倾斜歌词 on the "歌词辉光" switch did nothing whatsoever.
        if (_glowEnabled && _glowStrength > 0.001)
        {
            render.Halos = new GlowHalo?[words.Count];
            for (int i = 0; i < boxes.Count; i++)
            {
                var rects = boxes[i];
                if (rects.Count == 0) continue;
                var union = rects[0];
                for (int k = 1; k < rects.Count; k++) union.Union(rects[k]);
                if (union.Width <= 0.3) continue;

                // On the tilted plane the halo is cut from the warped ink with the same quad the
                // karaoke edge uses, so the bloom follows the tilted glyphs instead of sitting in a
                // flat rectangle behind them.
                Geometry shape;
                Rect bounds;
                if (render.WarpedInk != null)
                {
                    var pad = new Rect(union.X, union.Y - margin, union.Width, union.Height + margin * 2);
                    var quad = render.Plane.Quad(pad, render.Baseline);
                    if (quad == null || quad.Bounds.Width <= 0.3) continue;
                    bounds = quad.Bounds;
                    shape = Geometry.Combine(sourceInk, quad, GeometryCombineMode.Intersect, null);
                }
                else
                {
                    bounds = union;
                    var geo = new RectangleGeometry(union);
                    geo.Freeze();
                    shape = Geometry.Combine(sourceInk, geo, GeometryCombineMode.Intersect, null);
                }
                try
                {
                    shape.Freeze();
                    render.Halos[i] = GlowHalo.Build(shape, bounds, _glowRadius, _glowColor);
                }
                catch { }
            }
        }
        else
        {
            render.Halos = Array.Empty<GlowHalo?>();
        }
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
        public GlowHalo?[] Halos = Array.Empty<GlowHalo?>();
        /// <summary>Whole-line halo for cues without per-word timing (plain LRC): there is nothing to
        /// hang a per-word halo on, so the entire line blooms instead.</summary>
        public GlowHalo? LineHalo;
        public Geometry? Translation;
        public double TranslationHeight;
        public Geometry? WarpedInk;
        public LyricPlane Plane;
        public double Baseline;
        public double InkHeight;
    }
}
