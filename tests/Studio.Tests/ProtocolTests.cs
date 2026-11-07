using Studio.Core;
using Studio.Mock;
using Xunit;

namespace Studio.Tests;

public sealed class Harness : IAsyncDisposable
{
    public MockDebuggerServer Server { get; } = new();
    public DebuggerClient Client { get; } = new();
    public MemorySession Session { get; }
    public string JournalDirectory { get; } = Path.Combine(Path.GetTempPath(), "studio-test-" + Guid.NewGuid().ToString("N"));
    public Harness() { Server.Start(); Session = new(Client); }
    public Task Connect(int timeout = 3) => Client.ConnectAsync(new() { Host = "127.0.0.1", DebugPort = Server.Port, TimeoutSeconds = timeout });
    public PatchPlan Plan(byte[]? original = null, byte[]? replacement = null) => new(Client.ConnectionId, Server.Game with { Name = "eboot.bin" }, "Fixture", [new("p1", 0x402800, original ?? Hex.Bytes("8943604883C408"), replacement ?? Hex.Bytes("90909090909090"), [])], true);
    public PatchService Patcher => new(Session, JournalDirectory);
    public async ValueTask DisposeAsync() { await Client.DisposeAsync(); await Server.DisposeAsync(); if (Directory.Exists(JournalDirectory)) Directory.Delete(JournalDirectory, true); }
}

public class ProtocolTests
{
    [Fact] public void HeaderHasDocumentedLittleEndianFraming()
    {
        Assert.Equal(Hex.Bytes("CCBBAAFF0200AABD10000000"), Wire.Header(Wire.Read, 16));
        Assert.Equal((Wire.Read, 16), Wire.ParseHeader(Wire.Header(Wire.Read, 16)));
    }
    [Theory] [InlineData("00")] [InlineData("000000000200AABD10000000")] [InlineData("CCBBAAFF0200AABDFFFFFFFF")]
    public void InvalidHeaderRejected(string bytes) => Assert.Throws<InvalidDataException>(() => Wire.ParseHeader(Hex.Bytes(bytes)));
    [Fact] public async Task FragmentedHandshakeEnumerationAndMaps()
    {
        await using var h = new Harness(); h.Server.FragmentSize = 1; await h.Connect();
        Assert.Equal("1.3", h.Client.Information!.Protocol); Assert.Equal("13.52", h.Client.Information.FirmwareDisplay); Assert.True(h.Client.Information.VerifiedWrites);
        var processes = await h.Client.GetProcessesAsync(); Assert.Contains(processes, p => p.Pid == 100 && p.Name == "eboot.bin");
        var game = await h.Client.GetGameAsync(); Assert.Equal("CUSA39420", game.TitleId); Assert.Equal("01.05", game.Version);
        var maps = await h.Client.GetMapsAsync(100); Assert.Equal(2, maps.Count); Assert.Equal(0x400000UL, maps[0].Start); Assert.True(maps[0].Executable); Assert.Equal("rw-", maps[1].Permissions);
    }
    [Fact] public async Task ChunkedReadsConsumeExactlyTheExpectedBytes()
    {
        await using var h = new Harness(); await h.Connect();
        var data = await h.Client.ReadAsync(100, 0x400000, 0x20000); Assert.Equal(h.Server.Memory[..0x20000], data);
        Assert.True(await h.Client.PingAsync());
    }
    [Fact] public async Task InvalidProcessAndAddressReturnDocumentedError()
    {
        await using var h = new Harness(); await h.Connect();
        var e = await Assert.ThrowsAsync<DebuggerException>(() => h.Client.ReadAsync(100, 0xDEAD, 16)); Assert.Equal(0xF0000001U, e.Status); Assert.False(h.Client.IsConnected);
    }
    [Fact] public async Task StatusErrorClosesStreamToPreventDesynchronization()
    {
        await using var h = new Harness(); await h.Connect(); h.Server.NextStatus = 0xF0000003;
        await Assert.ThrowsAsync<DebuggerException>(() => h.Client.PingAsync()); Assert.False(h.Client.IsConnected);
    }
    [Fact] public async Task CancelledReadRequiresFreshConnection()
    {
        await using var h = new Harness(); await h.Connect(); var id = h.Client.ConnectionId; h.Server.ResponseDelay = TimeSpan.FromMilliseconds(300);
        using var ct = new CancellationTokenSource(40);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.Client.ReadAsync(100, 0x400000, 16, ct.Token)); Assert.False(h.Client.IsConnected);
        h.Server.ResponseDelay = TimeSpan.Zero; await h.Connect(); Assert.NotEqual(id, h.Client.ConnectionId); Assert.True(await h.Client.PingAsync());
    }
    [Fact] public async Task TimeoutReportedAccurately()
    {
        await using var h = new Harness(); await h.Connect(1); h.Server.ResponseDelay = TimeSpan.FromSeconds(2);
        await Assert.ThrowsAsync<TimeoutException>(() => h.Client.PingAsync()); Assert.False(h.Client.IsConnected);
    }
    [Fact] public async Task DroppedSocketCanReconnect()
    {
        await using var h = new Harness(); await h.Connect(); h.Server.DisconnectOnRead = true;
        await Assert.ThrowsAnyAsync<IOException>(() => h.Client.ReadAsync(100, 0x400000, 16)); h.Server.DisconnectOnRead = false; await h.Connect(); Assert.True(await h.Client.PingAsync());
    }
    [Fact] public async Task ConcurrentRequestsStayFramed()
    {
        await using var h = new Harness(); await h.Connect();
        var requests = Enumerable.Range(0, 30).Select(i => h.Client.ReadAsync(100, 0x402800, 7));
        foreach (var result in await Task.WhenAll(requests)) Assert.Equal(Hex.Bytes("8943604883C408"), result);
    }
    [Fact] public async Task InvalidMapOrderingIsRejected()
    {
        await using var h = new Harness(); h.Server.Regions = [new(0, 0x400000, 0x300000, 0, 5, "invalid")]; await h.Connect();
        await Assert.ThrowsAsync<InvalidDataException>(() => h.Client.GetMapsAsync(100)); Assert.False(h.Client.IsConnected);
    }
    [Fact] public void MemoryRegionUsesOverflowSafeBounds()
    {
        var map = new MemoryRegion(0, 0x400000, 0x401000, 0, 5, "code"); Assert.True(map.Contains(0x400FFF, 1)); Assert.False(map.Contains(0x400FFF, 2)); Assert.False(map.Contains(ulong.MaxValue, 10));
    }
}
