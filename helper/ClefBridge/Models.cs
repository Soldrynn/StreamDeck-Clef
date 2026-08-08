namespace ClefBridge;

internal sealed record MediaSnapshot(
    bool Available,
    string? SourceAppId = null,
    string? Title = null,
    string? Artist = null,
    string? Album = null,
    string PlaybackStatus = "unknown",
    long? PositionMs = null,
    long? DurationMs = null,
    string? ArtworkDataUri = null,
    string? ArtworkKey = null);

internal sealed record AudioSnapshot(
    bool Available,
    int? VolumePercent = null,
    bool? Muted = null,
    string? BindingKind = null);

internal sealed record BridgeSnapshot(
    string Type,
    long Revision,
    DateTimeOffset TimestampUtc,
    MediaSnapshot Media,
    AudioSnapshot Audio);

internal sealed record CommandMessage(string? Type, long Id, string? Name, double? Amount);

internal sealed record AudioCandidateEvidence(
    string? ProcessName,
    string? ExecutablePath,
    string? SessionIdentifier,
    string? DisplayName,
    AudioSessionState State);

internal readonly record struct AudioCandidateRanking(
    int IdentityScore,
    AudioSessionState State,
    float Peak,
    bool WasAudible,
    bool IsSelected,
    bool IsDefaultEndpoint);
