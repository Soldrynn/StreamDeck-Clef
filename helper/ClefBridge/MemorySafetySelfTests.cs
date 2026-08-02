namespace ClefBridge;

internal static class MemorySafetySelfTests
{
    public static void Run()
    {
        var released = new List<int>();
        var start = DateTimeOffset.UtcNow;
        using var retainer = new GraceRetainer<int>(TimeSpan.FromSeconds(30), 4, released.Add);

        for (var value = 0; value < 10; value++) retainer.Retain(value, start);
        Require(retainer.Count == 4, "retired callback hard bound");
        Require(released.Count == 6, "oldest callbacks released at hard bound");

        retainer.Trim(start + TimeSpan.FromSeconds(31));
        Require(retainer.Count == 0, "retired callback grace expiry");
        Require(released.Count == 10, "all expired callbacks released");
    }

    private static void Require(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException($"Self-test failed: {name}");
    }
}
