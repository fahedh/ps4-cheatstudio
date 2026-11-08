using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Studio.Core;
using Xunit;

namespace Studio.Tests;

public class MapWireTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PackedUpstreamMapsLeaveConnectionSynchronized(bool emptyFirstSlot)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var peer = Task.Run(async () =>
        {
            using var socket = await listener.AcceptTcpClientAsync(timeout.Token);
            using var stream = socket.GetStream();
            async Task Expect(uint command, int bodyLength, byte[] response)
            {
                var request = new byte[12 + bodyLength];
                await stream.ReadExactlyAsync(request, timeout.Token);
                Assert.Equal(0xFFAABBCCu, BinaryPrimitives.ReadUInt32LittleEndian(request));
                Assert.Equal(command, BinaryPrimitives.ReadUInt32LittleEndian(request.AsSpan(4)));
                Assert.Equal((uint)bodyLength, BinaryPrimitives.ReadUInt32LittleEndian(request.AsSpan(8)));
                if (bodyLength == 4) Assert.Equal(1409u, BinaryPrimitives.ReadUInt32LittleEndian(request.AsSpan(12)));
                await stream.WriteAsync(response, timeout.Token);
            }
            static byte[] StringReply(string text)
            {
                var bytes = Encoding.UTF8.GetBytes(text);
                var reply = new byte[4 + bytes.Length];
                BinaryPrimitives.WriteUInt32LittleEndian(reply, (uint)bytes.Length);
                bytes.CopyTo(reply, 4); return reply;
            }
            await Expect(0xBD000001, 0, StringReply("1.3"));
            await Expect(0xBD000501, 0, StringReply("ps4debug-NG by Pharaoh2k v1.3.4\0" + "1.1"));
            await Expect(0xBD000500, 0, [0x48, 0x05]);
            await Expect(0xBD000502, 0, [4, 0]);
            // Independent fixture: upstream packed name[32], u64 start/end/offset, u16 prot.
            var prefix = emptyFirstSlot ? 1 : 0;
            var maps = new byte[8 + (2 + prefix) * 58];
            BinaryPrimitives.WriteUInt32LittleEndian(maps, 0x80000000);
            BinaryPrimitives.WriteUInt32LittleEndian(maps.AsSpan(4), (uint)(2 + prefix));
            for (var i = 0; i < 2; i++)
            {
                var o = 8 + (i + prefix) * 58;
                Encoding.UTF8.GetBytes(i == 0 ? "eboot.bin" : "Il2CppUserAssemblies.prx").CopyTo(maps, o);
                BinaryPrimitives.WriteUInt64LittleEndian(maps.AsSpan(o + 32), 0x100000000UL + (ulong)i * 0x4000);
                BinaryPrimitives.WriteUInt64LittleEndian(maps.AsSpan(o + 40), 0x100002000UL + (ulong)i * 0x4000);
                BinaryPrimitives.WriteUInt64LittleEndian(maps.AsSpan(o + 48), (ulong)i * 0x2000);
                BinaryPrimitives.WriteUInt16LittleEndian(maps.AsSpan(o + 56), (ushort)(i == 0 ? 5 : 3));
            }
            await Expect(0xBDAA0004, 4, maps);
            await Expect(0xBDAACC06, 0, [0, 0, 0, 0x80]);
        }, timeout.Token);
        try
        {
            await using var client = new DebuggerClient();
            await client.ConnectAsync(new() { Host = "127.0.0.1", DebugPort = ((IPEndPoint)listener.LocalEndpoint).Port }, timeout.Token);
            var maps = await client.GetMapsAsync(1409, timeout.Token);
            Assert.Equal(2, maps.Count);
            Assert.Equal(emptyFirstSlot ? 1 : 0, maps[0].Index);
            Assert.Equal(emptyFirstSlot ? 2 : 1, maps[1].Index);
            Assert.Equal("eboot.bin", maps[0].Name);
            Assert.Equal("Il2CppUserAssemblies.prx", maps[1].Name);
            Assert.Equal(0x100004000UL, maps[1].Start);
            Assert.Equal(0x100006000UL, maps[1].End);
            Assert.Equal(0x2000UL, maps[1].Offset);
            Assert.Equal(5UL, maps[0].Protection);
            Assert.Equal(3UL, maps[1].Protection);
            Assert.True(await client.PingAsync(timeout.Token));
            await peer;
        }
        finally { listener.Stop(); }
    }
}
