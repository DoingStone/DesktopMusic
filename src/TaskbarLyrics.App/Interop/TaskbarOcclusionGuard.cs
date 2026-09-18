using System.Diagnostics;
using System.Runtime.InteropServices;

namespace TaskbarLyrics.App.Interop;

/// <summary>
/// Keeps the overlay above the taskbar from a dedicated thread.
/// <para>
/// The taskbar is topmost too, and Explorer re-raises <c>Shell_TrayWnd</c> when it is
/// activated, when the Start menu opens, and while the auto-hide bar animates in.
/// Whenever that happens the bar paints over the strip.
/// </para>
/// <para>
/// Re-asserting from the UI thread is too slow to be invisible: the notification
/// arrives on a hook thread, is marshalled through <c>Dispatcher.BeginInvoke</c>, and
/// then waits behind whatever WPF is rendering — measured as the lyrics staying
/// covered for ~150 ms. <c>SetWindowPos</c> needs no UI thread, so this runs its own
/// thread and reacts directly.
/// </para>
/// <para>
/// The thread blocks on an event signalled by the Z-order hook, with a slow safety
/// poll behind it. Busy-polling was measured at 1.6 % of a core purely in scheduler
/// wake-ups, which is a lot for an idle tray utility; waiting costs nothing and still
/// reacts immediately.
/// </para>
/// </summary>
internal sealed class TaskbarOcclusionGuard : IDisposable
{
    /// <summary>
    /// Safety-net poll. The hook covers the real triggers; this only catches cases
    /// where the taskbar rises without raising an event we listen for.
    /// </summary>
    private const int SafetyPollMs = 250;

    /// <summary>
    /// Follow-up re-asserts after a trigger, in milliseconds. The taskbar's rise is
    /// not a single instant — activation and the auto-hide animation settle over the
    /// following tens of milliseconds — so one immediate correction can lose the race.
    /// </summary>
    private static readonly int[] FollowUpsMs = { 12, 40, 90 };

    private const uint GW_HWNDPREV = 3;
    private const uint GA_ROOT = 2;

    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOACTIVATE = 0x0010;

    private static readonly IntPtr HWND_TOPMOST = new(-1);

    private readonly IntPtr _overlay;
    private readonly AutoResetEvent _signal = new(false);
    private Thread? _thread;
    private volatile bool _stopping;

    /// <summary>
    /// Cached taskbar handle. Re-resolving it with <c>FindWindow</c> on every check
    /// would walk the entire window list; at this rate it only needs occasional
    /// refreshing, because Explorer can restart.
    /// </summary>
    private IntPtr _taskbar;

    private DateTime _taskbarResolvedAt = DateTime.MinValue;

    /// <summary>
    /// How long a resolved taskbar handle is trusted. Time-based rather than
    /// count-based, so a burst of triggers cannot turn into a burst of FindWindow calls.
    /// </summary>
    private static readonly TimeSpan TaskbarCacheFor = TimeSpan.FromSeconds(2);

    /// <summary>Number of re-asserts performed; exposed for diagnostics and tests.</summary>
    public int ReassertCount { get; private set; }

    public TaskbarOcclusionGuard(IntPtr overlay)
    {
        _overlay = overlay;
    }

    public void Start()
    {
        if (_thread is not null || _overlay == IntPtr.Zero) return;

        // Escape hatch for A/B measurement, so this guard's effect can be attributed
        // rather than assumed from runs that all had it enabled.
        if (Environment.GetEnvironmentVariable("TBL_NO_GUARD") == "1") return;

        _thread = new Thread(Loop)
        {
            IsBackground = true,
            Name = "taskbar-occlusion-guard",
            // Above normal so it pre-empts rendering work rather than queueing behind it.
            Priority = ThreadPriority.AboveNormal,
        };

        _thread.Start();
    }

    /// <summary>
    /// Wake the guard. Safe to call from any thread (it is called from the WinEvent
    /// hook thread) and deliberately does no work beyond signalling, so the reaction
    /// is immediate rather than queued behind the UI thread.
    /// </summary>
    public void Notify()
    {
        try
        {
            _signal.Set();
        }
        catch (ObjectDisposedException)
        {
            // Shutting down.
        }
    }

    private void Loop()
    {
        while (!_stopping)
        {
            try
            {
                // Blocks until signalled, or until the safety poll elapses.
                _signal.WaitOne(SafetyPollMs);
                if (_stopping) break;

                if (IsTaskbarDirectlyAbove())
                {
                    Reassert();

                    // Keep correcting while the taskbar settles.
                    foreach (var delay in FollowUpsMs)
                    {
                        if (_stopping) break;
                        Thread.Sleep(delay);
                        if (IsTaskbarDirectlyAbove()) Reassert();
                    }
                }
            }
            catch
            {
                // A guard must never take the process down.
            }
        }
    }

    /// <summary>
    /// True when the taskbar is the window immediately above the overlay, i.e. it is
    /// the thing painting over us.
    /// </summary>
    /// <summary>
    /// True when the taskbar is anywhere above the overlay, not merely its immediate
    /// Z-order neighbour.
    /// <para>
    /// Checking only the immediate neighbour missed the Start-menu case: the order can
    /// be <c>[taskbar][Start host][overlay]</c>, so the neighbour is the Start host and
    /// the taskbar — which is still painting over us — went undetected. Only topmost
    /// windows can be above a topmost window, and there are few of them, so a short
    /// bounded walk is both correct and cheap.
    /// </para>
    /// </summary>
    private bool IsTaskbarDirectlyAbove()
    {
        var taskbar = ResolveTaskbar();
        if (taskbar == IntPtr.Zero) return false;

        var above = GetWindow(_overlay, GW_HWNDPREV);

        for (int step = 0; step < MaxZOrderWalk && above != IntPtr.Zero; step++)
        {
            if (above == taskbar) return true;

            var root = GetAncestor(above, GA_ROOT);
            if (root != IntPtr.Zero && root == taskbar) return true;

            above = GetWindow(above, GW_HWNDPREV);
        }

        return false;
    }

    /// <summary>Bound on the upward Z-order walk; the topmost band holds very few windows.</summary>
    private const int MaxZOrderWalk = 12;

    /// <summary>Taskbar handle, refreshed only occasionally.</summary>
    private IntPtr ResolveTaskbar()
    {
        if (_taskbar != IntPtr.Zero && DateTime.UtcNow - _taskbarResolvedAt <= TaskbarCacheFor)
        {
            return _taskbar;
        }

        _taskbarResolvedAt = DateTime.UtcNow;

        var found = FindWindow("Shell_TrayWnd", null);
        if (found != IntPtr.Zero) _taskbar = found;

        return _taskbar;
    }

    private void Reassert()
    {
        // One call: straight to the top of the topmost band. SWP_NOACTIVATE keeps focus
        // where the user put it.
        SetWindowPos(_overlay, HWND_TOPMOST, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);

        ReassertCount++;
    }

    public void Dispose()
    {
        _stopping = true;

        try
        {
            _thread?.Join(200);
        }
        catch
        {
            // Shutting down.
        }

        _thread = null;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hWnd, uint gaFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
}
