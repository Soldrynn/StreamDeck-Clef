using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text;
using Windows.Graphics.Imaging;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace ClefBridge;

internal sealed class MediaSessionResolver : IDisposable
{
    private static readonly bool Diagnostics = Environment.GetEnvironmentVariable("CLEF_MEDIA_DIAGNOSTICS") == "1";

    private static void Trace(string message) =>
        Console.Error.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {message}");

    private static readonly TimeSpan ArtworkRetryDuration = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan RapidProbeDuration = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan ArtworkReadTimeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan ArtworkReadStallBudget = TimeSpan.FromMilliseconds(1500);
    private const int MaximumStalledArtworkReads = 3;
    private static readonly TimeSpan ArtworkTransitionSettleDelay = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan PlaybackArtworkPulseDelay = TimeSpan.FromMilliseconds(1250);
    private static readonly TimeSpan PlaybackArtworkPulseRetryDelay = TimeSpan.FromMilliseconds(750);
    private const int MaximumPlaybackArtworkPulseAttempts = 4;
    private static readonly TimeSpan[] ForcedProjectionOffsets =
    [
        TimeSpan.FromMilliseconds(750),
        TimeSpan.FromMilliseconds(1500),
        TimeSpan.FromMilliseconds(2500),
        TimeSpan.FromSeconds(4),
        TimeSpan.FromSeconds(6),
        TimeSpan.FromSeconds(9),
        TimeSpan.FromSeconds(13),
        TimeSpan.FromSeconds(18)
    ];
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private readonly object _requestGate = new();
    private readonly ArtworkAssociationTracker _artworkAssociations = new();
    private readonly List<Task> _stalledArtworkReads = [];
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _selected;
    private MediaSnapshot _snapshot = new(false);
    private string? _title;
    private string? _artist;
    private string? _album;
    private string? _artworkDataUri;
    private string? _artworkKey;
    private ArtworkRequest? _pendingArtwork;
    private bool _artworkLoadRunning;
    private Task? _artworkLoadTask;
    private bool _metadataDirty = true;
    private bool _metadataProbeRequested = true;
    private bool _artworkDirty = true;
    private DateTimeOffset _lastMetadataProbeAt = DateTimeOffset.MinValue;
    private DateTimeOffset _artworkRetryUntil = DateTimeOffset.MinValue;
    private bool _disposed;
    private bool _eventRefreshScheduled;
    private bool _eventSessionResolutionRequested;
    private bool _eventProjectionRefreshRequested;
    private bool _eventPlaybackArtworkPulseRequested;
    private bool _eventMetadataDirty;
    private bool _eventMetadataProbeRequested;
    private DateTimeOffset _lastSessionResolutionAt = DateTimeOffset.MinValue;
    private DateTimeOffset _rapidProbeUntil = DateTimeOffset.MinValue;
    private DateTimeOffset _identityChangedAt = DateTimeOffset.MinValue;
    private DateTimeOffset _artworkFetchNotBefore = DateTimeOffset.MinValue;
    private int _forcedProjectionStage = ForcedProjectionOffsets.Length;
    private bool _armPlaybackArtworkPulseOnIdentityChange;
    private MediaIdentity? _pendingPlaybackArtworkPulseIdentity;
    private bool _playbackArtworkPulseIssued;
    private int _playbackArtworkPulseAttempts;
    private DateTimeOffset _nextPlaybackArtworkPulseAt = DateTimeOffset.MaxValue;
    private MediaIdentity? _expectedOldIdentity;
    private DateTimeOffset _expectedIdentityChangeUntil = DateTimeOffset.MinValue;
    private long _mediaPropertiesGeneration;
    private DateTimeOffset _lastManagerErrorLoggedAt = DateTimeOffset.MinValue;
    private SynchronizationContext? _context;
    private readonly Timer _rapidProbeTimer;

    public event Action? Changed;
    public MediaSnapshot Snapshot => _snapshot;

    public MediaSessionResolver()
    {
        _rapidProbeTimer = new Timer(_ => OnRapidProbeTimer(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public async Task InitializeAsync()
    {
        _context = SynchronizationContext.Current;
        await RefreshAsync();
    }

    public async Task RefreshAsync(
        bool forceSessionResolution = false,
        bool refreshSessionProjection = false,
        bool pulsePlaybackForArtwork = false)
    {
        if (_disposed) return;
        await _refreshLock.WaitAsync();
        try
        {
            if (_manager is null)
            {
                try
                {
                    _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
                    _manager.SessionsChanged += OnSessionsChanged;
                    _manager.CurrentSessionChanged += OnCurrentSessionChanged;
                    forceSessionResolution = true;
                }
                catch (Exception ex)
                {
                    if (DateTimeOffset.UtcNow - _lastManagerErrorLoggedAt > TimeSpan.FromSeconds(30))
                    {
                        Console.Error.WriteLine($"Media session manager unavailable: {ex.Message}");
                        _lastManagerErrorLoggedAt = DateTimeOffset.UtcNow;
                    }
                    SetSnapshot(new(false));
                    return;
                }
            }
            if (forceSessionResolution || refreshSessionProjection || _selected is null ||
                DateTimeOffset.UtcNow - _lastSessionResolutionAt >= TimeSpan.FromSeconds(3))
            {
                var current = _manager.GetCurrentSession();
                GlobalSystemMediaTransportControlsSession? best = null;
                var bestScore = int.MinValue;
                foreach (var session in _manager.GetSessions())
                {
                    GlobalSystemMediaTransportControlsSessionPlaybackInfo? info = null;
                    try { info = session.GetPlaybackInfo(); } catch { }
                    var playing = info?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
                    var score = SessionScorer.ScoreMedia(session.SourceAppUserModelId, ReferenceEquals(session, current), playing);
                    if (score > bestScore)
                    {
                        best = session;
                        bestScore = score;
                    }
                }
                if (bestScore < 80) best = null;
                if (!SameSession(_selected, best)) Bind(best);
                else if (refreshSessionProjection && best is not null) RefreshSessionProjection(best);
                _lastSessionResolutionAt = DateTimeOffset.UtcNow;
            }
            await UpdateSnapshotAsync();
            if (pulsePlaybackForArtwork) await PulsePlaybackForArtworkAsync();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Media session refresh: {ex.Message}");
            _lastSessionResolutionAt = DateTimeOffset.MinValue;
            SetSnapshot(new(false));
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    public async Task ToggleAsync()
    {
        var session = await RequireSessionAsync();
        var status = session.GetPlaybackInfo().PlaybackStatus;
        var accepted = false;
        try
        {
            accepted = status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing
                ? await session.TryPauseAsync()
                : await session.TryPlayAsync();
        }
        catch { }
        if (!accepted) accepted = await session.TryTogglePlayPauseAsync();
        if (!accepted) throw new InvalidOperationException("Apple Music for Windows rejected play/pause.");
        await Task.Delay(60);
        await RefreshAsync();
    }

    public async Task SkipAsync(bool next, int count)
    {
        var session = await RequireSessionAsync();
        count = Math.Clamp(count, 1, 20);
        PrepareForTrackTransition();
        for (var i = 0; i < count; i++)
        {
            var before = new MediaIdentity(_title, _artist, _album);
            var accepted = next
                ? await session.TrySkipNextAsync()
                : await session.TrySkipPreviousAsync();
            if (!accepted) throw new InvalidOperationException(next ? "Apple Music for Windows rejected next track." : "Apple Music for Windows rejected previous track.");
            if (!next && !await WaitForIdentityChangeAsync(before, TimeSpan.FromMilliseconds(400)))
            {
                if (!await session.TrySkipPreviousAsync())
                    throw new InvalidOperationException("Apple Music for Windows rejected previous track.");
            }
            if (i + 1 < count) await Task.Delay(80);
        }
        RequestMetadataRefresh(includeArtwork: true);
        await Task.Delay(100);
        await RefreshAsync();
    }

    private async Task<GlobalSystemMediaTransportControlsSession> RequireSessionAsync()
    {
        await RefreshAsync();
        return _selected ?? throw new InvalidOperationException("Apple Music for Windows media session is unavailable.");
    }

    private void Bind(GlobalSystemMediaTransportControlsSession? session)
    {
        if (_selected is not null)
        {
            _selected.MediaPropertiesChanged -= OnMediaPropertiesChanged;
            _selected.PlaybackInfoChanged -= OnPlaybackInfoChanged;
            _selected.TimelinePropertiesChanged -= OnTimelinePropertiesChanged;
        }
        _selected = session;
        _metadataDirty = true;
        _metadataProbeRequested = true;
        _artworkDirty = true;
        _lastMetadataProbeAt = DateTimeOffset.MinValue;
        _artworkRetryUntil = DateTimeOffset.UtcNow + ArtworkRetryDuration;
        _pendingArtwork = null;
        _expectedOldIdentity = null;
        CancelPlaybackArtworkPulse();
        _title = _artist = _album = _artworkDataUri = _artworkKey = null;
        if (_selected is not null)
        {
            _selected.MediaPropertiesChanged += OnMediaPropertiesChanged;
            _selected.PlaybackInfoChanged += OnPlaybackInfoChanged;
            _selected.TimelinePropertiesChanged += OnTimelinePropertiesChanged;
        }
    }

    private async Task UpdateSnapshotAsync()
    {
        var session = _selected;
        if (session is null)
        {
            SetSnapshot(new(false));
            return;
        }

        var now = DateTimeOffset.UtcNow;
        if (_armPlaybackArtworkPulseOnIdentityChange && now >= _expectedIdentityChangeUntil)
            _armPlaybackArtworkPulseOnIdentityChange = false;
        var metadataProbeAge = now - _lastMetadataProbeAt;
        var shouldProbeMetadata = _metadataDirty
            || (_metadataProbeRequested && metadataProbeAge >= TimeSpan.FromMilliseconds(500))
            || metadataProbeAge >= TimeSpan.FromSeconds(2);
        if (shouldProbeMetadata)
        {
            _metadataProbeRequested = false;
            _lastMetadataProbeAt = now;
            try
            {
                var properties = await session.TryGetMediaPropertiesAsync();
                var previousIdentity = new MediaIdentity(_title, _artist, _album);
                var nextIdentity = new MediaIdentity(properties.Title, properties.Artist, properties.AlbumTitle);
                var identityChanged = previousIdentity != nextIdentity;
                _title = nextIdentity.Title;
                _artist = nextIdentity.Artist;
                _album = nextIdentity.Album;
                _metadataDirty = false;

                if (identityChanged)
                {
                    var generation = Volatile.Read(ref _mediaPropertiesGeneration);
                    var hadPreviousTrack = HasMeaningfulIdentity(previousIdentity);
                    var shouldArmPlaybackPulse = _armPlaybackArtworkPulseOnIdentityChange &&
                        _expectedOldIdentity == previousIdentity &&
                        now < _expectedIdentityChangeUntil;
                    _artworkAssociations.ChangeIdentity(
                        IdentityKey(nextIdentity),
                        ArtworkGroupKey(nextIdentity),
                        hadPreviousTrack,
                        generation,
                        now);
                    if (_expectedOldIdentity == previousIdentity) _expectedOldIdentity = null;
                    _artworkDirty = true;
                    _artworkRetryUntil = now + ArtworkRetryDuration;
                    _artworkDataUri = null;
                    _artworkKey = null;
                    ScheduleForcedArtworkFetch(now, hadPreviousTrack);
                    if (shouldArmPlaybackPulse) ArmPlaybackArtworkPulse(nextIdentity);
                    _armPlaybackArtworkPulseOnIdentityChange = false;
                }

                if (_artworkDirty && now > _artworkRetryUntil) _artworkDirty = false;
                if (Diagnostics)
                    Trace($"Probe identityChanged={identityChanged} dirty={_artworkDirty} " +
                          $"haveArt={_artworkDataUri is not null} thumb={properties.Thumbnail is not null} " +
                          $"retryIn={(_artworkRetryUntil - now).TotalSeconds:F1}s title='{nextIdentity.Title}'");
                if (_artworkDirty)
                {
                    QueueArtwork(
                        properties.Thumbnail,
                        nextIdentity,
                        Volatile.Read(ref _mediaPropertiesGeneration));
                }
            }
            catch (Exception ex)
            {
                _metadataDirty = true;
                Console.Error.WriteLine($"Media metadata: {ex.Message}");
            }
        }

        var info = session.GetPlaybackInfo();
        var timeline = session.GetTimelineProperties();
        var status = info.PlaybackStatus switch
        {
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing => "playing",
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused => "paused",
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Stopped => "stopped",
            _ => "unknown"
        };
        var start = timeline.StartTime;
        var end = timeline.EndTime;
        var duration = end > start ? end - start : TimeSpan.Zero;
        var position = timeline.Position > start ? timeline.Position - start : TimeSpan.Zero;
        SetSnapshot(new(
            true,
            session.SourceAppUserModelId,
            _title,
            _artist,
            _album,
            status,
            (long)position.TotalMilliseconds,
            duration > TimeSpan.Zero ? (long)duration.TotalMilliseconds : null,
            _artworkDataUri,
            _artworkKey));
    }

    private static async Task<(string? DataUri, string? Key, bool Unanswered)> ReadArtworkAsync(
        IRandomAccessStreamReference? reference)
    {
        if (reference is null) return (null, null, false);
        using var timeout = new CancellationTokenSource(ArtworkReadTimeout);
        try
        {
            var (dataUri, key) = await ReadArtworkCoreAsync(reference, timeout.Token);
            return (dataUri, key, false);
        }
        catch (OperationCanceledException)
        {
            return (null, null, true);
        }
    }

    private static async Task<(string? DataUri, string? Key)> ReadArtworkCoreAsync(
        IRandomAccessStreamReference reference,
        CancellationToken cancellationToken)
    {
        using var stream = await reference.OpenReadAsync().AsTask(cancellationToken);
        if (stream.Size > 8 * 1024 * 1024) return (null, null);

        var decoder = await BitmapDecoder.CreateAsync(stream).AsTask(cancellationToken);
        if (decoder.PixelWidth == 0 || decoder.PixelHeight == 0) return (null, null);
        if (decoder.PixelWidth > 4096 || decoder.PixelHeight > 4096) return (null, null);
        const uint maximumDimension = 72;
        var scale = Math.Min(1d, maximumDimension / (double)Math.Max(decoder.PixelWidth, decoder.PixelHeight));
        var width = Math.Max(1u, (uint)Math.Round(decoder.PixelWidth * scale));
        var height = Math.Max(1u, (uint)Math.Round(decoder.PixelHeight * scale));
        var transform = new BitmapTransform
        {
            ScaledWidth = width,
            ScaledHeight = height,
            InterpolationMode = BitmapInterpolationMode.Fant
        };
        var pixelData = await decoder.GetPixelDataAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Premultiplied,
            transform,
            ExifOrientationMode.RespectExifOrientation,
            ColorManagementMode.ColorManageToSRgb).AsTask(cancellationToken);
        var pixels = pixelData.DetachPixelData();

        using var output = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, output).AsTask(cancellationToken);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, width, height, 96, 96, pixels);
        await encoder.FlushAsync().AsTask(cancellationToken);
        if (output.Size == 0 || output.Size > 24 * 1024) return (null, null);

        var bytes = new byte[(int)output.Size];
        output.Seek(0);
        using var reader = new DataReader(output.GetInputStreamAt(0));
        var loaded = await reader.LoadAsync((uint)bytes.Length).AsTask(cancellationToken);
        if (loaded != bytes.Length) return (null, null);
        reader.ReadBytes(bytes);
        var hash = Convert.ToHexString(SHA256.HashData(bytes))[..16].ToLowerInvariant();
        return ($"data:image/png;base64,{Convert.ToBase64String(bytes)}", hash);
    }

    private void QueueArtwork(
        IRandomAccessStreamReference? reference,
        MediaIdentity identity,
        long mediaPropertiesGeneration)
    {
        _pendingArtwork = new(
            reference,
            identity,
            IdentityKey(identity),
            ArtworkGroupKey(identity),
            mediaPropertiesGeneration);
        if (Diagnostics) Trace($"Artwork queued running={_artworkLoadRunning} reference={reference is not null}");
        if (_artworkLoadRunning || _disposed) return;
        _artworkLoadRunning = true;
        _artworkLoadTask = DrainArtworkAsync();
    }

    private async Task DrainArtworkAsync()
    {
        await Task.Yield();
        try
        {
            while (!_disposed)
            {
                var request = _pendingArtwork;
                if (request is null) return;
                if (!MatchesCurrentIdentity(request.Identity) || !_artworkDirty)
                {
                    if (Diagnostics)
                        Trace($"Artwork drain skip current={MatchesCurrentIdentity(request.Identity)} dirty={_artworkDirty}");
                    if (ReferenceEquals(_pendingArtwork, request)) _pendingArtwork = null;
                    continue;
                }
                var delay = _artworkFetchNotBefore - DateTimeOffset.UtcNow;
                if (delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay);
                    continue;
                }
                if (!ReferenceEquals(_pendingArtwork, request)) continue;
                _pendingArtwork = null;

                var attempt = AttemptArtworkAsync(request);
                var settled = await Task.WhenAny(attempt, Task.Delay(ArtworkReadStallBudget));
                if (!ReferenceEquals(settled, attempt)) await RetainStalledReadAsync(attempt);
            }
        }
        finally
        {
            _artworkLoadRunning = false;
            if (!_disposed && _pendingArtwork is not null)
            {
                _artworkLoadRunning = true;
                _artworkLoadTask = DrainArtworkAsync();
            }
        }
    }

    private async Task AttemptArtworkAsync(ArtworkRequest request)
    {
        try
        {
            var startedAt = DateTimeOffset.UtcNow;
            var (dataUri, key, unanswered) = await ReadArtworkAsync(request.Reference);
            if (Diagnostics)
                Trace($"Artwork read took={(DateTimeOffset.UtcNow - startedAt).TotalMilliseconds:F0}ms " +
                      $"hash={key ?? (unanswered ? "<unanswered>" : "<none>")} " +
                      $"current={MatchesCurrentIdentity(request.Identity)} group={request.GroupKey}");
            if (_disposed || !MatchesCurrentIdentity(request.Identity)) return;
            if (key is not null)
            {
                if (_expectedOldIdentity == request.Identity &&
                    DateTimeOffset.UtcNow < _expectedIdentityChangeUntil)
                    return;
                if (!_artworkAssociations.ShouldAccept(
                        key,
                        request.IdentityKey,
                        request.GroupKey,
                        request.MediaPropertiesGeneration,
                        DateTimeOffset.UtcNow))
                {
                    if (Diagnostics) Trace($"Artwork held back hash={key}");
                    return;
                }

                _artworkAssociations.Commit(key, request.GroupKey);
                _artworkDataUri = dataUri;
                _artworkKey = CompositeArtworkKey(request.IdentityKey, key);
                _artworkDirty = false;
                CompleteForcedArtworkFetch();
                if (_pendingArtwork?.Identity == request.Identity) _pendingArtwork = null;
                if (SnapshotMatchesIdentity(request.Identity))
                    SetSnapshot(_snapshot with { ArtworkDataUri = dataUri, ArtworkKey = _artworkKey });
            }
            else if (unanswered)
            {
                _artworkRetryUntil = DateTimeOffset.UtcNow + ArtworkRetryDuration;
            }
            else if (DateTimeOffset.UtcNow >= _artworkRetryUntil)
            {
                _artworkDirty = false;
            }
        }
        catch (Exception ex)
        {
            if (!_disposed) Console.Error.WriteLine($"Media artwork: {ex.Message}");
        }
    }

    private async Task RetainStalledReadAsync(Task attempt)
    {
        _stalledArtworkReads.RemoveAll(task => task.IsCompleted);
        _stalledArtworkReads.Add(attempt);
        if (_stalledArtworkReads.Count < MaximumStalledArtworkReads) return;
        var finished = await Task.WhenAny(_stalledArtworkReads);
        _stalledArtworkReads.Remove(finished);
    }

    private bool MatchesCurrentIdentity(MediaIdentity identity) =>
        identity == new MediaIdentity(_title, _artist, _album);

    private bool SnapshotMatchesIdentity(MediaIdentity identity) =>
        _snapshot.Available && identity == new MediaIdentity(_snapshot.Title, _snapshot.Artist, _snapshot.Album);

    private void PrepareForTrackTransition()
    {
        var identity = new MediaIdentity(_title, _artist, _album);
        if (!HasMeaningfulIdentity(identity)) return;
        _expectedOldIdentity = identity;
        _expectedIdentityChangeUntil = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5);
        _armPlaybackArtworkPulseOnIdentityChange = true;
        _pendingArtwork = null;
        _artworkDirty = true;
        _artworkRetryUntil = DateTimeOffset.UtcNow + ArtworkRetryDuration;
        CancelPlaybackArtworkPulse();
        ScheduleForcedArtworkFetch(DateTimeOffset.UtcNow, hadPreviousTrack: true);
    }

    private async Task<bool> WaitForIdentityChangeAsync(MediaIdentity before, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        do
        {
            _metadataDirty = true;
            _metadataProbeRequested = true;
            await RefreshAsync();
            if (!MatchesCurrentIdentity(before)) return true;
            await Task.Delay(80);
        }
        while (DateTimeOffset.UtcNow < deadline);
        return !MatchesCurrentIdentity(before);
    }

    private void ArmPlaybackArtworkPulse(MediaIdentity identity)
    {
        lock (_requestGate)
        {
            _pendingPlaybackArtworkPulseIdentity = identity;
            _playbackArtworkPulseIssued = false;
            _playbackArtworkPulseAttempts = 0;
            _nextPlaybackArtworkPulseAt = DateTimeOffset.UtcNow + PlaybackArtworkPulseDelay;
        }
    }

    private async Task PulsePlaybackForArtworkAsync()
    {
        MediaIdentity? expectedIdentity;
        lock (_requestGate) expectedIdentity = _pendingPlaybackArtworkPulseIdentity;
        if (expectedIdentity is null || !_artworkDirty || _artworkDataUri is not null ||
            !MatchesCurrentIdentity(expectedIdentity))
        {
            if (expectedIdentity is not null) FinishPlaybackArtworkPulseAttempt(expectedIdentity, retry: false);
            return;
        }

        var artworkTask = _artworkLoadTask;
        if (artworkTask is not null)
        {
            try { await artworkTask; } catch { }
        }
        if (!_artworkDirty || _artworkDataUri is not null ||
            !MatchesCurrentIdentity(expectedIdentity))
        {
            FinishPlaybackArtworkPulseAttempt(expectedIdentity, retry: false);
            return;
        }

        var session = _selected;
        if (session is null)
        {
            FinishPlaybackArtworkPulseAttempt(expectedIdentity, retry: true);
            return;
        }
        GlobalSystemMediaTransportControlsSessionPlaybackStatus status;
        try { status = session.GetPlaybackInfo().PlaybackStatus; }
        catch
        {
            FinishPlaybackArtworkPulseAttempt(expectedIdentity, retry: true);
            return;
        }
        if (status != GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
        {
            FinishPlaybackArtworkPulseAttempt(expectedIdentity, retry: true);
            return;
        }

        var paused = false;
        try
        {
            paused = await session.TryPauseAsync();
            if (!paused)
            {
                FinishPlaybackArtworkPulseAttempt(expectedIdentity, retry: true);
                return;
            }
            await WaitForPlaybackStatusAsync(
                session,
                GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused,
                TimeSpan.FromMilliseconds(450));
            await Task.Delay(140);

            _metadataDirty = true;
            _metadataProbeRequested = true;
            _artworkDirty = true;
            await UpdateSnapshotAsync();
            var pausedArtworkTask = _artworkLoadTask;
            if (pausedArtworkTask is not null)
            {
                try { await pausedArtworkTask; } catch { }
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Artwork playback refresh pause: {ex.Message}");
            FinishPlaybackArtworkPulseAttempt(expectedIdentity, retry: true);
            return;
        }
        finally
        {
            if (paused)
            {
                try
                {
                    if (!await session.TryPlayAsync())
                    {
                        await Task.Delay(40);
                        await session.TryPlayAsync();
                    }
                    await WaitForPlaybackStatusAsync(
                        session,
                        GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
                        TimeSpan.FromMilliseconds(450));
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Artwork playback refresh resume: {ex.Message}");
                }
            }
        }

        lock (_requestGate) _pendingPlaybackArtworkPulseIdentity = null;
        _metadataDirty = true;
        _metadataProbeRequested = true;
        if (_artworkDataUri is null)
        {
            _artworkDirty = true;
            _artworkRetryUntil = DateTimeOffset.UtcNow + ArtworkRetryDuration;
        }
        await Task.Delay(380);
        await UpdateSnapshotAsync();
        FinishPlaybackArtworkPulseAttempt(
            expectedIdentity,
            retry: _artworkDataUri is null && _artworkDirty && MatchesCurrentIdentity(expectedIdentity));
    }

    private void FinishPlaybackArtworkPulseAttempt(MediaIdentity identity, bool retry)
    {
        lock (_requestGate)
        {
            if (_pendingPlaybackArtworkPulseIdentity != identity) return;
            if (retry && _playbackArtworkPulseAttempts < MaximumPlaybackArtworkPulseAttempts &&
                DateTimeOffset.UtcNow < _rapidProbeUntil)
            {
                _playbackArtworkPulseIssued = false;
                _nextPlaybackArtworkPulseAt = DateTimeOffset.UtcNow + PlaybackArtworkPulseRetryDelay;
                return;
            }
            _pendingPlaybackArtworkPulseIdentity = null;
            _playbackArtworkPulseIssued = false;
            _playbackArtworkPulseAttempts = 0;
            _nextPlaybackArtworkPulseAt = DateTimeOffset.MaxValue;
        }
    }

    private void CancelPlaybackArtworkPulse()
    {
        lock (_requestGate)
        {
            _pendingPlaybackArtworkPulseIdentity = null;
            _playbackArtworkPulseIssued = false;
            _playbackArtworkPulseAttempts = 0;
            _nextPlaybackArtworkPulseAt = DateTimeOffset.MaxValue;
        }
    }

    private static async Task WaitForPlaybackStatusAsync(
        GlobalSystemMediaTransportControlsSession session,
        GlobalSystemMediaTransportControlsSessionPlaybackStatus desired,
        TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        do
        {
            try
            {
                if (session.GetPlaybackInfo().PlaybackStatus == desired) return;
            }
            catch { return; }
            await Task.Delay(30);
        }
        while (DateTimeOffset.UtcNow < deadline);
    }

    private void RefreshSessionProjection(GlobalSystemMediaTransportControlsSession session)
    {
        if (_selected is not null)
        {
            _selected.MediaPropertiesChanged -= OnMediaPropertiesChanged;
            _selected.PlaybackInfoChanged -= OnPlaybackInfoChanged;
            _selected.TimelinePropertiesChanged -= OnTimelinePropertiesChanged;
        }
        _selected = session;
        _selected.MediaPropertiesChanged += OnMediaPropertiesChanged;
        _selected.PlaybackInfoChanged += OnPlaybackInfoChanged;
        _selected.TimelinePropertiesChanged += OnTimelinePropertiesChanged;
        _metadataDirty = true;
        _metadataProbeRequested = true;
    }

    private static bool HasMeaningfulIdentity(MediaIdentity identity) =>
        !string.IsNullOrWhiteSpace(identity.Artist) ||
        !string.IsNullOrWhiteSpace(identity.Album) ||
        (!string.IsNullOrWhiteSpace(identity.Title) &&
         !string.Equals(identity.Title.Trim(), "Apple Music", StringComparison.OrdinalIgnoreCase));

    private static string IdentityKey(MediaIdentity identity) =>
        string.Join('\u001f', Normalize(identity.Title), Normalize(identity.Artist), Normalize(identity.Album));

    private static string ArtworkGroupKey(MediaIdentity identity)
    {
        var album = Normalize(identity.Album);
        return album.Length > 0
            ? $"album:{album}"
            : $"track:{Normalize(identity.Artist)}|{Normalize(identity.Title)}";
    }

    private static string CompositeArtworkKey(string identityKey, string contentHash)
    {
        var identityHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identityKey)))[..12].ToLowerInvariant();
        return $"{identityHash}-{contentHash}";
    }

    private static string Normalize(string? value) => value?.Trim().ToUpperInvariant() ?? string.Empty;

    private void SetSnapshot(MediaSnapshot snapshot)
    {
        if (_snapshot == snapshot) return;
        _snapshot = snapshot;
        Changed?.Invoke();
    }

    private void PostRefresh(
        bool resolveSession = false,
        bool refreshSessionProjection = false,
        bool pulsePlaybackForArtwork = false,
        bool metadataDirty = false,
        bool probeMetadata = false)
    {
        lock (_requestGate)
        {
            _eventSessionResolutionRequested |= resolveSession;
            _eventProjectionRefreshRequested |= refreshSessionProjection;
            _eventPlaybackArtworkPulseRequested |= pulsePlaybackForArtwork;
            _eventMetadataDirty |= metadataDirty;
            _eventMetadataProbeRequested |= probeMetadata;
            if (_eventRefreshScheduled || _disposed) return;
            _eventRefreshScheduled = true;
        }

        void Start() => _ = DrainEventRefreshesAsync();
        if (_context is null || ReferenceEquals(SynchronizationContext.Current, _context)) Start();
        else _context.Post(_ => Start(), null);
    }

    private async Task DrainEventRefreshesAsync()
    {
        while (!_disposed)
        {
            bool resolveSession;
            bool refreshSessionProjection;
            bool pulsePlaybackForArtwork;
            bool metadataDirty;
            bool probeMetadata;
            lock (_requestGate)
            {
                resolveSession = _eventSessionResolutionRequested;
                refreshSessionProjection = _eventProjectionRefreshRequested;
                pulsePlaybackForArtwork = _eventPlaybackArtworkPulseRequested;
                metadataDirty = _eventMetadataDirty;
                probeMetadata = _eventMetadataProbeRequested;
                _eventSessionResolutionRequested = false;
                _eventProjectionRefreshRequested = false;
                _eventPlaybackArtworkPulseRequested = false;
                _eventMetadataDirty = false;
                _eventMetadataProbeRequested = false;
            }

            if (metadataDirty) RequestMetadataRefresh(includeArtwork: true);
            else if (probeMetadata) _metadataProbeRequested = true;
            await RefreshAsync(
                forceSessionResolution: resolveSession,
                refreshSessionProjection: refreshSessionProjection,
                pulsePlaybackForArtwork: pulsePlaybackForArtwork);

            lock (_requestGate)
            {
                if (_eventSessionResolutionRequested || _eventProjectionRefreshRequested ||
                    _eventPlaybackArtworkPulseRequested ||
                    _eventMetadataDirty || _eventMetadataProbeRequested) continue;
                _eventRefreshScheduled = false;
                return;
            }
        }
    }

    private void OnSessionsChanged(GlobalSystemMediaTransportControlsSessionManager sender, SessionsChangedEventArgs args) => PostRefresh(resolveSession: true);
    private void OnCurrentSessionChanged(GlobalSystemMediaTransportControlsSessionManager sender, CurrentSessionChangedEventArgs args) => PostRefresh(resolveSession: true);
    private void OnMediaPropertiesChanged(GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs args)
    {
        Interlocked.Increment(ref _mediaPropertiesGeneration);
        PostRefresh(metadataDirty: true);
    }
    private void OnPlaybackInfoChanged(GlobalSystemMediaTransportControlsSession sender, PlaybackInfoChangedEventArgs args) => PostRefresh();
    private void OnTimelinePropertiesChanged(GlobalSystemMediaTransportControlsSession sender, TimelinePropertiesChangedEventArgs args)
        => PostRefresh(probeMetadata: true);

    private void RequestMetadataRefresh(bool includeArtwork)
    {
        _metadataDirty = true;
        _metadataProbeRequested = true;
        StartRapidMetadataProbes();
        if (!includeArtwork) return;
        _artworkDirty = true;
        _artworkRetryUntil = DateTimeOffset.UtcNow + ArtworkRetryDuration;
    }

    private void StartRapidMetadataProbes()
    {
        lock (_requestGate)
        {
            _rapidProbeUntil = DateTimeOffset.UtcNow + RapidProbeDuration;
            if (!_disposed) _rapidProbeTimer.Change(250, 250);
        }
    }

    private void ScheduleForcedArtworkFetch(DateTimeOffset now, bool hadPreviousTrack)
    {
        lock (_requestGate)
        {
            _identityChangedAt = now;
            _artworkFetchNotBefore = hadPreviousTrack ? now + ArtworkTransitionSettleDelay : now;
            _forcedProjectionStage = 0;
            _rapidProbeUntil = now + RapidProbeDuration;
            if (!_disposed) _rapidProbeTimer.Change(250, 250);
        }
    }

    private void CompleteForcedArtworkFetch()
    {
        lock (_requestGate)
        {
            _forcedProjectionStage = ForcedProjectionOffsets.Length;
            _pendingPlaybackArtworkPulseIdentity = null;
            _playbackArtworkPulseIssued = false;
            _playbackArtworkPulseAttempts = 0;
            _nextPlaybackArtworkPulseAt = DateTimeOffset.MaxValue;
            _rapidProbeUntil = DateTimeOffset.MinValue;
            try { _rapidProbeTimer.Change(Timeout.Infinite, Timeout.Infinite); } catch { }
        }
    }

    private void OnRapidProbeTimer()
    {
        var stop = false;
        var refreshProjection = false;
        var pulsePlaybackForArtwork = false;
        var now = DateTimeOffset.UtcNow;
        lock (_requestGate)
        {
            if (_forcedProjectionStage < ForcedProjectionOffsets.Length &&
                now >= _identityChangedAt + ForcedProjectionOffsets[_forcedProjectionStage])
            {
                _forcedProjectionStage++;
                refreshProjection = true;
            }
            if (!_playbackArtworkPulseIssued && _pendingPlaybackArtworkPulseIdentity is not null &&
                _playbackArtworkPulseAttempts < MaximumPlaybackArtworkPulseAttempts &&
                now >= _nextPlaybackArtworkPulseAt)
            {
                _playbackArtworkPulseIssued = true;
                _playbackArtworkPulseAttempts++;
                pulsePlaybackForArtwork = true;
            }
            if (_disposed || now >= _rapidProbeUntil)
            {
                stop = true;
                try { _rapidProbeTimer.Change(Timeout.Infinite, Timeout.Infinite); } catch { }
            }
        }
        if (!stop) PostRefresh(
            refreshSessionProjection: refreshProjection,
            pulsePlaybackForArtwork: pulsePlaybackForArtwork,
            probeMetadata: true);
    }

    private static bool SameSession(GlobalSystemMediaTransportControlsSession? left, GlobalSystemMediaTransportControlsSession? right)
    {
        if (ReferenceEquals(left, right)) return true;
        if (left is null || right is null) return false;
        IntPtr leftIdentity = IntPtr.Zero;
        IntPtr rightIdentity = IntPtr.Zero;
        try
        {
            leftIdentity = Marshal.GetIUnknownForObject(left);
            rightIdentity = Marshal.GetIUnknownForObject(right);
            return leftIdentity == rightIdentity;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (leftIdentity != IntPtr.Zero) Marshal.Release(leftIdentity);
            if (rightIdentity != IntPtr.Zero) Marshal.Release(rightIdentity);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _rapidProbeTimer.Dispose();
        _pendingArtwork = null;
        Bind(null);
        if (_manager is not null)
        {
            _manager.SessionsChanged -= OnSessionsChanged;
            _manager.CurrentSessionChanged -= OnCurrentSessionChanged;
        }
        _refreshLock.Dispose();
    }

    private sealed record MediaIdentity(string? Title, string? Artist, string? Album);
    private sealed record ArtworkRequest(
        IRandomAccessStreamReference? Reference,
        MediaIdentity Identity,
        string IdentityKey,
        string GroupKey,
        long MediaPropertiesGeneration);
}
