using System.IO;
using CurrentLyrics.Services;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CurrentLyrics.Controls;

/// <summary>
/// Builds the frosted background behind the lyric column.
///
/// The image is downscaled, box-blurred and then stretched back to the window, which produces the
/// soft "ground glass" look much more cheaply than a real gaussian pass on a 1440p bitmap. Only the
/// backdrop is blurred — the lyric geometry is drawn afterwards, on top, so the text stays crisp.
/// </summary>
internal static class BackdropBlur
{
    private static BitmapSource? _cachedSource;
    private static int _cachedRadius;
    private static double _cachedFade, _cachedFadeH, _cachedFadeV, _cachedScrim = -1;
    private static string? _cachedKey;
    private static BitmapSource? _cached;

    /// <summary>Fills <paramref name="dest"/> uniformly with a blurred copy of the source.</summary>
    public static BitmapSource? Frost(BitmapSource? source, int radius,
        double fadeStrength = 0, double fadeHPercent = 0, double fadeVPercent = 0, double scrim = 0)
    {
        if (source == null) return null;
        bool fading = fadeStrength > 1 && (fadeHPercent > 0.2 || fadeVPercent > 0.2);
        if (ReferenceEquals(source, _cachedSource) && radius == _cachedRadius &&
            fadeStrength == _cachedFade && fadeHPercent == _cachedFadeH &&
            fadeVPercent == _cachedFadeV && scrim == _cachedScrim && _cached != null)
            return _cached;

        try
        {
            int smallWidth = Math.Max(24, Math.Min(256, radius > 0 ? 256 / Math.Max(1, radius / 6) : 256));
            var small = Resize(source, smallWidth);
            var blurred = radius <= 0 ? small : BoxBlur(small, Math.Clamp(radius / 6, 1, 6), 3);
            blurred = ApplyScrimAndFade(blurred, scrim, fadeStrength, fadeHPercent, fadeVPercent);
            _cachedSource = source;
            _cachedRadius = radius;
            _cachedFade = fadeStrength;
            _cachedFadeH = fadeHPercent;
            _cachedFadeV = fadeVPercent;
            _cachedScrim = scrim;
            _cached = blurred;
            return blurred;
        }
        catch
        {
            return source;
        }
    }

    /// <summary>Multiplies the bitmap's alpha towards the four edges so it dissolves into the desktop.</summary>
    private static BitmapSource ApplyScrimAndFade(BitmapSource source, double scrimAmount,
        double strengthPercent, double hPercent, double vPercent)
    {
        int width = source.PixelWidth, height = source.PixelHeight;
        int stride = width * 4;
        var pixels = new byte[stride * height];
        new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0).CopyPixels(pixels, stride, 0);

        double strength = Math.Clamp(strengthPercent, 0, 100) / 100.0;
        double bandX = width * Math.Clamp(hPercent, 0, 20) / 100.0;
        double bandY = height * Math.Clamp(vPercent, 0, 20) / 100.0;

        for (int y = 0; y < height; y++)
        {
            double fy = 1.0;
            if (bandY > 0.5)
            {
                double d = Math.Min(y, height - 1 - y);
                if (d < bandY) fy = 1.0 - strength * (1.0 - d / bandY);
            }
            for (int x = 0; x < width; x++)
            {
                double fx = 1.0;
                if (bandX > 0.5)
                {
                    double d = Math.Min(x, width - 1 - x);
                    if (d < bandX) fx = 1.0 - strength * (1.0 - d / bandX);
                }
                int i = y * stride + x * 4;
                if (scrimAmount > 0.001)
                {
                    double keep = 1.0 - Math.Clamp(scrimAmount, 0, 1);
                    pixels[i] = (byte)(pixels[i] * keep);
                    pixels[i + 1] = (byte)(pixels[i + 1] * keep);
                    pixels[i + 2] = (byte)(pixels[i + 2] * keep);
                }
                // NOTE: this path writes Bgra32 (straight alpha), so the colour must NOT be scaled
                // here — the compositor applies the alpha. (FrostToSize writes Pbgra32 and therefore
                // does premultiply; see the comment there.) Frost() itself is currently unused.
                pixels[i + 3] = (byte)(pixels[i + 3] * fx * fy);
            }
        }
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>
    /// Edge profile: smoothstep raised to a power, so the first third of the band stays almost fully
    /// transparent. A plain ramp already looks like an edge because the picture's own contrast shows
    /// up a few pixels in.
    /// </summary>
    internal static double Feather(double t)
    {
        double s = Smooth(t);
        return Math.Pow(s, 2.2);
    }

    /// <summary>Smoothstep: zero derivative at both ends, so the fade never shows a band edge.</summary>
    internal static double Smooth(double t)
    {
        t = Math.Clamp(t, 0, 1);
        return t * t * (3 - 2 * t);
    }

    private static BitmapSource Resize(BitmapSource source, int width)
    {
        double scale = Math.Min(1.0, (double)width / Math.Max(1, source.PixelWidth));
        var transform = new ScaleTransform(scale, scale);
        return new TransformedBitmap(source, transform);
    }

    private static BitmapSource BoxBlur(BitmapSource source, int radius, int passes)
    {
        int width = source.PixelWidth, height = source.PixelHeight;
        var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        int stride = width * 4;
        var pixels = new byte[stride * height];
        converted.CopyPixels(pixels, stride, 0);

        var temp = new byte[pixels.Length];
        for (int pass = 0; pass < passes; pass++)
        {
            BlurHorizontal(pixels, temp, width, height, radius);
            BlurVertical(temp, pixels, width, height, radius);
        }

        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        bitmap.Freeze();
        return bitmap;
    }

    private static void BlurHorizontal(byte[] src, byte[] dst, int width, int height, int radius)
    {
        int stride = width * 4;
        for (int y = 0; y < height; y++)
        {
            int row = y * stride;
            for (int x = 0; x < width; x++)
            {
                int r = 0, g = 0, b = 0, a = 0, n = 0;
                for (int k = -radius; k <= radius; k++)
                {
                    int xx = Math.Clamp(x + k, 0, width - 1);
                    int i = row + xx * 4;
                    b += src[i]; g += src[i + 1]; r += src[i + 2]; a += src[i + 3]; n++;
                }
                int o = row + x * 4;
                dst[o] = (byte)(b / n);
                dst[o + 1] = (byte)(g / n);
                dst[o + 2] = (byte)(r / n);
                dst[o + 3] = (byte)(a / n);
            }
        }
    }

    private static void BlurVertical(byte[] src, byte[] dst, int width, int height, int radius)
    {
        int stride = width * 4;
        for (int x = 0; x < width; x++)
        {
            for (int y = 0; y < height; y++)
            {
                int r = 0, g = 0, b = 0, a = 0, n = 0;
                for (int k = -radius; k <= radius; k++)
                {
                    int yy = Math.Clamp(y + k, 0, height - 1);
                    int i = yy * stride + x * 4;
                    b += src[i]; g += src[i + 1]; r += src[i + 2]; a += src[i + 3]; n++;
                }
                int o = y * stride + x * 4;
                dst[o] = (byte)(b / n);
                dst[o + 1] = (byte)(g / n);
                dst[o + 2] = (byte)(r / n);
                dst[o + 3] = (byte)(a / n);
            }
        }
    }

    /// <summary>
    /// Full pipeline at the final size: frost → upscale to the window rect → readability scrim and
    /// edge alpha applied per pixel at full resolution, so the gradient is smooth and genuinely
    /// transparent (baking the ramp into the small blur bitmap produced a coarse band).
    /// </summary>
    public static BitmapSource? FrostToSize(BitmapSource? source, int radius, int width, int height,
        double fadeStrengthPercent, double fadeHPercent, double fadeVPercent, double scrim,
        double opacity = 1.0)
    {
        if (source == null || width < 2 || height < 2) return null;
        bool fading = fadeStrengthPercent > 1 && (fadeHPercent > 0.2 || fadeVPercent > 0.2);
        var key = $"{radius}|{width}x{height}|{fadeStrengthPercent}|{fadeHPercent}|{fadeVPercent}|{scrim}|{opacity:0.000}";
        if (ReferenceEquals(source, _cachedSource) && key == _cachedKey && _cached != null) return _cached;

        try
        {
            // Detail matters as much as the blur. The base used to be capped at 256 px ("2600/radius"),
            // so a 600 px overlay was a 256 px image stretched 2.3x — it read as a low-resolution
            // picture no matter what the 模糊 slider said (user report: 背景图分辨率过低).
            // Now the base stays close to the output size and the box blur is scaled so the *on-screen*
            // softness still matches the radius: screen blur ~= radius^2 * width / 10400 (which is what
            // the old formula produced), converted into base-image pixels.
            // Detail matters as much as the blur. The base used to be capped at 256 px ("2600/radius"),
            // so a 600 px overlay was a 256 px image stretched 2.3x and read as a low-resolution
            // picture whatever the 模糊 slider said (user report: 背景图分辨率过低). The base now stays
            // close to the output size, the on-screen softness is what the slider asks for, and that
            // softness is half of what the old curve produced — at 模糊=10 the old pipeline smeared
            // ~6 px on screen, which is what made the artwork look like a thumbnail.
            double targetBlur = radius <= 0 ? 0 : Math.Clamp(radius * (double)radius * width / 20800.0, 0.3, width * 0.35);
            int baseWidth = (int)Math.Clamp(Math.Round(Math.Max(width * 0.9, 320.0)), 64, 1400);
            var small = Resize(source, baseWidth);
            if (targetBlur > 0.3 && small != null)
            {
                int smallRadius = (int)Math.Clamp(Math.Round(targetBlur * small.PixelWidth / (double)Math.Max(1, width)), 1, 24);
                small = BoxBlur(small, smallRadius, 3);
            }
            if (small == null) return null;

            // upscale once with high quality into the exact output size
            var visual = new System.Windows.Media.DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                System.Windows.Media.RenderOptions.SetBitmapScalingMode(visual,
                    System.Windows.Media.BitmapScalingMode.HighQuality);
                var fill = FillRect(small, width, height);
                dc.DrawImage(small, fill);
            }
            var sized = new System.Windows.Media.Imaging.RenderTargetBitmap(width, height, 96, 96,
                System.Windows.Media.PixelFormats.Pbgra32);
            sized.Render(visual);

            int stride = width * 4;
            var pixels = new byte[stride * height];
            sized.CopyPixels(pixels, stride, 0);

            double strength = Math.Clamp(fadeStrengthPercent, 0, 100) / 100.0;
            double keep = 1.0 - Math.Clamp(scrim, 0, 1);
            double bake = Math.Clamp(opacity, 0, 1);
            double bandX = fading ? width * Math.Clamp(fadeHPercent, 0, AppSettings.MaxFadeHPercent) / 100.0 : 0;
            double bandY = fading ? height * Math.Clamp(fadeVPercent, 0, AppSettings.MaxFadeVPercent) / 100.0 : 0;

            // Per-edge fade, then the two axes are *multiplied* to close the corners.
            //
            // min(ax, ay) leaves the corner square: the outer iso-alpha contours run straight into the
            // border and turn 90°, which reads as a small unfeathered remnant in each corner (the
            // measured field had alpha>0.05 pinned at x=17 for every row from y=20 down to y=0).
            // Multiplying makes the iso-contours hyperbolic, so each corner dissolves into a rounded
            // vignette and, along the diagonal, reaches full transparency sooner than on the edges.
            for (int y = 0; y < height; y++)
            {
                double ay = 1.0;
                if (bandY > 0.5)
                {
                    double dy = Math.Min(y, height - 1 - y);
                    if (dy < bandY) ay = 1.0 - strength * Feather(1.0 - dy / bandY);
                }
                int row = y * stride;
                for (int x = 0; x < width; x++)
                {
                    double ax = 1.0;
                    if (bandX > 0.5)
                    {
                        double dx = Math.Min(x, width - 1 - x);
                        if (dx < bandX) ax = 1.0 - strength * Feather(1.0 - dx / bandX);
                    }

                    double alpha = ax * ay;
                    int i = row + x * 4;
                    // Pbgra32 is *premultiplied*: scaling the alpha without scaling the colour makes
                    // the layer add full-strength colour where it should be transparent, which is the
                    // hard edge the fade is supposed to remove. Multiply all four channels.
                    // `bake` folds the backdrop opacity in here so the per-frame composition is a
                    // plain blit instead of an opacity group (13 ms -> ~0 ms per frame).
                    double k = Math.Clamp(alpha, 0, 1) * bake;
                    pixels[i] = (byte)(pixels[i] * keep * k);
                    pixels[i + 1] = (byte)(pixels[i + 1] * keep * k);
                    pixels[i + 2] = (byte)(pixels[i + 2] * keep * k);
                    pixels[i + 3] = (byte)(pixels[i + 3] * k);
                }
            }

            var result = BitmapSource.Create(width, height, 96, 96,
                System.Windows.Media.PixelFormats.Pbgra32, null, pixels, stride);
            result.Freeze();

            if (Environment.GetEnvironmentVariable("LYRIC_DUMP_BG") == "1")
            {
                try
                {
                    var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                    encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(result));
                    using var stream = File.Create(Path.Combine(AppPaths.DataDir, "bg-dump.png"));
                    encoder.Save(stream);
                    File.AppendAllText(Path.Combine(AppPaths.DataDir, "bg-dump.txt"),
                        $"{width}x{height} fadeH={fadeHPercent} fadeV={fadeVPercent} strength={fadeStrengthPercent}\n");
                }
                catch { }
            }
            _cachedSource = source;
            _cachedKey = key;
            _cached = result;
            return result;
        }
        catch { return null; }
    }

    /// <summary>Scales a bitmap to fill the target rect (UniformToFill), cropping the overflow.</summary>
    public static Rect FillRect(BitmapSource image, double width, double height)
    {
        double imageAspect = (double)image.PixelWidth / Math.Max(1, image.PixelHeight);
        double targetAspect = width / Math.Max(1, height);
        double sw = width, sh = height;
        if (imageAspect > targetAspect) sw = height * imageAspect;   // wider → crop sides
        else sh = width / Math.Max(0.001, imageAspect);
        return new Rect((width - sw) / 2, (height - sh) / 2, sw, sh);
    }
}
