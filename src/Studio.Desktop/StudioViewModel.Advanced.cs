using System.Collections.ObjectModel;
using System.IO;
using System.Net;
using System.Text;
using System.Windows;
using System.Windows.Input;
using Studio.Core;

namespace Studio.Desktop;

public sealed partial class StudioViewModel
{
    private ScanSession? scan;
    private string scanStart = "400000", scanLength = "131072", scanValue = "100", signature = "89 43 60 48 83 C4 08", scanSummary = "No scan run yet.", viewerAddress = "402800", viewerLength = "128", hexView = "", manualOriginal = "89 43 60 48 83 C4 08", manualReplacement = "90 90 90 90 90 90 90", manualAddress = "402800", jumpTarget = "403800", dumpBase = "400000";
    private string localIp = "127.0.0.1", watchAddress = "420040", watchLength = "4", threadId = "101", debugStatus = "Detached. Attaching changes game execution and requires explicit confirmation.";
    private double progress;
    private ValueKind scanKind = ValueKind.Int32;
    private ScanComparison comparison = ScanComparison.Exact;
    public string ScanStart { get => scanStart; set => Set(ref scanStart, value); }
    public string ScanLength { get => scanLength; set => Set(ref scanLength, value); }
    public string ScanValue { get => scanValue; set => Set(ref scanValue, value); }
    public string Signature { get => signature; set => Set(ref signature, value); }
    public string ScanSummary { get => scanSummary; private set => Set(ref scanSummary, value); }
    public ValueKind ScanKind { get => scanKind; set => Set(ref scanKind, value); }
    public ScanComparison Comparison { get => comparison; set => Set(ref comparison, value); }
    public double Progress { get => progress; private set => Set(ref progress, value); }
    public string ViewerAddress { get => viewerAddress; set => Set(ref viewerAddress, value); }
    public string ViewerLength { get => viewerLength; set => Set(ref viewerLength, value); }
    public string HexView { get => hexView; private set => Set(ref hexView, value); }
    public string ManualOriginal { get => manualOriginal; set => Set(ref manualOriginal, value); }
    public string ManualReplacement { get => manualReplacement; set => Set(ref manualReplacement, value); }
    public string ManualAddress { get => manualAddress; set => Set(ref manualAddress, value); }
    public string JumpTarget { get => jumpTarget; set => Set(ref jumpTarget, value); }
    public string DumpBase { get => dumpBase; set => Set(ref dumpBase, value); }
    public string LocalIp { get => localIp; set => Set(ref localIp, value); }
    public string WatchAddress { get => watchAddress; set => Set(ref watchAddress, value); }
    public string WatchLength { get => watchLength; set => Set(ref watchLength, value); }
    public string ThreadId { get => threadId; set => Set(ref threadId, value); }
    public string DebugStatus { get => debugStatus; private set => Set(ref debugStatus, value); }
    public ObservableCollection<ScanHit> ScanHits { get; } = [];
    public ObservableCollection<InstructionView> Instructions { get; } = [];
    public ObservableCollection<string> Caves { get; } = [];
    public ICommand RefreshCommand { get; private set; } = null!;
    public ICommand ScanCommand { get; private set; } = null!;
    public ICommand RescanCommand { get; private set; } = null!;
    public ICommand PatternCommand { get; private set; } = null!;
    public ICommand SaveScanCommand { get; private set; } = null!;
    public ICommand LoadScanCommand { get; private set; } = null!;
    public ICommand ExportMemoryCommand { get; private set; } = null!;
    public ICommand ViewCommand { get; private set; } = null!;
    public ICommand NopCommand { get; private set; } = null!;
    public ICommand JumpCommand { get; private set; } = null!;
    public ICommand AddDraftCommand { get; private set; } = null!;
    public ICommand ManualTestCommand { get; private set; } = null!;
    public ICommand TrackCommand { get; private set; } = null!;
    public ICommand AttachCommand { get; private set; } = null!;
    public ICommand DetachCommand { get; private set; } = null!;
    public ICommand WatchCommand { get; private set; } = null!;
    public ICommand ClearWatchCommand { get; private set; } = null!;
    public ICommand ResumeCommand { get; private set; } = null!;
    public ICommand StepCommand { get; private set; } = null!;
    public ICommand RegistersCommand { get; private set; } = null!;

    private void InitializeAdvancedCommands(Func<Func<CancellationToken, Task>, Func<bool>?, AsyncCommand> command)
    {
        RefreshCommand = command(RefreshGameAsync, () => Connected && offlineDump is null);
        ScanCommand = command(async ct =>
        {
            if (game is null) throw new InvalidOperationException("Connect to a game first.");
            scan = await new MemoryScanner(session).InitialAsync(game, Hex.Address(ScanStart), int.Parse(ScanLength), ScanKind, Comparison, ScanValue, new Progress<double>(v => Progress = v * 100), ct); ShowScan();
        }, () => Connected && game?.IsGame == true);
        RescanCommand = command(async ct => { scan = await new MemoryScanner(session).RescanAsync(scan!, Comparison, ScanValue, ct); ShowScan(); }, () => Connected && scan is not null);
        PatternCommand = command(async ct =>
        {
            var pattern = BytePattern.Parse(Signature);
            var bytes = await RangeAsync(Hex.Address(ScanStart), int.Parse(ScanLength), ct);
            var hits = await Task.Run(() => pattern.Find(bytes, ct: ct), ct); ScanHits.Clear();
            foreach (var at in hits.Take(5000)) ScanHits.Add(new(Hex.Address(ScanStart) + (ulong)at, bytes.AsSpan(at, pattern.Bytes.Length).ToArray()));
            ScanSummary = $"{hits.Count} byte signatures found; displaying at most 5,000. Byte matches do not prove function equivalence."; scan = null; Status = ScanSummary;
        }, () => Connected || offlineDump is not null);
        SaveScanCommand = command(async ct =>
        {
            var path = dialogs.Save("Save scan session", "scan.studio.json", "Scan session|*.json"); if (path is not null) await LocalStore.SaveAsync(path, scan!, ct);
        }, () => scan is not null);
        LoadScanCommand = command(async ct =>
        {
            var path = dialogs.Open("Open scan session", "Scan session|*.json"); if (path is null) return;
            var saved = await LocalStore.LoadAsync<ScanSession>(path, ct);
            if (saved.Length is < 1 or > MemoryScanner.MaximumBytes || saved.Hits.Count > MemoryScanner.MaximumHits || saved.Hits.Any(h => h.Value.Length is < 1 or > 4096 || h.Address < saved.Start || h.Address - saved.Start >= (ulong)saved.Length)) throw new InvalidDataException("Invalid saved scan bounds or values.");
            scan = saved; ShowScan(); Status = "Saved results loaded for inspection. Rescanning requires the original connection and game session.";
        }, null);
        ExportMemoryCommand = command(async ct =>
        {
            var path = dialogs.Save("Export the selected memory range locally", "memory.bin", "Raw memory|*.bin"); if (path is null) return;
            var start = Hex.Address(ScanStart); var bytes = await RangeAsync(start, int.Parse(ScanLength), ct);
            await File.WriteAllBytesAsync(path, bytes, ct); await LocalStore.SaveAsync(path + ".map.json", new { start = $"0x{start:X}", length = bytes.Length, game, simulation = SimulationConnection, captured = DateTimeOffset.UtcNow }, ct);
            Status = "Memory range and address metadata saved locally.";
        }, () => Connected || offlineDump is not null);
        ViewCommand = command(ViewMemoryAsync, () => Connected || offlineDump is not null);
        NopCommand = command(_ => { ManualReplacement = Hex.Format(CodeAnalysis.Nops(Hex.Bytes(ManualOriginal), Hex.Address(ManualAddress))); Status = "NOP bytes generated at complete instruction boundaries. They are not applied."; return Task.CompletedTask; }, null);
        JumpCommand = command(_ =>
        {
            var original = Hex.Bytes(ManualOriginal); CodeAnalysis.Decode(original, Hex.Address(ManualAddress));
            if (original.Length < 5) throw new InvalidDataException("A rel32 jump needs at least 5 bytes of whole original instructions.");
            ManualReplacement = Hex.Format([.. CodeAnalysis.RelativeJump(Hex.Address(ManualAddress), Hex.Address(JumpTarget)), .. Enumerable.Repeat((byte)0x90, original.Length - 5)]);
            Status = "Relative jump calculated. Review the destination, cave and overwritten instructions before testing."; return Task.CompletedTask;
        }, null);
        AddDraftCommand = command(async ct =>
        {
            if (game is null || Regions.Count == 0) throw new InvalidOperationException("Connect to a game before creating a new trainer.");
            var original = Hex.Bytes(ManualOriginal); var replacement = Hex.Bytes(ManualReplacement); var address = Hex.Address(ManualAddress);
            var moduleBase = Hex.Address(ModuleBase);
            if (address < moduleBase || !Regions.Any(r => r.Contains(address, original.Length))) throw new InvalidDataException("The patch must be inside the selected game's mapped memory.");
            ImportTrainer(new(ProfileName, game.TitleId, game.Version, game.ProcessName, [new("New manual cheat", "User-authored unverified patch. Verify the main-module base and gameplay behavior.", [new("manual", 0, address - moduleBase, original, replacement, [])])]));
            await SaveLibraryAsync(ct); Status = "Manual cheat saved to the library as unverified. Analyze before testing or exporting.";
        }, () => Connected && game?.IsGame == true);
        ManualTestCommand = command(async ct =>
        {
            if (!WritesEnabled || game is null) throw new InvalidOperationException("Enable write testing first.");
            var plan = new PatchPlan(client.ConnectionId, game, "Manual patch", [new("manual", Hex.Address(ManualAddress), Hex.Bytes(ManualOriginal), Hex.Bytes(ManualReplacement), [])], InstructionPatch);
            var service = Patcher(); var review = await service.ReviewAsync(plan, ct);
            if (!dialogs.ConfirmPatch(review)) return;
            rollback = await service.ApplyAsync(review, new(review.Digest, WritesEnabled, true), ct); testedPlan = null; hardwareConfirmed = false;
            VerificationStatus = "Manual patch read back successfully. Gameplay effect is unverified. Use Restore original bytes to undo."; Status = VerificationStatus; await RecordVerification("Manual byte-write/readback", VerificationStatus, ct);
        }, () => Connected && WritesEnabled && game?.IsGame == true);
        TrackCommand = command(async ct =>
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested(); var bytes = await RangeAsync(Hex.Address(ManualAddress), Values.Width(ScanKind), ct);
                ScanSummary = $"Live value at 0x{Hex.Address(ManualAddress):X}: {FormatValue(bytes, ScanKind)} ({Hex.Format(bytes)})";
                await Task.Delay(500, ct);
            }
        }, () => Connected && ScanKind != ValueKind.Hex);
        AttachCommand = command(async ct =>
        {
            if (!WritesEnabled || game is null) throw new InvalidOperationException("Enable write testing before attaching a debugger.");
            if (!dialogs.Confirm("Attach to game debugger", "Attaching and watchpoints change game execution and may pause or crash the game. Allow the PS4 to connect back to this PC on TCP 755. Continue?")) return;
            var local = IPAddress.Parse(LocalIp); var remote = SimulationConnection ? IPAddress.Loopback : (await Dns.GetHostAddressesAsync(Host, ct)).First(ip => ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
            debug = new(client);
            debug.Interrupted += interrupt => Application.Current.Dispatcher.Invoke(() =>
            {
                ThreadId = interrupt.ThreadId.ToString(); ViewerAddress = interrupt.Rip.ToString("X");
                DebugStatus = $"Stopped: thread {interrupt.ThreadId} ({interrupt.ThreadName}), RIP 0x{interrupt.Rip:X}, RSP 0x{interrupt.Rsp:X}. A data watchpoint reports the instruction after the memory access. Inspect nearby code; resume when ready.";
                HexView = FormatRegisters(interrupt.Registers); log.Write("Debug interrupt", DebugStatus);
            });
            debug.Error += e => Application.Current.Dispatcher.Invoke(() => { DebugStatus = e; log.Write("Debug channel", e, "Error"); });
            await debug.AttachAsync(game, local, remote, int.Parse(InterruptPort), ct); DebugStatus = "Attached. Set an aligned write watchpoint or single-step a thread.";
        }, () => Connected && WritesEnabled && debug?.Attached != true);
        DetachCommand = command(async ct => { if (debug is not null) { await debug.DetachAsync(ct); await debug.DisposeAsync(); debug = null; DebugStatus = "Detached. Watchpoints cleared by the debugger."; } }, () => debug?.Attached == true);
        WatchCommand = command(async ct =>
        {
            var address = Hex.Address(WatchAddress); var length = int.Parse(WatchLength);
            if (!Regions.Any(r => r.Readable && r.Contains(address, length))) throw new InvalidDataException("Watchpoint is outside the selected game's regions.");
            await debug!.WatchAsync(0, address, length, true, ct); DebugStatus = "Hardware write watchpoint enabled in slot 0. Change the value in the game to stop on a writer.";
        }, () => debug?.Attached == true && WritesEnabled);
        ClearWatchCommand = command(async ct => { await debug!.WatchAsync(0, Hex.Address(WatchAddress), int.Parse(WatchLength), false, ct); DebugStatus = "Watchpoint slot 0 cleared."; }, () => debug?.Attached == true && WritesEnabled);
        ResumeCommand = command(async ct => { await debug!.ResumeAsync(ct); DebugStatus = "Continue command accepted."; }, () => debug?.Attached == true);
        StepCommand = command(async ct => { await debug!.StepAsync(int.Parse(ThreadId), ct); DebugStatus = "Thread step accepted; waiting for interrupt."; }, () => debug?.Attached == true && WritesEnabled);
        RegistersCommand = command(async ct => { HexView = FormatRegisters(await debug!.GetRegistersAsync(int.Parse(ThreadId), ct)); }, () => debug?.Attached == true);
    }
    private void ShowScan()
    {
        ScanHits.Clear(); foreach (var hit in scan!.Hits.Take(5000)) ScanHits.Add(hit);
        ScanSummary = $"{scan.Hits.Count:N0} values remain ({scan.Kind}); displaying at most 5,000. Change the value in the game and choose a comparison to narrow the results."; Status = ScanSummary;
    }
    private async Task<byte[]> RangeAsync(ulong start, int length, CancellationToken ct)
    {
        if (length is < 1 or > MemoryScanner.MaximumBytes) throw new ArgumentOutOfRangeException(nameof(length), "Select 1 byte to 64 MiB.");
        if (offlineDump is not null)
        {
            var region = Regions.Single(); if (!region.Contains(start, length)) throw new InvalidDataException("Range is outside the loaded dump.");
            return offlineDump.AsSpan(checked((int)(start - region.Start)), length).ToArray();
        }
        if (game is null) throw new InvalidOperationException("Connect or load a dump first.");
        return await new MemoryScanner(session).SnapshotAsync(game.Pid, start, length, new Progress<double>(v => Progress = v * 100), ct);
    }
    private async Task ViewMemoryAsync(CancellationToken ct)
    {
        var length = int.Parse(ViewerLength); if (length is < 1 or > 65536) throw new ArgumentOutOfRangeException(nameof(ViewerLength), "Viewer range must be 1–65536 bytes.");
        var start = Hex.Address(ViewerAddress); var bytes = await RangeAsync(start, length, ct); var output = new StringBuilder();
        for (var i = 0; i < bytes.Length; i += 16)
        {
            var row = bytes.AsSpan(i, Math.Min(16, bytes.Length - i)).ToArray();
            output.AppendLine($"{start + (ulong)i:X16}  {string.Join(' ', row.Select(b => b.ToString("X2"))).PadRight(47)}  {new string(row.Select(b => b is >= 32 and < 127 ? (char)b : '.').ToArray())}");
        }
        HexView = output.ToString(); Instructions.Clear(); foreach (var i in CodeAnalysis.Disassemble(bytes, start)) Instructions.Add(i);
        Caves.Clear(); foreach (var cave in CodeAnalysis.FindCaves(bytes, start, 16)) Caves.Add(cave.Explanation);
        Status = "Memory read and disassembled locally. Truncated instructions are shown explicitly.";
    }
    private static string FormatValue(byte[] b, ValueKind k) => k switch { ValueKind.Byte => b[0].ToString(), ValueKind.Int16 => BitConverter.ToInt16(b).ToString(), ValueKind.Int32 => BitConverter.ToInt32(b).ToString(), ValueKind.Int64 => BitConverter.ToInt64(b).ToString(), ValueKind.Float => BitConverter.ToSingle(b).ToString("R"), ValueKind.Double => BitConverter.ToDouble(b).ToString("R"), _ => Hex.Format(b) };
    private static string FormatRegisters(byte[] b)
    {
        var names = new[] { "r15", "r14", "r13", "r12", "r11", "r10", "r9", "r8", "rdi", "rsi", "rbp", "rbx", "rdx", "rcx", "rax" };
        var output = new StringBuilder(); for (var i = 0; i < names.Length; i++) output.AppendLine($"{names[i],6} = 0x{Wire.U64(b, i * 8):X16}");
        foreach (var (name, offset) in new[] { ("rip", 136), ("rflags", 152), ("rsp", 160) }) output.AppendLine($"{name,6} = 0x{Wire.U64(b, offset):X16}");
        return output.ToString();
    }
}
