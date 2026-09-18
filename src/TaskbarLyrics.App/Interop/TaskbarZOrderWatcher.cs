using System.Runtime.InteropServices;

namespace TaskbarLyrics.App.Interop;

/// <summary>
/// Watches for the taskbar being brought to the front and raises an event so the
/// overlay can immediately re-assert its Z-order.
/// <para>
/// The taskbar is topmost as well, and Explorer re-raises it above other topmost
/// windows whenever it is activated. A polling guard only notices up to one timer
/// interval later, which measured as the lyrics being covered for ~190 ms per
/// taskbar click — clearly visible as a flicker. Reacting to the foreground/reorder
/// event instead cuts that to roughly one compositor frame.
/// </para>
/// <para>
/// The hook is out-of-context, so no DLL is injected into Explorer.
/// </para>
/// </summary>
internal sealed class TaskbarZOrderWatcher : IDisposable
{
    private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    private const uint EVENT_OBJECT_REORDER = 0x8004;

    private const int WINEVENT_OUTOFCONTEXT = 0x0000;
    private const int OBJID_WINDOW = 0;
    private const int CHILDID_SELF = 0;

    /// <summary>Raised on the hook thread when the taskbar may have risen.</summary>
    public event Action? TaskbarRaised;

    /// <summary>
    /// Held in a field so the delegate is not collected while Windows still holds
    /// the function pointer — a collected callback would crash Explorer's callback
    /// into freed memory.
    /// </summary>
    private WinEventDelegate? _callback;

    private IntPtr _foregroundHook;
    private IntPtr _reorderHook;
    private bool _disposed;

    public void Start()
    {
        if (_disposed || _callback is not null) return;

        _callback = OnWinEvent;

        try
        {
            // Global foreground changes: clicking the taskbar makes Explorer the
            // foreground window, which is the trigger we care about.
            _foregroundHook = SetWinEventHook(
                EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND,
                IntPtr.Zero, _callback, 0, 0, WINEVENT_OUTOFCONTEXT);

            // Z-order changes on the taskbar itself.
            _reorderHook = SetWinEventHook(
                EVENT_OBJECT_REORDER, EVENT_OBJECT_REORDER,
                IntPtr.Zero, _callback, 0, 0, WINEVENT_OUTOFCONTEXT);

            Diag.Log($"[zwatch] hooks installed fg=0x{_foregroundHook.ToInt64():X} " +
                     $"reorder=0x{_reorderHook.ToInt64():X}");
        }
        catch (Exception ex)
        {
            Diag.Log($"[zwatch] hook setup failed: {ex.Message}");
        }
    }

    private void OnWinEvent(
        IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint thread, uint time)
    {
        if (_disposed) return;

        try
        {
            // For reorder events only the window object itself is interesting; child
            // reorders are frequent and irrelevant.
            if (eventType == EVENT_OBJECT_REORDER && (idObject != OBJID_WINDOW || idChild != CHILDID_SELF))
            {
                return;
            }

            TaskbarRaised?.Invoke();
        }
        catch
        {
            // A hook callback must never throw into the OS.
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            if (_foregroundHook != IntPtr.Zero) UnhookWinEvent(_foregroundHook);
            if (_reorderHook != IntPtr.Zero) UnhookWinEvent(_reorderHook);
        }
        catch
        {
            // Shutting down.
        }

        _foregroundHook = IntPtr.Zero;
        _reorderHook = IntPtr.Zero;
        _callback = null;
    }

    private delegate void WinEventDelegate(
        IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWinEventHook(
        uint eventMin, uint eventMax, IntPtr hmodWinEventProc,
        WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWinEvent(IntPtr hWinEventHook);
}
