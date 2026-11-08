using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;

namespace Studio.Core;

public static class Wire
{
    public const uint Magic = 0xFFAABBCC, Success = 0x80000000;
    public const uint Version = 0xBD000001, Firmware = 0xBD000500, Branding = 0xBD000501, Platform = 0xBD000502;
    public const uint Processes = 0xBDAA0001, Read = 0xBDAA0002, Write = 0xBDAA0003, Maps = 0xBDAA0004, Info = 0xBDAA000A, Foreground = 0xBDDD0006, Ping = 0xBDAACC06;
    public const uint Attach = 0xBDBB0001, Detach = 0xBDBB0002, Watchpoint = 0xBDBB0004, Threads = 0xBDBB0005, Registers = 0xBDBB0008, Continue = 0xBDBB0010, StepThread = 0xBDBB0013;
    public static byte[] Header(uint command, int length)
    {
        if (length is < 0 or > 1048576) throw new ArgumentOutOfRangeException(nameof(length));
        var b = new byte[12]; Put32(b, 0, Magic); Put32(b, 4, command); Put32(b, 8, (uint)length); return b;
    }
    public static (uint Command, int Length) ParseHeader(byte[] b)
    {
        if (b.Length != 12 || U32(b, 0) != Magic || U32(b, 8) > 1048576) throw new InvalidDataException("Invalid debugger request header.");
        return (U32(b, 4), (int)U32(b, 8));
    }
    public static uint U32(byte[] b, int o = 0) => BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(o));
    public static ulong U64(byte[] b, int o = 0) => BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(o));
    public static void Put32(byte[] b, int o, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(o), v);
    public static void Put64(byte[] b, int o, ulong v) => BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(o), v);
    public static string Text(byte[] b, int offset, int length) => Encoding.UTF8.GetString(b, offset, length).Split('\0')[0];
    public static byte[] Pid(int pid) { var b = new byte[4]; Put32(b, 0, (uint)pid); return b; }
    public static byte[] MemoryRequest(int pid, ulong address, int length)
    {
        var b = new byte[16]; Put32(b, 0, (uint)pid); Put64(b, 4, address); Put32(b, 12, (uint)length); return b;
    }
}

public sealed class DebuggerException(uint status) : IOException($"Debugger status 0x{status:X8}: {Meaning(status)}")
{
    public uint Status { get; } = status;
    private static string Meaning(uint s) => s switch { 0xF0000001 => "command failed or unsupported", 0xF0000002 => "too much data", 0xF0000003 => "missing data or authorization refused", 0xF0000004 => "another debug session is attached", 0xF0000005 => "invalid breakpoint/watchpoint index", _ => "unknown status" };
}

public sealed class DebuggerClient : IProcessMemory, IAsyncDisposable
{
    private TcpClient? tcp;
    private NetworkStream Stream => tcp?.GetStream() ?? throw new InvalidOperationException("Connect to the debugger first.");
    private readonly SemaphoreSlim wireGate = new(1, 1);
    private ConnectionSettings settings = new();
    public Guid ConnectionId { get; private set; }
    public DebuggerInfo? Information { get; private set; }
    public bool IsConnected => tcp is not null;
    public event Action<string>? Log;

    public async Task ConnectAsync(ConnectionSettings options, CancellationToken ct = default)
    {
        options.Validate(); settings = options;
        await Exchange(async token =>
        {
            Disconnect(); tcp = new TcpClient { NoDelay = true };
            await tcp.ConnectAsync(options.Host, options.DebugPort, token);
            ConnectionId = Guid.NewGuid();
            await Send(Wire.Version, [], token); var protocol = await StringReply(token);
            // This client implements the NG 1.3 framing; stop before probing undocumented older payloads.
            if (protocol != "1.3") throw new NotSupportedException($"Protocol {protocol} is not supported; use ps4debug-NG 1.3.4.");
            await Send(Wire.Branding, [], token); var brand = (await StringReply(token)).Split('\0');
            await Send(Wire.Firmware, [], token); var firmware = BinaryPrimitives.ReadUInt16LittleEndian(await Receive(2, token));
            await Send(Wire.Platform, [], token); var platform = BinaryPrimitives.ReadUInt16LittleEndian(await Receive(2, token));
            Information = new(protocol, brand[0], brand.Length > 1 ? brand[1] : "0.0", firmware, platform);
            if (!Information.IsNg || platform != 4) throw new NotSupportedException("This connection is not a supported ps4debug-NG PS4 endpoint.");
            Log?.Invoke($"Connected: {Information.Brand}; firmware {Information.FirmwareDisplay}; capability {Information.Capability}.");
            return true;
        }, ct);
    }
    public void Disconnect() { tcp?.Dispose(); tcp = null; Information = null; ConnectionId = Guid.Empty; }
    private async Task<T> Exchange<T>(Func<CancellationToken, Task<T>> action, CancellationToken ct)
    {
        await wireGate.WaitAsync(ct);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(settings.TimeoutSeconds));
        try { return await action(timeout.Token); }
        catch (Exception e)
        {
            // A failed/cancelled exchange may leave unread bytes. Never reuse a desynchronized stream.
            Disconnect(); Log?.Invoke($"Connection closed after {e.GetType().Name}: {e.Message}");
            if (e is OperationCanceledException && !ct.IsCancellationRequested) throw new TimeoutException("Debugger exchange timed out; reconnect before retrying.", e);
            throw;
        }
        finally { wireGate.Release(); }
    }
    internal Task<T> CommandAsync<T>(uint command, byte[] body, Func<NetworkStream, CancellationToken, Task<T>> response, CancellationToken ct = default) => Exchange(async t => { await Send(command, body, t); await Status(t); return await response(Stream, t); }, ct);
    internal Task<bool> SimpleAsync(uint command, byte[] body, CancellationToken ct = default) => CommandAsync(command, body, (_, _) => Task.FromResult(true), ct);
    private async Task Send(uint command, byte[] body, CancellationToken ct)
    {
        await Stream.WriteAsync(Wire.Header(command, body.Length), ct);
        if (body.Length > 0) await Stream.WriteAsync(body, ct);
    }
    private async Task<byte[]> Receive(int length, CancellationToken ct)
    {
        if (length is < 0 or > 16 * 1024 * 1024) throw new InvalidDataException("Debugger response exceeds the bound.");
        var b = new byte[length]; await Stream.ReadExactlyAsync(b, ct); return b;
    }
    private async Task Status(CancellationToken ct)
    {
        var s = Wire.U32(await Receive(4, ct)); if (s != Wire.Success) throw new DebuggerException(s);
    }
    private async Task<string> StringReply(CancellationToken ct)
    {
        var n = Wire.U32(await Receive(4, ct)); if (n is 0 or > 4096) throw new InvalidDataException("Invalid debugger string length.");
        return Encoding.UTF8.GetString(await Receive((int)n, ct));
    }
    public Task<bool> PingAsync(CancellationToken ct = default) => SimpleAsync(Wire.Ping, [], ct);
    public Task<IReadOnlyList<ProcessEntry>> GetProcessesAsync(CancellationToken ct = default) => CommandAsync<IReadOnlyList<ProcessEntry>>(Wire.Processes, [], async (_, t) =>
    {
        var n = Wire.U32(await Receive(4, t)); if (n > 16384) throw new InvalidDataException("Invalid process count.");
        var b = await Receive(checked((int)n * 36), t); var result = new List<ProcessEntry>();
        for (var i = 0; i < (int)n; i++) result.Add(new((int)Wire.U32(b, i * 36 + 32), Wire.Text(b, i * 36, 32)));
        return result;
    }, ct);
    public Task<GameIdentity> GetGameAsync(CancellationToken ct = default) => CommandAsync(Wire.Foreground, [], async (_, t) =>
    {
        var b = await Receive(132, t); var name = Wire.Text(b, 84, 40);
        return new GameIdentity((int)Wire.U32(b), name, Wire.Text(b, 4, 16), Wire.Text(b, 124, 8), name);
    }, ct);
    public Task<IReadOnlyList<MemoryRegion>> GetMapsAsync(int pid, CancellationToken ct = default) => CommandAsync<IReadOnlyList<MemoryRegion>>(Wire.Maps, Wire.Pid(pid), async (_, t) =>
    {
        var n = Wire.U32(await Receive(4, t)); if (n > 65536) throw new InvalidDataException("Invalid memory map count.");
        var b = await Receive(checked((int)n * 58), t); var result = new List<MemoryRegion>();
        for (var i = 0; i < (int)n; i++)
        {
            var o = i * 58; var start = Wire.U64(b, o + 32); var end = Wire.U64(b, o + 40);
            // The live NG endpoint may include a completely empty sentinel slot.
            // Retain wire indices for every real map; reject other invalid ranges.
            if (b.AsSpan(o, 58).IndexOfAnyExcept((byte)0) < 0) continue;
            if (end <= start) throw new InvalidDataException("Memory map end must exceed start.");
            result.Add(new(i, start, end, Wire.U64(b, o + 48), BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(o + 56, 2)), Wire.Text(b, o, 32)));
        }
        return result;
    }, ct);
    public Task<byte[]> ReadAsync(int pid, ulong address, int length, CancellationToken ct = default)
    {
        if (pid <= 0 || length is < 1 or > 16 * 1024 * 1024 || address > ulong.MaxValue - (ulong)length) throw new ArgumentOutOfRangeException(nameof(length));
        return Exchange(async t =>
        {
            var output = new byte[length];
            for (var done = 0; done < length;)
            {
                var n = Math.Min(65536, length - done);
                await Send(Wire.Read, Wire.MemoryRequest(pid, address + (ulong)done, n), t); await Status(t);
                (await Receive(n, t)).CopyTo(output, done); done += n;
            }
            return output;
        }, ct);
    }
    // Ordinary writes are deliberately internal; PatchService is the public write boundary.
    Task IProcessMemory.WriteAsync(int pid, ulong address, byte[] bytes, CancellationToken ct) => WriteCoreAsync(pid, address, bytes, ct);
    private async Task WriteCoreAsync(int pid, ulong address, byte[] bytes, CancellationToken ct)
    {
        if (Information?.VerifiedWrites != true) throw new NotSupportedException("Memory writes require ps4debug-NG capability 1.1 with verified terminal status.");
        if (bytes.Length is < 1 or > 1048576) throw new ArgumentOutOfRangeException(nameof(bytes));
        await Exchange(async t =>
        {
            await Send(Wire.Write, Wire.MemoryRequest(pid, address, bytes.Length), t); await Status(t);
            await Stream.WriteAsync(bytes, t); await Status(t); return true;
        }, ct);
    }
    public ValueTask DisposeAsync() { Disconnect(); return ValueTask.CompletedTask; }
}
