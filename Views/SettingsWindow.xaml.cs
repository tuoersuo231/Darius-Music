using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CurrentLyrics.Services;

namespace CurrentLyrics.Views;

public partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;
    private readonly Action<AppSettings> _onApply;
    private bool _initializing = true;

    private readonly PlayerController? _player;
    private System.Windows.Threading.DispatcherTimer? _songTimer;

    public SettingsWindow(AppSettings settings, Action<AppSettings> onApply, PlayerController? player = null)
    {
        InitializeComponent();
        _settings = settings;
        _onApply = onApply;
        _player = player;
        LoadFromSettings();
        _initializing = false;
        RefreshSongOffset();
        _songTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        _songTimer.Tick += (_, _) => RefreshSongOffset();
        _songTimer.Start();
        Closed += (_, _) => _songTimer?.Stop();

        chkEnable.Checked += (_, _) => Apply(s => s.Enabled = true);
        chkEnable.Unchecked += (_, _) => Apply(s => s.Enabled = false);
        chkTranslation.Checked += (_, _) => Apply(s => s.Translation = true);
        chkTranslation.Unchecked += (_, _) => Apply(s => s.Translation = false);
        chkGlow.Checked += (_, _) => Apply(s => s.GlowEnabled = true);
        chkGlow.Unchecked += (_, _) => Apply(s => s.GlowEnabled = false);
        chkLocked.Checked += (_, _) => Apply(s => s.Locked = true);
        chkLocked.Unchecked += (_, _) => Apply(s => s.Locked = false);
        chkTopmost.Checked += (_, _) => Apply(s => s.Topmost = true);
        chkTopmost.Unchecked += (_, _) => Apply(s => s.Topmost = false);
        sldTilt.ValueChanged += (_, _) =>
        {
            lblTilt.Text = $"{sldTilt.Value:0}°";
            Apply(s => s.TiltAngle = sldTilt.Value);
        };
        sldVp.ValueChanged += (_, _) =>
        {
            lblVp.Text = $"{sldVp.Value:0.0}× 列宽";
            Apply(s => s.VanishingOffset = sldVp.Value);
        };
        sldLines.ValueChanged += (_, _) =>
        {
            lblLines.Text = $"{sldLines.Value:0}";
            Apply(s => s.VisibleLines = (int)sldLines.Value);
        };
        sldScale.ValueChanged += (_, _) =>
        {
            lblScale.Text = $"{sldScale.Value:0.00}×";
            Apply(s => s.CurrentScale = sldScale.Value);
        };
        chkPerspective.Checked += (_, _) => Apply(s => s.Perspective = true);
        chkPerspective.Unchecked += (_, _) => Apply(s => s.Perspective = false);
        rbWenkai.Checked += (_, _) => Apply(s => s.FontFamily = FontResolver.WenKai);
        rbSystem.Checked += (_, _) => Apply(s => s.FontFamily = "Microsoft YaHei UI");

        sldFont.ValueChanged += (_, e) => Apply(s => s.FontSize = Math.Round(e.NewValue));
        sldWidth.ValueChanged += (_, e) => Apply(s => s.LineWidth = Math.Round(e.NewValue / 10) * 10);
        sldGap.ValueChanged += (_, e) => Apply(s => s.LineGap = Math.Round(e.NewValue));
        sldOpacity.ValueChanged += (_, e) => Apply(s => s.Opacity = Math.Round(e.NewValue) / 100.0);
        sldGlowStrength.ValueChanged += (_, e) => Apply(s => s.GlowStrength = Math.Round(e.NewValue) / 100.0);
        sldGlowSpread.ValueChanged += (_, e) => Apply(s => s.GlowSpread = Math.Round(e.NewValue) / 100.0);
        sldAnchor.ValueChanged += (_, e) => Apply(s => { s.Anchor = Math.Round(e.NewValue) / 100.0; s.CustomPosition = false; });
        sldOffset.ValueChanged += (_, e) => Apply(s => s.OffsetMs = (long)(Math.Round(e.NewValue / 100) * 100));


    }

    private void LoadFromSettings()
    {
        chkEnable.IsChecked = _settings.Enabled;
        chkTranslation.IsChecked = _settings.Translation;
        chkGlow.IsChecked = _settings.GlowEnabled;
        chkLocked.IsChecked = _settings.Locked;
        chkTopmost.IsChecked = _settings.Topmost;
        chkPerspective.IsChecked = _settings.Perspective;
        sldTilt.Value = _settings.TiltAngle;
        sldVp.Value = _settings.VanishingOffset;
        lblVp.Text = $"{_settings.VanishingOffset:0.0}× 列宽";
        sldLines.Value = _settings.VisibleLines;
        sldScale.Value = _settings.CurrentScale;
        lblTilt.Text = $"{_settings.TiltAngle:0}°";
        lblLines.Text = $"{_settings.VisibleLines}";
        lblScale.Text = $"{_settings.CurrentScale:0.00}×";
        bool wenkai = _settings.FontFamily.Contains("LXGW") || _settings.FontFamily.Contains("霞鹜");
        rbWenkai.IsChecked = wenkai;
        rbSystem.IsChecked = !wenkai;
        sldFont.Value = _settings.DisplayFontSize;
        sldWidth.Value = _settings.LineWidth;
        sldGap.Value = _settings.LineGap;
        sldOpacity.Value = _settings.DisplayOpacity * 100;
        sldGlowStrength.Value = _settings.GlowStrength * 100;
        sldGlowSpread.Value = _settings.GlowSpread * 100;
        sldAnchor.Value = _settings.Anchor * 100;

        rbBackdropCover.IsChecked = _settings.BackdropMode == BackdropMode.Cover;
        rbBackdropImage.IsChecked = _settings.BackdropMode == BackdropMode.Image;
        rbBackdropColor.IsChecked = _settings.BackdropMode == BackdropMode.Color;
        rbBackdropNone.IsChecked = _settings.BackdropMode == BackdropMode.Transparent;
        sldBackdropBlur.Value = _settings.BackdropBlur;
        sldBackdropOpacity.Value = _settings.BackdropOpacity * 100;
        txtBackdropBlur.Text = ((int)_settings.BackdropBlur).ToString();
        txtBackdropOpacity.Text = $"{(int)Math.Round(_settings.BackdropOpacity * 100)}%";
        txtBackdropColorHex.Text = "#" + _settings.BackdropColor.ToUpperInvariant();
        var swatch = new System.Windows.Media.SolidColorBrush(ParseHex(_settings.BackdropColor, System.Windows.Media.Color.FromRgb(0x0E, 0x11, 0x18)));
        btnBackdropColor.Background = swatch;

        sldBackdropBlur.ValueChanged += (_, e) =>
        {
            txtBackdropBlur.Text = ((int)e.NewValue).ToString();
            Apply(s => s.BackdropBlur = Math.Round(e.NewValue));
        };
        sldBackdropOpacity.ValueChanged += (_, e) =>
        {
            txtBackdropOpacity.Text = $"{(int)Math.Round(e.NewValue)}%";
            Apply(s => s.BackdropOpacity = Math.Round(e.NewValue) / 100.0);
        };

        sldBackdropWidth.Value = Math.Min(1400, _settings.BackdropWidth);
        sldBackdropHeight.Value = Math.Min(900, _settings.BackdropHeight);
        txtBackdropWidth.Text = _settings.BackdropWidth > 0 ? $"{(int)_settings.BackdropWidth} px" : "自动";
        txtBackdropHeight.Text = _settings.BackdropHeight > 0 ? $"{(int)_settings.BackdropHeight} px" : "自动";
        sldBackdropWidth.ValueChanged += (_, e) =>
        {
            txtBackdropWidth.Text = e.NewValue > 0.5 ? $"{(int)e.NewValue} px" : "自动";
            Apply(s => s.BackdropWidth = Math.Round(e.NewValue));
        };
        sldBackdropHeight.ValueChanged += (_, e) =>
        {
            txtBackdropHeight.Text = e.NewValue > 0.5 ? $"{(int)e.NewValue} px" : "自动";
            Apply(s => s.BackdropHeight = Math.Round(e.NewValue));
        };

        chkBackdropFade.IsChecked = _settings.BackdropFadeEnabled;
        chkBackdropFade.Checked += (_, _) => Apply(s => s.BackdropFadeEnabled = true);
        chkBackdropFade.Unchecked += (_, _) => Apply(s => s.BackdropFadeEnabled = false);
        sldFadeStrength.Value = _settings.BackdropFadeStrength;
        sldFadeH.Value = _settings.BackdropFadeH;
        sldFadeV.Value = _settings.BackdropFadeV;
        txtFadeStrength.Text = $"{(int)_settings.BackdropFadeStrength}%";
        txtFadeH.Text = PercentText(_settings.BackdropFadeH);
        txtFadeV.Text = PercentText(_settings.BackdropFadeV);
        sldFadeStrength.ValueChanged += (_, e) =>
        {
            txtFadeStrength.Text = $"{(int)e.NewValue}%";
            Apply(s => s.BackdropFadeStrength = Math.Round(e.NewValue));
        };
        sldFadeH.ValueChanged += (_, e) =>
        {
            txtFadeH.Text = PercentText(e.NewValue);
            // 1/7 of the width is the ceiling, so this one keeps a decimal (the cap is 14.2857%).
            Apply(s => s.BackdropFadeH = Math.Round(e.NewValue, 1));
        };
        sldFadeV.ValueChanged += (_, e) =>
        {
            txtFadeV.Text = PercentText(e.NewValue);
            Apply(s => s.BackdropFadeV = Math.Round(e.NewValue));
        };
        sldOffset.Value = _settings.OffsetMs;
        UpdateLabels();
    }

    /// <summary>Whole percentages stay "8%"; the 1/7 cap (14.2857) reads "14.3%".</summary>
    private static string PercentText(double value) =>
        (Math.Abs(value - Math.Round(value)) < 0.05 ? Math.Round(value).ToString("0") : value.ToString("0.#")) + "%";

    private void UpdateLabels()
    {
        txtFontValue.Text = $"{_settings.DisplayFontSize:0} px";
        txtWidthValue.Text = $"{_settings.LineWidth:0} px";
        txtGapValue.Text = $"{_settings.LineGap:0} px";
        txtOpacityValue.Text = $"{_settings.DisplayOpacity * 100:0}%";
        txtGlowStrength.Text = $"{_settings.GlowStrength * 100:0}%";
        txtGlowSpread.Text = $"{_settings.GlowSpread * 100:0}%";
        txtAnchorValue.Text = $"{_settings.Anchor * 100:0}%";
        txtOffsetValue.Text = _settings.OffsetMs == 0
            ? "同步"
            : $"{(_settings.OffsetMs > 0 ? "+" : "")}{_settings.OffsetMs} ms";
        txtTextColorHex.Text = $"#{_settings.TextColor}";
        txtGlowColorHex.Text = $"#{_settings.GlowColor}";

        btnTextColor.Background = new SolidColorBrush(DesktopLyricsWindow.ParseColor(_settings.TextColor));
        btnGlowColor.Background = new SolidColorBrush(DesktopLyricsWindow.ParseColor(_settings.GlowColor));
        glowPanel.IsEnabled = _settings.GlowEnabled;
        glowPanel.Opacity = _settings.GlowEnabled ? 1 : 0.45;
        txtSummary.Text = _settings.Enabled ? "已开启 · 播放音乐后同步显示" : "已关闭，样式与位置会保留";
    }

    /// <summary>Repaints just the two colour swatches after a picker closes.</summary>
    private void RefreshColorFields()
    {
        txtTextColorHex.Text = $"#{_settings.TextColor}";
        txtGlowColorHex.Text = $"#{_settings.GlowColor}";
        btnTextColor.Background = new SolidColorBrush(DesktopLyricsWindow.ParseColor(_settings.TextColor));
        btnGlowColor.Background = new SolidColorBrush(DesktopLyricsWindow.ParseColor(_settings.GlowColor));
    }

    private bool _applyQueued;

    private void Apply(Action<AppSettings> change)
    {
        if (_initializing) return;
        change(_settings);
        var normalized = _settings.Normalized();
        _settings.CopyFrom(normalized);
        UpdateLabels();
        QueueApply();
    }

    /// <summary>
    /// One apply per rendered frame, however many events a drag throws at us. Both the sliders and
    /// the inline colour picker go through here: the colour picker fires on every mouse move, and
    /// each apply used to rebuild the overlay's frosted backdrop (20–45 ms) — which is what made a
    /// colour drag stutter instead of previewing.
    /// </summary>
    private void QueueApply()
    {
        if (_applyQueued) return;
        _applyQueued = true;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _applyQueued = false;
            _onApply(_settings);
        }), System.Windows.Threading.DispatcherPriority.Render);
    }

    private static void Copy(AppSettings from, AppSettings to)
    {
        to.Enabled = from.Enabled;
        to.Translation = from.Translation;
        to.FontSize = from.FontSize;
        to.Opacity = from.Opacity;
        to.OffsetMs = from.OffsetMs;
        to.Height = from.Height;
        to.LineWidth = from.LineWidth;
        to.LineGap = from.LineGap;
        to.Anchor = from.Anchor;
        to.Backdrop = from.Backdrop;
        to.Perspective = from.Perspective;
        to.TiltAngle = from.TiltAngle;
        to.VanishingOffset = from.VanishingOffset;
        to.VisibleLines = from.VisibleLines;
        to.CurrentScale = from.CurrentScale;
        to.GlowEnabled = from.GlowEnabled;
        to.GlowStrength = from.GlowStrength;
        to.GlowSpread = from.GlowSpread;
        to.TextColor = from.TextColor;
        to.GlowColor = from.GlowColor;
        to.Locked = from.Locked;
        to.Topmost = from.Topmost;
        to.Scale = from.Scale;
        to.FontFamily = from.FontFamily;
        to.Volume = from.Volume;
        to.MusicFolder = from.MusicFolder;
    }

    // ---------- colour picker ----------

    /// <summary>One inline picker right under the row it belongs to: a colour is never worth a
    /// popup or a second window. Dragging previews live, 确定 commits and writes settings.</summary>
    private void ShowColorPicker(ContentControl host, string current, Action<string> assign)
    {
        if (host.Content is Controls.ColorPickerPanel)
        {
            host.Content = null;
            return;
        }
        string original = current;
        var panel = new Controls.ColorPickerPanel(current);
        panel.ColorChanged += hex => Preview(s => assign(hex));
        panel.Committed += hex =>
        {
            Apply(s => assign(hex));
            host.Content = null;
            RefreshColorFields();
        };
        panel.Cancelled += () =>
        {
            // The preview is live, so 取消 has to put the colour back — otherwise cancelling would
            // leave behind a colour the user explicitly rejected.
            Apply(s => assign(original));
            host.Content = null;
            RefreshColorFields();
        };
        host.Content = panel;
    }

    private void OnPickTextColor(object sender, RoutedEventArgs e)
        => ShowColorPicker(textColorHost, _settings.TextColor, hex => _settings.TextColor = hex);

    /// <summary>QA: opens the inline picker so the no-popup layout can be screenshotted.</summary>
    internal void DebugOpenTextColorPicker()
    {
        OnPickTextColor(this, new RoutedEventArgs());
        textColorHost.BringIntoView();
    }

    private void OnPickGlowColor(object sender, RoutedEventArgs e)
        => ShowColorPicker(glowColorHost, _settings.GlowColor, hex => _settings.GlowColor = hex);

    /// <summary>QA: opens the inline glow-colour picker exactly like clicking its swatch.</summary>
    internal void DebugOpenGlowColorPicker()
    {
        OnPickGlowColor(this, new RoutedEventArgs());
        glowColorHost.BringIntoView();
    }

    /// <summary>QA: exactly what the inline picker fires while a colour is dragged.</summary>
    internal void DebugPreviewGlowColor(string hex) => Preview(s => s.GlowColor = hex);

    /// <summary>QA: the 确定 path (commit + repaint the swatches).</summary>
    internal void DebugCommitGlowColor(string hex)
    {
        Apply(s => s.GlowColor = hex);
        RefreshColorFields();
    }

    /// <summary>Live recolour without touching the settings file; used while a colour is dragged.</summary>
    private void Preview(Action<AppSettings> change)
    {
        change(_settings);
        UpdateLabels();
        QueueApply();
    }

    // ---------- backdrop ----------
    private void OnBackdropModeChecked(object sender, RoutedEventArgs e)
    {
        var mode = sender == rbBackdropImage ? BackdropMode.Image
                 : sender == rbBackdropColor ? BackdropMode.Color
                 : sender == rbBackdropNone ? BackdropMode.Transparent
                 : BackdropMode.Cover;
        Apply(s => s.BackdropMode = mode);
    }

    private void OnPickBackdropImage(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择歌词背景图",
            Filter = "图片|*.jpg;*.jpeg;*.png;*.webp;*.bmp",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog(this) != true) return;
        Apply(s =>
        {
            s.BackdropImage = dialog.FileName;
            s.BackdropMode = BackdropMode.Image;
        });
        rbBackdropImage.IsChecked = true;
    }

    private void OnPickBackdropColor(object sender, RoutedEventArgs e)
    {
        // Inline panel right under the colour row — no second window for a colour.
        if (backdropPickerHost.Content is Controls.ColorPickerPanel)
        {
            backdropPickerHost.Content = null;
            return;
        }
        var panel = new Controls.ColorPickerPanel(_settings.BackdropColor);
        panel.Committed += hex =>
        {
            ApplyBackdropColor(hex);
            backdropPickerHost.Content = null;
        };
        panel.Cancelled += () => backdropPickerHost.Content = null;
        backdropPickerHost.Content = panel;
    }

    private void ApplyBackdropColor(string hex)
    {
        Apply(s => { s.BackdropColor = hex; s.BackdropMode = BackdropMode.Color; });
        rbBackdropColor.IsChecked = true;
        txtBackdropColorHex.Text = "#" + hex;
        btnBackdropColor.Background = new System.Windows.Media.SolidColorBrush(ParseHex(hex, System.Windows.Media.Color.FromRgb(0x0E, 0x11, 0x18)));
    }

    private static System.Windows.Media.Color ParseHex(string hex, System.Windows.Media.Color fallback)
    {
        try
        {
            var text = (hex ?? "").Trim().TrimStart('#');
            if (text.Length == 6)
                return System.Windows.Media.Color.FromRgb(
                    Convert.ToByte(text[..2], 16),
                    Convert.ToByte(text.Substring(2, 2), 16),
                    Convert.ToByte(text.Substring(4, 2), 16));
        }
        catch { }
        return fallback;
    }

    private void OnSongOffsetLater(object sender, RoutedEventArgs e) => NudgeSongOffset(-100);
    private void OnSongOffsetEarlier(object sender, RoutedEventArgs e) => NudgeSongOffset(100);
    private void OnSongOffsetZero(object sender, RoutedEventArgs e)
    {
        _player?.ResetSongOffset();
        RefreshSongOffset();
    }

    private void NudgeSongOffset(int delta)
    {
        _player?.NudgeSongOffset(delta);
        RefreshSongOffset();
    }

    /// <summary>Shows the playing song and its own offset, refreshed while the window is open.</summary>
    private void RefreshSongOffset()
    {
        if (_player == null)
        {
            txtSongName.Text = "未在播放";
            txtSongOffset.Text = "0 ms";
            return;
        }
        var song = _player.Current;
        txtSongName.Text = song == null ? "未在播放" : $"{song.DisplayTitle} · {song.DisplayArtist}";
        long value = _player.CurrentSongOffset;
        txtSongOffset.Text = value == 0 ? "0 ms" : $"{value / 100.0:0.0}00 ms".Replace("0.000", "0.0");
        txtSongOffset.Text = $"{value} ms";
    }

    private void OnOffsetLater(object sender, RoutedEventArgs e) => NudgeOffset(-100);
    private void OnOffsetEarlier(object sender, RoutedEventArgs e) => NudgeOffset(100);
    private void OnOffsetZero(object sender, RoutedEventArgs e) => Apply(s => s.OffsetMs = 0);

    private void NudgeOffset(int delta)
    {
        Apply(s => s.OffsetMs = Math.Clamp(s.OffsetMs + delta, -60_000, 60_000));
        sldOffset.Value = _settings.OffsetMs;
    }

    private void OnReset(object sender, RoutedEventArgs e)
    {
        var defaults = new AppSettings();
        Copy(defaults, _settings);
        _initializing = true;
        LoadFromSettings();
        _initializing = false;
        _onApply(_settings);
    }

    private void OnDone(object sender, RoutedEventArgs e) => Close();
}
