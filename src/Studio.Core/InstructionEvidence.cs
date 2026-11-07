using Iced.Intel;

namespace Studio.Core;

public record EvidencePackage(string TitleId, string Version, string Process, List<SourceEvidence> Sources);

/// <summary>Original-version data with explicit coordinates. No instruction/function boundary is inferred from a dump's first byte.</summary>
public record SourceEvidence(int Section, ulong SectionBase, ulong Start, byte[] Bytes, ulong? FunctionStart = null, int FunctionLength = 0)
{
    public ulong PatchAddress(PatchLine p) => checked(SectionBase + p.Offset);
    public bool Contains(ulong address, int length) => length > 0 && address >= Start && address - Start <= (ulong)Bytes.Length && (ulong)length <= (ulong)Bytes.Length - (address - Start);
}

public record InstructionEvidence(int OriginalInstructions, int TargetInstructions, int MatchedInstructions, bool LayoutChanged,
    int OperandAgreement, int ControlFlowAgreement, List<string> Changes, string FunctionComparison)
{
    public string Summary => $"{MatchedInstructions}/{OriginalInstructions} old instructions align with {TargetInstructions} target instructions. " +
        $"Operands {OperandAgreement}/100; control flow {ControlFlowAgreement}/100. " + string.Join(" ", Changes) + " " + FunctionComparison;
}

public static class InstructionMatching
{
    public static bool SameShape(Instruction a, Instruction b)
    {
        if (a.Mnemonic != b.Mnemonic || a.OpCount != b.OpCount || a.FlowControl != b.FlowControl) return false;
        for (var n = 0; n < a.OpCount; n++)
        {
            var ak = a.GetOpKind(n); var bk = b.GetOpKind(n);
            if (IsNear(ak) && IsNear(bk)) continue;
            if (ak != bk) return false;
            if (ak == OpKind.Register && a.GetOpRegister(n).GetSize() != b.GetOpRegister(n).GetSize()) return false;
        }
        return true;
    }
    private static bool IsNear(OpKind k) => k is OpKind.NearBranch16 or OpKind.NearBranch32 or OpKind.NearBranch64;

    public static InstructionEvidence Compare(byte[] oldBytes, ulong oldAddress, byte[] targetBytes, ulong targetAddress)
    {
        var old = CodeAnalysis.Decode(oldBytes, oldAddress); var current = CodeAnalysis.Decode(targetBytes, targetAddress);
        if (old.Count > 512 || current.Count > 512) throw new InvalidDataException("Instruction comparison is limited to 512 instructions; narrow the evidence window.");
        // LCS preserves instruction order while making inserted/deleted instructions visible.
        var table = new int[old.Count + 1, current.Count + 1];
        for (var i = old.Count - 1; i >= 0; i--)
            for (var j = current.Count - 1; j >= 0; j--)
                table[i, j] = SameShape(old[i], current[j]) ? 1 + table[i + 1, j + 1] : Math.Max(table[i + 1, j], table[i, j + 1]);
        var alignment = new List<(int Old, int New)>(); var oi = 0; var ni = 0;
        while (oi < old.Count && ni < current.Count)
        {
            if (SameShape(old[oi], current[ni])) { alignment.Add((oi++, ni++)); }
            else if (table[oi + 1, ni] >= table[oi, ni + 1]) oi++;
            else ni++;
        }
        var changes = new List<string>(); var operandChecks = 0; var equalOperands = 0;
        var branchChecks = Math.Max(old.Count(i => i.FlowControl != FlowControl.Next), current.Count(i => i.FlowControl != FlowControl.Next)); var equalBranches = 0;
        foreach (var (aIndex, bIndex) in alignment)
        {
            var a = old[aIndex]; var b = current[bIndex];
            for (var op = 0; op < a.OpCount; op++)
            {
                if (IsNear(a.GetOpKind(op))) continue;
                operandChecks++;
                bool same;
                if (a.GetOpKind(op) == OpKind.Register) same = a.GetOpRegister(op) == b.GetOpRegister(op);
                else if (a.GetOpKind(op) == OpKind.Memory)
                {
                    same = a.MemoryBase == b.MemoryBase && a.MemoryIndex == b.MemoryIndex && a.MemoryIndexScale == b.MemoryIndexScale && a.MemorySize == b.MemorySize &&
                        (a.IsIPRelativeMemoryOperand && b.IsIPRelativeMemoryOperand || a.MemoryDisplacement64 == b.MemoryDisplacement64);
                    if (a.IsIPRelativeMemoryOperand && b.IsIPRelativeMemoryOperand && a.IPRelativeMemoryAddress != b.IPRelativeMemoryAddress)
                        changes.Add($"RIP-relative target changed: 0x{a.IPRelativeMemoryAddress:X} → 0x{b.IPRelativeMemoryAddress:X}; data identity is unproven.");
                }
                else if (a.GetOpKind(op).ToString().StartsWith("Immediate", StringComparison.Ordinal)) same = a.GetImmediate(op) == b.GetImmediate(op);
                else same = false;
                if (same) equalOperands++;
                else changes.Add($"Instruction {aIndex + 1} ({a.Mnemonic}) operand {op + 1} changed; inspect its register/value/field offset.");
            }
            if (CodeAnalysis.IsBranch(a) && CodeAnalysis.IsBranch(b))
            {
                var oldTarget = old.FindIndex(i => i.IP == a.NearBranchTarget);
                var newTarget = current.FindIndex(i => i.IP == b.NearBranchTarget);
                if (oldTarget >= 0 && newTarget >= 0 && alignment.Contains((oldTarget, newTarget))) equalBranches++;
                else if (oldTarget < 0 && newTarget < 0)
                {
                    changes.Add($"External {a.Mnemonic} target 0x{a.NearBranchTarget:X} → 0x{b.NearBranchTarget:X}; the destination function is not verified.");
                    // Relative address arithmetic alone does not establish an equivalent callee.
                }
                else changes.Add($"Instruction {aIndex + 1} branch destination or internal/external control flow changed.");
            }
            else if (a.FlowControl == FlowControl.Return && b.FlowControl == FlowControl.Return) equalBranches++;
        }
        var layout = old.Count != current.Count || !old.Select(i => i.Length).SequenceEqual(current.Select(i => i.Length)) || alignment.Count != old.Count;
        if (layout) changes.Insert(0, $"Instruction layout changed: {old.Count} → {current.Count}; inserted/deleted/changed instructions need manual review.");
        if (!old.Where(i => i.FlowControl != FlowControl.Next).Select(i => (i.Mnemonic, i.FlowControl)).SequenceEqual(current.Where(i => i.FlowControl != FlowControl.Next).Select(i => (i.Mnemonic, i.FlowControl))))
            changes.Add("Branch condition, call/return kind, or control-flow instruction count changed.");
        if (alignment.Count < old.Count) changes.Add("Changed old instructions: " + string.Join(", ", old.Where((_, i) => !alignment.Any(a => a.Old == i)).Select(i => $"0x{i.IP:X} {i.Mnemonic}")) + ".");
        return new(old.Count, current.Count, alignment.Count, layout,
            operandChecks == 0 ? 100 : 100 * equalOperands / operandChecks,
            branchChecks == 0 ? 100 : 100 * equalBranches / branchChecks,
            changes.Distinct().ToList(), "No function boundary evidence was supplied; semantic equivalence is unproven.");
    }

    public static string CompareFunction(SourceEvidence source, PatchLine patch, byte[] target, MemoryRegion region, ulong candidate, CancellationToken ct)
    {
        if (source.FunctionStart is not ulong start || source.FunctionLength is < 1 or > 4096 || !source.Contains(start, source.FunctionLength) ||
            source.PatchAddress(patch) < start || source.PatchAddress(patch) + (ulong)patch.Original.Length > start + (ulong)source.FunctionLength)
            return "No verified source function bounds cover this patch. Capture a bounded old-version function to compare its structure.";
        var oldBytes = source.Bytes.AsSpan((int)(start - source.Start), source.FunctionLength).ToArray();
        try
        {
            var old = CodeAnalysis.Decode(oldBytes, start);
            if (old.Count > 256 || old[^1].FlowControl != FlowControl.Return) return "Supplied source function does not end with a decoded return; function-level comparison is unavailable.";
            var prefixLength = 0;
            foreach (var instruction in old) { prefixLength += instruction.Length; if (prefixLength >= 16) break; }
            var prefix = oldBytes[..prefixLength];
            var signature = CodeAnalysis.Signature(prefix);
            var anchor = signature.Find(target, 1000, ct).Where(at => region.Start + (ulong)at <= candidate && candidate - (region.Start + (ulong)at) <= 4096).LastOrDefault(-1);
            if (anchor < 0) return "The old function-entry anchor was not found near this candidate; function identity is unproven.";
            var decoder = Decoder.Create(64, new ByteArrayCodeReader(target, anchor, Math.Min(4096, target.Length - anchor))); decoder.IP = region.Start + (ulong)anchor;
            var total = 0; var instructions = 0;
            while (total < Math.Min(4096, target.Length - anchor) && instructions++ < 512)
            {
                var instruction = decoder.Decode(); if (instruction.IsInvalid) break; total += instruction.Length;
                if (instruction.FlowControl != FlowControl.Return) continue;
                if (candidate >= region.Start + (ulong)(anchor + total)) break;
                var comparison = Compare(oldBytes, start, target.AsSpan(anchor, total).ToArray(), region.Start + (ulong)anchor);
                return $"Bounded function evidence: entry signature and return found; {comparison.MatchedInstructions}/{comparison.OriginalInstructions} instructions align, control-flow evidence {comparison.ControlFlowAgreement}/100. " +
                    string.Join(" ", comparison.Changes.Take(6)) + " " +
                    "This compares one bounded linear function body, not all callers/paths; it does not prove equivalent behavior.";
            }
            return "No bounded target function return covers the candidate; inspect the function manually.";
        }
        catch (Exception e) when (e is InvalidDataException or FormatException) { return "Function comparison unavailable: " + e.Message; }
    }
}
