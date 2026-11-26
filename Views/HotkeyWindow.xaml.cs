using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using CurrentLyrics.Services;

namespace CurrentLyrics.Views;

/// <summary>
/// 快捷键: one row per action, each row is a box that records the next chord the user presses.
///
/// 1.2.60 起规则是「一个修饰键 + 一个按键」，F1~F12 可以单开。按键捕获挂在**窗口**上而不是方框上：
/// 中文输入法开着时 WPF 把字母报成 <see cref="Key.ImeProcessed"/>，真键在
/// <see cref="KeyEventArgs.ImeProcessedKey"/> 里 —— 旧实现直接读 e.Key，所以「Ctrl+P」在这台机器上
/// 根本录不进去，只会提示「最多两个键」。
/// </summary>
public partial class HotkeyWindow : Window
{
    private static readonly (HotkeyAction Action, string Label, string Default)[] Rows =
    {
        (HotkeyAction.AboveApps, "显示在其他应用上方", AppSettings.DefaultKeyAboveApps),
        (HotkeyAction.ToggleLyrics, "开启桌面歌词", AppSettings.DefaultKeyLyrics),
        (HotkeyAction.PlayPause, "播放 / 暂停", AppSettings.DefaultKeyPlayPause),
        (HotkeyAction.Next, "下一首", AppSettings.DefaultKeyNext),
        (HotkeyAction.Previous, "上一首", AppSettings.DefaultKeyPrevious),
        // 没有默认键：由用户自己设置。
        (HotkeyAction.TransparentBg, "背景实色 / 全透明", ""),
    };

    private readonly AppSettings _settings;
    private readonly Func<IReadOnlyList<HotkeyAction>> _onApply;
    private readonly Dictionary<HotkeyAction, TextBlock> _labels = new();
    private readonly Dictionary<HotkeyAction, Border> _boxes = new();
    private HotkeyAction? _recording;

    public HotkeyWindow(AppSettings settings, Func<IReadOnlyList<HotkeyAction>> onApply)
    {
        InitializeComponent();
        _settings = settings;
        _onApply = onApply;
        BuildRows();
        Refresh();
        chkAdmin.IsChecked = _settings.RunElevated;
        txtAdmin.Text = Elevation.IsElevated ? "已以管理员权限运行" : "需要管理员权限";
        // One handler for the whole window: whichever element has focus, the next chord lands here.
        PreviewKeyDown += OnWindowKey;
        PreviewKeyUp += (_, e) => { if (_recording != null) e.Handled = true; };
    }

    /// <summary>
    /// 游戏基本都以管理员权限运行；未提权时系统不会把热键投给低权限进程（UIPI），钩子也不会被调用。
    /// 打开开关 = 保存设置 + 立刻以管理员权限重开（弹一次 UAC）；关掉 = 下次启动恢复普通权限。
    /// </summary>
    private void OnAdminToggle(object sender, RoutedEventArgs e)
    {
        bool want = chkAdmin.IsChecked == true;
        _settings.RunElevated = want;
        SettingsStore.Save(_settings);
        if (!want)
        {
            txtAdmin.Text = Elevation.IsElevated ? "已以管理员权限运行" : "需要管理员权限";
            txtStatus.Text = Elevation.IsElevated
                ? "下次启动恢复普通权限。"
                : "下次启动恢复普通权限；游戏里将收不到快捷键。";
            return;
        }
        if (Elevation.IsElevated)
        {
            txtAdmin.Text = "已以管理员权限运行";
            txtStatus.Text = "已经在管理员权限下运行。";
            return;
        }
        if (!Elevation.TryRestartElevated(out string why))
        {
            _settings.RunElevated = false;
            SettingsStore.Save(_settings);
            chkAdmin.IsChecked = false;
            txtStatus.Text = why;
            return;
        }
        // 新的管理员进程已经起来：本进程必须真退出，否则两个实例会抢同一批热键。
        if (Owner is MainWindow main) main.ForceExit();
        else Application.Current.Shutdown();
    }

    /// <summary>Current text for an action, as stored in settings.</summary>
    private string TextFor(HotkeyAction action) => action switch
    {
        HotkeyAction.AboveApps => _settings.KeyAboveApps,
        HotkeyAction.ToggleLyrics => _settings.KeyLyrics,
        HotkeyAction.PlayPause => _settings.KeyPlayPause,
        HotkeyAction.Next => _settings.KeyNext,
        HotkeyAction.Previous => _settings.KeyPrevious,
        HotkeyAction.TransparentBg => _settings.KeyTransparentBg,
        _ => "",
    };

    private void SetText(HotkeyAction action, string text)
    {
        switch (action)
        {
            case HotkeyAction.AboveApps: _settings.KeyAboveApps = text; break;
            case HotkeyAction.ToggleLyrics: _settings.KeyLyrics = text; break;
            case HotkeyAction.PlayPause: _settings.KeyPlayPause = text; break;
            case HotkeyAction.Next: _settings.KeyNext = text; break;
            case HotkeyAction.Previous: _settings.KeyPrevious = text; break;
            case HotkeyAction.TransparentBg: _settings.KeyTransparentBg = text; break;
        }
    }

    private static string DefaultFor(HotkeyAction action)
    {
        foreach (var row in Rows)
            if (row.Action == action) return row.Default;
        return "";
    }

    private void BuildRows()
    {
        foreach (var (action, label, _) in Rows)
        {
            var grid = new Grid { Margin = new Thickness(0, 10, 0, 10) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var name = new TextBlock { Text = label, FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(name, 0);
            grid.Children.Add(name);

            var value = new TextBlock
            {
                FontSize = 12,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = (Brush)FindResource("TextBrush"),
            };
            var box = new Border
            {
                Child = value,
                MinWidth = 116,
                Padding = new Thickness(10, 5, 10, 5),
                CornerRadius = new CornerRadius(8),
                BorderThickness = new Thickness(1),
                BorderBrush = (Brush)FindResource("HairlineBrush"),
                Background = (Brush)FindResource("SurfaceBrush"),
                Cursor = Cursors.Hand,
                Focusable = true,
                Margin = new Thickness(0, 0, 8, 0),
            };
            box.MouseLeftButtonDown += (_, _) => BeginCapture(action);
            Grid.SetColumn(box, 1);
            grid.Children.Add(box);

            var reset = new Button
            {
                Content = "重置",
                Style = (Style)FindResource("GhostButton"),
                Padding = new Thickness(9, 3, 9, 3),
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
            };
            reset.Click += (_, _) => ResetOne(action);
            Grid.SetColumn(reset, 2);
            grid.Children.Add(reset);

            hotkeyRows.Children.Add(grid);
            if (hotkeyRows.Children.Count < Rows.Length)
                hotkeyRows.Children.Add(new Border
                {
                    Height = 1,
                    Background = (Brush)FindResource("HairlineBrush"),
                });

            _labels[action] = value;
            _boxes[action] = box;
        }
        hotkeyRows.Children.Add(new Border { Height = 1, Background = Brushes.Transparent, Margin = new Thickness(0, 3, 0, 0) });
    }

    private void Refresh()
    {
        foreach (var (action, _, _) in Rows)
        {
            var text = TextFor(action);
            bool empty = string.IsNullOrWhiteSpace(text);
            bool ok = HotkeyText.TryParse(text, out var chord) && HotkeyText.IsAllowed(chord, out _);
            _labels[action].Text = empty ? "未设置" : text;
            _labels[action].Foreground = (Brush)FindResource(ok ? "TextBrush" : "TextDimBrush");
            // An unset optional binding is not an error — only a real but illegal chord gets the accent.
            _boxes[action].BorderBrush = (Brush)FindResource(ok || empty ? "HairlineBrush" : "AccentBrush");
        }
    }

    private void BeginCapture(HotkeyAction action)
    {
        _recording = action;
        _labels[action].Text = "按下按键…";
        _labels[action].Foreground = (Brush)FindResource("AccentBrush");
        _boxes[action].BorderBrush = (Brush)FindResource("AccentBrush");
        txtStatus.Text = "";
        _boxes[action].Focus();
        Keyboard.Focus(_boxes[action]);
        Focus();
    }

    private void CancelCapture()
    {
        _recording = null;
        Refresh();
    }

    /// <summary>Window-level capture. Reads the real key (IME / Alt normalised) and enforces the rule.</summary>
    private void OnWindowKey(object sender, KeyEventArgs e)
    {
        if (_recording is not { } action) return;
        e.Handled = true;
        var key = HotkeyText.NormalizeKey(e);
        if (key == Key.Escape) { CancelCapture(); return; }
        if (!HotkeyText.TryCapture(e, out var chord, out var reason))
        {
            // A modifier on its own is the normal first half of a chord: keep waiting quietly.
            txtStatus.Text = HotkeyText.IsModifierKey(key) ? "再按一个具体的按键（只按修饰键不算）。" : reason;
            return;
        }
        Commit(action, chord);
    }

    private void Commit(HotkeyAction action, HotkeyChord chord)
    {
        string text = HotkeyText.Format(chord);
        // Reject a duplicate inside this page: two actions on one chord would make one of them dead
        // after RegisterHotKey returns false.
        foreach (var (other, _, _) in Rows)
        {
            if (other == action) continue;
            if (HotkeyText.TryParse(TextFor(other), out var bound) && bound.VirtualKey == chord.VirtualKey &&
                bound.Modifiers == chord.Modifiers)
            {
                txtStatus.Text = $"「{HotkeyText.Format(bound)}」已经给别的动作用了。";
                CancelCapture();
                return;
            }
        }

        SetText(action, text);
        _recording = null;
        Refresh();
        var failed = _onApply();
        if (failed.Contains(action))
        {
            txtStatus.Text = $"「{text}」没能注册：可能被别的程序占用（1409），换一个组合键试试。";
        }
        else
        {
            txtStatus.Text = $"「{text}」已生效（全局）。";
        }
    }

    private void ResetOne(HotkeyAction action)
    {
        var d = DefaultFor(action);
        SetText(action, d);
        _recording = null;
        Refresh();
        _onApply();
        txtStatus.Text = $"「{d}」已恢复默认。";
    }

    private void OnResetAll(object sender, RoutedEventArgs e)
    {
        foreach (var row in Rows) SetText(row.Action, row.Default);
        _recording = null;
        Refresh();
        var failed = _onApply();
        txtStatus.Text = failed.Count == 0
            ? "已恢复默认快捷键。"
            : $"已恢复默认，但有 {failed.Count} 个被别的程序占用。";
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    // ---- QA hooks (Program.cs --hotkey-capture-qa) ------------------------------------------
    /// <summary>Starts recording the way a mouse click does, so a synthetic key event can be routed
    /// through the real handler.</summary>
    internal void DebugBeginCapture(HotkeyAction action) => BeginCapture(action);

    internal bool DebugIsRecording => _recording != null;

    internal string DebugStatus => txtStatus.Text;

    internal string DebugLabel(HotkeyAction action) => _labels[action].Text;
}
