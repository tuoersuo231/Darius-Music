using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using CurrentLyrics.Models;
using CurrentLyrics.Services;

namespace CurrentLyrics.Views;

public partial class MainWindow : Window
{
    private readonly PlayerController _player;
    private readonly AppSettings _settings;
    private readonly HotkeyManager _hotkeys = new();
    private DesktopLyricsWindow? _desktop;
    private SettingsWindow? _settingsWindow;
    private readonly System.Collections.ObjectModel.ObservableCollection<Song> _view = new();
    private bool _suppressPlaylistEvent;
    private bool _suppressToggle;
    private bool _libraryActive;
    private bool _wasOnlineView;
    private DispatcherTimer? _onlineSearchTimer;
    /// <summary>Last online keyword, kept while the box is cleared for local filtering.</summary>
    private string _savedOnlineKeyword = "";

    public MainWindow(PlayerController player)
    {
        InitializeComponent();
        _player = player;
        _settings = player.Settings;

        // A throwaway/QA instance reads its data from a custom folder; say so in the title bar so it
        // is never mistaken for the real one (two identical windows side by side is how wrong
        // conclusions get made).
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DARIUS_DATA_DIR")))
            Title = "Darius Music · 测试环境";

        // 背景色=透明 → 真的能看见下层应用。只有一个可靠做法：窗口必须是「分层窗口」
        // （AllowsTransparency），DWM 才会保留每像素 alpha；普通窗口 + accent 模糊在本机实测
        // 客户区仍是黑的。分层窗口要求 WindowStyle=None，所以这里自己补拖拽和窗口按钮。
        _glassMode = Services.Palette.SurfaceAlpha(
            Services.Palette.Normalize(_settings.ThemeSurface, Services.Palette.DefaultSurface)) < 0xFF;
        if (_glassMode) EnterGlassMode();

        _player.StateChanged += OnStateChanged;
        // RegisterHotKey needs a real HWND, so the global chords are (re)applied as soon as the handle
        // exists — OnLoaded also calls ApplyHotkeys(), which is idempotent.
        SourceInitialized += (_, _) =>
        {
            _hotkeys.Attach(this);
            ApplyHotkeys();
        };
        // Slider 的类处理器先吃掉 PreviewMouseLeftButtonDown，用 += 挂的处理器收不到按下，
        // _seeking 永远是 false，松手时 EndSeek 就提前返回。统一用 handledEventsToo: true。
        sldProgress.AddHandler(UIElement.PreviewMouseLeftButtonDownEvent,
            new System.Windows.Input.MouseButtonEventHandler((_, _) => _seeking = true), true);
        sldProgress.AddHandler(UIElement.PreviewMouseLeftButtonUpEvent,
            new System.Windows.Input.MouseButtonEventHandler((_, _) => EndSeek()), true);
        sldProgress.AddHandler(UIElement.LostMouseCaptureEvent,
            new System.Windows.Input.MouseEventHandler((_, _) => EndSeek()), true);
        sldVolume.AddHandler(UIElement.PreviewMouseLeftButtonDownEvent,
            new System.Windows.Input.MouseButtonEventHandler((_, _) => _volumeDragging = true), true);
        sldVolume.AddHandler(UIElement.PreviewMouseLeftButtonUpEvent,
            new System.Windows.Input.MouseButtonEventHandler((_, _) => EndVolume()), true);
        sldVolume.AddHandler(UIElement.LostMouseCaptureEvent,
            new System.Windows.Input.MouseEventHandler((_, _) => EndVolume()), true);
        System.Windows.Media.CompositionTarget.Rendering += OnFrameTick;
        _player.TrackChanged += OnTrackChanged;
        _player.CoverChanged += OnCoverArrived;
        _player.ViewChanged += OnViewChanged;
        _player.Tick += OnTick;

        // The backdrop picture is sized from the window and the picture's own pixels, so a resize has to
        // redo that geometry — otherwise the framing drifts away from the no-blur case.
        SizeChanged += (_, _) =>
        {
            if (IsLoaded) ApplyThemeImageGeometry(Math.Clamp(_settings.ThemeBlur, 0, 60));
        };
        themeBackdrop.SizeChanged += (_, _) =>
        {
            if (IsLoaded) ApplyThemeImageGeometry(Math.Clamp(_settings.ThemeBlur, 0, 60));
        };

        // Typing in the online view runs a server search; wait for the typing to stop so one keyword
        // does not become one request per keystroke.
        _onlineSearchTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(450) };
        _onlineSearchTimer.Tick += async (_, _) =>
        {
            _onlineSearchTimer.Stop();
            if (!_player.IsOnlineView) return;
            _savedOnlineKeyword = txtSearch.Text?.Trim() ?? "";
            await _player.ShowOnlineSearchAsync(txtSearch.Text);
        };

        // OnLoaded is wired from MainWindow.xaml; adding it again here ran it TWICE, which made every
        // hotkey fire twice (F8 flipped the switch on and straight back off, Ctrl+Q likewise, P played
        // and paused) — the user's "快捷键根本没生效".


        // Dragging or resizing the window runs a modal loop on this thread. Holding the page's lyric
        // painting for the duration is what makes the window follow the cursor instead of catching up
        // in steps (the page repaints eight lines plus their glow, several milliseconds each frame).
        SourceInitialized += (_, _) =>
        {
            if (PresentationSource.FromVisual(this) is System.Windows.Interop.HwndSource source)
                source.AddHook(WindowLoopHook);
        };

        Closed += (_, _) =>
        {
            // Always tear the overlay down first, and never let a cleanup exception keep the
            // process (and a frozen lyric frame) alive.
            try
            {
                // 记住「上次听到哪」，下次启动在后台恢复（只记本地文件）。
                _player.CaptureResume();
                // The settings save is debounced while a slider is dragged; flush it before exit.
                Services.SettingsStore.Save(_settings);
                _tray?.Dispose();
                _tray = null;
                _desktop?.Close();
                _settingsWindow?.Close();
                _downloadWindow?.Close();
                _player.Dispose();
            }
            catch { }
            finally
            {
                Application.Current.Shutdown();
            }
        };

        // Logging off / shutting Windows down must not be blocked by the minimize-on-close rule.
        Application.Current.SessionEnding += (_, _) => _forceExit = true;
    }

    /// <summary>True once something really wants the process gone (update restart, session end).</summary>
    private bool _forceExit;

    private TrayIcon? _tray;

    /// <summary>托盘图标：关窗口以后程序还在，得让用户看得见、也能从这里退出。</summary>
    private void EnsureTray()
    {
        if (_tray != null) return;
        try
        {
            _tray = new TrayIcon(ShowFromTray, ExitFromTray);
        }
        catch (Exception ex)
        {
            // 托盘建不起来也不能让「关窗口」变成崩溃：退回最小化，至少程序还在。
            _trayError = ex.GetType().Name + ": " + ex.Message;
        }
    }

    private string _trayError = "";

    private void ShowFromTray()
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            Show();
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Activate();
            Topmost = Topmost;      // nudge it in front of the窗口 that was covering it
            UpdateOverlayForFocus();
        }));
    }

    private void ExitFromTray()
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _forceExit = true;
            Close();
        }));
    }

    /// <summary>真退出，绕过「关闭窗口最小化」（换权限重启、托盘「退出」都走这里）。</summary>
    public void ForceExit()
    {
        _forceExit = true;
        Application.Current.Shutdown();
    }

    /// <summary>关闭窗口在默认设置下只是缩到任务栏：歌继续放，桌面歌词也照旧。
    /// 界面外观 →「关闭窗口时最小化到任务栏」可以关掉，关掉就是真退出（老行为）。</summary>
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!_forceExit && _settings.MinimizeOnClose)
        {
            e.Cancel = true;
            EnsureTray();
            if (_tray != null) Hide();                 // 缩到托盘：任务栏按钮也收起来，只剩右下角图标
            else WindowState = WindowState.Minimized;  // 托盘没起来就退回最小化
            _tray?.ShowFirstHint();
            return;
        }
        base.OnClosing(e);
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        sldVolume.Value = _settings.Volume;
        // 音量条：点哪儿就跳到哪儿，按住还能继续拖（默认只按 LargeChange 跳一格）。
        sldVolume.IsMoveToPointEnabled = true;
        if (txtUpdateHint != null) txtUpdateHint.Text = "v" + Services.UpdateService.CurrentVersion;
        SetLyricsCheckbox(_settings.Enabled);
        btnPlayAll.IsEnabled = false;
        ApplySpectrumLayer();
        ApplyAppearance();

        if (_settings.Enabled)
        {
            // Prepare the overlay, but keep it off the app's own window: it becomes visible as
            // soon as this window loses focus (i.e. when the user goes back to the desktop).
            EnsureDesktopLyrics();
            UpdateOverlayForFocus();
        }

        lstSongs.ItemsSource = _view;
        _player.PlaylistsChanged += RefreshPlaylists;
        _player.ModeChanged += UpdatePlayModeUi;
        UpdatePlayModeUi();
        lstPlaylists.ItemsSource = _player.Playlists;

        // 快捷键：修饰键组合和 F1~F24 走系统级注册；只按字母/数字的那些留给窗口自己处理。
        _hotkeys.Attach(this);
        _hotkeys.Pressed -= OnHotkey;   // never stack two handlers on one chord
        _hotkeys.Pressed += OnHotkey;
        ApplyHotkeys();
        PreviewKeyDown += OnWindowPreviewKeyDown;

        // Local library only: no account, no WSA.
        try { await _player.ShowLibraryAsync(); }
        catch { }
        // 上次听着的那首歌：同一套界面里把歌、歌单、进度条放回原位，不弹任何窗口。
        TryRestoreLastSession();
    }

    /// <summary>
    /// 恢复上次的会话：先切回当时那个歌单，再把歌放进播放条、把进度条推上次的位置（暂停状态，
    /// 不自动播放）。整段在后台完成；文件不在了就静默跳过。
    /// </summary>
    private async void TryRestoreLastSession()
    {
        try
        {
            var path = (_settings.ResumeSongPath ?? "").Trim();
            if (path.Length == 0 || !System.IO.File.Exists(path)) return;
            var playlistName = (_settings.ResumePlaylist ?? "").Trim();
            if (playlistName.Length > 0)
            {
                var playlist = _player.Playlists.FirstOrDefault(p =>
                    string.Equals(p.Name, playlistName, StringComparison.OrdinalIgnoreCase));
                if (playlist != null && !ReferenceEquals(_player.Playlists, null))
                    await _player.ShowPlaylistAsync(playlist);
            }
            if (_player.Songs.Count == 0) await _player.ShowLibraryAsync();
            var song = _player.Songs.FirstOrDefault(s => string.Equals(s.Path, path, StringComparison.OrdinalIgnoreCase));
            if (song == null)
            {
                var name = System.IO.Path.GetFileName(path);
                song = _player.Songs.FirstOrDefault(s =>
                    string.Equals(System.IO.Path.GetFileName(s.Path), name, StringComparison.OrdinalIgnoreCase));
            }
            if (song == null) return;
            _player.PrepareResume(song, _settings.ResumePositionMs);
            var seconds = Math.Clamp(_settings.ResumePositionMs / 1000.0, 0, Math.Max(0, sldProgress.Maximum));
            // 这一步只是把进度条摆到上次的位置，不能当成用户拖动：以前它触发 OnProgressChanged →
            // Seek()，而此时媒体还是「打开但没播过」的暂停态，Position 会被静默忽略（seek.log 里
            // took=False），锚点又被改回 0 —— 用户看到的就是「恢复后进度条是 0:00、按播放没反应」。
            _restoringResume = true;
            try { sldProgress.Value = seconds; }
            finally { _restoringResume = false; }
            txtPos.Text = TimeSpan.FromSeconds(seconds).ToString(seconds >= 3600 ? @"h\:mm\:ss" : @"m\:ss");
        }
        catch { }
    }

    private void OnWindowPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        var key = HotkeyText.NormalizeKey(e);
        // Diagnostics only: every legal chord is global now, so nothing here can consume a key.
        _hotkeys.NoteLocalKey(key, System.Windows.Input.Keyboard.Modifiers);
        // F11：最大化 / 还原。透明背景的玻璃窗口没有系统标题栏（也就没有还原按钮），拖最大化窗口
        // 也不起作用，所以给一个键盘出口。只在窗口自己在前台时管，不抢别的程序的 F11。
        if (key == System.Windows.Input.Key.F11 &&
            System.Windows.Input.Keyboard.Modifiers == System.Windows.Input.ModifierKeys.None)
        {
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            e.Handled = true;
        }
    }

    /// <summary>Pushes the five bindings from settings into the registrar. Returns the actions whose
    /// chord could not be taken (another application already owns it).</summary>
    private IReadOnlyList<HotkeyAction> ApplyHotkeys()
    {
        // OnLoaded already attached, but a retry here keeps the window's message hook alive if the
        // handle was not ready yet (Attach is a no-op once it is).
        _hotkeys.Attach(this);
        var map = new Dictionary<HotkeyAction, string>
        {
            [HotkeyAction.AboveApps] = _settings.KeyAboveApps,
            [HotkeyAction.ToggleLyrics] = _settings.KeyLyrics,
            [HotkeyAction.PlayPause] = _settings.KeyPlayPause,
            [HotkeyAction.Next] = _settings.KeyNext,
            [HotkeyAction.Previous] = _settings.KeyPrevious,
            [HotkeyAction.TransparentBg] = _settings.KeyTransparentBg,
        };
        _hotkeys.Apply(map);
        return _hotkeys.Failed.ToList();
    }

    /// <summary>One place where a hotkey turns into the same call a button would make.</summary>
    private void OnHotkey(HotkeyAction action)
    {
        Services.HotkeyManager.Log($"ACTION {action} active={IsActive}");
        try
        {
            switch (action)
            {
                case HotkeyAction.AboveApps:
                    _settings.OverlayAboveApps = !_settings.OverlayAboveApps;
                    SettingsStore.Save(_settings);
                    _desktop?.ApplySettings(_settings);
                    // 「置顶」在普通窗口下看不出任何变化（歌词本来就是 topmost 的，这一项只对全屏/其他
                    // 置顶窗口起作用），所以必须给一句确认，否则用户只能认为键没反应。
                    ShowHotkeyToast(_settings.OverlayAboveApps
                        ? "已置顶：歌词显示在其他应用上方"
                        : "已取消置顶：歌词回到普通窗口层级");
                    break;
                case HotkeyAction.ToggleLyrics:
                    SetLyricsEnabled(!_settings.Enabled, save: true);
                    break;
                case HotkeyAction.PlayPause:
                    if (_player.Current == null) ShowHotkeyToast("还没有正在播放的歌");
                    else _player.Toggle();
                    break;
                case HotkeyAction.Next:
                    _player.Next();
                    break;
                case HotkeyAction.Previous:
                    _player.Previous();
                    break;
                case HotkeyAction.TransparentBg:
                {
                    // 00RRGGBB ↔ RRGGBB：颜色本身留着，只切 alpha。
                    var surface = Services.Palette.Normalize(_settings.ThemeSurface, Services.Palette.DefaultSurface);
                    string rgb = surface.Length == 8 ? surface.Substring(2) : surface;
                    bool wasClear = Services.Palette.SurfaceAlpha(surface) < 0xFF;
                    _settings.ThemeSurface = wasClear ? rgb : "00" + rgb;
                    SettingsStore.Save(_settings);
                    ApplyAppearance();
                    if (!_glassMode) ShowHotkeyToast("透明背景需要重启后生效");
                    else ShowHotkeyToast(wasClear ? "背景：实色" : "背景：全透明");
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            _hotkeyLog.Add($"{DateTime.Now:HH:mm:ss.fff} {action} THREW {ex.GetType().Name}: {ex.Message}");
            return;
        }
        _hotkeyLog.Add($"{DateTime.Now:HH:mm:ss.fff} {action} enabled={_settings.Enabled} " +
                       $"above={_settings.OverlayAboveApps} playing={_player.PlaybackState} cur={_player.Current?.Title}");
    }

    private readonly List<string> _hotkeyLog = new();

    private Border? _toast;
    private DispatcherTimer? _toastTimer;

    /// <summary>Confirmation for a hotkey whose effect is not visible on screen (置顶 / 没有歌可放).
    /// Prefers the desktop overlay when it is on screen — that is what the user is looking at while
    /// pressing global keys — and falls back to this window.</summary>
    internal void ShowHotkeyToast(string text)
    {
        if (_desktop is { IsVisible: true })
        {
            _desktop.ShowToast(text);
            return;
        }
        ShowToast(text);
    }

    private void ShowToast(string text)
    {
        if (Content is not Grid root) return;
        if (_toast == null)
        {
            var label = new TextBlock
            {
                FontSize = 12.5,
                Foreground = Brushes.White,
                TextAlignment = TextAlignment.Center,
            };
            _toast = new Border
            {
                Child = label,
                Background = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromArgb(0xE0, 0x16, 0x14, 0x1E)),
                BorderBrush = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(14, 7, 14, 7),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, 0, 92),
                IsHitTestVisible = false,
                Opacity = 0,
            };
            Grid.SetColumnSpan(_toast, 2);
            Panel.SetZIndex(_toast, 60);
            root.Children.Add(_toast);
            _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
            _toastTimer.Tick += (_, _) =>
            {
                _toastTimer!.Stop();
                if (_toast != null) _toast.Opacity = 0;
            };
        }
        ((TextBlock)_toast.Child).Text = text;
        _toast.Opacity = 1;
        _toastTimer!.Stop();
        _toastTimer.Start();
    }

    /// <summary>The HWND the global chords were registered on (QA posts WM_HOTKEY straight to it).</summary>
    public IntPtr DebugHotkeyHandle => _hotkeys.Handle;

    /// <summary>QA (--key-qa): registration state plus every action that actually fired, so a hotkey
    /// that is bound but silent can be told apart from one that never reaches the app.</summary>
    /// <summary>QA: 进度条与播放器对不上时用这个看（恢复进度那个 bug）。</summary>
    public string DebugProgress =>
        $"slider={sldProgress.Value:0.0}s max={sldProgress.Maximum:0.0}s raw={_player.RawPositionMs:0}ms " +
        $"playing={_player.PlaybackState} seeking={_seeking}";

    public string DebugHotkeys =>
        $"registered={_hotkeys.RegisteredCount} failed={_hotkeys.Failed.Count} {_hotkeys.HookState} " +
        $"hwnd={_hotkeys.Handle} hookCalls={_hotkeys.HookCalls} msgs=[{string.Join(";", _hotkeys.MessageLog.TakeLast(4))}] " +
        $"codes=[{string.Join(",", _hotkeys.FailCodes.Select(kv => $"{kv.Key}={kv.Value}"))}] " +
        $"keys=[{_hotkeys.LastLocalKey}] log=[{string.Join(" | ", _hotkeyLog)}]";

    /// <summary>Programmatic equivalent of the 桌面歌词 switch, for the hotkey path.</summary>
    private void SetLyricsEnabled(bool enabled, bool save)
    {
        _settings.Enabled = enabled;
        SetLyricsCheckbox(enabled);
        if (save) SettingsStore.Save(_settings);
        if (enabled) ShowDesktopLyrics();
        else _desktop?.Hide();
        UpdateOverlayForFocus();
    }

    private bool _glassMode;

    /// <summary>Layered-window mode, used when 背景色=透明. Borderless (WPF requires it), resizable
    /// through WindowChrome, with a hand-rolled drag strip and window buttons.</summary>
    private void EnterGlassMode()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        // 1/255 alpha, not 0: a layered window hit-tests per pixel, so a fully transparent client lets
        // every click fall through to the app below (the window could not even be dragged).
        Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0));
        ResizeMode = ResizeMode.CanResize;
        System.Windows.Shell.WindowChrome.SetWindowChrome(this, new System.Windows.Shell.WindowChrome
        {
            CaptionHeight = 0,
            ResizeBorderThickness = new Thickness(7),
            GlassFrameThickness = new Thickness(0),
            CornerRadius = new CornerRadius(0),
            UseAeroCaptionButtons = false,
        });

        PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState != MouseButtonState.Pressed) return;
            if (e.GetPosition(this).Y > 46) return;
            // Buttons / search box / sliders keep their own clicks.
            if (e.OriginalSource is DependencyObject source && HasClickableAncestor(source)) return;
            if (e.ClickCount == 2)
            {
                WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
                return;
            }
            try { DragMove(); } catch { }
        };

        if (Content is Grid root)
        {
            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 6, 10, 0),
            };
            buttons.Children.Add(WindowButton("—", () =>
            {
                WindowState = WindowState.Minimized;
            }));
            buttons.Children.Add(WindowButton("✕", Close));
            Panel.SetZIndex(buttons, 200);
            root.Children.Add(buttons);
        }
    }

    private Button WindowButton(string glyph, Action action)
    {
        var button = new Button
        {
            Content = new TextBlock { Text = glyph, FontSize = 13 },
            Width = 30,
            Height = 24,
            Margin = new Thickness(4, 0, 0, 0),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = (Brush)FindResource("TextDimBrush"),
            Cursor = System.Windows.Input.Cursors.Hand,
            Focusable = false,
        };
        button.Click += (_, _) => action();
        return button;
    }

    private static bool HasClickableAncestor(DependencyObject source)
    {
        for (DependencyObject? node = source; node != null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is Button or TextBox or Slider or ComboBox or CheckBox or ScrollBar or Thumb
                or ListBoxItem or MenuItem or ToggleButton) return true;
            if (node is Window) break;
        }
        return false;
    }

    public bool DebugGlassMode => _glassMode;

    private HotkeyWindow? _hotkeyWindow;

    private void OnOpenHotkeysClick(object sender, RoutedEventArgs e)
    {
        if (_hotkeyWindow is { IsVisible: true })
        {
            _hotkeyWindow.Activate();
            return;
        }
        _hotkeyWindow = new HotkeyWindow(_settings, () =>
        {
            SettingsStore.Save(_settings);
            return ApplyHotkeys();
        }) { Owner = this };
        // While the page is recording, the keyboard hook must not eat the keys being captured.
        _hotkeys.HookEnabled = false;
        _hotkeyWindow.Closed += (_, _) =>
        {
            _hotkeys.HookEnabled = true;
            _hotkeyWindow = null;
        };
        _hotkeyWindow.Show();
    }

    /// <summary>Switch state is the only thing that decides whether the overlay is on screen.</summary>
    private void UpdateOverlayForFocus()
    {
        if (_desktop == null) return;
        if (_settings.Enabled)
        {
            if (!_desktop.IsVisible) _desktop.Show();
        }
        else
        {
            _desktop.Hide();
        }
    }

    /// <summary>Programmatic switch update must not be mistaken for a user click.</summary>
    private void SetLyricsCheckbox(bool enabled)
    {
        _suppressToggle = true;
        chkLyrics.IsChecked = enabled;
        _suppressToggle = false;
    }


    // ---------- playlists ----------
    private const string AudioFilter =
        "音频文件|*.mp3;*.flac;*.m4a;*.aac;*.wav;*.ogg;*.opus;*.wma;*.aiff;*.aif;*.ape|所有文件|*.*";

    private void RefreshPlaylists()
    {
        Dispatcher.Invoke(() =>
        {
            // Counts follow the files on disk, so the sidebar never claims songs the view cannot show.
            _player.RefreshPlaylistCounts();
            foreach (var playlist in _player.Playlists)
                playlist.CoverImage ??= _player.ResolvePlaylistCover(playlist);
            lstPlaylists.Items.Refresh();
        });
    }

    private void OnNewPlaylistClick(object sender, RoutedEventArgs e)
    {
        var prompt = new PromptWindow("新建歌单", "新建歌单", "之后可以右键歌单重命名、换封面") { Owner = this };
        if (prompt.ShowDialog() != true) return;
        var playlist = _player.CreatePlaylist(prompt.Value);
        RefreshPlaylists();
        lstPlaylists.SelectedItem = playlist;
    }

    private async void OnPlaylistSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_refreshingSidebar) return;
        if (lstPlaylists.SelectedItem is not Playlist playlist) return;
        await OpenPlaylistAsync(playlist);
    }

    /// <summary>Clicking the row that is already open re-reads it: files may have been added, moved
    /// or renamed since it was opened. SelectionChanged never fires for the already-selected row, so
    /// the second click used to do nothing at all (user report, 2026-11-26).</summary>
    private async void OnPlaylistItemClick(object sender, MouseButtonEventArgs e)
    {
        _playlistClickLog = $"entered button={e.ChangedButton} src={e.OriginalSource?.GetType().Name}";
        if (e.ChangedButton != System.Windows.Input.MouseButton.Left) return;
        if (e.OriginalSource is not DependencyObject source) { _playlistClickLog += " bail=no-source"; return; }
        if (ItemsControl.ContainerFromElement(lstPlaylists, source) is not ListBoxItem row)
        {
            _playlistClickLog += " bail=no-container";
            return;
        }
        if (row.DataContext is not Playlist clicked) { _playlistClickLog += " bail=not-playlist"; return; }
        // A different row is left to SelectionChanged; this branch is the "same row again" case.
        if (!ReferenceEquals(clicked, lstPlaylists.SelectedItem))
        {
            _playlistClickLog += " bail=other-row";
            return;
        }
        _playlistClickLog += " -> reopen";
        e.Handled = true;
        await OpenPlaylistAsync(clicked);
    }

    private string _playlistClickLog = "";

    /// <summary>QA: what the re-click handler saw and decided.</summary>
    internal string DebugPlaylistClickLog => _playlistClickLog;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    /// <summary>QA: open the row menu through the real window message path (WM_RBUTTONDOWN/UP), which
    /// is what a right-click delivers — the only way the submenu behaves as it does for the user.</summary>
    internal void DebugPostRightClick(double x, double y)
    {
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this);
        int px = (int)Math.Round(x * dpi.DpiScaleX);
        int py = (int)Math.Round(y * dpi.DpiScaleY);
        int packed = (py << 16) | (px & 0xFFFF);
        PostMessage(hwnd, 0x0204, (IntPtr)2, (IntPtr)packed);   // WM_RBUTTONDOWN
        PostMessage(hwnd, 0x0205, IntPtr.Zero, (IntPtr)packed); // WM_RBUTTONUP
    }

    /// <summary>QA: is the row menu open, and what does the picker entry look like.</summary>
    internal string DebugSongMenuState()
    {
        var items = lstSongs.ContextMenu?.Items.OfType<MenuItem>().Select(i => i.Header as string ?? "?") ?? Enumerable.Empty<string>();
        return $"menuOpen={lstSongs.ContextMenu?.IsOpen == true} " +
               $"addHeader='{miAddPlaylist.Header}' addVisible={miAddPlaylist.Visibility} " +
               $"items=[{string.Join(" | ", items)}]";
    }

    /// <summary>QA: open the playlist picker for the first row and report its rows.</summary>
    internal string DebugOpenPlaylistPicker()
    {
        var song = _view.FirstOrDefault();
        if (song == null) return "no rows";
        var picker = new PlaylistPickerWindow(song.DisplayTitle, _player.Playlists) { Owner = this };
        picker.Show();
        _debugPicker = picker;
        return $"song='{song.DisplayTitle}' rows=[{string.Join(" | ", picker.DebugRows())}]";
    }

    private PlaylistPickerWindow? _debugPicker;

    /// <summary>QA: the picker window currently on screen.</summary>
    internal PlaylistPickerWindow? DebugPickerWindow => _debugPicker;

    /// <summary>QA: click the nth row of the open picker.</summary>
    internal string DebugPickRow(int index)
    {
        if (_debugPicker == null) return "picker not open";
        _debugPicker.DebugPick(index);
        var picked = _debugPicker.Selected;
        var created = _debugPicker.CreateDefault;
        var titles = string.Join(" | ", _debugPicker.DebugRows());
        _debugPicker.Close();
        _debugPicker = null;
        return $"picked='{picked?.Name ?? "(default)"}' createDefault={created} had=[{titles}]";
    }

    /// <summary>QA: close any open menus.</summary>
    internal void DebugCloseMenus()
    {
        if (lstSongs.ContextMenu is { } menu) menu.IsOpen = false;
    }

    /// <summary>QA: 另存到 a local row without the folder dialog.</summary>
    internal string DebugSaveLocalTo(int rowIndex, string folder)
    {
        if (rowIndex < 0 || rowIndex >= _view.Count) return $"index {rowIndex} out of range";
        var song = _view[rowIndex];
        if (song.Source != SongSource.Local) return "not a local row";
        var (ok, message) = CopySongWithSidecars(song.Path, folder);
        return ok ? $"ok file='{message}'" : "failed: " + message;
    }

    /// <summary>QA: deliver a real left button click through the window message path (what a mouse
    /// actually produces) at a point in this window's DIP coordinates.</summary>
    internal void DebugPostLeftClick(double x, double y)
    {
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this);
        int px = (int)Math.Round(x * dpi.DpiScaleX);
        int py = (int)Math.Round(y * dpi.DpiScaleY);
        int packed = (py << 16) | (px & 0xFFFF);
        PostMessage(hwnd, 0x0201, (IntPtr)1, (IntPtr)packed);   // WM_LBUTTONDOWN
        PostMessage(hwnd, 0x0202, IntPtr.Zero, (IntPtr)packed); // WM_LBUTTONUP
    }

    /// <summary>Every path that opens a playlist goes through here, so the QA counter sees them all.</summary>
    private async Task OpenPlaylistAsync(Playlist playlist)
    {
        _playlistOpens++;
        await _player.ShowPlaylistAsync(playlist);
        // The row's "N 首" comes from a computed property; rebinding the row here keeps the sidebar
        // in step with the rows that were just re-read from disk.
        if (_refreshingSidebar) return;
        _refreshingSidebar = true;
        try
        {
            var keep = lstPlaylists.SelectedItem;
            lstPlaylists.Items.Refresh();
            if (!ReferenceEquals(lstPlaylists.SelectedItem, keep)) lstPlaylists.SelectedItem = keep;
        }
        catch (Exception ex) { _playlistClickLog += " refresh-failed:" + ex.GetType().Name + ":" + ex.Message; }
        finally { _refreshingSidebar = false; }
    }

    private bool _refreshingSidebar;

    private int _playlistOpens;

    /// <summary>QA: how many times a playlist was opened/refreshed.</summary>
    internal int DebugPlaylistOpens => _playlistOpens;

    private void OnPlaylistAddSongs(object sender, RoutedEventArgs e)
    {
        var playlist = lstPlaylists.SelectedItem as Playlist ?? _player.Playlists.LastOrDefault();
        bool created = false;
        if (playlist == null)
        {
            // With no playlists this used to be a dead end ("请先新建一个歌单。"): create 默认歌单 and
            // let the picker continue, the same way「添加到歌单…」does.
            playlist = _player.CreatePlaylist("默认歌单");
            created = true;
            RefreshPlaylists();
            lstPlaylists.SelectedItem = playlist;
        }
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = $"添加到「{playlist.Name}」",
            Multiselect = true,
            Filter = AudioFilter,
            CheckFileExists = true,
        };
        if (dialog.ShowDialog(this) != true) return;
        var added = _player.AddToPlaylist(playlist, dialog.FileNames);
        RefreshPlaylists();
        txtViewSub.Text = $"已添加 {added} 首到「{playlist.Name}」" + (created ? "（新建）" : "");
    }

    private void OnPlaylistRename(object sender, RoutedEventArgs e)
    {
        if (lstPlaylists.SelectedItem is not Playlist playlist) return;
        var prompt = new PromptWindow("重命名歌单", playlist.Name) { Owner = this };
        if (prompt.ShowDialog() != true) return;
        _player.RenamePlaylist(playlist, prompt.Value);
        RefreshPlaylists();
    }

    private void OnPlaylistChangeCover(object sender, RoutedEventArgs e)
    {
        if (lstPlaylists.SelectedItem is not Playlist playlist) return;
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择封面图片",
            Filter = "图片|*.jpg;*.jpeg;*.png;*.webp;*.bmp",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog(this) != true) return;
        _player.SetPlaylistCover(playlist, dialog.FileName);
        RefreshPlaylists();
    }

    /// <summary>Drops entries whose file no longer exists (moved/deleted outside the app).</summary>
    private async void OnPlaylistCleanMissing(object sender, RoutedEventArgs e)
    {
        if (lstPlaylists.SelectedItem is not Playlist playlist) return;
        bool wasActive = ReferenceEquals(playlist, _player.PlaylistInView);
        int removed = _player.CleanMissing(playlist);
        RefreshPlaylists();
        txtViewSub.Text = removed > 0
            ? $"已从「{playlist.Name}」清掉 {removed} 个失效条目"
            : $"「{playlist.Name}」没有失效条目";
        if (removed > 0 && wasActive) await _player.ShowPlaylistAsync(playlist);
    }

    private void OnPlaylistDelete(object sender, RoutedEventArgs e)
    {
        if (lstPlaylists.SelectedItem is not Playlist playlist) return;
        if (MessageBox.Show(this, $"删除歌单「{playlist.Name}」？只删歌单，不动音频文件。",
                "删除歌单", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        _player.DeletePlaylist(playlist);
        RefreshPlaylists();
    }

    // ---------- removing a row (playlist entry / library path / scanned file) ----------
    private PlayerController.RemovedSong? _pendingRemoval;
    private DispatcherTimer? _undoTimer;
    private DispatcherTimer? _hideTimer;

    private void OnRowRemoveClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;                       // keep the click away from selection / double-click play
        if (sender is FrameworkElement { DataContext: Song song }) RemoveSong(song);
    }

    private void RemoveSong(Song song)
    {
        // Online rows are a search answer, not something that was ever added: dropping one only
        // affects the list on screen and must not touch the library or the hidden-files list.
        if (_player.IsOnlineView)
        {
            _player.Songs.Remove(song);
            ApplyFilter();
            bool empty = _player.Songs.Count == 0;
            emptyState.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
            lstSongs.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
            return;
        }
        string scope = _player.RemoveScopeText;
        var record = _player.RemoveSong(song);
        if (record == null) return;
        _pendingRemoval = record;
        ShowUndo($"{scope}《{song.DisplayTitle}》");
    }

    private void ShowUndo(string text)
    {
        txtUndo.Text = text;
        // Opacity is set outright rather than animated: a stalled render clock would otherwise
        // leave the bar invisible even though it is on screen.
        undoBar.BeginAnimation(OpacityProperty, null);
        undoBar.Opacity = 1;
        undoBar.Visibility = Visibility.Visible;
        _undoTimer?.Stop();
        _undoTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
        _undoTimer.Tick += (_, _) =>
        {
            _undoTimer?.Stop();
            HideUndo();
        };
        _undoTimer.Start();
    }

    private void HideUndo()
    {
        _pendingRemoval = null;
        var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(200)) { FillBehavior = FillBehavior.Stop };
        undoBar.BeginAnimation(OpacityProperty, fade);
        // A timer, not the animation's Completed event, guarantees the collapse: the animation
        // clock only advances while frames are actually being rendered.
        _hideTimer?.Stop();
        _hideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(230) };
        _hideTimer.Tick += (_, _) =>
        {
            _hideTimer?.Stop();
            undoBar.BeginAnimation(OpacityProperty, null);
            undoBar.Opacity = 0;
            undoBar.Visibility = Visibility.Collapsed;
        };
        _hideTimer.Start();
    }

    private void OnUndoClick(object sender, RoutedEventArgs e)
    {
        var record = _pendingRemoval;
        _undoTimer?.Stop();
        HideUndo();
        if (record != null) _player.RestoreRemoved(record);
    }

    private async void OnRestoreHiddenClick(object sender, RoutedEventArgs e)
    {
        int count = await _player.RestoreHiddenAsync();
        txtViewSub.Text = count > 0 ? $"已恢复 {count} 首被隐藏的曲目" : "没有被隐藏的曲目";
    }

    // ---------- row context menu ----------
    private void OnSongContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var song = ItemUnder(Mouse.GetPosition(lstSongs));
        if (song == null)
        {
            e.Handled = true;                   // empty area: no menu
            return;
        }
        lstSongs.SelectedItem = song;
        miDownload.Visibility = song.Source == SongSource.Netease ? Visibility.Visible : Visibility.Collapsed;
        miSaveAs.Visibility = Visibility.Visible;
        miAddPlaylist.Visibility = Visibility.Visible;
    }

    /// <summary>「添加到歌单…」 opens a picker window: one row per playlist, no flooded menu and no
    /// submenu (the theme's MenuItem template had no submenu popup at all).
    /// With no playlists at all there is nothing to pick, and asking the user to choose "默认歌单"
    /// from a one-row list is a pointless extra click: that case creates it and adds straight away.</summary>
    private async void OnSongMenuAddToPlaylist(object sender, RoutedEventArgs e)
    {
        if (lstSongs.SelectedItem is not Song song) return;
        Playlist? target = null;
        if (_player.Playlists.Count > 0)
        {
            var picker = new PlaylistPickerWindow(song.DisplayTitle, _player.Playlists) { Owner = this };
            DebugLastPicker = picker;
            bool accepted = picker.ShowDialog() == true;
            DebugLastPicker = null;
            if (!accepted) return;
            target = picker.Selected;
        }
        await AddSelectedToPlaylistAsync(target);
    }

    /// <summary>QA: the picker「添加到歌单…」just opened (null once it closed).</summary>
    internal PlaylistPickerWindow? DebugLastPicker { get; private set; }

    /// <summary>QA: playlist names as the sidebar sees them.</summary>
    internal string DebugPlaylistNames() =>
        _player.Playlists.Count == 0
            ? "(none)"
            : string.Join(", ", _player.Playlists.Select(p => $"{p.Name}:{p.Songs.Count}"));

    /// <summary>QA: run the row-context-menu handler without a mouse.</summary>
    internal void DebugOpenAddToPlaylist() => OnSongMenuAddToPlaylist(this, new RoutedEventArgs());

    /// <summary>Adds the selected row to a playlist. Local rows go straight in; a cloud row is saved
    /// to the download folder first and that file joins the playlist (playlists store file paths).</summary>
    private async Task<string> AddSelectedToPlaylistAsync(Playlist? target)
    {
        if (lstSongs.SelectedItem is not Song song) return "nothing selected";
        var totalClock = System.Diagnostics.Stopwatch.StartNew();
        long downloadMs = 0;
        // No playlists at all: make 默认歌单 right now, before the download below (which can take a
        // while or fail). "添加到歌单" with nothing to add to has to end in a playlist, not in a
        // message box telling the user to create one first.
        bool createdDefault = false;
        if (target == null && _player.Playlists.Count == 0)
        {
            target = _player.CreatePlaylist("默认歌单");
            createdDefault = true;
            RefreshPlaylists();
        }
        string? path = song.Source == SongSource.Local ? song.Path : null;
        if (path == null)
        {
            // Skip the download entirely when the target already holds this track (any format).
            var existing = target ?? _player.Playlists.FirstOrDefault(p => p.Name == "默认歌单");
            if (existing != null && _player.PlaylistHasNetease(existing, song.NeteaseId))
            {
                _player.SetNotice($"「{existing.Name}」里已有这首");
                return $"already in '{existing.Name}'";
            }
            _player.SetNotice($"正在下载「{song.DisplayTitle}」并加入歌单…");
            // 同一首歌本地已经有就直接用那份文件（歌单里登记过的、曲库里的、下载目录里的都算）。
            // 无损一首 100~200 MB，重下一遍就是几十秒，这才是"加到歌单很慢"的主要来源。
            var downloadClock = System.Diagnostics.Stopwatch.StartNew();
            path = _player.FindDownloadedPath(song.NeteaseId);
            if (path != null)
            {
                _player.SetNotice($"本地已经有「{song.DisplayTitle}」，直接加入…");
            }
            else
            {
                if (!await _player.DownloadSongAsync(song))
                    return "download failed: " + (_player.Notice ?? "未知原因");
                path = _player.LastDownloadPath;
                if (path == null) return "download produced no file";
            }
            downloadClock.Stop();
            downloadMs = downloadClock.ElapsedMilliseconds;
        }

        var (playlist, created, added) = AddPathToPlaylist(path, target);
        RefreshPlaylists();
        bool fresh = created || createdDefault;
        _player.SetNotice(added > 0
            ? $"已添加到「{playlist.Name}」" + (fresh ? "（新建）" : "")
            : $"「{playlist.Name}」里已有这首");
        // Adding to the playlist that is open right now should show the new row immediately.
        if (added > 0 && ReferenceEquals(_player.PlaylistInView, playlist))
            await _player.ShowPlaylistAsync(playlist);
        LogAddTiming(song, path, downloadMs, totalClock.ElapsedMilliseconds, added);
        return $"song='{song.DisplayTitle}' source={song.Source} target='{playlist.Name}' " +
               $"created={fresh} added={added} entries={playlist.Songs.Count} file='{System.IO.Path.GetFileName(path)}'";
    }

    /// <summary>加一首歌的耗时写进 data\add.log：慢了能一眼看出是"下载"还是"界面"。</summary>
    private static void LogAddTiming(Song song, string? path, long downloadMs, long totalMs, int added)
    {
        try
        {
            var file = System.IO.Path.Combine(Services.AppPaths.DataDir, "add.log");
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file)!);
            System.IO.File.AppendAllText(file,
                $"{DateTime.Now:MM-dd HH:mm:ss} '{song.DisplayTitle}' id={song.NeteaseId} src={song.Source} " +
                $"lookup+download={downloadMs}ms total={totalMs}ms added={added} " +
                $"file='{System.IO.Path.GetFileName(path ?? "")}'{Environment.NewLine}");
        }
        catch { }
    }

    /// <summary>Menu and QA share this: a null target means "默认歌单", created on demand.</summary>
    private (Playlist playlist, bool created, int added) AddPathToPlaylist(string path, Playlist? target)
    {
        bool created = false;
        if (target == null)
        {
            // Reuse an existing 默认歌单 rather than piling up duplicates if it is somehow not listed.
            target = _player.Playlists.FirstOrDefault(p => p.Name == "默认歌单");
            if (target == null)
            {
                target = _player.CreatePlaylist("默认歌单");
                created = true;
            }
        }
        int added = _player.AddToPlaylist(target, new[] { path });
        return (target, created, added);
    }

    private (Playlist playlist, bool created, int added) AddSongToPlaylist(Song song, Playlist? target) =>
        AddPathToPlaylist(song.Path, target);

    /// <summary>QA: run the add-to-playlist path without a mouse (selected row may be a cloud row).</summary>
    internal Task<string> DebugAddSelectedToPlaylist(string? playlistName) =>
        AddSelectedToPlaylistAsync(playlistName == null
            ? null
            : _player.Playlists.FirstOrDefault(p => p.Name == playlistName));

    /// <summary>QA: select a row by index (what a click would do).</summary>
    internal string DebugSelectRow(int index)
    {
        if (index < 0 || index >= _view.Count) return $"index {index} out of range (count={_view.Count})";
        lstSongs.SelectedItem = _view[index];
        return $"selected='{_view[index].DisplayTitle}' source={_view[index].Source}";
    }

    /// <summary>QA: run the add-to-playlist path without a mouse.</summary>
    internal string DebugAddToPlaylist(int songIndex, string? playlistName)
    {
        if (songIndex < 0 || songIndex >= _view.Count) return $"index {songIndex} out of range (count={_view.Count})";
        var song = _view[songIndex];
        var target = playlistName == null
            ? null
            : _player.Playlists.FirstOrDefault(p => p.Name == playlistName);
        var (playlist, created, added) = AddSongToPlaylist(song, target);
        RefreshPlaylists();
        return $"song='{song.DisplayTitle}' local={song.Source == SongSource.Local} " +
               $"target='{playlist.Name}' created={created} added={added} " +
               $"entries={playlist.Songs.Count} existing={playlist.ExistingCount} countText='{playlist.CountText}' " +
               $"playlists={_player.Playlists.Count}";
    }

    /// <summary>QA: what the playlist picker would list right now.</summary>
    internal string DebugAddPlaylistMenu()
    {
        var labels = _player.Playlists
            .OrderBy(p => p.Name, StringComparer.CurrentCulture)
            .Select(p => p.Name).ToList();
        return labels.Count == 0 ? "items=[默认歌单（新建）]" : $"items=[{string.Join(", ", labels)}]";
    }

    /// <summary>QA: open the row context menu so it can be screenshotted.</summary>
    internal string DebugOpenSongMenu()
    {
        var song = _view.FirstOrDefault();
        if (song == null) return "no rows";
        lstSongs.SelectedItem = song;
        miDownload.Visibility = song.Source == SongSource.Netease ? Visibility.Visible : Visibility.Collapsed;
        if (lstSongs.ContextMenu is not { } menu) return "no context menu";
        menu.PlacementTarget = lstSongs;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Center;
        menu.IsOpen = true;
        return $"open song='{song.DisplayTitle}'";
    }

    /// <summary>QA: close the menu again.</summary>
    internal void DebugCloseSongMenu() => DebugCloseMenus();

    private async void OnSongMenuDownload(object sender, RoutedEventArgs e)
    {
        if (lstSongs.SelectedItem is Song song) await _player.DownloadSongAsync(song);
    }

    /// <summary>另存到…: a one-off copy into a folder the user picks, so a track can live somewhere
    /// other than the configured download folder. Cloud rows are downloaded straight into it.</summary>
    private async void OnSongMenuSaveAs(object sender, RoutedEventArgs e)
    {
        if (lstSongs.SelectedItem is not Song song) return;
        var folder = FolderPicker.Pick("另存到文件夹", _player.Settings.ResolvedDownloadFolder);
        if (string.IsNullOrWhiteSpace(folder)) return;

        if (song.Source == SongSource.Local)
        {
            var (ok, message) = CopySongWithSidecars(song.Path, folder!);
            _player.SetNotice(ok ? $"已另存到 {folder}：{message}" : "另存失败：" + message);
            return;
        }

        _player.SetNotice($"正在另存「{song.DisplayTitle}」…");
        var saved = await _player.SaveSongToAsync(song, folder!);
        _player.SetNotice(saved.Ok
            ? $"已另存到 {folder}：{System.IO.Path.GetFileName(saved.Saved)}"
            : "另存失败：" + (saved.Error ?? "未知原因"));
    }

    /// <summary>Copies an audio file plus its same-named .jpg/.lrc into <paramref name="folder"/>.</summary>
    private static (bool ok, string message) CopySongWithSidecars(string source, string folder)
    {
        try
        {
            System.IO.Directory.CreateDirectory(folder);
            var target = UniquePath(folder, System.IO.Path.GetFileName(source));
            System.IO.File.Copy(source, target, false);
            foreach (var ext in new[] { ".jpg", ".lrc" })
            {
                var sidecar = System.IO.Path.ChangeExtension(source, ext);
                if (System.IO.File.Exists(sidecar))
                    System.IO.File.Copy(sidecar, System.IO.Path.ChangeExtension(target, ext), true);
            }
            return (true, System.IO.Path.GetFileName(target));
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private static string UniquePath(string folder, string name)
    {
        var path = System.IO.Path.Combine(folder, name);
        for (int i = 2; System.IO.File.Exists(path); i++)
            path = System.IO.Path.Combine(folder,
                System.IO.Path.GetFileNameWithoutExtension(name) + $" ({i})" + System.IO.Path.GetExtension(name));
        return path;
    }

    private void OnSongMenuPlay(object sender, RoutedEventArgs e)
    {
        if (lstSongs.SelectedItem is Song song) _ = _player.PlayAsync(song);
    }

    private void OnSongMenuRemove(object sender, RoutedEventArgs e)
    {
        if (lstSongs.SelectedItem is Song song) RemoveSong(song);
    }

    private void OnSongMenuReveal(object sender, RoutedEventArgs e)
    {
        if (lstSongs.SelectedItem is not Song { Source: SongSource.Local } song) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe",
                $"/select,\"{song.Path}\"") { UseShellExecute = true });
        }
        catch { }
    }

    private void OnSongMenuCopy(object sender, RoutedEventArgs e)
    {
        if (lstSongs.SelectedItem is not Song song) return;
        try { Clipboard.SetText($"{song.DisplayTitle} - {song.DisplayArtist}"); } catch { }
    }

    // ---------- play mode ----------
    private void OnPlayModeClick(object sender, RoutedEventArgs e)
    {
        _player.ToggleMode();
        UpdatePlayModeUi();
    }

    private void UpdatePlayModeUi()
    {
        bool repeat = _player.Mode == PlayMode.RepeatOne;
        txtPlayMode.Text = repeat ? "单曲循环" : "顺序播放";
        txtPlayModeIcon.Text = repeat ? "↻" : "⇉";
        txtPlayModeIcon.Foreground = repeat
            ? (System.Windows.Media.Brush)FindResource("AccentBrush")
            : (System.Windows.Media.Brush)FindResource("TextDimBrush");
    }

    // ---------- drag to reorder: lift the row, open a 2/3-row gap at the target ----------
    private Point _dragStart;
    private Song? _dragSong;
    private Border? _ghost;
    private ListBoxItem? _sourceItem;
    private double _gapOpen;
    private double _rowHeight = 57;
    private double _grabOffsetY;
    private int _insertIndex = -1;

    private void OnSongDragStart(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(lstSongs);
        _dragSong = ItemUnder(_dragStart);
        if (_dragSong == null) return;
        if (lstSongs.ItemContainerGenerator.ContainerFromItem(_dragSong) is ListBoxItem item)
            _grabOffsetY = _dragStart.Y - item.TranslatePoint(new Point(0, 0), lstSongs).Y;
    }

    private void OnSongDragMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragSong == null) return;
        var position = e.GetPosition(lstSongs);
        if (Math.Abs(position.Y - _dragStart.Y) < 6 && Math.Abs(position.X - _dragStart.X) < 6) return;

        var song = _dragSong;
        _dragSong = null;
        BeginDrag(song);
        var effect = DragDropEffects.None;
        try
        {
            effect = DragDrop.DoDragDrop(lstSongs, song, DragDropEffects.Move);
        }
        finally
        {
            // 取消（Esc / 松到窗口外）时 effect 是 None：这时不能按最后悬停的槽位重排，
            // 只能把被抬起的行原样放回去。
            EndDrag(song, effect);
        }
    }

    /// <summary>
    /// Lifts the row out of the list: a snapshot card floats under the cursor and the original slot
    /// collapses, so the list closes up. The insertion point is shown by opening a gap of 2/3 of a
    /// row height at the row the cursor is over.
    /// </summary>
    private void BeginDrag(Song song)
    {
        if (lstSongs.ItemContainerGenerator.ContainerFromItem(song) is not ListBoxItem item) return;
        _rowHeight = item.ActualHeight > 4 ? item.ActualHeight : 57;

        // Snapshot before the row is hidden: a VisualBrush of a collapsed element renders empty.
        // 2x snapshot (drawn at 1x) keeps the card sharp; the row width comes from the list so the
        // duration column is not clipped while the row is collapsed.
        double cardWidth = lstSongs.ActualWidth - 12;
        double cardHeight = item.ActualHeight;
        var snapshot = new System.Windows.Media.Imaging.RenderTargetBitmap(
            Math.Max(1, (int)(cardWidth * 2)), Math.Max(1, (int)(cardHeight * 2)), 192, 192,
            PixelFormats.Pbgra32);
        var visual = new System.Windows.Media.DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            var brush = new VisualBrush(item) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top };
            context.DrawRectangle(brush, null, new Rect(0, 0, cardWidth, cardHeight));
        }
        snapshot.Render(visual);
        snapshot.Freeze();
        var imageBrush = new ImageBrush(snapshot) { Stretch = Stretch.Uniform };
        _ghost = new Border
        {
            Width = cardWidth,
            Height = cardHeight,
            CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(Color.FromRgb(0x1B, 0x21, 0x2E)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x3A, 0x46, 0x5E)),
            BorderThickness = new Thickness(1),
            Opacity = 0.98,
            IsHitTestVisible = false,
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = 24,
                ShadowDepth = 8,
                Opacity = 0.5,
                Color = Colors.Black,
            },
            Child = new Rectangle { Fill = imageBrush, Width = cardWidth, Height = cardHeight, RadiusX = 8, RadiusY = 8 },
            // The card's content never changes while it flies, so rasterise it once: without this the
            // blur + 2x snapshot were re-rasterised every frame and the glide ran at ~25 fps.
            CacheMode = new System.Windows.Media.BitmapCache(),

        };
        dragLayer.Children.Add(_ghost);

        // Lift: the card swells a hair and fades up instead of appearing fully formed.
        var liftScale = new ScaleTransform(0.985, 0.985);
        _ghost.RenderTransformOrigin = new Point(0.5, 0.5);
        _ghost.RenderTransform = liftScale;
        var liftEase = new CubicEase { EasingMode = EasingMode.EaseOut };
        var liftDuration = new Duration(TimeSpan.FromMilliseconds(150));
        _ghost.BeginAnimation(OpacityProperty, new DoubleAnimation(0.98, liftDuration) { EasingFunction = liftEase, From = 0.55 });
        liftScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1.012, liftDuration) { EasingFunction = liftEase });
        liftScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1.012, liftDuration) { EasingFunction = liftEase });

        // The source row disappears and its slot collapses: only the card remains on screen.
        _sourceItem = item;
        item.Visibility = Visibility.Collapsed;

        var before = CaptureItemOffsets();
        AnimateReorder(before, 170);
    }

    private void EndDrag(Song song, DragDropEffects effect = DragDropEffects.Move)
    {
        int from = _view.IndexOf(song);
        int to = effect == DragDropEffects.None ? -1 : _insertIndex;
        _insertIndex = -1;

        // The card glides into the slot it is about to occupy while the rows underneath run a FLIP
        // pass, so the landing frame has motion instead of a jump. Positions are captured before the
        // rebuild, which is what makes the rows slide from where they were to where they now are.
        var before = CaptureItemOffsets();
        StartFrameMonitor(700);

        if (_ghost != null)
        {
            double target = GhostSlotTop(to, from);
            // The slot row is currently pushed down by the open gap; the card has to land where the
            // row will sit once that gap closes, or it would jump by two thirds of a row on release.
            if (to >= 0 && to < _view.Count &&
                lstSongs.ItemContainerGenerator.ContainerFromItem(_view[to]) is ListBoxItem slotRow &&
                slotRow.RenderTransform is TranslateTransform slotShift)
                target -= slotShift.Y;
            // A cancelled drag (Esc, released outside the window) never ran DragOver, so Canvas.Top is
            // still NaN — and DoubleAnimation(NaN) throws, which took the whole app down.
            if (double.IsNaN(target) || target < 0) target = Canvas.GetTop(_ghost);
            if (double.IsNaN(target)) target = Math.Max(0, _ghostTop);
            var glide = new DoubleAnimation(target, new Duration(TimeSpan.FromMilliseconds(150)))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            };
            bool landed = false;
            void Land()
            {
                if (landed) return;
                landed = true;
                // The gap was already open at this slot, so the rows underneath are where they belong:
                // rebuilding now only swaps the flying card for the real row. A second FLIP pass here
                // made the list slide again right after the card landed — the "double motion" bug.
                FinishDrag(song, from, to);
            }
            glide.Completed += (_, _) => Land();
            _ghost.BeginAnimation(Canvas.TopProperty, glide);
            // 兜底：动画时钟如果没走（窗口被遮挡/最小化、合成被关掉），被抬起的行就会永远留在
            // Collapsed 状态——歌从列表里消失。所以无论动画有没有跑完，都必须落一次。
            var guard = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(420),
            };
            guard.Tick += (_, _) => { guard.Stop(); Land(); };
            guard.Start();
            return;
        }

        FinishDrag(song, from, to);
        AnimateReorder(before, 210);
    }

    private double GhostSlotTop(int insertIndex, int from)
    {
        if (dragLayer.ActualHeight < 4) return -1;
        if (insertIndex >= _view.Count)
        {
            for (int i = _view.Count - 1; i >= 0; i--)
            {
                if (i == from) continue;
                if (lstSongs.ItemContainerGenerator.ContainerFromItem(_view[i]) is ListBoxItem last &&
                    last.Visibility == Visibility.Visible)
                    return Math.Min(dragLayer.ActualHeight - (_ghost?.Height ?? 0),
                        last.TranslatePoint(new Point(0, 0), lstSongs).Y + last.ActualHeight - 6);
            }
            return -1;
        }
        if (insertIndex >= 0 && insertIndex < _view.Count &&
            lstSongs.ItemContainerGenerator.ContainerFromItem(_view[insertIndex]) is ListBoxItem row)
            return row.TranslatePoint(new Point(0, 0), lstSongs).Y;
        return -1;
    }

    private double _lastDropMs;

    private void FinishDrag(Song song, int from, int to)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();

        // 抬起的行是先隐藏再腾出空位的。如果这次拖动没有改变顺序（原地放下、拖到列表下方的空白、
        // 或者中途取消），`Move` 不会发生，容器也不会被重建，Visibility 就永远停在 Collapsed
        // —— 歌直接从列表里消失。恢复可见性必须在这里，而且要早于 CloseGap。
        if (_sourceItem != null)
        {
            _sourceItem.Visibility = Visibility.Visible;
            if (_sourceItem.RenderTransform is TranslateTransform leftover)
            {
                leftover.BeginAnimation(TranslateTransform.YProperty, null);
                leftover.Y = 0;
            }
            _sourceItem = null;
        }

        CloseGap(0);
        if (from >= 0 && to >= 0)
        {
            // `to` is an insertion slot in 0..Count; removing the dragged row first shifts every
            // later slot down by one, then clamp into the real index range.
            if (from < to) to--;
            to = Math.Clamp(to, 0, Math.Max(0, _view.Count - 1));
            if (to != from)
            {
                _view.Move(from, to);
                _player.SetOrder(_view);
            }
        }

        if (_ghost != null)
        {
            dragLayer.Children.Remove(_ghost);
            _ghost = null;
        }
        _sourceItem = null;

        // No rebuild: the ObservableCollection move already moved the container, and re-creating every
        // row (templates, cover bitmaps) cost ~27 ms on the landing frame.
        RenumberView();
        UpdateListFooter();
        _lastDropMs = clock.Elapsed.TotalMilliseconds;

        // The order is only a convenience for the next start: saving it (a synchronous JSON write)
        // on the landing frame was the hitch the user felt after every drop.
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _player.PersistOrder();
            if (Environment.GetEnvironmentVariable("LYRIC_DROP_LOG") == "1")
            {
                try
                {
                    System.IO.File.AppendAllText(System.IO.Path.Combine(AppPaths.DataDir, "drop.log"),
                        $"from={from} to={to} dragged={song.DisplayTitle}\n" +
                        $"  view=[{string.Join(",", _view.Select(s => s.DisplayTitle))}]\n" +
                        $"  model=[{string.Join(",", _player.Songs.Select(s => s.DisplayTitle))}]\n");
                }
                catch { }
            }
        }), DispatcherPriority.Background);
    }

    private double _ghostTop;

    /// <summary>Moves the lifted card and remembers where it is: a drag that never ran DragOver has
    /// no Canvas.Top (NaN), and animating to NaN throws.</summary>
    private void MoveGhost(double top)
    {
        _ghostTop = Math.Clamp(top, 0, Math.Max(0, dragLayer.ActualHeight - (_ghost?.Height ?? 0)));
        if (_ghost != null) Canvas.SetTop(_ghost, _ghostTop);
    }

    private void OnSongDragOver(object sender, DragEventArgs e)
    {
        bool dragging = e.Data.GetDataPresent(typeof(Song));
        e.Effects = dragging ? DragDropEffects.Move : DragDropEffects.None;
        if (!dragging || _ghost == null || e.Data.GetData(typeof(Song)) is not Song dragged) { e.Handled = true; return; }

        var position = e.GetPosition(lstSongs);
        Canvas.SetLeft(_ghost, 8);
        MoveGhost(position.Y - _grabOffsetY);

        int target = FindInsertIndex(position, dragged);
        if (target != _insertIndex)
        {
            _insertIndex = target;
            OpenGap(target);
        }
        e.Handled = true;
    }

    private void OnSongDrop(object sender, DragEventArgs e) => e.Handled = true;

    /// <summary>QA: lifts the first row and opens a gap at the third slot, ready for a drop.</summary>
    internal void PrepareDropForQa(double pointerY)
    {
        if (_view.Count < 3) return;
        BeginDrag(_view[0]);
        Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
        if (_ghost != null)
        {
            Canvas.SetLeft(_ghost, 8);
            MoveGhost(pointerY - _grabOffsetY);
        }
        _insertIndex = _view.Count;   // trailing slot
        OpenGap(_view.Count);
    }

    /// <summary>QA: runs the exact drop path from code so frames can be captured around it.</summary>
    internal string SimulateDropForQa()
    {
        if (_sourceItem == null) return "no-source";
        var song = _sourceItem.DataContext as Song;
        if (song == null) return "no-datacontext";
        EndDrag(song);
        return "enddrag";
    }

    /// <summary>QA: lift row <paramref name="index"/> and leave the insertion slot at
    /// <paramref name="slot"/>, so drops that do not change the order can be exercised.</summary>
    internal void PrepareDropAtForQa(int index, int slot)
    {
        if (index < 0 || index >= _view.Count) return;
        BeginDrag(_view[index]);
        Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
        _insertIndex = slot;
        OpenGap(slot);
    }

    /// <summary>QA: drop as if the drag had been cancelled (Esc / released outside the window).</summary>
    internal void SimulateDropCancelForQa()
    {
        if (_sourceItem?.DataContext is not Song song) return;
        EndDrag(song, DragDropEffects.None);
    }

    internal string DebugOrder() => string.Join(" | ", _view.Select(s => s.DisplayTitle));

    /// <summary>First row whose vertical centre is below the cursor becomes the insertion slot.</summary>
    private int FindInsertIndex(Point position, Song dragged)
    {
        for (int i = 0; i < _view.Count; i++)
        {
            var song = _view[i];
            if (ReferenceEquals(song, dragged)) continue;
            if (lstSongs.ItemContainerGenerator.ContainerFromItem(song) is not ListBoxItem container) continue;
            if (container.Visibility != Visibility.Visible) continue;
            double top = container.TranslatePoint(new Point(0, 0), lstSongs).Y;
            if (position.Y < top + container.ActualHeight / 2) return i;
        }
        return _view.Count;          // below every row: a slot after the last one
    }

    /// <summary>
    /// Opens the drop slot by pushing the rows below it down with a RenderTransform. Animating Margin
    /// (what this used to do) forces a full list layout on every DragOver, which is where the frame
    /// time and the hitch at the release came from — a transform only re-composites.
    /// </summary>
    private void OpenGap(int index)
    {
        // Full row height, not two thirds of it: the dragged card is a whole row tall, so a shorter
        // gap made it overlap the row underneath and that row looked shoved into the next one.
        double gap = Math.Max(_rowHeight - 2, 20);
        int slot = Math.Clamp(index, 0, _view.Count);
        for (int i = 0; i < _view.Count; i++)
        {
            if (lstSongs.ItemContainerGenerator.ContainerFromItem(_view[i]) is not ListBoxItem item) continue;
            if (ReferenceEquals(item, _sourceItem)) continue;
            double target = i >= slot ? gap : 0;
            // Instant, not animated: during a fast drag the 150 ms slide could not keep up with
            // the card, so the card overlapped the row that had not moved out of the way yet.
            Slide(item, target, 0);
        }
        _gapOpen = gap;
    }

    private void CloseGap(int ms)
    {
        if (_gapOpen <= 0) return;
        _gapOpen = 0;
        foreach (var song in _view)
        {
            if (lstSongs.ItemContainerGenerator.ContainerFromItem(song) is not ListBoxItem item) continue;
            if (ReferenceEquals(item, _sourceItem)) continue;
            Slide(item, 0, ms);
        }
    }

    /// <summary>Transform-only row offset: no layout pass, so it never costs a frame.</summary>
    private static void Slide(ListBoxItem item, double offset, int ms)
    {
        var transform = item.RenderTransform as TranslateTransform;
        if (transform == null)
        {
            transform = new TranslateTransform();
            item.RenderTransform = transform;
        }
        if (ms <= 0)
        {
            transform.BeginAnimation(TranslateTransform.YProperty, null);
            transform.Y = offset;
            return;
        }
        transform.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(offset, TimeSpan.FromMilliseconds(ms))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            });
    }

    private Dictionary<Song, double> CaptureItemOffsets()
    {
        var map = new Dictionary<Song, double>();
        foreach (var song in _view)
        {
            if (lstSongs.ItemContainerGenerator.ContainerFromItem(song) is ListBoxItem item)
                map[song] = item.TranslatePoint(new Point(0, 0), lstSongs).Y;
        }
        return map;
    }

    /// <summary>
    /// FLIP transition: each row starts at its previous offset and eases to zero, so rows slide past
    /// each other instead of teleporting.
    /// </summary>
    private void AnimateReorder(Dictionary<Song, double> before, int durationMs = 260)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            foreach (var (song, oldY) in before)
            {
                if (lstSongs.ItemContainerGenerator.ContainerFromItem(song) is not ListBoxItem item) continue;
                double newY = item.TranslatePoint(new Point(0, 0), lstSongs).Y;
                double delta = oldY - newY;
                if (Math.Abs(delta) < 0.5) continue;
                if (item.Opacity < 0.5) continue;      // the dragged placeholder stays put

                var slide = new TranslateTransform(0, delta);
                item.RenderTransform = slide;
                var animation = new DoubleAnimation(delta, 0, TimeSpan.FromMilliseconds(durationMs))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                };
                animation.Completed += (_, _) =>
                {
                    slide.BeginAnimation(TranslateTransform.YProperty, null);
                    item.RenderTransform = Transform.Identity;
                };
                slide.BeginAnimation(TranslateTransform.YProperty, animation);
            }
        }), DispatcherPriority.Render);
    }

    private double _dragScrollOffset()
    {
        var viewer = DescendantScrollViewer(lstSongs);
        return viewer?.VerticalOffset ?? 0;
    }

    private static ScrollViewer? DescendantScrollViewer(DependencyObject root)
    {
        int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer viewer) return viewer;
            var nested = DescendantScrollViewer(child);
            if (nested != null) return nested;
        }
        return null;
    }

    private Song? ItemUnder(Point point)
    {
        var element = lstSongs.InputHitTest(point) as DependencyObject;
        while (element != null && element is not ListBoxItem)
        {
            element = System.Windows.Media.VisualTreeHelper.GetParent(element);
        }
        return (element as ListBoxItem)?.DataContext as Song;
    }

    private void RenumberSongs()
    {
        RenumberView();
    }

    /// <summary>QA hook: leaves a drag ghost floating and the list already reordered, for a screenshot.</summary>
    internal void RunDragVisualTest(double pointerY)
    {
        if (_view.Count < 3) return;
        var dragged = _view[0];
        BeginDrag(dragged);
        Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
        if (_ghost != null)
        {
            Canvas.SetLeft(_ghost, 8);
            MoveGhost(pointerY - _grabOffsetY);
        }
        _insertIndex = _view.Count;   // trailing slot
        OpenGap(_view.Count);
    }

    /// <summary>QA hook: performs a reorder and reports whether rows were animated.</summary>
    internal string RunDropAnimationTest(int from, int to)
    {
        if (_player.Songs.Count <= Math.Max(from, to)) return "not enough songs";
        var before = CaptureItemOffsets();
        var moved = _view[from];
        _view.Move(from, to);
        RenumberView();
        _player.MoveSong(from, to);
        AnimateReorder(before);
        Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
        Dispatcher.Invoke(() => { }, DispatcherPriority.Background);

        int animated = 0;
        foreach (var song in _player.Songs)
        {
            if (lstSongs.ItemContainerGenerator.ContainerFromItem(song) is ListBoxItem item &&
                item.RenderTransform is TranslateTransform translate && Math.Abs(translate.Y) > 0.01)
                animated++;
        }
        return $"moved={moved.DisplayTitle} from={from} to={to} rowsTracked={before.Count} animating={animated}";
    }

    private async void OnPlayAllClick(object sender, RoutedEventArgs e)
    {
        if (_player.Songs.Count == 0) return;
        await _player.PlayAsync(_player.Songs[0]);
    }

    // ---------- view state ----------
    private void OnViewChanged()
    {
        Dispatcher.Invoke(() =>
        {
            txtViewName.Text = _player.ViewName;
            txtViewSub.Text = _player.ViewSubtitle;
            // Playlists / the local library carry a decoded bitmap; streamed queues carry a URL.
            var headerCover = _player.ViewCoverImage
                              ?? (_player.ViewCoverUrl != null ? TryImage(_player.ViewCoverUrl) : null);
            imgHeader.Source = headerCover;
            txtHeaderGlyph.Visibility = headerCover == null ? Visibility.Visible : Visibility.Collapsed;
            bool libraryActive = _player.ViewName is "本地音乐" or "音乐库";
            btnLocal.Background = libraryActive
                ? (System.Windows.Media.Brush)FindResource("NavActiveBrush")
                : Brushes.Transparent;
            btnOnline.Background = _player.IsOnlineView
                ? (System.Windows.Media.Brush)FindResource("NavActiveBrush")
                : Brushes.Transparent;
            // With a transparent background the active pill has no fill at all, so the accent ink is what
            // says "this is the current view".
            btnLocal.Foreground = libraryActive
                ? (System.Windows.Media.Brush)FindResource("AccentBrush")
                : (System.Windows.Media.Brush)FindResource("TextDimBrush");
            btnOnline.Foreground = _player.IsOnlineView
                ? (System.Windows.Media.Brush)FindResource("AccentBrush")
                : (System.Windows.Media.Brush)FindResource("TextDimBrush");
            // The same box filters the list locally and searches the server. No placeholder text: the
            // box keeps just its magnifier icon (user request, 2026-11-27).
            // Leaving the online view must drop its keyword from the shared box: the same text would
            // otherwise filter the library that is being opened next, and a keyword with no local
            // match empties the list. The text is remembered instead of thrown away, so coming back
            // to 在线搜索 re-runs the same search (user asked for that, 2026-11-26).
            if (_wasOnlineView && !_player.IsOnlineView)
            {
                _onlineSearchTimer?.Stop();
                _savedOnlineKeyword = txtSearch.Text?.Trim() ?? "";
                if (txtSearch.Text.Length > 0) txtSearch.Text = "";
            }
            _wasOnlineView = _player.IsOnlineView;
            _libraryActive = libraryActive;
            btnPlayAll.IsEnabled = _player.Songs.Count > 0;
            ApplyFilter();
            emptyState.Visibility = _player.Songs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            lstSongs.Visibility = _player.Songs.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            UpdateEmptyStateText();
            UpdateListFooter();
        });
    }

    /// <summary>QA: how many rows the list is actually showing after filtering.</summary>
    public int DebugViewCount => _view.Count;

    /// <summary>QA: remove a row through the same path the hover button uses.</summary>
    public string DebugRemoveAt(int index)
    {
        if (index < 0 || index >= _view.Count) return $"index {index} out of range (count={_view.Count})";
        var song = _view[index];
        RemoveSong(song);
        return $"removed='{song.DisplayTitle}' scope='{_player.RemoveScopeText}' " +
               $"bar={undoBar.Visibility} text='{txtUndo.Text}' remaining={_player.Songs.Count}";
    }

    /// <summary>QA: press 撤销 the way the user would.</summary>
    public string DebugUndoRemove()
    {
        if (_pendingRemoval == null) return "nothing pending";
        var title = _pendingRemoval.Song.DisplayTitle;
        OnUndoClick(this, new RoutedEventArgs());
        return $"undone='{title}' rows={_player.Songs.Count} bar={undoBar.Visibility}";
    }

    /// <summary>QA: where the undo bar actually ended up after the show animation.</summary>
    public string DebugUndoState()
    {
        var point = undoBar.TranslatePoint(new Point(0, 0), this);
        return $"vis={undoBar.Visibility} opacity={undoBar.Opacity:0.00} " +
               $"at={point.X:0},{point.Y:0} size={undoBar.ActualWidth:0}x{undoBar.ActualHeight:0} " +
               $"text='{txtUndo.Text}'";
    }

    /// <summary>QA: positions/sizes of the player-bar pieces, relative to the window.</summary>
    public string DebugLayout()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"window {ActualWidth:0.0}x{ActualHeight:0.0}");
        sb.AppendLine($"hotkeys registered={_hotkeys.RegisteredCount} failed={_hotkeys.Failed.Count} " +
                      $"codes=[{string.Join(",", _hotkeys.FailCodes.Select(kv => $"{kv.Key}={kv.Value}"))}] " +
                      $"binds=[{_settings.KeyAboveApps}|{_settings.KeyLyrics}|{_settings.KeyPlayPause}|{_settings.KeyNext}|{_settings.KeyPrevious}]");
        // 背景图的实际尺寸/负边距：磨砂变大小时 Width/Height 应该**不变**（只该多露出外围像素），
        // 以前是 UniformToFill 填满被负边距撑大的盒子，所以磨砂越大图越放大。
        var bmpSize = (themeImage.Source as BitmapSource);
        sb.AppendLine($"themeImage w={themeImage.Width:0.0} h={themeImage.Height:0.0} " +
                      $"margin={themeImage.Margin.Left:0.0} " +
                      $"src={(bmpSize == null ? "none" : $"{bmpSize.PixelWidth}x{bmpSize.PixelHeight}")} " +
                      $"blur={_settings.ThemeBlur:0} seeThrough={_seeThrough} geom='{_geomLog}'");
        if (FindName("playerBar") is Grid bar)
        {
            sb.AppendLine($"playerBar {bar.ActualWidth:0.0}x{bar.ActualHeight:0.0} cols=" +
                          string.Join(",", bar.ColumnDefinitions.Select(c => c.ActualWidth.ToString("0.0"))));
            foreach (FrameworkElement child in bar.Children)
            {
                var cp = child.TransformToAncestor(this).Transform(new Point(0, 0));
                sb.AppendLine($"  child {child.GetType().Name} col={Grid.GetColumn(child)} " +
                              $"x={cp.X:0.0} w={child.ActualWidth:0.0} ha={child.HorizontalAlignment}");
            }
        }
        foreach (var name in new[]
                 {
                     "centerBlock", "seekRow",
                     "imgCover", "txtTitle", "btnPrev", "btnPlay", "btnNext",
                     "txtPos", "sldProgress", "txtDur", "btnPlayMode", "chkLyrics", "sldVolume",
                 })
        {
            if (FindName(name) is not FrameworkElement fe)
            {
                sb.AppendLine($"{name}: missing");
                continue;
            }
            var p = fe.TransformToAncestor(this).Transform(new Point(0, 0));
            sb.AppendLine($"{name}: x={p.X:0.0} y={p.Y:0.0} w={fe.ActualWidth:0.0} h={fe.ActualHeight:0.0}");
        }
        return sb.ToString();
    }

    private static BitmapImage? TryImage(string url)
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.UriSource = new Uri(url, UriKind.Absolute);
            image.DecodePixelWidth = 200;
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.EndInit();
            return image;
        }
        catch { return null; }
    }

    private void ApplyFilter()
    {
        var query = txtSearch.Text?.Trim() ?? "";
        // Online results are already the answer to a server-side search; filtering them again by the
        // same keyword would hide every row whose title happens not to contain it.
        var matches = query.Length == 0 || _player.IsOnlineView
            ? _player.Songs.ToList()
            : _player.Songs.Where(s => s.DisplayTitle.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                                    || s.DisplayArtist.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                                    || s.Album.Contains(query, StringComparison.CurrentCultureIgnoreCase)).ToList();
        _view.Clear();
        foreach (var song in matches) _view.Add(song);
        RenumberView();
        UpdateListFooter();
    }

    private void RenumberView()
    {
        for (int i = 0; i < _view.Count; i++) _view[i].Index = i + 1;
    }

    /// <summary>Empty-state wording depends on where the empty list came from: a playlist whose
    /// files were moved away needs a different hint than an untouched library.</summary>
    private void UpdateEmptyStateText()
    {
        if (_player.IsOnlineView)
        {
            txtEmptyTitle.Text = "还没有搜索结果";
            txtEmptyHint.Text = "在上面的搜索框输入歌名、歌手或专辑，回车即可搜索网易云音乐";
            return;
        }
        var playlist = _player.PlaylistInView;
        if (playlist != null)
        {
            txtEmptyTitle.Text = "这个歌单里没有可播放的文件";
            txtEmptyHint.Text = playlist.MissingCount > 0
                ? $"其中 {playlist.MissingCount} 个条目指向的文件已不存在\n右键歌单 →「清理失效条目」，或「添加歌曲…」重新添加"
                : "右键歌单 →「添加歌曲…」，把音频文件加进来";
            return;
        }
        txtEmptyTitle.Text = "这里是空的";
        txtEmptyHint.Text = "点左侧「本地音乐」选择你的音乐目录";
    }

    /// <summary>Footer line: how many tracks and how long the current list is.</summary>
    private void UpdateListFooter()
    {
        int count = _view.Count;
        if (count == 0)
        {
            txtListFooter.Text = "";
            return;
        }
        double seconds = _view.Sum(s => s.Duration.TotalSeconds);
        int minutes = (int)Math.Round(seconds / 60);
        string length = minutes >= 60 ? $"{minutes / 60} 小时 {minutes % 60} 分钟" : $"{minutes} 分钟";
        txtListFooter.Text = $"共 {count} 首 · {length}";
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        if (_player.IsOnlineView)
        {
            _onlineSearchTimer?.Stop();
            _onlineSearchTimer?.Start();
            return;
        }
        ApplyFilter();
    }

    private async void OnSearchKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Enter) return;
        e.Handled = true;
        _onlineSearchTimer?.Stop();
        _savedOnlineKeyword = txtSearch.Text?.Trim() ?? "";
        await _player.ShowOnlineSearchAsync(txtSearch.Text);
    }

    /// <summary>Opens the online search view; the search box then queries the server instead of
    /// filtering the local list. Clicking it while already there keeps the current results — it used
    /// to re-search with an empty keyword and blank the whole list.</summary>
    private async void OnOnlineClick(object sender, RoutedEventArgs e) => await EnterOnlineViewAsync();

    private async Task EnterOnlineViewAsync()
    {
        if (_player.IsOnlineView)
        {
            txtSearch.Focus();
            txtSearch.SelectAll();
            return;
        }
        // Coming back from the library: restore the last keyword and run that search again.
        var keyword = _savedOnlineKeyword;
        if (keyword.Length > 0 && !string.Equals(txtSearch.Text, keyword, StringComparison.Ordinal))
            txtSearch.Text = keyword;
        await _player.ShowOnlineSearchAsync(keyword);
        _savedOnlineKeyword = keyword;
        txtSearch.Focus();
        txtSearch.SelectAll();
    }

    /// <summary>QA: the search box text, the keyword the online view holds, and the row count.</summary>
    internal string DebugOnlineState() =>
        $"box='{txtSearch.Text}' saved='{_savedOnlineKeyword}' keyword='{_player.LastOnlineKeyword}' " +
        $"online={_player.IsOnlineView} songs={_player.Songs.Count} rows={_view.Count} " +
        $"subtitle='{_player.ViewSubtitle}'";

    /// <summary>QA: click 在线搜索 the way the user would.</summary>
    internal void DebugEnterOnline() => _ = EnterOnlineViewAsync();

    /// <summary>QA: put text in the search box (what typing would leave behind).</summary>
    internal void DebugSetSearchText(string text) => txtSearch.Text = text ?? "";

    /// <summary>Pastable NetEase session cookie — the same one the web player uses. Without it the
    /// search still works anonymously, but lossless and VIP-only tracks stay unavailable.</summary>
    private void OnNeteaseCookieClick(object sender, RoutedEventArgs e)
    {
        var prompt = new PromptWindow("网易云 Cookie",
            _settings.NeteaseCookie ?? "",
            "浏览器登录 music.163.com 后打开开发者工具 → 网络 → 任意请求的 Cookie，整段粘贴到这里（要含 MUSIC_U）。留空即匿名。")
        { Owner = this };
        if (prompt.ShowDialog() != true) return;
        _settings.NeteaseCookie = prompt.Value;
        SettingsStore.Save(_settings);
        ApplyNeteaseSettings();
        _player.SetNotice(prompt.Value.Length == 0 ? "已切换为匿名访问" : "已填入 Cookie，重新播放一次即可生效");
        if (_player.IsOnlineView) _ = _player.ShowOnlineSearchAsync(txtSearch.Text);
    }

    private void OnNeteaseClearClick(object sender, RoutedEventArgs e)
    {
        _settings.NeteaseCookie = "";
        SettingsStore.Save(_settings);
        ApplyNeteaseSettings();
        _player.SetNotice("已清除 Cookie，按匿名方式访问");
    }

    private void OnNeteaseQualityClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string quality }) return;
        _settings.NeteaseQuality = quality;
        SettingsStore.Save(_settings);
        ApplyNeteaseSettings();
        var label = NeteaseApi.QualityChoices.FirstOrDefault(q => q.Value == quality).Label ?? quality;
        _player.SetNotice("在线音质：" + label);
    }

    private void ApplyNeteaseSettings()
    {
        NeteaseApi.Cookie = _settings.NeteaseCookie ?? "";
        NeteaseApi.Quality = _settings.NeteaseQuality ?? "exhigh";
    }

    /// <summary>Rebuilds the 落雪 submenu each time it opens: imported scripts change at runtime.</summary>
    private void OnOnlineMenuOpening(object sender, ContextMenuEventArgs e)
    {
        try
        {
            LxStore.Load();
            miLxSmart.IsChecked = LxStore.SmartSwitch;
            // The first four items are fixed (导入 / 分隔线 / 自动切换 / 移除); everything after is a script.
            while (miLx.Items.Count > 4) miLx.Items.RemoveAt(miLx.Items.Count - 1);

            if (LxStore.Entries.Count == 0)
            {
                miLx.Items.Add(new MenuItem { Header = "（还没有导入脚本）", IsEnabled = false });
                return;
            }
            miLx.Items.Add(new Separator());
            foreach (var entry in LxStore.Entries)
            {
                var item = new MenuItem
                {
                    Header = entry.Error != null ? $"{entry.Name}（{entry.Error}）" : $"{entry.Name}  ·  {entry.Summary}",
                    IsCheckable = true,
                    IsChecked = entry.Id == LxStore.ActiveId,
                    Tag = entry.Id,
                    IsEnabled = entry.Error == null,
                };
                item.Click += OnLxPickClick;
                miLx.Items.Add(item);
            }
        }
        catch
        {
            // A broken index must never make the whole context menu unusable.
        }
    }

    private void OnLxPickClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string id }) return;
        LxStore.SetActive(id);
        var entry = LxStore.Entries.FirstOrDefault(x => x.Id == id);
        _player.SetNotice("落雪音源：" + (entry?.Name ?? id));
    }

    private void OnLxSmartClick(object sender, RoutedEventArgs e)
    {
        LxStore.SetSmartSwitch(miLxSmart.IsChecked);
        _player.SetNotice(LxStore.SmartSwitch ? "已开启：多个音源依次尝试" : "已关闭自动切换");
    }

    private void OnLxRemoveClick(object sender, RoutedEventArgs e)
    {
        if (LxStore.ActiveId.Length == 0)
        {
            _player.SetNotice("还没有选择中的落雪音源");
            return;
        }
        var name = LxStore.Entries.FirstOrDefault(x => x.Id == LxStore.ActiveId)?.Name ?? "音源";
        LxStore.Remove(LxStore.ActiveId);
        _player.SetNotice("已移除：" + name);
    }

    private void OnLxImportClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择落雪（LX）音源脚本",
            Multiselect = true,
            Filter = "落雪音源脚本 (*.js)|*.js|所有文件 (*.*)|*.*",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog(this) != true) return;
        int ok = 0;
        var problems = new List<string>();
        foreach (var path in dialog.FileNames)
        {
            try
            {
                var entry = LxStore.Import(path);
                if (entry.Error != null) problems.Add($"{entry.Name}：{entry.Error}");
                else ok++;
            }
            catch (Exception ex)
            {
                problems.Add(System.IO.Path.GetFileName(path) + "：" + ex.Message);
            }
        }
        _player.SetNotice(problems.Count == 0
            ? $"已导入 {ok} 个音源"
            : $"导入 {ok} 个，{problems.Count} 个有问题：{problems[0]}");
    }

    // ---------- account ----------
    private async void OnLocalClick(object sender, RoutedEventArgs e) => await _player.ShowLibraryAsync();

    /// <summary>Picks audio files (sound files only) and adds them to the library list.</summary>
    private async void OnAddFilesClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择音频文件",
            Multiselect = true,
            Filter = AudioFilter,
            CheckFileExists = true,
        };
        if (dialog.ShowDialog(this) != true) return;
        await _player.AddLibraryFilesAsync(dialog.FileNames);
    }

    private async void OnPickFolderClick(object sender, RoutedEventArgs e)
    {
        var folder = FolderPicker.Pick("选择音乐文件夹（同名 .lrc 会自动识别）", _settings.MusicFolder);
        if (string.IsNullOrEmpty(folder)) return;
        _settings.LibraryFiles.Clear();
        SettingsStore.Save(_settings);
        await _player.ShowLocalAsync(folder);
    }

    private async void OnClearLibraryClick(object sender, RoutedEventArgs e)
    {
        _settings.LibraryFiles.Clear();
        SettingsStore.Save(_settings);
        await _player.ShowLibraryAsync();
    }

    /// <summary>One click on a row plays it. Clicks on the row's own buttons (remove, etc.) are left
    /// alone, and a click that turned into a drag never reaches here because the drag loop swallows
    /// the release.</summary>
    private async void OnSongClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && FindAncestor<Button>(source) != null) return;
        var hit = ItemUnder(e.GetPosition(lstSongs));
        if (hit == null) return;
        if (_player.Current == hit)
        {
            _player.Toggle();
            return;
        }
        await _player.PlayAsync(hit);
    }

    private static T? FindAncestor<T>(DependencyObject node) where T : DependencyObject
    {
        while (node != null)
        {
            if (node is T match) return match;
            node = node is System.Windows.Media.Visual || node is System.Windows.Media.Media3D.Visual3D
                ? System.Windows.Media.VisualTreeHelper.GetParent(node)
                : System.Windows.LogicalTreeHelper.GetParent(node);
        }
        return null;
    }

    // ---------- transport ----------
    private async void OnPlayClick(object sender, RoutedEventArgs e)
    {
        if (_player.Current == null)
        {
            if (lstSongs.SelectedItem is Song selected) await _player.PlayAsync(selected);
            else _player.ToggleOrStart();
            return;
        }
        _player.Toggle();
    }

    private void OnPrevClick(object sender, RoutedEventArgs e) => _player.Previous();
    private void OnNextClick(object sender, RoutedEventArgs e) => _player.Next();

    private bool _seeking;

    /// <summary>正在把「上次听到哪」回填到进度条：这段时间里的 Value 变化不是用户操作，不能触发 seek。</summary>
    private bool _restoringResume;

    /// <summary>拖动进度条：过程中不 seek（媒体管线会被打崩），松手才跳一次。</summary>
    private void EndSeek()
    {
        if (!_seeking) return;
        _seeking = false;
        _lastSeekUtc = DateTime.UtcNow;
        _player.Seek(sldProgress.Value * 1000);
    }

    private DateTime _lastSeekUtc = DateTime.MinValue;

    /// <summary>松手时已经 seek 过一次，Value 还会再报一次变化，这里要挡掉，否则一次点击跳两下。</summary>
    private void OnProgressChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_restoringResume) return;
        if (_seeking || sldProgress.IsMouseCaptureWithin) return;
        if ((DateTime.UtcNow - _lastSeekUtc).TotalMilliseconds < 600) return;
        if (Math.Abs(_player.RawPositionMs / 1000.0 - e.NewValue) > 1.2)
            _player.Seek(e.NewValue * 1000);
    }

    private bool _volumeDragging;

    private void OnVolumeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsLoaded) return;
        _player.Playback.SetVolume(e.NewValue);
        _settings.Volume = e.NewValue;
        // 拖动时每改一格都写一次 settings.json 会卡，松手时统一落一次。
        if (!_volumeDragging) SettingsStore.Save(_settings);
    }

    private void EndVolume()
    {
        if (!_volumeDragging) return;
        _volumeDragging = false;
        SettingsStore.Save(_settings);
    }

    // ---------- state ----------
    private void OnStateChanged()
    {
        Dispatcher.Invoke(() =>
        {
            bool playing = _player.PlaybackState;
            pathPlayIcon.Visibility = playing ? Visibility.Collapsed : Visibility.Visible;
            pathPauseIcon.Visibility = playing ? Visibility.Visible : Visibility.Collapsed;
            if (_player.Notice is { } notice) txtViewSub.Text = notice;
            else if (_player.Playback.Failure is { } failure) txtViewSub.Text = failure;
            else if (_player.Preparing is { } preparing) txtViewSub.Text = preparing;
            else if (_player.IsTrial) txtViewSub.Text = "试听片段 · 网易云 VIP 歌曲登录后可完整播放";
        });
    }

    private void OnTrackChanged()
    {
        Dispatcher.Invoke(() =>
        {
            var song = _player.Current;
            txtTitle.Text = song?.DisplayTitle ?? "未在播放";
            // Artist only: the player bar used to append " · 本地" / " · 网易云", which the user asked to
            // drop (1.2.68) — the row is about the song, not where it came from.
            txtArtist.Text = song?.DisplayArtist ?? "";
            imgCover.Source = song?.Cover;
            RefreshCoverBackdrop();
            sldProgress.Maximum = Math.Max(1, song?.Duration.TotalSeconds ?? 0);
            sldProgress.Value = 0;
            txtDur.Text = song?.DurationText ?? "0:00";

            if (song != null && _settings.Enabled)
            {
                EnsureDesktopLyrics();
                UpdateOverlayForFocus();
            }
        });
    }

    /// <summary>Online artwork arrives after the track started. Repainting only on TrackChanged meant
    /// the player bar stayed on the glyph and the "follow the cover" backdrop fell back to a solid
    /// colour until the next song.</summary>
    private void OnCoverArrived(Song song)
    {
        Dispatcher.Invoke(() =>
        {
            if (!ReferenceEquals(_player.Current, song)) return;
            imgCover.Source = song.Cover;
            RefreshCoverBackdrop();
        });
    }

    private void OnTick(double positionMs)
    {
        var total = _player.Playback.DurationMs;
        if (total <= 0) return;
        // While the user holds the thumb, playback must not drag the value back under the cursor.
        if (_seeking) return;
        // 文件的时长标签可能不存在（网易云下载的 mp3 就没有），OnTrackChanged 里 Maximum 会退化成
        // 1 秒，于是刚开始播放滑块就顶到最右。以媒体自己报的时长为准，差超过 1.5 秒就纠正回来。
        double realTotal = total / 1000.0;
        if (Math.Abs(sldProgress.Maximum - realTotal) > 1.5)
        {
            sldProgress.Maximum = realTotal;
            if (_player.Current is { } song &&
                Math.Abs((song.Duration - TimeSpan.FromMilliseconds(total)).TotalSeconds) > 1.5)
                song.Duration = TimeSpan.FromMilliseconds(total);
        }
        sldProgress.Value = Math.Clamp(positionMs / 1000, 0, total / 1000);
        txtPos.Text = FormatTime(positionMs / 1000);
        txtDur.Text = FormatTime(total / 1000);
    }

    private static string FormatTime(double seconds)
    {
        if (seconds < 0 || double.IsNaN(seconds)) seconds = 0;
        int s = (int)seconds;
        return $"{s / 60}:{s % 60:00}";
    }

    /// <summary>QA: what the player bar's seek row is showing right now.</summary>
    internal string DebugProgressBar() =>
        $"value={sldProgress.Value:0.00} max={sldProgress.Maximum:0.00} pos={_player.RawPositionMs:0} " +
        $"dur={_player.Playback.DurationMs:0} playing={_player.PlaybackState} " +
        $"posText={txtPos.Text} durText={txtDur.Text} cur='{_player.Current?.DisplayTitle}'";

    // ---------- desktop lyrics ----------
    private void EnsureDesktopLyrics()
    {
        if (_desktop != null) return;
        _desktop = new DesktopLyricsWindow(_player, _settings);
        _desktop.SettingsRequested += OpenSettings;
    }

    private void ShowDesktopLyrics()
    {
        EnsureDesktopLyrics();
        _settings.Enabled = true;
        _desktop!.ApplySettings(_settings);
        if (!_desktop.IsVisible) _desktop.Show();
    }

    private void OnLyricsToggle(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _suppressToggle) return;
        SetLyricsEnabled(chkLyrics.IsChecked == true, save: true);
    }

    // ---------- now playing page (inside this window) ----------
    private NowPlayingView? _nowPlayingView;
    private bool _pageClosing;

    /// <summary>Click on the empty part of the player bar opens the now-playing page; clicks that land
    /// on a button / slider / switch keep their own meaning.</summary>
    private void OnPlayerBarClick(object sender, MouseButtonEventArgs e)
    {
        for (var node = e.OriginalSource as DependencyObject; node != null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is ButtonBase or Slider or Thumb) return;
        }
        OpenNowPlaying();
    }

    private void BarLog(string text)
    {
        try
        {
            System.IO.File.AppendAllText(System.IO.Path.Combine(AppPaths.DataDir, "click.log"),
                $"{DateTime.Now:HH:mm:ss} {text}\n");
        }
        catch { }
    }

    /// <summary>Shows the now-playing page on top of the library inside the same window. The page's
    /// layouts need a much smaller window than the library does, so the window minimum is relaxed
    /// while it is open (that is how you reach the portrait form by dragging).</summary>
    private void OpenNowPlaying()
    {
        try
        {
            if (_nowPlayingView == null)
            {
                _nowPlayingView = new NowPlayingView(_player, _settings);
                _nowPlayingView.BackRequested += CloseNowPlaying;
                _nowPlayingView.DesktopLyricsToggled += ReloadDesktopLyricsState;
                playerPage.Children.Add(_nowPlayingView);
            }
            _pageClosing = false;
            MinWidth = 380;
            MinHeight = 460;
            // The page is opaque; collapsing the library keeps its list/player-bar out of every
            // resize layout pass, which is most of the cost while dragging the window edge.
            sidebarLayer.Visibility = Visibility.Collapsed;
            libraryLayer.Visibility = Visibility.Collapsed;
            playerPage.Visibility = Visibility.Visible;
            AnimatePageIn();
            _nowPlayingView.RefreshFromSettings();
        }
        catch (Exception ex)
        {
            BarLog($"now playing failed {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>Rises into place instead of snapping in: opacity, 16 px of lift and a whisper of
    /// scale, all on one curve so the window does not appear to jump.</summary>
    private void AnimatePageIn()
    {
        playerPage.BeginAnimation(OpacityProperty, null);
        playerPage.Opacity = 0;
        playerPage.RenderTransform = new TranslateTransform(0, 14);
        // Wait one layout+render pass: animating a page that has never been painted is what made a
        // bright frame flash in the top-left corner.
        Dispatcher.BeginInvoke(new Action(() =>
        {
            var slide = playerPage.RenderTransform as TranslateTransform;
            var duration = new Duration(TimeSpan.FromMilliseconds(220));
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            var fadeIn = new DoubleAnimation(1, duration) { EasingFunction = ease };
            // Collapse the library only once the page is opaque over it: doing it first let the blue
            // window wash show through the half-faded page, which was the flash in the top-left.
            fadeIn.Completed += (_, _) =>
            {
                sidebarLayer.Visibility = Visibility.Collapsed;
                libraryLayer.Visibility = Visibility.Collapsed;
                // The page paints its own opaque backdrop, so the themed one underneath is fully
                // covered. Leaving it alive costs a full-window gaussian blur (ThemeBlur, up to 60)
                // on every resize frame, which is most of the hitch while dragging the window edge.
                themeBackdrop.Visibility = Visibility.Collapsed;
                playerPage.BeginAnimation(OpacityProperty, null);
                playerPage.Opacity = 1;
            };
            playerPage.BeginAnimation(OpacityProperty, fadeIn);
            slide?.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, duration) { EasingFunction = ease });
        }), DispatcherPriority.Render);
    }
    private void CloseNowPlaying()
    {
        if (playerPage.Visibility != Visibility.Visible || _pageClosing) return;
        _pageClosing = true;
        // Reveal the library underneath straight away so the fade reads as a cross-dissolve, and
        // restore the minimum size now — the page's relaxed limits must not leak into the library.
        sidebarLayer.Visibility = Visibility.Visible;
        libraryLayer.Visibility = Visibility.Visible;
        themeBackdrop.Visibility = Visibility.Visible;
        if (ActualWidth < 1080) Width = 1180;
        if (ActualHeight < 600) Height = 760;
        MinWidth = 1080;
        MinHeight = 600;

        var fade = new DoubleAnimation(0, new Duration(TimeSpan.FromMilliseconds(150)))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn },
        };
        fade.Completed += (_, _) =>
        {
            playerPage.BeginAnimation(OpacityProperty, null);
            playerPage.Opacity = 1;
            playerPage.RenderTransform = Transform.Identity;
            playerPage.Visibility = Visibility.Collapsed;
            _pageClosing = false;
        };
        playerPage.BeginAnimation(OpacityProperty, fade);
    }

    /// <summary>QA: open/close the in-window page without simulating input.</summary>
    public void DebugOpenPlayerPage() => OpenNowPlaying();    public void DebugClosePlayerPage() => CloseNowPlaying();
    public NowPlayingView? PlayerView => _nowPlayingView;
    public string DebugPlayerPageState() =>
        $"visible={playerPage.Visibility} view={(_nowPlayingView != null)} children={playerPage.Children.Count}";

    /// <summary>
    /// 按 spectrum.json 的 Layer 决定频谱挂在哪一层：
    /// <c>overlay</c> → 根 Grid 的最上层（ZIndex 5，播放页 10 仍在它上面），贴窗底、不参与布局；
    /// <c>backdrop</c> → 塞进壁纸层 themeBackdrop，夹在壁纸和面板中间。
    /// </summary>
    private void ApplySpectrumLayer()
    {
        if (bottomSpectrum == null) return;
        bool backdrop = Services.SpectrumOptions.Current.IsBackdrop;
        var host = backdrop ? (Panel)themeBackdrop : rootGrid;
        if (ReferenceEquals(bottomSpectrum.Parent, host)) return;
        if (bottomSpectrum.Parent is Panel old) old.Children.Remove(bottomSpectrum);
        bottomSpectrum.VerticalAlignment = VerticalAlignment.Bottom;
        bottomSpectrum.HorizontalAlignment = HorizontalAlignment.Stretch;
        if (backdrop)
        {
            Panel.SetZIndex(bottomSpectrum, 0);
            themeBackdrop.Children.Add(bottomSpectrum);
        }
        else
        {
            // 11 = 在 playerPage(10) 之上：主页和播放页都要看得到。
            Panel.SetZIndex(bottomSpectrum, 11);
            rootGrid.Children.Add(bottomSpectrum);
        }
    }

    /// <summary>QA：背景暗度，走和界面外观滑块同一条路（ApplySettings）。</summary>
    internal string DebugSetScrim(double value)
    {
        _settings.ThemeScrim = Math.Clamp(value, 0, 100);
        ApplySettings(_settings);
        return $"scrim={_settings.ThemeScrim:0} opacity={themeScrim?.Opacity:0.00}";
    }

    /// <summary>QA：让外部构造的窗口也能走主窗口的设置回调。</summary>
    internal void DebugApplySettings(AppSettings settings) => ApplySettings(settings);

    /// <summary>QA：把当前频谱挂载位置报出来（overlay / backdrop / 无父级）。</summary>
    internal string DebugSpectrumLayer()
    {
        var parent = bottomSpectrum?.Parent as FrameworkElement;
        var name = parent == null ? "none" : (ReferenceEquals(parent, themeBackdrop) ? "backdrop" : parent.Name);
        return $"{name} z={Panel.GetZIndex(bottomSpectrum)} pageZ={Panel.GetZIndex(playerPage)} " +
               $"vis={bottomSpectrum?.Visibility} op={bottomSpectrum?.Opacity:0.00}";
    }

    /// <summary>QA: switch the themed window backdrop on or off, so the resize cost of the full-window
    /// blur can be measured on its own.</summary>
    internal void DebugSetThemeBackdrop(bool on) =>
        themeBackdrop.Visibility = on ? Visibility.Visible : Visibility.Collapsed;

    private const int WM_ENTERSIZEMOVE = 0x0231;
    private const int WM_EXITSIZEMOVE = 0x0232;

    /// <summary>Tells the now-playing page to stop repainting while the window is being dragged, and
    /// to catch up once in a single paint when the loop ends.</summary>
    private IntPtr WindowLoopHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_ENTERSIZEMOVE) _nowPlayingView?.SetWindowDragHold(true);
        else if (msg == WM_EXITSIZEMOVE) _nowPlayingView?.SetWindowDragHold(false);
        return IntPtr.Zero;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape && playerPage.Visibility == Visibility.Visible)
        {
            CloseNowPlaying();
            e.Handled = true;
        }
        base.OnKeyDown(e);
    }

    /// <summary>Called by the now-playing page when it toggles 桌面歌词 from there.</summary>
    public void ReloadDesktopLyricsState()
    {
        SetLyricsCheckbox(_settings.Enabled);
        if (_settings.Enabled) ShowDesktopLyrics();
        else _desktop?.Hide();
        UpdateOverlayForFocus();
    }

    private void OnPlayerBarDoubleClick(object sender, MouseButtonEventArgs e) => OpenNowPlaying();

    private void OnOpenSettingsClick(object sender, RoutedEventArgs e) => OpenSettings();

    /// <summary>侧栏的检查更新：更新源随包发布（update-feed.txt 或编译时烘进去），
    /// 用户自己填过设置就用设置的。</summary>
    private void OnCheckUpdateClick(object sender, RoutedEventArgs e) => _ = CheckUpdateFromSidebarAsync();

    private async Task CheckUpdateFromSidebarAsync()
    {
        if (txtUpdateHint == null || btnCheckUpdate == null) return;
        var feeds = Services.UpdateService.ResolveFeeds(_settings.UpdateFeed);
        if (feeds.Count == 0)
        {
            txtUpdateHint.Text = "未设置更新源";
            return;
        }
        btnCheckUpdate.IsEnabled = false;
        txtUpdateHint.Text = "检查中…";
        try
        {
            var (manifest, feed) = await Services.UpdateService.CheckAnyAsync(feeds);
            if (manifest == null)
            {
                // Say why: "找不到 latest.json" used to cover a timeout, a 404 and a broken feed alike.
                var reason = Services.UpdateService.LastFailure;
                txtUpdateHint.Text = reason == null ? "没有可用的更新源" : "检查失败：" + reason;
                txtUpdateHint.ToolTip = string.Join("\n", feeds);
                return;
            }
            txtUpdateHint.ToolTip = feed;
            string current = Services.UpdateService.CurrentVersion;
            if (!Services.UpdateService.IsNewerThanCurrent(manifest))
            {
                txtUpdateHint.Text = $"已是最新 {current}";
                return;
            }
            var answer = MessageBox.Show(this,
                $"发现新版本 {manifest.Version}（当前 {current}）\n\n{manifest.Notes}\n\n下载并重启更新？",
                "检查更新", MessageBoxButton.OKCancel, MessageBoxImage.Question);
            if (answer != MessageBoxResult.OK)
            {
                txtUpdateHint.Text = manifest.Version + " 可更新";
                return;
            }
            if (Services.VersionStore.Enabled)
            {
                // 版本目录布局：只新增 versions\vX 并原子切指针，绝不覆盖正在运行的文件。
                var progressInstall = new Progress<double>(p => txtUpdateHint.Text = $"下载中 {p * 100:0}%");
                txtUpdateHint.Text = "下载中 0%";
                var installed = await Services.UpdateService.InstallAsync(feed, manifest, progressInstall);
                txtUpdateHint.Text = $"正在重启到 {installed}…";
                var launcher = Services.Layout.LauncherPath;
                if (System.IO.File.Exists(launcher)) System.Diagnostics.Process.Start(launcher);
                _forceExit = true;
                Application.Current.Shutdown();
                return;
            }

            var progress = new Progress<double>(p => txtUpdateHint.Text = $"下载中 {p * 100:0}%");
            var zip = await Services.UpdateService.FetchAsync(feed, manifest, progress);
            txtUpdateHint.Text = "正在重启更新…";
            Services.UpdateService.ApplyAndRestart(zip);
            _forceExit = true;
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            txtUpdateHint.Text = "更新失败：" + ex.GetType().Name;
        }
        finally
        {
            btnCheckUpdate.IsEnabled = true;
        }
    }

    private SpectrumWindow? _spectrumWindow;

    private void OnOpenSpectrumClick(object sender, RoutedEventArgs e)
    {
        if (_spectrumWindow is { IsVisible: true })
        {
            _spectrumWindow.Activate();
            return;
        }
        _spectrumWindow = new SpectrumWindow(RefreshSpectrumPlacement) { Owner = this };
        _spectrumWindow.Closed += (_, _) => _spectrumWindow = null;
        _spectrumWindow.Show();
    }

    /// <summary>频谱设置窗改过参数之后调：重新挂一次层，再重跑一遍调色（面板透明度跟着层走）。</summary>
    internal void RefreshSpectrumPlacement()
    {
        ApplySpectrumLayer();
        ApplyAppearance();
    }

    private AppearanceWindow? _appearanceWindow;

    private void OnOpenAppearanceClick(object sender, RoutedEventArgs e)
    {
        if (_appearanceWindow is { IsVisible: true })
        {
            _appearanceWindow.Activate();
            return;
        }
        _appearanceWindow = new AppearanceWindow(_settings, ApplySettings, ApplyAppearance) { Owner = this };
        _appearanceWindow.Closed += (_, _) => _appearanceWindow = null;
        _appearanceWindow.Show();
    }

    private void OpenSettings()
    {
        if (_settingsWindow is { IsVisible: true })
        {
            _settingsWindow.Activate();
            return;
        }
        _settingsWindow = new SettingsWindow(_settings, ApplySettings, _player);
        _settingsWindow.Closed += (_, _) => { _settingsWindow = null; UpdateOverlayForFocus(); };
        _settingsWindow.Activated += (_, _) => UpdateOverlayForFocus();
        _settingsWindow.Deactivated += (_, _) => UpdateOverlayForFocus();
        _settingsWindow.Show();
    }

    private DownloadWindow? _downloadWindow;

    private void OnOpenDownloadClick(object sender, RoutedEventArgs e) => OpenDownloadSettings();

    /// <summary>下载与存储 sits on its own — the user explicitly did not want the download folder
    /// buried in the desktop-lyrics window.</summary>
    private void OpenDownloadSettings()
    {
        if (_downloadWindow is { IsVisible: true })
        {
            _downloadWindow.Activate();
            return;
        }
        _downloadWindow = new DownloadWindow(_settings, ApplySettings) { Owner = this };
        _downloadWindow.Closed += (_, _) => _downloadWindow = null;
        _downloadWindow.Show();
    }

    /// <summary>QA: the download settings window, so it can be screenshotted.</summary>
    internal DownloadWindow? DebugDownloadWindow => _downloadWindow;

    internal void DebugOpenDownloadSettings() => OpenDownloadSettings();

    private DispatcherTimer? _settingsSaveTimer;
    private string _themeSignature = "";

    private void ApplySettings(AppSettings settings)
    {
        // 拖动设置里的滑条会连续调用这里；写盘放到停手之后，配色只在真的变了才重建。
        if (_settingsSaveTimer == null)
        {
            _settingsSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            _settingsSaveTimer.Tick += (_, _) =>
            {
                _settingsSaveTimer!.Stop();
                SettingsStore.Save(_settings);
            };
        }
        _settingsSaveTimer.Stop();
        _settingsSaveTimer.Start();

        SetLyricsCheckbox(settings.Enabled);
        if (settings.Enabled) ShowDesktopLyrics();
        else _desktop?.Hide();
        _desktop?.ApplySettings(settings);
        _nowPlayingView?.RefreshFromSettings();
        // The 下载与存储 window edits the cookie/quality too, so the API client has to follow it.
        ApplyNeteaseSettings();
        string signature = $"{settings.ThemeImage}|{settings.ThemeAccent}|{settings.ThemeSurface}|{settings.ThemeOpacity:0.##}|{settings.ThemeScrim:0.##}|{Services.SpectrumOptions.Current.Layer}|{Services.SpectrumOptions.Current.PanelSeeThrough}";
        if (signature != _themeSignature)
        {
            _themeSignature = signature;
            ApplyAppearance();
        }
        // 背景磨砂不在上面那个签名里（重排配色不需要跟着重跑），但它只能靠 ApplyAppearance 落到
        // themeImage.Effect 上 —— 以前这里什么都不做，所以磨砂滑块拖到底也没反应。
        if (Math.Abs(settings.ThemeBlur - _themeBlurApplied) > 0.01) ApplyThemeBlur();
        UpdateOverlayForFocus();
    }

    private double _themeBlurApplied = double.NaN;

    /// <summary>Frosting of the backdrop image, 0..60 px. Cheap on its own: only the blurred element's
    /// effect changes, so a slider drag does not re-run the palette.</summary>
    private void ApplyThemeBlur()
    {
        double blur = Math.Clamp(_settings.ThemeBlur, 0, 60);
        if (Math.Abs(blur - _themeBlurApplied) < 0.01) return;
        _themeBlurApplied = blur;
        themeImage.Effect = blur > 0.5
            ? new System.Windows.Media.Effects.BlurEffect
            {
                Radius = blur,
                RenderingBias = System.Windows.Media.Effects.RenderingBias.Performance,
            }
            : null;
        ApplyThemeImageGeometry(blur);
    }

    /// <summary>
    /// The window backdrop is a "fill and crop" picture, so the picture has to be centred and bled
    /// past the window edges. It used to be laid out at the window's top-left corner: the window then
    /// showed the artwork's top-left corner instead of its middle, which put the thin light border
    /// baked into the cover file exactly on the window's top edge — a bright strip that faded out
    /// over the blur radius (user report: 窗口上方有一个颜色渐变). The bleed also keeps the blur's own
    /// edge fade (the blur samples outside the picture, so its outermost pixels turn transparent)
    /// outside the visible area, so the top edge is just picture.
    /// </summary>
    private void ApplyThemeImageGeometry(double blur)
    {
        double pad = Math.Max(16, blur * 2 + 16);
        var margin = new Thickness(-pad);
        if (themeImage.Margin != margin) themeImage.Margin = margin;
        // The negative margin exists so that the blur's own edge fade (it samples outside the picture,
        // so the outermost pixels turn transparent) lands outside the window. UniformToFill then had to
        // scale the picture up to fill that padded box — which is why 背景磨砂 also *zoomed* the image.
        // Size the image at the window's own cover scale and let the extra ring show more of the source
        // instead of a magnified crop, so the framing no longer changes with the blur radius.
        if (themeImage.Source is not BitmapSource bmp || bmp.PixelWidth <= 0 || bmp.PixelHeight <= 0)
        {
            _geomLog = "no-src";
            return;
        }
        double w = themeBackdrop.ActualWidth > 4 ? themeBackdrop.ActualWidth : (Width > 4 ? Width : 0);
        double h = themeBackdrop.ActualHeight > 4 ? themeBackdrop.ActualHeight : (Height > 4 ? Height : 0);
        if (w < 4 || h < 4)
        {
            _geomLog = $"tiny w={w:0.0} h={h:0.0}";
            return;
        }
        double scale = Math.Max(w / bmp.PixelWidth, h / bmp.PixelHeight);
        double width = bmp.PixelWidth * scale;
        double height = bmp.PixelHeight * scale;
        // NaN != NaN: comparing a never-set Width with a number yields NaN, and NaN > 0.5 is false, so
        // the assignment below never happened and the image stayed UniformToFill-sized (the zoom).
        if (double.IsNaN(themeImage.Width) || double.IsNaN(themeImage.Height)
            || Math.Abs(themeImage.Width - width) > 0.5 || Math.Abs(themeImage.Height - height) > 0.5)
        {
            themeImage.Width = width;
            themeImage.Height = height;
            _geomLog = $"set {width:0}x{height:0} (window {w:0}x{h:0})";
        }
    }

    private string _geomLog = "";

    /// <summary>QA: whether the window is in see-through (layered) mode.</summary>
    public bool DebugSeeThrough => _glassMode && _seeThrough;

    private bool _seeThrough;

    /// <summary>Recolours the whole window from the theme colour and rebuilds the backdrop. The
    /// backdrop can be a photo on disk or "当前歌曲封面", which follows the playing track.</summary>
    public void ApplyAppearance()
    {
        string raw = (_settings.ThemeImage ?? "").Trim();
        bool coverMode = _coverBackdropOverride ||
                         string.Equals(raw, Services.Palette.CoverToken, StringComparison.OrdinalIgnoreCase);
        string path = !coverMode && raw.Length > 0 && System.IO.File.Exists(raw) ? raw : "";
        bool photo = coverMode || path.Length > 0;
        // 频谱在壁纸层时，面板必须让出透明，否则柱子一根都透不出来（0..80 由「面板透出」控制）。
        byte cap = 0xFF;
        if (Services.SpectrumOptions.Current.IsBackdrop)
        {
            int see = Math.Clamp(Services.SpectrumOptions.Current.PanelSeeThrough, 0, 80);
            cap = (byte)Math.Round(255.0 * (100 - see) / 100.0);
        }
        Services.Palette.Apply(_settings.ThemeAccent, _settings.ThemeSurface, photo, cap);
        // 背景色选「透明」（8 位 AARRGGBB，alpha=00）时窗口必须是分层窗口才真的透过去看得到下层应用；
        // 这个属性只能在窗口创建前决定，所以切换要重启。若现在还不是玻璃窗口，这里先不透明，
        // 并给一句提示（见下面的 toast）。
        bool wantSeeThrough = Services.Palette.SurfaceAlpha(
            Services.Palette.Normalize(_settings.ThemeSurface, Services.Palette.DefaultSurface)) < 0xFF;
        _seeThrough = wantSeeThrough && _glassMode;
        // Same 1/255 trick as in the constructor: 0 would make the whole window click-through.
        Background = _seeThrough
            ? new SolidColorBrush(Color.FromArgb(1, 0, 0, 0))
            : (Brush)FindResource("WindowWashBrush");
        if (wantSeeThrough && !_glassMode)
            ShowToast("透明背景需要重启后生效");
        // The nav chip and the toolbar keep brush instances, not bindings, so they have to be handed
        // the new palette by hand or they would stay on the colour they were built with.
        btnLocal.Background = _libraryActive ? (Brush)FindResource("NavActiveBrush") : Brushes.Transparent;

        double opacity = Math.Clamp(_settings.ThemeOpacity, 0, 100) / 100.0;
        themeBackdrop.Opacity = opacity;
        // 背景暗度：和播放页的「背景暗度」一样，就是一层黑压在壁纸上。
        if (themeScrim != null) themeScrim.Opacity = Math.Clamp(_settings.ThemeScrim, 0, 100) / 100.0;

        if (coverMode)
        {
            _themeImagePath = null;
            SetThemeImage(_player.Current?.Cover);
        }
        else if (path != _themeImagePath)
        {
            _themeImagePath = path;
            SetThemeImage(path.Length == 0 ? null : LoadBitmap(path));
        }

        double blur = Math.Clamp(_settings.ThemeBlur, 0, 60);
        themeImage.Effect = blur > 0.5
            ? new System.Windows.Media.Effects.BlurEffect
            {
                Radius = blur,
                RenderingBias = System.Windows.Media.Effects.RenderingBias.Performance,
            }
            : null;
        ApplyThemeImageGeometry(blur);
        _themeBlurApplied = blur;
        // At OnLoaded the window has no measured size yet, so the geometry above bails out; redo it once
        // layout is done (this is also what makes the first frame already un-zoomed).
        Dispatcher.BeginInvoke(new Action(() => ApplyThemeImageGeometry(Math.Clamp(_settings.ThemeBlur, 0, 60))),
            System.Windows.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>Song-cover backdrops change with the track, so this runs on every track change.</summary>
    private void RefreshCoverBackdrop()
    {
        if (!string.Equals((_settings.ThemeImage ?? "").Trim(), Services.Palette.CoverToken,
                StringComparison.OrdinalIgnoreCase)) return;
        SetThemeImage(_player.Current?.Cover);
    }

    private BitmapSource? _themeCover;

    private void SetThemeImage(BitmapSource? source)
    {
        if (ReferenceEquals(_themeCover, source) && themeImage.Source == source) return;
        _themeCover = source;
        themeImage.Source = source;
        // The cover-mode backdrop arrives *after* layout (it is loaded from disk / the network), so the
        // geometry pass in ApplyAppearance always bailed with "no-src" and the image kept whatever size
        // the previous track had. Re-run it here once the bitmap actually exists.
        ApplyThemeImageGeometry(_themeBlurApplied);
    }

    private static BitmapSource? LoadBitmap(string path)
    {
        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.UriSource = new Uri(path);
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>QA: switch the backdrop to "playing track's cover" in memory only — the settings
    /// file is left untouched so a screenshot run never changes what the user sees next start.</summary>
    internal void DebugUseCoverBackdrop()
    {
        _coverBackdropOverride = true;
        ApplyAppearance();
    }

    /// <summary>QA: run the exact path the 界面外观 sliders use (ApplySettings, not ApplyAppearance) —
    /// that difference is what hid the 背景磨砂 slider doing nothing.</summary>
    internal void DebugApplySettings() => ApplySettings(_settings);

    /// <summary>QA: what the backdrop image's effect actually is.</summary>
    internal string DebugThemeEffect() => themeImage.Effect switch
    {
        System.Windows.Media.Effects.BlurEffect b => $"blur r={b.Radius:0.##}",
        null => "none",
        _ => "other",
    };

    /// <summary>QA: render the backdrop image on its own (effect included) so a blur radius can be
    /// measured in pixels, without whatever the window paints on top of it.</summary>
    internal string DebugSaveThemeImage(string path)
    {
        int w = (int)Math.Round(themeImage.ActualWidth), h = (int)Math.Round(themeImage.ActualHeight);
        if (themeImage.Source == null) return $"src=null size={w}x{h}";
        if (w < 8 || h < 8) return $"src={themeImage.Source.Width}x{themeImage.Source.Height} size={w}x{h}";
        var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(w, h, 96, 96,
            System.Windows.Media.PixelFormats.Pbgra32);
        rtb.Render(themeImage);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
        using (var stream = System.IO.File.Create(path)) encoder.Save(stream);
        return $"src={themeImage.Source.Width}x{themeImage.Source.Height} size={w}x{h} effect={DebugThemeEffect()}";
    }

    /// <summary>QA only: pretend "当前歌曲封面" is the chosen backdrop without touching settings.</summary>
    private bool _coverBackdropOverride;

    /// <summary>QA: what the window is actually painting for the playing track's artwork.</summary>
    internal string DebugCoverState() =>
        $"current='{_player.Current?.DisplayTitle}' " +
        $"cover={(_player.Current?.Cover != null ? "bitmap" : "null")} " +
        $"coverUrl={(_player.Current?.CoverUrl != null ? "url" : "none")} " +
        $"barCover={(imgCover.Source != null ? "bitmap" : "null")} " +
        $"backdrop={(themeImage.Source != null ? "bitmap" : "null")} " +
        $"coverMode={_coverBackdropOverride || string.Equals((_settings.ThemeImage ?? "").Trim(), Services.Palette.CoverToken, StringComparison.OrdinalIgnoreCase)}";

    /// <summary>QA: state of the drag layer frame by frame, so a landing hitch can be traced.</summary>
    internal string DebugDropState()
    {
        string ghost = _ghost == null
            ? "none"
            : $"top={Canvas.GetTop(_ghost):0} op={_ghost.Opacity:0.00} h={_ghost.ActualHeight:0}";
        int animated = 0;
        foreach (var song in _view)
        {
            if (lstSongs.ItemContainerGenerator.ContainerFromItem(song) is ListBoxItem item &&
                item.RenderTransform is TranslateTransform t && Math.Abs(t.Y) > 0.5)
                animated++;
        }
        return $"ghost={ghost} insert={_insertIndex} rows={_view.Count} sliding={animated} " +
               $"listOpacity={lstSongs.Opacity:0.00} gap={_gapOpen:0} " +
               $"dropMs={_lastDropMs:0.0}";
    }

    /// <summary>QA: which rows still have a visible container (a drop that never restores the
    /// lifted row leaves it collapsed, i.e. the song disappears from the list).</summary>
    internal string DebugRowVisibility()
    {
        int visible = 0;
        var bad = new List<string>();
        for (int i = 0; i < _view.Count; i++)
        {
            if (lstSongs.ItemContainerGenerator.ContainerFromItem(_view[i]) is ListBoxItem item)
            {
                if (item.Visibility == Visibility.Visible) visible++;
                else bad.Add($"{i}={_view[i].DisplayTitle}({item.Visibility})");
            }
            else
            {
                bad.Add($"{i}={_view[i].DisplayTitle}(no-container)");
            }
        }
        return $"rows={_view.Count} visible={visible} srcItem={(_sourceItem == null ? "null" : "set")} bad=[{string.Join(";", bad)}]";
    }

    /// <summary>QA: frame timing of the landing animation, so "is it rendering or is it the code"
    /// can be answered with numbers instead of a feeling.</summary>
    private readonly System.Diagnostics.Stopwatch _frameClock = System.Diagnostics.Stopwatch.StartNew();
    private double _lastFrameMs;
    private double _worstFrameMs;
    private int _frameCount;
    private bool _frameMonitor;
    private readonly List<double> _frameDeltas = new();
    private double _monitorStartMs;

    internal void StartFrameMonitor(int ms)
    {
        _frameMonitor = true;
        _frameCount = 0;
        _worstFrameMs = 0;
        _frameDeltas.Clear();
        _monitorStartMs = _frameClock.Elapsed.TotalMilliseconds;
        _lastFrameMs = _monitorStartMs;
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            _frameMonitor = false;
        };
        timer.Start();
    }

    private void OnFrameTick(object? sender, EventArgs e)
    {
        if (!_frameMonitor) return;
        double now = _frameClock.Elapsed.TotalMilliseconds;
        double delta = now - _lastFrameMs;
        _lastFrameMs = now;
        if (delta <= 0 || delta > 400) return;      // pause/gap, not a rendered frame
        _frameCount++;
        _frameDeltas.Add(delta);
        if (delta > _worstFrameMs) _worstFrameMs = delta;
    }

    internal string DebugFrameStats()
    {
        double span = Math.Max(1, _frameClock.Elapsed.TotalMilliseconds - _monitorStartMs);
        double fps = _frameCount * 1000.0 / span;
        double avg = _frameDeltas.Count > 0 ? _frameDeltas.Average() : 0;
        int over20 = 0, over33 = 0, over6 = 0, over9 = 0;
        foreach (var d in _frameDeltas)
        {
            if (d > 20) over20++;
            if (d > 33) over33++;
            if (d > 6.2) over6++;
            if (d > 9) over9++;
        }
        return $"fps={fps:0} frames={_frameCount} avgMs={avg:0.0} worstMs={_worstFrameMs:0.0} " +
               $"over6={over6} over9={over9} over20={over20} over33={over33}";
    }

    /// <summary>QA: opens the desktop-lyrics settings window so its sliders can be timed.</summary>
    internal void DebugOpenDesktopSettings() => OpenSettings();

    internal SettingsWindow? DebugSettingsWindow => _settingsWindow;

    private string? _themeImagePath;
}
