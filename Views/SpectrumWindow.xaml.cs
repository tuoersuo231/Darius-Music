using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using CurrentLyrics.Controls;
using CurrentLyrics.Services;

namespace CurrentLyrics.Views;

/// <summary>
/// 频谱设置窗。所有改动都是**实时**的：拖滑块立刻调 <see cref="SpectrumView.RefreshAll"/>，
/// 服务重建缓冲、控件按新参数重画，不用重启。停手 0.7 秒自动写回 data\spectrum.json。
/// </summary>
public partial class SpectrumWindow : Window
{
    private readonly Action _onChanged;
    private readonly SpectrumSettings _settings = SpectrumOptions.Current;
    private readonly DispatcherTimer _save;
    private bool _loading = true;

    public SpectrumWindow(Action onChanged)
    {
        _onChanged = onChanged;
        InitializeComponent();

        _save = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        _save.Tick += (_, _) =>
        {
            _save.Stop();
            try { _settings.Write(); } catch { }
        };

        Hook(sldHeight, 8, 600, () => _settings.Height, v => _settings.Height = v, txtHeight, " px");
        Hook(sldBars, 4, 128, () => _settings.Bars, v => _settings.Bars = (int)Math.Round(v), txtBars, " 条");
        Hook(sldGap, 0, 24, () => _settings.Gap, v => _settings.Gap = v, txtGap, " px", "0.#");
        Hook(sldRadius, 0, 12, () => _settings.CornerRadius, v => _settings.CornerRadius = v, txtRadius, " px", "0.#");
        Hook(sldOpacity, 0.05, 1, () => _settings.Opacity, v => _settings.Opacity = v, txtOpacity, "",
            "0.00", v => $"{v * 100:0}%");
        Hook(sldSeeThrough, 0, 80, () => _settings.PanelSeeThrough, v => _settings.PanelSeeThrough = (int)Math.Round(v),
            txtSeeThrough, " %", "0", null, placement: true);
        Hook(sldFreqMin, 20, 2000, () => _settings.FreqMin, v => _settings.FreqMin = v, txtFreqMin, " Hz");
        Hook(sldFreqMax, 1000, 16000, () => _settings.FreqMax, v => _settings.FreqMax = v, txtFreqMax, " Hz");
        Hook(sldBoost, 1.0, 1.6, () => _settings.BoostPerBar, v => _settings.BoostPerBar = v, txtBoost, "×", "0.00",
            v => $"{v:0.00}× ≈ +{Math.Clamp((v - 1) * 40, 0, 24):0.0} dB");
        Hook(sldFloor, -90, 0, () => _settings.FloorDb, v => _settings.FloorDb = v, txtFloor, " dB");
        Hook(sldSpan, 4, 60, () => _settings.SpanDb, v => _settings.SpanDb = v, txtSpan, " dB");
        Hook(sldFast, 20, 800, () => _settings.FastMs, v => _settings.FastMs = v, txtFast, " ms");
        Hook(sldSlow, 100, 2400, () => _settings.SlowMs, v => _settings.SlowMs = v, txtSlow, " ms");
        Hook(sldGhostAlpha, 0, 255, () => _settings.GhostAlpha, v => _settings.GhostAlpha = (int)Math.Round(v), txtGhostAlpha, "");
        Hook(sldPeakThickness, 1, 16, () => _settings.PeakThickness, v => _settings.PeakThickness = v,
            txtPeakThickness, " px", "0.#");
        Hook(sldSteps, 0, 40, () => _settings.Steps, v => _settings.Steps = (int)Math.Round(v), txtSteps, "", "0",
            v => v < 2 ? "平滑" : $"{v:0} 段");

        BindSwitch(chkEnabled, () => _settings.Enabled, v => _settings.Enabled = v);
        BindSwitch(chkOwnAudio, () => _settings.OwnAudioOnly, v => _settings.OwnAudioOnly = v);
        BindSwitch(chkAutoGain, () => _settings.AutoGain, v => _settings.AutoGain = v);
        BindSwitch(chkMirror, () => _settings.Mirror, v => _settings.Mirror = v);
        BindSwitch(chkGhost, () => _settings.Ghost, v => _settings.Ghost = v);
        BindSwitch(chkPeak, () => _settings.Peak, v => _settings.Peak = v);

        txtColor.Text = _settings.Color;
        RebuildSwatches();
        _loading = false;
        // 打开窗口时先按当前层级把「面板透出」的可用状态摆对（自动判断是只读的）。
        sldSeeThrough.IsEnabled = SpectrumOptions.Backdrop;
        sldSeeThrough.Opacity = SpectrumOptions.Backdrop ? 1 : 0.45;
        Closed += (_, _) =>
        {
            try { _settings.Write(); } catch { }
        };
    }

    /// <summary>滑块 → 字段。范围、初值、读数文字一起带上。</summary>
    private void Hook(DragOnlySlider slider, double min, double max, Func<double> read, Action<double> write,
        TextBlock label, string unit = "", string format = "0", Func<double, string>? text = null,
        bool placement = false)
    {
        slider.Minimum = min;
        slider.Maximum = max;
        slider.Value = Math.Clamp(read(), min, max);
        slider.IsMoveToPointEnabled = true;
        label.Text = text?.Invoke(read()) ?? read().ToString(format) + unit;
        slider.ValueChanged += (_, e) =>
        {
            if (_loading) return;
            write(e.NewValue);
            label.Text = text?.Invoke(read()) ?? read().ToString(format) + unit;
            Apply(placement);
        };
    }

    private void BindSwitch(CheckBox box, Func<bool> read, Action<bool> write, bool placement = false)
    {
        box.IsChecked = read();
        box.Click += (_, _) =>
        {
            if (_loading) return;
            write(box.IsChecked == true);
            Apply(placement);
        };
    }

    private void Apply(bool placement)
    {
        // 面板本来就不透明时频谱会浮在上面，「面板透出」这一项不起作用 —— 置灰，别让人拖了没反应。
        sldSeeThrough.IsEnabled = SpectrumOptions.Backdrop;
        sldSeeThrough.Opacity = SpectrumOptions.Backdrop ? 1 : 0.45;
        SpectrumView.RefreshAll();
        // 只有动了"层级"才需要重排窗口配色（面板透明度跟着层走），拖条不用。
        if (placement) _onChanged?.Invoke();
        _save.Stop();
        _save.Start();
    }

    private void OnSwatchClick(object sender, MouseButtonEventArgs e)
    {
        if (_loading) return;
        if (sender is FrameworkElement element && element.Tag is string hex)
        {
            _settings.Color = "#" + SpectrumSettings.NormalizeHex(hex);
            txtColor.Text = _settings.Color;
            RememberColor(hex);
            Apply(placement: false);
        }
    }

    /// <summary>点「自选颜色…」展开色盘。拖动实时改色，按确定才记进"最近用过"。</summary>
    private void OnPickColorClick(object sender, RoutedEventArgs e)
    {
        if (pickerCard.Visibility == Visibility.Visible)
        {
            pickerCard.Visibility = Visibility.Collapsed;
            pickerHost.Content = null;
            return;
        }

        var panel = new ColorPickerPanel(_settings.Color);
        panel.ColorChanged += hex =>
        {
            var clean = SpectrumSettings.NormalizeHex(hex);
            if (clean.Length != 6) return;
            _settings.Color = "#" + clean;
            txtColor.Text = _settings.Color;
            Apply(placement: false);
        };
        panel.Committed += hex =>
        {
            var clean = SpectrumSettings.NormalizeHex(hex);
            if (clean.Length == 6)
            {
                _settings.Color = "#" + clean;
                txtColor.Text = _settings.Color;
                RememberColor(clean);
                Apply(placement: false);
            }
            pickerCard.Visibility = Visibility.Collapsed;
            pickerHost.Content = null;
        };
        panel.Cancelled += () =>
        {
            pickerCard.Visibility = Visibility.Collapsed;
            pickerHost.Content = null;
        };
        pickerHost.Content = panel;
        pickerCard.Visibility = Visibility.Visible;
        // 色盘在下面，展开后滚到它那里，不然点完像没反应。
        pickerCard.BringIntoView();
    }

    /// <summary>把颜色记到"最近用过"最前面，最多留 8 个。</summary>
    private void RememberColor(string hex)
    {
        var clean = SpectrumSettings.NormalizeHex(hex);
        if (clean.Length != 6) return;
        _settings.RecentColors ??= new List<string>();
        _settings.RecentColors.RemoveAll(h => string.Equals(h, clean, StringComparison.OrdinalIgnoreCase));
        _settings.RecentColors.Insert(0, clean);
        while (_settings.RecentColors.Count > 8)
            _settings.RecentColors.RemoveAt(_settings.RecentColors.Count - 1);
        RebuildSwatches();
    }

    private void RebuildSwatches()
    {
        if (swatchHost == null) return;
        swatchHost.Children.Clear();
        var active = SpectrumSettings.NormalizeHex(_settings.Color);
        foreach (var hex in _settings.RecentColors ?? new List<string>())
        {
            var swatch = new Border
            {
                Width = 34,
                Height = 24,
                CornerRadius = new CornerRadius(6),
                Margin = new Thickness(0, 0, 8, 8),
                Cursor = Cursors.Hand,
                Background = new SolidColorBrush(ParseHex(hex)),
                Tag = "#" + hex,
                ToolTip = "#" + hex,
                BorderThickness = new Thickness(string.Equals(hex, active, StringComparison.OrdinalIgnoreCase) ? 2 : 0),
                BorderBrush = Brushes.White,
            };
            swatch.MouseLeftButtonUp += OnSwatchClick;
            swatchHost.Children.Add(swatch);
        }
    }

    private static Color ParseHex(string hex)
    {
        if (hex != null && hex.Length == 6 &&
            byte.TryParse(hex.AsSpan(0, 2), NumberStyles.HexNumber, null, out var r) &&
            byte.TryParse(hex.AsSpan(2, 2), NumberStyles.HexNumber, null, out var g) &&
            byte.TryParse(hex.AsSpan(4, 2), NumberStyles.HexNumber, null, out var b))
            return Color.FromRgb(r, g, b);
        return Color.FromRgb(0xC7, 0x7D, 0xFF);
    }

    /// <summary>QA：滚到最底部（截图验证下面几张卡片，尤其是新增的落点/量化）。</summary>
    internal string DebugScrollEnd()
    {
        var viewer = FindScroll(this);
        if (viewer == null) return "no-scrollviewer";
        viewer.ScrollToEnd();
        return $"offset={viewer.VerticalOffset:0}/{viewer.ScrollableHeight:0}";
    }

    private static ScrollViewer? FindScroll(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer viewer) return viewer;
            var found = FindScroll(child);
            if (found != null) return found;
        }
        return null;
    }

    /// <summary>QA：报当前参数。</summary>
    internal string DebugState() =>
        $"layer={_settings.Layer} bars={_settings.Bars} height={_settings.Height:0} mirror={_settings.Mirror} " +
        $"autoGain={_settings.AutoGain} color={_settings.Color}";

    /// <summary>QA：像拖滑块一样实时改高度，用来验证不重启就生效。</summary>
    internal string DebugSetHeight(double value)
    {
        _loading = false;
        sldHeight.Value = value;
        return $"requested={value:0} applied={_settings.Height:0}";
    }

    /// <summary>QA：报告当前自动判断出来的层级（1.3.16 起没有开关了）。</summary>
    internal string DebugLayer()
    {
        Apply(placement: false);
        return $"layer={(SpectrumOptions.Backdrop ? "backdrop" : "overlay")} see={_settings.PanelSeeThrough}";
    }

    /// <summary>QA：面板透出百分比。</summary>
    internal string DebugSetSeeThrough(int value)
    {
        _loading = false;
        sldSeeThrough.Value = value;
        return $"see={_settings.PanelSeeThrough}";
    }

    /// <summary>QA：像按确定一样选一个颜色（进"最近用过"）。</summary>
    internal string DebugPickColor(string hex)
    {
        RememberColor(hex);
        var clean = SpectrumSettings.NormalizeHex(hex);
        if (clean.Length == 6) _settings.Color = "#" + clean;
        txtColor.Text = _settings.Color;
        Apply(placement: false);
        return $"color={_settings.Color} recents={string.Join("/", _settings.RecentColors ?? new List<string>())}";
    }

    /// <summary>QA：展开色盘。</summary>
    internal string DebugOpenPicker()
    {
        OnPickColorClick(this, new RoutedEventArgs());
        return pickerCard.Visibility.ToString();
    }
}
