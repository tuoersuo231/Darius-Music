using System.Diagnostics;
using System.Runtime.InteropServices;

namespace CurrentLyrics.Services;

/// <summary>
/// One player per desktop. Two instances look identical and both try to own the same global hotkeys —
/// only the first one gets them, the second reports Error 1409 for every chord, and which window reacts
/// depends on which one you happened to start last. That is exactly「快捷键优先级很低」when both are up.
/// </summary>
public static class SingleInstance
{
    private const string MutexName = @"Local\DariusMusic.SingleInstance.v1";
    private static Mutex? _mutex;
    private static bool _owned;

    /// <summary>True when this process is the first one. Keep the mutex for the process lifetime.</summary>
    public static bool TryAcquire()
    {
        try
        {
            _mutex = new Mutex(true, MutexName, out _owned);
            return _owned;
        }
        catch
        {
            return true;    // a broken mutex must never stop the app from starting
        }
    }

    /// <summary>Give the name up before starting a replacement process (elevation restart), otherwise the
    /// new instance sees the mutex still owned and thinks it is the second copy.</summary>
    public static void Release()
    {
        try
        {
            if (_owned) _mutex?.ReleaseMutex();
        }
        catch { }
        try { _mutex?.Dispose(); } catch { }
        _mutex = null;
        _owned = false;
    }

    /// <summary>Bring the window of the running instance to the front (what the launcher does twice
    /// when you double-click it).</summary>
    public static void ActivateExisting()
    {
        try
        {
            using var current = Process.GetCurrentProcess();
            foreach (var other in Process.GetProcessesByName(current.ProcessName))
            {
                if (other.Id == current.Id) continue;
                var handle = other.MainWindowHandle;
                if (handle == IntPtr.Zero) continue;
                ShowWindow(handle, SW_RESTORE);
                SetForegroundWindow(handle);
                return;
            }
        }
        catch { }
    }

    private const int SW_RESTORE = 9;

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int cmd);
}
