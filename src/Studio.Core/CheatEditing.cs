namespace Studio.Core;

public record PatchDraft(string Id, string Section, string Offset, string Original, string Replacement, string Dependencies = "", bool Absolute = false, string? Ps5Offset = null, bool IsCodeCave = false);
public record JumpInspection(string PatchId, string From, string Target, string Kind, string Destination, string Warning);
public record EditorValidation(List<string> Errors, List<string> Warnings, List<InstructionView> Instructions, List<JumpInspection> Jumps)
{
    public bool Valid => Errors.Count == 0;
    public string Summary => Valid ? "Draft is structurally valid. It is not gameplay verified. " + string.Join(" ", Warnings) : string.Join(Environment.NewLine, Errors);
}

/// <summary>Shared editor operations: Windows text fields are validated by the same core used in automated tests.</summary>
public static class CheatEditing
{
    public static PatchLine Parse(PatchDraft draft)
    {
        if (string.IsNullOrWhiteSpace(draft.Id) || draft.Id.Any(c => !char.IsLetterOrDigit(c) && c is not ('-' or '_'))) throw new InvalidDataException("Use a nonempty line ID with letters, digits, '-' or '_'.");
        return new(draft.Id, int.Parse(draft.Section), Hex.Address(draft.Offset), Hex.Bytes(draft.Original), Hex.Bytes(draft.Replacement),
            draft.Dependencies.Split([',', ' ', ';', '\t', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct().ToArray(), draft.Absolute, draft.Ps5Offset, draft.IsCodeCave);
    }
    public static EditorValidation Validate(string name, string titleId, string version, string process, string cheatName, string description,
        IReadOnlyList<PatchDraft> drafts, bool instructionPatch, IReadOnlyDictionary<int, ulong>? sectionBases = null)
    {
        var errors = new List<string>(); var warnings = new List<string>(); var instructions = new List<InstructionView>(); var jumps = new List<JumpInspection>();
        var lines = new List<PatchLine>();
        for (var n = 0; n < drafts.Count; n++)
        {
            try { lines.Add(Parse(drafts[n])); }
            catch (Exception e) when (e is FormatException or OverflowException or InvalidDataException) { errors.Add($"Line {n + 1} ({drafts[n].Id}): {e.Message}"); }
        }
        if (errors.Count > 0) return new(errors, warnings, instructions, jumps);
        try { TrainerCodec.Validate(new(name, titleId, version, process, [new(cheatName, description, lines)])); }
        catch (InvalidDataException e) { errors.Add(e.Message); }
        foreach (var group in lines.GroupBy(p => (p.Section, p.Absolute)))
        {
            var ordered = group.OrderBy(p => p.Offset).ToArray();
            for (var n = 1; n < ordered.Length; n++) if (ordered[n - 1].Offset + (ulong)ordered[n - 1].Original.Length > ordered[n].Offset)
                errors.Add($"{ordered[n - 1].Id} and {ordered[n].Id} overlap in section {group.Key.Section}.");
        }
        if (instructionPatch)
        {
            foreach (var p in lines)
            {
                var baseAddress = sectionBases?.GetValueOrDefault(p.Section) ?? 0;
                try
                {
                    var address = p.Absolute ? p.Offset : checked(baseAddress + p.Offset);
                    if (!p.IsCodeCave && !PortingEngine.IsCave(p))
                    {
                        CodeAnalysis.Decode(p.Original, address);
                        instructions.AddRange(CodeAnalysis.Disassemble(p.Original, address).Select(i => i with { Assembly = p.Id + " original: " + i.Assembly }));
                    }
                    var replacement = CodeAnalysis.Decode(p.Replacement, address);
                    instructions.AddRange(CodeAnalysis.Disassemble(p.Replacement, address).Select(i => i with { Assembly = p.Id + " replacement: " + i.Assembly }));
                    foreach (var i in replacement.Where(CodeAnalysis.IsBranch))
                    {
                        var destination = lines.FirstOrDefault(other =>
                        {
                            if (other.Section != p.Section && (sectionBases is null || !sectionBases.ContainsKey(other.Section) || !sectionBases.ContainsKey(p.Section))) return false;
                            var start = other.Absolute ? other.Offset : checked((sectionBases?.GetValueOrDefault(other.Section) ?? 0) + other.Offset);
                            return i.NearBranchTarget >= start && i.NearBranchTarget - start <= (ulong)other.Replacement.Length;
                        });
                        var warning = destination is null ? "Destination is outside the edited lines; verify its mapping, lifetime and instruction boundary on the target." : "";
                        if (destination is not null)
                        {
                            var destAddress = destination.Absolute ? destination.Offset : checked((sectionBases?.GetValueOrDefault(destination.Section) ?? 0) + destination.Offset);
                            var boundaries = CodeAnalysis.Decode(destination.Replacement, destAddress).Select(d => d.IP).Append(destAddress + (ulong)destination.Replacement.Length);
                            if (!boundaries.Contains(i.NearBranchTarget)) errors.Add($"{p.Id}: jump to 0x{i.NearBranchTarget:X} lands inside a replacement instruction in {destination.Id}.");
                            if (destination.Id != p.Id && (destination.IsCodeCave || PortingEngine.IsCave(destination)) && !p.Dependencies.Contains(destination.Id))
                                errors.Add($"{p.Id}: add dependency '{destination.Id}' so its cave is written before this hook.");
                        }
                        jumps.Add(new(p.Id, $"0x{i.IP:X}", $"0x{i.NearBranchTarget:X}", i.Mnemonic.ToString(), destination?.Id ?? "External / unresolved", warning));
                    }
                }
                catch (Exception e) when (e is InvalidDataException or OverflowException) { errors.Add($"{p.Id}: {e.Message}"); }
            }
            if (lines.Select(p => p.Section).Distinct().Count() > 1 && (sectionBases is null || lines.Any(p => !sectionBases.ContainsKey(p.Section)))) warnings.Add("Cross-section jump addresses require original section bases; displayed offsets alone do not prove a valid destination.");
        }
        if (lines.Any(p => p.IsCodeCave || PortingEngine.IsCave(p))) warnings.Add("Possible cave lines require separate live-region/reference analysis and explicit cave confirmation before testing.");
        if (lines.Any(p => p.Ps5Offset is not null)) warnings.Add("PS5Offset is preserved as metadata. It never controls a PS4 address; JSON cannot store it.");
        return new(errors.Distinct().ToList(), warnings, instructions, jumps);
    }

    public static Trainer Build(Trainer basis, Cheat? edited, string name, string titleId, string version, string process, string cheatName, string description,
        IReadOnlyList<PatchDraft> drafts, bool instructionPatch, IReadOnlyDictionary<int, ulong>? sectionBases = null)
    {
        var validation = Validate(name, titleId, version, process, cheatName, description, drafts, instructionPatch, sectionBases);
        if (!validation.Valid) throw new InvalidDataException(validation.Summary);
        var replacement = new Cheat(cheatName, description, drafts.Select(Parse).ToList());
        var cheats = basis.Cheats.ToList(); var index = edited is null ? -1 : cheats.IndexOf(edited);
        if (index < 0) cheats.Add(replacement); else cheats[index] = replacement;
        return TrainerCodec.Validate(basis with { Name = name, TitleId = titleId, Version = version, Process = process, Cheats = cheats });
    }
}
