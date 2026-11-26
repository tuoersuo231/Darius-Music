using System.Windows;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace CurrentLyrics.Views;

/// <summary>
/// 托盘图标（右下角通知区）。
///
/// Why it exists: 「关闭窗口时最小化」只把窗口缩进任务栏时，用户关掉窗口会以为程序已经退出，
/// 于是又启动一次 —— 结果是两个进程各自注册全局热键，先启动的那个独占 F6/F7/F8，后启动的
/// 全部 1409，看起来就是「快捷键只在窗口在前台时好使」。有了托盘图标，关窗口之后程序在哪、
/// 怎么退出都看得见，也不会再"以为关了又开一个"。
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private bool _balloonShown;

    public TrayIcon(Action show, Action exit)
    {
        var menu = new Forms.ContextMenuStrip { ShowImageMargin = false };
        menu.Items.Add("打开主界面", null, (_, _) => show());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("退出 Darius Music", null, (_, _) => exit());

        _icon = new Forms.NotifyIcon
        {
            Text = "Darius Music",
            Icon = LoadIcon(),
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.DoubleClick += (_, _) => show();
    }

    /// <summary>One hint per session, on the first close-to-tray, so the icon is not a surprise.</summary>
    public void ShowFirstHint()
    {
        if (_balloonShown) return;
        _balloonShown = true;
        try
        {
            _icon.ShowBalloonTip(2500, "Darius Music", "已缩到托盘，音乐继续播放；右键图标可以退出。",
                Forms.ToolTipIcon.Info);
        }
        catch { }
    }

    private static Drawing.Icon LoadIcon()
    {
        try
        {
            var path = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
            if (System.IO.File.Exists(path)) return new Drawing.Icon(path);
        }
        catch { }
        try
        {
            var exe = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exe)) return Drawing.Icon.ExtractAssociatedIcon(exe) ?? Drawing.SystemIcons.Application;
        }
        catch { }
        return Drawing.SystemIcons.Application;
    }

    public void Dispose()
    {
        try
        {
            _icon.Visible = false;
            _icon.Dispose();
        }
        catch { }
    }
}
