using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using CurrentLyrics.Views;

namespace CurrentLyrics.Controls;

/// <summary>
/// Inline colour picker (saturation/value square, hue strip, hex field, screen eyedropper). It is a
/// panel, not a window: colour choices belong inside the window that owns them.
/// </summary>
public sealed class ColorPickerPanel : Grid
{
    /// <summary>Fires on every change so the host can preview the colour live.</summary>
    public event Action<string>? ColorChanged;
    public event Action<string>? Committed;
    public event Action? Cancelled;

    private readonly Border _svArea;
    private readonly Rectangle _svOverlay;
    private readonly Ellipse _svMarker;
    private readonly Border _hueBar;
    private readonly Rectangle _hueMarker;
    private readonly Border _preview;
    private readonly TextBox _hex;
    private readonly TextBlock _rgb;

    private double _hue;
    private double _sat = 0.75;
    private double _value = 1.0;
    private bool _syncing;

    public string Hex => $"{Current.R:X2}{Current.G:X2}{Current.B:X2}";
    private Color Current => HsvToColor(_hue, _sat, _value);

    public ColorPickerPanel(string initialHex, bool showActions = true)
    {
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        _svMarker = new Ellipse
        {
            Width = 12,
            Height = 12,
            Stroke = Brushes.White,
            StrokeThickness = 2,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            IsHitTestVisible = false,
        };
        _svOverlay = new Rectangle();
        _svArea = new Border
        {
            Height = 150,
            CornerRadius = new CornerRadius(9),
            ClipToBounds = true,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x26, 0x2F, 0x3E)),
            BorderThickness = new Thickness(1),
            Background = Brushes.White,
            Cursor = Cursors.Cross,
            Child = new Grid { Children = { _svOverlay, _svMarker } },
        };
        _svArea.MouseLeftButtonDown += OnSvMouse;
        _svArea.MouseMove += OnSvMouse;
        Children.Add(_svArea);

        _hueMarker = new Rectangle
        {
            Width = 4,
            HorizontalAlignment = HorizontalAlignment.Left,
            Fill = Brushes.White,
            IsHitTestVisible = false,
        };
        var hueFill = new Rectangle
        {
            Fill = new LinearGradientBrush(new GradientStopCollection
            {
                new GradientStop(Color.FromRgb(0xFF, 0, 0), 0),
                new GradientStop(Color.FromRgb(0xFF, 0xFF, 0), 0.17),
                new GradientStop(Color.FromRgb(0, 0xFF, 0), 0.33),
                new GradientStop(Color.FromRgb(0, 0xFF, 0xFF), 0.5),
                new GradientStop(Color.FromRgb(0, 0, 0xFF), 0.67),
                new GradientStop(Color.FromRgb(0xFF, 0, 0xFF), 0.83),
                new GradientStop(Color.FromRgb(0xFF, 0, 0), 1),
            }, new Point(0, 0), new Point(1, 0)),
        };
        _hueBar = new Border
        {
            Height = 14,
            Margin = new Thickness(0, 10, 0, 0),
            CornerRadius = new CornerRadius(7),
            ClipToBounds = true,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x26, 0x2F, 0x3E)),
            BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand,
            Child = new Grid { Children = { hueFill, _hueMarker } },
        };
        _hueBar.MouseLeftButtonDown += OnHueMouse;
        _hueBar.MouseMove += OnHueMouse;
        SetRow(_hueBar, 1);
        Children.Add(_hueBar);

        _preview = new Border
        {
            Width = 54,
            Height = 30,
            CornerRadius = new CornerRadius(8),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x26, 0x2F, 0x3E)),
            BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 12, 8, 0),
        };
        _hex = new TextBox
        {
            Height = 30,
            Width = 96,
            Margin = new Thickness(0, 12, 8, 0),
            FontSize = 12.5,
            VerticalContentAlignment = VerticalAlignment.Center,
            Padding = new Thickness(8, 0, 8, 0),
            Background = new SolidColorBrush(Color.FromRgb(0x1B, 0x21, 0x2E)),
            Foreground = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x26, 0x2F, 0x3E)),
            BorderThickness = new Thickness(1),
        };
        _hex.TextChanged += OnHexChanged;
        var eyedrop = new Button
        {
            Content = "摘取",
            Margin = new Thickness(0, 12, 0, 0),
            Padding = new Thickness(10, 3, 10, 3),
            ToolTip = "从屏幕上取色",
            Style = (Style)Application.Current.Resources["GhostButton"],
            Foreground = Brushes.White,
        };
        eyedrop.Click += (_, _) => OpenEyedropper();
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Children = { _preview, _hex, eyedrop },
        };
        SetRow(row, 2);
        Children.Add(row);

        _rgb = new TextBlock
        {
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.FromRgb(0x8A, 0x92, 0xA6)),
            Margin = new Thickness(0, 8, 0, 0),
        };

        var bottom = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 10, 0, 0),
        };
        if (showActions)
        {
            var ok = new Button
            {
                Content = "确定",
                MinWidth = 68,
                Padding = new Thickness(10, 4, 10, 4),
                Style = (Style)Application.Current.Resources["PrimaryButton"],
            };
            ok.Click += (_, _) => Committed?.Invoke(Hex);
            var cancel = new Button
            {
                Content = "取消",
                MinWidth = 68,
                Margin = new Thickness(8, 0, 0, 0),
                Padding = new Thickness(10, 4, 10, 4),
                Style = (Style)Application.Current.Resources["GhostButton"],
                Foreground = Brushes.White,
            };
            cancel.Click += (_, _) => Cancelled?.Invoke();
            bottom.Children.Add(ok);
            bottom.Children.Add(cancel);
        }
        var footer = new Grid();
        footer.Children.Add(_rgb);
        footer.Children.Add(bottom);
        SetRow(footer, 3);
        Children.Add(footer);

        SetFromHex(initialHex);
        Loaded += (_, _) => Paint();
    }

    private void SetFromHex(string hex)
    {
        var text = (hex ?? "").Trim().TrimStart('#');
        if (text.Length != 6) text = "4C8DFF";
        try
        {
            SetColor(Color.FromRgb(
                Convert.ToByte(text[..2], 16),
                Convert.ToByte(text.Substring(2, 2), 16),
                Convert.ToByte(text.Substring(4, 2), 16)));
        }
        catch
        {
            SetColor(Color.FromRgb(0x4C, 0x8D, 0xFF));
        }
    }

    private void SetColor(Color color)
    {
        double r = color.R / 255.0, g = color.G / 255.0, b = color.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        _value = max;
        _sat = max <= 0.0001 ? 0 : (max - min) / max;
        double d = max - min;
        if (d <= 0.0001) _hue = 0;
        else if (Math.Abs(max - r) < 0.0001) _hue = 60 * (((g - b) / d) % 6);
        else if (Math.Abs(max - g) < 0.0001) _hue = 60 * ((b - r) / d + 2);
        else _hue = 60 * ((r - g) / d + 4);
        if (_hue < 0) _hue += 360;
        Paint();
        ColorChanged?.Invoke(Hex);
    }

    private void Paint()
    {
        if (_svOverlay == null) return;
        var hueColor = HsvToColor(_hue, 1, 1);
        var drawing = new DrawingGroup();
        drawing.Children.Add(new GeometryDrawing(
            new LinearGradientBrush(Colors.White, hueColor, new Point(0, 0), new Point(1, 0)), null,
            new RectangleGeometry(new Rect(0, 0, 1, 1))));
        drawing.Children.Add(new GeometryDrawing(
            new LinearGradientBrush(Color.FromArgb(0, 0, 0, 0), Color.FromArgb(255, 0, 0, 0), new Point(0, 0), new Point(0, 1)), null,
            new RectangleGeometry(new Rect(0, 0, 1, 1))));
        _svOverlay.Fill = new DrawingBrush(drawing)
        {
            ViewportUnits = BrushMappingMode.RelativeToBoundingBox,
            Viewport = new Rect(0, 0, 1, 1),
        };

        var color = Current;
        _preview.Background = new SolidColorBrush(color);
        _rgb.Text = $"R {color.R}   G {color.G}   B {color.B}";

        double w = _svArea.ActualWidth, h = _svArea.ActualHeight;
        if (w > 4 && h > 4) _svMarker.Margin = new Thickness(_sat * w - 6, (1 - _value) * h - 6, 0, 0);
        double hueWidth = _hueBar.ActualWidth;
        if (hueWidth > 4) _hueMarker.Margin = new Thickness(_hue / 360.0 * (hueWidth - 4), 0, 0, 0);

        if (!_syncing)
        {
            _syncing = true;
            _hex.Text = Hex;
            _syncing = false;
        }
    }

    private static Color HsvToColor(double h, double s, double v)
    {
        double c = v * s;
        double x = c * (1 - Math.Abs(h / 60.0 % 2 - 1));
        double m = v - c;
        double r = 0, g = 0, b = 0;
        if (h < 60) { r = c; g = x; }
        else if (h < 120) { r = x; g = c; }
        else if (h < 180) { g = c; b = x; }
        else if (h < 240) { g = x; b = c; }
        else if (h < 300) { r = x; b = c; }
        else { r = c; b = x; }
        return Color.FromRgb((byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
    }

    private void OnSvMouse(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        var p = e.GetPosition(_svArea);
        _sat = Math.Clamp(p.X / Math.Max(1, _svArea.ActualWidth), 0, 1);
        _value = 1 - Math.Clamp(p.Y / Math.Max(1, _svArea.ActualHeight), 0, 1);
        Paint();
        ColorChanged?.Invoke(Hex);
    }

    private void OnHueMouse(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        var p = e.GetPosition(_hueBar);
        _hue = Math.Clamp(p.X / Math.Max(1, _hueBar.ActualWidth), 0, 1) * 360;
        Paint();
        ColorChanged?.Invoke(Hex);
    }

    private void OnHexChanged(object sender, TextChangedEventArgs e)
    {
        if (_syncing) return;
        var text = _hex.Text.Trim().TrimStart('#');
        if (text.Length != 6) return;
        SetFromHex(text);
    }

    private void OpenEyedropper()
    {
        var host = Window.GetWindow(this);
        var probe = new ScreenProbeWindow(host ?? Application.Current.MainWindow);
        probe.Closed += (_, _) =>
        {
            if (probe.Picked is not { } picked) return;
            SetColor(picked);
            Paint();
        ColorChanged?.Invoke(Hex);
        };
        probe.Show();
    }
}
