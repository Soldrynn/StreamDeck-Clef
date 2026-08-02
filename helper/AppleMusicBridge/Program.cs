using System.Text.Json;
using System.Text.Json.Serialization;

namespace AppleMusicBridge;

internal static class Program
{
    private static readonly object OutputGate = new();
    private static readonly Stream OutputStream = Console.OpenStandardOutput();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    [MTAThread]
    public static int Main(string[] args)
    {
        using var context = new SingleThreadSynchronizationContext();
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(context);
        try
        {
            return context.Run(RunAsync(args));
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }

    private static async Task<int> RunAsync(string[] args)
    {
        if (args.Contains("--self-test", StringComparer.OrdinalIgnoreCase))
        {
            ResolverSelfTests.Run();
            Console.WriteLine("Resolver self-tests passed.");
            return 0;
        }

        try
        {
            await using var service = new BridgeService();
            service.StateChanged += Write;
            Write(new { type = "hello", protocol = 1, version = "0.1.12" });
            await service.InitializeAsync();

            string? line;
            while ((line = await Console.In.ReadLineAsync()) is not null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                CommandMessage? command = null;
                try
                {
                    command = JsonSerializer.Deserialize<CommandMessage>(line, JsonOptions);
                    if (command?.Type != "command") throw new InvalidOperationException("Expected a command message.");
                    await service.ExecuteAsync(command);
                    Write(new { type = "ack", id = command.Id, ok = true });
                }
                catch (Exception ex)
                {
                    var detail = string.IsNullOrWhiteSpace(ex.Message)
                        ? $"{ex.GetType().Name} (0x{ex.HResult:X8})"
                        : $"{ex.Message} [{ex.GetType().Name}, 0x{ex.HResult:X8}]";
                    Write(new { type = "ack", id = command?.Id ?? 0, ok = false, error = detail });
                }
            }
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static void Write<T>(T message)
    {
        lock (OutputGate)
        {
            JsonSerializer.Serialize(OutputStream, message, JsonOptions);
            OutputStream.WriteByte((byte)'\n');
            OutputStream.Flush();
        }
    }
}
