using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using CurrentLyrics.Controls;
using CurrentLyrics.Models;
using CurrentLyrics.Services;

namespace CurrentLyrics.Views;

/// <summary>
/// Frameless desktop lyric column: multi-line stack with the current cue anchored in place,
/// per-word karaoke ink and held-note glow. No cover, no pill — just text over the desktop.
/// </summary>
public sealed class DesktopLyricsWindow : Window
{
    private readonly PlayerController _player;
    private AppSettings _settings;
    private readonly LyricStackView _stack;
    private readonly StackPanel _meta;
    private readonly TextBlock _metaTitle;
    private readonly TextBlock _metaArtist;
    private readonly Grid _root;

    /// <summary>Host for the lyric column; carries the top/bottom alpha ramp.</summary>
    private readonly Grid _lyricsHost;

    private bool _dragging;
    private Point _dragStart;
    private double _dragLeft, _dragTop;
    private DateTime _lastClick = DateTime.MinValue;
    private bool _demo;
    private bool _userMove;
    private readonly Image _backdropImage;
    private BitmapSource? _backdropBitmap;
    private double _backdropOpacity = 1.0;
    private System.Windows.Media.Imaging.BitmapSource? _backdropCustom;
    private bool _loggedVisible;
    private ContextMenu? _menu;

    public event Action? SettingsRequested;

    public DesktopLyricsWindow(PlayerController player, AppSettings settings)
    {
        _player = player;
        _settings = settings;

        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        // Hardware rendering by default. It used to be forced to software because "a layered window
        // rendered by D3D can quantise per-pixel alpha" — that was really the premultiplied-bitmap bug
        // (fixed, verified: the ramp is identical on GPU). Measured: software ~9 ms/frame, GPU ~4 ms.
        // LYRIC_SW=1 forces the old software path for A/B.
        System.Windows.Media.RenderOptions.ProcessRenderMode =
            Environment.GetEnvironmentVariable("LYRIC_SW") == "1"
                ? System.Windows.Interop.RenderMode.SoftwareOnly
                : System.Windows.Interop.RenderMode.Default;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = settings.OverlayAboveApps && settings.BackdropMode == BackdropMode.Transparent;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Title = "Darius Music";
        SizeToContent = SizeToContent.Manual;
        Width = settings.LineWidth + 160;
        Height = 640;

        _stack = new LyricStackView
        {
            // The backdrop is a sibling element (see _backdropImage): it carries the baked edge ramp,
            // and the stack only draws glyphs on top of it.
            BackdropDrawnByHost = true,
            // Hit-testable so the lyric column itself can be dragged (and right-clicked).
            IsHitTestVisible = true,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
        };

        _metaTitle = new TextBlock
        {
            FontSize = 34,
            FontWeight = FontWeights.Medium,
            Foreground = Brushes.White,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        _metaArtist = new TextBlock
        {
            FontSize = 20,
            Foreground = new SolidColorBrush(Color.FromArgb(0xAA, 0xFF, 0xFF, 0xFF)),
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 10, 0, 0),
        };
        _meta = new StackPanel
        {
            Orientation = Orientation.Vertical,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Visibility = Visibility.Collapsed,
        };
        _metaTitle.Text = "桌面歌词";
        _metaArtist.Text = "播放歌曲后显示歌词";
        _meta.Children.Add(_metaTitle);
        _meta.Children.Add(_metaArtist);

        // The backdrop is a plain Image element whose bitmap already carries frost, edge ramp and
        // opacity. Keeping it out of the per-frame path is what makes the overlay cheap (~12% of one
        // core while playing, vs ~50% when the whole window is recomposited every frame).
        _backdropImage = new Image
        {
            Stretch = Stretch.Fill,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            IsHitTestVisible = false,
        };

        // The lyric column sits inside its own host so a vertical alpha ramp can be applied to the
        // text alone (the backdrop image is a sibling and must not be masked). Without it a cue that
        // ran into the window edge was sliced mid-glyph; the per-line alpha alone can only dim a whole
        // cue, not fade one.
        _lyricsHost = new Grid();
        _lyricsHost.Children.Add(_stack);
        _lyricsHost.SizeChanged += (_, _) => UpdateLyricsMask();

        _root = new Grid();
        _root.Children.Add(_backdropImage);
        _root.Children.Add(_lyricsHost);
        _root.Children.Add(_meta);
        Content = _root;

        _stack.MouseLeftButtonDown += OnMouseDown;
        _stack.MouseMove += OnMouseMove;
        _stack.MouseLeftButtonUp += OnMouseUp;
        _stack.MouseRightButtonUp += (_, _) => ShowMenu();
        _stack.MouseWheel += OnWheel;

        _root.MouseLeftButtonDown += OnMouseDown;
        _root.MouseMove += OnMouseMove;
        _root.MouseLeftButtonUp += OnMouseUp;
        _root.MouseRightButtonUp += (_, _) => ShowMenu();
        _root.MouseWheel += OnWheel;

        _player.Tick += OnTick;
        _player.TrackChanged += OnTrackChanged;
        _player.CoverChanged += OnCoverArrived;
        _player.LyricsChanged += RefreshLyrics;
        System.Windows.Media.CompositionTarget.Rendering += OnCompositionRendering;
        Closed += (_, _) =>
        {
            System.Windows.Media.CompositionTarget.Rendering -= OnCompositionRendering;
            _player.CoverChanged -= OnCoverArrived;
            SetForegroundHook(false);
            SetAboveWatchdog(false);
        };

        Loaded += (_, _) =>
        {
            ApplySettings(settings);
            RepositionWindow();
            AnimateEntrance();
            // The overlay is normally switched on *while a track is already playing*. Nothing would
            // hand the fresh window its document in that case: LyricsChanged fired before it existed,
            // and the per-tick path only refreshes the placeholder — so the column stayed empty (only
            // the backdrop was visible) until the next track change.
            RefreshLyrics();
            LogState("loaded");
        };
        SourceInitialized += (_, _) =>
        {
            ApplyWindowStyles();
            ApplyOverlayTopmost();
            if (PresentationSource.FromVisual(this) is HwndSource source) source.AddHook(WndProc);
        };
        // Show() after a Hide() drops the window at the bottom of the topmost band, and nothing tells the
        // foreground hook about it (the foreground did not change). Re-assert on every appearance.
        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible) return;
            ApplyOverlayTopmost();
            if (_settings.OverlayAboveApps) QueueRaise();
        };
        // 背景宽高 changes the window size, and ActualWidth still reports the *old* size while
        // ApplySettings runs, so the backdrop has to be rebuilt once the new layout is in place.
        SizeChanged += (_, _) =>
        {
            if (!IsLoaded) return;
            if (_settings.BackdropMode == BackdropMode.Transparent)
            {
                _backdropBitmap = null;
                _backdropImage.Source = null;
            }
            else
            {
                UpdateBackdropVisual(_settings);
            }
            PaintSoon();
        };

        // A window that was filled before it had a presentation source, or that was hidden while the
        // track changed, can come back with its backdrop element intact and the glyph column never
        // painted. That reads as "desktop lyrics are broken", so every show and every track/lyric
        // load asks for one extra paint.
        IsVisibleChanged += (_, _) => { if (IsVisible) PaintSoon(); };
    }

    /// <summary>Repaints the column immediately (cheap, idempotent, one frame).</summary>
    public void ForceRepaint()
    {
        _stack.InvalidateVisual();
        UpdateLayout();
    }

    /// <summary>Repaints twice: now, and again once the dispatcher has laid the window out.</summary>
    private void PaintSoon()
    {
        _stack.InvalidateVisual();
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _stack.InvalidateVisual();
            UpdateLayout();
        }), System.Windows.Threading.DispatcherPriority.Render);
    }

    /// <summary>
    /// Dragging is done by the window manager (HTCAPTION) instead of WPF mouse events: the layered,
    /// no-activate window does not reliably deliver them.
    /// </summary>
    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_NCHITTEST = 0x0084;
        const int WM_NCRBUTTONUP = 0x00A5;
        const int WM_ENTERSIZEMOVE = 0x0231;
        const int WM_EXITSIZEMOVE = 0x0232;
        const int WM_NCLBUTTONDBLCLK = 0x00A3;
        const int HTTRANSPARENT = -1;
        const int HTCAPTION = 2;

        if (msg == WM_NCHITTEST)
        {
            handled = true;
            return _settings.Locked ? (IntPtr)HTTRANSPARENT : (IntPtr)HTCAPTION;
        }
        if (msg == WM_ENTERSIZEMOVE)
        {
            // Only a move the *user* started may be persisted. A real drag always begins with the
            // left button held; the window manager also sends ENTERSIZEMOVE for its own round-trips
            // (window show, SetWindowPos, style changes), and persisting those shifted the overlay
            // by a few dozen DIP on every programmatic show/close.
            _userMove = (GetAsyncKeyState(0x01) & 0x8000) != 0;   // VK_LBUTTON
            return IntPtr.Zero;
        }
        if (msg == WM_EXITSIZEMOVE)
        {
            if (_userMove)
            {
                _userMove = false;
                RememberPosition();
            }
            return IntPtr.Zero;
        }
        if (msg == WM_NCRBUTTONUP)
        {
            // lParam carries the *screen* point of the click as two signed shorts. Passing it down is
            // what keeps the menu at the cursor: asking WPF for the mouse position instead returns
            // (0,0) for this no-activate layered window, which is why the menu sometimes opened in
            // the top-left corner of the screen.
            long packed = (long)lParam;
            int screenX = unchecked((short)(packed & 0xFFFF));
            int screenY = unchecked((short)((packed >> 16) & 0xFFFF));
            ShowMenu(screenX, screenY);
            handled = true;
            return IntPtr.Zero;
        }
        if (msg == WM_NCLBUTTONDBLCLK)
        {
            handled = true;
            return IntPtr.Zero;
        }
        return IntPtr.Zero;
    }

    /// <summary>
    /// A drag is performed by the window manager, so WPF's Left/Top never change. Without this the
    /// next layout pass snapped the overlay back to the centred position — which is why dragging
    /// looked like it did nothing.
    /// </summary>
    private void RememberPosition()
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;
            if (!GetWindowRect(hwnd, out var rect)) return;
            var dpi = VisualTreeHelper.GetDpi(this);
            _settings.WindowX = rect.Left / dpi.DpiScaleX;
            _settings.WindowY = rect.Top / dpi.DpiScaleY;
            _settings.CustomPosition = true;
            SettingsStore.Save(_settings);
            LogState($"drag-saved {_settings.WindowX:0},{_settings.WindowY:0}");
        }
        catch (Exception ex) { LogState("drag-save-failed " + ex.GetType().Name + ": " + ex.Message); }
    }

    // ---------- hotkey confirmation ----------
    private Border? _toast;
    private System.Windows.Threading.DispatcherTimer? _toastTimer;

    /// <summary>
    /// A one-line confirmation at the top of the overlay, for hotkeys whose result is otherwise
    /// invisible (显示在其他应用上方 / 没有歌可放). 1.5 s, hit-test off, never touches the lyric column.
    /// </summary>
    public void ShowToast(string text)
    {
        if (_root == null) return;
        if (_toast == null)
        {
            var label = new TextBlock
            {
                FontSize = 13,
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
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 14, 0, 0),
                IsHitTestVisible = false,
                Opacity = 0,
            };
            Panel.SetZIndex(_toast, 90);
            _root.Children.Add(_toast);
            _toastTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
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

    // ---------- window plumbing ----------
    /// <summary>
    /// "显示在其他应用上层". A plain topmost window still loses to another topmost window that gets
    /// activated — most fullscreen games, media players and Task Manager are topmost — so a topmost
    /// overlay ends up hidden behind them. With this on, the overlay puts itself back at the top of the
    /// topmost band every time another window is activated (no activation of its own: the window is
    /// WS_EX_NOACTIVATE, so the bump never takes focus from the app underneath).
    /// Transparent backdrop only; <see cref="AppSettings.Normalized"/> clears it in the other modes.
    /// </summary>
    private void ApplyOverlayTopmost()
    {
        // 唯一的置顶开关。关掉它就是普通窗口：别的窗口（包括播放器自己）会盖住歌词，这样开关
        // 两个方向都能立刻看见变化；以前还叠了一个 Topmost，普通窗口下两边画面一模一样，
        // 用户只能判成「关了也还在上面」。
        bool above = _settings.OverlayAboveApps && _settings.BackdropMode == BackdropMode.Transparent;
        Topmost = above;
        if (above) RaiseAbove();
        else SinkBelowOthers();
        SetForegroundHook(above);
        SetAboveWatchdog(above);
    }

    /// <summary>
    /// The OFF direction has to move the window, not just clear WS_EX_TOPMOST: <c>Topmost = false</c>
    /// sends HWND_NOTOPMOST, which parks the window at the TOP of the non-topmost band — still covering
    /// every normal app, so the switch looked dead until something else re-stacked the screen (user
    /// report: 关闭时无法及时生效，要刷新整个屏幕). Sinking to HWND_BOTTOM makes other windows cover the
    /// lyrics immediately; the desktop is still behind it, so they stay visible on an empty desktop.
    /// </summary>
    private void SinkBelowOthers()
    {
        // QA A/B: DARIUS_NOSINK=1 keeps the old behaviour (only clear WS_EX_TOPMOST) so the difference
        // can be measured on the same binary — that old behaviour is the reported bug.
        if (Environment.GetEnvironmentVariable("DARIUS_NOSINK") == "1") return;
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_BOTTOM, 0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE |
            NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_NOOWNERZORDER);
        _sinkCount++;
    }

    private int _sinkCount;

    private void RaiseAbove()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        // Our own settings window / dialogs must stay usable: while one of them is the active window
        // the overlay stays out of the way instead of parking itself over the sliders.
        if (OwnWindowActive())
        {
            _raiseSkipped++;
            return;
        }
        _raiseCount++;
        NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE |
            NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_NOOWNERZORDER);
        _raiseError = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
    }

    private int _raiseCount;
    private int _raiseSkipped;
    private int _raiseError;

    /// <summary>QA (--above-qa): how often the overlay actually asked to be put on top, how often it
    /// stood down because one of our own windows was active, and the last Win32 error.</summary>
    public string DebugAboveState =>
        $"raises={_raiseCount} skipped={_raiseSkipped} err={_raiseError} sinks={_sinkCount} " +
        $"hook={(_foregroundHook != IntPtr.Zero)} watchdog={(_aboveWatchdogTimer != null)} " +
        $"visible={IsVisible} topmost={Topmost}";

    private bool OwnWindowActive()
    {
        var app = System.Windows.Application.Current;
        if (app == null) return false;
        foreach (Window window in app.Windows)
            if (!ReferenceEquals(window, this) && window.IsActive) return true;
        return false;
    }

    private System.Windows.Threading.DispatcherTimer? _raiseSoon;
    private System.Windows.Threading.DispatcherTimer? _aboveWatchdogTimer;

    /// <summary>
    /// A window that takes the foreground is raised *after* the foreground event is delivered, so a
    /// bump from inside the event loses the race and the newcomer stays on top. Do it again on the next
    /// dispatcher turn and once more a beat later (a game that goes fullscreen raises itself twice).
    /// </summary>
    private void QueueRaise()
    {
        Dispatcher.BeginInvoke(new Action(RaiseAbove), System.Windows.Threading.DispatcherPriority.Background);
        if (_raiseSoon == null)
        {
            _raiseSoon = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(220) };
            _raiseSoon.Tick += (_, _) => { _raiseSoon!.Stop(); RaiseAbove(); };
        }
        _raiseSoon.Stop();
        _raiseSoon.Start();
    }

    private void SetAboveWatchdog(bool on)
    {
        if (!on)
        {
            _aboveWatchdogTimer?.Stop();
            _aboveWatchdogTimer = null;
            _raiseSoon?.Stop();
            return;
        }
        if (_aboveWatchdogTimer != null) return;
        // Safety net for windows that re-raise themselves without a foreground change (toggling
        // fullscreen inside a game). Only acts when something really is above — no constant churn.
        // 700 ms, not 1.5 s: another topmost window that grabs the top every frame otherwise keeps the
        // overlay buried for a visible moment.
        _aboveWatchdogTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        _aboveWatchdogTimer.Tick += (_, _) =>
        {
            if (!_settings.OverlayAboveApps || _settings.BackdropMode != BackdropMode.Transparent) return;
            if (AnythingAbove()) RaiseAbove();
        };
        _aboveWatchdogTimer.Start();
    }

    /// <summary>True when some other visible window sits above this one in z-order.</summary>
    private bool AnythingAbove()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero || !IsVisible) return false;
        IntPtr h = NativeMethods.GetTopWindow(IntPtr.Zero);
        int guard = 0;
        while (h != IntPtr.Zero && guard++ < 400)
        {
            if (NativeMethods.IsWindowVisible(h))
                return h != hwnd;      // the first visible window is either us (nothing above) or not
            h = NativeMethods.GetWindow(h, NativeMethods.GW_HWNDNEXT);
        }
        return false;
    }

    private NativeMethods.WinEventDelegate? _foregroundProc;
    private IntPtr _foregroundHook;

    private void SetForegroundHook(bool on)
    {
        if (on)
        {
            if (_foregroundHook != IntPtr.Zero) return;
            _foregroundProc = (_, _, hwnd, _, _, _, _) =>
            {
                if (!_settings.OverlayAboveApps || _settings.BackdropMode != BackdropMode.Transparent) return;
                if (hwnd == new WindowInteropHelper(this).Handle) return;
                QueueRaise();
            };
            _foregroundHook = NativeMethods.SetWinEventHook(
                NativeMethods.EVENT_SYSTEM_FOREGROUND, NativeMethods.EVENT_SYSTEM_FOREGROUND,
                IntPtr.Zero, _foregroundProc, 0, 0, NativeMethods.WINEVENT_OUTOFCONTEXT);
            return;
        }
        if (_foregroundHook == IntPtr.Zero) return;
        NativeMethods.UnhookWinEvent(_foregroundHook);
        _foregroundHook = IntPtr.Zero;
        _foregroundProc = null;
    }

    private void ApplyWindowStyles()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        var ex = NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE);
        ex |= NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE;
        if (_settings.Locked) ex |= NativeMethods.WS_EX_TRANSPARENT;
        else ex &= ~NativeMethods.WS_EX_TRANSPARENT;
        NativeMethods.SetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE, ex);
        NativeMethods.SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_FRAMECHANGED);
    }

    private void LogState(string where)
    {
        try
        {
            var line = $"{DateTime.Now:HH:mm:ss} {where} visible={IsVisible} opacity={_root.Opacity:0.00} " +
                       $"rect={Left:0},{Top:0} {Width:0}x{Height:0} doc={(_player.Lyrics?.Lines.Count ?? 0)} " +
                       $"stackdoc={_stack.HasDocument} stack={_stack.Visibility} meta={_meta.Visibility} enabled={_settings.Enabled} " +
                       $"above={_settings.OverlayAboveApps} topmost={Topmost} hook={(_foregroundHook != IntPtr.Zero)} mode={_settings.BackdropMode}";
            System.IO.File.AppendAllText(System.IO.Path.Combine(AppPaths.DataDir, "overlay.log"), line + Environment.NewLine);
        }
        catch { }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetCursorPos(out NativePoint point);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X, Y;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left, Top, Right, Bottom;
    }

    private void RepositionWindow()
    {
        var area = SystemParameters.WorkArea;
        // The window *is* the backdrop: it never gets smaller than the lyric column (the stack
        // reports that minimum), and a manual size can only grow it.
        var minimum = _stack.ContentMinimum;
        double wantedWidth = _settings.BackdropWidth > 0 ? _settings.BackdropWidth : _settings.LineWidth + 36;
        double wantedHeight = _settings.BackdropHeight > 0 ? _settings.BackdropHeight : minimum.Height;
        Width = Math.Min(area.Width - 20, Math.Max(minimum.Width, wantedWidth));
        Height = Math.Min(area.Height - 40, Math.Max(minimum.Height, wantedHeight));
        bool hasPosition = _settings.CustomPosition;
        Left = hasPosition
            ? Math.Clamp(_settings.WindowX, area.Left - Width + 120, area.Right - 120)
            : area.Left + (area.Width - Width) / 2;
        Top = hasPosition
            ? Math.Clamp(_settings.WindowY, area.Top - Height + 80, area.Bottom - 80)
            : area.Top + area.Height * _settings.Anchor - Height / 2;
        if (Top < area.Top) Top = area.Top;
        if (Top > area.Bottom - Height) Top = area.Bottom - Height;
    }

    private void AnimateEntrance()
    {
        // No fade-in: an empty overlay never ticks the render clock, which used to freeze the
        // animation at its starting opacity and leave a half-visible window behind.
        _root.BeginAnimation(OpacityProperty, null);
        _root.Opacity = _settings.DisplayOpacity;
    }

    // ---------- public API ----------
    /// <summary>
    /// Vertical alpha ramp for the lyric column: the top and bottom ~11% of the window dissolve, so a
    /// cue that runs into the edge fades out instead of being sliced mid-glyph. Rebuilt on resize.
    /// </summary>
    private void UpdateLyricsMask()
    {
        double height = _lyricsHost.ActualHeight;
        if (height < 8)
        {
            _lyricsHost.OpacityMask = null;
            return;
        }
        double band = Math.Clamp(height * 0.11, 10, 90);
        double edge = band / height;
        var brush = new LinearGradientBrush
        {
            MappingMode = BrushMappingMode.Absolute,
            StartPoint = new Point(0, 0),
            EndPoint = new Point(0, height),
            GradientStops =
            {
                new GradientStop(Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 0),
                new GradientStop(Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF), edge),
                new GradientStop(Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF), 1 - edge),
                new GradientStop(Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 1),
            },
        };
        brush.Freeze();
        _lyricsHost.OpacityMask = brush;
    }

    public void ApplySettings(AppSettings settings)
    {
        _settings = settings;
        ApplyOverlayTopmost();
        _stack.ApplySettings(settings, 1.0);
        ApplyPerspective(settings.Perspective, settings.TiltAngle);

        _root.Opacity = settings.DisplayOpacity;
        _metaTitle.FontSize = Math.Max(18, settings.DisplayFontSize * 0.52);
        _metaArtist.FontSize = Math.Max(12, settings.DisplayFontSize * 0.30);
        _metaTitle.FontFamily = FontResolver.Resolve(settings.FontFamily);
        _metaArtist.FontFamily = FontResolver.Resolve(settings.FontFamily);
        _metaTitle.MaxWidth = settings.LineWidth;
        _metaArtist.MaxWidth = settings.LineWidth;

        RepositionWindow();
        // Every settings change (a colour drag fires dozens) used to rebuild the frosted backdrop —
        // a 20–45 ms blur pass for something that did not change. Compare a signature instead.
        if (BackdropSignature(settings) != _backdropSignature) UpdateBackdropVisual(settings);
        ApplyWindowStyles();
    }

    private string _backdropSignature = "";

    /// <summary>Everything <see cref="UpdateBackdropVisual"/> actually reads.</summary>
    private string BackdropSignature(AppSettings settings)
    {
        var cover = settings.BackdropMode == BackdropMode.Cover ? _player.Current?.Cover : null;
        int coverId = cover == null ? 0 : System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(cover);
        return $"{settings.BackdropMode}|{settings.BackdropImage}|{coverId}|{settings.BackdropColor}|" +
               $"{settings.BackdropBlur:0.##}|{settings.BackdropOpacity:0.####}|" +
               $"{settings.BackdropFadeEnabled}|{settings.BackdropFadeStrength:0.##}|" +
               $"{settings.BackdropFadeH:0.###}|{settings.BackdropFadeV:0.###}|" +
               $"{ActualWidth:0.#}x{ActualHeight:0.#}";
    }

    /// <summary>
    /// Rebuilds the backdrop bitmap (frost + baked edge fade + baked opacity) and hands it to its own
    /// element. The bitmap itself is <b>premultiplied</b>: the alpha ramp is multiplied into the
    /// colour, which is what makes the edge dissolve into genuinely transparent pixels instead of
    /// adding colour that the alpha claims is not there (that was the visible "boundary line").
    /// </summary>
    private void UpdateBackdropVisual(AppSettings settings)
    {
        if (settings.BackdropMode == BackdropMode.Transparent)
        {
            _backdropBitmap = null;
            _backdropImage.Source = null;
            _backdropImage.Width = 0;
            _backdropImage.Height = 0;
            _backdropSignature = BackdropSignature(settings);
            return;
        }

        double width = Math.Max(2, ActualWidth > 1 ? ActualWidth : Width);
        double height = Math.Max(2, ActualHeight > 1 ? ActualHeight : Height);

        // Build the backdrop at device resolution so the pixel ramp is exact at the density it is
        // actually displayed at (a 96-dpi bitmap gets resampled by the compositor).
        double pixelScale = 1.0;
        try { pixelScale = VisualTreeHelper.GetDpi(this).DpiScaleX; } catch { }
        if (pixelScale < 0.5 || pixelScale > 4) pixelScale = 1.0;
        int pixelWidth = Math.Max(2, (int)Math.Round(width * pixelScale));
        int pixelHeight = Math.Max(2, (int)Math.Round(height * pixelScale));

        BitmapSource? image = settings.BackdropMode switch
        {
            BackdropMode.Image => LoadImage(settings.BackdropImage),
            BackdropMode.Cover => _player.Current?.Cover ?? LoadImage(settings.BackdropImage),
            _ => null,
        };

        // Backdrop opacity is baked into the bitmap's alpha (see FrostToSize); the frame composition
        // then never needs an opacity group, which was the most expensive part of a compose.
        _backdropOpacity = Math.Clamp(settings.BackdropOpacity, 0.05, 1);

        if (image == null)
        {
            var colour = DesktopLyricsWindow.ParseColor(settings.BackdropColor);
            var tinted = Color.FromArgb((byte)Math.Round(255 * _backdropOpacity), colour.R, colour.G, colour.B);
            var solid = new System.Windows.Media.Imaging.RenderTargetBitmap(
                pixelWidth, pixelHeight, 96 * pixelScale, 96 * pixelScale, PixelFormats.Pbgra32);
            var dv = new System.Windows.Media.DrawingVisual();
            using (var dc = dv.RenderOpen())
                dc.DrawRectangle(new System.Windows.Media.SolidColorBrush(tinted), null,
                    new Rect(0, 0, width, height));
            solid.Render(dv);
            solid.Freeze();
            _backdropBitmap = solid;
        }
        else
        {
            _backdropBitmap = Controls.BackdropBlur.FrostToSize(image, (int)Math.Round(settings.BackdropBlur),
                pixelWidth, pixelHeight,
                settings.BackdropFadeEnabled ? settings.BackdropFadeStrength : 0,
                settings.BackdropFadeH, settings.BackdropFadeV, 0.42, _backdropOpacity);
        }

        _backdropImage.Width = width;
        _backdropImage.Height = height;
        _backdropImage.Source = _backdropBitmap;
        _backdropSignature = BackdropSignature(settings);

        DumpBackdrop();
        if (!_loggedBackdrop)
        {
            _loggedBackdrop = true;
            LogState($"backdrop-ok {pixelWidth}x{pixelHeight} doc={_stack.HasDocument}");
        }
    }

    private bool _loggedBackdrop;

    /// <summary>
    /// QA: LYRIC_DUMP_BG=1 writes the finished backdrop bitmap plus a premultiplied-alpha audit.
    /// (Premultiplied violations are what used to turn the feathered edge into a hard line.)
    /// </summary>
    private void DumpBackdrop()
    {
        if (Environment.GetEnvironmentVariable("LYRIC_DUMP_BG") != "1" || _backdropBitmap == null) return;
        try
        {
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(_backdropBitmap));
            using (var stream = System.IO.File.Create(System.IO.Path.Combine(
                       AppPaths.DataDir, "backdrop-dump.png")))
                encoder.Save(stream);

            int w = _backdropBitmap.PixelWidth, h = _backdropBitmap.PixelHeight, stride = w * 4;
            var px = new byte[stride * h];
            _backdropBitmap.CopyPixels(px, stride, 0);
            long violations = 0;
            for (int i = 0; i < px.Length; i += 4)
            {
                int a = px[i + 3];
                if (px[i] > a + 1 || px[i + 1] > a + 1 || px[i + 2] > a + 1) violations++;
            }
            var lines = new List<string>
            {
                $"backdrop {w}x{h} premultViolations={violations} of {w * h}",
                "y = 1/8 height, x: B G R A (as stored, premultiplied)",
            };
            int row = Math.Max(0, h / 8) * stride;
            for (int x = 0; x < Math.Min(w, 260); x += 5)
            {
                int i = row + x * 4;
                lines.Add($"x={x} b={px[i]} g={px[i + 1]} r={px[i + 2]} a={px[i + 3]}");
            }
            System.IO.File.WriteAllLines(System.IO.Path.Combine(
                AppPaths.DataDir, "backdrop-dump.txt"), lines);
            LogState($"backdrop-premult-violations {violations}");
        }
        catch { }
    }

    /// <summary>Feeds the stack the artwork it needs for cover mode / hit geometry.</summary>
    private void UpdateBackdrop(AppSettings settings)
    {
        var custom = string.IsNullOrWhiteSpace(settings.BackdropImage) ? null : LoadImage(settings.BackdropImage);
        if (!ReferenceEquals(custom, _backdropCustom))
        {
            _backdropCustom = custom;
            _stack.BackdropImageOverride = custom;
        }
        _stack.SetCoverImage(_player.Current?.Cover);
    }

    private static System.Windows.Media.Imaging.BitmapSource? LoadImage(string path)
    {
        try
        {
            if (!System.IO.File.Exists(path)) return null;
            var image = new System.Windows.Media.Imaging.BitmapImage();
            image.BeginInit();
            image.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            image.UriSource = new Uri(path);
            image.DecodePixelWidth = 640;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch { return null; }
    }

    /// <summary>Called when the lyric document arrives after the track started.</summary>
    public void RefreshLyrics()
    {
        // The word-level upgrade lands on a background thread, so hop back to the UI thread first,
        // the way NowPlayingView does — SetDocument touches live visual state.
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(RefreshLyrics));
            return;
        }
        _stack.SetDocument(_player.Lyrics, (long)Math.Max(0, _player.PositionMs));
        UpdateMetaVisibility();
    }

    /// <summary>
    /// “3D 倾斜歌词”: WPF's real 3D pipeline does not composite inside a layered window, so the
    /// -32° Y-rotation is approximated with the matching affine (cos 32° horizontal squeeze plus a
    /// shallow shear), hinged on the left edge like the original transformOrigin.
    /// </summary>
    /// <summary>
    /// The tilt is produced entirely by the projective warp inside LyricStackView; the element
    /// itself must stay untransformed. (An old affine "lean" used to be applied here as well, which
    /// added a constant ~10° rotation on top of the projection — that is what kept tilting the
    /// reading line no matter how the perspective was tuned.)
    /// </summary>
    private void ApplyPerspective(bool enabled, double angle = -32)
    {
        _stack.RenderTransform = Transform.Identity;
    }

    /// <summary>Visual QA hook: pin the column to a fixed lyric position.</summary>
    public void SetDemoPosition(long ms)
    {
        _demo = true;
        _stack.SetDocument(_player.Lyrics, ms);
        _stack.Snap();
        UpdateMetaVisibility();
    }

    /// <summary>Visual QA: advance the lyric clock without touching the media player.</summary>
    public void DemoAdvance(long ms)
    {
        _demo = true;
        _stack.Tick(ms);
    }

    private void OnTick(double positionMs)
    {
        // The lyric clock is driven by the compositor (see OnCompositionRendering) so animation
        // frames line up with what is actually painted; this tick only keeps the placeholder in sync.
        if (!IsVisible || _demo) return;
        UpdateMetaVisibility();
    }

    private TimeSpan _lastRenderingTime = TimeSpan.MinValue;

    /// <summary>
    /// Advances the lyric clock once per compositor frame, on the same frame cadence the window
    /// presents at. A 16 ms DispatcherTimer was measured at only ~38 ticks/s with 1..45 ms jitter
    /// (it competes with the render pass), and a 60 fps throttle on top of that left the desktop
    /// lyrics at 57.6 paints/s on a 179 Hz display — every transition showed the same position for
    /// three refreshes in a row. Advance() (not Tick()) is used so a frame where neither the
    /// position nor an animation moved is skipped instead of repainted.
    /// </summary>
    private void OnCompositionRendering(object? sender, EventArgs e)
    {
        if (e is RenderingEventArgs args)
        {
            if (args.RenderingTime == _lastRenderingTime) return;   // same compositor frame
            _lastRenderingTime = args.RenderingTime;
        }
        FrameCount++;
        if (!IsVisible || _demo) return;
        // Nothing to animate while paused — but let an in-flight transition finish.
        if (!_player.PlaybackState && !_stack.HasActiveAnimation) return;
        _stack.Advance((long)Math.Max(0, _player.PositionMs));
        UpdateMetaVisibility();
    }

    /// <summary>The artwork for a cloud track lands after the track change; the overlay's cover
    /// backdrop would otherwise stay on the previous picture (or on nothing).</summary>
    private void OnCoverArrived(Models.Song song)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!ReferenceEquals(_player.Current, song)) return;
            if (_settings.BackdropMode is not BackdropMode.Cover) return;
            _stack.SetCoverImage(song.Cover);
            UpdateBackdropVisual(_settings);
            PaintSoon();
        }));
    }

    private void OnTrackChanged()
    {
        _stack.SetDocument(_player.Lyrics, (long)Math.Max(0, _player.PositionMs));
        if (_settings.BackdropMode is BackdropMode.Cover)
        {
            _stack.SetCoverImage(_player.Current?.Cover);
            UpdateBackdropVisual(_settings);
        }
        var song = _player.Current;
        _metaTitle.Text = song?.DisplayTitle ?? "Darius Music";
        _metaArtist.Text = song == null ? "等待播放" : song.DisplayArtist;
        UpdateMetaVisibility();
    }

    private void UpdateMetaVisibility()
    {
        if (!_loggedVisible && IsVisible) { _loggedVisible = true; LogState("shown"); }
        bool hasLyrics = _player.Lyrics is { Lines.Count: > 0 };
        _meta.Visibility = hasLyrics ? Visibility.Collapsed : Visibility.Visible;
        var wanted = hasLyrics ? Visibility.Visible : Visibility.Collapsed;
        if (_stack.Visibility != wanted)
        {
            _stack.Visibility = wanted;
            // A collapsed column measures 0x0, and WPF does not repaint an element that was never
            // laid out at a real size: that is how the overlay ended up showing its backdrop with no
            // lyrics on top. One invalidate after the flip is all it needs.
            if (wanted == Visibility.Visible) PaintSoon();
        }
    }

    // ---------- interaction ----------
    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_settings.Locked) return;
        var now = DateTime.UtcNow;
        if ((now - _lastClick).TotalMilliseconds < 350)
        {
            _lastClick = DateTime.MinValue;
            SettingsRequested?.Invoke();
            e.Handled = true;
            return;
        }
        _lastClick = now;
        _dragging = true;
        _dragStart = e.GetPosition(this);
        _dragLeft = Left;
        _dragTop = Top;
        CaptureMouse();
        e.Handled = true;
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging || e.LeftButton != MouseButtonState.Pressed) return;
        var p = e.GetPosition(this);
        Left = _dragLeft + (p.X - _dragStart.X);
        Top = _dragTop + (p.Y - _dragStart.Y);
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        ReleaseMouseCapture();
        var area = SystemParameters.WorkArea;
        _settings.Anchor = Math.Clamp((Top + Height / 2 - area.Top) / area.Height, 0, 1);
        SettingsStore.Save(_settings);
        e.Handled = true;
    }

    private void OnWheel(object sender, MouseWheelEventArgs e)
    {
        var step = e.Delta > 0 ? 3 : -3;
        _settings.FontSize = Math.Clamp(_settings.FontSize + step, 20, 140);
        SettingsStore.Save(_settings);
        ApplySettings(_settings);
        e.Handled = true;
    }

    /// <summary>Right-click menu. <paramref name="screenX"/>/<paramref name="screenY"/> are physical
    /// screen pixels (from WM_NCRBUTTONUP's lParam); when omitted the current cursor position is used.
    /// The menu is placed with an absolute point, never with WPF's mouse placement, because this
    /// window never takes activation and its mouse state is unreliable.</summary>
    private void ShowMenu(int screenX = int.MinValue, int screenY = int.MinValue)
    {
        var menu = new ContextMenu();

        var settings = new MenuItem { Header = "桌面歌词设置…" };
        settings.Click += (_, _) => SettingsRequested?.Invoke();
        menu.Items.Add(settings);
        menu.Items.Add(new Separator());

        var locked = new MenuItem { Header = "鼠标穿透（锁定）", IsCheckable = true, IsChecked = _settings.Locked };
        locked.Click += (_, _) =>
        {
            _settings.Locked = !_settings.Locked;
            SettingsStore.Save(_settings);
            ApplyWindowStyles();
        };
        menu.Items.Add(locked);

        var reset = new MenuItem { Header = "重置歌词位置" };
        reset.Click += (_, _) =>
        {
            _settings.CustomPosition = false;
            SettingsStore.Save(_settings);
            RepositionWindow();
        };
        menu.Items.Add(reset);

        var translation = new MenuItem { Header = "显示翻译", IsCheckable = true, IsChecked = _settings.Translation };
        translation.Click += (_, _) =>
        {
            _settings.Translation = !_settings.Translation;
            SettingsStore.Save(_settings);
            ApplySettings(_settings);
        };
        menu.Items.Add(translation);

        var backdrop = new MenuItem { Header = "背景柔光", IsCheckable = true, IsChecked = _settings.Backdrop };
        backdrop.Click += (_, _) =>
        {
            _settings.Backdrop = !_settings.Backdrop;
            SettingsStore.Save(_settings);
            ApplySettings(_settings);
        };
        menu.Items.Add(backdrop);

        // 「窗口置顶」和「显示在其他应用上层」原本是两个开关，但它们的区别用户看不出来：只要
        // 置顶开着，普通窗口下两种状态画面完全一样。现在只有一个总开关（下面那个），所以这里
        // 只保留「显示在其他应用上层」，关掉它是真的落到普通窗口层级（立刻生效，不需要切窗口）。
        var aboveApps = new MenuItem
        {
            Header = "显示在其他应用上层",
            IsCheckable = true,
            IsChecked = _settings.OverlayAboveApps,
            IsEnabled = _settings.BackdropMode == BackdropMode.Transparent,
        };
        aboveApps.Click += (_, _) =>
        {
            _settings.OverlayAboveApps = !_settings.OverlayAboveApps;
            SettingsStore.Save(_settings);
            ApplySettings(_settings);
        };
        menu.Items.Add(aboveApps);

        menu.Items.Add(new Separator());
        var previous = new MenuItem { Header = "上一首" };
        previous.Click += (_, _) => _player.Previous();
        var toggle = new MenuItem { Header = _player.PlaybackState ? "暂停" : "播放" };
        toggle.Click += (_, _) => _player.Toggle();
        var next = new MenuItem { Header = "下一首" };
        next.Click += (_, _) => _player.Next();
        menu.Items.Add(previous);
        menu.Items.Add(toggle);
        menu.Items.Add(next);

        menu.Items.Add(new Separator());
        var hide = new MenuItem { Header = "关闭桌面歌词" };
        hide.Click += (_, _) =>
        {
            _settings.Enabled = false;
            SettingsStore.Save(_settings);
            Hide();
        };
        menu.Items.Add(hide);

        // Absolute placement in DIPs. Converted from the physical click point with this window's
        // own DPI scale so 100%/125%/150% all land under the cursor.
        var dpi = VisualTreeHelper.GetDpi(this);
        Point target;
        if (screenX != int.MinValue && screenY != int.MinValue)
            target = new Point(screenX / dpi.DpiScaleX, screenY / dpi.DpiScaleY);
        else
            target = CursorPointInDips(dpi);

        menu.PlacementTarget = _root;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.AbsolutePoint;
        menu.HorizontalOffset = target.X;
        menu.VerticalOffset = target.Y;
        menu.Opened += (_, _) => ClampMenuToWorkArea(menu);
        _menu = menu;
        menu.IsOpen = true;
    }

    private Point CursorPointInDips(DpiScale dpi)
    {
        if (!GetCursorPos(out var point)) return new Point(0, 0);
        return new Point(point.X / dpi.DpiScaleX, point.Y / dpi.DpiScaleY);
    }

    /// <summary>Nudges a just-opened menu back inside the work area (it can only be measured once
    /// it is open, so this runs from Opened).</summary>
    private static void ClampMenuToWorkArea(ContextMenu menu)
    {
        var area = SystemParameters.WorkArea;          // DIPs
        double width = menu.ActualWidth, height = menu.ActualHeight;
        if (width <= 0 || height <= 0) return;
        double x = menu.HorizontalOffset, y = menu.VerticalOffset;
        double nx = Math.Min(x, Math.Max(area.Left, area.Right - width));
        double ny = Math.Min(y, Math.Max(area.Top, area.Bottom - height));
        if (Math.Abs(nx - x) > 0.5) menu.HorizontalOffset = nx;
        if (Math.Abs(ny - y) > 0.5) menu.VerticalOffset = ny;
    }

    /// <summary>QA: open the menu at a physical screen point and report where it actually landed.</summary>
    public string DebugOpenMenuAt(int screenX, int screenY)
    {
        ShowMenu(screenX, screenY);
        return $"asked={screenX},{screenY} {DebugMenuOrigin()}";
    }

    /// <summary>QA: where the last opened menu sits, in physical screen pixels.</summary>
    public string DebugMenuOrigin()
    {
        if (_menu == null) return "menu=none";
        var dpi = VisualTreeHelper.GetDpi(this);
        var origin = _menu.PointToScreen(new Point(0, 0));
        return $"menu={origin.X:0},{origin.Y:0} size={_menu.ActualWidth * dpi.DpiScaleX:0}x{_menu.ActualHeight * dpi.DpiScaleY:0} " +
               $"dpi={dpi.DpiScaleX}";
    }

    /// <summary>QA: post a real WM_NCRBUTTONUP with packed screen coordinates, i.e. exactly the
    /// message a right-click delivers, so the decode path is exercised too.</summary>
    public void DebugPostNcRightUp(int screenX, int screenY)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        long packed = ((long)(short)screenX & 0xFFFF) | (((long)(short)screenY & 0xFFFF) << 16);
        PostMessage(hwnd, 0x00A5, IntPtr.Zero, (IntPtr)packed);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

    /// <summary>QA: close whatever menu is open (the popup stays until dismissed otherwise).</summary>
    public void DebugCloseMenu()
    {
        if (_menu != null) _menu.IsOpen = false;
        _menu = null;
    }

    // ---------- preview ----------
    /// <summary>QA: makes the overlay visible even while the app owns focus, then repaints.</summary>
    public void ShowOverlayForTest()
    {
        _demo = true;
        Show();
        Visibility = Visibility.Visible;
        _root.Opacity = 1;
        _stack.InvalidateVisual();
        UpdateLayout();
    }

    /// <summary>QA: milliseconds to produce one full-resolution glyph frame (the per-tick cost).</summary>
    public string BenchGlyphs(int iterations)
    {
        double width = Math.Max(2, ActualWidth > 1 ? ActualWidth : Width);
        double height = Math.Max(2, ActualHeight > 1 ? ActualHeight : Height);
        double scale = 1.0;
        try { scale = VisualTreeHelper.GetDpi(this).DpiScaleX; } catch { }
        int w = Math.Max(2, (int)Math.Round(width * scale));
        int h = Math.Max(2, (int)Math.Round(height * scale));

        var clock = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < iterations; i++)
        {
            var frame = new RenderTargetBitmap(w, h, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
            var lyrics = new DrawingVisual();
            using (var dc = lyrics.RenderOpen())
                _stack.DrawInto(dc, width, height);
            frame.Render(lyrics);
            frame.Freeze();
        }
        clock.Stop();
        double glyphs = clock.Elapsed.TotalMilliseconds / Math.Max(1, iterations);
        return $"glyph_frame_ms={glyphs:0.00} frame={w}x{h} max_fps={(glyphs > 0 ? 1000.0 / glyphs : 0):0.0}";
    }

    public double DebugStackFadeH => _stack.DebugFadeH;
    public long DebugRenderCount => _stack.RenderCount;
    /// <summary>Distinct compositor frames seen since the window was created.</summary>
    public long FrameCount { get; private set; }
    public void ResetFrameCount() => FrameCount = 0;
    public double DebugRenderMsTotal => _stack.RenderMsTotal;
    public double DebugLastRenderMs => _stack.LastRenderMs;
    public void ResetRenderStats() => _stack.ResetRenderStats();
    public string DebugMotion =>
        $"scroll={_stack.DebugScrollDelta,8:0.00} morph={_stack.DebugMorph:0.000} morphing={(_stack.DebugMorphing ? 1 : 0)}";
    public string DebugStackState =>
        $"mode={_stack.DebugMode} fade={_stack.DebugFadeEnabled} strength={_stack.DebugFadeStrength} " +
        $"h={_stack.DebugFadeH} v={_stack.DebugFadeV} image={_stack.DebugHasImage}";

    public string DebugInkState => _stack.DebugInkState;

    /// <summary>Diagnostics: whether the glow switch reached the column and how many halos it built.</summary>
    public string DebugGlowState => _stack.DebugGlow;

    /// <summary>
    /// QA: paints only the glyph column and counts what colour the light actually is. A red glow
    /// shows up as red-dominant pixels, so "did the colour reach the screen, and is the glow still
    /// there at all" becomes two numbers instead of an opinion.
    /// </summary>
    public (long Lit, long Red, long Green, long Blue, long Neutral) DebugGlowCounts()
    {
        double width = Math.Max(2, ActualWidth > 1 ? ActualWidth : Width);
        double height = Math.Max(2, ActualHeight > 1 ? ActualHeight : Height);
        double scale = 1.0;
        try { scale = VisualTreeHelper.GetDpi(this).DpiScaleX; } catch { }
        int w = Math.Max(2, (int)Math.Round(width * scale));
        int h = Math.Max(2, (int)Math.Round(height * scale));
        var frame = new RenderTargetBitmap(w, h, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen()) _stack.DrawInto(dc, width, height);
        frame.Render(visual);
        frame.Freeze();

        int stride = w * 4;
        var pixels = new byte[stride * h];
        frame.CopyPixels(pixels, stride, 0);
        long lit = 0, red = 0, green = 0, blue = 0, neutral = 0;
        for (int i = 0; i < pixels.Length; i += 4)
        {
            int b = pixels[i], g = pixels[i + 1], r = pixels[i + 2], a = pixels[i + 3];
            if (a < 24) continue;
            lit++;
            if (r - Math.Max(g, b) > 20) red++;
            else if (g - Math.Max(r, b) > 20) green++;
            else if (b - Math.Max(r, g) > 20) blue++;
            else neutral++;
        }
        return (lit, red, green, blue, neutral);
    }

    public string DebugGlowPixels()
    {
        var c = DebugGlowCounts();
        return $"lit={c.Lit} red={c.Red} green={c.Green} blue={c.Blue} neutral={c.Neutral}";
    }

    /// <summary>QA: forces the pre-1.2.29 behaviour (colour change throws the geometry away), so the
    /// old and the new latency can be measured on one binary.</summary>
    public void DebugSetGlowColorRebuild(bool on) => _stack.DebugGlowColorRebuild = on;

    /// <summary>Diagnostics: the column's layout size and how often it actually painted.</summary>
    public string DebugPaintState =>
        $"stack={_stack.ActualWidth:0}x{_stack.ActualHeight:0} paints={_stack.RenderCount} frames={FrameCount}";

    /// <summary>Diagnostics: whether the column was handed a document at all.</summary>
    public string DebugDocumentState => $"stackdoc={_stack.HasDocument} doc={_player.Lyrics?.Lines.Count ?? 0}";

    /// <summary>Captures the overlay exactly as the desktop shows it, DPI-corrected.</summary>
    public void CaptureScreen(string path)
    {
        double scale = 1.0;
        try { scale = VisualTreeHelper.GetDpi(this).DpiScaleX; } catch { }
        int x = (int)Math.Round(Left * scale);
        int y = (int)Math.Round(Top * scale);
        int w = Math.Max(1, (int)Math.Round(ActualWidth * scale));
        int h = Math.Max(1, (int)Math.Round(ActualHeight * scale));
        var dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) System.IO.Directory.CreateDirectory(dir);
        using var bmp = new System.Drawing.Bitmap(w, h);
        using (var g = System.Drawing.Graphics.FromImage(bmp))
            g.CopyFromScreen(x, y, 0, 0, new System.Drawing.Size(w, h));
        bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
    }

    public void RenderPreview(string path)
    {
        _root.BeginAnimation(OpacityProperty, null);
        _root.Opacity = 1;
        UpdateLayout();

        int winW = (int)Math.Ceiling(ActualWidth > 1 ? ActualWidth : Width);
        int winH = (int)Math.Ceiling(ActualHeight > 1 ? ActualHeight : Height);
        var winRtb = new RenderTargetBitmap(Math.Max(1, winW), Math.Max(1, winH), 96, 96, PixelFormats.Pbgra32);
        winRtb.Render(this);

        int pad = 48;
        int cw = Math.Min(winW, (int)_settings.LineWidth + pad * 2);
        int ch = winH;
        int cx = Math.Max(0, (winW - cw) / 2);

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var bg = new LinearGradientBrush(Color.FromRgb(0x23, 0x2A, 0x38), Color.FromRgb(0x0E, 0x11, 0x18), 90);
            dc.DrawRectangle(bg, null, new Rect(0, 0, cw, ch));
            dc.PushClip(new RectangleGeometry(new Rect(0, 0, cw, ch)));
            dc.DrawImage(winRtb, new Rect(-cx, 0, winW, winH));
            dc.Pop();
        }
        var rtb = new RenderTargetBitmap(Math.Max(1, cw), Math.Max(1, ch), 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);

        var dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) System.IO.Directory.CreateDirectory(dir);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = System.IO.File.Create(path);
        encoder.Save(fs);
    }

    internal static Color ParseColor(string hex, double alpha = 255)
    {
        hex = (hex ?? "").TrimStart('#');
        if (hex.Length != 6) hex = "FFFFFF";
        byte a = (byte)Math.Clamp(alpha, 0, 255);
        return Color.FromArgb(a,
            Convert.ToByte(hex[..2], 16),
            Convert.ToByte(hex[2..4], 16),
            Convert.ToByte(hex[4..], 16));
    }
}
