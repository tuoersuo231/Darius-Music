using System.Windows;
using System.Windows.Media;

namespace CurrentLyrics.Controls;

/// <summary>
/// True perspective for the lyric plane: the column is hinged on its left edge and rotated around
/// Y, then projected with a pinhole camera, so glyphs and line pitch genuinely shrink towards the
/// far end. WPF's 3D engine cannot composite into a per-pixel transparent window, so the same
/// projection is applied to the vector geometry — the text stays sharp and the window stays fully
/// transparent.
/// </summary>
public static class PerspectiveWarp
{
    /// <summary>
    /// Largest far/near size ratio the plane may produce. A pinhole projection on a wide column
    /// (36°, vanishing point at 1.7 columns) shrinks the far end to 0.65× the near end, which reads
    /// as "the text got squashed" rather than "further away". Beyond this ratio the size range is
    /// compressed uniformly — the trapezoid and the baseline fan stay, the crushing does not.
    /// </summary>
    public static double MaxDepthRatio { get; set; } = 1.90;

    /// <summary>Camera distance that gives a clearly readable near/far ratio.</summary>
    /// <summary>
    /// Camera distance that places the vanishing point <paramref name="vanishingOffset"/> column
    /// widths to the left of the column. A Y-rotation puts it at x = C·cosθ/sinθ, so
    /// C = |x|·tan|θ|.
    /// </summary>
    public static double CameraFor(double width, double angleDeg, double vanishingOffset)
    {
        double tan = Math.Tan(Math.Abs(angleDeg) * Math.PI / 180);
        return Math.Max(width * Math.Max(vanishingOffset, 0.05) * tan, 40);
    }

    /// <summary>Where the rows meet, measured from the column's left edge (negative = left).</summary>
    public static double VanishingX(double width, double angleDeg, double vanishingOffset)
        => -width * Math.Max(vanishingOffset, 0.05);

    /// <summary>Scale that makes the projected column fill the original width again.</summary>
    public static double FitFor(double width, double angleDeg, double camera)
        => width / Math.Max(RawX(width, angleDeg, camera), 1);

    private static double Project(double x, double angleDeg, double camera)
    {
        double theta = angleDeg * Math.PI / 180;
        double depth = camera + x * Math.Sin(theta);
        if (depth < 1) depth = 1;
        return camera / depth;
    }

    private static double RawX(double x, double angleDeg, double camera)
        => x * Math.Cos(angleDeg * Math.PI / 180) * Project(x, angleDeg, camera);

    /// <summary>Glyph scale of a source column (depths of field), including the fit scale.</summary>
    public static double ScaleAt(double x, double angleDeg, double camera, double fit)
        => Project(x, angleDeg, camera) * fit;

    /// <summary>Average glyph scale across the column — the factor that keeps line pitch honest.</summary>
    public static double MeanScale(double width, double angleDeg, double camera, double fit)
        => (Project(0, angleDeg, camera) + Project(width, angleDeg, camera)) / 2 * fit;

    /// <summary>
    /// How much of the pinhole size range survives. 1 = keep the true projection; less than 1
    /// pulls the far end up towards the near end so the row stops looking crushed.
    /// </summary>
    /// <param name="fanReference">
    /// Column width at which the tilt looks "as designed". The vertical drop across a row is
    /// pitch·Δs, and the same drop over a shorter row reads steeper — so for a narrower column the
    /// drop is scaled by width/reference, which keeps the apparent angle constant. 0 = no such
    /// normalisation (true geometry). Above the reference the size cap in <see cref="MaxDepthRatio"/>
    /// decides, so a wide window keeps the size range it always had.
    /// </param>
    public static double StrengthFor(double width, double angleDeg, double camera, double fit,
                                     double fanReference = 0)
    {
        double a = ScaleAt(0, angleDeg, camera, fit);
        double b = ScaleAt(width, angleDeg, camera, fit);
        double near = Math.Max(a, b);
        double far = Math.Min(a, b);
        double target = Math.Max(1.01, MaxDepthRatio);
        if (near <= 1e-6 || far <= 1e-6) return 1.0;
        double spread = near - far;
        if (spread <= 1e-6) return 1.0;
        double allowed = Math.Min(spread, near * (1 - 1 / target));
        if (fanReference > 0 && width > 0 && width < fanReference)
            allowed *= width / fanReference;
        return Math.Clamp(allowed / spread, 0, 1);
    }

    /// <summary>Projected X of a source column, including the fit scale.</summary>
    public static double MapX(double x, double angleDeg, double camera, double fit)
        => RawX(x, angleDeg, camera) * fit;

    public static Point Map(double x, double v, double angleDeg, double camera, double fit)
    {
        double s = Project(x, angleDeg, camera) * fit;
        return new Point(x * Math.Cos(angleDeg * Math.PI / 180) * s, v * s);
    }

    public static Geometry? Warp(Geometry source, double angleDeg, double camera, double fit, double vOffset)
    {
        try
        {
            var flattened = source.GetFlattenedPathGeometry(0.12, ToleranceType.Absolute);
            var result = new PathGeometry { FillRule = flattened.FillRule };
            foreach (var figure in flattened.Figures)
            {
                var newFigure = new PathFigure
                {
                    StartPoint = Map(figure.StartPoint.X, figure.StartPoint.Y - vOffset, angleDeg, camera, fit),
                    IsClosed = figure.IsClosed,
                    IsFilled = figure.IsFilled,
                };
                AddSegments(newFigure, figure, (x, v) => Map(x, v - vOffset, angleDeg, camera, fit));
                result.Figures.Add(newFigure);
            }
            result.Freeze();
            return result;
        }
        catch
        {
            return null;
        }
    }

    internal static void AddSegments(PathFigure target, PathFigure source, Func<double, double, Point> map)
    {
        foreach (var segment in source.Segments)
        {
            if (segment is PolyLineSegment poly)
            {
                var points = new PointCollection(poly.Points.Count);
                foreach (var p in poly.Points) points.Add(map(p.X, p.Y));
                target.Segments.Add(new PolyLineSegment(points, true));
            }
            else if (segment is LineSegment line)
            {
                target.Segments.Add(new LineSegment(map(line.Point.X, line.Point.Y), true));
            }
        }
    }

    /// <summary>Projected quad of a source rectangle (a projective map keeps edges straight).</summary>
    public static Geometry Quad(Rect rect, double angleDeg, double camera, double fit, double vOffset)
    {
        var p0 = Map(rect.Left, rect.Top - vOffset, angleDeg, camera, fit);
        var p1 = Map(rect.Right, rect.Top - vOffset, angleDeg, camera, fit);
        var p2 = Map(rect.Right, rect.Bottom - vOffset, angleDeg, camera, fit);
        var p3 = Map(rect.Left, rect.Bottom - vOffset, angleDeg, camera, fit);
        var figure = new PathFigure { StartPoint = p0, IsClosed = true, IsFilled = true };
        figure.Segments.Add(new PolyLineSegment(new PointCollection { p1, p2, p3 }, true));
        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        geometry.Freeze();
        return geometry;
    }
}

/// <summary>
/// One lyric plane. The rows fan towards a vanishing point that the user places with
/// <c>交点位置</c> (in column widths), and the fan gain comes from <c>3D倾斜角度</c>.
/// Both axes use the same per-column scale, so glyphs are only ever resized — never squeezed —
/// while the row pitch shrinks by exactly the same factor as the ink, so rows cannot collide.
/// </summary>
public readonly struct LyricPlane
{
    public readonly double Angle;
    /// <summary>Vanishing distance in source pixels (how far the apex sits past the left edge).</summary>
    public readonly double Camera;
    public readonly double Fit;
    /// <summary>Scale at x = width, the near edge the fit pins to the column width.</summary>
    public readonly double Near;
    /// <summary>Fan gain actually applied (0 = flat, 1 = full tilt at this angle).</summary>
    public readonly double Strength;

    private readonly double _apex;
    private readonly double _gain;

    private LyricPlane(double angle, double apex, double gain, double fit, double near)
    {
        Angle = angle;
        Camera = apex;
        _apex = apex;
        _gain = gain;
        Fit = fit;
        Near = near;
        Strength = gain;
    }

    /// <summary>Tilt at which the fan is at full strength; a smaller angle scales the gain down.</summary>
    private const double FullTiltDegrees = 30.0;

    public static LyricPlane For(double width, double angleDeg, double vanishingOffset, double fanReference = 0)
    {
        double apex = Math.Max(width * Math.Max(vanishingOffset, 0.05), 40);

        double gain = Math.Abs(Math.Sin(angleDeg * Math.PI / 180)) / Math.Sin(FullTiltDegrees * Math.PI / 180);
        // A narrow column shows the same drop over fewer pixels, which reads as a steeper fan; scale
        // the gain by width/reference so the angle looks identical at any window size.
        if (fanReference > 0 && width > 0 && width < fanReference) gain *= width / fanReference;
        gain = Math.Clamp(gain, 0, 1);

        // Cap the size range: a fan wider than MaxDepthRatio makes the far end look pinched, so the
        // gain is reduced rather than the geometry being abandoned.
        double max = Math.Max(1.01, PerspectiveWarp.MaxDepthRatio);
        double rawEnd = 1 + gain * width / apex;
        if (rawEnd > max && rawEnd > 1.0001) gain *= (max - 1) / (rawEnd - 1);

        double end = 1 + gain * width / apex;
        double fit = width / Math.Max(width * (0.5 + 0.5 * end), 1);
        return new LyricPlane(angleDeg, apex, gain, fit, end * fit);
    }

    /// <summary>Glyph scale at source column <paramref name="x"/> (equal on both axes).</summary>
    public double Scale(double x) => (1 + _gain * x / _apex) * Fit;

    /// <summary>
    /// Source column → rendered x. The far end is pulled in slightly so the column reads as a plane
    /// instead of a rectangle, without the horizontal squeeze a cos(angle) factor would add.
    /// </summary>
    public double MapX(double x) => x * (1 + 0.5 * _gain * x / _apex) * Fit;

    public Point Map(double x, double v) => new(MapX(x), v * Scale(x));

    public Geometry? Warp(Geometry source, double vOffset)
    {
        try
        {
            // Lambdas cannot capture `this` of a struct, so hand the projection to a local copy.
            var plane = this;
            var flattened = source.GetFlattenedPathGeometry(0.12, ToleranceType.Absolute);
            var result = new PathGeometry { FillRule = flattened.FillRule };
            foreach (var figure in flattened.Figures)
            {
                var newFigure = new PathFigure
                {
                    StartPoint = Map(figure.StartPoint.X, figure.StartPoint.Y - vOffset),
                    IsClosed = figure.IsClosed,
                    IsFilled = figure.IsFilled,
                };
                PerspectiveWarp.AddSegments(newFigure, figure, (x, v) => plane.Map(x, v - vOffset));
                result.Figures.Add(newFigure);
            }
            result.Freeze();
            return result;
        }
        catch
        {
            return null;
        }
    }

    public Geometry Quad(Rect rect, double vOffset)
    {
        var p0 = Map(rect.Left, rect.Top - vOffset);
        var p1 = Map(rect.Right, rect.Top - vOffset);
        var p2 = Map(rect.Right, rect.Bottom - vOffset);
        var p3 = Map(rect.Left, rect.Bottom - vOffset);
        var figure = new PathFigure { StartPoint = p0, IsClosed = true, IsFilled = true };
        figure.Segments.Add(new PolyLineSegment(new PointCollection { p1, p2, p3 }, true));
        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        geometry.Freeze();
        return geometry;
    }
}
