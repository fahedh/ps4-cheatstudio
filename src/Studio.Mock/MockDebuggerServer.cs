using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Studio.Core;

namespace Studio.Mock;

/// <summary>Loopback-only deterministic subset of the documented NG protocol, never a real payload.</summary>
public sealed class MockDebuggerServer : IAsyncDisposable
{
    public const ulong BaseAddress = 0x400000;
    public byte[] Memory { get; } = Enumerable.Repeat((byte)0xCC, 0x30000).ToArray();
    public GameIdentity Game { get; set; } = new(100, "Simulated Silksong", "CUSA39420", "01.05", "eboot.bin");
    public IReadOnlyList<MemoryRegion> Regions { get; set; } = [new(0, BaseAddress, BaseAddress + 0x20000, 0, 5, "executable"), new(1, BaseAddress + 0x20000, BaseAddress + 0x30000, 0, 3, "data")];
    public int FragmentSize { get; set; } = int.MaxValue;
    public string ProtocolVersion { get; set; } = "1.3";
    public string Branding { get; set; } = "ps4debug-NG by Pharaoh2k v1.3.4 [SIMULATOR]\0" + "1.1";
    public ushort FirmwareVersion { get; set; } = 1352;
    public ushort PlatformId { get; set; } = 4;
    public bool FailAttachAfterCallback { get; set; }
    public List<byte[]> WatchpointPackets { get; } = [];
    public TimeSpan ResponseDelay { get; set; }
    public uint? NextStatus { get; set; }
    public bool DisconnectOnRead { get; set; }
    public int WriteCount { get; private set; }
    public int FailWriteNumber { get; set; } = -1;
    public List<(ulong Address, byte[] Bytes)> Written { get; } = [];
    private TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource lifetime = new();
    private readonly ConcurrentDictionary<TcpClient, Task> connections = new();
    private Task? accepting;
    public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;
    public event Action<string>? Log;

    public MockDebuggerServer()
    {
        Hex.Bytes("89 43 60 48 83 C4 08").CopyTo(Memory, 0x2800);
        new byte[64].CopyTo(Memory, 0x3800);
        BitConverter.GetBytes(100).CopyTo(Memory, 0x20040);
    }
    public void Start(int port = 0)
    {
        if (accepting is not null) throw new InvalidOperationException("Simulator already started.");
        if (port != 0) listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start(); accepting = AcceptAsync(lifetime.Token); Log?.Invoke($"Simulator listening on 127.0.0.1:{Port}.");
    }
    private async Task AcceptAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(ct); client.NoDelay = true;
                var task = ServeAsync(client, ct); connections[client] = task;
                _ = task.ContinueWith(_ => { connections.TryRemove(client, out var ignored); client.Dispose(); }, TaskScheduler.Default);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (SocketException) when (ct.IsCancellationRequested) { }
    }
    private async Task ServeAsync(TcpClient client, CancellationToken ct)
    {
        using var stream = client.GetStream(); TcpClient? events = null; var attached = false;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var header = new byte[12]; await stream.ReadExactlyAsync(header, ct);
                var (command, size) = Wire.ParseHeader(header); var body = new byte[size]; await stream.ReadExactlyAsync(body, ct);
                if (ResponseDelay > TimeSpan.Zero) await Task.Delay(ResponseDelay, ct);
                if (NextStatus is uint forced) { NextStatus = null; await Status(stream, forced, ct); continue; }
                switch (command)
                {
                    case Wire.Version: await StringReply(stream, ProtocolVersion, ct); break;
                    case Wire.Branding: await StringReply(stream, Branding, ct); break;
                    case Wire.Firmware: await Reply(stream, BitConverter.GetBytes(FirmwareVersion), ct); break;
                    case Wire.Platform: await Reply(stream, BitConverter.GetBytes(PlatformId), ct); break;
                    case Wire.Ping: await Status(stream, Wire.Success, ct); break;
                    case Wire.Processes:
                        var processes = new byte[76]; Wire.Put32(processes, 0, 2);
                        Text(processes, 4, "SceShellCore"); Wire.Put32(processes, 36, 1);
                        Text(processes, 40, "eboot.bin"); Wire.Put32(processes, 72, (uint)Game.Pid);
                        await Status(stream, Wire.Success, ct); await Reply(stream, processes, ct); break;
                    case Wire.Foreground:
                        var game = new byte[132]; Wire.Put32(game, 0, (uint)Game.Pid); Text(game, 4, Game.TitleId); Text(game, 20, "SIMULATED-CONTENT"); Text(game, 84, Game.ProcessName); Text(game, 124, Game.Version);
                        await Status(stream, Wire.Success, ct); await Reply(stream, game, ct); break;
                    case Wire.Maps:
                        if (body.Length != 4 || Wire.U32(body) != Game.Pid) { await Status(stream, 0xF0000001, ct); break; }
                        var maps = new byte[4 + 58 * Regions.Count]; Wire.Put32(maps, 0, (uint)Regions.Count);
                        for (var i = 0; i < Regions.Count; i++) { var m = Regions[i]; var o = 4 + i * 58; Text(maps, o, m.Name); Wire.Put64(maps, o + 32, m.Start); Wire.Put64(maps, o + 40, m.End); Wire.Put64(maps, o + 48, m.Offset); BinaryPrimitives.WriteUInt16LittleEndian(maps.AsSpan(o + 56, 2), checked((ushort)m.Protection)); }
                        await Status(stream, Wire.Success, ct); await Reply(stream, maps, ct); break;
                    case Wire.Read:
                    case Wire.Write:
                        if (DisconnectOnRead && command == Wire.Read) return;
                        if (body.Length != 16) { await Status(stream, 0xF0000003, ct); break; }
                        var pid = Wire.U32(body); var address = Wire.U64(body, 4); var length = Wire.U32(body, 12);
                        if (pid != Game.Pid || length is 0 or > 1048576 || !Regions.Any(m => m.Contains(address, (int)length))) { await Status(stream, 0xF0000001, ct); break; }
                        await Status(stream, Wire.Success, ct);
                        if (command == Wire.Read) await Reply(stream, Memory.AsSpan((int)(address - BaseAddress), (int)length).ToArray(), ct);
                        else
                        {
                            var bytes = new byte[length]; await stream.ReadExactlyAsync(bytes, ct); WriteCount++;
                            if (WriteCount == FailWriteNumber) { await Status(stream, 0xF0000001, ct); break; }
                            bytes.CopyTo(Memory, (int)(address - BaseAddress)); Written.Add((address, bytes));
                            await Status(stream, Wire.Success, ct);
                        }
                        break;
                    case Wire.Attach:
                        if (body.Length != 8 || Wire.U32(body) != Game.Pid || attached) { await Status(stream, 0xF0000004, ct); break; }
                        events = new TcpClient();
                        await events.ConnectAsync(new IPAddress(body.AsSpan(4, 4)), 755, ct);
                        if (FailAttachAfterCallback) { await Status(stream, 0xF0000001, ct); events.Dispose(); events = null; break; }
                        attached = true; await Status(stream, Wire.Success, ct); break;
                    case Wire.Detach: attached = false; events?.Dispose(); events = null; await Status(stream, Wire.Success, ct); break;
                    case Wire.Watchpoint:
                        if (!attached || body.Length != 24 || Wire.U32(body) > 3) { await Status(stream, 0xF0000005, ct); break; }
                        WatchpointPackets.Add(body.ToArray());
                        await Status(stream, Wire.Success, ct);
                        if (Wire.U32(body, 4) != 0 && events is not null) await SendInterrupt(events.GetStream(), ct);
                        break;
                    case Wire.Threads:
                        if (!attached) { await Status(stream, 0xF0000001, ct); break; }
                        await Status(stream, Wire.Success, ct); await Reply(stream, [1, 0, 0, 0, 101, 0, 0, 0], ct); break;
                    case Wire.Registers:
                        if (!attached || body.Length != 4) { await Status(stream, 0xF0000001, ct); break; }
                        var registers = new byte[184]; Wire.Put64(registers, 136, BaseAddress + 0x2803); Wire.Put64(registers, 160, BaseAddress + 0x2F000);
                        await Status(stream, Wire.Success, ct); await Reply(stream, registers, ct); break;
                    case Wire.Continue:
                    case Wire.StepThread:
                        await Status(stream, attached ? Wire.Success : 0xF0000001, ct);
                        if (attached && command == Wire.StepThread && events is not null) await SendInterrupt(events.GetStream(), ct);
                        break;
                    default: await Status(stream, 0xF0000001, ct); break;
                }
            }
        }
        catch (Exception e) when (e is IOException or SocketException or OperationCanceledException or InvalidDataException or ObjectDisposedException) { Log?.Invoke($"Simulator client closed: {e.Message}"); }
        finally { events?.Dispose(); }
    }
    private async Task SendInterrupt(NetworkStream stream, CancellationToken ct)
    {
        var packet = new byte[1184]; Wire.Put32(packet, 0, 101); Wire.Put32(packet, 4, 5); Text(packet, 8, "Simulated writer");
        Wire.Put64(packet, 48 + 136, BaseAddress + 0x2803); Wire.Put64(packet, 48 + 160, BaseAddress + 0x2F000); await Reply(stream, packet, ct);
    }
    private static void Text(byte[] b, int at, string value) => Encoding.UTF8.GetBytes(value).CopyTo(b, at);
    private Task Status(NetworkStream stream, uint s, CancellationToken ct) => Reply(stream, BitConverter.GetBytes(s), ct);
    private async Task StringReply(NetworkStream stream, string s, CancellationToken ct) { var b = Encoding.UTF8.GetBytes(s); await Reply(stream, BitConverter.GetBytes(b.Length), ct); await Reply(stream, b, ct); }
    private async Task Reply(NetworkStream stream, byte[] bytes, CancellationToken ct)
    {
        for (var at = 0; at < bytes.Length;)
        {
            var n = Math.Min(Math.Max(1, FragmentSize), bytes.Length - at); await stream.WriteAsync(bytes.AsMemory(at, n), ct); at += n;
        }
    }
    public async ValueTask DisposeAsync()
    {
        lifetime.Cancel(); listener.Stop(); foreach (var c in connections.Keys) c.Dispose();
        if (accepting is not null) await accepting;
        await Task.WhenAll(connections.Values); lifetime.Dispose();
    }
}
