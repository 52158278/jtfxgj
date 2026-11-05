using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace HueContrast.Core;

/// <summary>热键描述，如 Ctrl+Alt+Q。</summary>
public sealed class HotkeySpec
{
    public Keys Key { get; set; } = Keys.Q;
    public bool Ctrl { get; set; } = true;
    public bool Alt { get; set; } = true;
    public bool Shift { get; set; }
    public bool Win { get; set; }

    public int Modifiers =>
        (Ctrl ? 0x0002 : 0) | (Alt ? 0x0001 : 0) | (Shift ? 0x0004 : 0) | (Win ? 0x0008 : 0);

    public override string ToString()
    {
        var parts = new List<string>();
        if (Ctrl) parts.Add("Ctrl");
        if (Alt) parts.Add("Alt");
        if (Shift) parts.Add("Shift");
        if (Win) parts.Add("Win");
        parts.Add(Key.ToString());
        return string.Join("+", parts);
    }

    public HotkeySpec Clone() => new()
    {
        Key = Key, Ctrl = Ctrl, Alt = Alt, Shift = Shift, Win = Win
    };

    public static HotkeySpec Default() => new() { Key = Keys.Q, Ctrl = true, Alt = true };

    public static HotkeySpec Parse(string text, HotkeySpec fallback = null)
    {
        fallback ??= Default();
        if (string.IsNullOrWhiteSpace(text)) return fallback;

        string[] parts = text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) return fallback;

        var spec = new HotkeySpec { Ctrl = false, Alt = false, Shift = false, Win = false };
        bool hasKey = false;

        foreach (string p in parts)
        {
            switch (p.ToLowerInvariant())
            {
                case "ctrl":
                case "control": spec.Ctrl = true; break;
                case "alt": spec.Alt = true; break;
                case "shift": spec.Shift = true; break;
                case "win":
                case "windows": spec.Win = true; break;
                default:
                    string name = p;
                    if (name.Length == 1 && char.IsLetterOrDigit(name[0])) name = name.ToUpperInvariant();
                    if (Enum.TryParse<Keys>(name, true, out var key))
                    {
                        spec.Key = key;
                        hasKey = true;
                    }
                    break;
            }
        }

        if (!hasKey) return fallback;
        return spec;
    }
}

public sealed class HotkeyManager : NativeWindow, IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private const int HotkeyId = 0x4857;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private bool _registered;
    private Action _callback;

    public HotkeyManager()
    {
        var cp = new CreateParams
        {
            Caption = "HueContrastHotkeyWindow",
            X = 0, Y = 0, Width = 0, Height = 0,
            Style = 0,
            ExStyle = 0
        };
        CreateHandle(cp);
    }

    public bool Register(HotkeySpec spec, Action callback)
    {
        Unregister();
        _callback = callback;
        _registered = RegisterHotKey(Handle, HotkeyId, (uint)spec.Modifiers, (uint)spec.Key);
        return _registered;
    }

    public void Unregister()
    {
        if (_registered)
        {
            UnregisterHotKey(Handle, HotkeyId);
            _registered = false;
        }
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_HOTKEY && m.WParam.ToInt32() == HotkeyId)
        {
            _callback?.Invoke();
            return;
        }
        base.WndProc(ref m);
    }

    public void Dispose()
    {
        Unregister();
        if (Handle != IntPtr.Zero) DestroyHandle();
    }
}
