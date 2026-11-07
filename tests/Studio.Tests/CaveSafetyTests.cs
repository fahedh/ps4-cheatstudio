using Studio.Core;
using Xunit;

namespace Studio.Tests;

public class CaveSafetyTests
{
    private static byte[] Memory => Enumerable.Repeat((byte)0xCC, 128).ToArray();
    private static PatchLine Cave => new("cave", 14, 0x200, new byte[12], Hex.Bytes("8943604883C408E9FBFEFFFF"), []);
    private static MemoryRegion Region => new(0, 0x400000, 0x400080, 0, 5, "code");

    [Fact] public void NoReferenceScanCanAutomaticallyProveThatPaddingIsSafe()
    {
        var memory = Memory; new byte[12].CopyTo(memory, 64);
        var assessment = CaveAnalysis.Assess(Cave, 0x400040, new byte[12], Region, CaveAnalysis.References(memory, Region.Start, default), memory, Region.Start, new HashSet<string>());
        Assert.True(assessment.CanReview); Assert.False(assessment.ConfirmedByUser); Assert.Contains("Manual cave confirmation", assessment.Explanation); Assert.Contains("not proven", assessment.Explanation);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public void ExistingBranchOrRipReferenceIntoPaddingBlocksUse(bool rip)
    {
        var memory = Memory; new byte[12].CopyTo(memory, 64);
        if (rip) Hex.Bytes("488D0539000000").CopyTo(memory, 0); // RIP after 7 bytes + 0x39 = 0x400040.
        else Hex.Bytes("E93B000000").CopyTo(memory, 0); // Next IP + 0x3B = 0x400040.
        var assessment = CaveAnalysis.Assess(Cave, 0x400040, new byte[12], Region, CaveAnalysis.References(memory, Region.Start, default), memory, Region.Start, new HashSet<string>());
        Assert.False(assessment.CanReview); Assert.Contains(assessment.Conflicts, c => c.Contains("reference"));
    }
    [Fact] public void MappingPermissionsAndFullRangeAreRequired()
    {
        var assessment = CaveAnalysis.Assess(Cave, 0x400040, new byte[12], Region with { Protection = 3 }, [], Memory, Region.Start, new HashSet<string>());
        Assert.False(assessment.CanReview); Assert.Contains(assessment.Conflicts, c => c.Contains("executable"));
    }
    [Fact] public void OriginalBytesAndExistingCodeCannotBeBypassedByConfirmation()
    {
        var bytes = Hex.Bytes("8943604883C4089090909090");
        var assessment = CaveAnalysis.Assess(Cave, 0x400040, bytes, Region, [], Memory, Region.Start, new HashSet<string>());
        Assert.False(assessment.CanReview); Assert.Contains(assessment.Conflicts, c => c.Contains("original")); Assert.Contains(assessment.Conflicts, c => c.Contains("existing non-padding"));
    }
    [Fact] public void CaveConfirmationIsBoundToLocationAndOriginalBytes()
    {
        var memory = Memory; new byte[12].CopyTo(memory, 64); new byte[12].CopyTo(memory, 80);
        var old = CaveAnalysis.Assess(Cave, 0x400040, new byte[12], Region, [], memory, Region.Start, new HashSet<string>());
        var changed = CaveAnalysis.Assess(Cave, 0x400050, new byte[12], Region, [], memory, Region.Start, new HashSet<string> { old.ReviewToken });
        Assert.NotEqual(old.ReviewToken, changed.ReviewToken); Assert.False(changed.ConfirmedByUser);
    }
    [Fact] public void RelocationRejectsJumpIntoMiddleOfInstruction()
    {
        var e = Assert.Throws<InvalidDataException>(() => CodeAnalysis.Relocate(Hex.Bytes("E9FCFFFFFF"), 0x1000, 0x2000, new Dictionary<ulong, ulong>(), true));
        Assert.Contains("inside an instruction", e.Message);
    }
    [Fact] public void InternalBranchRemainsInternalAfterRelocation()
    {
        var relocated = CodeAnalysis.Relocate(Hex.Bytes("750190C3"), 0x1000, 0x2000, new Dictionary<ulong, ulong>(), true);
        Assert.Equal(0x2003UL, CodeAnalysis.Disassemble(relocated, 0x2000)[0].BranchTarget);
    }
    [Fact] public void ExternalRelocatedTargetIsNotMistakenForAnOldInternalInstructionAddress()
    {
        // Old E9 targets 0x5000; that destination relocates to 0x1005, which happens to be an OLD NOP address.
        var relocated = CodeAnalysis.Relocate(Hex.Bytes("E9FB3F000090"), 0x1000, 0x3000, new Dictionary<ulong, ulong> { [0x5000] = 0x1005 }, true);
        Assert.Equal(0x1005UL, CodeAnalysis.Disassemble(relocated, 0x3000)[0].BranchTarget);
    }
    [Fact] public void ExplicitOriginalSectionBasesSupportCrossSectionHookAndReturn()
    {
        var hook = new PatchLine("hook", 14, 0x100, Hex.Bytes("8943604883C408"), Hex.Bytes("E9FB0F00009090"), []);
        var cave = Cave with { Section = 18, Offset = 0x100, Replacement = Hex.Bytes("8943604883C408E9FBEFFFFF") };
        var first = Memory; var second = Memory; hook.Original.CopyTo(first, 16); new byte[12].CopyTo(second, 32);
        var cheat = new Cheat("cross-section", "synthetic", [hook, cave]);
        var segments = new List<(MemoryRegion Region, byte[] Bytes)> { (Region, first), (new(1, 0x500000, 0x500080, 0, 5, "other code"), second) };
        var options = new PortingOptions { SourceSectionBases = new() { [14] = 0x1000, [18] = 0x2000 } };
        var preliminary = PortingEngine.AnalyzeSegments(cheat, segments, true, options: options); Assert.False(preliminary.CanTest);
        var token = preliminary.Candidates.Single(c => c.CodeCave).Cave!.ReviewToken;
        var result = PortingEngine.AnalyzeSegments(cheat, segments, true, options: options with { ReviewedCaves = [token] });
        Assert.True(result.CanTest, result.Summary); var newHook = result.ProposedPatches.Single(p => p.Id == "hook"); var newCave = result.ProposedPatches.Single(p => p.Id == "cave");
        Assert.Equal(0x500020UL, CodeAnalysis.Disassemble(newHook.Replacement, 0x400010)[0].BranchTarget);
        Assert.Equal(0x400017UL, CodeAnalysis.Disassemble(newCave.Replacement, 0x500020)[^1].BranchTarget); Assert.Contains("cave", newHook.Dependencies); Assert.True(newCave.CaveUseApproved);
    }
    [Fact] public async Task PatchBoundaryRejectsUnconfirmedPaddingEvenIfCallerOmitsCaveFlag()
    {
        await using var h = new Harness(); await h.Connect();
        var plan = h.Plan() with { Patches = [new("hidden-cave", 0x403800, new byte[12], Enumerable.Repeat((byte)0x90, 12).ToArray(), [])] };
        var e = await Assert.ThrowsAsync<InvalidDataException>(() => h.Patcher.ReviewAsync(plan)); Assert.Contains("explicit confirmation", e.Message); Assert.Equal(0, h.Server.WriteCount);
    }
    [Fact] public async Task CodePatchCannotBeTestedInNonExecutableDataMap()
    {
        await using var h = new Harness(); Hex.Bytes("8943604883C408").CopyTo(h.Server.Memory, 0x20080); await h.Connect();
        var plan = h.Plan(); plan.Patches[0] = plan.Patches[0] with { Address = 0x420080 };
        await Assert.ThrowsAsync<InvalidDataException>(() => h.Patcher.ReviewAsync(plan)); Assert.Equal(0, h.Server.WriteCount);
    }
    [Fact] public async Task DirectJumpCannotTargetAnUnmappedReturnLocation()
    {
        await using var h = new Harness(); await h.Connect();
        var plan = h.Plan(replacement: [.. CodeAnalysis.RelativeJump(0x402800, 0x600000), 0x90, 0x90]);
        var e = await Assert.ThrowsAsync<InvalidDataException>(() => h.Patcher.ReviewAsync(plan)); Assert.Contains("branch destination", e.Message); Assert.Equal(0, h.Server.WriteCount);
    }
    [Fact] public async Task PatchBoundaryAlsoEnforcesHookToCaveDependencyOrdering()
    {
        await using var h = new Harness(); await h.Connect();
        var plan = h.Plan(replacement: [.. CodeAnalysis.RelativeJump(0x402800, 0x403800), 0x90, 0x90]);
        plan.Patches.Add(new("cave", 0x403800, new byte[12], Enumerable.Repeat((byte)0x90, 12).ToArray(), [], true, true));
        var e = await Assert.ThrowsAsync<InvalidDataException>(() => h.Patcher.ReviewAsync(plan)); Assert.Contains("dependency", e.Message); Assert.Equal(0, h.Server.WriteCount);
    }
}
