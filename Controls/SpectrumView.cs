using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using CurrentLyrics.Services;

namespace CurrentLyrics.Controls;

/// <summary>
/// 镜像频谱条。中间低频、两边高频（<c>Mirror=false</c> 时从左到右由低到高），
/// 实心条 + 慢衰减残影。
///
/// 所有外观参数都来自 <see cref="SpectrumSettings"/>（settings.json 的 <c>Spectrum</c> 节点），
/// 改完重启生效。数据来自 <see cref="SpectrumService"/>（WASAPI 回环），播什么都会跳。
///
/// 所有实例共用一个回环，且**从不 Stop** —— 页面切换时控件会被移出可视树再放回来，
/// 停掉再恢复要重新初始化 WASAPI，中间会丢几秒数据（看起来就是"频谱不动了"）。
/// </summary>
public sealed class SpectrumView : FrameworkElement
{
    private static SpectrumService? _shared;
    private static int _users;
    private static readonly List<SpectrumView> _live = new();

    private readonly SpectrumService _service;
    private SpectrumSettings _options = SpectrumOptions.Current;

    private SolidColorBrush? _fill;
    private SolidColorBrush? _ghost;
    private Color _cachedFill = Colors.Transparent;
    private double _cachedAlpha = -1;
    private int _cachedGhostAlpha = -1;

    private bool _running;
    private DispatcherTimer? _timer;

    /// <summary>诊断用：回环抓了多少字节、画出来多大、当前最大值。</summary>
    public string DebugState
    {
        get
        {
            var fast = _service.Read();
            float max = 0;
            foreach (var value in fast) if (value > max) max = value;
            return $"running={_service.Running} bytes={_service.BytesCaptured} frames={_service.Frames} " +
                   $"device='{_service.ActiveDevice}' taps=[{_service.TapDiagnostics}] " +
                   $"size={ActualWidth:0}x{ActualHeight:0} visible={IsVisible} bars={fast.Length} max={max:0.00} " +
                   $"vals=[{string.Join(",", fast.Select(v => v.ToString("0.00")))}]";
        }
    }

    public SpectrumView()
    {
        _service = Acquire();
        _options = SpectrumOptions.Current;
        _service.Configure(_options);
        IsHitTestVisible = false;
        SnapsToDevicePixels = true;
        _live.Add(this);
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    /// <summary>
    /// 参数被设置窗口改过之后调这个：服务重新分配缓冲、所有活着的控件马上按新参数重画。
    /// 不用重启 —— 「频谱设置」里拖滑块就是走这条路。
    /// </summary>
    public static void RefreshAll()
    {
        var options = SpectrumOptions.Current;
        _shared?.Configure(options);
        foreach (var view in _live.ToArray())
        {
            view._options = options;
            view.ApplyOptions();
        }
    }

    /// <summary>把高度 / 透明度和开关落到控件上（参数可以在启动之后改）。</summary>
    private void ApplyOptions()
    {
        var options = SpectrumOptions.Current;
        _options = options;
        Visibility = options.Enabled ? Visibility.Visible : Visibility.Collapsed;
        if (!options.Enabled) return;
        double wanted = Math.Max(8, options.Height);
        if (double.IsNaN(Height) || Math.Abs(Height - wanted) > 0.5) Height = wanted;
        // 透明度不再交给控件级 Opacity：那样会连颜色一起和底色混（用户：颜色变了）。
        // 改成画的时候按 alpha 上色、颜色先反解，见 OnRender / Unmix。
        Opacity = 1;
        InvalidateVisual();
    }

    private static SpectrumService Acquire()
    {
        if (_shared == null)
        {
            _shared = new SpectrumService();
            _shared.Configure(SpectrumOptions.Current);
            _shared.Start();
        }
        _users++;
        return _shared;
    }

    private static void Release()
    {
        // 故意不 Stop，理由见类注释。
        if (_users > 0) _users--;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _service.Start();   // 幂等：布局重建会被移除又加回来，这里确保回环是活的

        // 高度也交给配置，XAML 那边不写死，否则改了 spectrum.json 没反应。
        ApplyOptions();

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

        var options = SpectrumOptions.Current;
        _options = options;
        Color color = options.ResolveColor();
        double alpha = Math.Clamp(options.Opacity, 0.02, 1);
        Color panel = PanelColor();
        // 半透明的柱子会跟底色混，颜色就变了。这里先把颜色反解出来：
        // 画 a 的透明度、颜色 C'，合成结果正好是 C —— 看起来"淡了"，但色相不跑。
        Color drawn = Unmix(color, panel, alpha);
        if (_fill == null || drawn != _cachedFill || alpha != _cachedAlpha)
        {
            _cachedFill = drawn;
            _cachedAlpha = alpha;
            _fill = Freeze(Color.FromArgb((byte)Math.Round(alpha * 255), drawn.R, drawn.G, drawn.B));
            _ghost = null;
        }
        int ghostAlpha = Math.Clamp(options.GhostAlpha, 0, 255);
        if (_ghost == null || ghostAlpha != _cachedGhostAlpha)
        {
            _cachedGhostAlpha = ghostAlpha;
            _ghost = Freeze(Color.FromArgb((byte)Math.Round(ghostAlpha / 255.0 * alpha * 255), drawn.R, drawn.G, drawn.B));
        }

        var fast = _service.Read();
        var slow = _service.Read(true);
        // 频段数可能少于柱子数：镜像模式下左右两半共用同一组频段。
        int bands = fast.Length;
        int count = Math.Max(1, options.Mirror ? options.Bars : bands);
        if (bands == 0) return;

        double gap = Math.Max(0, options.Gap);
        double radius = Math.Max(0, options.CornerRadius);
        double totalGap = gap * (count - 1);
        double barWidth = Math.Max(1.0, (width - totalGap) / count);
        bool mirrored = options.Mirror;

        for (int i = 0; i < count; i++)
        {
            int source = i;
            if (mirrored)
            {
                // i=0 和 i=count-1 是最外侧的高频，中间是低频。
                // 按比例摊到 0..bands-1：以前用"到中心的距离直接取整"，39 个频段里会有一半
                // 因为银行家舍入被跳过、另一半被画两遍，看起来又糊又平。
                double half = (count - 1) / 2.0;
                double t = half < 0.5 ? 0 : Math.Abs(i - half) / half;
                source = (int)Math.Round(t * (bands - 1));
            }
            source = Math.Clamp(source, 0, bands - 1);

            double x = i * (barWidth + gap);
            double solid = height * fast[source];
            double ghost = options.Ghost ? height * slow[source] : 0;

            if (ghost > solid + 0.5)
                DrawBar(dc, _ghost!, x, barWidth, height, ghost - solid, solid, radius);
            if (solid > 0.5)
                DrawBar(dc, _fill!, x, barWidth, height, solid, 0, radius);
        }
    }

    private static void DrawBar(DrawingContext dc, Brush brush, double x, double w, double height,
        double h, double bottomOffset, double radius)
    {
        if (h <= 0.5) return;
        double top = height - bottomOffset - h;
        if (radius <= 0.5)
        {
            dc.DrawRectangle(brush, null, new Rect(x, top, w, h));
            return;
        }
        double r = Math.Min(radius, Math.Min(w / 2, h / 2));
        dc.DrawRoundedRectangle(brush, null, new Rect(x, top, w, h), r, r);
    }

    private static SolidColorBrush Freeze(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    /// <summary>柱子底下那层是什么颜色（面板色）。用来把透明度"反解"成不透的颜色。</summary>
    private Color PanelColor()
    {
        try
        {
            if (TryFindResource("SurfaceBrush") is SolidColorBrush brush) return brush.Color;
        }
        catch { }
        return Color.FromRgb(0x0D, 0x0F, 0x14);
    }

    /// <summary>
    /// 求 C'，使得 <c>alpha*C' + (1-alpha)*back == target</c>。
    /// 这样柱子按 alpha 画出去，压在面板上的最终颜色还是 target 本身 —— 只是周围的字能透过来。
    /// </summary>
    private static Color Unmix(Color target, Color back, double alpha)
    {
        if (alpha >= 0.995) return target;
        double a = Math.Max(0.02, alpha);
        byte Mix(double t, double b) => (byte)Math.Clamp((t - (1 - a) * b) / a, 0, 255);
        return Color.FromRgb(Mix(target.R, back.R), Mix(target.G, back.G), Mix(target.B, back.B));
    }
}