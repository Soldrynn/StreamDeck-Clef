using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ClefBridge;

/// <summary>
/// Reaches the Apple Music for Windows controls that the Windows media session
/// does not expose (shuffle, repeat, favorite, playlists) through UI Automation,
/// the accessibility API. No keyboard or mouse input is synthesized, and the app
/// window can stay minimized.
/// </summary>
internal sealed class UiAutomationResolver : IDisposable
{
    private const string ProcessName = "AppleMusic";
    private const string ShuffleId = "ShuffleButton";
    private const string RepeatId = "RepeatButton";
    private const string PlaylistHeaderId = "Sidebar_Header_Playlists";
    private const string PagePlayId = "PlayButton";
    private const string PlaylistIdSuffix = "IKIND:ePlaylist";
    private const string NavigationItemClass = "Microsoft.UI.Xaml.Controls.NavigationViewItem";
    private const string HeaderGroupClass = "NamedContainerAutomationPeer";
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan SettleDelay = TimeSpan.FromMilliseconds(250);
    private static readonly bool Diagnostics = Environment.GetEnvironmentVariable("CLEF_UI_DIAGNOSTICS") == "1";

    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly object _gate = new();
    private IUIAutomation? _automation;
    private IUIAutomationElement? _window;
    private UiSnapshot _snapshot = new(false);
    private bool _disposed;
    private DateTimeOffset _lastErrorLoggedAt = DateTimeOffset.MinValue;

    public event Action? Changed;
    public UiSnapshot Snapshot { get { lock (_gate) return _snapshot; } }

    public Task RefreshAsync() => RunAsync(() => { ReadState(); return null; }, swallow: true);

    public Task SetShuffleAsync(bool? desired) => RunAsync(() =>
    {
        var shuffle = Require(Find(Window(), Uia.AutomationIdProperty, ShuffleId), "shuffle button");
        var toggle = Pattern<IUIAutomationTogglePattern>(shuffle, Uia.TogglePattern);
        toggle.get_CurrentToggleState(out var state);
        var active = state == Uia.ToggleOn;
        if (desired is null || desired.Value != active) toggle.Toggle();
        Thread.Sleep(SettleDelay);
        ReadState();
        return null;
    });

    public Task SetRepeatAsync(string? desired) => RunAsync(() =>
    {
        var window = Window();
        var cycle = desired is null or "cycle";
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var repeat = Require(Find(window, Uia.AutomationIdProperty, RepeatId), "repeat button");
            var current = RepeatModeFromName(Text(repeat, Uia.NameProperty));
            if (!cycle && current == desired) break;
            Pattern<IUIAutomationInvokePattern>(repeat, Uia.InvokePattern).Invoke();
            Thread.Sleep(SettleDelay);
            if (cycle || current == "unknown") break;
        }
        ReadState();
        return null;
    });

    public Task FavoriteAsync() => RunAsync(() =>
    {
        var favorite = Require(FindButtonByName(Window(), "Favorite"), "favorite button");
        Pattern<IUIAutomationInvokePattern>(favorite, Uia.InvokePattern).Invoke();
        return null;
    });

    public Task<object?> ListPlaylistsAsync() => RunAsync(() =>
    {
        var window = Window();
        var items = FindPlaylists(window);
        if (items.Count == 0)
        {
            ExpandPlaylistHeader(window);
            items = FindPlaylists(window);
        }
        return items.Select(item => new { id = item.Id, name = item.Name }).ToArray();
    });

    public Task PlayPlaylistAsync(string id) => RunAsync(() =>
    {
        if (string.IsNullOrWhiteSpace(id) || !id.EndsWith(PlaylistIdSuffix, StringComparison.Ordinal))
            throw new InvalidOperationException("Choose a playlist for this key first.");
        var window = Window();
        var item = Find(window, Uia.AutomationIdProperty, id);
        if (item is null)
        {
            ExpandPlaylistHeader(window);
            item = Require(Find(window, Uia.AutomationIdProperty, id), "playlist");
        }
        var name = Text(item, Uia.NameProperty);
        Pattern<IUIAutomationSelectionItemPattern>(item, Uia.SelectionItemPattern).Select();
        // Wait for the playlist page itself, not a Play button left over from the previous page:
        // the page header is a named group whose name starts with the playlist name.
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(2.5);
        IUIAutomationElement? play = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            Thread.Sleep(120);
            var header = FindHeaderGroup(window, name);
            play = header is null ? null : Find(header, Uia.AutomationIdProperty, PagePlayId);
            if (play is not null) break;
        }
        play ??= Find(window, Uia.AutomationIdProperty, PagePlayId);
        Pattern<IUIAutomationInvokePattern>(Require(play, "playlist play button"), Uia.InvokePattern).Invoke();
        return null;
    });

    /// <summary>Maps the repeat button's accessible name to a mode. English names only.</summary>
    public static string RepeatModeFromName(string? name)
    {
        var text = (name ?? string.Empty).Trim().ToLowerInvariant();
        if (text.Length == 0) return "unknown";
        if (text.StartsWith("do not") || text.StartsWith("don't") || text.Contains("off")) return "off";
        if (text.Contains("one") || text.Contains("song") || text.Contains("track")) return "one";
        if (text.Contains("all") || text == "repeat") return "all";
        return "unknown";
    }

    private async Task<object?> RunAsync(Func<object?> operation, bool swallow = false)
    {
        if (_disposed) return null;
        await _lock.WaitAsync();
        try
        {
            var work = Task.Run(operation);
            var completed = await Task.WhenAny(work, Task.Delay(OperationTimeout));
            if (completed != work)
            {
                Invalidate();
                throw new TimeoutException("Apple Music for Windows did not respond to the interface request.");
            }
            return await work;
        }
        catch (InvalidOperationException) when (swallow)
        {
            SetSnapshot(new(false));
            return null;
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // UIA surfaces failures as COMException, InvalidCastException, UnauthorizedAccessException,
            // ArgumentException, and more; none of them may escape into the helper's watchdog.
            Invalidate();
            SetSnapshot(new(false));
            if (!swallow) throw new InvalidOperationException(ex is TimeoutException ? ex.Message : "Apple Music for Windows interface is unavailable.", ex);
            LogThrottled($"UI automation refresh: {ex.Message}");
            return null;
        }
        finally
        {
            _lock.Release();
        }
    }

    private void ReadState()
    {
        if (!ProcessRunning())
        {
            Invalidate();
            SetSnapshot(new(false));
            return;
        }
        var window = Window();
        var shuffle = Find(window, Uia.AutomationIdProperty, ShuffleId);
        var repeat = Find(window, Uia.AutomationIdProperty, RepeatId);
        if (shuffle is null && repeat is null)
        {
            Invalidate();
            SetSnapshot(new(false));
            return;
        }
        bool? shuffleActive = null;
        if (shuffle is not null)
        {
            Pattern<IUIAutomationTogglePattern>(shuffle, Uia.TogglePattern).get_CurrentToggleState(out var state);
            shuffleActive = state == Uia.ToggleOn;
        }
        var repeatMode = repeat is null ? null : RepeatModeFromName(Text(repeat, Uia.NameProperty));
        SetSnapshot(new(true, shuffleActive, repeatMode));
    }

    private IUIAutomationElement Window()
    {
        if (_window is not null)
        {
            try
            {
                _window.GetCurrentPropertyValue(Uia.ProcessIdProperty, out var pid);
                if (pid is int id && id > 0 && !ProcessExited(id)) return _window;
            }
            catch (COMException) { }
            Invalidate();
        }
        var automation = _automation ??= (IUIAutomation)(object)new CUIAutomationComObject();
        automation.GetRootElement(out var root);
        foreach (var process in Process.GetProcessesByName(ProcessName))
        {
            using (process)
            {
                automation.CreatePropertyCondition(Uia.ProcessIdProperty, process.Id, out var condition);
                root.FindFirst(Uia.TreeScopeChildren, condition, out var window);
                if (window is null) continue;
                if (Find(window, Uia.AutomationIdProperty, ShuffleId) is null &&
                    Find(window, Uia.AutomationIdProperty, RepeatId) is null) continue;
                _window = window!;
                if (Diagnostics) Trace($"Bound Apple Music window for process {process.Id}.");
                return window;
            }
        }
        throw new InvalidOperationException("Apple Music for Windows window is not open.");
    }

    private static bool ProcessRunning()
    {
        var processes = Process.GetProcessesByName(ProcessName);
        try { return processes.Length > 0; }
        finally { foreach (var process in processes) process.Dispose(); }
    }

    private static bool ProcessExited(int id)
    {
        try
        {
            using var process = Process.GetProcessById(id);
            return process.HasExited;
        }
        catch (ArgumentException) { return true; }
    }

    private IUIAutomationElement? Find(IUIAutomationElement parent, int propertyId, object value)
    {
        _automation!.CreatePropertyCondition(propertyId, value, out var condition);
        parent.FindFirst(Uia.TreeScopeDescendants, condition, out var found);
        return found;
    }

    private IUIAutomationElement? FindButtonByName(IUIAutomationElement parent, string name)
    {
        _automation!.CreatePropertyCondition(Uia.NameProperty, name, out var byName);
        _automation.CreatePropertyCondition(Uia.ControlTypeProperty, Uia.ButtonControlType, out var byType);
        _automation.CreateAndCondition(byName, byType, out var condition);
        parent.FindFirst(Uia.TreeScopeDescendants, condition, out var found);
        return found;
    }

    private List<(string Id, string Name)> FindPlaylists(IUIAutomationElement window)
    {
        var result = new List<(string, string)>();
        _automation!.CreatePropertyCondition(Uia.ClassNameProperty, NavigationItemClass, out var condition);
        window.FindAll(Uia.TreeScopeDescendants, condition, out var items);
        if (items is null) return result;
        items.get_Length(out var length);
        for (var i = 0; i < length; i++)
        {
            items.GetElement(i, out var item);
            var id = Text(item, Uia.AutomationIdProperty);
            if (!id.EndsWith(PlaylistIdSuffix, StringComparison.Ordinal)) continue;
            var name = Text(item, Uia.NameProperty);
            if (name.Length == 0) continue;
            result.Add((id, name));
        }
        return result;
    }

    private IUIAutomationElement? FindHeaderGroup(IUIAutomationElement window, string playlistName)
    {
        if (playlistName.Length == 0) return null;
        _automation!.CreatePropertyCondition(Uia.ClassNameProperty, HeaderGroupClass, out var condition);
        window.FindAll(Uia.TreeScopeDescendants, condition, out var groups);
        if (groups is null) return null;
        groups.get_Length(out var length);
        for (var i = 0; i < length; i++)
        {
            groups.GetElement(i, out var group);
            if (Text(group, Uia.NameProperty).StartsWith(playlistName, StringComparison.Ordinal)) return group;
        }
        return null;
    }

    private void ExpandPlaylistHeader(IUIAutomationElement window)
    {
        var header = Find(window, Uia.AutomationIdProperty, PlaylistHeaderId);
        if (header is null) return;
        header.GetCurrentPattern(Uia.ExpandCollapsePattern, out var pattern);
        if (pattern is IUIAutomationExpandCollapsePattern expand)
        {
            expand.Expand();
            Thread.Sleep(SettleDelay);
        }
    }

    private static T Pattern<T>(IUIAutomationElement element, int patternId) where T : class
    {
        element.GetCurrentPattern(patternId, out var pattern);
        return pattern as T ?? throw new InvalidOperationException("Apple Music for Windows control does not support this action.");
    }

    private static string Text(IUIAutomationElement element, int propertyId)
    {
        element.GetCurrentPropertyValue(propertyId, out var value);
        return value as string ?? string.Empty;
    }

    private static IUIAutomationElement Require(IUIAutomationElement? element, string what) =>
        element ?? throw new InvalidOperationException($"Apple Music for Windows {what} was not found.");

    private void Invalidate() => _window = null;

    private void SetSnapshot(UiSnapshot snapshot)
    {
        lock (_gate)
        {
            if (_snapshot == snapshot) return;
            _snapshot = snapshot;
        }
        Changed?.Invoke();
    }

    private void LogThrottled(string message)
    {
        if (DateTimeOffset.UtcNow - _lastErrorLoggedAt < TimeSpan.FromSeconds(30)) return;
        _lastErrorLoggedAt = DateTimeOffset.UtcNow;
        Console.Error.WriteLine(message);
    }

    private static void Trace(string message) => Console.Error.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {message}");

    public void Dispose()
    {
        // A worker abandoned by a timeout may still be running; keep the automation object
        // alive for it and let the process exit reclaim everything.
        _disposed = true;
        _window = null;
    }
}
