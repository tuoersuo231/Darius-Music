using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CurrentLyrics.Views;

public sealed class ScreenProbeWindow : Window
{
    public Color? Picked { get; private set; }

    private readonly BitmapSource? _shot;
    private readonly double _dpiScale;

    public ScreenProbeWindow(Window owner)
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        Cursor = Cursors.Cross;
        Owner = owner;
        WindowStartupLocation = WindowStartupLocation.Manual;

        var (x, y, w, h) = VirtualScreen();
        var dpi = VisualTreeHelper.GetDpi(owner);
        _dpiScale = dpi.DpiScaleX;
        Left = x / dpi.DpiScaleX;
        Top = y / dpi.DpiScaleY;
        Width = w / dpi.DpiScaleX;
        Height = h / dpi.DpiScaleY;

        try
        {
            using var bitmap = new System.Drawing.Bitmap(w, h);
            using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
                graphics.CopyFromScreen(x, y, 0, 0, new System.Drawing.Size(w, h));
            using var stream = new System.IO.MemoryStream();
            bitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
            stream.Position = 0;
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            _shot = image;
        }
        catch
        {
            // Some fullscreen surfaces refuse a GDI copy; the picker itself still works.
        }

        Content = new Image { Source = _shot, Stretch = Stretch.Fill };
        MouseLeftButtonUp += (_, e) =>
        {
            Picked = SampleAt(e.GetPosition(this));
            Close();
        };
        MouseRightButtonUp += (_, _) => Close();
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Close();
        };
    }

    private Color? SampleAt(Point point)
    {
        if (_shot == null) return null;
        int px = (int)Math.Round(point.X * _dpiScale);
        int py = (int)Math.Round(point.Y * _dpiScale);
        px = Math.Clamp(px, 0, _shot.PixelWidth - 1);
        py = Math.Clamp(py, 0, _shot.PixelHeight - 1);
        var buffer = new byte[4];
        _shot.CopyPixels(new Int32Rect(px, py, 1, 1), buffer, 4, 0);
        return Color.FromRgb(buffer[2], buffer[1], buffer[0]);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    private static (int X, int Y, int Width, int Height) VirtualScreen()
        => (GetSystemMetrics(76), GetSystemMetrics(77), GetSystemMetrics(78), GetSystemMetrics(79));
}
