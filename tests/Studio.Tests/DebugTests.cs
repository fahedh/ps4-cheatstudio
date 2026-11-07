using System.Net;
using System.Net.Sockets;
using Studio.Core;
using Studio.Mock;
using Xunit;

namespace Studio.Tests;
[CollectionDefinition("Reverse debug port", DisableParallelization = true)] public class DebugPortCollection;

[Collection("Reverse debug port")]
public class DebugTests
{
    [Fact] public async Task AttachWatchpointInterruptRegistersStepAndDetach()
    {
        await using var h = new Harness(); await h.Connect(); await using var debug = new DebugSession(h.Client);
        var hit = new TaskCompletionSource<DebugInterrupt>(TaskCreationOptions.RunContinuationsAsynchronously); debug.Interrupted += i => hit.TrySetResult(i);
        await debug.AttachAsync(await h.Client.GetGameAsync(), IPAddress.Loopback, IPAddress.Loopback); Assert.True(debug.Attached);
        await debug.WatchAsync(0, 0x420040, 4, true); var interrupt = await hit.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(101U, interrupt.ThreadId); Assert.Equal(0x402803UL, interrupt.Rip); Assert.Equal(0x42F000UL, interrupt.Rsp);
        var registers = await debug.GetRegistersAsync(101); Assert.Equal(184, registers.Length); Assert.Equal(0x402803UL, Wire.U64(registers, 136));
        await debug.StepAsync(101); await debug.ResumeAsync(); await debug.WatchAsync(0, 0x420040, 4, false); await debug.DetachAsync(); Assert.False(debug.Attached); Assert.True(await h.Client.PingAsync());
    }
    [Fact] public async Task UnsupportedInterruptPortIsReportedBeforeAttachment()
    {
        await using var h = new Harness(); await h.Connect(); await using var debug = new DebugSession(h.Client);
        await Assert.ThrowsAsync<NotSupportedException>(() => debug.AttachAsync(awaitGame(), IPAddress.Loopback, IPAddress.Loopback, 1755));
        Assert.False(debug.Attached); Assert.True(h.Client.IsConnected);
        GameIdentity awaitGame() => h.Server.Game with { Name = "eboot.bin" };
    }
    [Fact] public async Task WatchpointAlignmentAndLengthChecked()
    {
        await using var h = new Harness(); await h.Connect(); await using var debug = new DebugSession(h.Client);
        await debug.AttachAsync(await h.Client.GetGameAsync(), IPAddress.Loopback, IPAddress.Loopback);
        await Assert.ThrowsAsync<ArgumentException>(() => debug.WatchAsync(0, 0x420041, 4, true)); await Assert.ThrowsAsync<ArgumentException>(() => debug.WatchAsync(4, 0x420040, 4, true));
    }
    [Fact] public async Task ConfiguredSimulatorPortCanBeUsedByStandaloneHost()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0); probe.Start(); var port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
        await using var server = new MockDebuggerServer(); server.Start(port); Assert.Equal(port, server.Port);
        await using var client = new DebuggerClient(); await client.ConnectAsync(new() { Host = "127.0.0.1", DebugPort = port }); Assert.True(await client.PingAsync());
    }
}
