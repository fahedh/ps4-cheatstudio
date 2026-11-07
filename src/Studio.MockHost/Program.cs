using Studio.Mock;
namespace Studio.MockHost;

public static class Program
{
    public static async Task Main(string[] args)
    {
        var port = args.Length == 2 && args[0] == "--port" ? int.Parse(args[1]) : 1744;
        await using var server = new MockDebuggerServer(); server.Log += Console.WriteLine; server.Start(port);
        Console.WriteLine("SIMULATOR ONLY. Code signature at 0x402800; Int32 value 100 at 0x420040. No real game behavior is simulated.");
        var exit = new TaskCompletionSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; exit.TrySetResult(); };
        await exit.Task;
    }
}
