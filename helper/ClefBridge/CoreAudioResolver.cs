using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ClefBridge;

internal sealed class CoreAudioResolver : IDisposable
{
    private static readonly Guid EventContext = new("50D4180C-EC12-42CB-A2B8-4E4F56DB993B");
    private static readonly TimeSpan SessionResolutionInterval = TimeSpan.FromSeconds(2);
    private readonly object _gate = new();
    private readonly object _postGate = new();
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private readonly EndpointNotifications _endpointNotifications;
    private readonly SessionNotifications _sessionNotifications;
    private readonly List<ManagerBinding> _sessionManagers = [];
    private readonly HashSet<string> _audibleSessionIds = new(StringComparer.Ordinal);
    private readonly GraceRetainer<SelectedSession> _retiredSessions = new(TimeSpan.FromSeconds(30), 32, session => session.Release());
    private readonly GraceRetainer<object> _retiredComObjects = new(TimeSpan.FromSeconds(30), 64, ReleaseCom);
    private IMMDeviceEnumerator? _deviceEnumerator;
    private SelectedSession? _selected;
    private AudioSnapshot _snapshot = new(false);
    private bool _disposed;
    private bool _operationPosted;
    private bool _refreshRequested;
    private bool _resetRequested;
    private bool _selectedInvalidated;
    private DateTimeOffset _lastManagerResetRequestedAt = DateTimeOffset.MinValue;
    private DateTimeOffset _lastSessionResolutionAt = DateTimeOffset.MinValue;
    private SynchronizationContext? _context;

    public event Action? Changed;
    public AudioSnapshot Snapshot { get { lock (_gate) return _snapshot; } }

    public CoreAudioResolver()
    {
        _endpointNotifications = new EndpointNotifications(RequestManagerReset);
        _sessionNotifications = new SessionNotifications(() => RequestOperation(reset: false));
    }

    public async Task InitializeAsync()
    {
        _context = SynchronizationContext.Current;
        await ResetAsync();
    }

    public async Task RefreshAsync(bool forceSessionResolution = false)
    {
        if (_disposed) return;
        await _refreshLock.WaitAsync();
        try
        {
            _retiredSessions.Trim();
            _retiredComObjects.Trim();
            if (_sessionManagers.Count == 0)
            {
                InitializeManagers();
                forceSessionResolution = true;
            }
            if (forceSessionResolution || _selectedInvalidated || _selected is null ||
                DateTimeOffset.UtcNow - _lastSessionResolutionAt >= SessionResolutionInterval)
            {
                var candidate = FindBestSession();
                Bind(candidate);
                _lastSessionResolutionAt = DateTimeOffset.UtcNow;
            }
            UpdateSnapshot();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Core Audio refresh: {ex.Message}");
            SetSnapshot(new(false));
            RequestManagerReset();
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    public async Task AdjustVolumeAsync(double deltaPercentagePoints)
    {
        await RefreshAsync(forceSessionResolution: true);
        SelectedSession? selected;
        lock (_gate) selected = _selected;
        if (selected is null) throw new InvalidOperationException("Apple Music for Windows audio session is unavailable.");
        try
        {
            ThrowIfFailed(selected.Volume.GetMasterVolume(out var current));
            var next = Math.Clamp(current + (float)(deltaPercentagePoints / 100d), 0f, 1f);
            var context = EventContext;
            ThrowIfFailed(selected.Volume.SetMasterVolume(next, ref context));
            var muted = Snapshot.Muted ?? false;
            if (selected.Volume.GetMute(out var currentMute) >= 0) muted = currentMute;
            // Some Apple Music for Windows/Amp sessions accept SetMasterVolume immediately but
            // return a stale cached value and omit the expected callback for several
            // seconds. The successful target is authoritative for immediate UI;
            // ordinary event/watchdog reads reconcile any later external change.
            SetSnapshot(new(true, (int)Math.Round(next * 100), muted, selected.BindingKind));
        }
        catch
        {
            InvalidateSelectedSession(resetManagers: true);
            throw;
        }
    }

    public async Task ToggleMuteAsync()
    {
        await RefreshAsync(forceSessionResolution: true);
        SelectedSession? selected;
        lock (_gate) selected = _selected;
        if (selected is null) throw new InvalidOperationException("Apple Music for Windows audio session is unavailable.");
        try
        {
            ThrowIfFailed(selected.Volume.GetMute(out var muted));
            var context = EventContext;
            ThrowIfFailed(selected.Volume.SetMute(!muted, ref context));
            var volume = Snapshot.VolumePercent;
            if (volume is null)
            {
                try
                {
                    if (selected.Volume.GetMasterVolume(out var currentVolume) >= 0)
                        volume = (int)Math.Round(currentVolume * 100);
                }
                catch { }
            }
            SetSnapshot(new(true, volume, !muted, selected.BindingKind));
        }
        catch
        {
            InvalidateSelectedSession(resetManagers: true);
            throw;
        }
    }

    private async Task ResetAsync()
    {
        if (_disposed) return;
        await _refreshLock.WaitAsync();
        try
        {
            Unbind();
            foreach (var binding in _sessionManagers)
            {
                try { binding.Manager.UnregisterSessionNotification(_sessionNotifications); } catch { }
                _retiredComObjects.Retain(binding.Manager);
            }
            _sessionManagers.Clear();
            if (_deviceEnumerator is not null)
            {
                try { _deviceEnumerator.UnregisterEndpointNotificationCallback(_endpointNotifications); } catch { }
                _retiredComObjects.Retain(_deviceEnumerator);
                _deviceEnumerator = null;
            }
            InitializeManagers();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Core Audio reset: {ex.Message}");
            SetSnapshot(new(false));
        }
        finally
        {
            _refreshLock.Release();
        }
        await RefreshAsync(forceSessionResolution: true);
    }

    private void InitializeManagers()
    {
        if (_sessionManagers.Count > 0) return;
        IMMDeviceEnumerator? deviceEnumerator = null;
        IMMDeviceCollection? endpoints = null;
        var endpointNotificationsRegistered = false;
        try
        {
            deviceEnumerator = (IMMDeviceEnumerator)(object)new MMDeviceEnumeratorComObject();
            ThrowIfFailed(deviceEnumerator.RegisterEndpointNotificationCallback(_endpointNotifications));
            endpointNotificationsRegistered = true;
            var defaultEndpointIds = GetDefaultRenderEndpointIds(deviceEnumerator);
            ThrowIfFailed(deviceEnumerator.EnumAudioEndpoints(EDataFlow.Render, 0x1, out endpoints));
            ThrowIfFailed(endpoints.GetCount(out var count));
            for (uint index = 0; index < count; index++)
            {
                IMMDevice? endpoint = null;
                object? managerObject = null;
                var managerRetained = false;
                try
                {
                    ThrowIfFailed(endpoints.Item(index, out endpoint));
                    ThrowIfFailed(endpoint.GetId(out var endpointId));
                    var iid = typeof(IAudioSessionManager2).GUID;
                    ThrowIfFailed(endpoint.Activate(ref iid, ClsCtx.All, IntPtr.Zero, out managerObject));
                    var manager = (IAudioSessionManager2)managerObject;
                    ThrowIfFailed(manager.RegisterSessionNotification(_sessionNotifications));
                    _sessionManagers.Add(new(endpointId, defaultEndpointIds.Contains(endpointId), manager));
                    managerRetained = true;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Core Audio endpoint {index}: {ex.Message}");
                }
                finally
                {
                    ReleaseCom(endpoint);
                    if (!managerRetained) ReleaseCom(managerObject);
                }
            }

            _deviceEnumerator = deviceEnumerator;
            deviceEnumerator = null;
        }
        finally
        {
            ReleaseCom(endpoints);
            if (deviceEnumerator is not null)
            {
                if (endpointNotificationsRegistered)
                {
                    try { deviceEnumerator.UnregisterEndpointNotificationCallback(_endpointNotifications); } catch { }
                }
                ReleaseCom(deviceEnumerator);
            }
        }
    }

    private Candidate? FindBestSession()
    {
        Candidate? best = null;
        var endpointScanFailed = false;
        var seenSessionIds = new HashSet<string>(StringComparer.Ordinal);
        string? selectedSessionId;
        lock (_gate) selectedSessionId = _selected?.InstanceIdentifier;
        foreach (var binding in _sessionManagers)
        {
            IAudioSessionEnumerator? enumerator = null;
            try
            {
                ThrowIfFailed(binding.Manager.GetSessionEnumerator(out enumerator));
                ThrowIfFailed(enumerator!.GetCount(out var count));
                for (var index = 0; index < count; index++)
                {
                    IAudioSessionControl? control = null;
                    try
                    {
                        ThrowIfFailed(enumerator.GetSession(index, out control));
                        var control2 = (IAudioSessionControl2)control;
                        var state = GetState(control2);
                        var displayName = GetDisplayName(control2);
                        var identifier = GetSessionIdentifier(control2);
                        var instanceIdentifier = GetSessionInstanceIdentifier(control2);
                        var processId = GetProcessId(control2);
                        var (processName, executablePath) = GetProcessEvidence(processId);
                        var peak = GetPeak(control);
                        var evidence = new AudioCandidateEvidence(processName, executablePath, identifier, displayName, state);
                        var scored = SessionScorer.ScoreAudio(evidence);
                        if (!SessionScorer.IsAcceptableAudioScore(scored.Score)) continue;
                        var volume = (ISimpleAudioVolume)control;
                        var runtimeId = $"{binding.EndpointId}|{instanceIdentifier ?? identifier ?? $"pid:{processId}"}";
                        seenSessionIds.Add(runtimeId);
                        var safePeak = peak.HasValue && float.IsFinite(peak.Value)
                            ? Math.Clamp(peak.Value, 0f, 1f)
                            : 0f;
                        if (SessionScorer.IsAudiblePeak(safePeak)) _audibleSessionIds.Add(runtimeId);
                        var ranking = new AudioCandidateRanking(
                            scored.Score,
                            state,
                            safePeak,
                            _audibleSessionIds.Contains(runtimeId),
                            string.Equals(runtimeId, selectedSessionId, StringComparison.Ordinal),
                            binding.IsDefaultEndpoint);
                        if (Environment.GetEnvironmentVariable("CLEF_AUDIO_DIAGNOSTICS") == "1")
                        {
                            var level = volume.GetMasterVolume(out var currentVolume) >= 0 ? currentVolume : -1;
                            var muted = volume.GetMute(out var currentMute) >= 0 && currentMute;
                            Console.Error.WriteLine(
                                $"Audio candidate endpoint={binding.EndpointId} index={index} state={state} " +
                                $"peak={(peak.HasValue ? peak.Value.ToString("F6") : "unavailable")} " +
                                $"volume={level:F3} muted={muted} pid={processId} score={scored.Score} " +
                                $"tier={SessionScorer.AudioActivityTier(ranking)} default={binding.IsDefaultEndpoint} " +
                                $"selected={ranking.IsSelected} " +
                                $"display={displayName ?? "<none>"} instance={instanceIdentifier ?? "<none>"}");
                        }
                        var candidate = new Candidate(control, control2, volume, runtimeId, ranking, scored.BindingKind);
                        control = null;
                        if (best is null || SessionScorer.CompareAudioCandidates(candidate.Ranking, best.Ranking) > 0)
                        {
                            best?.Release();
                            best = candidate;
                        }
                        else
                        {
                            candidate.Release();
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"Audio candidate {binding.EndpointId}/{index}: {ex.Message}");
                    }
                    finally
                    {
                        ReleaseCom(control);
                    }
                }
            }
            catch (Exception ex)
            {
                endpointScanFailed = true;
                Console.Error.WriteLine($"Audio endpoint scan {binding.EndpointId}: {ex.Message}");
            }
            finally
            {
                ReleaseCom(enumerator);
            }
        }
        if (!endpointScanFailed) _audibleSessionIds.IntersectWith(seenSessionIds);
        if (best is null && endpointScanFailed)
            throw new InvalidOperationException("Core Audio endpoint scan failed before an Apple Music session was found.");
        return best;
    }

    private void Bind(Candidate? candidate)
    {
        lock (_gate)
        {
            if (candidate is not null && !_selectedInvalidated &&
                _selected?.InstanceIdentifier == candidate.InstanceIdentifier)
            {
                _selected.BindingKind = candidate.BindingKind;
                candidate.Release();
                return;
            }
            UnbindLocked();
            if (candidate is null) return;
            var events = new SessionEvents(
                () => Post(UpdateSnapshot),
                () => RequestOperation(reset: false),
                () => Post(() => InvalidateSelectedSession(resetManagers: true)));
            try
            {
                ThrowIfFailed(candidate.Control2.RegisterAudioSessionNotification(events));
                _selected = candidate.Adopt(events);
                _selectedInvalidated = false;
            }
            catch
            {
                candidate.Release();
                throw;
            }
        }
    }

    private void UpdateSnapshot()
    {
        SelectedSession? selected;
        lock (_gate) selected = _selected;
        if (selected is null)
        {
            SetSnapshot(new(false));
            return;
        }
        try
        {
            ThrowIfFailed(selected.Volume.GetMasterVolume(out var volume));
            ThrowIfFailed(selected.Volume.GetMute(out var muted));
            SetSnapshot(new(true, (int)Math.Round(volume * 100), muted, selected.BindingKind));
        }
        catch
        {
            InvalidateSelectedSession(resetManagers: true);
        }
    }

    private void SetSnapshot(AudioSnapshot snapshot)
    {
        var changed = false;
        lock (_gate)
        {
            if (_snapshot != snapshot)
            {
                _snapshot = snapshot;
                changed = true;
            }
        }
        if (changed) Changed?.Invoke();
    }

    private void Unbind()
    {
        lock (_gate) UnbindLocked();
    }

    private void UnbindLocked()
    {
        _selectedInvalidated = false;
        if (_selected is null) return;
        var retired = _selected;
        try { retired.Control2.UnregisterAudioSessionNotification(retired.Events); } catch { }
        _selected = null;
        _retiredSessions.Retain(retired);
    }

    private void InvalidateSelectedSession(bool resetManagers)
    {
        lock (_gate) _selectedInvalidated = true;
        SetSnapshot(new(false));
        if (resetManagers) RequestManagerReset();
        else RequestOperation(reset: false);
    }

    private static (string? Name, string? Path) GetProcessEvidence(uint processId)
    {
        if (processId == 0) return (null, null);
        try
        {
            using var process = Process.GetProcessById((int)processId);
            string? path = null;
            try { path = process.MainModule?.FileName; } catch { }
            return (process.ProcessName, path);
        }
        catch { return (null, null); }
    }

    private static AudioSessionState GetState(IAudioSessionControl2 control)
    {
        try { return control.GetState(out var value) >= 0 ? value : AudioSessionState.Inactive; }
        catch { return AudioSessionState.Inactive; }
    }

    private static string? GetDisplayName(IAudioSessionControl2 control)
    {
        try { return control.GetDisplayName(out var value) >= 0 ? value : null; }
        catch { return null; }
    }

    private static string? GetSessionIdentifier(IAudioSessionControl2 control)
    {
        try { return control.GetSessionIdentifier(out var value) >= 0 ? value : null; }
        catch { return null; }
    }

    private static string? GetSessionInstanceIdentifier(IAudioSessionControl2 control)
    {
        try { return control.GetSessionInstanceIdentifier(out var value) >= 0 ? value : null; }
        catch { return null; }
    }

    private static uint GetProcessId(IAudioSessionControl2 control)
    {
        try { return control.GetProcessId(out var value) >= 0 ? value : 0; }
        catch { return 0; }
    }

    private static HashSet<string> GetDefaultRenderEndpointIds(IMMDeviceEnumerator enumerator)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var role in new[] { ERole.Console, ERole.Multimedia })
        {
            IMMDevice? endpoint = null;
            try
            {
                if (enumerator.GetDefaultAudioEndpoint(EDataFlow.Render, role, out endpoint) >= 0 &&
                    endpoint.GetId(out var id) >= 0)
                    ids.Add(id);
            }
            catch { }
            finally { ReleaseCom(endpoint); }
        }
        return ids;
    }

    private static float? GetPeak(IAudioSessionControl control)
    {
        try
        {
            var meter = (IAudioMeterInformation)control;
            return meter.GetPeakValue(out var peak) >= 0 ? peak : null;
        }
        catch { return null; }
    }

    private static void ThrowIfFailed(int hresult)
    {
        if (hresult < 0) Marshal.ThrowExceptionForHR(hresult);
    }

    private void Post(Action operation)
    {
        if (_context is null || ReferenceEquals(SynchronizationContext.Current, _context)) operation();
        else _context.Post(_ => operation(), null);
    }

    private void RequestOperation(bool reset)
    {
        lock (_postGate)
        {
            if (reset) _resetRequested = true;
            else _refreshRequested = true;
            if (_operationPosted || _disposed) return;
            _operationPosted = true;
        }

        void Start() => _ = DrainRequestedOperationsAsync();
        if (_context is null || ReferenceEquals(SynchronizationContext.Current, _context)) Start();
        else _context.Post(_ => Start(), null);
    }

    private void RequestManagerReset()
    {
        var now = DateTimeOffset.UtcNow;
        lock (_postGate)
        {
            // A reset performs another scan. Keep persistent Windows failures
            // from turning that scan into an immediate reset loop.
            if (now - _lastManagerResetRequestedAt < TimeSpan.FromSeconds(2)) return;
            _lastManagerResetRequestedAt = now;
        }
        RequestOperation(reset: true);
    }

    private async Task DrainRequestedOperationsAsync()
    {
        while (!_disposed)
        {
            bool reset;
            bool refresh;
            lock (_postGate)
            {
                reset = _resetRequested;
                refresh = _refreshRequested;
                _resetRequested = false;
                _refreshRequested = false;
                if (!reset && !refresh)
                {
                    _operationPosted = false;
                    return;
                }
            }

            if (reset) await ResetAsync();
            else if (refresh) await RefreshAsync(forceSessionResolution: true);
        }
    }

    private static void ReleaseCom(object? value)
    {
        if (value is null || !Marshal.IsComObject(value)) return;
        try { Marshal.ReleaseComObject(value); } catch { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Unbind();
        foreach (var binding in _sessionManagers)
        {
            try { binding.Manager.UnregisterSessionNotification(_sessionNotifications); } catch { }
            _retiredComObjects.Retain(binding.Manager);
        }
        _sessionManagers.Clear();
        _audibleSessionIds.Clear();
        if (_deviceEnumerator is not null)
        {
            try { _deviceEnumerator.UnregisterEndpointNotificationCallback(_endpointNotifications); } catch { }
            _retiredComObjects.Retain(_deviceEnumerator);
            _deviceEnumerator = null;
        }
        // Do not synchronously release callback-bound RCWs during shutdown;
        // stdin closure is immediately followed by process exit, and an
        // already-dispatched Core Audio callback may still be returning.
        _refreshLock.Dispose();
    }

    private sealed class Candidate(IAudioSessionControl control, IAudioSessionControl2 control2, ISimpleAudioVolume volume, string instanceIdentifier, AudioCandidateRanking ranking, string? bindingKind)
    {
        private bool _owned = true;
        public IAudioSessionControl Control { get; } = control;
        public IAudioSessionControl2 Control2 { get; } = control2;
        public ISimpleAudioVolume Volume { get; } = volume;
        public string InstanceIdentifier { get; } = instanceIdentifier;
        public AudioCandidateRanking Ranking { get; } = ranking;
        public string? BindingKind { get; } = bindingKind;

        public SelectedSession Adopt(SessionEvents events)
        {
            if (!_owned) throw new InvalidOperationException("Audio candidate was already released.");
            _owned = false;
            return new(Control, Control2, Volume, InstanceIdentifier, BindingKind, events);
        }

        public void Release()
        {
            if (!_owned) return;
            _owned = false;
            ReleaseCom(Control);
        }
    }

    private sealed class SelectedSession(IAudioSessionControl control, IAudioSessionControl2 control2, ISimpleAudioVolume volume, string instanceIdentifier, string? bindingKind, SessionEvents events)
    {
        private bool _released;
        public IAudioSessionControl Control { get; } = control;
        public IAudioSessionControl2 Control2 { get; } = control2;
        public ISimpleAudioVolume Volume { get; } = volume;
        public string InstanceIdentifier { get; } = instanceIdentifier;
        public string? BindingKind { get; set; } = bindingKind;
        public SessionEvents Events { get; } = events;

        public void Release()
        {
            if (_released) return;
            _released = true;
            ReleaseCom(Control);
        }
    }

    private sealed record ManagerBinding(string EndpointId, bool IsDefaultEndpoint, IAudioSessionManager2 Manager);

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    private sealed class EndpointNotifications(Action reset) : IMMNotificationClient
    {
        public int OnDefaultDeviceChanged(EDataFlow flow, ERole role, string? id) { if (flow is EDataFlow.Render or EDataFlow.All) reset(); return 0; }
        public int OnDeviceStateChanged(string id, uint state) { reset(); return 0; }
        public int OnDeviceAdded(string id) => 0;
        public int OnDeviceRemoved(string id) { reset(); return 0; }
        public int OnPropertyValueChanged(string id, PropertyKey key) => 0;
    }

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    private sealed class SessionNotifications(Action refresh) : IAudioSessionNotification
    {
        public int OnSessionCreated(IAudioSessionControl session) { refresh(); return 0; }
    }

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    private sealed class SessionEvents(Action volumeChanged, Action rebind, Action reset) : IAudioSessionEvents
    {
        public int OnDisplayNameChanged(string? value, IntPtr context) { rebind(); return 0; }
        public int OnIconPathChanged(string? value, IntPtr context) => 0;
        public int OnSimpleVolumeChanged(float volume, bool muted, IntPtr context) { volumeChanged(); return 0; }
        public int OnChannelVolumeChanged(uint count, IntPtr volumes, uint channel, IntPtr context) => 0;
        public int OnGroupingParamChanged(IntPtr grouping, IntPtr context) => 0;
        public int OnStateChanged(AudioSessionState state) { if (state == AudioSessionState.Expired) reset(); else rebind(); return 0; }
        public int OnSessionDisconnected(AudioSessionDisconnectReason reason) { reset(); return 0; }
    }
}
