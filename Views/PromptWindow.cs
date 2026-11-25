using System.Windows;
using System.Windows.Controls;

namespace CurrentLyrics.Views;

/// <summary>Tiny modal prompt (WPF has no InputBox) used for playlist names and renames.</summary>
public sealed class PromptWindow : Window
{
    private readonly TextBox _box;

    public string Value => _box.Text.Trim();

    public PromptWindow(string title, string initial, string hint = "")
    {
        Title = title;
        Width = 360;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Background = System.Windows.Media.Brushes.Transparent;
        AllowsTransparency = true;
        WindowStyle = WindowStyle.None;

        var panel = new StackPanel { Margin = new Thickness(22, 20, 22, 18) };
        var card = new Border
        {
            CornerRadius = new CornerRadius(14),
            Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x16, 0x1C, 0x27)),
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x26, 0x2F, 0x3E)),
            BorderThickness = new Thickness(1),
            Child = panel,
        };
        Content = card;

        panel.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 14,
            Foreground = System.Windows.Media.Brushes.White,
            Margin = new Thickness(0, 0, 0, 10),
        });

        _box = new TextBox { Text = initial, FontSize = 13, Padding = new Thickness(8, 6, 8, 6) };
        panel.Children.Add(_box);

        if (!string.IsNullOrEmpty(hint))
        {
            panel.Children.Add(new TextBlock
            {
                Text = hint,
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 6, 0, 0),
                Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x7F, 0x8C, 0xA6)),
            });
        }

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 14, 0, 0),
        };
        var ok = new Button { Content = "确定", MinWidth = 76, Padding = new Thickness(10, 5, 10, 5), Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "取消", MinWidth = 76, Padding = new Thickness(10, 5, 10, 5) };
        ok.Click += (_, _) => { DialogResult = true; };
        cancel.Click += (_, _) => { DialogResult = false; };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        panel.Children.Add(buttons);

        Loaded += (_, _) => { _box.Focus(); _box.SelectAll(); };
        KeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Enter) DialogResult = true;
            else if (e.Key == System.Windows.Input.Key.Escape) DialogResult = false;
        };
    }
}
