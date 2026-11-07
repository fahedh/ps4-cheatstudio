using System.Text.Json;
using Studio.Core;
using Xunit;

namespace Studio.Tests;
public record FixturePatch(string Id, string Offset, string Original, string Replacement);
public record PortFixture(string Name, bool SimulatedOnly, string Base, string TargetHex, List<FixturePatch> Patches, bool ExpectedTestable, int ExpectedMatches);

public class PortingTests
{
    [Theory] [InlineData("unchanged")] [InlineData("relocated")] [InlineData("ambiguous")] [InlineData("changed")] [InlineData("invalid-cave")] [InlineData("missing")] [InlineData("relative-multiline")]
    public void AcceptanceFixturesHaveHonestOutcomes(string name)
    {
        var f = JsonSerializer.Deserialize<PortFixture>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "examples", "fixtures", name + ".json")))!;
        Assert.True(f.SimulatedOnly); var bytes = Hex.Bytes(f.TargetHex); var start = Hex.Address(f.Base);
        var cheat = new Cheat(f.Name, "Synthetic acceptance case", f.Patches.Select(p => new PatchLine(p.Id, 14, Hex.Address(p.Offset), Hex.Bytes(p.Original), Hex.Bytes(p.Replacement), [])).ToList());
        var result = PortingEngine.AnalyzeSegments(cheat, [(new(0, start, start + (ulong)bytes.Length, 0, 5, "executable"), bytes)], true);
        if (name == "relative-multiline")
        {
            // Safety correction: padding must first be explicitly reviewed, even in a deterministic fixture.
            Assert.False(result.CanTest); Assert.Contains(result.Warnings, w => w.Contains("padding is not proof"));
            var cave = Assert.Single(result.Candidates, c => c.CodeCave); Assert.True(cave.Cave!.CanReview); Assert.False(cave.Cave.ConfirmedByUser);
            result = PortingEngine.AnalyzeSegments(cheat, [(new(0, start, start + (ulong)bytes.Length, 0, 5, "executable"), bytes)], true,
                options: new() { ReviewedCaves = [cave.Cave.ReviewToken] });
        }
        Assert.True(f.ExpectedTestable == result.CanTest, result.Summary); Assert.Equal(f.ExpectedMatches, result.Candidates.Count);
        if (!f.ExpectedTestable) { Assert.Empty(result.ProposedPatches); Assert.Contains("Manual verification required", result.Summary); }
        else Assert.All(result.Candidates, c => Assert.Equal("Generated candidate — not verified", c.Status));
        if (name == "unchanged") Assert.Equal(0x1B6BFEDUL, result.Candidates.Single().Address);
        if (name == "relocated") Assert.NotEqual(0x1B6BFEDUL, result.Candidates.Single().Address);
        if (name == "relative-multiline")
        {
            Assert.Equal(Hex.Bytes("E92B0000009090"), result.ProposedPatches.Single(p => p.Id == "hook").Replacement);
            Assert.Equal(Hex.Bytes("8943604883C408E9CBFFFFFF"), result.ProposedPatches.Single(p => p.Id == "cave").Replacement);
            Assert.Contains("cave", result.ProposedPatches.Single(p => p.Id == "hook").Dependencies);
        }
    }
    [Theory] [InlineData(0x1000,0x2000,"E9FB0F0000")] [InlineData(0x2000,0x1000,"E9FBEFFFFF")]
    public void Rel32DisplacementsAreSignedAndRelativeToNextInstruction(long from, long to, string expected) => Assert.Equal(Hex.Bytes(expected), CodeAnalysis.RelativeJump((ulong)from, (ulong)to));
    [Fact] public void OutOfRangeRelativeJumpRejected() => Assert.Throws<InvalidDataException>(() => CodeAnalysis.RelativeJump(0, 0xFFFFFFFF00000000));
    [Fact] public void NopPatchRequiresWholeInstructions()
    {
        Assert.Equal(7, CodeAnalysis.Nops(Hex.Bytes("8943604883C408"), 0x1000).Length); Assert.Throws<InvalidDataException>(() => CodeAnalysis.Nops(Hex.Bytes("48 83 C4"), 0x1000));
    }
    [Fact] public void DisassemblyIdentifiesWriteAndJump()
    {
        var instructions = CodeAnalysis.Disassemble(Hex.Bytes("894360E910000000"), 0x1000); Assert.Contains("mov", instructions[0].Assembly); Assert.Equal(0x1018UL, instructions[1].BranchTarget);
    }
    [Fact] public void RelativeSignatureMasksOnlyRelocatableOperands()
    {
        var pattern = CodeAnalysis.Signature(Hex.Bytes("E812345678894360")); Assert.True(pattern.Matches(Hex.Bytes("E800000000894360"), 0)); Assert.False(pattern.Matches(Hex.Bytes("E800000000894361"), 0));
    }
    [Fact] public void UnknownBranchTargetNeverInvented()
    {
        Assert.Throws<InvalidDataException>(() => CodeAnalysis.Relocate(Hex.Bytes("E910000000"), 0x1000, 0x2000, new Dictionary<ulong, ulong>(), true));
    }
    [Fact] public void CaveDetectionDoesNotClaimFreeMemory()
    {
        var cave = CodeAnalysis.FindCaves(new byte[32], 0x1000, 16).Single(); Assert.Equal(32, cave.Length); Assert.Contains("does not prove", cave.Explanation);
    }
    [Fact] public void AmbiguityAcrossMemoryRegionsIsNotHidden()
    {
        var bytes = Hex.Bytes("8943604883C408CC"); var cheat = new Cheat("test", "", [new("p", 14, 0x100, bytes[..7], Hex.Bytes("90909090909090"), [])]);
        var result = PortingEngine.AnalyzeSegments(cheat, [(new(0, 0x400000, 0x400008, 0, 5, "a"), bytes), (new(1, 0x600000, 0x600008, 0, 5, "b"), bytes)], true); Assert.False(result.CanTest); Assert.Equal(2, result.Candidates.Count);
    }
    [Fact] public void WindowOverlapDoesNotCreateFalseAmbiguity()
    {
        var bytes = Hex.Bytes("8943604883C408CC"); var cheat = new Cheat("test", "", [new("p", 0, 0x100, bytes[..7], Hex.Bytes("90909090909090"), [])]);
        var result = PortingEngine.AnalyzeSegments(cheat, [(new(0, 0x400000, 0x400008, 0, 5, "a"), bytes), (new(0, 0x400000, 0x400008, 0, 5, "a"), bytes)], true); Assert.True(result.CanTest); Assert.Single(result.Candidates);
    }
    [Fact] public void DifferentSourceSectionsRequireAnExplicitOriginalBaseMapping()
    {
        var bytes = Hex.Bytes("8943604883C408CC9090909090CC");
        var cheat = new Cheat("test", "", [new("a", 14, 0x100, bytes[..7], Hex.Bytes("90909090909090"), []), new("b", 17, 0x200, bytes[8..13], Hex.Bytes("9090909090"), [])]);
        var result = PortingEngine.AnalyzeSegments(cheat, [(new(0, 0x400000, 0x40000E, 0, 5, "code"), bytes)], true);
        Assert.False(result.CanTest); Assert.Contains(result.Warnings, w => w.Contains("original section bases"));
    }
}
