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

    /// <summary>Raised for every activation.</summary>
    public event Action<HotkeyAction>? Pressed;

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
    }

    /// <summary>Registers every binding. Safe to call repeatedly (e.g. after the user edits).</summary>
    public void Apply(IReadOnlyDictionary<HotkeyAction, string> texts)
    {
        UnregisterAll();
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
            if (_source != null && NativeMethods.RegisterHotKey(_source.Handle, id, mods, chord.VirtualKey))
            {
                _byId[id] = action;
            }
            else
            {
                Failed.Add(action);
                // 1409 = ERROR_HOTKEY_ALREADY_REGISTERED (another process holds this chord).
                FailCodes[action] = _source == null ? -1 : System.Runtime.InteropServices.Marshal.GetLastWin32Error();
            }
        }
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
                Pressed?.Invoke(action);
                handled = true;
            }
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
        _source?.RemoveHook(WndProc);
        _source = null;
        _attached = false;
    }
}
