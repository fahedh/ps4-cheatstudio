using Studio.Core;
using Xunit;

namespace Studio.Tests;

public class InstructionEvidenceTests
{
    private static byte[] OldHook => Hex.Bytes("8943604883C408");
    private static byte[] Prefix => Hex.Bytes("554889E54883EC20488B5D08488B4D10488B5518");
    private static byte[] Suffix => Hex.Bytes("488B45104883C4205DC3");
    private static PatchLine Patch => new("health", 14, 0x100, OldHook, Hex.Bytes("90909090909090"), []);
    private static SourceEvidence Source => new(14, 0, 0x100UL - (ulong)Prefix.Length, [.. Prefix, .. OldHook, .. Suffix], 0x100UL - (ulong)Prefix.Length, Prefix.Length + OldHook.Length + Suffix.Length);
    private static (MemoryRegion Region, byte[] Bytes) Target(byte[] modified)
    {
        var bytes = Enumerable.Repeat((byte)0xCC, 256).ToArray(); new byte[] { }.CopyTo(bytes, 0); Prefix.CopyTo(bytes, 32); modified.CopyTo(bytes, 32 + Prefix.Length); Suffix.CopyTo(bytes, 32 + Prefix.Length + modified.Length);
        return (new(0, 0x400000, 0x400100, 0, 5, "code"), bytes);
    }
    [Fact] public void ChangedFieldOperandIsFoundWithoutInventingAReadyPatch()
    {
        var result = PortingEngine.AnalyzeSegments(new("health", "", [Patch]), [Target(Hex.Bytes("8943684883C408"))], true, options: new() { Sources = [Source] });
        Assert.False(result.CanTest); var candidate = Assert.Single(result.Candidates); Assert.False(candidate.ExactBytes); Assert.Equal(0x400000UL + 32 + (ulong)Prefix.Length, candidate.Address);
        Assert.Contains(candidate.Evidence!.Changes, c => c.Contains("operand")); Assert.Contains("Bounded function evidence", candidate.Evidence.FunctionComparison); Assert.NotEmpty(result.NextSteps); Assert.Empty(result.ProposedPatches);
    }
    [Fact] public void SurroundingAnchorsFindChangedInstructionDirection()
    {
        var result = PortingEngine.AnalyzeSegments(new("health", "", [Patch]), [Target(Hex.Bytes("8B43604883C408"))], true, options: new() { Sources = [Source] });
        var candidate = Assert.Single(result.Candidates); Assert.True(candidate.Evidence!.LayoutChanged); Assert.Contains(candidate.Evidence.Changes, c => c.Contains("Changed old instructions")); Assert.False(result.CanTest);
    }
    [Fact] public void InsertedInstructionChangesTheCandidateSpanAndRequiresManualRedesign()
    {
        var result = PortingEngine.AnalyzeSegments(new("health", "", [Patch]), [Target(Hex.Bytes("894360904883C408"))], true, options: new() { Sources = [Source] });
        var candidate = Assert.Single(result.Candidates); Assert.Equal(8, candidate.Expected.Length); Assert.Equal(3, candidate.Evidence!.TargetInstructions); Assert.True(candidate.Evidence.LayoutChanged); Assert.False(result.CanTest);
    }
    [Fact] public void ContextRanksMultipleCandidatesWithoutClaimingFunctionEquivalence()
    {
        var target = Target(Hex.Bytes("8943684883C408")); Hex.Bytes("8943704883C408").CopyTo(target.Bytes, 180);
        var result = PortingEngine.AnalyzeSegments(new("health", "", [Patch]), [target], true, options: new() { Sources = [Source] });
        Assert.Equal(2, result.Candidates.Count); Assert.True(result.Candidates[0].Score > result.Candidates[1].Score); Assert.False(result.CanTest);
        Assert.Contains("does not prove", result.Candidates[0].Evidence!.FunctionComparison);
    }
    [Fact] public void AChangedBranchConditionLowersControlFlowEvidence()
    {
        var compared = InstructionMatching.Compare(Hex.Bytes("75029090C3"), 0x1000, Hex.Bytes("74029090C3"), 0x2000);
        Assert.True(compared.ControlFlowAgreement < 100); Assert.Contains(compared.Changes, c => c.Contains("Branch condition"));
    }
    [Fact] public void InternalBranchDestinationsCompareThroughInstructionAlignment()
    {
        var compared = InstructionMatching.Compare(Hex.Bytes("75029090C3"), 0x1000, Hex.Bytes("7503909090C3"), 0x2000);
        Assert.Equal(100, compared.ControlFlowAgreement); Assert.True(compared.LayoutChanged);
    }
    [Fact] public void ExternalCallAddressArithmeticDoesNotProveCalleeIdentity()
    {
        var compared = InstructionMatching.Compare(Hex.Bytes("E800010000C3"), 0x1000, Hex.Bytes("E800010000C3"), 0x2000);
        Assert.True(compared.ControlFlowAgreement < 100); Assert.Contains(compared.Changes, c => c.Contains("destination function is not verified"));
    }
    [Fact] public void WrongOriginalDumpCannotAuthorizeAChangedPatch()
    {
        var source = Source; source.Bytes[Prefix.Length] = 0xCC;
        var result = PortingEngine.AnalyzeSegments(new("health", "", [Patch]), [Target(OldHook)], true, options: new() { Sources = [source] });
        Assert.False(result.CanTest); Assert.Contains(result.Warnings, w => w.Contains("does not contain"));
    }
    [Fact] public void MissingContextDoesNotGenerateAnOffsetForUnrelatedCode()
    {
        var result = PortingEngine.AnalyzeSegments(new("health", "", [Patch]), [(new(0, 0x400000, 0x400040, 0, 5, "code"), Enumerable.Repeat((byte)0xCC, 64).ToArray())], true);
        Assert.Empty(result.Candidates); Assert.Empty(result.ProposedPatches); Assert.False(result.CanTest); Assert.Contains(result.NextSteps, s => s.Contains("Capture old-version context", StringComparison.OrdinalIgnoreCase) || s.Contains("Capture old context"));
    }
    [Fact] public void MatchingBytesAloneNeverProduceAHardwareVerifiedStatus()
    {
        var result = PortingEngine.AnalyzeSegments(new("health", "", [Patch]), [Target(OldHook)], true);
        Assert.True(result.CanTest); Assert.All(result.Candidates, c => Assert.Equal("Generated candidate — not verified", c.Status));
    }
    [Fact] public async Task ShippedModifiedSilksongFixtureSupportsTheGuidedEvidencePath()
    {
        var examples = Path.Combine(AppContext.BaseDirectory, "examples"); var trainer = TrainerCodec.Read(Path.Combine(examples, "silksong-context-demo.shn"));
        var source = await LocalStore.LoadAsync<EvidencePackage>(Path.Combine(examples, "silksong-synthetic-old.studio-evidence.json"));
        var memory = File.ReadAllBytes(Path.Combine(examples, "silksong-synthetic-modified.bin")); var segments = new List<(MemoryRegion Region, byte[] Bytes)> { (new(0, 0x400000, 0x400100, 0, 5, "offline synthetic"), memory) };
        var without = PortingEngine.AnalyzeSegments(trainer.Cheats[0], segments, true);
        var withContext = PortingEngine.AnalyzeSegments(trainer.Cheats[0], segments, true, options: new() { Sources = source.Sources });
        Assert.Equal("01.04", source.Version); Assert.False(withContext.CanTest); Assert.Empty(withContext.ProposedPatches);
        var candidate = Assert.Single(withContext.Candidates); Assert.Contains("Bounded function evidence", candidate.Evidence!.FunctionComparison);
        Assert.True(candidate.Score > without.Candidates.Single().Score); Assert.False(candidate.ExactBytes); Assert.Equal("illustrative-metadata-only", trainer.Cheats[0].Patches[0].Ps5Offset);
    }
    [Fact] public void AmbiguousContextEndsNeverChooseAnArbitraryOverwriteLength()
    {
        var target = Target(Hex.Bytes("8943684883C408")); Suffix.CopyTo(target.Bytes, 32 + Prefix.Length + 20);
        var result = PortingEngine.AnalyzeSegments(new("health", "", [Patch]), [target], true, options: new() { Sources = [Source] });
        Assert.False(result.CanTest); Assert.Contains(result.Warnings, w => w.Contains("multiple possible instruction-span boundaries"));
    }
}
