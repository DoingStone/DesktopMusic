using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace TaskbarLyrics.App.Interop;

/// <summary>
/// A system-tray icon built directly on <c>Shell_NotifyIcon</c>.
/// <para>
/// Implemented with Win32 rather than a UI framework package so the app has zero
/// third-party dependencies. The icon runs its own message-only window on a
/// dedicated STA thread, and re-registers itself when Explorer restarts.
/// </para>
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private const int WM_APP = 0x8000;
    private const int WM_TRAYICON = WM_APP + 1;
    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_RBUTTONUP = 0x0205;
    private const int WM_COMMAND = 0x0111;
    private const int WM_DESTROY = 0x0002;
    private const int WM_TASKBARCREATED = 0x00000000; // registered at runtime
    private const int WM_CONTEXTMENU = 0x007B;

    private const int NIM_ADD = 0x00000000;
    private const int NIM_MODIFY = 0x00000001;
    private const int NIM_DELETE = 0x00000002;

    private const int NIF_MESSAGE = 0x00000001;
    private const int NIF_ICON = 0x00000002;
    private const int NIF_TIP = 0x00000004;

    private const int MF_STRING = 0x00000000;
    private const int MF_SEPARATOR = 0x00000800;
    private const int MF_CHECKED = 0x00000008;
    private const int MF_UNCHECKED = 0x00000000;

    private const int TPM_RIGHTBUTTON = 0x0002;
    private const int TPM_RETURNCMD = 0x0100;
    private const int TPM_NONOTIFY = 0x0080;

    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new(false);
    private readonly List<TrayMenuItem> _items = new();

    /// <summary>
    /// The UI thread's dispatcher.
    /// <para>
    /// Essential: the tray runs its own message loop on a separate thread and its
    /// menu handlers fire there. Every callback must therefore be marshalled back
    /// to the UI thread — touching WPF objects from the tray thread throws, and
    /// those exceptions would be swallowed inside native callbacks, leaving the
    /// app apparently wedged (for example an "Exit" that never exits).
    /// </para>
    /// </summary>
    private readonly Dispatcher _uiDispatcher;

    private IntPtr _hwnd = IntPtr.Zero;
    private IntPtr _hIcon = IntPtr.Zero;
    private uint _taskbarCreatedMessage;
    private bool _disposed;
    private string _tooltip = "任务栏歌词";

    public TrayIcon()
    {
        // Capture the creating (UI) thread up front.
        _uiDispatcher = Dispatcher.CurrentDispatcher;

        _thread = new Thread(ThreadMain)
        {
            IsBackground = true,
            Name = "TaskbarLyrics.Tray",
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _ready.Wait(TimeSpan.FromSeconds(5));
    }

    /// <summary>
    /// Distinguish a deliberate double-click from an incidental single click.
    /// <para>
    /// The lyric overlay sits immediately above the notification area, so a
    /// single stray click on the taskbar can land on this icon. Requiring a
    /// double-click makes accidental lyric hiding essentially impossible.
    /// </para>
    /// </summary>
    private void HandleLeftClick()
    {
        var now = DateTime.UtcNow;
        var sinceLast = now - _lastLeftClick;
        _lastLeftClick = now;

        if (sinceLast <= DoubleClickWindow)
        {
            _lastLeftClick = DateTime.MinValue;

            var doubleClick = LeftDoubleClickAction;
            if (doubleClick is not null)
            {
                RunOnUiThread(doubleClick);
                return;
            }
        }

        // A single click is only honoured when no double-click action is set,
        // i.e. when the caller explicitly wants the simple behaviour.
        if (LeftDoubleClickAction is null && sinceLast > DoubleClickWindow &&
            LeftClickAction is { } single)
        {
            RunOnUiThread(single);
        }
    }

    /// <summary>
    /// Run a callback on the UI thread. Non-blocking when called from the tray
    /// thread, so a callback that shuts the app down cannot deadlock against the
    /// tray's own message loop.
    /// </summary>
    private void RunOnUiThread(Action action)
    {
        if (_uiDispatcher.CheckAccess())
        {
            action();
            return;
        }

        try
        {
            _uiDispatcher.BeginInvoke(DispatcherPriority.Normal, action);
        }
        catch (Exception ex)
        {
            Diag.Log($"[tray] failed to marshal callback: {ex.Message}");
        }
    }

    /// <summary>Menu entries, rebuilt on each popup so check states stay current.</summary>
    public sealed record TrayMenuItem(string Text, Action? Invoke = null, Func<bool>? IsChecked = null, bool IsSeparator = false)
    {
        public static TrayMenuItem Separator() => new(string.Empty, IsSeparator: true);
    }

    public void SetMenu(IEnumerable<TrayMenuItem> items)
    {
        _items.Clear();
        _items.AddRange(items);
    }

    public void SetTooltip(string text)
    {
        _tooltip = text.Length > 120 ? text[..120] : text;
        UpdateIcon();
    }

    /// <summary>
    /// Single left-click action. Prefer <see cref="LeftDoubleClickAction"/>:
    /// a plain single click is too easy to trigger by accident, which matters
    /// because the lyric overlay sits directly above the notification area.
    /// </summary>
    public Action? LeftClickAction { get; set; }

    /// <summary>Double left-click action (the deliberate gesture).</summary>
    public Action? LeftDoubleClickAction { get; set; }

    /// <summary>Maximum gap between the two clicks of a double-click.</summary>
    private static readonly TimeSpan DoubleClickWindow = TimeSpan.FromMilliseconds(
        Math.Max(200, (int)GetDoubleClickTime()));

    private DateTime _lastLeftClick = DateTime.MinValue;

    private void ThreadMain()
    {
        _taskbarCreatedMessage = RegisterWindowMessage("TaskbarCreated");

        var wndClass = new WNDCLASSEX
        {
            cbSize = Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = WndProc,
            hInstance = GetModuleHandle(null),
            lpszClassName = "TaskbarLyricsTrayWnd",
        };

        RegisterClassEx(ref wndClass);

        _hwnd = CreateWindowEx(
            0, wndClass.lpszClassName, "TaskbarLyrics",
            0, 0, 0, 0, 0,
            HWND_MESSAGE, IntPtr.Zero, wndClass.hInstance, IntPtr.Zero);

        _hIcon = CreateNoteIcon();

        AddIcon();
        _ready.Set();

        while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }

        RemoveIcon();

        if (_hIcon != IntPtr.Zero) DestroyIcon(_hIcon);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam)
    {
        // Explorer restarted: re-add the icon so the app stays reachable.
        if (_taskbarCreatedMessage != 0 && msg == (int)_taskbarCreatedMessage)
        {
            AddIcon();
            return IntPtr.Zero;
        }

        switch (msg)
        {
            case WM_TRAYICON:
                var evt = lParam.ToInt32() & 0xFFFF;
                if (evt is WM_LBUTTONUP)
                {
                    HandleLeftClick();
                }
                else if (evt is WM_RBUTTONUP or WM_CONTEXTMENU)
                {
                    ShowMenu();
                }
                return IntPtr.Zero;

            case WM_COMMAND:
                var id = wParam.ToInt32() & 0xFFFF;
                var commandItem = _items.FirstOrDefault(i => i.GetHashCode() == id);
                if (commandItem?.Invoke is { } commandAction) RunOnUiThread(commandAction);
                return IntPtr.Zero;

            case WM_DESTROY:
                PostQuitMessage(0);
                return IntPtr.Zero;
        }

        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    private void ShowMenu()
    {
        var menu = CreatePopupMenu();
        if (menu == IntPtr.Zero) return;

        try
        {
            // First entry shows the current track as a disabled header.
            var headerId = 1;
            var idMap = new Dictionary<int, TrayMenuItem>();

            foreach (var item in _items)
            {
                if (item.IsSeparator)
                {
                    AppendMenu(menu, MF_SEPARATOR, 0, null);
                    continue;
                }

                var flags = MF_STRING;
                var isChecked = item.IsChecked?.Invoke() ?? false;
                flags |= isChecked ? MF_CHECKED : MF_UNCHECKED;

                // Hash codes give each entry a stable command id.
                var commandId = item.GetHashCode() & 0x7FFF;
                if (commandId == 0) commandId = headerId++;

                AppendMenu(menu, flags, (IntPtr)commandId, item.Text);
                idMap[commandId] = item;
            }

            if (idMap.Count == 0) return;

            GetCursorPos(out var pt);

            // Required so the menu closes when the user clicks elsewhere.
            SetForegroundWindow(_hwnd);

            var chosen = TrackPopupMenuEx(
                menu,
                TPM_RIGHTBUTTON | TPM_RETURNCMD | TPM_NONOTIFY,
                pt.X, pt.Y, _hwnd, IntPtr.Zero);

            if (chosen != 0 && idMap.TryGetValue(chosen, out var selected) &&
                selected.Invoke is { } menuAction)
            {
                // Marshal: menu handlers must not run on the tray thread.
                RunOnUiThread(menuAction);
            }

            PostMessage(_hwnd, 0x0000, IntPtr.Zero, IntPtr.Zero); // WM_NULL
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    private void AddIcon()
    {
        var data = new NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = _hwnd,
            uID = 1,
            uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
            uCallbackMessage = WM_TRAYICON,
            hIcon = _hIcon,
            szTip = _tooltip,
            szInfo = string.Empty,
            szTip2 = string.Empty,
        };

        Shell_NotifyIcon(NIM_ADD, ref data);
    }

    private void UpdateIcon()
    {
        if (_hwnd == IntPtr.Zero) return;

        var data = new NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = _hwnd,
            uID = 1,
            uFlags = NIF_TIP,
            hIcon = _hIcon,
            szTip = _tooltip,
            szInfo = string.Empty,
            szTip2 = string.Empty,
        };

        Shell_NotifyIcon(NIM_MODIFY, ref data);
    }

    private void RemoveIcon()
    {
        if (_hwnd == IntPtr.Zero) return;

        var data = new NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = _hwnd,
            uID = 1,
        };

        Shell_NotifyIcon(NIM_DELETE, ref data);
    }

    /// <summary>
    /// Load the application icon from the embedded master artwork and convert it
    /// to an HICON for the notification area.
    /// <para>
    /// Using GDI+ to build the HICON (rather than drawing with GDI primitives)
    /// keeps the tray icon pixel-identical to the executable's icon, including its
    /// alpha channel, at every DPI.
    /// </para>
    /// </summary>
    private static IntPtr CreateNoteIcon()
    {
        try
        {
            using var stream = typeof(TrayIcon).Assembly
                .GetManifestResourceStream("TaskbarLyrics.AppIcon.png");

            if (stream is null)
            {
                Diag.Log("[tray] embedded icon missing; drawing a fallback");
                return CreateFallbackIcon();
            }

            using var source = new Bitmap(stream);

            // The notification area renders at the small-icon metric; 32 px covers
            // it even on very high DPI where Windows scales the icon up.
            using var scaled = new Bitmap(32, 32, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(scaled))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.CompositingQuality = CompositingQuality.HighQuality;
                g.Clear(Color.Transparent);
                g.DrawImage(source, new Rectangle(0, 0, 32, 32));
            }

            return CreateHIconFromBitmap(scaled);
        }
        catch (Exception ex)
        {
            Diag.Log($"[tray] icon load failed: {ex.Message}");
            return CreateFallbackIcon();
        }
    }

    /// <summary>
    /// Build an HICON that preserves the bitmap's alpha channel.
    /// <para>
    /// The obvious <c>Bitmap.GetHicon</c> cannot be used: it routes through
    /// <c>CreateIconIndirect</c> with a monochrome mask and mangles soft edges.
    /// This builds the icon from an explicit 32-bpp colour bitmap plus a fully
    /// transparent mask, which Windows composites correctly.
    /// </para>
    /// </summary>
    private static IntPtr CreateHIconFromBitmap(Bitmap bitmap)
    {
        var hBitmap = bitmap.GetHbitmap(Color.FromArgb(0));
        var hMask = CreateBitmap(bitmap.Width, bitmap.Height, 1, 1, IntPtr.Zero);

        try
        {
            var info = new ICONINFO
            {
                fIcon = true,
                xHotspot = 0,
                yHotspot = 0,
                hbmMask = hMask,
                hbmColor = hBitmap,
            };

            return CreateIconIndirect(ref info);
        }
        finally
        {
            DeleteObject(hBitmap);
            DeleteObject(hMask);
        }
    }

    /// <summary>
    /// Minimal drawn icon used only if the embedded artwork cannot be loaded, so
    /// the app still shows something identifiable in the tray.
    /// </summary>
    private static IntPtr CreateFallbackIcon()
    {
        const int size = 32;

        using var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            using var plate = new SolidBrush(Color.FromArgb(255, 8, 8, 8));
            g.FillRectangle(plate, 0, 0, size, size);

            using var note = new SolidBrush(Color.FromArgb(255, 120, 235, 150));
            g.FillEllipse(note, 6, 19, 11, 9);
            g.FillRectangle(note, 15, 5, 3, 19);
            g.FillRectangle(note, 18, 6, 9, 4);
        }

        return CreateHIconFromBitmap(bmp);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        var clock = System.Diagnostics.Stopwatch.StartNew();

        if (_hwnd != IntPtr.Zero)
        {
            PostMessage(_hwnd, WM_DESTROY, IntPtr.Zero, IntPtr.Zero);
        }

        Diag.Log($"[tray] WM_DESTROY posted at {clock.ElapsedMilliseconds} ms");

        // Wait for the tray thread to remove the icon and exit, but never block
        // shutdown indefinitely — the icon vanishes with the process anyway.
        if (_thread.IsAlive && !_thread.Join(TimeSpan.FromMilliseconds(600)))
        {
            Diag.Log("[tray] thread did not exit within 600 ms; continuing shutdown");
        }
        else
        {
            Diag.Log($"[tray] thread joined at {clock.ElapsedMilliseconds} ms");
        }

        _ready.Dispose();
        Diag.Log($"[tray] dispose complete at {clock.ElapsedMilliseconds} ms");
    }

    // ---- native --------------------------------------------------------

    private static readonly IntPtr HWND_MESSAGE = new(-3);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;

        public uint dwState;
        public uint dwStateMask;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;

        public uint uTimeoutOrVersion;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;

        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip2;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public int cbSize;
        public uint style;
        public WndProcDelegate lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
        public IntPtr hIconSm;
    }

    private delegate IntPtr WndProcDelegate(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX;
        public int ptY;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO
    {
        public bool fIcon;
        public int xHotspot;
        public int yHotspot;
        public IntPtr hbmMask;
        public IntPtr hbmColor;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINTSTRUCT
    {
        public int X, Y;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(int message, ref NOTIFYICONDATA data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassEx(ref WNDCLASSEX lpwcx);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowEx(
        int exStyle, string className, string windowName, int style,
        int x, int y, int width, int height,
        IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetMessage(out MSG msg, IntPtr hwnd, uint min, uint max);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG msg);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DispatchMessage(ref MSG msg);

    [DllImport("user32.dll")]
    private static extern void PostQuitMessage(int exitCode);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string message);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenu(IntPtr menu, int flags, IntPtr id, string? text);

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr menu);

    [DllImport("user32.dll")]
    private static extern int TrackPopupMenuEx(IntPtr menu, int flags, int x, int y, IntPtr hwnd, IntPtr paramsPtr);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINTSTRUCT pt);

    /// <summary>The user's configured double-click interval, in milliseconds.</summary>
    [DllImport("user32.dll")]
    private static extern uint GetDoubleClickTime();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? name);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr dc);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr dc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int width, int height);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateBitmap(int width, int height, uint planes, uint bits, IntPtr bitsPtr);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr obj);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateSolidBrush(uint color);

    [DllImport("user32.dll")]
    private static extern int FillRect(IntPtr dc, ref RECT rect, IntPtr brush);

    [DllImport("gdi32.dll")]
    private static extern bool Ellipse(IntPtr dc, int left, int top, int right, int bottom);

    [DllImport("user32.dll")]
    private static extern IntPtr CreateIconIndirect(ref ICONINFO iconInfo);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr icon);
}
