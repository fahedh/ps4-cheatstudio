using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Studio.Core;
using Xunit;

namespace Studio.Tests;

public class ProtocolAuditTests
{
    [Theory] [InlineData("1.2")] [InlineData("2.0")]
    public async Task UnknownProtocolIsRejectedBeforeIssuingUndocumentedCommands(string version)
    {
        await using var h = new Harness(); h.Server.ProtocolVersion = version;
        await Assert.ThrowsAsync<NotSupportedException>(() => h.Connect()); Assert.False(h.Client.IsConnected);
    }
    [Fact] public async Task UnadvertisedVerifiedWriteCapabilityCannotBeAssumed()
    {
        await using var h = new Harness(); h.Server.Branding = "ps4debug-NG by Pharaoh2k v1.3.4 [SIMULATOR]\0" + "1.0"; await h.Connect();
        Assert.False(h.Client.Information!.VerifiedWrites); Assert.True(await h.Client.PingAsync());
        await Assert.ThrowsAsync<NotSupportedException>(() => ((IProcessMemory)h.Client).WriteAsync(100, 0x402800, Hex.Bytes("90"))); Assert.Equal(0, h.Server.WriteCount);
    }
    [Fact] public async Task UnknownFirmwareIsReportedAsUnavailableRatherThanInvented()
    {
        await using var h = new Harness(); h.Server.FirmwareVersion = ushort.MaxValue; await h.Connect(); Assert.Equal("Unavailable", h.Client.Information!.FirmwareDisplay);
    }
    [Fact] public async Task MissingOrMalformedGameVersionCannotAuthorizeWrites()
    {
        await using var h = new Harness(); h.Server.Game = h.Server.Game with { Version = "unknown" }; await h.Connect();
        await Assert.ThrowsAsync<InvalidDataException>(() => h.Patcher.ReviewAsync(h.Plan())); Assert.Equal(0, h.Server.WriteCount);
    }
    [Fact] public async Task HandshakeMatchesIndependentPublishedByteFixtures()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var peer = Task.Run(async () =>
        {
            using var socket = await listener.AcceptTcpClientAsync(timeout.Token); using var stream = socket.GetStream();
            async Task Expect(string header, byte[] response)
            {
                var received = new byte[12]; await stream.ReadExactlyAsync(received, timeout.Token); Assert.Equal(Convert.FromHexString(header), received);
                foreach (var b in response) await stream.WriteAsync(new byte[] { b }, timeout.Token);
            }
            // Literals from PROTOCOL.md: no generic status word precedes these four info replies.
            await Expect("CCBBAAFF010000BD00000000", [3, 0, 0, 0, (byte)'1', (byte)'.', (byte)'3']);
            var brand = Encoding.UTF8.GetBytes("ps4debug-NG by Pharaoh2k v1.3.4\0" + "1.1"); var branding = new byte[4 + brand.Length]; BinaryPrimitives.WriteUInt32LittleEndian(branding, (uint)brand.Length); brand.CopyTo(branding, 4);
            await Expect("CCBBAAFF010500BD00000000", branding);
            await Expect("CCBBAAFF000500BD00000000", [0x48, 0x05]); // decimal 1352, not BCD 0x1352.
            await Expect("CCBBAAFF020500BD00000000", [4, 0]);
        }, timeout.Token);
        try
        {
            await using var client = new DebuggerClient(); await client.ConnectAsync(new() { Host = "127.0.0.1", DebugPort = port }, timeout.Token);
            Assert.Equal("13.52", client.Information!.FirmwareDisplay); Assert.True(client.Information.VerifiedWrites); await peer;
        }
        finally { listener.Stop(); }
    }
}

[Collection("Reverse debug port")]
public class DebugProtocolAuditTests
{
    [Theory] [InlineData(1,0)] [InlineData(2,1)] [InlineData(4,3)] [InlineData(8,2)]
    public async Task WatchpointUsesPackedDr7WidthAndWriteType(int bytes, int encoded)
    {
        await using var h = new Harness(); h.Server.FragmentSize = 1; await h.Connect(); await using var debug = new DebugSession(h.Client);
        await debug.AttachAsync(await h.Client.GetGameAsync(), IPAddress.Loopback, IPAddress.Loopback);
        await debug.WatchAsync(0, 0x420040, bytes, true);
        var expected = Convert.FromHexString("000000000100000000000000010000004000420000000000"); expected[8] = (byte)encoded;
        Assert.Equal(expected, Assert.Single(h.Server.WatchpointPackets));
    }
    [Fact] public async Task FailedAttachAfterReverseCallbackCleansUpAndCanReconnect()
    {
        await using var h = new Harness(); h.Server.FailAttachAfterCallback = true; await h.Connect(); await using var debug = new DebugSession(h.Client);
        await Assert.ThrowsAsync<DebuggerException>(() => debug.AttachAsync(awaitGame(), IPAddress.Loopback, IPAddress.Loopback)); Assert.False(debug.Attached); Assert.False(h.Client.IsConnected);
        h.Server.FailAttachAfterCallback = false; await h.Connect();
        await debug.AttachAsync(await h.Client.GetGameAsync(), IPAddress.Loopback, IPAddress.Loopback); Assert.True(debug.Attached); await debug.DetachAsync(); Assert.True(await h.Client.PingAsync());
        GameIdentity awaitGame() => h.Server.Game with { Name = "eboot.bin" };
    }
}
