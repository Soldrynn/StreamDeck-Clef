namespace ClefBridge;

/// <summary>
/// Prevents a thumbnail hash already committed to one album/track group from
/// being assigned to a different group while GSMTC media properties settle.
/// New hashes require either a later media-properties event or two separated
/// observations after the transition window begins.
/// </summary>
internal sealed class ArtworkAssociationTracker
{
    private const int MaximumHistory = 16;
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
        else if (now - _candidate.LastSeenAt >= TimeSpan.FromMilliseconds(350))
        {
            _candidate = _candidate with { LastSeenAt = now, Observations = _candidate.Observations + 1 };
        }

        if (mediaPropertiesGeneration > _transitionGeneration &&
            now - _transitionAt >= TimeSpan.FromMilliseconds(250))
            return true;

        return _candidate.Observations >= 2 &&
            now - _transitionAt >= TimeSpan.FromMilliseconds(1200) &&
            now - _candidate.FirstSeenAt >= TimeSpan.FromMilliseconds(500);
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
