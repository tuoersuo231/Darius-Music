using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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
        Hook(sldOpacity, 0.05, 1, () => _settings.Opacity, v => _settings.Opacity = v, txtOpacity, "", "0.00");
        Hook(sldFreqMin, 20, 2000, () => _settings.FreqMin, v => _settings.FreqMin = v, txtFreqMin, " Hz");
        Hook(sldFreqMax, 1000, 16000, () => _settings.FreqMax, v => _settings.FreqMax = v, txtFreqMax, " Hz");
        Hook(sldBoost, 1.0, 1.6, () => _settings.BoostPerBar, v => _settings.BoostPerBar = v, txtBoost, "×", "0.00",
            v => $"{v:0.00}× ≈ +{Math.Clamp((v - 1) * 40, 0, 24):0.0} dB");
        Hook(sldFloor, -90, 0, () => _settings.FloorDb, v => _settings.FloorDb = v, txtFloor, " dB");
        Hook(sldSpan, 4, 60, () => _settings.SpanDb, v => _settings.SpanDb = v, txtSpan, " dB");
        Hook(sldFast, 20, 800, () => _settings.FastMs, v => _settings.FastMs = v, txtFast, " ms");
        Hook(sldSlow, 100, 2400, () => _settings.SlowMs, v => _settings.SlowMs = v, txtSlow, " ms");
        Hook(sldGhostAlpha, 0, 255, () => _settings.GhostAlpha, v => _settings.GhostAlpha = (int)Math.Round(v), txtGhostAlpha, "");

        BindSwitch(chkEnabled, () => _settings.Enabled, v => _settings.Enabled = v);
        BindSwitch(chkOverlay, () => !_settings.IsBackdrop,
            v => _settings.Layer = v ? "overlay" : "backdrop", placement: true);
        BindSwitch(chkAutoGain, () => _settings.AutoGain, v => _settings.AutoGain = v);
        BindSwitch(chkMirror, () => _settings.Mirror, v => _settings.Mirror = v);
        BindSwitch(chkGhost, () => _settings.Ghost, v => _settings.Ghost = v);

        txtColor.Text = _settings.Color;
        _loading = false;
        Closed += (_, _) =>
        {
            try { _settings.Write(); } catch { }
        };
    }

    /// <summary>滑块 → 字段。范围、初值、读数文字一起带上。</summary>
    private void Hook(DragOnlySlider slider, double min, double max, Func<double> read, Action<double> write,
        TextBlock label, string unit = "", string format = "0", Func<double, string>? text = null)
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
            Apply(placement: false);
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
            _settings.Color = hex;
            txtColor.Text = hex;
            Apply(placement: false);
        }
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

    /// <summary>QA：像点开关一样切层级，验证放在哪一层会立刻变。</summary>
    internal string DebugSetOverlay(bool overlay)
    {
        _loading = false;
        chkOverlay.IsChecked = overlay;
        _settings.Layer = overlay ? "overlay" : "backdrop";
        Apply(placement: true);
        return $"overlay={overlay} layer={_settings.Layer}";
    }
}
