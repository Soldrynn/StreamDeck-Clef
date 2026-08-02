namespace ClefBridge;

internal static class ArtworkAssociationSelfTests
{
    public static void Run()
    {
        var start = DateTimeOffset.UtcNow;
        var tracker = new ArtworkAssociationTracker();

        tracker.ChangeIdentity("song-a", "album-a", hadPreviousTrack: false, 1, start);
        Require(tracker.ShouldAccept("hash-a", "song-a", "album-a", 1, start), "initial artwork is immediate");
        tracker.Commit("hash-a", "album-a");

        tracker.ChangeIdentity("song-b", "album-b", hadPreviousTrack: true, 2, start);
        Require(!tracker.ShouldAccept("hash-a", "song-b", "album-b", 3, start + TimeSpan.FromSeconds(5)),
            "previous album artwork cannot cross-bind");

        Require(!tracker.ShouldAccept("hash-b", "song-b", "album-b", 2, start + TimeSpan.FromMilliseconds(300)),
            "new artwork waits for settlement");
        Require(tracker.ShouldAccept("hash-b", "song-b", "album-b", 2, start + TimeSpan.FromMilliseconds(1500)),
            "stable new artwork is accepted");
        tracker.Commit("hash-b", "album-b");

        tracker.ChangeIdentity("song-c", "album-b", hadPreviousTrack: true, 4, start + TimeSpan.FromSeconds(2));
        Require(tracker.ShouldAccept("hash-b", "song-c", "album-b", 4, start + TimeSpan.FromSeconds(2)),
            "shared album artwork remains immediate");

        tracker.ChangeIdentity("song-d", "album-d", hadPreviousTrack: true, 5, start + TimeSpan.FromSeconds(3));
        Require(tracker.ShouldAccept("hash-d", "song-d", "album-d", 6, start + TimeSpan.FromMilliseconds(3300)),
            "later media-properties event confirms artwork");
    }

    private static void Require(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException($"Self-test failed: {name}");
    }
}
