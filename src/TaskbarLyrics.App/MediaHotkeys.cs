using System.Windows;
using TaskbarLyrics.App.Configuration;
using TaskbarLyrics.App.Interop;

namespace TaskbarLyrics.App;

/// <summary>
/// Owns the system-wide playback shortcuts.
/// <para>
/// Hotkeys are used instead of on-screen buttons for the primary transport
/// actions, because the overlay window is deliberately click-through: adding
/// clickable controls there would start swallowing taskbar clicks.
/// </para>
/// </summary>
internal sealed class MediaHotkeys
{
    // Stable ids so a re-registration can unregister exactly what it registered.
    private const int IdPlayPause = 0x7101;
    private const int IdNext = 0x7102;
    private const int IdPrevious = 0x7103;
    private const int IdToggleOverlay = 0x7104;

    private readonly Window _window;
    private readonly Func<Task> _togglePlayPause;
    private readonly Func<Task> _next;
    private readonly Func<Task> _previous;
    private readonly Action _toggleOverlay;

    private readonly List<GlobalHotkeys.Binding> _registered = new();

    public MediaHotkeys(
        Window window,
        Func<Task> togglePlayPause,
        Func<Task> next,
        Func<Task> previous,
        Action toggleOverlay)
    {
        _window = window;
        _togglePlayPause = togglePlayPause;
        _next = next;
        _previous = previous;
        _toggleOverlay = toggleOverlay;
    }

    /// <summary>Human-readable text for the shortcuts that could not be claimed.</summary>
    public IReadOnlyList<string> UnavailableShortcuts { get; private set; } = Array.Empty<string>();

    /// <summary>Install the message hook. Call once, after the window has a handle.</summary>
    public void Attach()
    {
        GlobalHotkeys.Attach(_window, OnHotkey);
    }

    /// <summary>Drop every registration, so the shortcuts are released on exit.</summary>
    public void Unregister()
    {
        GlobalHotkeys.Unregister(_window, _registered);
        _registered.Clear();
    }

    /// <summary>
    /// (Re)register from settings. Safe to call whenever the bindings change; the
    /// previous set is always released first.
    /// </summary>
    public void Apply(AppSettings settings)
    {
        Unregister();

        var candidates = new (int Id, string? Text, string Label)[]
        {
            (IdPlayPause, settings.HotkeyPlayPause, "暂停/播放"),
            (IdNext, settings.HotkeyNextTrack, "下一首"),
            (IdPrevious, settings.HotkeyPreviousTrack, "上一首"),
            (IdToggleOverlay, settings.HotkeyToggleOverlay, "显示/隐藏歌词"),
        };

        var parsed = new List<GlobalHotkeys.Binding>();
        var labels = new Dictionary<int, string>();

        foreach (var (id, text, label) in candidates)
        {
            var binding = GlobalHotkeys.Parse(id, text);
            if (binding is null) continue;

            parsed.Add(binding.Value);
            labels[id] = $"{label}（{text}）";
        }

        var failed = GlobalHotkeys.Register(_window, parsed);

        _registered.AddRange(parsed.Where(p => !failed.Contains(p.Id)));
        UnavailableShortcuts = failed
            .Select(id => labels.TryGetValue(id, out var l) ? l : $"id {id}")
            .ToArray();

        Diag.Log($"[hotkeys] registered {_registered.Count}, failed {failed.Count}" +
                 (failed.Count > 0 ? " -> " + string.Join(", ", UnavailableShortcuts) : ""));
    }

    private void OnHotkey(int id)
    {
        try
        {
            switch (id)
            {
                case IdPlayPause:
                    _ = _togglePlayPause();
                    break;
                case IdNext:
                    _ = _next();
                    break;
                case IdPrevious:
                    _ = _previous();
                    break;
                case IdToggleOverlay:
                    _toggleOverlay();
                    break;
            }
        }
        catch (Exception ex)
        {
            Diag.Log($"[hotkeys] handler {id} failed: {ex.Message}");
        }
    }
}
