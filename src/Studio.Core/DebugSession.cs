using System.Net;
using System.Net.Sockets;

namespace Studio.Core;

public record DebugInterrupt(uint ThreadId, uint Status, string ThreadName, ulong Rip, ulong Rsp, byte[] Registers);

/// <summary>Optional explicit debug attachment. NG uses a reverse TCP connection to client port 755.</summary>
public sealed class DebugSession(DebuggerClient client) : IAsyncDisposable
{
    private TcpListener? listener;
    private TcpClient? eventClient;
    private CancellationTokenSource? lifetime;
    private Task? pump;
    private volatile bool detaching;
    public bool Attached { get; private set; }
    public event Action<DebugInterrupt>? Interrupted;
    public event Action<string>? Error;
    public async Task AttachAsync(GameIdentity game, IPAddress localAddress, IPAddress consoleAddress, int port = 755, CancellationToken ct = default)
    {
        if (!game.IsGame || !game.SameGame(await client.GetGameAsync(ct))) throw new InvalidDataException("Debug attachment is restricted to the selected running game.");
        if (Attached) throw new InvalidOperationException("Detach the current debug session first.");
        if (port != 755) throw new NotSupportedException("ps4debug-NG 1.3.4 fixes the reverse interrupt port at 755; changing it requires a payload that documents another port.");
        if (localAddress.AddressFamily != AddressFamily.InterNetwork) throw new ArgumentException("Use the IPv4 address of this PC on the console's LAN.");
        detaching = false; listener = new TcpListener(localAddress, port); listener.Start();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(8));
        var accept = listener.AcceptTcpClientAsync(timeout.Token).AsTask();
        try
        {
            var b = new byte[8]; Wire.Put32(b, 0, (uint)game.Pid); localAddress.GetAddressBytes().CopyTo(b, 4);
            await client.SimpleAsync(Wire.Attach, b, timeout.Token);
            eventClient = await accept;
            if (!((IPEndPoint)eventClient.Client.RemoteEndPoint!).Address.Equals(consoleAddress)) throw new IOException("Interrupt connection did not come from the selected console.");
            Attached = true; lifetime = new(); pump = PumpAsync(lifetime.Token);
        }
        catch
        {
            timeout.Cancel(); listener.Stop(); eventClient?.Dispose();
            try { var accepted = await accept; if (!ReferenceEquals(accepted, eventClient)) accepted.Dispose(); } catch (Exception e) when (e is OperationCanceledException or SocketException or ObjectDisposedException) { }
            client.Disconnect(); throw;
        }
    }
    private async Task PumpAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var b = new byte[1184]; await eventClient!.GetStream().ReadExactlyAsync(b, ct);
                Interrupted?.Invoke(new(Wire.U32(b), Wire.U32(b, 4), Wire.Text(b, 8, 40), Wire.U64(b, 184), Wire.U64(b, 208), b.AsSpan(48, 184).ToArray()));
            }
        }
        catch (Exception e) when (e is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
        {
            if (!ct.IsCancellationRequested && !detaching) { Attached = false; client.Disconnect(); listener?.Stop(); Error?.Invoke("Interrupt channel closed; command connection was closed to release debugger state: " + e.Message); }
        }
    }
    public Task<bool> WatchAsync(int slot, ulong address, int byteLength, bool enabled, CancellationToken ct = default)
    {
        EnsureAttached();
        if (slot is < 0 or > 3 || byteLength is not (1 or 2 or 4 or 8) || address % (ulong)byteLength != 0) throw new ArgumentException("Use slot 0–3 and a naturally aligned length of 1, 2, 4 or 8 bytes.");
        var b = new byte[24]; Wire.Put32(b, 0, (uint)slot); Wire.Put32(b, 4, enabled ? 1u : 0u);
        var encoded = byteLength switch { 1 => 0u, 2 => 1u, 4 => 3u, 8 => 2u, _ => throw new ArgumentOutOfRangeException(nameof(byteLength)) };
        Wire.Put32(b, 8, encoded); Wire.Put32(b, 12, 1); Wire.Put64(b, 16, address); return client.SimpleAsync(Wire.Watchpoint, b, ct);
    }
    public Task<bool> ResumeAsync(CancellationToken ct = default) { EnsureAttached(); return client.SimpleAsync(Wire.Continue, Wire.Pid(0), ct); }
    public Task<bool> StepAsync(int threadId, CancellationToken ct = default) { EnsureAttached(); return client.SimpleAsync(Wire.StepThread, Wire.Pid(threadId), ct); }
    public Task<byte[]> GetRegistersAsync(int threadId, CancellationToken ct = default)
    {
        EnsureAttached(); return client.CommandAsync(Wire.Registers, Wire.Pid(threadId), async (s, t) => { var b = new byte[184]; await s.ReadExactlyAsync(b, t); return b; }, ct);
    }
    private void EnsureAttached() { if (!Attached || !client.IsConnected) throw new InvalidOperationException("Explicit debug attachment is required first."); }
    public async Task DetachAsync(CancellationToken ct = default)
    {
        detaching = true;
        try { if (Attached && client.IsConnected) await client.SimpleAsync(Wire.Detach, [], ct); }
        finally
        {
            Attached = false; lifetime?.Cancel(); eventClient?.Dispose(); listener?.Stop();
            if (pump is not null) await pump;
        }
    }
    public async ValueTask DisposeAsync()
    {
        try { await DetachAsync(); } catch (Exception e) when (e is IOException or OperationCanceledException or TimeoutException or InvalidOperationException) { client.Disconnect(); }
        finally { lifetime?.Cancel(); eventClient?.Dispose(); listener?.Stop(); lifetime?.Dispose(); }
    }
}
