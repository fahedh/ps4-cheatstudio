using Studio.Core;

namespace Studio.Desktop;

public sealed class PatchRowViewModel : Observable
{
    private string id = "", section = "0", offset = "0", original = "", replacement = "", dependencies = "", ps5Offset = "";
    private bool absolute, isCave;
    public string Id { get => id; set => Set(ref id, value); }
    public string Section { get => section; set => Set(ref section, value); }
    public string Offset { get => offset; set => Set(ref offset, value); }
    public string Original { get => original; set => Set(ref original, value); }
    public string Replacement { get => replacement; set => Set(ref replacement, value); }
    public string Dependencies { get => dependencies; set => Set(ref dependencies, value); }
    public string Ps5Offset { get => ps5Offset; set => Set(ref ps5Offset, value); }
    public bool Absolute { get => absolute; set => Set(ref absolute, value); }
    public bool IsCave { get => isCave; set => Set(ref isCave, value); }
    public PatchDraft Draft => new(Id, Section, Offset, Original, Replacement, Dependencies, Absolute, Ps5Offset.Length == 0 ? null : Ps5Offset, IsCave);
    public static PatchRowViewModel From(PatchLine p) => new()
    {
        Id = p.Id, Section = p.Section.ToString(), Offset = p.Offset.ToString("X"), Original = p.OriginalHex, Replacement = p.ReplacementHex,
        Dependencies = string.Join(", ", p.Dependencies), Ps5Offset = p.Ps5Offset ?? "", Absolute = p.Absolute, IsCave = PortingEngine.IsCave(p)
    };
}
