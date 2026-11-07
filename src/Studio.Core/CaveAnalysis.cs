using System.Security.Cryptography;
using System.Text;
using Iced.Intel;

namespace Studio.Core;

public record CaveAssessment(bool ExecutableRegion, bool OriginalBytesMatch, bool BoundaryProven, List<string> Conflicts,
    string ReviewToken, bool ConfirmedByUser)
{
    public bool CanReview => ExecutableRegion && OriginalBytesMatch && Conflicts.Count == 0;
    public string Explanation => !CanReview ? "Cave rejected: " + string.Join(" ", Conflicts) :
        (ConfirmedByUser ? "User confirmed cave use; automatic analysis does not prove it is unused. " : "Manual cave confirmation required. ") +
        (BoundaryProven ? "A decoded return precedes the padding. " : "An instruction boundary at the padding start is not proven. ") +
        "Check allocation lifetime, all references, copied instructions and return jumps before testing.";
}

public static class CaveAnalysis
{
    public static IReadOnlyList<(ulong From, ulong To, string Kind)> References(byte[] bytes, ulong start, CancellationToken ct)
    {
        var result = new List<(ulong, ulong, string)>();
        var decoder = Iced.Intel.Decoder.Create(64, new ByteArrayCodeReader(bytes)); decoder.IP = start;
        var count = 0;
        while (decoder.IP - start < (ulong)bytes.Length)
        {
            if ((count++ & 4095) == 0) ct.ThrowIfCancellationRequested();
            var i = decoder.Decode(); if (i.IsInvalid) continue;
            if (CodeAnalysis.IsBranch(i)) result.Add((i.IP, i.NearBranchTarget, i.Mnemonic.ToString()));
            if (i.IsIPRelativeMemoryOperand) result.Add((i.IP, i.IPRelativeMemoryAddress, "RIP-relative memory"));
            if (result.Count > 1000000) throw new InvalidDataException("Too many code references; narrow the target ranges for cave analysis.");
        }
        return result;
    }

    public static CaveAssessment Assess(PatchLine patch, ulong address, byte[] expected, MemoryRegion region,
        IReadOnlyList<(ulong From, ulong To, string Kind)> references, byte[] window, ulong windowStart, ISet<string> confirmations)
    {
        var conflicts = new List<string>();
        var mapped = region.Readable && region.Executable && region.Contains(address, expected.Length);
        var captured = address >= windowStart && address - windowStart <= (ulong)window.Length && (ulong)expected.Length <= (ulong)window.Length - (address - windowStart);
        var originals = captured && expected.SequenceEqual(patch.Original) && window.AsSpan((int)(address - windowStart), expected.Length).SequenceEqual(expected);
        if (!mapped) conflicts.Add("The full cave is not inside a readable executable memory region.");
        if (!originals) conflicts.Add("Padding does not match the expected original bytes.");
        if (expected.Any(b => b is not (0 or 0x90 or 0xCC)) || expected.Distinct().Count() != 1) conflicts.Add("The range contains existing non-padding code/data.");
        if (expected.All(b => b == 0xCC)) conflicts.Add("INT3-filled memory may contain active breakpoint reservations; choose and inspect another range.");
        foreach (var r in references.Where(r => r.To >= address && r.To - address < (ulong)expected.Length).Take(8))
            conflicts.Add($"Possible existing {r.Kind} reference from 0x{r.From:X} into the cave at 0x{r.To:X}.");
        var at = captured ? (int)(address - windowStart) : 0; var boundary = false;
        // Evidence, not proof of safe allocation: a nearby RET can explain why padding starts here.
        for (var back = 1; back <= Math.Min(15, at); back++)
        {
            try
            {
                var preceding = CodeAnalysis.Decode(window.AsSpan(at - back, back).ToArray(), address - (ulong)back);
                if (preceding[^1].FlowControl == FlowControl.Return) { boundary = true; break; }
            }
            catch (InvalidDataException) { }
        }
        var token = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{patch.Id}:{region.Index}:{address:X}:{Hex.Format(expected)}")));
        return new(mapped, originals, boundary, conflicts, token, confirmations.Contains(token));
    }
}
