namespace ClefBridge;

internal sealed class ArtworkAssociationTracker
{
    private const int MaximumHistory = 16;
    private static readonly TimeSpan ObservationGap = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan MinimumTransitionAge = TimeSpan.FromMilliseconds(600);
    private static readonly TimeSpan MinimumCandidateAge = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan MinimumConfirmedTransitionAge = TimeSpan.FromMilliseconds(150);
    private readonly List<Commitment> _history = [];
    private string? _currentIdentityKey;
    private string? _currentGroupKey;
    private bool _requiresSettlement;
    private long _transitionGeneration;
    private DateTimeOffset _transitionAt;
    private Candidate? _candidate;

    public void ChangeIdentity(
        string identityKey,
        string groupKey,
        bool hadPreviousTrack,
        long mediaPropertiesGeneration,
        DateTimeOffset now)
    {
        _currentIdentityKey = identityKey;
        _currentGroupKey = groupKey;
        _requiresSettlement = hadPreviousTrack;
        _transitionGeneration = mediaPropertiesGeneration;
        _transitionAt = now;
        _candidate = null;
    }

    public bool ShouldAccept(
        string contentHash,
        string identityKey,
        string groupKey,
        long mediaPropertiesGeneration,
        DateTimeOffset now)
    {
        if (!string.Equals(identityKey, _currentIdentityKey, StringComparison.Ordinal) ||
            !string.Equals(groupKey, _currentGroupKey, StringComparison.Ordinal))
            return false;

        var sameGroupCommit = _history.LastOrDefault(item =>
            string.Equals(item.ContentHash, contentHash, StringComparison.Ordinal) &&
            string.Equals(item.GroupKey, groupKey, StringComparison.Ordinal));
        if (sameGroupCommit is not null) return true;

        var conflictingCommit = _history.Any(item =>
            string.Equals(item.ContentHash, contentHash, StringComparison.Ordinal) &&
            !string.Equals(item.GroupKey, groupKey, StringComparison.Ordinal));
        if (conflictingCommit) return false;
        if (!_requiresSettlement) return true;

        if (_candidate is null ||
            !string.Equals(_candidate.ContentHash, contentHash, StringComparison.Ordinal) ||
            !string.Equals(_candidate.IdentityKey, identityKey, StringComparison.Ordinal))
        {
            _candidate = new(contentHash, identityKey, now, now, 1);
        }
        else if (now - _candidate.LastSeenAt >= ObservationGap)
        {
            _candidate = _candidate with { LastSeenAt = now, Observations = _candidate.Observations + 1 };
        }

        if (mediaPropertiesGeneration > _transitionGeneration &&
            now - _transitionAt >= MinimumConfirmedTransitionAge)
            return true;

        return _candidate.Observations >= 2 &&
            now - _transitionAt >= MinimumTransitionAge &&
            now - _candidate.FirstSeenAt >= MinimumCandidateAge;
    }

    public void Commit(string contentHash, string groupKey)
    {
        _history.RemoveAll(item =>
            string.Equals(item.ContentHash, contentHash, StringComparison.Ordinal) &&
            string.Equals(item.GroupKey, groupKey, StringComparison.Ordinal));
        _history.Add(new(contentHash, groupKey));
        if (_history.Count > MaximumHistory) _history.RemoveRange(0, _history.Count - MaximumHistory);
        _requiresSettlement = false;
        _candidate = null;
    }

    private sealed record Commitment(string ContentHash, string GroupKey);
    private sealed record Candidate(
        string ContentHash,
        string IdentityKey,
        DateTimeOffset FirstSeenAt,
        DateTimeOffset LastSeenAt,
        int Observations);
}
