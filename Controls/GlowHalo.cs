using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CurrentLyrics.Controls;

/// <summary>
/// Cached OUTER halo for one lyric word — the Windows equivalent of the app's
/// LyricsTextRaster.glyphHalo() (BlurMaskFilter.Blur.OUTER): the glyph is rasterised once,
/// its alpha is blurred, and the sharp glyph alpha is subtracted so the bloom only lives
/// outside the ink and can never wash out the unsung interior.
/// </summary>
internal sealed class GlowHalo
{
    /// <summary>Alpha only, row-major. Kept so a colour change is a tint, not a re-rasterise:
    /// rebuilding thirteen word halos (RenderTargetBitmap + three blur passes each) took long
    /// enough that dragging the colour visibly froze the lyric column.</summary>
    private readonly byte[] _mask;
    private readonly int _pixelWidth;
    private readonly int _pixelHeight;

    public BitmapSource Image { get; }
    public double X { get; }
    public double Y { get; }

    private GlowHalo(BitmapSource image, byte[] mask, int pixelWidth, int pixelHeight, double x, double y)
    {
        Image = image;
        _mask = mask;
        _pixelWidth = pixelWidth;
        _pixelHeight = pixelHeight;
        X = x;
        Y = y;
    }

    /// <summary>Same bloom, same place, new light colour — a byte loop instead of a raster pass.</summary>
    public GlowHalo Recolor(Color color)
    {
        var bitmap = Tint(_mask, _pixelWidth, _pixelHeight, color);
        return new GlowHalo(bitmap, _mask, _pixelWidth, _pixelHeight, X, Y);
    }

    public static GlowHalo? Build(Geometry geometry, Rect box, double radius, Color color)
    {
        int margin = (int)Math.Ceiling(Math.Max(radius * 3, 6));
        int width = (int)Math.Ceiling(box.Width) + margin * 2;
        int height = (int)Math.Ceiling(box.Height) + margin * 2;
        if (width <= 0 || height <= 0 || width > 4000 || height > 4000) return null;

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var brush = new SolidColorBrush(Colors.White);
            brush.Freeze();
            dc.PushTransform(new TranslateTransform(-box.X + margin, -box.Y + margin));
            dc.DrawGeometry(brush, null, geometry);
            dc.Pop();
        }

        var rtb = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);

        int stride = width * 4;
        var pixels = new byte[stride * height];
        rtb.CopyPixels(pixels, stride, 0);

        // alpha only
        var alpha = new float[width * height];
        var source = new float[width * height];
        for (int i = 0; i < alpha.Length; i++)
        {
            alpha[i] = pixels[i * 4 + 3] / 255f;
            source[i] = alpha[i];
        }

        int r = Math.Max(1, (int)Math.Round(radius * 0.55));
        for (int pass = 0; pass < 3; pass++) BoxBlur(alpha, width, height, r);

        // OUTER: keep only what the blur added outside the glyph, then regain the density a
        // pure gaussian loses when it is spread over a wide kernel.
        var mask = new byte[alpha.Length];
        for (int i = 0; i < alpha.Length; i++)
        {
            alpha[i] = Math.Clamp((alpha[i] - source[i]) * 2.4f, 0f, 1f);
            mask[i] = (byte)Math.Clamp((int)Math.Round(alpha[i] * 255f), 0, 255);
        }

        var bitmap = Tint(mask, width, height, color);
        return new GlowHalo(bitmap, mask, width, height, box.X - margin, box.Y - margin);
    }

    /// <summary>Premultiplied Pbgra32 bitmap from an alpha mask (RGB must already carry the alpha).</summary>
    private static BitmapSource Tint(byte[] mask, int width, int height, Color color)
    {
        int stride = width * 4;
        var tinted = new byte[stride * height];
        for (int i = 0; i < mask.Length; i++)
        {
            int a = mask[i];
            if (a == 0) continue;
            int o = i * 4;
            tinted[o + 0] = (byte)(color.B * a / 255);
            tinted[o + 1] = (byte)(color.G * a / 255);
            tinted[o + 2] = (byte)(color.R * a / 255);
            tinted[o + 3] = (byte)a;
        }
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Pbgra32, null, tinted, stride);
        bitmap.Freeze();
        return bitmap;
    }

    private static void BoxBlur(float[] data, int width, int height, int radius)
    {
        var temp = new float[data.Length];
        // horizontal
        for (int y = 0; y < height; y++)
        {
            int row = y * width;
            float sum = 0;
            int count = 0;
            for (int x = -radius; x <= radius; x++)
            {
                if (x >= 0 && x < width) { sum += data[row + x]; count++; }
            }
            for (int x = 0; x < width; x++)
            {
                temp[row + x] = sum / count;
                int add = x + radius + 1;
                int remove = x - radius;
                if (add < width) { sum += data[row + add]; count++; }
                if (remove >= 0) { sum -= data[row + remove]; count--; }
            }
        }
        // vertical
        for (int x = 0; x < width; x++)
        {
            float sum = 0;
            int count = 0;
            for (int y = -radius; y <= radius; y++)
            {
                if (y >= 0 && y < height) { sum += temp[y * width + x]; count++; }
            }
            for (int y = 0; y < height; y++)
            {
                data[y * width + x] = sum / count;
                int add = y + radius + 1;
                int remove = y - radius;
                if (add < height) { sum += temp[add * width + x]; count++; }
                if (remove >= 0) { sum -= temp[remove * width + x]; count--; }
            }
        }
    }
}
