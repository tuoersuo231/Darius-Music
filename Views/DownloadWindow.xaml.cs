using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using CurrentLyrics.Services;

namespace CurrentLyrics.Views;

/// <summary>
/// 下载与存储: where 下载到本地 puts files, at which quality, and whether a finished download
/// joins 本地音乐. Its own window — it has nothing to do with the desktop lyrics, and the lyric
/// settings window was the wrong place for it (user asked for that separation).
/// </summary>
public partial class DownloadWindow : Window
{
    private readonly AppSettings _settings;
    private readonly Action<AppSettings> _onApply;
    private bool _initializing = true;

    public DownloadWindow(AppSettings settings, Action<AppSettings> onApply)
    {
        InitializeComponent();
        _settings = settings;
        _onApply = onApply;

        foreach (var (value, label) in NeteaseApi.QualityChoices)
            cmbQuality.Items.Add(new ComboBoxItem { Content = label, Tag = value });

        Refresh();
        _initializing = false;
        Loaded += (_, _) => RefreshUsage();
    }

    /// <summary>Paints every field from the settings; called after each change too, so the
    /// "默认 / 自定义" state and the folder path never disagree with what was just saved.</summary>
    private void Refresh()
    {
        string chosen = (_settings.DownloadFolder ?? "").Trim();
        string folder = _settings.ResolvedDownloadFolder;
        txtFolder.Text = folder;
        txtFolderState.Text = chosen.Length == 0
            ? "默认目录（我的音乐\\Darius Music）"
            : "自定义目录";

        bool missing = !Directory.Exists(folder);
        if (missing) txtFolderState.Text += " · 目录不存在";

        string quality = _settings.NeteaseQuality ?? "exhigh";
        foreach (var item in cmbQuality.Items.OfType<ComboBoxItem>())
            if (string.Equals(item.Tag as string, quality, StringComparison.OrdinalIgnoreCase))
                cmbQuality.SelectedItem = item;

        chkAddLibrary.IsChecked = _settings.AddDownloadedToLibrary;

        bool isDefault = string.Equals(folder.TrimEnd('\\'),
            AppSettings.DefaultDownloadFolder.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
        btnResetFolder.IsEnabled = !isDefault;
        btnResetFolder.Opacity = isDefault ? 0.45 : 1;
    }

    /// <summary>Counts what is already sitting in the download folder, so the folder choice has a
    /// visible result instead of being an invisible string.</summary>
    private void RefreshUsage()
    {
        string folder = _settings.ResolvedDownloadFolder;
        if (!Directory.Exists(folder))
        {
            txtUsage.Text = "还没有文件";
            return;
        }

        try
        {
            long bytes = 0;
            int songs = 0;
            int sidecars = 0;
            foreach (var file in Directory.EnumerateFiles(folder, "*.*", SearchOption.TopDirectoryOnly))
            {
                long size = 0;
                try { size = new FileInfo(file).Length; } catch { }
                bytes += size;
                if (TagReader.IsAudio(file)) songs++;
                else if (file.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
                         file.EndsWith(".lrc", StringComparison.OrdinalIgnoreCase)) sidecars++;
            }
            txtUsage.Text = songs == 0
                ? "还没有文件"
                : $"已下载 {songs} 首，共 {SizeText(bytes)}（含 {sidecars} 个封面/歌词）";
        }
        catch (Exception e)
        {
            txtUsage.Text = "读不到目录内容";
            ToolTip = e.Message;
        }
    }

    private static string SizeText(long bytes)
    {
        if (bytes >= 1L << 30) return $"{bytes / 1024.0 / 1024.0 / 1024.0:0.00} GB";
        if (bytes >= 1L << 20) return $"{bytes / 1024.0 / 1024.0:0.0} MB";
        return $"{bytes / 1024.0:0} KB";
    }

    private void Apply(Action<AppSettings> change)
    {
        change(_settings);
        _onApply(_settings);
        Refresh();
        RefreshUsage();
    }

    private void OnPickFolder(object sender, RoutedEventArgs e)
    {
        string current = _settings.ResolvedDownloadFolder;
        var picked = FolderPicker.Pick("选择在线歌曲的下载目录", current);
        if (string.IsNullOrWhiteSpace(picked)) return;
        Apply(s => s.DownloadFolder = picked!);
    }

    private void OnOpenFolder(object sender, RoutedEventArgs e)
    {
        string folder = _settings.ResolvedDownloadFolder;
        try
        {
            Directory.CreateDirectory(folder);
            Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "打不开目录：\n" + folder + "\n\n" + ex.Message, "下载与存储",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnResetFolder(object sender, RoutedEventArgs e)
    {
        Apply(s => s.DownloadFolder = "");
    }

    private void OnQualityChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing) return;
        if (cmbQuality.SelectedItem is not ComboBoxItem { Tag: string quality }) return;
        _settings.NeteaseQuality = quality;
        _onApply(_settings);
    }

    private void OnAddLibraryChanged(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        _settings.AddDownloadedToLibrary = chkAddLibrary.IsChecked == true;
        _onApply(_settings);
    }

    private void OnDone(object sender, RoutedEventArgs e) => Close();
}
