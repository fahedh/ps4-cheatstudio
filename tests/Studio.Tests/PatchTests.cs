using Studio.Core;
using Xunit;

namespace Studio.Tests;
public class PatchTests
{
    [Fact] public async Task ApplyAndRollbackVerifyBothDirections()
    {
        await using var h = new Harness(); await h.Connect(); var service = h.Patcher;
        var review = await service.ReviewAsync(h.Plan()); var journal = await service.ApplyAsync(review, new(review.Digest, true, true));
        Assert.Equal(Hex.Bytes("90909090909090"), await h.Client.ReadAsync(100, 0x402800, 7)); Assert.True(File.Exists(Path.Combine(h.JournalDirectory, journal.Id + ".json")));
        var restore = await service.ReviewRollbackAsync(journal); await service.ApplyAsync(restore, new(restore.Digest, true, true)); Assert.Equal(Hex.Bytes("8943604883C408"), await h.Client.ReadAsync(100, 0x402800, 7));
    }
    [Theory] [InlineData(false,true)] [InlineData(true,false)] [InlineData(false,false)]
    public async Task ExplicitWriteAndRiskApprovalAreRequired(bool writes, bool risk)
    {
        await using var h = new Harness(); await h.Connect(); var review = await h.Patcher.ReviewAsync(h.Plan()); await Assert.ThrowsAsync<InvalidOperationException>(() => h.Patcher.ApplyAsync(review, new(review.Digest, writes, risk))); Assert.Equal(0, h.Server.WriteCount);
    }
    [Fact] public async Task OriginalMismatchRejectsBeforeAnyWrite()
    {
        await using var h = new Harness(); await h.Connect(); await Assert.ThrowsAsync<InvalidDataException>(() => h.Patcher.ReviewAsync(h.Plan(Hex.Bytes("00000000000000")))); Assert.Equal(0, h.Server.WriteCount);
    }
    [Fact] public async Task OriginalBytesAreCheckedAgainAfterReview()
    {
        await using var h = new Harness(); await h.Connect(); var service = h.Patcher; var review = await service.ReviewAsync(h.Plan()); h.Server.Memory[0x2800] = 0xCC;
        await Assert.ThrowsAsync<InvalidDataException>(() => service.ApplyAsync(review, new(review.Digest, true, true))); Assert.Equal(0, h.Server.WriteCount);
    }
    [Fact] public async Task ChangedGameVersionRejectsApply()
    {
        await using var h = new Harness(); await h.Connect(); var review = await h.Patcher.ReviewAsync(h.Plan()); h.Server.Game = h.Server.Game with { Version = "01.06" };
        await Assert.ThrowsAsync<InvalidDataException>(() => h.Patcher.ApplyAsync(review, new(review.Digest, true, true))); Assert.Equal(0, h.Server.WriteCount);
    }
    [Fact] public async Task ReconnectInvalidatesApproval()
    {
        await using var h = new Harness(); await h.Connect(); var review = await h.Patcher.ReviewAsync(h.Plan()); await h.Connect();
        await Assert.ThrowsAsync<InvalidDataException>(() => h.Patcher.ApplyAsync(review, new(review.Digest, true, true))); Assert.Equal(0, h.Server.WriteCount);
    }
    [Fact] public async Task TamperedReviewedBytesRejectApproval()
    {
        await using var h = new Harness(); await h.Connect(); var review = await h.Patcher.ReviewAsync(h.Plan()); review.Plan.Patches[0].Replacement[0] = 0xCC;
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Patcher.ApplyAsync(review, new(review.Digest, true, true))); Assert.Equal(0, h.Server.WriteCount);
    }
    [Fact] public async Task RejectSystemProcessesAndKernelAddresses()
    {
        await using var h = new Harness(); await h.Connect(); var plan = h.Plan();
        await Assert.ThrowsAsync<InvalidDataException>(() => h.Patcher.ReviewAsync(plan with { Game = plan.Game with { TitleId = "", ProcessName = "SceShellCore" } }));
        plan.Patches[0] = plan.Patches[0] with { Address = 0xFFFFFFFF80000000 }; await Assert.ThrowsAsync<InvalidDataException>(() => h.Patcher.ReviewAsync(plan)); Assert.Equal(0, h.Server.WriteCount);
    }
    [Fact] public async Task OverlappingAndMissingDependencyPatchesRejected()
    {
        await using var h = new Harness(); await h.Connect(); var plan = h.Plan(); plan.Patches.Add(plan.Patches[0] with { Id = "overlap", Address = 0x402801 }); await Assert.ThrowsAsync<InvalidDataException>(() => h.Patcher.ReviewAsync(plan));
        plan.Patches.RemoveAt(1); plan.Patches[0] = plan.Patches[0] with { Dependencies = ["missing"] }; await Assert.ThrowsAsync<InvalidDataException>(() => h.Patcher.ReviewAsync(plan));
    }
    [Fact] public async Task FailureRetainsJournalAndDoesNotPretendRollbackSucceeded()
    {
        await using var h = new Harness(); await h.Connect(); h.Server.FailWriteNumber = 1; var review = await h.Patcher.ReviewAsync(h.Plan());
        var e = await Assert.ThrowsAsync<IOException>(() => h.Patcher.ApplyAsync(review, new(review.Digest, true, true))); Assert.Contains("may have been written", e.Message); Assert.Single(Directory.GetFiles(h.JournalDirectory)); Assert.False(h.Client.IsConnected);
    }
    [Fact] public async Task DependenciesWriteCaveBeforeHook()
    {
        await using var h = new Harness(); await h.Connect(); var plan = h.Plan() with { InstructionPatch = false };
        plan.Patches[0] = plan.Patches[0] with { Dependencies = ["cave"] }; plan.Patches.Add(new("cave", 0x403800, new byte[12], Enumerable.Repeat((byte)0x90, 12).ToArray(), []));
        var review = await h.Patcher.ReviewAsync(plan); await h.Patcher.ApplyAsync(review, new(review.Digest, true, true)); Assert.Equal(0x403800UL, h.Server.Written[0].Address); Assert.Equal(0x402800UL, h.Server.Written[1].Address);
    }
    [Fact] public async Task SharedSessionGatePreventsConflictingHighLevelOperations()
    {
        await using var h = new Harness(); await h.Connect(); var service = h.Patcher; var review = await service.ReviewAsync(h.Plan()); var acquired = new TaskCompletionSource(); var release = new TaskCompletionSource();
        var blockingScan = h.Session.ExclusiveAsync(async () => { acquired.SetResult(); await release.Task; return true; }); await acquired.Task;
        var apply = service.ApplyAsync(review, new(review.Digest, true, true)); await Task.Delay(30); Assert.Equal(0, h.Server.WriteCount); release.SetResult(); await blockingScan; await apply; Assert.Equal(1, h.Server.WriteCount);
    }
    [Fact] public async Task RollbackCannotOverwriteUnexpectedCurrentBytes()
    {
        await using var h = new Harness(); await h.Connect(); var review = await h.Patcher.ReviewAsync(h.Plan()); var journal = await h.Patcher.ApplyAsync(review, new(review.Digest, true, true));
        h.Server.Memory[0x2800] = 0xCC; await Assert.ThrowsAsync<InvalidDataException>(() => h.Patcher.ReviewRollbackAsync(journal)); Assert.Equal(1, h.Server.WriteCount);
    }
    [Fact] public async Task RollbackCannotReuseAddressesAfterReconnect()
    {
        await using var h = new Harness(); await h.Connect(); var review = await h.Patcher.ReviewAsync(h.Plan()); var journal = await h.Patcher.ApplyAsync(review, new(review.Digest, true, true));
        await h.Connect(); await Assert.ThrowsAsync<InvalidDataException>(() => h.Patcher.ReviewRollbackAsync(journal)); Assert.Equal(1, h.Server.WriteCount);
    }
}
