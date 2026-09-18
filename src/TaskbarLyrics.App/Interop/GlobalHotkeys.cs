using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace TaskbarLyrics.App.Interop;

/// <summary>
/// System-wide hotkeys, so playback can be controlled without the overlay having
/// to receive mouse input (it is deliberately click-through).
/// <para>
/// Hotkeys are registered against the overlay's own window handle, so they live
/// and die with the application and need no extra window.
/// </para>
/// </summary>
internal static class GlobalHotkeys
{
    /// <summary>Identifies our WM_HOTKEY messages; must be unique per thread.</summary>
    private const int HotkeyMessage = 0x0312;

    /// <summary>Application-defined id base, to avoid clashing with anyone else.</summary>
    private const int IdBase = 0x7100;

    [Flags]
    internal enum Modifiers : uint
    {
        None = 0,
        Alt = 0x0001,
        Control = 0x0002,
        Shift = 0x0004,
        Win = 0x0008,
        NoRepeat = 0x4000,
    }

    /// <summary>A parsed hotkey binding.</summary>
    internal readonly record struct Binding(int Id, Modifiers Modifiers, uint VirtualKey);

    /// <summary>
    /// Parse a binding such as <c>Ctrl+Alt+Space</c> or <c>Ctrl+Alt+Right</c>.
    /// Returns null when the text cannot be understood.
    /// </summary>
    public static Binding? Parse(int id, string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var parts = text.Split('+', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Trim())
            .Where(p => p.Length > 0)
            .ToArray();

        if (parts.Length == 0) return null;

        var modifiers = Modifiers.NoRepeat;
        Key? key = null;

        foreach (var part in parts)
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl":
                case "control":
                    modifiers |= Modifiers.Control;
                    break;
                case "alt":
                    modifiers |= Modifiers.Alt;
                    break;
                case "shift":
                    modifiers |= Modifiers.Shift;
                    break;
                case "win":
                    modifiers |= Modifiers.Win;
                    break;
                default:
                    if (!Enum.TryParse<Key>(part, ignoreCase: true, out var parsed)) return null;
                    key = parsed;
                    break;
            }
        }

        if (key is null) return null;

        var virtualKey = (uint)KeyInterop.VirtualKeyFromKey(key.Value);
        if (virtualKey == 0) return null;

        return new Binding(id, modifiers, virtualKey);
    }

    /// <summary>
    /// Register a set of bindings. Returns the ids that failed, so the caller can
    /// report which shortcut is already taken by another application.
    /// </summary>
    public static IReadOnlyList<int> Register(Window window, IEnumerable<Binding> bindings)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return Array.Empty<int>();

        var failed = new List<int>();

        foreach (var binding in bindings)
        {
            if (!RegisterHotKey(handle, binding.Id, (uint)binding.Modifiers, binding.VirtualKey))
            {
                failed.Add(binding.Id);
            }
        }

        return failed;
    }

    public static void Unregister(Window window, IEnumerable<Binding> bindings)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;

        foreach (var binding in bindings)
        {
            UnregisterHotKey(handle, binding.Id);
        }
    }

    /// <summary>
    /// Hook a window's messages and dispatch WM_HOTKEY to a handler keyed by id.
    /// </summary>
    public static void Attach(Window window, Action<int> onHotkey)
    {
        var source = (HwndSource?)PresentationSource.FromVisual(window);
        if (source is null) return;

        source.AddHook((IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) =>
        {
            if (msg != HotkeyMessage) return IntPtr.Zero;

            handled = true;
            onHotkey(wParam.ToInt32());
            return IntPtr.Zero;
        });
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
