using System.Text.Json;

namespace ClefBridge;

internal sealed class BridgeService : IAsyncDisposable
{
    private readonly MediaSessionResolver _media = new();
    private readonly CoreAudioResolver _audio = new();
    private readonly UiAutomationResolver _ui = new();
    private readonly SemaphoreSlim _commandLock = new(1, 1);
    private readonly object _stateGate = new();
    private readonly CancellationTokenSource _lifetime = new();
    private Task? _watchdog;
    private long _revision;
    private string? _lastArtworkKeySent;

    public event Action<BridgeSnapshot>? StateChanged;

    public async Task InitializeAsync()
    {
        _media.Changed += RequestPublish;
        _audio.Changed += RequestPublish;
        _ui.Changed += RequestPublish;
        await _media.InitializeAsync();
        await _audio.InitializeAsync();
        await _ui.RefreshAsync();
        _watchdog = WatchdogAsync(_lifetime.Token);
        Publish();
    }

    public async Task<object?> ExecuteAsync(CommandMessage command)
    {
        await _commandLock.WaitAsync();
        try
        {
            switch (command.Name)
            {
                case "shuffle": await _ui.SetShuffleAsync(Flag(command.Amount)); break;
                case "repeat": await _ui.SetRepeatAsync(command.Target); break;
                case "favorite": await _ui.FavoriteAsync(); break;
                case "playPlaylist": await _ui.PlayPlaylistAsync(command.Target ?? string.Empty); break;
                case "listPlaylists": return await _ui.ListPlaylistsAsync();
                case "toggle": await _media.ToggleAsync(); break;
                case "next": await _media.SkipAsync(true, Count(command.Amount)); break;
                case "previous": await _media.SkipAsync(false, Count(command.Amount)); break;
                case "volume": await _audio.AdjustVolumeAsync(command.Amount ?? 0); break;
                case "toggleMute": await _audio.ToggleMuteAsync(); break;
                case "refresh":
                    await Task.WhenAll(
                        _media.RefreshAsync(forceSessionResolution: true),
                        _audio.RefreshAsync(forceSessionResolution: true),
                        _ui.RefreshAsync());
                    Publish();
                    break;
                default: throw new InvalidOperationException($"Unknown command '{command.Name}'.");
            }
            return null;
        }
        finally
        {
            _commandLock.Release();
        }
    }

    private static int Count(double? value) => Math.Clamp((int)Math.Round(value ?? 1), 1, 20);
    private static bool? Flag(double? value) => value is null ? null : value.Value >= 0.5;

    private void RequestPublish()
    {
        // Resolver event storms are already coalesced at their source. Publishing
        // synchronously here avoids starving a delayed continuation behind the
        // bridge's single-threaded Windows API context.
        Publish();
    }

    private void Publish()
    {
        lock (_stateGate)
        {
            var media = _media.Snapshot;
            if (media.ArtworkKey is not null && media.ArtworkKey == _lastArtworkKeySent)
                media = media with { ArtworkDataUri = null };
            else
                _lastArtworkKeySent = media.ArtworkKey;

            StateChanged?.Invoke(new(
                "state",
                ++_revision,
                DateTimeOffset.UtcNow,
                media,
                _audio.Snapshot,
                _ui.Snapshot));
        }
    }

    private async Task WatchdogAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2.5));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                try
                {
                    await Task.WhenAll(_media.RefreshAsync(), _audio.RefreshAsync(), _ui.RefreshAsync());
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Watchdog refresh: {ex.Message}");
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        if (_watchdog is not null)
        {
            try { await _watchdog; }
            catch (OperationCanceledException) { }
        }
        _media.Dispose();
        _audio.Dispose();
        _ui.Dispose();
        _commandLock.Dispose();
        _lifetime.Dispose();
    }
}
