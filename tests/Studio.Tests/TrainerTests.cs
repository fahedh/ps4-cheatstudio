using Studio.Core;
using Xunit;

namespace Studio.Tests;
public class TrainerTests
{
    private static string Examples => Path.Combine(AppContext.BaseDirectory, "examples");
    [Theory] [InlineData("synthetic-demo.json")] [InlineData("synthetic-demo.shn")]
    public void ParseRealGoldHenShapes(string filename)
    {
        var trainer = TrainerCodec.Read(Path.Combine(Examples, filename)); Assert.Equal("CUSA39420", trainer.TitleId); Assert.Equal("01.04", trainer.Version);
        var p = trainer.Cheats.Single().Patches.Single(); Assert.Equal(0x1B6BFEDUL, p.Offset); Assert.Equal(Hex.Bytes("8943604883C408"), p.Original);
    }
    [Fact] public void JsonRoundTripPreservesBytesMetadataAndOffsets()
    {
        var original = TrainerCodec.Read(Path.Combine(Examples, "synthetic-demo.json")); var parsed = TrainerCodec.ReadJson(TrainerCodec.Json(original));
        Assert.Equal(original.TitleId, parsed.TitleId); Assert.Equal(original.Version, parsed.Version); Assert.Equal(original.Cheats[0].Patches[0].Replacement, parsed.Cheats[0].Patches[0].Replacement);
    }
    [Fact] public void ShnRoundTripPreservesSections()
    {
        var original = TrainerCodec.Read(Path.Combine(Examples, "synthetic-demo.shn")); var parsed = TrainerCodec.ReadShn(TrainerCodec.Shn(original)); Assert.Equal(14, parsed.Cheats[0].Patches[0].Section);
        Assert.Throws<InvalidDataException>(() => TrainerCodec.Json(original));
    }
    [Fact] public void AbsoluteAddressingIsPreservedNotSilentlyReinterpreted()
    {
        var t = TrainerCodec.Read(Path.Combine(Examples, "synthetic-demo.shn")); t.Cheats[0].Patches[0] = t.Cheats[0].Patches[0] with { Absolute = true };
        Assert.True(TrainerCodec.ReadShn(TrainerCodec.Shn(t)).Cheats[0].Patches[0].Absolute); Assert.Throws<InvalidDataException>(() => TrainerCodec.Json(t));
    }
    [Fact] public void XmlExternalEntitiesAreRejected() => Assert.Throws<System.Xml.XmlException>(() => TrainerCodec.ReadShn("<!DOCTYPE Trainer [<!ENTITY x SYSTEM 'file:///etc/passwd'>]><Trainer>&x;</Trainer>"));
    [Fact] public void InvalidAbsoluteFlagCannotBecomeRelativeByAccident()
    {
        var text = File.ReadAllText(Path.Combine(Examples, "synthetic-demo.shn")).Replace("<Section>", "<Absolute>maybe</Absolute><Section>"); Assert.Throws<InvalidDataException>(() => TrainerCodec.ReadShn(text));
    }
    [Fact] public void UnknownShnCommandsCannotBeDiscarded()
    {
        var text = File.ReadAllText(Path.Combine(Examples, "synthetic-demo.shn")).Replace("<Cheat Text=", "<Script>unsupported</Script><Cheat Text="); Assert.Throws<NotSupportedException>(() => TrainerCodec.ReadShn(text));
    }
    [Fact] public void AddressPrefixIsAcceptedOnlyAtTheBeginning()
    {
        Assert.Equal(0x1020UL, Hex.Address("0x1020")); Assert.Throws<FormatException>(() => Hex.Address("10x020"));
    }
    [Theory] [InlineData("1")] [InlineData("GG")] [InlineData("AA=")] [InlineData("")] [InlineData("0xAA")]
    public void InvalidHexCannotBeSilentlyFixed(string text) => Assert.Throws<FormatException>(() => Hex.Bytes(text));
    [Fact] public void HexSupportsBothShnAndJsonSeparators() => Assert.Equal(Hex.Bytes("AB 10 CD"), Hex.Bytes("AB-10-CD"));
    [Theory] [InlineData("CUSA3942","01.05")] [InlineData("CUSA39420","1.05")] [InlineData("CUSA39420","01.5")]
    public void InvalidTitleOrVersionRejected(string title, string version)
    {
        var t = TrainerCodec.Read(Path.Combine(Examples, "synthetic-demo.json")); Assert.Throws<InvalidDataException>(() => TrainerCodec.Validate(t with { TitleId = title, Version = version }));
    }
    [Fact] public void LengthMismatchRejected()
    {
        var t = TrainerCodec.Read(Path.Combine(Examples, "synthetic-demo.json")); t.Cheats[0].Patches[0] = t.Cheats[0].Patches[0] with { Replacement = [0x90] }; Assert.Throws<InvalidDataException>(() => TrainerCodec.Validate(t));
    }
    [Fact] public void MissingAndCyclicDependenciesRejected()
    {
        var t = TrainerCodec.Read(Path.Combine(Examples, "synthetic-demo.json")); var p = t.Cheats[0].Patches[0];
        t.Cheats[0].Patches[0] = p with { Dependencies = ["missing"] }; Assert.Throws<InvalidDataException>(() => TrainerCodec.Validate(t));
        t.Cheats[0].Patches[0] = p with { Id = "a", Dependencies = ["b"] }; t.Cheats[0].Patches.Add(p with { Id = "b", Dependencies = ["a"] }); Assert.Throws<InvalidDataException>(() => TrainerCodec.Validate(t));
    }
    [Fact] public void ExactVersionMatchingIncludesProcessAndPid()
    {
        var game = new GameIdentity(100, "Game", "CUSA39420", "01.05", "eboot.bin"); Assert.False(game.SameGame(game with { Version = "01.04" })); Assert.False(game.SameGame(game with { Pid = 101 })); Assert.False(game.SameGame(game with { ProcessName = "SceShellCore" })); Assert.True(game.SameGame(game with { Name = "different display name" }));
    }
    [Fact] public void ExtendedMemoryCommandsAreRejectedRatherThanDiscarded()
    {
        var text = File.ReadAllText(Path.Combine(Examples, "synthetic-demo.json")).Replace("\"offset\":", "\"pointer\":\"1234\",\"offset\":"); Assert.Throws<NotSupportedException>(() => TrainerCodec.ReadJson(text));
    }
    [Fact] public async Task AtomicLocalProfilesPersistWithoutPromotingVerification()
    {
        var directory = Path.Combine(Path.GetTempPath(), "profile-" + Guid.NewGuid()); var path = Path.Combine(directory, "profile.json");
        try { await LocalStore.SaveAsync(path, new GameProfile { TitleId = "CUSA39420", Version = "01.05" }); var p = await LocalStore.LoadAsync<GameProfile>(path); Assert.Empty(p.Verifications); Assert.Equal("01.05", p.Version); Assert.Single(Directory.GetFiles(directory)); }
        finally { Directory.Delete(directory, true); }
    }
}
