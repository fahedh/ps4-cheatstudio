using Iced.Intel;

namespace Studio.Core;

public record PortingOptions
{
    public List<SourceEvidence> Sources { get; init; } = [];
    public Dictionary<int, ulong> SourceSectionBases { get; init; } = [];
    public HashSet<string> ReviewedCaves { get; init; } = [];
    public Dictionary<string, ulong> SelectedCandidates { get; init; } = [];
}

public static class PortingEngine
{
    public static PortResult Analyze(Cheat cheat, byte[] target, MemoryRegion region, bool instructionPatch, PortSourceContext? sourceContext = null, CancellationToken ct = default) =>
        AnalyzeSegments(cheat, [(region, target)], instructionPatch, ct, sourceContext is null ? null : new()
        {
            Sources = [new(cheat.Patches[0].Section, 0, sourceContext.Start, sourceContext.Bytes)]
        });

    public static PortResult AnalyzeSegments(Cheat cheat, IReadOnlyList<(MemoryRegion Region, byte[] Bytes)> segments,
        bool instructionPatch, CancellationToken ct = default, PortingOptions? options = null)
    {
        options ??= new();
        TrainerCodec.Validate(new("Analysis", "CUSA00000", "01.00", "eboot.bin", [cheat]));
        foreach (var (region, bytes) in segments)
            if (region.End <= region.Start || bytes.Length == 0 || (ulong)bytes.Length > region.Size) throw new InvalidDataException("Target bytes must fit their mapped region.");
        var warnings = new List<string>(); var steps = new List<string>(); var candidates = new List<PortCandidate>();
        var chosen = new Dictionary<string, PortCandidate>();
        var bases = new Dictionary<int, ulong>(options.SourceSectionBases);
        foreach (var s in options.Sources)
        {
            if (bases.TryGetValue(s.Section, out var existing) && existing != s.SectionBase) throw new InvalidDataException("Conflicting original section-base mappings.");
            bases[s.Section] = s.SectionBase;
        }
        var crossSection = instructionPatch && cheat.Patches.Select(p => p.Section).Distinct().Count() > 1;
        if (crossSection && cheat.Patches.Any(p => !bases.ContainsKey(p.Section)))
            warnings.Add("Different source sections require explicit original section bases before any cross-section jump can be recalculated.");
        ulong OldAddress(PatchLine p) => checked((bases.TryGetValue(p.Section, out var start) ? start : 0) + p.Offset);
        var hasCaves = instructionPatch && cheat.Patches.Any(IsCave);
        var references = new List<(ulong From, ulong To, string Kind)>();
        if (hasCaves) foreach (var (region, bytes) in segments) references.AddRange(CaveAnalysis.References(bytes, region.Start, ct));

        foreach (var patch in cheat.Patches)
        {
            ct.ThrowIfCancellationRequested();
            if (patch.Absolute) { warnings.Add($"{patch.Id}: absolute SHN addressing needs an explicit manual mapping; it was not reinterpreted."); continue; }
            var source = options.Sources.FirstOrDefault(s => s.Section == patch.Section && s.Contains(OldAddress(patch), patch.Original.Length));
            if (source is not null && !source.Bytes.AsSpan((int)(OldAddress(patch) - source.Start), patch.Original.Length).SequenceEqual(patch.Original))
            {
                warnings.Add($"{patch.Id}: the loaded old-version dump does not contain the trainer's original bytes at 0x{OldAddress(patch):X}. Correct its section/base/version first.");
                source = null;
            }
            var lineCandidates = new List<PortCandidate>();
            foreach (var (region, bytes) in segments)
            {
                ct.ThrowIfCancellationRequested();
                if (!region.Readable || instructionPatch && !region.Executable) continue;
                try
                {
                    if (instructionPatch && IsCave(patch))
                    {
                        foreach (var cave in CodeAnalysis.FindCaves(bytes, region.Start, patch.Original.Length).Where(c => c.Fill == patch.Original[0]).Take(1001))
                        {
                            if (lineCandidates.Count == 1000) throw new InvalidDataException("Too many possible padding ranges; narrow the search.");
                            var at = (int)(cave.Address - region.Start); var expected = bytes.AsSpan(at, patch.Original.Length).ToArray();
                            var assessment = CaveAnalysis.Assess(patch, cave.Address, expected, region, references, bytes, region.Start, options.ReviewedCaves);
                            lineCandidates.Add(new(patch.Id, cave.Address, assessment.CanReview ? 20 : 0, expected.SequenceEqual(patch.Original), true,
                                assessment.Explanation, expected, region.Index, Cave: assessment));
                        }
                        continue;
                    }
                    var found = new Dictionary<int, (int Length, bool Context)>();
                    var signature = instructionPatch ? CodeAnalysis.Signature(patch.Original) : new BytePattern(patch.Original.Select(b => (byte?)b));
                    foreach (var at in signature.Find(bytes, 1000, ct)) found[at] = (patch.Original.Length, false);
                    if (instructionPatch)
                    {
                        FindOperandMatches(patch, bytes, region.Start, found, ct);
                        if (source is not null) FindContextMatches(patch, source, bytes, found, ct);
                    }
                    foreach (var (at, match) in found)
                    {
                        var address = checked(region.Start + (ulong)at); var expected = bytes.AsSpan(at, match.Length).ToArray();
                        var exact = expected.SequenceEqual(patch.Original); InstructionEvidence? comparison = null;
                        var reasons = new List<string> { exact ? "Original bytes match exactly." : "Code differs from the old version; this is a review candidate, not an applicable patch." };
                        var score = exact ? 65 : 20;
                        if (instructionPatch)
                        {
                            try
                            {
                                comparison = InstructionMatching.Compare(patch.Original, OldAddress(patch), expected, address);
                                score += 10 * comparison.MatchedInstructions / Math.Max(1, comparison.OriginalInstructions) + comparison.OperandAgreement / 10;
                                if (source is not null) comparison = comparison with { FunctionComparison = InstructionMatching.CompareFunction(source, patch, bytes, region, address, ct) };
                                reasons.Add(comparison.Summary);
                            }
                            catch (InvalidDataException e) { reasons.Add(e.Message); score -= 20; }
                        }
                        if (match.Context) { score += 25; reasons.Add("Both old surrounding anchors matched; the bytes between them were compared, including inserted/deleted instructions."); }
                        else if (source is null) reasons.Add("No original context dump was available. Capture old-version context to distinguish function/operand changes.");
                        lineCandidates.Add(new(patch.Id, address, Math.Clamp(score, 0, 95), exact, false, string.Join(" ", reasons), expected, region.Index, comparison));
                    }
                }
                catch (Exception e) when (e is InvalidDataException or FormatException) { warnings.Add($"{patch.Id}: {e.Message}"); }
            }
            // Overlapping windows cannot count as separate candidates; retain the strongest evidence per location.
            lineCandidates = lineCandidates.GroupBy(c => c.Address).Select(g => g.OrderByDescending(c => c.Score).ThenByDescending(c => c.Expected.Length).First()).OrderByDescending(c => c.Score).ThenBy(c => c.Address).ToList();
            foreach (var c in lineCandidates) candidates.Add(c with { Explanation = c.Explanation + (lineCandidates.Count == 1 ? " Unique in all searched ranges." : $" {lineCandidates.Count} candidates across the searched ranges; no automatic choice.") });
            PortCandidate? selection = null;
            if (options.SelectedCandidates.TryGetValue(patch.Id, out var selectedAddress)) selection = lineCandidates.SingleOrDefault(c => c.Address == selectedAddress);
            else if (lineCandidates.Count == 1) selection = lineCandidates[0];
            if (selection is null)
            {
                warnings.Add($"{patch.Id}: {(lineCandidates.Count == 0 ? "the original signature and supported instruction/context searches found no candidate" : "several candidate locations remain; inspect and explicitly select one") }.");
                continue;
            }
            if (!selection.ExactBytes)
            {
                warnings.Add($"{patch.Id}: original instructions changed. Use 'Inspect in editor' to compare the actual bytes and redesign the replacement; old replacement bytes were not applied automatically.");
                continue;
            }
            if (selection.Cave is { } selectedAssessment)
            {
                if (!selectedAssessment.CanReview) { warnings.Add($"{patch.Id}: {selectedAssessment.Explanation}"); continue; }
                if (!selectedAssessment.ConfirmedByUser) { warnings.Add($"{patch.Id}: padding is not proof of free code space. Explicitly review and confirm this cave before a proposal is testable."); continue; }
            }
            chosen[patch.Id] = selection;
        }

        var proposed = new List<PatchLine>();
        if (warnings.Count == 0 && chosen.Count == cheat.Patches.Count)
        {
            var mapping = new Dictionary<ulong, ulong>();
            void Map(ulong old, ulong target)
            {
                if (mapping.TryGetValue(old, out var prior) && prior != target) throw new InvalidDataException("Original source ranges overlap or adjacent return targets moved inconsistently; manual mapping is required.");
                mapping[old] = target;
            }
            try
            {
                foreach (var p in cheat.Patches)
                {
                    var old = OldAddress(p); var address = chosen[p.Id].Address;
                    Map(old, address); Map(checked(old + (ulong)p.Original.Length), checked(address + (ulong)p.Original.Length));
                    if (instructionPatch)
                    {
                        foreach (var i in CodeAnalysis.Decode(p.Replacement, old)) Map(i.IP, checked(address + i.IP - old));
                    }
                }
                foreach (var p in cheat.Patches)
                {
                    var selected = chosen[p.Id]; var old = OldAddress(p); var dependencies = p.Dependencies.ToHashSet();
                    if (instructionPatch) foreach (var i in CodeAnalysis.Decode(p.Replacement, old).Where(CodeAnalysis.IsBranch))
                    {
                        var destination = cheat.Patches.FirstOrDefault(other => other.Id != p.Id && i.NearBranchTarget >= OldAddress(other) && i.NearBranchTarget - OldAddress(other) < (ulong)other.Replacement.Length);
                        if (destination is not null && chosen[destination.Id].CodeCave) dependencies.Add(destination.Id);
                    }
                    var regionBase = segments.Where(s => s.Region.Index == selected.Section).Min(s => s.Region.Start);
                    proposed.Add(p with
                    {
                        Section = selected.Section, Offset = selected.Address - regionBase, Original = selected.Expected,
                        Replacement = CodeAnalysis.Relocate(p.Replacement, old, selected.Address, mapping, instructionPatch), Dependencies = dependencies.ToArray(), Absolute = false,
                        Ps5Offset = null, IsCodeCave = selected.CodeCave, CaveUseApproved = selected.Cave?.ConfirmedByUser == true
                    });
                }
                var ordered = proposed.OrderBy(p => chosen[p.Id].Address).ToArray();
                for (var n = 1; n < ordered.Length; n++)
                    if (chosen[ordered[n - 1].Id].Address + (ulong)ordered[n - 1].Original.Length > chosen[ordered[n].Id].Address) throw new InvalidDataException("Candidate patch ranges overlap in target memory.");
                TrainerCodec.Validate(new("Candidate", "CUSA00000", "01.00", "eboot.bin", [new(cheat.Name, cheat.Description, proposed)]));
            }
            catch (InvalidDataException e) { warnings.Add(e.Message); }
        }
        if (warnings.Count > 0) proposed.Clear();
        if (candidates.Any(c => c.CodeCave)) steps.Add("For each possible cave, inspect incoming branch/data references, instruction boundaries, allocation lifetime and return jumps. 'Review cave' records your explicit judgment; the tool never proves padding is unused.");
        if (candidates.Any(c => !c.ExactBytes)) steps.Add("Select a changed candidate and choose 'Inspect in editor'. Compare old/new disassembly and the reported operands/layout. Capture a writer with a hardware watchpoint during gameplay if the intended instruction is unclear. Do not reuse the old replacement blindly.");
        if (warnings.Count > 0)
        {
            steps.Add("Load an old-version raw dump with its original base/section mapping, or connect to that exact old game version and use 'Capture old context'. The app verifies old bytes before using the evidence.");
            steps.Add("Analyze again to compare surrounding signatures and rank candidates. Optional explicit old function start/length enables bounded function-body and branch-structure comparison; this is not semantic proof.");
            steps.Add("Only live gameplay/debugging can establish what writes health/currency and whether a cheat works. Test reviewed bytes with rollback, then separately record real-hardware gameplay success.");
        }
        return new(candidates.OrderBy(c => c.PatchId).ThenByDescending(c => c.Score).ToList(), proposed, warnings.Distinct().ToList()) { NextSteps = steps };
    }

    public static bool IsCave(PatchLine patch) => patch.IsCodeCave || patch.Original.Length >= 5 && patch.Original.All(b => b == patch.Original[0]) && patch.Original[0] is 0 or 0x90 or 0xCC;

    private static void FindOperandMatches(PatchLine patch, byte[] bytes, ulong start, Dictionary<int, (int Length, bool Context)> found, CancellationToken ct)
    {
        List<Instruction> old;
        try { old = CodeAnalysis.Decode(patch.Original, 0); } catch (InvalidDataException) { return; }
        if (old.Count is < 2 or > 32) return; // Single instructions are too generic for unanchored shape search.
        var decoder = Decoder.Create(64, new ByteArrayCodeReader(bytes)); decoder.IP = start;
        var window = new Queue<Instruction>(); var count = 0;
        while (decoder.IP - start < (ulong)bytes.Length)
        {
            if ((count++ & 4095) == 0) ct.ThrowIfCancellationRequested();
            var instruction = decoder.Decode(); if (instruction.IsInvalid) { window.Clear(); continue; }
            window.Enqueue(instruction); if (window.Count > old.Count) window.Dequeue();
            if (window.Count != old.Count || window.Peek().Mnemonic != old[0].Mnemonic || !window.Zip(old).All(pair => InstructionMatching.SameShape(pair.First, pair.Second))) continue;
            var first = window.Peek(); var at = (int)(first.IP - start); var length = (int)(instruction.NextIP - first.IP);
            found.TryAdd(at, (length, false));
            if (found.Count > 1000) throw new InvalidDataException("Too many instruction-shape candidates; capture surrounding old-version context or narrow the range.");
        }
    }
    private static void FindContextMatches(PatchLine patch, SourceEvidence source, byte[] bytes, Dictionary<int, (int Length, bool Context)> found, CancellationToken ct)
    {
        var address = source.PatchAddress(patch); var before = (int)Math.Min(32UL, address - source.Start);
        if (source.FunctionStart is ulong function && function <= address && address - function <= 4096 && source.Contains(function, (int)(address - function)))
        {
            try
            {
                var prefixInstructions = CodeAnalysis.Decode(source.Bytes.AsSpan((int)(function - source.Start), (int)(address - function)).ToArray(), function);
                var boundary = prefixInstructions.Where(i => address - i.IP >= 8 && address - i.IP <= 47).OrderBy(i => Math.Abs((long)(address - i.IP) - 32)).FirstOrDefault();
                if (boundary.Length > 0) before = (int)(address - boundary.IP);
            }
            catch (InvalidDataException) { /* No supplied boundary proof; exact/context discovery remains conservative. */ }
        }
        var afterAt = checked((int)(address - source.Start) + patch.Original.Length); var after = Math.Min(32, source.Bytes.Length - afterAt);
        if (before < 8 || after < 8) return;
        var prefix = CodeAnalysis.Signature(source.Bytes.AsSpan((int)(address - source.Start) - before, before).ToArray());
        var suffix = CodeAnalysis.Signature(source.Bytes.AsSpan(afterAt, after).ToArray());
        static bool Strong(BytePattern p) => p.Bytes.Count(b => b is not null) >= 6 && p.Bytes.Where(b => b is not null).Distinct().Count() >= 3;
        if (!Strong(prefix) || !Strong(suffix)) return;
        var ends = suffix.Find(bytes, 1000, ct);
        foreach (var start in prefix.Find(bytes, 1000, ct))
        {
            var hook = start + before;
            var possibleEnds = ends.Where(e => e > hook && e - hook <= patch.Original.Length + 32).ToArray();
            if (possibleEnds.Length > 1) throw new InvalidDataException("Surrounding anchors have multiple possible instruction-span boundaries. Supply stronger context; no overwrite length was chosen.");
            foreach (var end in possibleEnds)
            {
                found[hook] = (end - hook, true);
                if (found.Count > 1000) throw new InvalidDataException("Surrounding anchors are ambiguous; provide stronger function/context evidence.");
            }
        }
    }
}
