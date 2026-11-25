using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CurrentLyrics.Controls;

/// <summary>
/// The desktop-lyrics backdrop, drawn as one plain element: the frosted picture plus four edge-fade
/// bands whose gradients are defined in <b>absolute pixels</b> of this element.
///
/// Brushes with relative viewbox/viewport were silently mis-mapped inside the layered overlay window
/// (the ramp collapsed to a few pixels, which is what looked like a hard boundary), so the fade is
/// drawn here with explicit coordinates instead.
/// </summary>
public sealed class BackdropSurface : FrameworkElement
{
    private BitmapSource? _picture;
    private Color _color = Color.FromRgb(0x0E, 0x11, 0x18);
    private bool _solid;
    private int _blur = 24;
    private bool _fadeEnabled;
    private double _fadeStrength = 65;
    private double _fadeH = 20;
    private double _fadeV = 20;
    private double _opacity = 0.9;

    public BackdropSurface()
    {
        IsHitTestVisible = false;
    }

    public double SurfaceOpacity
    {
        get => _opacity;
        set { _opacity = value; InvalidateVisual(); }
    }

    public void SetSolid(Color color, double opacity)
    {
        _solid = true;
        _color = color;
        _opacity = opacity;
        InvalidateVisual();
    }

    public void SetPicture(BitmapSource? picture, int blur, double opacity,
        bool fadeEnabled, double fadeStrength, double fadeH, double fadeV)
    {
        bool changed = !ReferenceEquals(picture, _picture) || blur != _blur || fadeEnabled != _fadeEnabled ||
                       Math.Abs(fadeStrength - _fadeStrength) > 0.01 || Math.Abs(fadeH - _fadeH) > 0.01 ||
                       Math.Abs(fadeV - _fadeV) > 0.01;
        _solid = picture == null;
        _picture = picture;
        _blur = blur;
        _opacity = opacity;
        _fadeEnabled = fadeEnabled;
        _fadeStrength = fadeStrength;
        _fadeH = fadeH;
        _fadeV = fadeV;
        if (changed) InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = RenderSize.Width, h = RenderSize.Height;
        if (w < 2 || h < 2) return;

        dc.PushOpacity(Math.Clamp(_opacity, 0.05, 1));

        if (_solid || _picture == null)
        {
            var brush = new SolidColorBrush(_color);
            brush.Freeze();
            dc.DrawRectangle(brush, null, new Rect(0, 0, w, h));
            dc.Pop();
            return;
        }

        var frosted = BackdropBlur.FrostToSize(_picture, _blur, (int)Math.Round(w), (int)Math.Round(h), 0, 0, 0, 0.42);
        if (frosted == null)
        {
            dc.Pop();
            return;
        }
        dc.DrawImage(frosted, new Rect(0, 0, w, h));

        if (!_fadeEnabled || _fadeStrength <= 1) { dc.Pop(); return; }

        // Edge fade in absolute pixels: each band runs from fully transparent at the outer edge to
        // fully opaque where it meets the picture. Drawn *over* the picture, so the alpha ramps are
        // plain rectangles and cannot be re-mapped by a brush-space mistake.
        double strength = Math.Clamp(_fadeStrength, 0, 100) / 100.0;
        double bandX = w * Math.Clamp(_fadeH, 0, 20) / 100.0;
        double bandY = h * Math.Clamp(_fadeV, 0, 20) / 100.0;

        if (bandX > 0.5)
        {
            DrawFade(dc, new Rect(0, 0, bandX, h), new Point(0, 0), new Point(bandX, 0), strength, clearEdge: true);
            DrawFade(dc, new Rect(w - bandX, 0, bandX, h), new Point(w, 0), new Point(w - bandX, 0), strength, clearEdge: true);
        }
        if (bandY > 0.5)
        {
            DrawFade(dc, new Rect(0, 0, w, bandY), new Point(0, 0), new Point(0, bandY), strength, clearEdge: true);
            DrawFade(dc, new Rect(0, h - bandY, w, bandY), new Point(0, h), new Point(0, h - bandY), strength, clearEdge: true);
        }

        dc.Pop();
    }

    /// <summary>
    /// Paints one edge band. <paramref name="outer"/> is the fully transparent end, <paramref name="inner"/>
    /// the fully opaque one; smoothstep keeps the slope zero at the inner end so no band edge shows.
    /// </summary>
    private static void DrawFade(DrawingContext dc, Rect rect, Point outer, Point inner,
        double strength, bool clearEdge)
    {
        var clear = Color.FromArgb(0, 0, 0, 0);
        var solid = Color.FromArgb((byte)(255 * strength), 0, 0, 0);
        var brush = new LinearGradientBrush(new GradientStopCollection
        {
            new GradientStop(clear, 0.0),
            new GradientStop(Color.FromArgb((byte)(255 * strength * 0.55), 0, 0, 0), 0.55),
            new GradientStop(Color.FromArgb((byte)(255 * strength * 0.92), 0, 0, 0), 0.86),
            new GradientStop(solid, 1.0),
        }, outer, inner)
        {
            MappingMode = BrushMappingMode.Absolute,
            SpreadMethod = GradientSpreadMethod.Pad,
        };
        brush.Freeze();
        dc.DrawRectangle(brush, null, rect);
    }
}
