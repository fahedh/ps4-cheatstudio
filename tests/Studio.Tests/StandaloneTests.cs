using System.Net.Sockets;
using Studio.Core;
using Xunit;

namespace Studio.Tests;

public class StandaloneTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(2, 1)]
    public async Task LoaderSendsOnceOnlyWhenRefused(int refusals, int expected)
    {
        int attempts = 0, sent = 0;
        await PayloadBootstrap.ConnectAsync(_ => attempts++ < refusals
            ? Task.FromException(new SocketException((int)SocketError.ConnectionRefused)) : Task.CompletedTask,
            _ => { sent++; return Task.CompletedTask; }, default);
        Assert.Equal(expected, sent);
    }

    [Fact]
    public async Task IncompatibleListenerNeverTriggersPayload()
    {
        await Assert.ThrowsAsync<NotSupportedException>(() => PayloadBootstrap.ConnectAsync(
            _ => throw new NotSupportedException(), _ => throw new InvalidOperationException(), default));
    }

    [Fact]
    public void NeighborSeparatesIdenticalHooksAndIgnoresDuplicateAnchors()
    {
        var hook = new PatchLine("hook", 14, 100, [1,2,3,4], [5,6,7,8], []);
        var anchor = hook with { Offset = 108, Original = [9,10,11,12] };
        byte[] segment = [1,2,3,4,0,0,0,0,9,10,11,12,1,2,3,4,0,0,0,0,1,1,1,1];
        Assert.Equal(1, PortingEngine.CountMatchingNeighbors(hook, 0, segment, [hook, anchor, anchor]));
        Assert.Equal(0, PortingEngine.CountMatchingNeighbors(hook, 12, segment, [hook, anchor]));
    }
}
