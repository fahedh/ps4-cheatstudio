using System.Xml.Linq;
using Studio.Core;
using Xunit;

namespace Studio.Tests;

public class ShnCompatibilityTests
{
    private static string Fixture => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "examples", "silksong-metadata-structure.shn"));

    [Fact] public void SilksongSection14AndPs5MetadataAreIndependent()
    {
        var trainer = TrainerCodec.ReadShn(Fixture); var line = trainer.Cheats[0].Patches[0];
        Assert.Equal("CUSA39420", trainer.TitleId); Assert.Equal("01.04", trainer.Version); Assert.Equal(14, line.Section); Assert.Equal(0x1B6BFEDUL, line.Offset);
        Assert.Equal(Hex.Bytes("8943604883C408"), line.Original); Assert.Equal("104FDB2-Il2CppUserAssemblies.prx[0]-5", line.Ps5Offset);
        Assert.Equal(0x4783800UL, trainer.Cheats[0].Patches[1].Offset); Assert.Equal(2, trainer.Cheats.Count);
    }
    [Fact] public void RoundTripPreservesAuthorsGenresPs5MetadataAndEveryMemoryLine()
    {
        var trainer = TrainerCodec.ReadShn(Fixture); var round = TrainerCodec.ReadShn(TrainerCodec.Shn(trainer));
        Assert.Equal("Alternate Trainer Author", round.Metadata!.Authors["Moder"]); Assert.Equal("Metadata Fixture Contributor", round.Metadata.Authors["Author"]); Assert.Equal(["Action"], round.Metadata.Genres);
        foreach (var (old, now) in trainer.Cheats.SelectMany(c => c.Patches).Zip(round.Cheats.SelectMany(c => c.Patches)))
        {
            Assert.Equal(old.Section, now.Section); Assert.Equal(old.Offset, now.Offset); Assert.Equal(old.Ps5Offset, now.Ps5Offset); Assert.Equal(old.Original, now.Original); Assert.Equal(old.Replacement, now.Replacement);
        }
    }
    [Theory] [InlineData("Moder")] [InlineData("Author")] [InlineData("Creator")]
    public void AlternateAuthorAttributesAreNotReplacedWithStudioAttribution(string attribute)
    {
        var xml = XDocument.Parse(Fixture); xml.Root!.Attribute("Moder")!.Remove(); xml.Root.Attribute("Author")!.Remove(); xml.Root.SetAttributeValue(attribute, "Original & Independent Author");
        var round = XDocument.Parse(TrainerCodec.Shn(TrainerCodec.ReadShn(xml.ToString())));
        Assert.Equal("Original & Independent Author", round.Root!.Attribute(attribute)!.Value);
    }
    [Theory] [InlineData("Pointer")] [InlineData("Script")] [InlineData("Write64")] [InlineData("PS5Execute")]
    public void UnsupportedOperationalElementsAreStillRejectedWithTheirName(string command)
    {
        var xml = Fixture.Replace("<PS5Offset>", $"<{command}>execute</{command}><PS5Offset>");
        var e = Assert.Throws<NotSupportedException>(() => TrainerCodec.ReadShn(xml)); Assert.Contains(command, e.Message);
    }
    [Fact] public void NestedCommandsCannotBeHiddenInAllowedPs5Metadata()
    {
        var xml = Fixture.Replace("800-executable[0]-5", "<Script>run</Script>"); Assert.Throws<InvalidDataException>(() => TrainerCodec.ReadShn(xml));
    }
    [Fact] public void DuplicateOffsetFieldsAreRejectedRatherThanChoosingOne()
    {
        var xml = Fixture.Replace("<Offset>1B6BFED</Offset>", "<Offset>1B6BFED</Offset><Offset>1234</Offset>"); Assert.Throws<InvalidDataException>(() => TrainerCodec.ReadShn(xml));
    }
    [Fact] public void OperationalAttributesCannotBeIgnored()
    {
        var xml = Fixture.Replace("<Cheat Text=", "<Cheat Script=\"unsafe\" Text="); Assert.Throws<NotSupportedException>(() => TrainerCodec.ReadShn(xml));
    }
    [Fact] public void JsonCreditsSurviveImportExport()
    {
        var json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "examples", "synthetic-demo.json"));
        var t = TrainerCodec.ReadJson(json) with { Metadata = new([], [], ["Original author", "Contributor"]) };
        Assert.Equal(t.Metadata.Credits, TrainerCodec.ReadJson(TrainerCodec.Json(t)).Metadata!.Credits);
    }
    [Fact] public void Ps5MetadataDoesNotChangeThePs4SearchAddress()
    {
        var t = TrainerCodec.ReadShn(Fixture); var line = t.Cheats[0].Patches[0] with { Replacement = Hex.Bytes("90909090909090") };
        var memory = Enumerable.Repeat((byte)0xCC, 128).ToArray(); line.Original.CopyTo(memory, 32);
        var result = PortingEngine.AnalyzeSegments(new("synthetic", "", [line]), [(new(0, 0x400000, 0x400080, 0, 5, "code"), memory)], true);
        Assert.True(result.CanTest, result.Summary); Assert.Equal(0x400020UL, result.Candidates.Single().Address); Assert.Null(result.ProposedPatches.Single().Ps5Offset);
    }
}
