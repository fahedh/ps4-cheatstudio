using Studio.Core;
using Xunit;

namespace Studio.Tests;

public class CheatEditorTests
{
    private static Trainer Trainer => TrainerCodec.Read(Path.Combine(AppContext.BaseDirectory, "examples", "silksong-metadata-structure.shn"));
    private static List<PatchDraft> Drafts(Trainer t) => t.Cheats[0].Patches.Select(p => new PatchDraft(p.Id, p.Section.ToString(), p.Offset.ToString("X"), p.OriginalHex, p.ReplacementHex,
        p.Id == "line-1" ? "line-2" : "", Ps5Offset: p.Ps5Offset, IsCodeCave: PortingEngine.IsCave(p))).ToList();
    private static EditorValidation Validate(Trainer t, List<PatchDraft> drafts) => CheatEditing.Validate(t.Name, t.TitleId, t.Version, t.Process, t.Cheats[0].Name, "edited", drafts, true);

    [Fact] public void MultiLineEditorValidatesDisassemblesAndInspectsHookAndReturn()
    {
        var t = Trainer; var validation = Validate(t, Drafts(t)); Assert.True(validation.Valid, validation.Summary);
        Assert.Equal(2, validation.Jumps.Count); Assert.Contains(validation.Jumps, j => j.Destination == "line-2"); Assert.Contains(validation.Instructions, i => i.Assembly.Contains("original:")); Assert.Contains(validation.Instructions, i => i.Assembly.Contains("replacement:"));
    }
    [Fact] public void EditedTrainerRoundTripKeepsOtherCheatsAndMetadata()
    {
        var t = Trainer; var drafts = Drafts(t); drafts[0] = drafts[0] with { Ps5Offset = "alternate-platform-description" };
        var built = CheatEditing.Build(t, t.Cheats[0], t.Name, t.TitleId, t.Version, t.Process, "Edited synthetic cheat", "edited", drafts, true);
        Assert.Equal(2, built.Cheats.Count); Assert.Equal(t.Cheats[1], built.Cheats[1]);
        var round = TrainerCodec.ReadShn(TrainerCodec.Shn(built)); Assert.Equal("Alternate Trainer Author", round.Metadata!.Authors["Moder"]);
        Assert.Contains(round.Cheats[0].Patches, p => p.Ps5Offset == "alternate-platform-description"); Assert.Equal(t.Cheats[0].Patches[0].Original, built.Cheats[0].Patches[0].Original);
    }
    [Fact] public void DependenciesOrderCaveBeforeHookInGoldHenExport()
    {
        var t = Trainer; var built = CheatEditing.Build(t, t.Cheats[0], t.Name, t.TitleId, t.Version, t.Process, t.Cheats[0].Name, "edited", Drafts(t), true);
        var round = TrainerCodec.ReadShn(TrainerCodec.Shn(built)); Assert.Equal(0x4783800UL, round.Cheats[0].Patches[0].Offset); Assert.Equal(0x1B6BFEDUL, round.Cheats[0].Patches[1].Offset);
    }
    [Fact] public void MissingCaveDependencyIsAnActionableValidationError()
    {
        var t = Trainer; var drafts = Drafts(t); drafts[0] = drafts[0] with { Dependencies = "" }; var validation = Validate(t, drafts);
        Assert.False(validation.Valid); Assert.Contains(validation.Errors, e => e.Contains("add dependency 'line-2'"));
    }
    [Fact] public void RemovingAReferencedLineDoesNotSilentlyDropTheDependency()
    {
        var t = Trainer; var drafts = Drafts(t); drafts.RemoveAt(1); Assert.False(Validate(t, drafts).Valid);
    }
    [Theory] [InlineData("-1","0","90","90")] [InlineData("14","ZZ","90","90")] [InlineData("14","123","GG","90")] [InlineData("14","123","894360","90")]
    public void InvalidTextFieldsOrByteLengthsBlockSaving(string section, string offset, string original, string replacement)
    {
        var t = Trainer; var validation = Validate(t, [new("a", section, offset, original, replacement)]); Assert.False(validation.Valid); Assert.NotEmpty(validation.Errors);
    }
    [Fact] public void OverlappingLinesAreRejected()
    {
        var t = Trainer; var drafts = new List<PatchDraft> { new("a", "14", "100", "909090", "909090"), new("b", "14", "101", "909090", "909090") };
        Assert.Contains(Validate(t, drafts).Errors, e => e.Contains("overlap"));
    }
    [Fact] public void RelativeJumpIntoReplacementInstructionIsRejected()
    {
        var t = Trainer; var drafts = new List<PatchDraft> { new("a", "14", "100", "9090909090", "E9FCFFFFFF") };
        Assert.Contains(Validate(t, drafts).Errors, e => e.Contains("inside a replacement instruction"));
    }
    [Fact] public async Task StudioProjectRetainsDependenciesWithoutAddingUnsupportedGoldHenCommands()
    {
        var t = Trainer; var built = CheatEditing.Build(t, t.Cheats[0], t.Name, t.TitleId, t.Version, t.Process, t.Cheats[0].Name, "edited", Drafts(t), true);
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        try { await LocalStore.SaveAsync(path, built); var round = await LocalStore.LoadAsync<Trainer>(path); Assert.Contains("line-2", round.Cheats[0].Patches[0].Dependencies); Assert.DoesNotContain("Dependencies", TrainerCodec.Shn(round)); }
        finally { File.Delete(path); }
    }
}
