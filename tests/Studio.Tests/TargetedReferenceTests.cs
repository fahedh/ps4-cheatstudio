using Studio.Core;
using Xunit;

namespace Studio.Tests;

public class TargetedReferenceTests
{
    [Fact]
    public void LargeUnrelatedReferenceSetDoesNotHideIncomingCaveReference()
    {
        // More than a million unrelated short jumps used to abort cave analysis.
        var bytes = new byte[2 * 1000002 + 5];
        for (var i = 0; i < bytes.Length - 5; i += 2) { bytes[i] = 0xEB; bytes[i + 1] = 0; }
        bytes[^5] = 0xE9;
        var from = 0x400000UL + (ulong)bytes.Length - 5;
        var target = from + 5 + 100;
        BitConverter.GetBytes(100).CopyTo(bytes, bytes.Length - 4);
        var refs = CaveAnalysis.References(bytes, 0x400000, default, [(target, 16)]);
        Assert.Single(refs);
        Assert.Equal(target, refs[0].To);
    }

    [Fact]
    public void OverlappingRangesKeepEvidenceForEachRangeAfterOtherRangeFills()
    {
        var bytes = Enumerable.Repeat(new byte[] { 0xEB, 0 }, 20).SelectMany(b => b).ToArray();
        var refs = CaveAnalysis.References(bytes, 0x400000, default, [(0x400000, 100), (0x400020, 8)]);
        Assert.Contains(refs, r => r.To == 0x400020);
        Assert.Contains(refs, r => r.To == 0x400026);
        Assert.True(refs.Count <= 16);
    }
}
