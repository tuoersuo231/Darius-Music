using System.Diagnostics;
using System.IO;

namespace CurrentLyrics.Services;

/// <summary>
/// 以管理员权限重开自己。
///
/// 为什么需要：游戏（尤其是带反作弊的）几乎都以管理员权限运行。Windows 的 UIPI 规定：低完整性级别
/// 的进程在「高完整性级别的窗口位于前台」时，既收不到 RegisterHotKey 投递的 WM_HOTKEY，低级键盘钩子
/// 也不会被调用。所以症状就是「普通应用里能按、游戏里完全按不动」—— 这不是优先级问题，是权限问题。
/// AutoHotkey 一类工具的官方说法也是「目标程序跑在管理员权限下，工具也必须以管理员权限运行」。
///
/// 这里只做一件事：用安装目录里的启动器，带上 runas 重开一次。提权意味着每次启动弹一次 UAC，
/// 所以不做开机自启，也不要默认开启。
/// </summary>
public static class Elevation
{
    /// <summary>当前进程是否已经是管理员权限。</summary>
    public static bool IsElevated => NativeMethods.IsProcessElevated();

    /// <summary>安装目录下的启动器（E:\Darius Music\Darius Music.exe）。开发目录里跑时是 null。</summary>
    public static string? LauncherPath
    {
        get
        {
            try
            {
                var exe = Environment.ProcessPath;
                if (string.IsNullOrEmpty(exe)) return null;
                var dir = Path.GetDirectoryName(exe);
                if (string.IsNullOrEmpty(dir)) return null;

                // 版本文件在 versions\vX.Y.Z\Darius Music.exe，启动器在两层之上。
                var versionRoot = Path.GetDirectoryName(dir);
                if (string.Equals(Path.GetFileName(versionRoot), "versions", StringComparison.OrdinalIgnoreCase))
                {
                    var root = Path.GetDirectoryName(versionRoot!);
                    if (!string.IsNullOrEmpty(root))
                    {
                        var launcher = Path.Combine(root, "Darius Music.exe");
                        if (File.Exists(launcher)) return launcher;
                    }
                }

                var local = Path.Combine(dir, "Darius Music.exe");
                if (File.Exists(local) && !string.Equals(local, exe, StringComparison.OrdinalIgnoreCase)) return local;
                return null;
            }
            catch
            {
                return null;
            }
        }
    }

    /// <summary>
    /// 用启动器以管理员权限重开。成功返回 true（调用方应当立刻退出本进程），失败返回 false 并给出
    /// 一句可以直接显示的原因（例如用户在 UAC 对话框上点了「否」）。
    /// </summary>
    public static bool TryRestartElevated(out string error)
    {
        error = "";
        var launcher = LauncherPath;
        if (launcher == null)
        {
            error = "这里是开发目录，没有启动器可以提权重开。";
            return false;
        }
        try
        {
            var info = new ProcessStartInfo(launcher)
            {
                UseShellExecute = true,     // runas 只能走 ShellExecute
                Verb = "runas",
                WorkingDirectory = Path.GetDirectoryName(launcher) ?? "",
            };
            if (Process.Start(info) == null)
            {
                error = "没能启动管理员进程。";
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            // 1223 = ERROR_CANCELLED：用户点了 UAC 的「否」。
            error = ex is System.ComponentModel.Win32Exception { NativeErrorCode: 1223 }
                ? "取消了管理员权限，游戏里仍然收不到快捷键。"
                : "以管理员权限启动失败：" + ex.Message;
            return false;
        }
    }
}
