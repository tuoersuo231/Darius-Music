using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace CurrentLyrics.Services;

/// <summary>The five user-assignable actions. Kept as an enum so a binding is one small record and the
/// settings file stays readable ("KeyPlayPause": "Ctrl+P").</summary>
public enum HotkeyAction
{
    AboveApps,
    ToggleLyrics,
    PlayPause,
    Next,
    Previous,
    /// <summary>背景色在「实色 / 全透明」之间切换。没有默认按键（用户自己设）。</summary>
    TransparentBg,
}

/// <summary>
/// One chord: at most ONE modifier plus ONE key ("Ctrl+Q", "F8").
///
/// 用户的规则（1.2.60 起）：所有快捷键必须由修饰键当前缀，组合键锁定在两个键；F1~F12 例外，可以
/// 单开、不需要修饰键。所以「P」这种裸字母不再是合法绑定 —— 它以前只在窗口有焦点时才触发，用户按
/// 播放/暂停自然「没反应」。
/// </summary>
public readonly record struct HotkeyChord(int VirtualKey, int Modifiers)
{
    public bool IsEmpty => VirtualKey == 0;

    /// <summary>F1..F12 — the one family of keys allowed without a modifier.</summary>
    public bool IsBareFunctionKey => VirtualKey >= 0x70 && VirtualKey <= 0x7B;

    /// <summary>Every chord that passes the rule is registered system-wide (it has a modifier, or it
    /// is a function key), so nothing is left to the window's own key handler any more.</summary>
    public bool IsGlobal => !IsEmpty && HotkeyText.IsAllowed(this, out _);

    public string Text => HotkeyText.Format(this);
}

/// <summary>Parse / format / validate "Ctrl+Q"-style text, and turn a WPF key event into a chord.</summary>
public static class HotkeyText
{
    /// <summary>Parses "Ctrl+Q", "ctrl + q", "F8", "Alt+F4". Returns false for anything that is not
    /// one modifier at most plus one key.</summary>
    public static bool TryParse(string? text, out HotkeyChord chord)
    {
        chord = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var parts = text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || parts.Length > 2) return false;   // 两位上限
        int mods = 0;
        int vk = 0;
        foreach (var part in parts)
        {
            if (TryModifier(part, out int mod))
            {
                if (mods != 0 || vk != 0) return false;   // 两个修饰键 = 三位以上，拒绝
                mods = mod;
                continue;
            }
            if (vk != 0) return false;                    // 两个普通键也拒绝
            if (!TryKey(part, out vk)) return false;
        }
        if (vk == 0) return false;
        chord = new HotkeyChord(vk, mods);
        return true;
    }

    /// <summary>
    /// The binding rule, in one place so the settings page, the settings file and the registrar can
    /// never disagree. <paramref name="reason"/> is shown verbatim to the user.
    /// </summary>
    public static bool IsAllowed(in HotkeyChord chord, out string reason)
    {
        reason = "";
        if (chord.IsEmpty)
        {
            reason = "这里还没有按键。";
            return false;
        }
        if (IsModifierVirtualKey(chord.VirtualKey))
        {
            reason = "只按修饰键不算：按住 Ctrl / Alt / Shift / Win 再按一个字母，或者单按 F1~F12。";
            return false;
        }
        int mods = ModifierCount(chord.Modifiers);
        if (mods == 0)
        {
            if (chord.IsBareFunctionKey) return true;
            reason = "必须带修饰键（Ctrl / Alt / Shift / Win），例如 Ctrl+P；只有 F1~F12 可以不搭配饰键。";
            return false;
        }
        if (mods > 1)
        {
            reason = "最多一个修饰键加一个按键（一共两个键）。";
            return false;
        }
        return true;
    }

    /// <summary>Settings-file hygiene: canonical text for a legal chord, otherwise the default.
    /// Turns a legacy "P" (window-local, effectively dead) into "Ctrl+P" on the next load/save.</summary>
    public static string NormalizeOrDefault(string? text, string fallback)
    {
        if (TryParse(text, out var chord) && IsAllowed(chord, out _)) return Format(chord);
        if (TryParse(fallback, out var def) && IsAllowed(def, out _)) return Format(def);
        return fallback;
    }

    public static string Format(HotkeyChord chord)
    {
        if (chord.IsEmpty) return "";
        var key = KeyName(chord.VirtualKey);
        return chord.Modifiers == 0 ? key : ModifierName(chord.Modifiers) + "+" + key;
    }

    /// <summary>
    /// The key an event really carries. With a Chinese IME switched on, WPF reports a letter key as
    /// <see cref="Key.ImeProcessed"/> and puts the real key in <see cref="KeyEventArgs.ImeProcessedKey"/>;
    /// Alt-combinations arrive as <see cref="Key.System"/> with <see cref="KeyEventArgs.SystemKey"/>.
    /// Reading <c>e.Key</c> alone is why the settings page refused "Ctrl+P" on this machine and only
    /// said「最多两个键」.
    /// </summary>
    public static Key NormalizeKey(KeyEventArgs e) => ResolveKey(e.Key, e.SystemKey, e.ImeProcessedKey);

    /// <summary>The pure part of <see cref="NormalizeKey"/>, so QA can prove the IME path without a
    /// real input method (WPF does not let a synthetic KeyEventArgs carry ImeProcessedKey).</summary>
    public static Key ResolveKey(Key key, Key systemKey, Key imeProcessedKey) => key switch
    {
        Key.System => systemKey,
        Key.ImeProcessed => imeProcessedKey,
        var k => k,
    };

    /// <summary>Captures a real key event (IME/Alt normalised) and applies the rule.</summary>
    public static bool TryCapture(KeyEventArgs e, out HotkeyChord chord, out string reason)
    {
        if (e == null)
        {
            chord = default;
            reason = "没有收到按键。";
            return false;
        }
        return TryCaptureCore(NormalizeKey(e), ModifierBits(e.KeyboardDevice.Modifiers), out chord, out reason);
    }

    /// <summary>Same rule, for callers that already have a key and a modifier state.</summary>
    public static bool TryCapture(Key key, ModifierKeys modifiers, out HotkeyChord chord) =>
        TryCaptureCore(key, ModifierBits(modifiers), out chord, out _);

    /// <summary>Same rule, keeping the explanation (QA / settings page).</summary>
    public static bool TryCapture(Key key, ModifierKeys modifiers, out HotkeyChord chord, out string reason) =>
        TryCaptureCore(key, ModifierBits(modifiers), out chord, out reason);

    private static bool TryCaptureCore(Key key, int mods, out HotkeyChord chord, out string reason)
    {
        chord = default;
        reason = "";
        if (key is Key.None)
        {
            reason = "没有收到按键。";
            return false;
        }
        if (IsModifierKey(key))
        {
            reason = "只按修饰键不算：按住 Ctrl / Alt / Shift / Win 再按一个字母，或者单按 F1~F12。";
            return false;
        }
        int vk = KeyInterop.VirtualKeyFromKey(key);
        if (vk == 0)
        {
            reason = "这个按键识别不了，换一个试试。";
            return false;
        }
        var candidate = new HotkeyChord(vk, mods);
        if (!IsAllowed(candidate, out reason)) return false;
        chord = candidate;
        return true;
    }

    /// <summary>Human name with the same wording the picker shows.</summary>
    public static string KeyName(int vk) => KeyInterop.KeyFromVirtualKey(vk) switch
    {
        Key.None => "-",
        var k when k >= Key.D0 && k <= Key.D9 => ((char)('0' + (k - Key.D0))).ToString(),
        var k when k >= Key.NumPad0 && k <= Key.NumPad9 => "Num" + (k - Key.NumPad0),
        var k => k.ToString(),
    };

    public static string ModifierName(int mods)
    {
        var names = new List<string>(3);
        if ((mods & NativeMethods.MOD_CONTROL) != 0) names.Add("Ctrl");
        if ((mods & NativeMethods.MOD_ALT) != 0) names.Add("Alt");
        if ((mods & NativeMethods.MOD_SHIFT) != 0) names.Add("Shift");
        if ((mods & NativeMethods.MOD_WIN) != 0) names.Add("Win");
        return string.Join("+", names);
    }

    public static int ModifierCount(int mods)
    {
        int mask = NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT | NativeMethods.MOD_SHIFT | NativeMethods.MOD_WIN;
        int n = 0;
        for (int bits = mods & mask; bits != 0; bits &= bits - 1) n++;
        return n;
    }

    public static int ModifierBits(ModifierKeys modifiers)
    {
        int mods = 0;
        if (modifiers.HasFlag(ModifierKeys.Control)) mods |= NativeMethods.MOD_CONTROL;
        if (modifiers.HasFlag(ModifierKeys.Alt)) mods |= NativeMethods.MOD_ALT;
        if (modifiers.HasFlag(ModifierKeys.Shift)) mods |= NativeMethods.MOD_SHIFT;
        if (modifiers.HasFlag(ModifierKeys.Windows)) mods |= NativeMethods.MOD_WIN;
        return mods;
    }

    public static bool IsModifierKey(Key key) =>
        key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift
            or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin;

    public static bool IsModifierVirtualKey(int vk) =>
        vk is 0x10 or 0x11 or 0x12 or 0x5B or 0x5C or (>= 0xA0 and <= 0xA5);

    private static bool TryModifier(string part, out int mod)
    {
        mod = 0;
        switch (part.ToLowerInvariant())
        {
            case "ctrl" or "control": mod = NativeMethods.MOD_CONTROL; return true;
            case "alt": mod = NativeMethods.MOD_ALT; return true;
            case "shift": mod = NativeMethods.MOD_SHIFT; return true;
            case "win" or "windows" or "meta": mod = NativeMethods.MOD_WIN; return true;
            default: return false;
        }
    }

    private static bool TryKey(string part, out int vk)
    {
        vk = 0;
        if (part.Length == 1)
        {
            char c = char.ToUpperInvariant(part[0]);
            if (c >= 'A' && c <= 'Z') { vk = c; return true; }
            if (c >= '0' && c <= '9') { vk = c; return true; }
        }
        if (part.StartsWith("Num", StringComparison.OrdinalIgnoreCase) && part.Length == 4 &&
            char.IsAsciiDigit(part[3]))
        {
            vk = 0x60 + (part[3] - '0');
            return true;
        }
        if (part.StartsWith('F') && int.TryParse(part[1..], out int fn) && fn >= 1 && fn <= 24)
        {
            vk = 0x70 + (fn - 1);
            return true;
        }
        // Named keys the WPF enum knows (Space, Enter, Up, MediaPlayPause…).
        if (Enum.TryParse<Key>(part, ignoreCase: true, out var key) && key != Key.None)
        {
            vk = KeyInterop.VirtualKeyFromKey(key);
            return vk != 0;
        }
        return false;
    }
}

/// <summary>Registers the chords the user picked as global hotkeys on the main window and raises
/// <see cref="Pressed"/>. Under the 1.2.60 rule every legal chord is global (modifier or F1~F12), so
/// there is no window-local fallback left: a binding that fails here is reported, never silently
/// downgraded to "only works while this window is focused".</summary>
public sealed class HotkeyManager : IDisposable
{
    private readonly Dictionary<int, HotkeyAction> _byId = new();     // RegisterHotKey id → action
    private HwndSource? _source;
    private bool _attached;

    // ---- WH_KEYBOARD_LL: the higher-priority path ----
    // RegisterHotKey is a *registered* hotkey: the system only posts WM_HOTKEY after the foreground
    // window has had its chance, and a fullscreen game reading the keyboard directly never lets it
    // through — that is「这个应用没有在前台时快捷键优先级很低」. A low-level hook is called from the
    // raw-input thread before any window sees the key, so it fires either way and can swallow the key
    // (the game below does not also react). RegisterHotKey stays as the fallback: if the hook is
    // removed (Windows drops a hook whose callback is too slow) the chords keep working.
    private readonly Dictionary<int, List<(HotkeyAction Action, int Mods)>> _hookChords = new();
    private NativeMethods.LowLevelKeyboardProc? _hookProc;
    private volatile IntPtr _hookHandle = IntPtr.Zero;
    private Thread? _hookThread;
    private uint _hookThreadId;
    private int _hookError;
    private int _hookHits;
    private int _hookCalls;
    private bool _hookEnabled = true;

    // ---- 通路 2：Raw Input sink（游戏在前台时 WM_HOTKEY 常常根本不来，WM_INPUT 会来）----
    private bool _rawRegistered;
    private int _rawError;
    private int _rawHits;
    private int _rawMessages;

    // ---- 通路 3：轮询 GetAsyncKeyState（钩子被系统丢掉、热键被前台吞掉时，这条还能兜住）----
    private (int Vk, HotkeyAction Action, int Mods)[] _pollBinds = Array.Empty<(int, HotkeyAction, int)>();
    private Thread? _pollThread;
    private volatile bool _pollRun;
    private int _pollHits;

    /// <summary>同一个动作 400 ms 内只执行一次：三条通路会同时看到同一次按键。</summary>
    private readonly Dictionary<HotkeyAction, long> _lastFire = new();

    /// <summary>False while the 快捷键 settings page is recording a chord, so the hook does not eat
    /// the very key the user is trying to capture.</summary>
    public bool HookEnabled
    {
        get => _hookEnabled;
        set => _hookEnabled = value;
    }

    public string HookState =>
        $"hook={(_hookHandle != IntPtr.Zero)} hookErr={_hookError} hookCalls={_hookCalls} hookHits={_hookHits} " +
        $"hookRetries={_hookRetries} enabled={_hookEnabled} " +
        $"raw={_rawRegistered} rawErr={_rawError} rawMsgs={_rawMessages} rawHits={_rawHits} " +
        $"poll={_pollThread != null} pollBinds={_pollBinds.Length} pollHits={_pollHits} " +
        $"elevated={NativeMethods.IsProcessElevated()}";

    /// <summary>Raised for every activation.</summary>
    public event Action<HotkeyAction>? Pressed;

    /// <summary>
    /// 「快捷键只在窗口前台时有效」这种问题，猜没用 —— 这里把三条通路各自有没有消息记进
    /// data\hotkey.log：注册结果、WM_HOTKEY 到没到、钩子有没有截到、动作执行没执行。
    /// 用户按完键把文件拿来一看就知道是「没注册」「注册了但收不到」「收到了没执行」。
    /// </summary>
    public static void Log(string text)
    {
        try
        {
            var path = System.IO.Path.Combine(Services.AppPaths.DataDir, "hotkey.log");
            var info = new System.IO.FileInfo(path);
            if (info.Exists && info.Length > 512 * 1024) info.Delete();
            System.IO.File.AppendAllText(path, $"{DateTime.Now:MM-dd HH:mm:ss.fff} {text}{Environment.NewLine}");
        }
        catch { }
    }

    /// <summary>Chords that were requested but could not be registered, so the settings page can say
    /// which one failed instead of pretending it works.</summary>
    public List<HotkeyAction> Failed { get; } = new();

    /// <summary>How many chords are actually held right now (diagnostics / --ui-shot).</summary>
    public int RegisteredCount => _byId.Count;

    public void Attach(Window window)
    {
        if (_attached && _source != null) return;
        // WindowInteropHelper, not PresentationSource.FromVisual: at SourceInitialized the HWND exists
        // but the window's visual root is not attached to it yet, so FromVisual can return null and
        // every RegisterHotKey silently fails (that is what "registered=0 failed=4" was).
        IntPtr handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;
        Handle = handle;
        var source = HwndSource.FromHwnd(handle);
        if (source == null) return;
        if (!ReferenceEquals(source, _source))
        {
            _source?.RemoveHook(WndProc);
            source.AddHook(WndProc);
            _source = source;
        }
        _attached = true;
        InstallHook();
        RegisterRawInput();
        StartPolling();
    }

    /// <summary>
    /// Installs the keyboard hook once, on its OWN thread with its own GetMessage loop. A low-level hook
    /// callback only runs while the installing thread sits inside GetMessage/PeekMessage; running it on
    /// the UI thread meant the callback never fired at all while the app was busy painting (measured:
    /// hook installed with error 0, hookCalls=0). The dedicated thread also keeps a slow UI frame from
    /// making Windows drop the hook for being late.
    /// DARIUS_NOHOOK=1 skips it (QA A/B).
    /// </summary>
    private void InstallHook()
    {
        if (_hookThread != null) return;
        if (Environment.GetEnvironmentVariable("DARIUS_NOHOOK") == "1") return;
        _hookProc ??= HookProc;              // keep the delegate alive for the hook's lifetime
        _hookThread = new Thread(() =>
        {
            _hookThreadId = NativeMethods.GetCurrentThreadId();
            // Create this thread's message queue BEFORE installing: SetWindowsHookEx returns a
            // handle but the callback is never invoked while the installing thread has no queue
            // (measured: hookErr=0, hookCalls=0, so the hook was silently dead).
            NativeMethods.PeekMessage(out _, IntPtr.Zero, 0, 0, 0);
            _hookHandle = NativeMethods.SetWindowsHookEx(NativeMethods.WH_KEYBOARD_LL, _hookProc, IntPtr.Zero, 0);
            _hookError = _hookHandle == IntPtr.Zero ? Marshal.GetLastWin32Error() : 0;
            Log($"hook installed handle={_hookHandle} err={_hookError}");
            if (_hookHandle == IntPtr.Zero) return;
            while (NativeMethods.GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                NativeMethods.TranslateMessage(ref msg);
                NativeMethods.DispatchMessage(ref msg);
            }
            if (_hookHandle != IntPtr.Zero) NativeMethods.UnhookWindowsHookEx(_hookHandle);
            _hookHandle = IntPtr.Zero;
        })
        {
            IsBackground = true,
            Name = "DariusHotkeyHook",
        };
        _hookThread.SetApartmentState(ApartmentState.STA);
        _hookThread.Start();
        // Give the thread a moment so Apply() knows whether the hook is up (it decides whether a
        // RegisterHotKey failure is fatal or covered by the hook).
        for (int i = 0; i < 400 && _hookHandle == IntPtr.Zero && _hookThread.IsAlive && _hookError == 0; i++)
            Thread.Sleep(1);
        StartHookWatchdog();
    }

    /// <summary>
    /// The hook can come up "installed, no callbacks" (measured on this machine: hookErr=0, hookCalls=0
    /// for a whole session). If nothing has reached the hook a couple of seconds after installing, tear
    /// the thread down and install once more — a fresh queue+hook has been seen to work where the first
    /// one stayed silent.
    /// </summary>
    private void StartHookWatchdog()
    {
        if (_hookHandle == IntPtr.Zero) return;
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null) return;
        var check = new System.Windows.Threading.DispatcherTimer(
            TimeSpan.FromSeconds(2.5), System.Windows.Threading.DispatcherPriority.Background, (_, _) =>
            {
                foreach (var timer in _hookTimers) timer.Stop();
                if (_hookHandle == IntPtr.Zero || _hookCalls > 0) return;
                _hookRetries++;
                if (_hookRetries > 2) return;
                TearDownHook();
                InstallHook();
            }, dispatcher);
        _hookTimers.Add(check);
        check.Start();
    }

    private readonly List<System.Windows.Threading.DispatcherTimer> _hookTimers = new();
    private int _hookRetries;

    private void TearDownHook()
    {
        if (_hookThreadId != 0)
            NativeMethods.PostThreadMessage(_hookThreadId, NativeMethods.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        if (_hookHandle != IntPtr.Zero)
        {
            NativeMethods.UnhookWindowsHookEx(_hookHandle);
            _hookHandle = IntPtr.Zero;
        }
        _hookThread = null;
        _hookThreadId = 0;
    }

    /// <summary>Runs on the raw-input thread: keep it tiny. Only an exact chord match is swallowed.</summary>
    private IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        _hookCalls++;
        if (nCode >= 0 && _hookEnabled)
        {
            int msg = wParam.ToInt32();
            if (msg == NativeMethods.WM_KEYDOWN || msg == NativeMethods.WM_SYSKEYDOWN)
            {
                var info = Marshal.PtrToStructure<NativeMethods.KbdLlHookStruct>(lParam);
                int vk = (int)info.VirtualKey;
                if (_hookChords.TryGetValue(vk, out var chords))
                {
                    int mods = CurrentModifiers();
                    for (int i = 0; i < chords.Count; i++)
                    {
                        if (chords[i].Mods != mods) continue;
                        var action = chords[i].Action;
                        _hookHits++;
                        Log($"HOOK vk={vk} mods={mods} action={action}");
                        Fire(action, "hook", vk, mods);
                        // Deliberately NOT swallowed: a global hook that eats keys is a support nightmare
                        // (one bad match and typing breaks in every app). The key also reaches the
                        // foreground app, and RegisterHotKey may fire too — WndProc drops that duplicate
                        // inside the 400 ms window.
                        break;
                    }
                }
            }
        }
        return NativeMethods.CallNextHookEx(_hookHandle, nCode, wParam, lParam);
    }

    private static int CurrentModifiers()
    {
        int mods = 0;
        if ((NativeMethods.GetAsyncKeyState(NativeMethods.VK_CONTROL) & 0x8000) != 0) mods |= NativeMethods.MOD_CONTROL;
        if ((NativeMethods.GetAsyncKeyState(NativeMethods.VK_MENU) & 0x8000) != 0) mods |= NativeMethods.MOD_ALT;
        if ((NativeMethods.GetAsyncKeyState(NativeMethods.VK_SHIFT) & 0x8000) != 0) mods |= NativeMethods.MOD_SHIFT;
        if ((NativeMethods.GetAsyncKeyState(NativeMethods.VK_LWIN) & 0x8000) != 0 ||
            (NativeMethods.GetAsyncKeyState(NativeMethods.VK_RWIN) & 0x8000) != 0) mods |= NativeMethods.MOD_WIN;
        return mods;
    }

    /// <summary>
    /// 三条通路的唯一出口：命中 → 去重 → 记日志 → 切到 UI 线程执行动作。
    /// 钩子线程、Raw Input（UI 线程）、轮询线程都会调到这里，所以去重要加锁。
    /// </summary>
    private void Fire(HotkeyAction action, string source, int vk = 0, int mods = 0)
    {
        long now = Environment.TickCount64;
        lock (_lastFire)
        {
            if (_lastFire.TryGetValue(action, out long last) && now - last <= 400)
            {
                Log($"fire-drop action={action} source={source} dt={now - last}ms");
                return;
            }
            _lastFire[action] = now;
        }
        Log($"FIRE action={action} source={source} vk={vk} mods={mods}");
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null)
        {
            Pressed?.Invoke(action);
            return;
        }
        if (dispatcher.CheckAccess()) Pressed?.Invoke(action);
        else dispatcher.BeginInvoke(new Action(() => Pressed?.Invoke(action)));
    }

    /// <summary>把键盘用 RIDEV_INPUTSINK 注册到本窗口：窗口不在前台时系统也把 WM_INPUT 送过来。</summary>
    private void RegisterRawInput()
    {
        if (_rawRegistered || Handle == IntPtr.Zero) return;
        if (Environment.GetEnvironmentVariable("DARIUS_NORAW") == "1") return;
        var devices = new[]
        {
            new NativeMethods.RawInputDevice
            {
                UsagePage = NativeMethods.HID_USAGE_PAGE_GENERIC,
                Usage = NativeMethods.HID_USAGE_GENERIC_KEYBOARD,
                Flags = NativeMethods.RIDEV_INPUTSINK,
                Target = Handle,
            },
        };
        _rawRegistered = NativeMethods.RegisterRawInputDevices(devices, 1,
            (uint)Marshal.SizeOf<NativeMethods.RawInputDevice>());
        if (!_rawRegistered) _rawError = Marshal.GetLastWin32Error();
        Log($"rawinput registered={_rawRegistered} err={_rawError} hwnd={Handle}");
    }

    /// <summary>WM_INPUT 落在 UI 线程：只处理键盘按下，按 vk + 当前修饰键匹配后交给 Fire。</summary>
    private void ReadRawInput(IntPtr lParam)
    {
        _rawMessages++;
        bool verbose = Environment.GetEnvironmentVariable("DARIUS_RAWLOG") == "1";
        uint header = (uint)Marshal.SizeOf<NativeMethods.RawInputHeader>();
        uint size = 0;
        uint query = NativeMethods.GetRawInputData(lParam, NativeMethods.RID_INPUT, IntPtr.Zero, ref size, header);
        if (verbose) Log($"RAWIN #{_rawMessages} query={query} size={size}");
        if (query != 0 || size == 0 || size > 512) return;
        IntPtr buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            uint copied = NativeMethods.GetRawInputData(lParam, NativeMethods.RID_INPUT, buffer, ref size, header);
            if (verbose) Log($"RAWIN #{_rawMessages} copied={copied} size={size}");
            if (copied != size) return;
            int type = Marshal.ReadInt32(buffer);
            if (type != (int)NativeMethods.RIM_TYPEKEYBOARD) return;
            var keyboard = Marshal.PtrToStructure<NativeMethods.RawInput>(buffer).Keyboard;
            int vk = keyboard.VirtualKey;
            // RI_KEY_BREAK(0x01) = 抬起。少数设备/远程桌面会把 Message 发成 0，所以两个都看。
            bool down = keyboard.Message is NativeMethods.WM_KEYDOWN or NativeMethods.WM_SYSKEYDOWN ||
                        (keyboard.Flags & 0x01) == 0;
            if (verbose)
                Log($"RAW type={type} vk={vk} flags=0x{keyboard.Flags:X} msg=0x{keyboard.Message:X} " +
                    $"down={down} known={_hookChords.ContainsKey(vk)} mods={CurrentModifiers()}");
            if (!down || vk == 0 || vk == 0xFF) return;
            if (!_hookChords.TryGetValue(vk, out var chords)) return;
            int mods = CurrentModifiers();
            for (int i = 0; i < chords.Count; i++)
            {
                if (chords[i].Mods != mods) continue;
                _rawHits++;
                Fire(chords[i].Action, "raw", vk, mods);
                break;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>
    /// 轮询兜底：GetAsyncKeyState 读的是系统级按键状态，不经过前台窗口，全屏游戏独占键盘也读得到
    /// （前提是权限不比游戏低）。只认「从未按下到按下」这一下，按住不放不会连发。
    /// DARIUS_NOPOLL=1 可关（QA A/B）。
    /// </summary>
    private void StartPolling()
    {
        if (_pollThread != null) return;
        if (Environment.GetEnvironmentVariable("DARIUS_NOPOLL") == "1") return;
        _pollRun = true;
        _pollThread = new Thread(() =>
        {
            var down = new Dictionary<int, bool>();
            while (_pollRun)
            {
                try { PollOnce(down); } catch { }
                Thread.Sleep(12);
            }
        })
        {
            IsBackground = true,
            Name = "DariusHotkeyPoll",
        };
        _pollThread.Start();
    }

    private void PollOnce(Dictionary<int, bool> down)
    {
        var binds = _pollBinds;
        if (binds.Length == 0) return;
        for (int i = 0; i < binds.Length; i++)
        {
            var (vk, action, mods) = binds[i];
            bool pressed = (NativeMethods.GetAsyncKeyState(vk) & 0x8000) != 0;
            down.TryGetValue(vk, out bool was);
            down[vk] = pressed;
            if (!pressed || was || !_hookEnabled) continue;
            if (CurrentModifiers() != mods) continue;
            _pollHits++;
            Fire(action, "poll", vk, mods);
        }
    }

    /// <summary>Registers every binding. Safe to call repeatedly (e.g. after the user edits).</summary>
    public void Apply(IReadOnlyDictionary<HotkeyAction, string> texts)
    {
        UnregisterAll();
        _hookChords.Clear();
        foreach (var (action, text) in texts)
        {
            if (!HotkeyText.TryParse(text, out var chord) || chord.IsEmpty) continue;
            if (!HotkeyText.IsAllowed(chord, out _))
            {
                Failed.Add(action);
                FailCodes[action] = -2;      // -2 = 不符合绑定规则（缺少修饰键且不是 F1~F12）
                continue;
            }
            int id = 100 + (int)action;
            // No MOD_NOREPEAT: on this machine the call still returned TRUE with it, but the system
            // then never posted WM_HOTKEY and the keystroke kept reaching the window — i.e. the hotkey
            // was silently not in effect. Plain modifiers work.
            int mods = chord.Modifiers;
            if (!_hookChords.TryGetValue(chord.VirtualKey, out var list))
                _hookChords[chord.VirtualKey] = list = new List<(HotkeyAction, int)>(1);
            list.Add((action, mods));

            // QA A/B: DARIUS_HOOKONLY=1 registers nothing, so the hook alone has to carry the chord.
            if (Environment.GetEnvironmentVariable("DARIUS_HOOKONLY") == "1") continue;

            if (_source != null && NativeMethods.RegisterHotKey(_source.Handle, id, mods, chord.VirtualKey))
            {
                _byId[id] = action;
            }
            else
            {
                // 1409 = ERROR_HOTKEY_ALREADY_REGISTERED (another process holds this chord).
                int code = _source == null ? -1 : Marshal.GetLastWin32Error();
                FailCodes[action] = code;
                // With the keyboard hook up, a chord someone else registered still works for us.
                if (_hookHandle == IntPtr.Zero) Failed.Add(action);
            }
        }
        Log($"apply registered={_byId.Count} failed={Failed.Count} hook={_hookHandle != IntPtr.Zero} " +
            $"codes=[{string.Join(",", FailCodes.Select(kv => kv.Key + "=" + kv.Value))}] " +
            $"binds=[{string.Join(" | ", _hookChords.Select(kv => kv.Key + ":" + kv.Value.Count))}]");

        // 轮询用的是「vk + 修饰键」清单，和钩子共用一份，重新注册后一起换掉。
        var poll = new List<(int Vk, HotkeyAction Action, int Mods)>();
        foreach (var (vk, list) in _hookChords)
            foreach (var (action, mods) in list)
                poll.Add((vk, action, mods));
        _pollBinds = poll.ToArray();
        Log($"poll binds={_pollBinds.Length} thread={_pollThread != null} raw={_rawRegistered} " +
            $"elevated={NativeMethods.IsProcessElevated()}");
    }

    /// <summary>Win32 error per failed action (-1 = no window handle yet, -2 = rule, 1409 = taken).</summary>
    public Dictionary<HotkeyAction, int> FailCodes { get; } = new();

    /// <summary>Last key the window saw (diagnostics: tells "no key arrives" from "no match").</summary>
    public string LastLocalKey { get; private set; } = "-";

    public void NoteLocalKey(Key key, ModifierKeys modifiers) =>
        LastLocalKey = modifiers == ModifierKeys.None ? key.ToString() : $"{modifiers}+{key}";

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        HookCalls++;
        if (msg == NativeMethods.WM_HOTKEY)
        {
            bool known = _byId.TryGetValue(wParam.ToInt32(), out var action);
            MessageLog.Add($"WM_HOTKEY w={wParam.ToInt32()} known={known}");
            if (known)
            {
                Log($"WM_HOTKEY id={wParam.ToInt32()} action={action}");
                Fire(action, "hotkey");
                handled = true;
            }
        }
        else if (msg == NativeMethods.WM_INPUT)
        {
            // 不设 handled：WM_INPUT 还要交给 WPF 自己处理输入法/触控笔。
            ReadRawInput(lParam);
        }
        return IntPtr.Zero;
    }

    /// <summary>Diagnostics: how many messages reached the hook, and the last few WM_HOTKEY ones.</summary>
    public int HookCalls { get; private set; }
    public List<string> MessageLog { get; } = new();
    public IntPtr Handle { get; private set; }

    private void UnregisterAll()
    {
        Failed.Clear();
        FailCodes.Clear();
        if (_source != null)
            foreach (int id in _byId.Keys) NativeMethods.UnregisterHotKey(_source.Handle, id);
        _byId.Clear();
    }

    public void Dispose()
    {
        UnregisterAll();
        _hookChords.Clear();
        _pollBinds = Array.Empty<(int, HotkeyAction, int)>();
        _pollRun = false;
        _pollThread = null;
        foreach (var timer in _hookTimers) timer.Stop();
        TearDownHook();
        _source?.RemoveHook(WndProc);
        _source = null;
        _attached = false;
    }
}
