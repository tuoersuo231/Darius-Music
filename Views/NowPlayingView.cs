using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using CurrentLyrics.Controls;
using CurrentLyrics.Services;

namespace CurrentLyrics.Views;

/// <summary>
/// 正在播放页（主窗口内的一个页面，不是独立窗口）：整页封面背景 + 歌词列。页面窄时是「手机版」
/// 竖排（默认歌词优先，点标题可切大封面），拉宽到 <see cref="WideThreshold"/> 以上自动变成横版
/// （左信息右歌词）。歌词参数来自 settings 的 Player* 一组字段，和桌面歌词的配置完全独立。
/// </summary>
public sealed class NowPlayingView : Grid
{
    /// <summary>Wide arrangement needs a real window, not just a landscape aspect: below this width
    /// the two panes would squeeze the lyrics column into a stripe.</summary>
    private const double WideThreshold = 760;

    /// <summary>Column width the 3D fan is calibrated to: below it the tilt is damped so the same
    /// 倾斜角度 looks the same in a narrow window (see LyricStackView.FanReferenceWidth).</summary>
    private const double TiltReferenceWidth = 900;

    private readonly PlayerController _player;
    private readonly AppSettings _settings;

    /// <summary>Top of the 显示行数 slider: how many cues the current window can actually hold.</summary>
    private int _maxLineCount = 7;
    private double _appliedLineWidth;
    private int _appliedVisibleLines;

    private readonly Grid _root = new();
    private readonly Button _backButton;
    private readonly Button _moreButton;
    private readonly Border _moreHost;
    private readonly Rectangle _moreScrim;

    /// <summary>QA: labels and slider instances of the ⋯ sheet; rebuilt every time it opens.</summary>
    private readonly List<string> _rowLabels = new();
    private readonly Dictionary<string, DragOnlySlider> _rowSliders = new();

    /// <summary>QA: park the lyric clock at a fixed position so a frame can be inspected without a media source.</summary>
    private long? _demoPin;
    private BitmapSource? _cover;
    private BitmapSource? _blurred;
    private BitmapSource? _blurredFor;
    private double _blurredRadius = -1;
    private DispatcherTimer? _resizeDebounce;
    private DispatcherTimer? _resizeRelease;
    private bool _resizeHold;
    private readonly Image _backdrop = new();
    private readonly Rectangle _scrim = new();
    private readonly Grid _content = new();

    private readonly Border _coverFrame;
    private readonly TextBlock _coverGlyph;
    private readonly ImageBrush _coverBrush = new() { Stretch = Stretch.UniformToFill, AlignmentX = AlignmentX.Center, AlignmentY = AlignmentY.Center };
    private readonly Brush _coverFallback = new SolidColorBrush(Color.FromRgb(0x16, 0x1C, 0x28));
    private readonly TextBlock _title;
    private readonly TextBlock _artist;
    private readonly StackPanel _info;

    private readonly Slider _progress = new();
    private readonly TextBlock _posText;
    private readonly TextBlock _durText;
    private readonly TextBlock _qualityText;
    private readonly TextBlock _caption;
    private readonly Grid _progressPanel;

    private readonly StackPanel _controls;
    private readonly Button _playButton;
    private readonly Grid _playGlyph;
    private readonly Path _playTriangle;
    private readonly Path _pauseBars;
    private readonly Button _modeButton;
    private readonly TextBlock _modeGlyph;
    private readonly TextBlock _modeText;
    private readonly Button _lyricsButton;

    private readonly LyricStackView _stack = new();
    private readonly Border _lyricsHost;
    private readonly TextBlock _noLyrics;

    private bool _wide;
    private bool _initialised;
    private bool _dragging;
    private bool _compactHeader = true;
    private StackPanel? _headerStack;
    private Panel? _headerParent;
    private int _lastCueIndex = -2;

    public event Action? BackRequested;
    public event Action? DesktopLyricsToggled;

    public NowPlayingView(PlayerController player, AppSettings settings)
    {
        _player = player;
        _settings = settings;

        Background = new SolidColorBrush(Color.FromRgb(0x07, 0x08, 0x0B));
        ClipToBounds = true;
        System.Windows.Documents.TextElement.SetForeground(this, (Brush)Application.Current.Resources["TextBrush"]);
        System.Windows.Documents.TextElement.SetFontFamily(this, (FontFamily)Application.Current.Resources["UiFont"]);

        // Background: the current cover, pre-blurred into a small bitmap (no live effect — see
        // RebuildBackdrop) and pushed down; a vertical scrim keeps the text legible.
        // The source is a deliberately low-resolution blur, so Fant ("HighQuality") was paying for
        // detail that is not there; bilinear sampling of a blurred image is indistinguishable and
        // was 15.6 ms of the page's software draw cost.
        _backdrop.Stretch = Stretch.UniformToFill;
        RenderOptions.SetBitmapScalingMode(_backdrop, BitmapScalingMode.Linear);
        _backdrop.Opacity = 0.9;

        _scrim.Fill = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(0, 1),
            GradientStops =
            {
                new GradientStop(Color.FromArgb(0x55, 7, 8, 11), 0),
                new GradientStop(Color.FromArgb(0xB0, 7, 8, 11), 0.45),
                new GradientStop(Color.FromArgb(0xF2, 7, 8, 11), 1),
            },
        };

        _coverGlyph = new TextBlock
        {
            Text = "♪",
            FontSize = 34,
            Foreground = new SolidColorBrush(Color.FromRgb(0x3C, 0x46, 0x58)),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _coverFrame = new Border
        {
            CornerRadius = new CornerRadius(0),
            Background = _coverFallback,
            ClipToBounds = true,
            Child = _coverGlyph,
            HorizontalAlignment = HorizontalAlignment.Center,
            Effect = new DropShadowEffect { Color = Colors.Black, BlurRadius = 36, ShadowDepth = 10, Opacity = 0.55 },
        };

        _title = new TextBlock
        {
            FontSize = 26,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brushes.White,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 520,
        };
        _artist = new TextBlock
        {
            FontSize = 14,
            Foreground = new SolidColorBrush(Color.FromArgb(0xAA, 0xFF, 0xFF, 0xFF)),
            Margin = new Thickness(0, 8, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        _info = new StackPanel { Orientation = Orientation.Vertical, Children = { _title, _artist } };

        _posText = new TextBlock
        {
            Text = "0:00", FontSize = 11, Foreground = new SolidColorBrush(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF)),
            VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left,
        };
        _durText = new TextBlock
        {
            Text = "0:00", FontSize = 11, Foreground = new SolidColorBrush(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF)),
            VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right,
        };
        _qualityText = new TextBlock
        {
            Text = "", FontSize = 11, Foreground = new SolidColorBrush(Color.FromArgb(0x7A, 0xFF, 0xFF, 0xFF)),
            VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center,
        };
        _caption = new TextBlock
        {
            Text = "", FontSize = 13, Foreground = new SolidColorBrush(Color.FromArgb(0x86, 0xFF, 0xFF, 0xFF)),
            TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 0, 10),
            Visibility = Visibility.Collapsed,
        };
        _progress.Style = (Style)Application.Current.Resources["ThinSlider"];
        _progress.IsMoveToPointEnabled = true;
        _progress.ValueChanged += OnProgressChanged;
        // Slider 的类处理器会先把 PreviewMouseLeftButtonDown 标成已处理，用 += 挂的处理器收不到
        // 按下事件（_dragging 永远是 false），松手时 EndSeek 提前 return，点进度条就没有反应。
        // 必须 handledEventsToo: true。
        _progress.AddHandler(UIElement.PreviewMouseLeftButtonDownEvent,
            new MouseButtonEventHandler((_, _) => _dragging = true), true);
        _progress.AddHandler(UIElement.PreviewMouseLeftButtonUpEvent,
            new MouseButtonEventHandler((_, _) => EndSeek()), true);
        _progress.AddHandler(UIElement.LostMouseCaptureEvent,
            new MouseEventHandler((_, _) => EndSeek()), true);

        // 进度条占一行，时间（已播 / 音质 / 剩余）另占一行，和参考播放器一致。
        _progressPanel = new Grid { Margin = new Thickness(0, 18, 0, 0) };
        _progressPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _progressPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _progressPanel.Children.Add(_progress);
        var times = new Grid { Margin = new Thickness(0, 7, 0, 0) };
        times.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(58) });
        times.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        times.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(58) });
        times.Children.Add(_posText);
        Grid.SetColumn(_qualityText, 1);
        times.Children.Add(_qualityText);
        Grid.SetColumn(_durText, 2);
        times.Children.Add(_durText);
        Grid.SetRow(times, 1);
        _progressPanel.Children.Add(times);

        _playGlyph = new Grid { Width = 26, Height = 26 };
        _playTriangle = new Path
        {
            Data = Geometry.Parse("M3,1 L24,13 L3,25 Z"),
            Fill = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(2, 0, 0, 0),
        };
        _pauseBars = new Path
        {
            Data = Geometry.Parse("M3,1 H9.5 V25 H3 Z M16.5,1 H23 V25 H16.5 Z"),
            Fill = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed,
        };
        _playGlyph.Children.Add(_playTriangle);
        _playGlyph.Children.Add(_pauseBars);
        _playButton = new Button
        {
            Style = (Style)Application.Current.Resources["IconButton"],
            Width = 56, Height = 56,
            Content = _playGlyph,
            ToolTip = "播放 / 暂停",
        };
        _playButton.Click += (_, _) => _player.ToggleOrStart();

        _modeGlyph = new TextBlock { Text = "⇉", FontSize = 14, Foreground = new SolidColorBrush(Color.FromArgb(0xBB, 0xFF, 0xFF, 0xFF)), VerticalAlignment = VerticalAlignment.Center };
        _modeText = new TextBlock { Text = "顺序播放", FontSize = 12, Visibility = Visibility.Collapsed };
        _modeButton = new Button
        {
            Style = (Style)Application.Current.Resources["IconButton"],
            Width = 44, Height = 44,
            Content = _modeGlyph,
            ToolTip = "顺序播放 / 单曲循环",
        };
        _modeButton.Click += (_, _) => { _player.ToggleMode(); UpdateMode(); };

        _lyricsButton = new Button
        {
            Style = (Style)Application.Current.Resources["IconButton"],
            Width = 44, Height = 44,
            Content = new TextBlock
            {
                Text = "\uE7F4", FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 14,
            },
            ToolTip = "桌面歌词 开 / 关",
        };
        _lyricsButton.Click += (_, _) => ToggleDesktopLyrics();
        _controls = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 18, 0, 0),
        };
        var prev = new Button
        {
            Style = (Style)Application.Current.Resources["IconButton"],
            Width = 44, Height = 44,
            Content = new TextBlock { Text = "\uE100", FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 15 },
        };
        prev.Click += (_, _) => _player.Previous();
        var next = new Button
        {
            Style = (Style)Application.Current.Resources["IconButton"],
            Width = 44, Height = 44,
            Content = new TextBlock { Text = "\uE101", FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 15 },
        };
        next.Click += (_, _) => _player.Next();
        _controls.Children.Add(_modeButton);
        _controls.Children.Add(new Border { Width = 10 });
        _controls.Children.Add(prev);
        _controls.Children.Add(_playButton);
        _controls.Children.Add(next);
        _controls.Children.Add(new Border { Width = 10 });
        _controls.Children.Add(_lyricsButton);

        _stack.BackdropDrawnByHost = true;
        _stack.IsHitTestVisible = false;
        // 邻行的「九次位移涂抹」会把笔画糊粗一圈，桌面歌词窗一直是关的（所以那边字看着正常），
        // 播放页却开着 —— 用户看到的「非读取行字体粗」就是这个。这里跟桌面歌词对齐：关掉。
        _stack.SoftNeighbours = false;
        _stack.FanReferenceWidth = TiltReferenceWidth;
        _noLyrics = new TextBlock
        {
            Text = "纯音乐，暂无歌词",
            FontSize = 14,
            Foreground = new SolidColorBrush(Color.FromArgb(0x88, 0xFF, 0xFF, 0xFF)),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed,
        };
        _lyricsHost = new Border { Child = new Grid { Children = { _stack, _noLyrics } }, Background = Brushes.Transparent };

        _content.ClipToBounds = true;

        // Top strip: a back arrow (returns to the library inside the same window) and, on the wide
        // arrangement, nothing else — the reference keeps the ⋯ menu there, we have no actions to put
        // in it. The strip floats above the pager so both layouts share it.
        _backButton = new Button
        {
            Style = (Style)Application.Current.Resources["IconButton"],
            Width = 40,
            Height = 40,
            Content = new TextBlock
            {
                Text = "\uE72B", FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 13,
                Foreground = Brushes.White,
            },
            ToolTip = "返回列表（Esc）",
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(16, 10, 0, 0),
        };
        _backButton.Click += (_, _) =>
        {
            CloseMoreMenu();
            BackRequested?.Invoke();
        };

        _moreButton = new Button
        {
            Style = (Style)Application.Current.Resources["IconButton"],
            Width = 40,
            Height = 40,
            Content = new TextBlock
            {
                Text = "\uE712", FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 16,
                Foreground = Brushes.White,
            },
            ToolTip = "播放器设置",
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 10, 16, 0),
        };
        _moreButton.Click += (_, _) => ToggleMoreMenu();

        // The settings sheet lives in the page itself (not a Popup): it stays inside the window, it
        // is captured by the QA renderer, and a click on the page background closes it.
        _moreScrim = new Rectangle
        {
            Fill = new SolidColorBrush(Color.FromArgb(0x01, 0, 0, 0)),
            Visibility = Visibility.Collapsed,
        };
        _moreScrim.MouseLeftButtonUp += (_, _) => CloseMoreMenu();
        _moreHost = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 54, 16, 0),
            Visibility = Visibility.Collapsed,
        };

        _root.Children.Add(_backdrop);
        _root.Children.Add(_scrim);
        _root.Children.Add(_content);
        _root.Children.Add(_backButton);
        _root.Children.Add(_moreButton);
        _root.Children.Add(_moreScrim);
        _root.Children.Add(_moreHost);
        Children.Add(_root);

        Loaded += (_, _) => EnsureInitialised();
        SizeChanged += OnSizeChanged;
        _player.TrackChanged += OnTrackChanged;
        _player.CoverChanged += OnCoverArrived;
        _player.LyricsChanged += OnLyricsChanged;
        _player.StateChanged += OnStateChanged;
        _player.Tick += OnTick;
        // The lyrics repaint on the composition tick, not on the 16 ms player clock: that clock
        // exists to move the progress bar and to end/advance a track, and painting from it capped
        // the karaoke at 60 Hz on a 179 Hz display (the "帧数提不上去" the user could see).
        System.Windows.Media.CompositionTarget.Rendering += OnFrameTick;
        Unloaded += (_, _) =>
        {
            System.Windows.Media.CompositionTarget.Rendering -= OnFrameTick;
            _player.TrackChanged -= OnTrackChanged;
            _player.CoverChanged -= OnCoverArrived;
            _player.LyricsChanged -= OnLyricsChanged;
            _player.StateChanged -= OnStateChanged;
            _player.Tick -= OnTick;
        };
    }

    /// <summary>Rebuilds the arrangement; the shared elements are re-parented, so this only runs when
    /// the window crosses the portrait/wide threshold or changes size while portrait.</summary>
    private void BuildLayout(bool wide)
    {
        _wide = wide;
        // Every shared piece must be released from whatever panel held it last — a UI element can
        // only have one parent, and a silently failed re-parent leaves the page half empty (the
        // progress bar and control row used to vanish when switching back from the wide layout).
        DetachShared();
        _headerStack = null;
        _content.Children.Clear();
        _content.RowDefinitions.Clear();
        _content.ColumnDefinitions.Clear();
        // Leave room for the floating back button so it never sits on top of the cover/title.
        _content.Margin = new Thickness(0, 52, 0, 0);

        if (wide)
        {
            _content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(400) });
            _content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            _content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            var left = new Grid { Margin = new Thickness(44, 40, 28, 36) };
            left.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            left.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            left.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var head = new StackPanel { Orientation = Orientation.Horizontal };
            _coverFrame.Width = 86;
            _coverFrame.Height = 86;
            _coverFrame.CornerRadius = new CornerRadius(0);
            _coverFrame.HorizontalAlignment = HorizontalAlignment.Left;
            _coverFrame.VerticalAlignment = VerticalAlignment.Top;
            head.Children.Add(_coverFrame);
            _info.Margin = new Thickness(16, 2, 0, 0);
            _info.VerticalAlignment = VerticalAlignment.Center;
            head.Children.Add(_info);
            _headerParent = head;
            left.Children.Add(head);

            var bottom = new StackPanel { VerticalAlignment = VerticalAlignment.Bottom };
            _caption.Visibility = Visibility.Visible;
            bottom.Children.Add(_caption);
            bottom.Children.Add(_progressPanel);
            // Keep the transport centred on the progress bar: the play button is the middle of the
            // five-item row, so a centred row puts it exactly under the bar's centre.
            _controls.HorizontalAlignment = HorizontalAlignment.Center;
            bottom.Children.Add(_controls);
            Grid.SetRow(bottom, 2);
            left.Children.Add(bottom);

            _content.Children.Add(left);
            Grid.SetColumn(_lyricsHost, 1);
            _lyricsHost.Margin = new Thickness(0, 24, 40, 24);
            _content.Children.Add(_lyricsHost);
        }
        else
        {
            _content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            _content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });      // header
            _content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // lyrics
            _content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });      // progress
            _content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });      // controls

            // Header flips between the compact lyrics-page form (46 px thumb beside the title) and the
            // cover-page form (big square above the title) — click it to switch, like the reference's
            // cover/lyrics pages.
            _headerStack = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(28, 16, 28, 0),
                Children = { _coverFrame, _info },
                Background = Brushes.Transparent,
                Cursor = Cursors.Hand,
                ToolTip = "点击切换 封面 / 歌词",
            };
            _headerStack.MouseLeftButtonUp += (_, _) => SetCompactHeader(!_compactHeader);
            _headerParent = _headerStack;
            _content.Children.Add(_headerStack);

            Grid.SetRow(_lyricsHost, 1);
            _lyricsHost.Margin = new Thickness(16, 14, 16, 8);
            _content.Children.Add(_lyricsHost);

            _progressPanel.Margin = new Thickness(28, 0, 28, 0);
            _caption.Visibility = Visibility.Collapsed;
            Grid.SetRow(_progressPanel, 2);
            _content.Children.Add(_progressPanel);

            _controls.Margin = new Thickness(0, 20, 0, 22);
            _controls.HorizontalAlignment = HorizontalAlignment.Center;
            Grid.SetRow(_controls, 3);
            _content.Children.Add(_controls);

            SetCompactHeader(_compactHeader);
        }
        UpdateCoverSize();
    }

    /// <summary>Removes the reused elements from whichever panel currently owns them.</summary>
    private void DetachShared()
    {
        foreach (var element in new FrameworkElement[]
                 { _coverFrame, _info, _progressPanel, _controls, _caption, _lyricsHost })
        {
            if (element.Parent is Panel panel) panel.Children.Remove(element);
        }
    }

    /// <summary>Portrait only: compact = lyrics-first (small thumb in a header row), otherwise the
    /// big cover page. Wide mode always uses its own two-column arrangement.</summary>
    private void SetCompactHeader(bool compact)
    {
        _compactHeader = compact;
        if (_wide || _headerStack == null) return;

        if (compact)
        {
            _headerStack.Orientation = Orientation.Horizontal;
            _coverFrame.HorizontalAlignment = HorizontalAlignment.Left;
            _coverFrame.VerticalAlignment = VerticalAlignment.Center;
            _coverFrame.Width = 46;
            _coverFrame.Height = 46;
            _coverFrame.CornerRadius = new CornerRadius(0);
            _coverFrame.Margin = new Thickness(0);
            _info.Margin = new Thickness(12, 0, 0, 0);
            _info.VerticalAlignment = VerticalAlignment.Center;
            _title.FontSize = 16;
            _artist.FontSize = 13;
            _artist.Margin = new Thickness(0, 2, 0, 0);
            _headerStack.HorizontalAlignment = HorizontalAlignment.Left;
        }
        else
        {
            _headerStack.Orientation = Orientation.Vertical;
            _coverFrame.HorizontalAlignment = HorizontalAlignment.Center;
            _coverFrame.VerticalAlignment = VerticalAlignment.Top;
            UpdateCoverSize();
            _coverFrame.Margin = new Thickness(0, 0, 0, 20);
            _info.Margin = new Thickness(0);
            _info.VerticalAlignment = VerticalAlignment.Top;
            _title.FontSize = 26;
            _artist.FontSize = 14;
            _artist.Margin = new Thickness(0, 8, 0, 0);
            _headerStack.HorizontalAlignment = HorizontalAlignment.Stretch;
        }
        ApplyPlayerSettings();
    }

    /// <summary>Re-applies the Player* settings (called when the settings window changes them).</summary>
    public void RefreshFromSettings()
    {
        EnsureInitialised();
        if (!_initialised) return;
        ApplyPlayerSettings();
        UpdateTrack();
        UpdateMode();
        _stack.Snap();
    }

    private bool _playerApplyQueued;

    /// <summary>Coalesces slider changes into one apply per frame.</summary>
    private void QueuePlayerApply()
    {
        if (_playerApplyQueued) return;
        _playerApplyQueued = true;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _playerApplyQueued = false;
            ApplyPlayerSettings();
        }), DispatcherPriority.Render);
    }

    private DispatcherTimer? _settingsSaveTimer;

    /// <summary>Writes settings.json once the user stops moving the slider (a synchronous JSON write
    /// per drag step was several milliseconds of the stutter).</summary>
    private void QueuePlayerSettingsSave()
    {
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
    }

    /// <summary>First real layout: builds the arrangement once the element has a size. Called from
    /// Loaded *and* the first SizeChanged, because a page that starts out collapsed can reach a
    /// presentation source without ever raising Loaded while hidden.</summary>
    private void EnsureInitialised()
    {
        if (_initialised || ActualWidth < 2 || ActualHeight < 2) return;
        _initialised = true;
        BuildLayout(ActualWidth >= WideThreshold && ActualWidth > ActualHeight);
        ApplyPlayerSettings();
        UpdateTrack();
        UpdateMode();
        SyncLyrics();
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        EnsureInitialised();
        if (!_initialised) return;
        bool wide = ActualWidth >= WideThreshold && ActualWidth > ActualHeight;
        if (wide != _wide)
        {
            BuildLayout(wide);
            ApplyPlayerSettings();
            return;
        }
        UpdateCoverSize();
        // Re-laying out the lyric column costs a glyph-cache rebuild, so during a drag only do it once
        // the user stops moving (otherwise resizing stutters).
        _resizeDebounce?.Stop();
        _resizeDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(140) };
        _resizeDebounce.Tick += (_, _) =>
        {
            _resizeDebounce?.Stop();
            ApplyPlayerSettings();
        };
        _resizeDebounce.Start();

        // Freeze the lyric painting while the edge is being dragged: the karaoke renderer redraws
        // every visible line each frame, and during a resize that is what made dragging feel heavy
        // (measured 27 ms/step with lyrics live vs 9.4 ms/step with the page empty).
        _resizeHold = true;
        _resizeRelease?.Stop();
        _resizeRelease = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _resizeRelease.Tick += (_, _) =>
        {
            _resizeRelease?.Stop();
            _resizeHold = false;
            _stack.SoftNeighbours = false;
            _stack.Snap();
        };
        _resizeRelease.Start();
        // The soft-neighbour smear draws nine copies per neighbour line; skip it while dragging.
        _stack.SoftNeighbours = false;
    }

    /// <summary>WM_ENTERSIZEMOVE / WM_EXITSIZEMOVE. A window move or resize runs its modal loop on
    /// this thread, so every millisecond the page spends redrawing lyrics is a millisecond the window
    /// does not follow the cursor — which is what "dragging stutters" actually is.</summary>
    private bool _dragHold;
    /// <summary>Whether the neighbour smear was on when the drag loop started, so the drag can restore
    /// it instead of guessing. See SetWindowDragHold.</summary>
    private bool _softBeforeDrag;

    public void SetWindowDragHold(bool on)
    {
        if (_dragHold == on) return;
        _dragHold = on;
        if (on)
        {
            _softBeforeDrag = _stack.SoftNeighbours;
            _stack.SoftNeighbours = false;
            return;
        }
        // Restore, do NOT hard-code true. Forcing the nine-tap smear on after every window move left
        // every neighbour (distance >= 2) baked with a ±(font*0.072) ghost for the rest of the
        // session — measured as the "字体笔画变粗" that只 appears on this page and never on the
        // desktop overlay (which keeps SoftNeighbours false). A restart cleared it, one window move
        // brought it back, which is why it kept "coming back".
        _stack.SoftNeighbours = _softBeforeDrag;
        // Back out of the loop: catch up in one paint instead of replaying the skipped frames.
        _stack.Snap();
        ApplyPlayerSettings();
        OnTick(_player.PositionMs);       // 进度条与两个时间标签补齐
    }

    /// <summary>Portrait: cover is a big square (60% of the width) when the big-cover page is on.</summary>
    private void UpdateCoverSize()    {
        if (_wide) return;
        if (_compactHeader) return;
        double side = Math.Clamp(ActualWidth * 0.56, 160, 420);
        _coverFrame.Width = side;
        _coverFrame.Height = side;
        _coverFrame.CornerRadius = new CornerRadius(0);
    }

    private DispatcherTimer? _blurTimer;

    private void ApplyBackdropSettings(bool deferBlur = false)
    {
        double strength = Math.Clamp(_settings.PlayerBackdropOpacity, 0, 1);
        byte Alpha(double factor) => (byte)Math.Round(Math.Clamp(strength * factor, 0, 1) * 255);
        _scrim.Fill = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(0, 1),
            GradientStops =
            {
                new GradientStop(Color.FromArgb(Alpha(0.55), 7, 8, 11), 0),
                new GradientStop(Color.FromArgb(Alpha(1.05), 7, 8, 11), 0.5),
                new GradientStop(Color.FromArgb(Alpha(1.35), 7, 8, 11), 1),
            },
        };
        if (!deferBlur)
        {
            RebuildBackdrop();
            return;
        }
        // Baking the blurred cover costs ~30 ms; while the blur slider is moving, do it at most every
        // 130 ms instead of on every tick.
        if (_blurTimer == null)
        {
            _blurTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(130) };
            _blurTimer.Tick += (_, _) =>
            {
                _blurTimer!.Stop();
                RebuildBackdrop();
            };
        }
        _blurTimer.Stop();
        _blurTimer.Start();
    }

    /// <summary>Bakes the blurred cover into a small bitmap instead of putting a live BlurEffect on a
    /// window-sized image: a live blur re-renders every resize frame (which made dragging stutter),
    /// a 480 px bitmap just gets scaled, which is free.</summary>
    private void RebuildBackdrop()
    {
        double radius = Math.Clamp(_settings.PlayerBackdropBlur, 0, 80);
        if (_cover == null)
        {
            _backdrop.Source = null;
            _blurredFor = null;
            return;
        }
        if (ReferenceEquals(_blurredFor, _cover) && Math.Abs(_blurredRadius - radius) < 0.5 && _blurred != null)
        {
            _backdrop.Source = _blurred;
            return;
        }

        const int w = 480;
        int h = Math.Max(1, (int)Math.Round(w * (double)_cover.PixelHeight / Math.Max(1, _cover.PixelWidth)));
        double radiusPx = radius * w / Math.Max(1, _cover.PixelWidth);

        // BlurEffect samples past the visual's own bounds, so baking straight into a w×h bitmap
        // leaves a translucent rim — stretched over the window that rim is the dark band that ringed
        // all four edges of the page. Bake into a padded bitmap and keep only the middle, so the
        // result stays opaque right up to its border.
        int pad = (int)Math.Ceiling(radiusPx * 3) + 2;
        int pw = w + pad * 2, ph = h + pad * 2;

        var baseVisual = new DrawingVisual();
        using (var dc = baseVisual.RenderOpen())
            dc.DrawImage(_cover, BackdropBlur.FillRect(_cover, pw, ph));
        var flat = new RenderTargetBitmap(pw, ph, 96, 96, PixelFormats.Pbgra32);
        flat.Render(baseVisual);
        flat.Freeze();

        var blurredVisual = new DrawingVisual
        {
            Effect = radiusPx > 0.5
                ? new BlurEffect { Radius = radiusPx, KernelType = KernelType.Gaussian }
                : null,
        };
        using (var dc = blurredVisual.RenderOpen())
            dc.DrawImage(flat, new Rect(0, 0, pw, ph));
        var padded = new RenderTargetBitmap(pw, ph, 96, 96, PixelFormats.Pbgra32);
        padded.Render(blurredVisual);
        padded.Freeze();

        var blurred = new CroppedBitmap(padded, new Int32Rect(pad, pad, w, h));
        blurred.Freeze();

        _blurred = blurred;
        _blurredFor = _cover;
        _blurredRadius = radius;
        _backdrop.Source = blurred;
    }

    /// <summary>Pushes the Player* settings into the lyric renderer through a throwaway copy of the
    /// app settings, so the desktop overlay's own numbers stay untouched.</summary>
    private void ApplyPlayerSettings()
    {
        var view = _settings.Clone();
        view.FontSize = _settings.PlayerFontSize;
        view.VisibleLines = _settings.PlayerVisibleLines;
        // Portrait is a narrow column: keep the current line's growth modest and give the text more
        // of the width, otherwise every lyric wraps into three rows.
        double currentScale = _settings.PlayerCurrentScale;
        double padding = 160;
        if (!_wide)
        {
            padding = 120;
            currentScale = Math.Min(currentScale, Math.Max(1.06, 0.7 + ActualWidth / 1600.0));
        }
        view.CurrentScale = currentScale;
        view.LineGap = _settings.PlayerLineGap;
        view.Translation = _settings.PlayerTranslation;
        view.GlowEnabled = _settings.PlayerGlow;
        view.GlowStrength = _settings.PlayerGlowStrength;
        // The page has its own glow range and colour: before this it inherited the desktop overlay's
        // pair (the clone carried GlowSpread/GlowColor over), so the page could not be tuned on its own.
        view.GlowSpread = _settings.PlayerGlowSpread;
        view.GlowColor = _settings.PlayerGlowColor;
        view.TextColor = _settings.PlayerTextColor;
        view.Perspective = _settings.PlayerPerspective;
        view.TiltAngle = _settings.PlayerTiltAngle;
        view.VanishingOffset = _settings.PlayerVanishingOffset;
        // The current line is drawn at CurrentScale, so it grows past the column width; the desktop
        // overlay solves that by making the window 160 px wider than the column. Do the same here so
        // long lines never touch the window edge.
        double available = _lyricsHost.ActualWidth;
        if (available < 200)
        {
            // Before the first measure the host reports 0; fall back to the column it will live in,
            // never to the whole window — that made a wide page draw lyrics across the cover column.
            available = _wide ? ActualWidth - 400 - 40 : ActualWidth - 32;
        }
        available = Math.Max(200, available);
        view.LineWidth = _settings.PlayerLineWidth >= 1
            ? Math.Min(_settings.PlayerLineWidth, available - 40)
            : available - padding;
        view.LineWidth = Math.Clamp(view.LineWidth, 200, 1600);

        // How many cues fit this window feeds both the slider ceiling and the applied count, so a
        // setting saved for a taller window never spills over the edge of a shorter one.
        double hostHeight = _lyricsHost.ActualHeight;
        if (hostHeight < 120) hostHeight = Math.Max(240, ActualHeight - 190);
        double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        _maxLineCount = Controls.LyricStackView.LinesThatFit(hostHeight, view.FontSize * dpi,
            view.LineGap * dpi, currentScale, view.Perspective);
        view.VisibleLines = Math.Clamp(_settings.PlayerVisibleLines, 3, Math.Max(3, _maxLineCount));

        _appliedLineWidth = view.LineWidth;
        _appliedVisibleLines = view.VisibleLines;
        if (Environment.GetEnvironmentVariable("LYRIC_PAGE") == "1")
        {
            try
            {
                System.IO.File.AppendAllText(System.IO.Path.Combine(Services.AppPaths.DataDir, "page-apply.log"),
                    $"wide={_wide} window={ActualWidth:0}x{ActualHeight:0} host={_lyricsHost.ActualWidth:0}x{_lyricsHost.ActualHeight:0} " +
                    $"available={available:0} lineWidth={view.LineWidth:0} rows={_maxLineCount} visible={view.VisibleLines} " +
                    $"dpi={dpi:0.00} font={view.FontSize:0}\n");
            }
            catch { }
        }
        _stack.ApplySettings(view, dpi);
        _stack.SetDocument(_player.Lyrics, (long)Math.Max(0, _player.PositionMs));
        ApplyBackdropSettings(true);
    }

    private void UpdateTrack()
    {
        var song = _player.Current;
        _title.Text = song?.DisplayTitle ?? "未在播放";
        _artist.Text = song == null ? "" : $"{song.DisplayArtist} · {song.SourceLabel}";
        _coverBrush.ImageSource = song?.Cover;
        _coverFrame.Background = song?.Cover != null ? _coverBrush : _coverFallback;
        _coverGlyph.Visibility = song?.Cover == null ? Visibility.Visible : Visibility.Collapsed;
        _cover = song?.Cover;
        if (!ReferenceEquals(_blurredFor, _cover)) _blurredFor = null;
        RebuildBackdrop();
        _progress.Maximum = Math.Max(1, song?.Duration.TotalSeconds ?? 0);
        _durText.Text = song == null ? "0:00" : "-" + FormatTime(_progress.Maximum);
        _qualityText.Text = QualityLabel(song);
        _caption.Text = song == null ? "" : $"「{song.DisplayArtist} / {song.DisplayTitle}";
        _lyricsButton.ToolTip = _settings.Enabled ? "桌面歌词：已开启（点击关闭）" : "桌面歌词：已关闭（点击开启）";
    }

    /// <summary>Format label under the bar: local files only carry their container, which is honest.</summary>
    private static string QualityLabel(Models.Song? song)
    {
        if (song == null) return "";
        string ext = System.IO.Path.GetExtension(song.Path).TrimStart('.').ToUpperInvariant();
        if (ext.Length == 0) return "";
        bool lossless = ext is "FLAC" or "WAV" or "AIFF" or "AIF" or "APE";
        return lossless ? ext + " · 无损" : ext;
    }

    private void UpdateMode()
    {
        bool repeat = _player.Mode == PlayMode.RepeatOne;
        _modeGlyph.Text = repeat ? "↻" : "⇉";
        _modeText.Text = repeat ? "单曲循环" : "顺序播放";
        bool playing = _player.PlaybackState;
        _playTriangle.Visibility = playing ? Visibility.Collapsed : Visibility.Visible;
        _pauseBars.Visibility = playing ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ToggleDesktopLyrics()
    {
        _settings.Enabled = !_settings.Enabled;
        SettingsStore.Save(_settings);
        _lyricsButton.ToolTip = _settings.Enabled ? "桌面歌词：已开启（点击关闭）" : "桌面歌词：已关闭（点击开启）";
        DesktopLyricsToggled?.Invoke();
    }

    /// <summary>拖完/点完才 seek 一次：拖动过程中每变一点就 seek，会把媒体管线打到卡死，
    /// 往前拖尤其明显（seek 风暴之后状态就回不来了）。</summary>
    private void EndSeek()
    {
        if (!_dragging && !_seekPending) return;
        _dragging = false;
        _seekPending = false;
        _player.Seek(_progress.Value * 1000);
    }

    private bool _seekPending;

    private void OnProgressChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        // Click-to-seek also lands here, and the value is user-driven until the mouse is released.
        if (_dragging) _seekPending = true;
    }

    private void OnTrackChanged() => Dispatcher.Invoke(() =>
    {
        UpdateTrack();
        ApplyPlayerSettings();
        SyncLyrics();
    });

    private void OnStateChanged() => Dispatcher.Invoke(UpdateMode);

    /// <summary>Cloud artwork arrives after the track change; the page must not stay on the glyph.</summary>
    private void OnCoverArrived(Models.Song song) => Dispatcher.BeginInvoke(new Action(() =>
    {
        if (!ReferenceEquals(_player.Current, song)) return;
        UpdateTrack();
    }));

    private void OnLyricsChanged() => Dispatcher.Invoke(() =>
    {
        SyncLyrics();
        _stack.Snap();
    });

    private void OnTick(double positionMs)
    {
        if (!IsVisible) return;
        // Dragging the window freezes the whole page. The lyric column, the progress bar and both
        // time labels all repaint, and every one of those is a dirty region the compositor has to
        // redo while the modal move loop sits on this same thread. SetWindowDragHold() catches the
        // page up in one go when the loop ends.
        if (_dragHold) return;
        if (!_dragging)
        {
            _progress.Value = Math.Clamp(positionMs / 1000, 0, _progress.Maximum);
            _posText.Text = FormatTime(positionMs / 1000);
        }
        long total = (long)Math.Max(0, _player.Playback.DurationMs);
        if (total > 0 && _player.Current != null)
        {
            // Remaining time, like the reference player ("-5:02"), not the total.
            double left = Math.Max(0, total - positionMs) / 1000.0;
            _durText.Text = "-" + FormatTime(left);
        }
    }

    /// <summary>One repaint per composed frame, which is what lifts the karaoke above the 60 Hz the
    /// player clock was imposing. It also advances the scroll/morph animations.</summary>
    private void OnFrameTick(object? sender, EventArgs e)
    {
        if (!_initialised || !IsVisible || _resizeHold || _dragHold) return;
        _stack.Advance(_demoPin ?? (long)Math.Max(0, _player.PositionMs));
    }

    private void SyncLyrics()    {
        _stack.SetDocument(_player.Lyrics, (long)Math.Max(0, _player.PositionMs));
        _lastCueIndex = _stack.CurrentIndex;
        _noLyrics.Text = _player.Current == null ? "还没有播放歌曲" : "纯音乐，暂无歌词";
        _noLyrics.Visibility = _stack.HasDocument ? Visibility.Collapsed : Visibility.Visible;
    }

    private static string FormatTime(double seconds)
    {
        if (seconds < 0 || double.IsNaN(seconds)) seconds = 0;
        int s = (int)seconds;
        return $"{s / 60}:{s % 60:00}";
    }

    // ---------- 右上角「⋯」播放器设置 ----------
    private void ToggleMoreMenu()
    {
        if (_moreHost.Visibility == Visibility.Visible)
        {
            CloseMoreMenu();
            return;
        }
        double width = Math.Clamp(ActualWidth - 80, 260, 330);
        _moreHost.Child = new Border
        {
            Width = width,
            Background = new SolidColorBrush(Color.FromArgb(0xF7, 0x14, 0x17, 0x1E)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x33, 0x3B, 0x4C)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Child = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                MaxHeight = Math.Max(240, ActualHeight - 140),
                Content = BuildMoreMenu(width),
            },
        };
        _moreHost.MaxHeight = Math.Max(260, ActualHeight - 110);
        _moreScrim.Visibility = Visibility.Visible;
        _moreHost.Visibility = Visibility.Visible;
    }

    private void CloseMoreMenu()
    {
        _moreHost.Visibility = Visibility.Collapsed;
        _moreScrim.Visibility = Visibility.Collapsed;
    }

    private StackPanel BuildMoreMenu(double width)
    {
        _rowLabels.Clear();
        _rowSliders.Clear();
        var stack = new StackPanel { Margin = new Thickness(16, 14, 16, 16) };
        stack.Children.Add(new TextBlock
        {
            Text = "播放器设置", FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White,
            Margin = new Thickness(0, 0, 0, 4),
        });

        double lyricMax = Math.Max(240, Math.Round(width - 24));
        SliderRow(stack, "歌词字号", 18, 72, _settings.PlayerFontSize,
            v => $"{v:0} px", v => _settings.PlayerFontSize = Math.Round(v));
        SliderRow(stack, "歌词行宽", 0, lyricMax, _settings.PlayerLineWidth,
            v => v < 1 ? "跟随窗口" : $"{v:0} px", v => _settings.PlayerLineWidth = Math.Round(v));
        SliderRow(stack, "行间距", 0, 40, _settings.PlayerLineGap,
            v => $"{v:0} px", v => _settings.PlayerLineGap = Math.Round(v));
        // The ceiling is whatever actually fits this window at the current font, gap and scale.
        SliderRow(stack, "显示行数", 3, Math.Max(3, _maxLineCount), _settings.PlayerVisibleLines,
            v => $"{v:0} 行", v => _settings.PlayerVisibleLines = (int)Math.Round(v), snap: 1);
        SliderRow(stack, "当前行放大", 1.0, 2.0, _settings.PlayerCurrentScale,
            v => $"{v:0.00}×", v => _settings.PlayerCurrentScale = Math.Round(v, 2));
        SwitchRow(stack, "3D 倾斜歌词", _settings.PlayerPerspective, on => _settings.PlayerPerspective = on);
        SliderRow(stack, "3D 倾斜角度", -85, 0, _settings.PlayerTiltAngle,
            v => $"{v:0}°", v => _settings.PlayerTiltAngle = Math.Round(v));
        SliderRow(stack, "交点位置", 0.4, 4.0, _settings.PlayerVanishingOffset,
            v => $"{v:0.0}× 列宽", v => _settings.PlayerVanishingOffset = Math.Round(v, 2));
        SwitchRow(stack, "显示翻译", _settings.PlayerTranslation, on => _settings.PlayerTranslation = on);
        // The three glow rows live in their own panel so they can be dimmed while 歌词辉光 is off —
        // dragging 强度 with the switch off changed a number that had no effect, which is exactly
        // what made the feature look broken.
        var glowPanel = new StackPanel { Opacity = _settings.PlayerGlow ? 1 : 0.45, IsEnabled = _settings.PlayerGlow };
        SwitchRow(stack, "歌词辉光", _settings.PlayerGlow, on =>
        {
            _settings.PlayerGlow = on;
            glowPanel.IsEnabled = on;
            glowPanel.Opacity = on ? 1 : 0.45;
        });
        stack.Children.Add(glowPanel);
        SliderRow(glowPanel, "辉光强度", 0, 200, _settings.PlayerGlowStrength * 100,
            v => $"{v:0}%", v => _settings.PlayerGlowStrength = Math.Round(v) / 100.0);
        SliderRow(glowPanel, "辉光范围", 50, 200, _settings.PlayerGlowSpread * 100,
            v => $"{v:0}%", v => _settings.PlayerGlowSpread = Math.Round(v) / 100.0);
        ColorRow(glowPanel, "辉光颜色", _settings.PlayerGlowColor, hex =>
        {
            _settings.PlayerGlowColor = hex;
            ApplyPlayerSettings();
        });
        ColorRow(stack, "文字颜色", _settings.PlayerTextColor, hex =>
        {
            _settings.PlayerTextColor = hex;
            ApplyPlayerSettings();
        });
        SliderRow(stack, "背景模糊", 0, 80, _settings.PlayerBackdropBlur,
            v => $"{v:0}", v => _settings.PlayerBackdropBlur = Math.Round(v));
        SliderRow(stack, "背景暗度", 0, 100, _settings.PlayerBackdropOpacity * 100,
            v => $"{v:0}%", v => _settings.PlayerBackdropOpacity = Math.Round(v) / 100.0);
        return stack;
    }

    private void SliderRow(StackPanel parent, string label, double min, double max, double value,
                           Func<double, string> format, Action<double> apply, double snap = 0)
    {
        var head = new Grid { Margin = new Thickness(0, 10, 0, 0) };
        var name = new TextBlock
        {
            Text = label, FontSize = 12.5, Foreground = new SolidColorBrush(Color.FromRgb(0xE8, 0xEB, 0xF2)),
            VerticalAlignment = VerticalAlignment.Center,
        };
        var readout = new TextBlock
        {
            Text = format(Math.Clamp(value, min, max)), FontSize = 11.5, HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(Color.FromRgb(0x6F, 0x9E, 0xFF)),
        };
        head.Children.Add(name);
        head.Children.Add(readout);

        var slider = new DragOnlySlider
        {
            Style = (Style)Application.Current.Resources["ThinSlider"],
            Minimum = min,
            Maximum = max,
            Value = Math.Clamp(value, min, max),
            Margin = new Thickness(0, 6, 0, 2),
            IsSnapToTickEnabled = snap > 0,
            TickFrequency = snap > 0 ? snap : 0.01,
        };
        slider.ValueChanged += (_, e) =>
        {
            readout.Text = format(e.NewValue);
            apply(e.NewValue);
            // 拖动一次会连发几十个 ValueChanged。以前每个都同步落盘 + 立刻重建歌词几何，
            // 一帧里要做好几遍；现在合并成每帧最多一次，写盘推迟到停手之后。
            QueuePlayerApply();
            QueuePlayerSettingsSave();
        };

        parent.Children.Add(head);
        parent.Children.Add(slider);
        _rowLabels.Add(label);
        _rowSliders[label] = slider;
    }

    private void SwitchRow(StackPanel parent, string label, bool value, Action<bool> apply)
    {
        var grid = new Grid { Margin = new Thickness(0, 12, 0, 2) };
        grid.Children.Add(new TextBlock
        {
            Text = label, FontSize = 12.5, Foreground = new SolidColorBrush(Color.FromRgb(0xE8, 0xEB, 0xF2)),
            VerticalAlignment = VerticalAlignment.Center,
        });
        var toggle = new CheckBox
        {
            Style = (Style)Application.Current.Resources["Switch"],
            IsChecked = value,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };
        toggle.Checked += (_, _) => { apply(true); SettingsStore.Save(_settings); ApplyPlayerSettings(); };
        toggle.Unchecked += (_, _) => { apply(false); SettingsStore.Save(_settings); ApplyPlayerSettings(); };
        grid.Children.Add(toggle);
        parent.Children.Add(grid);
        _rowLabels.Add(label);
    }

    private void ColorRow(StackPanel parent, string label, string hex, Action<string> apply)
    {
        var grid = new Grid { Margin = new Thickness(0, 12, 0, 2) };
        grid.Children.Add(new TextBlock
        {
            Text = label, FontSize = 12.5, Foreground = new SolidColorBrush(Color.FromRgb(0xE8, 0xEB, 0xF2)),
            VerticalAlignment = VerticalAlignment.Center,
        });
        var swatch = new Border
        {
            Width = 40, Height = 22, CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x33, 0x3B, 0x4C)),
            Background = new SolidColorBrush(ParseHex(hex)),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = Cursors.Hand,
            ToolTip = "#" + hex.ToUpperInvariant(),
        };
        swatch.MouseLeftButtonUp += (_, _) =>
        {
            // Inline, inside the sheet: a colour is never worth a second window.
            string original = hex;
            var host = new ContentControl();
            var panel = new Controls.ColorPickerPanel(hex);
            // Live preview while dragging; the file is only written once, on 确定.
            panel.ColorChanged += picked => PreviewColor(() => apply(picked));
            panel.Committed += picked =>
            {
                hex = picked;
                swatch.Background = new SolidColorBrush(ParseHex(hex));
                swatch.ToolTip = "#" + hex.ToUpperInvariant();
                _colorApplyPending = null;   // a queued preview must not overwrite the committed value
                apply(hex);
                SettingsStore.Save(_settings);
                host.Content = null;
            };
            panel.Cancelled += () =>
            {
                _colorApplyPending = null;
                apply(original);
                host.Content = null;
            };
            host.Content = panel;
            parent.Children.Add(host);
        };
        grid.Children.Add(swatch);
        parent.Children.Add(grid);
        _rowLabels.Add(label);
    }

    private bool _colorApplyQueued;
    private Action? _colorApplyPending;

    /// <summary>One recolour per rendered frame while an inline colour picker is dragged: the panel
    /// fires on every mouse move, and each apply rebuilds the page's lyric settings.</summary>
    private void PreviewColor(Action apply)
    {
        _colorApplyPending = apply;
        if (_colorApplyQueued) return;
        _colorApplyQueued = true;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _colorApplyQueued = false;
            var pending = _colorApplyPending;
            _colorApplyPending = null;
            pending?.Invoke();
        }), System.Windows.Threading.DispatcherPriority.Render);
    }

    private static Color ParseHex(string hex)
    {        hex = (hex ?? "").Trim().TrimStart('#');
        if (hex.Length != 6) return Colors.White;
        try
        {
            return Color.FromRgb(
                Convert.ToByte(hex.Substring(0, 2), 16),
                Convert.ToByte(hex.Substring(2, 2), 16),
                Convert.ToByte(hex.Substring(4, 2), 16));
        }
        catch { return Colors.White; }
    }

    /// <summary>QA: open the ⋯ menu (popups are separate windows, so this is only for interaction).</summary>
    public void DebugOpenMoreMenu() => ToggleMoreMenu();

    /// <summary>QA: current layout mode plus the numbers behind it.</summary>
    public string DebugState()
    {
        return $"wide={_wide} size={ActualWidth:0}x{ActualHeight:0} cover={_coverFrame.Width:0}x{_coverFrame.Height:0} " +
               $"lyrics={_lyricsHost.ActualWidth:0}x{_lyricsHost.ActualHeight:0} rows={_maxLineCount} " +
               $"lineWidth={_appliedLineWidth:0} visible={_appliedVisibleLines} " +
               $"song='{_player.Current?.DisplayTitle}' hasDoc={_stack.HasDocument} cue={_stack.CurrentIndex}";
    }

    /// <summary>QA: switch the portrait header between the compact and the big-cover form.</summary>
    public void DebugSetCompactHeader(bool compact) => SetCompactHeader(compact);

    /// <summary>QA: drive the lyric renderer directly, so its per-frame cost can be timed without the
    /// 16 ms player clock in the way.</summary>
    internal void DebugAdvance(long positionMs) => _stack.Tick(positionMs);
    /// <summary>Live scroll offset of the transition, sampled per composed frame by --cue-perf to
    /// count how many distinct animation positions a transition actually gets.</summary>
    internal double DebugScrollDelta => _stack.DebugScrollDelta;
    internal double DebugPitch => _stack.DebugPitch;
    internal double DebugMorph => _stack.DebugMorph;
    internal double DebugShift => _stack.DebugShift;
    internal double DebugOffsetFix => _stack.DebugOffsetFix;
    internal string DebugRows => _stack.DebugRows;
    internal string DebugFrameWork => _stack.DebugFrameWork;

    /// <summary>QA: how long the column's own DrawCore takes per repaint (the geometry emission, not
    /// WPF's rasterisation of it).</summary>
    internal string DebugRenderStats() =>
        $"stack-renders={_stack.RenderCount} lastMs={_stack.LastRenderMs:0.00} " +
        $"avgMs={(_stack.RenderCount > 0 ? _stack.RenderMsTotal / _stack.RenderCount : 0):0.00}";

    /// <summary>QA: hide the lyric column or the backdrop, to attribute the frame cost.</summary>
    internal void DebugSetPartVisible(string part, bool on)
    {
        if (part == "lyrics") _lyricsHost.Visibility = on ? Visibility.Visible : Visibility.Hidden;
        else if (part == "backdrop") _backdrop.Visibility = on ? Visibility.Visible : Visibility.Hidden;
        else if (part == "scrim") _scrim.Visibility = on ? Visibility.Visible : Visibility.Hidden;
    }

    /// <summary>QA: software draw cost of each layer. RenderTargetBitmap is CPU raster, so the numbers
    /// are a stable relative measure of how much drawing each layer emits (not the GPU time).</summary>
    internal string DebugDrawCost()
    {
        string Measure(string tag, Visual visual, double w, double h, int iterations = 6)
        {
            if (w < 2 || h < 2) return $"{tag}=n/a";
            var bitmap = new RenderTargetBitmap((int)w, (int)h, 96, 96, PixelFormats.Pbgra32);
            var clock = System.Diagnostics.Stopwatch.StartNew();
            for (int i = 0; i < iterations; i++) bitmap.Render(visual);
            clock.Stop();
            return $"{tag}={clock.Elapsed.TotalMilliseconds / iterations:0.00}ms";
        }

        var parts = new List<string>();
        parts.Add(Measure("stack", _stack, _stack.ActualWidth, _stack.ActualHeight));
        bool soft = _stack.SoftNeighbours;
        _stack.SoftNeighbours = false;
        parts.Add(Measure("stack-softOff", _stack, _stack.ActualWidth, _stack.ActualHeight));
        _stack.SoftNeighbours = soft;
        parts.Add(Measure("backdrop", _backdrop, _backdrop.ActualWidth, _backdrop.ActualHeight));
        parts.Add(Measure("page", _root, _root.ActualWidth, _root.ActualHeight, 3));
        return string.Join(" ", parts);
    }

    /// <summary>QA: the smallest alpha on each border of the baked backdrop. Anything below 255 is a
    /// translucent rim, which is what a dark band around the page looks like at the pixel level.</summary>
    public string DebugBackdropEdge()
    {
        if (_blurred == null) return "backdrop=null";
        var bmp = new FormatConvertedBitmap(_blurred, PixelFormats.Bgra32, null, 0);
        int w = bmp.PixelWidth, h = bmp.PixelHeight, stride = w * 4;
        var px = new byte[stride * h];
        bmp.CopyPixels(px, stride, 0);
        int Min(int x0, int x1, int y0, int y1)
        {
            int m = 255;
            for (int y = Math.Max(0, y0); y < Math.Min(h, y1); y++)
                for (int x = Math.Max(0, x0); x < Math.Min(w, x1); x++)
                {
                    int a = px[y * stride + x * 4 + 3];
                    if (a < m) m = a;
                }
            return m;
        }
        return $"blurred={w}x{h} edgeMinAlpha=[left={Min(0, 1, 0, h)} right={Min(w - 1, w, 0, h)} " +
               $"top={Min(0, w, 0, 1)} bottom={Min(0, w, h - 1, h)}]";
    }

    /// <summary>QA: the ⋯ sheet's row labels, in the order they are drawn.</summary>
    public string DebugMoreMenuRows() => _rowLabels.Count == 0 ? "(sheet closed)" : string.Join(" | ", _rowLabels);

    /// <summary>QA: move one of the ⋯ sheet's sliders, exactly as dragging it would.</summary>
    public double DebugSetRowValue(string label, double value)
    {
        if (!_rowSliders.TryGetValue(label, out var slider)) return double.NaN;
        slider.Value = value;
        return slider.Value;
    }

    /// <summary>QA: the glow numbers the page is actually painting with.</summary>
    public string DebugGlowState =>
        $"glow={(_settings.PlayerGlow ? 1 : 0)} strength={_settings.PlayerGlowStrength:0.00} " +
        $"spread={_settings.PlayerGlowSpread:0.00} radiusScale={_settings.PlayerGlowRadiusScale:0.000} " +
        $"color=#{_settings.PlayerGlowColor} | {(string.IsNullOrEmpty(_stack.DebugGlow) ? "" : _stack.DebugGlow)}";

    /// <summary>QA: set the glow colour through the same path the colour row uses.</summary>
    public void DebugSetGlowColor(string hex)
    {
        _settings.PlayerGlowColor = hex;
        ApplyPlayerSettings();
    }

    /// <summary>QA: freeze the lyric clock at <paramref name="ms"/> and paint that moment.</summary>
    public void DebugPinPosition(long ms)
    {
        _demoPin = ms;
        _stack.SetDocument(_player.Lyrics, ms);
        _stack.Snap();
        _stack.InvalidateVisual();
    }

    /// <summary>QA: move the pinned clock WITHOUT snapping, so the cue transition can be filmed.</summary>
    public void DebugPinPositionAnimated(long ms)
    {
        _demoPin = ms;
        _stack.Tick(ms);
    }

    /// <summary>QA: press the transport triangle exactly like a user click.</summary>
    public bool DebugClickPlay()
    {
        if (_playButton == null) return false;
        _playButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        return true;
    }

    /// <summary>QA: which cue the player and the stack think we are in.</summary>
    public string DebugLyrics()
    {
        var (index, line, progress, _) = _player.CurrentCue();
        return $"pos={_player.PositionMs:0} playerCue={index} '{line?.Text}' progress={progress:0.00} " +
               $"stackCue={_stack.CurrentIndex} stackLine='{_stack.CurrentLine?.Text}'";
    }

    /// <summary>QA: the karaoke numbers of the reading line (see LyricStackView.DebugKaraoke).</summary>
    public string DebugKaraoke => _stack.DebugKaraoke;

    /// <summary>QA: build every word's karaoke ink at once, so a pinned still shows the full fill.</summary>
    public void DebugBuildAllWordParts() => _stack.DebugBuildAllWordParts();
}
