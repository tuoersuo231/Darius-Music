using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CurrentLyrics.Services;

namespace CurrentLyrics.Views;

/// <summary>
/// 添加到歌单 picker. A Window rather than a ContextMenu submenu: the theme's MenuItem template had no
/// submenu popup at all, and a menu flooded with every playlist is unusable once the user has many.
/// One row per playlist; clicking a row adds straight away.
/// </summary>
public partial class PlaylistPickerWindow : Window
{
    /// <summary>The playlist the user picked, or null when they chose "create 默认歌单".</summary>
    public Playlist? Selected { get; private set; }

    /// <summary>True when the list was empty and the user accepted "默认歌单".</summary>
    public bool CreateDefault { get; private set; }

    public PlaylistPickerWindow(string songTitle, IEnumerable<Playlist> playlists)
    {
        InitializeComponent();
        txtSong.Text = songTitle;
        bool any = false;
        foreach (var playlist in playlists)
        {
            any = true;
            rowHost.Children.Add(Row(playlist.Name, playlist.CountText, () =>
            {
                Selected = playlist;
                Accept();
            }));
        }

        if (!any)
        {
            rowHost.Children.Add(Row("默认歌单", "0 首", () =>
            {
                CreateDefault = true;
                Accept();
            }));
        }
    }

    /// <summary>Modal close; a non-modal window (QA) has no DialogResult to set, so just close.</summary>
    private void Accept()
    {
        try
        {
            DialogResult = true;
        }
        catch (InvalidOperationException)
        {
            Close();
        }
    }

    private Button Row(string title, string caption, Action onClick)
    {
        var name = new TextBlock { Text = title, FontSize = 13 };
        var sub = new TextBlock
        {
            Text = caption,
            FontSize = 11,
            Margin = new Thickness(0, 3, 0, 0),
            Foreground = (Brush)FindResource("TextFaintBrush"),
        };
        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        stack.Children.Add(name);
        stack.Children.Add(sub);

        var button = new Button
        {
            Content = stack,
            Style = (Style)FindResource("GhostButton"),
            HorizontalContentAlignment = HorizontalAlignment.Left,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(0, 0, 0, 8),
            Padding = new Thickness(14, 10, 14, 10),
        };
        button.Click += (_, _) => onClick();
        return button;
    }

    /// <summary>QA: the row titles, in order.</summary>
    internal IReadOnlyList<string> DebugRows() => rowHost.Children.OfType<Button>()
        .Select(b => (b.Content as StackPanel)?.Children.OfType<TextBlock>().FirstOrDefault()?.Text ?? "?")
        .ToList();

    /// <summary>QA: click a row the way a user would.</summary>
    internal void DebugPick(int index)
    {
        if (rowHost.Children.Count == 0) return;
        index = Math.Clamp(index, 0, rowHost.Children.Count - 1);
        if (rowHost.Children[index] is Button button)
            button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        try { DialogResult = false; }
        catch (InvalidOperationException) { Close(); }
    }
}
