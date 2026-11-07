using System.Security.Cryptography;
using System.Text.Json;

namespace Studio.Core;

public record ResolvedPatch(string Id, ulong Address, byte[] Original, byte[] Replacement, string[] Dependencies, bool IsCodeCave = false, bool CaveUseApproved = false);
public record PatchPlan(Guid ConnectionId, GameIdentity Game, string CheatName, List<ResolvedPatch> Patches, bool InstructionPatch);
public record PatchReview(string Digest, PatchPlan Plan, string Description);
public record PatchApproval(string Digest, bool EnableWrites, bool AcceptCrashRisk);
public record RollbackJournal(Guid Id, Guid ConnectionId, GameIdentity Game, string CheatName, List<ResolvedPatch> Patches, DateTimeOffset Created);

public sealed class PatchService(MemorySession session, string journalDirectory)
{
    public async Task<PatchReview> ReviewAsync(PatchPlan plan, CancellationToken ct = default)
    {
        ValidatePlan(plan);
        await session.ExclusiveAsync(async () => { await CheckIdentity(plan, ct); await CheckOriginals(plan, ct); return true; }, ct);
        var immutable = Clone(plan);
        return new(Digest(immutable), immutable, string.Join(Environment.NewLine, immutable.Patches.Select(p => $"0x{p.Address:X}: {Hex.Format(p.Original)} → {Hex.Format(p.Replacement)}")));
    }
    public Task<RollbackJournal> ApplyAsync(PatchReview review, PatchApproval approval, CancellationToken ct = default) => session.ExclusiveAsync(async () =>
    {
        var plan = Clone(review.Plan); ValidatePlan(plan);
        if (!approval.EnableWrites || !approval.AcceptCrashRisk || approval.Digest != Digest(plan) || approval.Digest != review.Digest) throw new InvalidOperationException("Explicit approval of these exact bytes and crash/save-corruption risks is required.");
        await CheckIdentity(plan, ct); await CheckOriginals(plan, ct);
        var ordered = Order(plan.Patches);
        var journal = new RollbackJournal(Guid.NewGuid(), plan.ConnectionId, plan.Game, plan.CheatName, ordered, DateTimeOffset.UtcNow);
        Directory.CreateDirectory(journalDirectory);
        // Durable backup before the first write. A network failure can leave a partial write.
        await LocalStore.SaveAsync(Path.Combine(journalDirectory, journal.Id + ".json"), journal, ct);
        var attempted = new List<ResolvedPatch>();
        try
        {
            foreach (var p in ordered)
            {
                ct.ThrowIfCancellationRequested();
                // Recheck directly before writing too; memory may change after the batch preflight.
                if (!(await session.Memory.ReadAsync(plan.Game.Pid, p.Address, p.Original.Length, ct)).SequenceEqual(p.Original)) throw new InvalidDataException($"Original bytes changed at 0x{p.Address:X}.");
                attempted.Add(p);
                await session.Memory.WriteAsync(plan.Game.Pid, p.Address, p.Replacement, ct);
                if (!(await session.Memory.ReadAsync(plan.Game.Pid, p.Address, p.Replacement.Length, ct)).SequenceEqual(p.Replacement)) throw new IOException($"Readback failed at 0x{p.Address:X}.");
            }
        }
        catch (Exception failure)
        {
            // No blind rollback when a connection fails: retain journal and report exact recovery state.
            throw new IOException($"Patch did not finish; {attempted.Count} line(s) may have been written. Stop gameplay. Rollback journal {journal.Id} was saved; inspect/review restoration before retrying. {failure.Message}", failure);
        }
        return journal;
    }, ct);
    public async Task<PatchReview> ReviewRollbackAsync(RollbackJournal journal, CancellationToken ct = default)
    {
        if (journal.ConnectionId != session.Memory.ConnectionId) throw new InvalidDataException("Rollback belongs to an older connection. Revalidate the saved addresses and game session manually; automatic restoration is blocked.");
        var plan = new PatchPlan(journal.ConnectionId, journal.Game, "Restore " + journal.CheatName, journal.Patches.AsEnumerable().Reverse().Select(p => new ResolvedPatch(p.Id, p.Address, p.Replacement.ToArray(), p.Original.ToArray(), [])).ToList(), false);
        return await ReviewAsync(plan, ct);
    }
    private async Task CheckIdentity(PatchPlan plan, CancellationToken ct)
    {
        if (plan.ConnectionId == Guid.Empty || plan.ConnectionId != session.Memory.ConnectionId) throw new InvalidDataException("The console connection changed. Analyze the cheat again.");
        var game = await session.Memory.GetGameAsync(ct);
        if (!game.IsGame || !game.SameGame(plan.Game) || !game.HasKnownVersion) throw new InvalidDataException("Running game, process, CUSA or known XX.YY version does not match the reviewed plan.");
    }
    private async Task CheckOriginals(PatchPlan plan, CancellationToken ct)
    {
        var maps = await session.Memory.GetMapsAsync(plan.Game.Pid, ct);
        foreach (var p in plan.Patches)
        {
            if (p.Address >= 0x0000800000000000 || !maps.Any(m => m.Readable && (!plan.InstructionPatch && !p.IsCodeCave || m.Executable) && m.Contains(p.Address, p.Original.Length))) throw new InvalidDataException($"Patch 0x{p.Address:X} is outside mapped user memory or lacks executable permission for a code patch.");
            if (plan.InstructionPatch) foreach (var instruction in CodeAnalysis.Decode(p.Replacement, p.Address).Where(CodeAnalysis.IsBranch))
                if (instruction.NearBranchTarget >= 0x0000800000000000 || !maps.Any(m => m.Readable && m.Executable && m.Contains(instruction.NearBranchTarget, 1)))
                    throw new InvalidDataException($"{p.Id}: branch destination 0x{instruction.NearBranchTarget:X} is outside readable executable user memory. Correct the jump/return mapping before testing.");
            var original = await session.Memory.ReadAsync(plan.Game.Pid, p.Address, p.Original.Length, ct);
            if (!original.SequenceEqual(p.Original)) throw new InvalidDataException($"Original-byte mismatch at 0x{p.Address:X}. Nothing was written.");
        }
    }
    private static void ValidatePlan(PatchPlan plan)
    {
        if (!plan.Game.IsGame || plan.Patches.Count is 0 or > 1024) throw new InvalidDataException("Select a running CUSA game and 1–1024 patches.");
        foreach (var p in plan.Patches)
        {
            if (p.Original.Length == 0 || p.Original.Length != p.Replacement.Length || p.Original.Length > 1048576 || p.Address > ulong.MaxValue - (ulong)p.Original.Length) throw new InvalidDataException("Invalid patch size or address.");
            var padding = plan.InstructionPatch && p.Original.Length >= 5 && p.Original.All(b => b == p.Original[0]) && p.Original[0] is 0 or 0x90 or 0xCC;
            if ((p.IsCodeCave || padding) && !p.CaveUseApproved) throw new InvalidDataException("A possible code cave requires explicit confirmation of its use; padding alone is not proof of safe executable space. Review it through the candidate workflow or multi-line editor.");
            if (plan.InstructionPatch) { CodeAnalysis.Decode(p.Original, p.Address); CodeAnalysis.Decode(p.Replacement, p.Address); }
        }
        var byAddress = plan.Patches.OrderBy(p => p.Address).ToArray();
        for (var i = 1; i < byAddress.Length; i++) if (byAddress[i - 1].Address + (ulong)byAddress[i - 1].Original.Length > byAddress[i].Address) throw new InvalidDataException("Overlapping patches are not allowed.");
        if (plan.InstructionPatch)
        {
            foreach (var p in plan.Patches)
                foreach (var jump in CodeAnalysis.Decode(p.Replacement, p.Address).Where(CodeAnalysis.IsBranch))
                {
                    var destination = plan.Patches.FirstOrDefault(other => jump.NearBranchTarget >= other.Address && jump.NearBranchTarget - other.Address < (ulong)other.Replacement.Length);
                    if (destination is null) continue;
                    if (!CodeAnalysis.Decode(destination.Replacement, destination.Address).Any(i => i.IP == jump.NearBranchTarget))
                        throw new InvalidDataException($"{p.Id}: branch destination lands inside a replacement instruction in {destination.Id}.");
                    if (destination.Id != p.Id && destination.IsCodeCave && !p.Dependencies.Contains(destination.Id))
                        throw new InvalidDataException($"{p.Id}: cave dependency {destination.Id} must precede the hook.");
                }
        }
        Order(plan.Patches);
    }
    private static List<ResolvedPatch> Order(List<ResolvedPatch> lines)
    {
        if (lines.Select(p => p.Id).Distinct().Count() != lines.Count) throw new InvalidDataException("Duplicate patch IDs.");
        var ordered = new List<ResolvedPatch>(); var active = new HashSet<string>(); var done = new HashSet<string>();
        void Visit(ResolvedPatch p)
        {
            if (active.Contains(p.Id)) throw new InvalidDataException("Cyclic patch dependencies.");
            if (done.Contains(p.Id)) return;
            active.Add(p.Id);
            foreach (var d in p.Dependencies) Visit(lines.SingleOrDefault(l => l.Id == d) ?? throw new InvalidDataException($"Missing dependency {d}."));
            active.Remove(p.Id); done.Add(p.Id); ordered.Add(p);
        }
        foreach (var p in lines) Visit(p); return ordered;
    }
    private static PatchPlan Clone(PatchPlan p) => p with { Patches = p.Patches.Select(l => l with { Original = l.Original.ToArray(), Replacement = l.Replacement.ToArray(), Dependencies = l.Dependencies.ToArray() }).ToList() };
    private static string Digest(PatchPlan p) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(p)));
}
