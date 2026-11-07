using Iced.Intel;

namespace Studio.Core;

public record InstructionView(ulong Address, string Bytes, string Assembly, int Length, string Flow, ulong? BranchTarget)
{
    public string AddressHex => $"0x{Address:X}";
}
public record CodeCave(ulong Address, int Length, byte Fill)
{
    public string Explanation => $"0x{Address:X}: {Length} bytes of {Fill:X2}. Padding only; this does not prove the memory is unused.";
}

public static class CodeAnalysis
{
    public static List<Instruction> Decode(byte[] bytes, ulong address)
    {
        var decoder = Decoder.Create(64, new ByteArrayCodeReader(bytes)); decoder.IP = address;
        var output = new List<Instruction>();
        while (decoder.IP - address < (ulong)bytes.Length)
        {
            var instruction = decoder.Decode();
            if (instruction.IsInvalid) throw new InvalidDataException($"Cannot decode complete x86-64 instructions at 0x{instruction.IP:X}.");
            output.Add(instruction);
        }
        return output;
    }
    public static List<InstructionView> Disassemble(byte[] bytes, ulong address)
    {
        var decoder = Decoder.Create(64, new ByteArrayCodeReader(bytes)); decoder.IP = address;
        var formatter = new IntelFormatter(); var result = new List<InstructionView>();
        while (decoder.IP - address < (ulong)bytes.Length)
        {
            var i = decoder.Decode(); var output = new AssemblyOutput(); formatter.Format(i, output);
            var offset = checked((int)(i.IP - address));
            result.Add(new(i.IP, Hex.Format(bytes.AsSpan(offset, i.Length).ToArray()), i.IsInvalid ? "<invalid or truncated instruction>" : output.ToString(), i.Length, i.FlowControl.ToString(), IsBranch(i) ? i.NearBranchTarget : null));
        }
        return result;
    }
    public static bool IsBranch(Instruction i) => i.Op0Kind is OpKind.NearBranch16 or OpKind.NearBranch32 or OpKind.NearBranch64;
    public static BytePattern Signature(byte[] original)
    {
        var mask = original.Select(b => (byte?)b).ToArray();
        var decoder = Decoder.Create(64, new ByteArrayCodeReader(original));
        while (decoder.IP < (ulong)original.Length)
        {
            var i = decoder.Decode(); if (i.IsInvalid) return new(original.Select(b => (byte?)b));
            var c = decoder.GetConstantOffsets(i); var start = (int)i.IP;
            if (i.IsIPRelativeMemoryOperand) for (var n = 0; n < c.DisplacementSize; n++) mask[start + c.DisplacementOffset + n] = null;
            if (IsBranch(i)) for (var n = 0; n < c.ImmediateSize; n++) mask[start + c.ImmediateOffset + n] = null;
        }
        return new(mask);
    }
    public static IReadOnlyList<CodeCave> FindCaves(byte[] bytes, ulong address, int minimumLength)
    {
        if (minimumLength < 1) throw new ArgumentOutOfRangeException(nameof(minimumLength));
        var caves = new List<CodeCave>();
        for (var i = 0; i < bytes.Length;)
        {
            var start = i; var fill = bytes[i++];
            if (fill is not (0 or 0x90 or 0xCC)) continue;
            while (i < bytes.Length && bytes[i] == fill) i++;
            if (i - start >= minimumLength) caves.Add(new(address + (ulong)start, i - start, fill));
        }
        return caves;
    }
    public static byte[] RelativeJump(ulong from, ulong to)
    {
        var delta = (Int128)to - ((Int128)from + 5);
        if (delta < int.MinValue || delta > int.MaxValue) throw new InvalidDataException("The target is outside the ±2 GiB rel32 jump range.");
        return [0xE9, .. BitConverter.GetBytes((int)delta)];
    }
    public static byte[] Nops(byte[] original, ulong address)
    {
        Decode(original, address); return Enumerable.Repeat((byte)0x90, original.Length).ToArray();
    }
    public static byte[] Relocate(byte[] patch, ulong source, ulong destination, IReadOnlyDictionary<ulong, ulong> mapping, bool code)
    {
        if (!code) return patch.ToArray();
        var instructions = Decode(patch, source);
        for (var n = 0; n < instructions.Count; n++)
        {
            var i = instructions[n];
            if (IsBranch(i))
            {
                var target = i.NearBranchTarget;
                if (target >= source && target < source + (ulong)patch.Length) { /* BlockEncoder handles intra-block targets. */ }
                else if (mapping.TryGetValue(target, out var relocated)) i.NearBranch64 = relocated;
                else throw new InvalidDataException($"Branch target 0x{target:X} has no verified relocation. Manual verification required.");
            }
            if (i.IsIPRelativeMemoryOperand)
            {
                if (mapping.TryGetValue(i.IPRelativeMemoryAddress, out var relocated)) i.MemoryDisplacement64 = relocated;
                else throw new InvalidDataException($"RIP-relative data target 0x{i.IPRelativeMemoryAddress:X} has no relocation. Manual verification required.");
            }
            instructions[n] = i;
        }
        var writer = new ByteWriter();
        if (!BlockEncoder.TryEncode(64, new InstructionBlock(writer, instructions, destination), out var error, out _, BlockEncoderOptions.DontFixBranches)) throw new InvalidDataException(error);
        if (writer.Bytes.Count != patch.Length) throw new InvalidDataException("Relocation changes the patch size; an expanded patch needs manual design.");
        return writer.Bytes.ToArray();
    }
    private sealed class AssemblyOutput : FormatterOutput
    {
        private readonly System.Text.StringBuilder builder = new();
        public override void Write(string text, FormatterTextKind kind) => builder.Append(text);
        public override string ToString() => builder.ToString();
    }
    private sealed class ByteWriter : CodeWriter
    {
        public List<byte> Bytes { get; } = [];
        public override void WriteByte(byte value) => Bytes.Add(value);
    }
}

public record PortCandidate(string PatchId, ulong Address, int Score, bool ExactBytes, bool CodeCave, string Explanation, byte[] Expected)
{
    public string AddressHex => $"0x{Address:X}";
    public string Status => "Generated candidate — not verified";
}
public record PortResult(List<PortCandidate> Candidates, List<PatchLine> ProposedPatches, List<string> Warnings)
{
    public bool CanTest => ProposedPatches.Count > 0 && Warnings.Count == 0;
    public string Summary => CanTest ? "One candidate per line was found. Review and test it; matching bytes do not prove the cheat works." : "Manual verification required. " + string.Join(" ", Warnings);
}
public record PortSourceContext(ulong Start, byte[] Bytes);

public static class PortingEngine
{
    public static PortResult AnalyzeSegments(Cheat cheat, IReadOnlyList<(MemoryRegion Region, byte[] Bytes)> segments, bool instructionPatch, CancellationToken ct = default)
    {
        var candidates = new List<PortCandidate>(); var warnings = new List<string>();
        var chosen = new Dictionary<string, PortCandidate>();
        if (instructionPatch && cheat.Patches.Select(p => p.Section).Distinct().Count() > 1)
            warnings.Add("Source patches use different SHN sections. Resolve their original section bases before recalculating cross-section branches; section-relative offsets alone are insufficient.");
        foreach (var patch in cheat.Patches)
        {
            ct.ThrowIfCancellationRequested();
            // Collect matches independently in each bounded window, then resolve ambiguity globally.
            foreach (var (region, bytes) in segments)
            {
                var probe = new Cheat(cheat.Name, cheat.Description, [patch with { Replacement = patch.Original, Dependencies = [] }]);
                candidates.AddRange(Analyze(probe, bytes, region, instructionPatch, ct: ct).Candidates);
            }
            var matches = candidates.Where(c => c.PatchId == patch.Id).DistinctBy(c => c.Address).ToList();
            if (matches.Count == 1 && matches[0].ExactBytes) chosen[patch.Id] = matches[0];
            else warnings.Add($"{patch.Id}: {(matches.Count == 0 ? "signature missing, unsupported addressing, or no valid code cave" : matches.Count > 1 ? "several possible matches; no address was selected" : "instruction bytes changed; manual comparison is required")}.");
        }
        candidates = candidates.DistinctBy(c => (c.PatchId, c.Address)).ToList();
        var proposed = new List<PatchLine>();
        if (warnings.Count == 0 && chosen.Count == cheat.Patches.Count)
        {
            var mapping = new Dictionary<ulong, ulong>();
            foreach (var p in cheat.Patches)
            {
                if (!mapping.TryAdd(p.Offset, chosen[p.Id].Address)) { warnings.Add("Old section-relative offsets overlap; source mapping must be resolved manually."); break; }
            }
            foreach (var p in cheat.Patches)
            {
                var end = checked(p.Offset + (ulong)p.Original.Length); var targetEnd = checked(chosen[p.Id].Address + (ulong)p.Original.Length);
                if (mapping.TryGetValue(end, out var mapped) && mapped != targetEnd) { warnings.Add("Adjacent source patches relocated non-contiguously; return targets need manual resolution."); break; }
                mapping.TryAdd(end, targetEnd);
            }
            if (warnings.Count == 0)
            {
                foreach (var p in cheat.Patches)
                {
                    try
                    {
                        var candidate = chosen[p.Id];
                        var region = segments.Where(s => s.Region.Contains(candidate.Address, p.Original.Length)).Select(s => s.Region).OrderBy(r => r.Start).First();
                        var originalBase = segments.Where(s => s.Region.Index == region.Index).Min(s => s.Region.Start);
                        var dependencies = p.Dependencies.ToHashSet();
                        if (instructionPatch) foreach (var instruction in CodeAnalysis.Decode(p.Replacement, p.Offset).Where(CodeAnalysis.IsBranch))
                        {
                            var referenced = cheat.Patches.FirstOrDefault(other => other.Id != p.Id && other.Offset == instruction.NearBranchTarget);
                            if (referenced is not null && chosen[referenced.Id].CodeCave) dependencies.Add(referenced.Id);
                        }
                        proposed.Add(p with { Section = region.Index, Offset = candidate.Address - originalBase, Original = candidate.Expected, Replacement = CodeAnalysis.Relocate(p.Replacement, p.Offset, candidate.Address, mapping, instructionPatch), Dependencies = dependencies.ToArray(), Absolute = false });
                    }
                    catch (InvalidDataException e) { warnings.Add($"{p.Id}: {e.Message}"); }
                }
            }
        }
        if (warnings.Count == 0)
        {
            var ordered = proposed.Select(p => (Patch: p, Address: chosen[p.Id].Address)).OrderBy(p => p.Address).ToArray();
            for (var i = 1; i < ordered.Length; i++) if (ordered[i - 1].Address + (ulong)ordered[i - 1].Patch.Original.Length > ordered[i].Address) { warnings.Add("Candidate patch lines overlap in target memory. Resolve their dependencies/locations manually."); break; }
        }
        if (warnings.Count > 0) proposed.Clear();
        return new(candidates, proposed, warnings);
    }

    public static PortResult Analyze(Cheat cheat, byte[] target, MemoryRegion region, bool instructionPatch, PortSourceContext? sourceContext = null, CancellationToken ct = default)
    {
        var candidates = new List<PortCandidate>(); var warnings = new List<string>(); var selected = new Dictionary<string, PortCandidate>();
        if (target.Length == 0 || (ulong)target.Length > region.Size) throw new ArgumentException("Target bytes must fit the selected region.");
        foreach (var p in cheat.Patches)
        {
            ct.ThrowIfCancellationRequested();
            if (p.Absolute) { warnings.Add($"{p.Id}: absolute SHN addressing needs manual section mapping."); continue; }
            var cave = instructionPatch && p.Original.All(b => b == p.Original[0]) && p.Original[0] is 0 or 0x90 or 0xCC;
            List<int> matches;
            if (cave)
            {
                if (!region.Executable) { warnings.Add($"{p.Id}: a code cave must be in an executable region."); continue; }
                matches = CodeAnalysis.FindCaves(target, region.Start, p.Original.Length).Where(c => c.Fill == p.Original[0]).Select(c => checked((int)(c.Address - region.Start))).ToList();
            }
            else
            {
                var signature = instructionPatch ? CodeAnalysis.Signature(p.Original) : new BytePattern(p.Original.Select(b => (byte?)b));
                try { matches = signature.Find(target, ct: ct).ToList(); }
                catch (InvalidDataException e) { warnings.Add($"{p.Id}: {e.Message}"); continue; }
            }
            var lineCandidates = new List<PortCandidate>();
            foreach (var at in matches.Take(1000))
            {
                var original = target.AsSpan(at, p.Original.Length).ToArray(); var exact = original.SequenceEqual(p.Original);
                var reasons = new List<string> { cave ? "Possible padding; no proof that it is unused." : exact ? "Original bytes match exactly." : "Instruction signature matches; relative operands changed." };
                var score = cave ? 20 : exact ? 65 : 40;
                if (instructionPatch && !cave)
                {
                    try
                    {
                        var old = CodeAnalysis.Decode(p.Original, p.Offset); var current = CodeAnalysis.Decode(original, region.Start + (ulong)at);
                        if (!old.Select(i => (i.Mnemonic, i.FlowControl, i.Length)).SequenceEqual(current.Select(i => (i.Mnemonic, i.FlowControl, i.Length)))) { reasons.Add("Instruction layout changed."); score -= 25; }
                        else { reasons.Add("Instruction lengths, mnemonics and branch kinds agree."); score += 10; }
                    }
                    catch (InvalidDataException e) { reasons.Add(e.Message); score -= 30; }
                }
                if (sourceContext is not null && p.Offset >= sourceContext.Start + 16 && p.Offset + (ulong)p.Original.Length + 16 <= sourceContext.Start + (ulong)sourceContext.Bytes.Length && at >= 16 && at + p.Original.Length + 16 <= target.Length)
                {
                    var oldAt = checked((int)(p.Offset - sourceContext.Start));
                    var before = sourceContext.Bytes.AsSpan(oldAt - 16, 16).SequenceEqual(target.AsSpan(at - 16, 16));
                    var after = sourceContext.Bytes.AsSpan(oldAt + p.Original.Length, 16).SequenceEqual(target.AsSpan(at + p.Original.Length, 16));
                    score += (before ? 8 : 0) + (after ? 8 : 0); reasons.Add($"Surrounding bytes: before {(before ? "matches" : "differs")}, after {(after ? "matches" : "differs")}.");
                }
                else reasons.Add("No original context dump; equivalent function and control flow are unproven.");
                if (matches.Count == 1) { score += 5; reasons.Add("Unique in the selected range only."); }
                else reasons.Add($"{matches.Count} matches in the selected range; ambiguous.");
                lineCandidates.Add(new(p.Id, region.Start + (ulong)at, Math.Clamp(score, 0, 95), exact, cave, string.Join(" ", reasons), original));
            }
            candidates.AddRange(lineCandidates.OrderByDescending(c => c.Score));
            if (lineCandidates.Count == 1 && (lineCandidates[0].ExactBytes || cave)) selected[p.Id] = lineCandidates[0];
            else warnings.Add($"{p.Id}: {(lineCandidates.Count == 0 ? "original signature not found" : lineCandidates.Count > 1 ? "multiple possible locations" : "original bytes changed")}. No address was chosen.");
        }
        var proposed = new List<PatchLine>();
        if (selected.Count == cheat.Patches.Count)
        {
            var mapping = new Dictionary<ulong, ulong>();
            foreach (var p in cheat.Patches)
            {
                if (!mapping.TryAdd(p.Offset, selected[p.Id].Address)) { warnings.Add("Source sections contain duplicate relative offsets; map them manually."); break; }
                mapping.TryAdd(checked(p.Offset + (ulong)p.Original.Length), checked(selected[p.Id].Address + (ulong)p.Original.Length));
            }
            if (warnings.Count == 0)
            {
                foreach (var p in cheat.Patches)
                {
                    try
                    {
                        var candidate = selected[p.Id];
                        var dependencies = p.Dependencies.ToHashSet();
                        if (instructionPatch)
                        {
                            foreach (var i in CodeAnalysis.Decode(p.Replacement, p.Offset).Where(CodeAnalysis.IsBranch))
                            {
                                var d = cheat.Patches.FirstOrDefault(other => other.Id != p.Id && i.NearBranchTarget == other.Offset);
                                // A cave's return to the hook does not make it depend on activating the hook first.
                                if (d is not null && selected[d.Id].CodeCave) dependencies.Add(d.Id);
                            }
                        }
                        proposed.Add(p with { Section = region.Index, Offset = candidate.Address - region.Start, Original = candidate.Expected, Replacement = CodeAnalysis.Relocate(p.Replacement, p.Offset, candidate.Address, mapping, instructionPatch), Dependencies = dependencies.ToArray(), Absolute = false });
                    }
                    catch (InvalidDataException e) { warnings.Add($"{p.Id}: {e.Message}"); }
                }
            }
        }
        if (warnings.Count > 0) proposed.Clear();
        return new(candidates, proposed, warnings);
    }
}
