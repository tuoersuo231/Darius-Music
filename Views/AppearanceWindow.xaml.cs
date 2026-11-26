using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CurrentLyrics.Services;

namespace CurrentLyrics.Views;

/// <summary>
/// 界面外观: the paper colour, the backdrop (a photo or the playing track's cover), its opacity and
/// frosting. Its own window — these apply to the whole app, not to the desktop lyrics.
/// </summary>
public partial class AppearanceWindow : Window
{
    private static readonly string[] SurfacePresets =
    {
        "FFFFFF", "E8EEF8", "F4EFE6", "E7F2EE", "F6EDF2", "EEF0F4",
        "0B0E14", "101725", "0A1512", "171021",
    };

    private static readonly string[] SurfaceNames =
    {
        "白", "雾蓝", "米白", "淡青", "藕粉", "浅灰",
        "墨黑", "夜蓝", "深墨绿", "深紫",
    };

    private readonly AppSettings _settings;
    private readonly Action<AppSettings> _onApply;
    private readonly Action? _onPreview;
    private bool _initializing = true;

    public AppearanceWindow(AppSettings settings, Action<AppSettings> onApply, Action? onPreview = null)
    {
        InitializeComponent();
        _settings = settings;
        _onApply = onApply;
        _onPreview = onPreview ?? (() => onApply(settings));

        for (int i = 0; i < SurfacePresets.Length; i++)
        {
            var hex = SurfacePresets[i];
            surfacePresets.Children.Add(Swatch(hex, SurfaceNames[i], s => s.ThemeSurface = hex));
        }
        // 透明：面板不再铺自己的底色，封面/背景图整面透出来。存成 8 位 AARRGGBB（"00RRGGBB"），
        // 颜色本身留着给文字和强调色算对比度。
        surfacePresets.Children.Add(TransparentSwatch());

        sldThemeOpacity.ValueChanged += (_, e) =>
        {
            if (_initializing) return;
            txtThemeOpacity.Text = $"{e.NewValue:0}%";
            Apply(s => s.ThemeOpacity = Math.Round(e.NewValue));
        };
        sldThemeBlur.ValueChanged += (_, e) =>
        {
            if (_initializing) return;
            txtThemeBlur.Text = $"{e.NewValue:0} px";
            Apply(s => s.ThemeBlur = Math.Round(e.NewValue));
        };
        chkMinimizeOnClose.Checked += (_, _) =>
        {
            if (!_initializing) Apply(s => s.MinimizeOnClose = true);
        };
        chkMinimizeOnClose.Unchecked += (_, _) =>
        {
            if (!_initializing) Apply(s => s.MinimizeOnClose = false);
        };

        txtVersion.Text = Services.UpdateService.CurrentVersion;
        txtUpdateFeed.Text = Services.UpdateService.ResolveFeed(_settings.UpdateFeed);
        LoadFromSettings();
        _initializing = false;
    }

    /// <summary>One colour chip; the chosen one gets a light ring.</summary>
    private Button Swatch(string hex, string name, Action<AppSettings> change)
    {
        var color = Services.Palette.Parse(hex);
        var button = new Button
        {
            Width = 34,
            Height = 26,
            Margin = new Thickness(0, 0, 8, 8),
            Cursor = System.Windows.Input.Cursors.Hand,
            ToolTip = name,
            Background = new SolidColorBrush(color),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0, 0, 0)),
            BorderThickness = new Thickness(1),
            Style = (Style)Application.Current.Resources["GhostButton"],
        };
        button.Click += (_, _) =>
        {
            Apply(change);
            RefreshFields();
        };
        return button;
    }

    /// <summary>The 透明 chip: keeps the current colour (it still drives the ink and the accent) but
    /// stores it with a zero alpha, which makes every panel paint nothing at all. The checkerboard is
    /// the usual "no colour" pattern — a plain light chip looked like one more white preset.</summary>
    private Button TransparentSwatch()
    {
        var light = Color.FromRgb(0xE9, 0xEB, 0xEF);
        var dark = Color.FromRgb(0xC9, 0xCE, 0xD6);
        var checker = new DrawingBrush
        {
            TileMode = TileMode.Tile,
            Viewport = new Rect(0, 0, 12, 12),
            ViewportUnits = BrushMappingMode.Absolute,
            Stretch = Stretch.None,
            Drawing = new DrawingGroup
            {
                Children =
                {
                    new GeometryDrawing(new SolidColorBrush(light), null, new RectangleGeometry(new Rect(0, 0, 12, 12))),
                    new GeometryDrawing(new SolidColorBrush(dark), null, new RectangleGeometry(new Rect(0, 0, 6, 6))),
                    new GeometryDrawing(new SolidColorBrush(dark), null, new RectangleGeometry(new Rect(6, 6, 6, 6))),
                },
            },
        };
        var button = new Button
        {
            Width = 56,
            Height = 26,
            Margin = new Thickness(0, 0, 8, 8),
            Cursor = System.Windows.Input.Cursors.Hand,
            Background = checker,
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0, 0, 0)),
            BorderThickness = new Thickness(1),
            Style = (Style)Application.Current.Resources["GhostButton"],
            Content = new TextBlock
            {
                Text = "透明",
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromRgb(0x22, 0x25, 0x2B)),
            },
        };
        button.Click += (_, _) =>
        {
            var rgb = Services.Palette.Normalize(_settings.ThemeSurface, Services.Palette.DefaultSurface);
            if (rgb.Length == 8) rgb = rgb.Substring(2);
            Apply(s => s.ThemeSurface = "00" + rgb);
            RefreshFields();
        };
        return button;
    }

    private void LoadFromSettings()
    {
        txtThemeOpacity.Text = $"{_settings.ThemeOpacity:0}%";
        txtThemeBlur.Text = $"{_settings.ThemeBlur:0} px";
        sldThemeOpacity.Value = Math.Clamp(_settings.ThemeOpacity, 0, 100);
        sldThemeBlur.Value = Math.Clamp(_settings.ThemeBlur, 0, 60);
        chkMinimizeOnClose.IsChecked = _settings.MinimizeOnClose;
        RefreshFields();
    }

    private void RefreshFields()
    {
        string surfaceHex = Services.Palette.Normalize(_settings.ThemeSurface, Services.Palette.DefaultSurface);
        txtSurfaceHex.Text = "#" + surfaceHex;
        var preview = Services.Palette.Parse(surfaceHex);
        // A fully transparent surface would make the preview chip invisible; show it faintly instead.
        if (preview.A < 0x30) preview = Color.FromArgb(0x30, preview.R, preview.G, preview.B);
        bdSurface.Background = new SolidColorBrush(preview);

        string image = (_settings.ThemeImage ?? "").Trim();
        bool cover = image.Equals(Services.Palette.CoverToken, StringComparison.OrdinalIgnoreCase);
        txtThemeImage.Text = cover
            ? "当前歌曲封面"
            : image.Length == 0 ? "未设置" : Path.GetFileName(image);

        // Opacity and frosting only mean something when something is behind the window.
        bool hasBackdrop = cover || (image.Length > 0 && File.Exists(image));
        sldThemeOpacity.IsEnabled = hasBackdrop;
        sldThemeBlur.IsEnabled = hasBackdrop;
        sldThemeOpacity.Opacity = hasBackdrop ? 1 : 0.4;
        sldThemeBlur.Opacity = hasBackdrop ? 1 : 0.4;
        txtThemeOpacity.Opacity = hasBackdrop ? 1 : 0.4;
        txtThemeBlur.Opacity = hasBackdrop ? 1 : 0.4;
    }

    /// <summary><paramref name="persist"/> is false while the picker is being dragged: the window
    /// recolours live, but the settings file is only written once the change is committed.</summary>
    private void Apply(Action<AppSettings> change, bool persist = true)
    {
        change(_settings);
        if (!persist)
        {
            _onPreview();
            return;
        }
        SettingsStore.Save(_settings);
        _onApply(_settings);
    }


    private void OnSurfaceSwatchClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        => ShowPickerFor("背景色", _settings.ThemeSurface, "ThemeSurface");

    private void ShowPickerFor(string title, string current, string field)
    {
        pickerTitle.Text = title;
        var panel = new Controls.ColorPickerPanel(current);
        panel.ColorChanged += hex => Apply(s =>
        {
            if (field == "ThemeAccent") s.ThemeAccent = hex;
            else s.ThemeSurface = hex;
        }, persist: false);
        panel.Committed += hex =>
        {
            Apply(s =>
            {
                if (field == "ThemeAccent") s.ThemeAccent = hex;
                else s.ThemeSurface = hex;
            });
            pickerCard.Visibility = Visibility.Collapsed;
            pickerHost.Content = null;
            RefreshFields();
        };
        panel.Cancelled += () =>
        {
            pickerCard.Visibility = Visibility.Collapsed;
            pickerHost.Content = null;
        };
        pickerHost.Content = panel;
        pickerCard.Visibility = Visibility.Visible;
    }

    private void OnCheckUpdate(object sender, RoutedEventArgs e) => _ = CheckUpdateAsync();

    /// <summary>真正的退出。开了「关闭窗口时最小化」以后标题栏的 ✕ 不再结束进程，这里是备用出口。</summary>
    private void OnQuit(object sender, RoutedEventArgs e) => Application.Current.Shutdown();

    /// <summary>完全离线的更新方式：U 盘/移动硬盘上带着 zip 过来，直接选文件替换，
    /// 不需要任何网络，也不需要更新源地址。</summary>
    private void OnUpdateFromFile(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择更新包",
            Filter = "更新包 (*.zip)|*.zip|所有文件 (*.*)|*.*",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog(this) != true) return;
        var name = System.IO.Path.GetFileName(dialog.FileName);
        var answer = MessageBox.Show(this,
            $"用「{name}」替换当前程序并重启？\n\n设置和歌单在 data 文件夹里，会保留。",
            "从文件更新", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (answer != MessageBoxResult.OK) return;
        try
        {
            txtUpdateStatus.Text = "正在重启更新…";
            Services.UpdateService.ApplyAndRestart(dialog.FileName);
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            txtUpdateStatus.Text = "更新失败：" + ex.GetType().Name;
        }
    }

    private async Task CheckUpdateAsync()
    {
        btnCheckUpdate.IsEnabled = false;
        txtUpdateStatus.Text = "检查中…";
        try
        {
            string feed = (txtUpdateFeed.Text ?? "").Trim();
            if (feed != _settings.UpdateFeed)
            {
                // 只在用户真的改了地址时才落盘；包自带的默认值不写进设置，换包才能生效。
                _settings.UpdateFeed = Services.UpdateService.ResolveFeed("") == feed ? "" : feed;
                SettingsStore.Save(_settings);
            }
            if (feed.Length == 0)
            {
                txtUpdateStatus.Text = "先填更新源（文件夹或 http 地址）";
                return;
            }
            var feeds = Services.UpdateService.ResolveFeeds(_settings.UpdateFeed);
            var (manifest, hit) = await Services.UpdateService.CheckAnyAsync(feeds);
            if (manifest == null)
            {
                txtUpdateStatus.Text = "没找到 latest.json";
                return;
            }
            feed = hit;
            if (!Services.UpdateService.IsNewerThanCurrent(manifest))
            {
                txtUpdateStatus.Text = $"已是最新 {Services.UpdateService.CurrentVersion}";
                return;
            }
            var answer = MessageBox.Show(this,
                $"发现新版本 {manifest.Version}（当前 {Services.UpdateService.CurrentVersion}）\n\n{manifest.Notes}\n\n下载并重启更新？",
                "检查更新", MessageBoxButton.OKCancel, MessageBoxImage.Question);
            if (answer != MessageBoxResult.OK) return;

            if (Services.VersionStore.Enabled)
            {
                var installProgress = new Progress<double>(p => txtUpdateStatus.Text = $"下载中 {p * 100:0}%");
                var installed = await Services.UpdateService.InstallAsync(feed, manifest, installProgress);
                txtUpdateStatus.Text = $"正在重启到 {installed}…";
                var launcher = Services.Layout.LauncherPath;
                if (System.IO.File.Exists(launcher)) System.Diagnostics.Process.Start(launcher);
                Application.Current.Shutdown();
                return;
            }

            var progress = new Progress<double>(p => txtUpdateStatus.Text = $"下载中 {p * 100:0}%");
            var zip = await Services.UpdateService.FetchAsync(feed, manifest, progress);
            txtUpdateStatus.Text = "正在重启更新…";
            Services.UpdateService.ApplyAndRestart(zip);
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            txtUpdateStatus.Text = "更新失败：" + ex.GetType().Name;
        }
        finally
        {
            btnCheckUpdate.IsEnabled = true;
        }
    }

    private void OnUseThemeCover(object sender, RoutedEventArgs e)
    {
        Apply(s => s.ThemeImage = Services.Palette.CoverToken);
        RefreshFields();
    }

    private void OnPickThemeImage(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择界面背景图",
            Filter = "图片|*.jpg;*.jpeg;*.png;*.webp;*.bmp",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog(this) != true) return;
        Apply(s => s.ThemeImage = dialog.FileName);
        RefreshFields();
    }

    private void OnClearThemeImage(object sender, RoutedEventArgs e)
    {
        Apply(s => s.ThemeImage = "");
        RefreshFields();
    }
}
