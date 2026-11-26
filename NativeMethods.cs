using System.Runtime.InteropServices;

namespace CurrentLyrics;

internal static class NativeMethods
{
    public const int GWL_EXSTYLE = -20;
    public const int WS_EX_TRANSPARENT = 0x00000020;
    public const int WS_EX_TOOLWINDOW = 0x00000080;
    public const int WS_EX_NOACTIVATE = 0x08000000;

    [DllImport("user32.dll", SetLastError = true)]
    public static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    // ---- global hotkeys: RegisterHotKey posts WM_HOTKEY to the window's message loop ----
    public const int WM_HOTKEY = 0x0312;
    public const int MOD_ALT = 0x0001;
    public const int MOD_CONTROL = 0x0002;
    public const int MOD_SHIFT = 0x0004;
    public const int MOD_WIN = 0x0008;
    /// <summary>Suppresses auto-repeat; only used together with a modifier.</summary>
    public const int MOD_NOREPEAT = 0x4000;

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool RegisterHotKey(IntPtr hWnd, int id, int fsModifiers, int vk);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    // ---- low-level keyboard hook: sees a key BEFORE the foreground app ----
    // RegisterHotKey only wins when nothing else eats the key first; fullscreen games read the keyboard
    // directly and never let the WM_HOTKEY path fire, which is the user's「不在前台时快捷键优先级很低」.
    // A WH_KEYBOARD_LL hook is called from the raw-input thread before the key reaches any window.
    public const int WH_KEYBOARD_LL = 13;
    public const int WM_KEYDOWN = 0x0100;
    public const int WM_SYSKEYDOWN = 0x0104;
    public const int VK_SHIFT = 0x10;
    public const int VK_CONTROL = 0x11;
    public const int VK_MENU = 0x12;
    public const int VK_LWIN = 0x5B;
    public const int VK_RWIN = 0x5C;

    [StructLayout(LayoutKind.Sequential)]
    public struct KbdLlHookStruct
    {
        public uint VirtualKey;
        public uint ScanCode;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    public delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc proc, IntPtr hMod, uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    public static extern IntPtr CallNextHookEx(IntPtr hook, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern short GetAsyncKeyState(int vk);

    // ---- a private message loop for the hook thread (the hook callback only runs on a thread that
    // is inside GetMessage/PeekMessage; the UI thread is busy painting lyrics, so it gets its own) ----
    [StructLayout(LayoutKind.Sequential)]
    public struct Msg
    {
        public IntPtr Hwnd;
        public int Message;
        public IntPtr WParam;
        public IntPtr LParam;
        public int Time;
        public int X;
        public int Y;
    }

    public const int WM_QUIT = 0x0012;

    [DllImport("user32.dll")]
    public static extern sbyte GetMessage(out Msg msg, IntPtr hWnd, uint min, uint max);

    /// <summary>PM_NOREMOVE (0): forces this thread's message queue into existence without consuming
    /// anything. SetWindowsHookEx with a thread-local hook silently does nothing while the installing
    /// thread has no queue yet — that is why the hook reported success but never got called.</summary>
    [DllImport("user32.dll")]
    public static extern bool PeekMessage(out Msg msg, IntPtr hWnd, uint min, uint max, uint removeMsg);

    [DllImport("user32.dll")]
    public static extern bool TranslateMessage(ref Msg msg);

    [DllImport("user32.dll")]
    public static extern IntPtr DispatchMessage(ref Msg msg);

    [DllImport("user32.dll")]
    public static extern bool PostThreadMessage(uint threadId, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    public static extern uint GetCurrentThreadId();

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    // ---- synthetic keystrokes (QA: prove a hotkey really fires through the OS, not just by message) ----
    public const uint KEYEVENTF_KEYUP = 0x0002;

    [DllImport("user32.dll")]
    public static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll")]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    public static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("gdi32.dll")]
    private static extern int GetDeviceCaps(IntPtr hdc, int nIndex);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hdc);

    /// <summary>Primary display refresh rate in Hz, or 60 when it cannot be read. WPF samples every
    /// animation at 60 fps unless told otherwise, which is why motion looked like 60 Hz on a 179 Hz
    /// panel even once the frame rate was high enough for more.</summary>
    public static int DisplayRefreshHz()
    {
        try
        {
            IntPtr dc = GetDC(IntPtr.Zero);
            if (dc == IntPtr.Zero) return 60;
            int hz = GetDeviceCaps(dc, 116);   // VREFRESH
            ReleaseDC(IntPtr.Zero, dc);
            return hz is >= 30 and <= 480 ? hz : 60;
        }
        catch
        {
            return 60;
        }
    }

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    public static extern int AddFontResourceEx(string lpszFilename, uint fl, IntPtr pdv);

    public const uint FR_PRIVATE = 0x10;

    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOZORDER = 0x0004;
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_FRAMECHANGED = 0x0020;
    public const uint SWP_NOOWNERZORDER = 0x0200;

    /// <summary>"Top of the topmost band" for SetWindowPos.</summary>
    public static readonly IntPtr HWND_TOPMOST = new(-1);

    /// <summary>Bottom of the whole z-order: because HWND_NOTOPMOST alone only puts a window at the TOP
    /// of the non-topmost band (still over every normal app), switching 显示在其他应用上层 off has to
    /// sink explicitly, or nothing visibly changes until Windows re-stacks the screen.</summary>
    public static readonly IntPtr HWND_BOTTOM = new(1);

    /// <summary>
    /// Another window became the foreground one. This is the only reliable "some other app just came to
    /// the front" signal: among topmost windows the one activated last wins, so a plain topmost overlay
    /// still ends up behind a topmost game / player / Task Manager.
    /// </summary>
    public const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    public const uint WINEVENT_OUTOFCONTEXT = 0x0000;

    public delegate void WinEventDelegate(IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

    [DllImport("user32.dll")]
    public static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc,
        WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

    [DllImport("user32.dll")]
    public static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    public const uint GW_HWNDNEXT = 2;

    [DllImport("user32.dll")]
    public static extern IntPtr GetTopWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern IntPtr GetWindow(IntPtr hWnd, uint cmd);

    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    /// <summary>QA only: find a window of another process by its title (--above-qa drives a rival
    /// topmost window that way).</summary>
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr FindWindow(string? lpClassName, string lpWindowName);

    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    /// <summary>Top-level windows in Z order, top first — the canonical way to compare two windows.</summary>
    [DllImport("user32.dll")]
    public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder text, int count);

    // ---- 窗口背景透明（背景色选「透明」时让桌面/下层应用透过来） ----
    [StructLayout(LayoutKind.Sequential)]
    public struct AccentPolicy
    {
        public int AccentState;
        public int AccentFlags;
        public int GradientColor;   // AABBGGRR
        public int AnimationId;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct WindowCompositionAttributeData
    {
        public int Attribute;
        public IntPtr Data;
        public int SizeOfData;
    }

    public const int WcaAccentPolicy = 19;
    public const int AccentDisabled = 0;
    public const int AccentEnableBlurBehind = 3;
    public const int AccentEnableAcrylicBlurBehind = 4;

    [DllImport("user32.dll")]
    public static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);

    [StructLayout(LayoutKind.Sequential)]
    public struct Margins
    {
        public int Left;
        public int Right;
        public int Top;
        public int Bottom;
    }

    /// <summary>Turns the whole client area into "glass" so the accent blur has an alpha surface to
    /// composite; without it DWM treats a normal WPF client as opaque and the accent shows nothing.</summary>
    [DllImport("dwmapi.dll")]
    public static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref Margins margins);

    // ---- Raw Input 后台接收：第三条快捷键通路 ----
    // RegisterHotKey 只有在「前台窗口没把按键吃掉」时才投递 WM_HOTKEY，全屏游戏直接用 Raw Input
    // 读键盘，那条路就断了。RIDEV_INPUTSINK 是系统级的「后台也把键盘事件投给我」注册：窗口不在前台、
    // 甚至隐藏着，WM_INPUT 照样送到这里。它和低级钩子是两套独立机制，一套被系统丢掉时另一套还在。
    public const int WM_INPUT = 0x00FF;
    public const uint RID_INPUT = 0x10000003;
    public const uint RIM_TYPEKEYBOARD = 1;
    public const uint RIDEV_INPUTSINK = 0x0100;
    public const ushort HID_USAGE_PAGE_GENERIC = 0x01;
    public const ushort HID_USAGE_GENERIC_KEYBOARD = 0x06;

    [StructLayout(LayoutKind.Sequential)]
    public struct RawInputDevice
    {
        public ushort UsagePage;
        public ushort Usage;
        public uint Flags;
        public IntPtr Target;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RawInputHeader
    {
        public uint Type;
        public uint Size;
        public IntPtr Device;
        public IntPtr WParam;
    }

    /// <summary>RAWKEYBOARD: MakeCode / Flags / Reserved / VKey / Message / ExtraInformation.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct RawKeyboard
    {
        public ushort MakeCode;
        public ushort Flags;
        public ushort Reserved;
        public ushort VirtualKey;
        public uint Message;
        public uint ExtraInformation;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RawInput
    {
        public RawInputHeader Header;
        public RawKeyboard Keyboard;
    }

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool RegisterRawInputDevices(RawInputDevice[] devices, uint count, uint size);

    /// <summary>dwSize on the way in is sizeof(RAWINPUTHEADER). Retur: bytes copied (0 = nothing to
    /// read, 0xFFFFFFFF = error). Call once with a null buffer to learn the real size.</summary>
    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint GetRawInputData(IntPtr rawInput, uint command, IntPtr buffer, ref uint size, uint headerSize);

    // ---- 提权检测（UIPI：低权限进程在前台是高权限窗口时既收不到热键，钩子也不会被调用）----
    private const uint TOKEN_QUERY = 0x0008;
    private const int TokenElevation = 20;

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(IntPtr token, int infoClass, out int info, int length, out int returned);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    /// <summary>True when this process already has an elevated (administrator) token. Version
    /// manifests cannot upgrade the token at runtime, so this is either "started elevated" or false.</summary>
    public static bool IsProcessElevated()
    {
        IntPtr token = IntPtr.Zero;
        try
        {
            if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, out token)) return false;
            if (!GetTokenInformation(token, TokenElevation, out int elevated, sizeof(int), out _)) return false;
            return elevated != 0;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (token != IntPtr.Zero) CloseHandle(token);
        }
    }
}
