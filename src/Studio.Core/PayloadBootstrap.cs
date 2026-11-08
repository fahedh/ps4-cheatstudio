using System.Net.Sockets;
using System.Security.Cryptography;

namespace Studio.Core;

public static class PayloadBootstrap
{
    public const string FileName = "ps4debug-ng_v1.3.4_release_2026-11-05.bin";
    public const string Digest = "8BFE0507972718E6078778C5F9C44D84293197C0547239AB4227BDB351299B3E";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60), MaxResponseContentBufferSize = 4 * 1024 * 1024 };

    public static async Task ConnectAsync(Func<CancellationToken, Task> handshake, Func<CancellationToken, Task> send, CancellationToken ct)
    {
        try { await handshake(ct); return; }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionRefused) { }
        await send(ct);
        for (int attempt = 0; attempt < 20; attempt++)
        {
            await Task.Delay(500, ct);
            try { await handshake(ct); return; }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionRefused) { }
        }
        throw new IOException("Payload sent once, but the debugger did not start. Check the console and loader port.");
    }

    public static async Task SendOfficialAsync(ConnectionSettings settings, CancellationToken ct)
    {
        if (settings.PayloadPort == settings.DebugPort) throw new InvalidDataException("Set different loader and debugger ports.");
        var portable = Path.Combine(AppContext.BaseDirectory, FileName);
        var cache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PS4GoldHENCheatStudio", "payloads", FileName);
        var file = File.Exists(portable) ? portable : cache;
        bool download = !File.Exists(file);
        var bytes = download
            ? await Http.GetByteArrayAsync($"https://github.com/Pharaoh2k/ps4debug-NG/releases/download/1.3.4/{FileName}", ct)
            : await File.ReadAllBytesAsync(file, ct);
        if (Convert.ToHexString(SHA256.HashData(bytes)) != Digest) throw new InvalidDataException("Debugger payload checksum mismatch. Replace the local payload with the official 1.3.4 release.");
        if (download)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(cache)!);
            var temp = cache + "." + Guid.NewGuid().ToString("N");
            try { await File.WriteAllBytesAsync(temp, bytes, ct); File.Move(temp, cache, true); }
            finally { File.Delete(temp); }
        }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(settings.TimeoutSeconds));
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(settings.Host, settings.PayloadPort, deadline.Token);
        await tcp.GetStream().WriteAsync(bytes, deadline.Token);
        tcp.Client.Shutdown(SocketShutdown.Send);
    }
}
