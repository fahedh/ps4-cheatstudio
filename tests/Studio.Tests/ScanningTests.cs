using Studio.Core;
using Xunit;

namespace Studio.Tests;
public class ScanningTests
{
    [Theory] [InlineData("8943??4883C408")] [InlineData("89 43 ?? 48 83 C4 08")] [InlineData("89-43-?-48-83-C4-08")]
    public void WildcardsMatch(string pattern) => Assert.Equal([1], BytePattern.Parse(pattern).Find(Hex.Bytes("CC8943604883C408CC")));
    [Theory] [InlineData("")] [InlineData("?? ??")] [InlineData("89 4")] [InlineData("GG")]
    public void InvalidPatternsRejected(string pattern) => Assert.ThrowsAny<FormatException>(() => BytePattern.Parse(pattern));
    [Fact] public void SignatureMatchAtFinalByteIsIncluded() => Assert.Equal([1], BytePattern.Parse("BB CC").Find(Hex.Bytes("AA BB CC")));
    [Fact] public void MatchLimitIsExplicit() => Assert.Throws<InvalidDataException>(() => BytePattern.Parse("00").Find(new byte[10], 3));
    [Fact] public void CancellationIsObserved()
    {
        using var cts = new CancellationTokenSource(); cts.Cancel(); Assert.Throws<OperationCanceledException>(() => BytePattern.Parse("00").Find(new byte[10000], ct: cts.Token));
    }
    [Fact] public async Task PatternSpans64KiBReadBoundary()
    {
        await using var h = new Harness(); Hex.Bytes("12 34 56 78 9A BC").CopyTo(h.Server.Memory, 65534); await h.Connect();
        var matches = await new MemoryScanner(h.Session).PatternAsync(100, 0x400000, 131072, BytePattern.Parse("12 34 ?? 78 9A BC")); Assert.Equal([0x40FFFEUL], matches);
    }
    [Theory] [InlineData(ValueKind.Byte,"100")] [InlineData(ValueKind.Int16,"-123")] [InlineData(ValueKind.Int32,"1234567")] [InlineData(ValueKind.Int64,"9223372036854775806")] [InlineData(ValueKind.Float,"1.25")] [InlineData(ValueKind.Double,"1.234567890123")]
    public async Task ExactNumericScansFindTypedValues(ValueKind kind, string value)
    {
        await using var h = new Harness(); var bytes = Values.Parse(value, kind); bytes.CopyTo(h.Server.Memory, 0x20080); await h.Connect();
        var s = await new MemoryScanner(h.Session).InitialAsync(await h.Client.GetGameAsync(), 0x420000, 1024, kind, ScanComparison.Exact, value);
        Assert.Contains(s.Hits, hit => hit.Address == 0x420080 && hit.Value.SequenceEqual(bytes));
    }
    [Theory] [InlineData(ScanComparison.Increased,101)] [InlineData(ScanComparison.Decreased,99)] [InlineData(ScanComparison.Changed,77)] [InlineData(ScanComparison.Unchanged,100)] [InlineData(ScanComparison.Exact,99)]
    public async Task RescansUsePreviousBytes(ScanComparison comparison, int now)
    {
        await using var h = new Harness(); await h.Connect(); var scanner = new MemoryScanner(h.Session);
        var scan = await scanner.InitialAsync(await h.Client.GetGameAsync(), 0x420000, 1024, ValueKind.Int32, ScanComparison.Exact, "100");
        BitConverter.GetBytes(now).CopyTo(h.Server.Memory, 0x20040); var filtered = await scanner.RescanAsync(scan, comparison, now.ToString()); Assert.Single(filtered.Hits); Assert.Equal(0x420040UL, filtered.Hits[0].Address);
    }
    [Fact] public async Task UnknownInitialValueCapturesAndNarrows()
    {
        await using var h = new Harness(); await h.Connect(); var scanner = new MemoryScanner(h.Session);
        var scan = await scanner.InitialAsync(await h.Client.GetGameAsync(), 0x420000, 256, ValueKind.Int32, ScanComparison.Unknown, ""); Assert.Equal(64, scan.Hits.Count);
        BitConverter.GetBytes(9).CopyTo(h.Server.Memory, 0x20020); var filtered = await scanner.RescanAsync(scan, ScanComparison.Changed, ""); Assert.Single(filtered.Hits); Assert.Equal(0x420020UL, filtered.Hits[0].Address);
    }
    [Fact] public void Int64ComparisonDoesNotLoseLowBits() => Assert.Equal(1, Values.Compare(BitConverter.GetBytes(long.MaxValue), BitConverter.GetBytes(long.MaxValue - 1), ValueKind.Int64));
    [Fact] public async Task OldScanCannotBeAppliedAfterReconnect()
    {
        await using var h = new Harness(); await h.Connect(); var scanner = new MemoryScanner(h.Session); var scan = await scanner.InitialAsync(await h.Client.GetGameAsync(), 0x420000, 256, ValueKind.Int32, ScanComparison.Unknown, "");
        await h.Connect(); await Assert.ThrowsAsync<InvalidDataException>(() => scanner.RescanAsync(scan, ScanComparison.Unchanged, ""));
    }
    [Fact] public async Task OutOfBoundsScanRejectedBeforeMemoryRead()
    {
        await using var h = new Harness(); await h.Connect(); await Assert.ThrowsAsync<InvalidDataException>(() => new MemoryScanner(h.Session).SnapshotAsync(100, 0xDEAD, 256)); Assert.True(h.Client.IsConnected);
    }
}
