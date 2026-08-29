using System.Threading.Channels;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClefBridge;

internal static class Program
{
    private const int MaximumQueuedFrames = 256;
    private static int _queuedFrames;
    private static readonly Stream OutputStream = Console.OpenStandardOutput();
    private static readonly Channel<byte[]> Output = Channel.CreateUnbounded<byte[]>(
        new UnboundedChannelOptions { SingleReader = true });
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

        var writer = new Thread(PumpOutput) { IsBackground = true, Name = "ClefBridge output" };
        writer.Start();
        try
        {
            await using var service = new BridgeService();
            service.StateChanged += snapshot => Write(snapshot, droppable: true);
            Write(new { type = "hello", protocol = 1, version = "1.0.0.0" });
            await service.InitializeAsync();

            await foreach (var line in ReadCommandLinesAsync())
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
        finally
        {
            Output.Writer.TryComplete();
            writer.Join(TimeSpan.FromSeconds(2));
        }
    }

    private static async IAsyncEnumerable<string> ReadCommandLinesAsync()
    {
        var lines = Channel.CreateUnbounded<string>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
        _ = Task.Run(() =>
        {
            try
            {
                string? line;
                while ((line = Console.In.ReadLine()) is not null) lines.Writer.TryWrite(line);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Command input failed: {ex.Message}");
            }
            finally
            {
                lines.Writer.TryComplete();
            }
        });

        await foreach (var line in lines.Reader.ReadAllAsync()) yield return line;
    }

    private static void Write<T>(T message, bool droppable = false)
    {
        if (droppable && Volatile.Read(ref _queuedFrames) >= MaximumQueuedFrames) return;
        byte[] frame;
        try
        {
            using var buffer = new MemoryStream();
            JsonSerializer.Serialize(buffer, message, JsonOptions);
            buffer.WriteByte((byte)'\n');
            frame = buffer.ToArray();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Output serialization failed: {ex.Message}");
            return;
        }
        if (Output.Writer.TryWrite(frame)) Interlocked.Increment(ref _queuedFrames);
    }

    private static void PumpOutput()
    {
        try
        {
            while (Output.Reader.WaitToReadAsync().AsTask().GetAwaiter().GetResult())
            {
                while (Output.Reader.TryRead(out var frame))
                {
                    Interlocked.Decrement(ref _queuedFrames);
                    OutputStream.Write(frame, 0, frame.Length);
                }
                OutputStream.Flush();
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Output pipe closed: {ex.Message}");
        }
    }
}
