using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using CurrentLyrics.Services;

namespace CurrentLyrics.Controls;

/// <summary>
/// 24 条镜像频谱：中间是低频、两边是高频，跟着系统输出跳。
///
/// 数据来自 <see cref="SpectrumService"/>（WASAPI 回环），所以不依赖播放器 —— 播什么都能跳，
/// 包括别的程序的声音。所有实例共用一个 loopback，关到最后一个才停。
///
/// 画法照「简约频谱」皮肤：实心条是 150 ms 快衰减，后面拖一段 800 ms 慢衰减的残影。
/// </summary>
public sealed class SpectrumView : FrameworkElement
{
    private static SpectrumService? _shared;
    private static int _users;

    private readonly SpectrumService _service;
    private readonly float[] _fast = new float[SpectrumService.BarCount];
    private readonly float[] _slow = new float[SpectrumService.BarCount];

    private SolidColorBrush? _fill;
    private SolidColorBrush? _ghost;
    private Color _fillColor = Color.FromRgb(0xC7, 0x7D, 0xFF);
    private double _gap = 3;
    private double _radius = 1.5;
    private bool _running;
    private DispatcherTimer? _timer;

    public SpectrumView()
    {
        _service = Acquire();
        IsHitTestVisible = false;
        SnapsToDevicePixels = true;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    /// <summary>条的颜色（主题强调色）。</summary>
    public Color FillColor
    {
        get => _fillColor;
        set
        {
            if (value == _fillColor) return;
            _fillColor = value;
            _fill = null;
            _ghost = null;
            InvalidateVisual();
        }
    }

    /// <summary>条之间的空隙。</summary>
    public double Gap
    {
        get => _gap;
        set { _gap = Math.Max(0, value); InvalidateVisual(); }
    }

    public double CornerRadius
    {
        get => _radius;
        set { _radius = Math.Max(0, value); InvalidateVisual(); }
    }

    private static SpectrumService Acquire()
    {
        if (_shared == null)
        {
            _shared = new SpectrumService();
            _shared.Start();
        }
        _users++;
        return _shared;
    }

    private static void Release()
    {
        // 故意不 Stop：页面切换时控件会被 DetachShared 移出可视树再放回来，停掉再恢复要重新
        // 初始化 WASAPI，中间会丢几秒数据（看起来就是"频谱不动了"）。loopback 本身只是每秒
        // 九十几次 FFT，常驻代价可以忽略。
        if (_users > 0) _users--;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _service.Start();   // 幂等：布局重建会被移除又加回来，这里确保回环是活的
        if (_running) return;
        _running = true;
        CompositionTarget.Rendering += OnFrame;
        // Rendering 在正常窗口里每帧都来；QA 的离屏渲染循环不一定驱动它，所以再挂一个定时器兜底。
        _timer ??= new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(33),
        };
        _timer.Tick -= OnFrame;
        _timer.Tick += OnFrame;
        _timer.Start();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_running)
        {
            CompositionTarget.Rendering -= OnFrame;
            _running = false;
        }
        if (_timer != null)
        {
            _timer.Tick -= OnFrame;
            _timer.Stop();
        }
        Release();
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        if (!IsVisible || ActualWidth < 4 || ActualHeight < 4) return;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        double width = ActualWidth;
        double height = ActualHeight;
        if (width <= 1 || height <= 1) return;

        if (_fill == null) _fill = Freeze(Color.FromArgb(255, _fillColor.R, _fillColor.G, _fillColor.B));
        if (_ghost == null) _ghost = Freeze(Color.FromArgb(70, _fillColor.R, _fillColor.G, _fillColor.B));

        var fast = _service.Read();
        var slow = _service.Read(true);
        Array.Copy(fast, _fast, Math.Min(fast.Length, _fast.Length));
        Array.Copy(slow, _slow, Math.Min(slow.Length, _slow.Length));

        int count = SpectrumService.BarCount;
        int half = count / 2;
        double totalGap = _gap * (count - 1);
        double barWidth = Math.Max(1.2, (width - totalGap) / count);

        for (int i = 0; i < count; i++)
        {
            // 镜像：i=0 和 i=count-1 都是最外侧的高频，中间是低频。
            int source = Math.Abs(i - (count - 1) / 2.0) < 0.5
                ? 0
                : (int)Math.Round(Math.Abs(i - (count - 1) / 2.0));
            source = Math.Clamp(source, 0, half);

            double x = i * (barWidth + _gap);
            double solid = height * _fast[source];
            double ghost = height * _slow[source];

            if (ghost > solid + 0.5)
                DrawBar(dc, _ghost!, x, barWidth, height, ghost - solid, solid);
            if (solid > 0.5)
                DrawBar(dc, _fill!, x, barWidth, height, solid, 0);
        }
    }

    private void DrawBar(DrawingContext dc, Brush brush, double x, double w, double height,
        double h, double bottomOffset)
    {
        double top = height - bottomOffset - h;
        if (h <= 0.5) return;
        if (_radius <= 0.5)
        {
            dc.DrawRectangle(brush, null, new Rect(x, top, w, h));
            return;
        }
        double r = Math.Min(_radius, Math.Min(w / 2, h / 2));
        dc.DrawRoundedRectangle(brush, null, new Rect(x, top, w, h), r, r);
    }

    private static SolidColorBrush Freeze(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}