using System.Collections.ObjectModel;
using System.IO;
using System.Net;
using System.Text;
using System.Windows;
using System.Windows.Input;
using Studio.Core;
using Studio.Mock;

namespace Studio.Desktop;

public record RecentSession(DateTimeOffset Time, string TitleId, string Version, string Mode);
public record UserSettings(ConnectionSettings Connection, bool SetupComplete, List<RecentSession> Recent);

public sealed partial class StudioViewModel : Observable, IAsyncDisposable
{
    private readonly IUserDialogs dialogs;
    private readonly string dataDirectory;
    private readonly DiagnosticsLog log;
    private readonly DebuggerClient client = new();
    private MemorySession session;
    private MockDebuggerServer? simulator;
    private DebugSession? debug;
    private CancellationTokenSource? operation;
    private RollbackJournal? rollback;
    private PatchPlan? testedPlan;
    private PortResult? port;
    private byte[]? offlineDump;
    private GameIdentity? game;
    private bool hardwareConfirmed;
    private bool setupComplete;
    public bool SetupComplete => setupComplete;
    public ObservableCollection<RecentSession> Recent { get; } = [];
    public ObservableCollection<Trainer> Library { get; } = [];
    public ObservableCollection<Cheat> Cheats { get; } = [];
    public ObservableCollection<MemoryRegion> Regions { get; } = [];
    public ObservableCollection<ProcessEntry> Processes { get; } = [];
    public ObservableCollection<PortCandidate> Candidates { get; } = [];
    public ObservableCollection<PatchLine> Proposed { get; } = [];
    public ObservableCollection<DiagnosticEntry> Logs { get; } = [];
    public ObservableCollection<VerificationRecord> Verifications { get; } = [];
    public IReadOnlyList<ValueKind> ValueKinds { get; } = Enum.GetValues<ValueKind>();
    public IReadOnlyList<ScanComparison> Comparisons { get; } = Enum.GetValues<ScanComparison>();

    private string host = "192.168.8.188", debugPort = "744", payloadPort = "9090", ftpPort = "2121", interruptPort = "755", timeout = "8";
    public string Host { get => host; set => Set(ref host, value); }
    public string DebugPort { get => debugPort; set => Set(ref debugPort, value); }
    public string PayloadPort { get => payloadPort; set => Set(ref payloadPort, value); }
    public string FtpPort { get => ftpPort; set => Set(ref ftpPort, value); }
    public string InterruptPort { get => interruptPort; set => Set(ref interruptPort, value); }
    public string Timeout { get => timeout; set => Set(ref timeout, value); }
    private bool useSimulator, busy, advancedMode, writesEnabled, instructionPatch = true;
    public bool UseSimulator { get => useSimulator; set => Set(ref useSimulator, value); }
    public bool IsBusy { get => busy; private set { Set(ref busy, value); Changed(nameof(IsIdle)); } }
    public bool IsIdle => !IsBusy;
    public bool AdvancedMode { get => advancedMode; set => Set(ref advancedMode, value); }
    public bool WritesEnabled { get => writesEnabled; set => Set(ref writesEnabled, value); }
    public bool InstructionPatch { get => instructionPatch; set { if (Set(ref instructionPatch, value)) { ResetAnalysis(); ValidateEditor(); } } }
    private string status = "Ready. Connect your console or try the simulator.", errorMessage = "", analysisSummary = "Import a trainer to begin.", diagnostics = "Connect to run read-only diagnostics.", verificationStatus = "No cheat has been tested.", targetVersion = "01.05", profileName = "My game", profileTitle = "CUSA00000";
    public string Status { get => status; private set => Set(ref status, value); }
    public string ErrorMessage { get => errorMessage; private set => Set(ref errorMessage, value); }
    public string AnalysisSummary { get => analysisSummary; private set => Set(ref analysisSummary, value); }
    public string DiagnosticText { get => diagnostics; private set => Set(ref diagnostics, value); }
    public string VerificationStatus { get => verificationStatus; private set => Set(ref verificationStatus, value); }
    public string TargetVersion { get => targetVersion; set { if (Set(ref targetVersion, value)) ResetAnalysis(); } }
    public string ProfileName { get => profileName; set => Set(ref profileName, value); }
    public string ProfileTitle { get => profileTitle; set => Set(ref profileTitle, value); }
    private string scanBudget = "256";
    public string ScanBudget { get => scanBudget; set => Set(ref scanBudget, value); }
    private string moduleBase = "400000";
    public string ModuleBase { get => moduleBase; set => Set(ref moduleBase, value); }
    private int guidedStep, setupStep;
    public int GuidedStep { get => guidedStep; set => Set(ref guidedStep, value); }
    public int SetupStep { get => setupStep; private set { Set(ref setupStep, value); Changed(nameof(SetupHeading)); Changed(nameof(SetupDescription)); Changed(nameof(SetupNextText)); } }
    public string SetupHeading => SetupStep switch { 0 => "Welcome to Cheat Studio", 1 => "Choose your connection", _ => "Check your setup" };
    public string SetupDescription => SetupStep switch
    {
        0 => "Port a trainer with guided analysis, review every proposed change, and keep a way back. Everything stays on your PC. No account or API key is needed.",
        1 => "Enter your PS4's local IP. GoldHEN and ps4debug-NG must already be running. You can also use the local simulator while your console is unavailable.",
        _ => "Use Test connection to detect the game and exact version. You can finish setup offline and connect later. All connections start read-only."
    };
    public string SetupNextText => SetupStep == 2 ? "Finish setup" : "Continue";
    public event Action? SetupFinished;
    private Trainer? selectedTrainer;
    public Trainer? SelectedTrainer
    {
        get => selectedTrainer;
        set
        {
            if (!Set(ref selectedTrainer, value)) return;
            if (sourcePackage is not null && value is not null && (sourcePackage.TitleId != value.TitleId || sourcePackage.Version != value.Version || sourcePackage.Process != value.Process))
            { sourcePackage = null; EvidenceStatus = "Old context cleared because the selected source trainer changed identity/version."; }
            Cheats.Clear(); if (value is not null) foreach (var c in value.Cheats) Cheats.Add(c);
            SelectedCheat = Cheats.FirstOrDefault(); ResetAnalysis(); Changed(nameof(TrainerDetails));
        }
    }
    private Cheat? selectedCheat;
    public Cheat? SelectedCheat { get => selectedCheat; set { if (Set(ref selectedCheat, value)) { ResetAnalysis(); SourceSection = value?.Patches.FirstOrDefault()?.Section.ToString() ?? "0"; Changed(nameof(SourcePatches)); } } }
    private MemoryRegion? selectedRegion;
    public MemoryRegion? SelectedRegion { get => selectedRegion; set { if (Set(ref selectedRegion, value)) { ScanStart = value?.Start.ToString("X") ?? "0"; ScanLength = Math.Min(value?.Size ?? 0, 1048576).ToString(); } } }
    public IEnumerable<PatchLine> SourcePatches => SelectedCheat?.Patches ?? [];
    public string TrainerDetails => SelectedTrainer is null ? "No trainer imported." : $"{SelectedTrainer.Name} · {SelectedTrainer.TitleId} · source {SelectedTrainer.Version} · {SelectedTrainer.Process}\nImported offsets are unverified. SHN section numbers are old-version references; they are not reused as target addresses.";
    public bool Connected => client.IsConnected;
    public bool SimulationConnection { get; private set; }
    public string ConnectionLabel => offlineDump is not null ? "OFFLINE DUMP · no live testing" : !Connected ? "DISCONNECTED · read-only" : SimulationConnection ? "SIMULATOR · no hardware validation" : "PS4 CONNECTED";
    public string GameLabel => game is null ? "No game detected" : $"{game.TitleId} · {game.Version} · {game.ProcessName} · PID {game.Pid}";
    public string DebuggerLabel => client.Information is { } i ? $"{i.Brand} · firmware {i.FirmwareDisplay} · protocol {i.Protocol} · capability {i.Capability}" : "Debugger is loaded separately. No payload is injected.";

    public ICommand ConnectCommand { get; }
    public ICommand DisconnectCommand { get; }
    public ICommand SaveSettingsCommand { get; }
    public ICommand ImportCommand { get; }
    public ICommand DemoTrainerCommand { get; }
    public ICommand AnalyzeCommand { get; }
    public ICommand TestCommand { get; }
    public ICommand RollbackCommand { get; }
    public ICommand ConfirmWorkingCommand { get; }
    public ICommand ExportCommand { get; }
    public ICommand ExportSourceCommand { get; }
    public ICommand DiagnosticsCommand { get; }
    public ICommand ExportDiagnosticsCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand SetupNextCommand { get; }
    public ICommand SetupBackCommand { get; }
    public ICommand SaveProfileCommand { get; }
    public ICommand LoadProfileCommand { get; }
    public ICommand LoadDumpCommand { get; }
    public ICommand ClearDumpCommand { get; }
    public ICommand CompareVersionsCommand { get; }
    public ICommand RemoveTrainerCommand { get; }
    private string versionComparison = "Compare another trainer for the same title to inspect changes between versions.";
    public string VersionComparison { get => versionComparison; private set => Set(ref versionComparison, value); }

    public StudioViewModel(IUserDialogs dialogs, string dataDirectory)
    {
        this.dialogs = dialogs; this.dataDirectory = dataDirectory; session = new(client); log = new(Path.Combine(dataDirectory, "logs"));
        log.Added += e => Application.Current.Dispatcher.Invoke(() => { Logs.Add(e); if (Logs.Count > 5000) Logs.RemoveAt(0); });
        client.Log += m => log.Write("Protocol", m);
        AsyncCommand Command(Func<CancellationToken, Task> action, Func<bool>? condition = null) => new(() => Run(action), () => IsIdle && (condition?.Invoke() ?? true));
        ConnectCommand = Command(ConnectAsync);
        DisconnectCommand = Command(async _ => { await DisconnectAsync(); Status = "Disconnected. Read-only mode restored."; });
        SaveSettingsCommand = Command(async ct => { await SaveSettingsAsync(ct); Status = "Connection settings saved locally."; });
        ImportCommand = Command(async ct => { var path = dialogs.Open("Import an existing trainer", "GoldHEN trainers|*.json;*.shn"); if (path is not null) { ImportTrainer(TrainerCodec.Read(path)); await SaveLibraryAsync(ct); if (Connected || offlineDump is not null) await AnalyzeAsync(ct); } });
        DemoTrainerCommand = Command(async ct => { ImportTrainer(DemoTrainer()); await SaveLibraryAsync(ct); });
        AnalyzeCommand = Command(AnalyzeAsync, () => SelectedCheat is not null && (Connected || offlineDump is not null));
        TestCommand = Command(TestAsync, () => Connected && port?.CanTest == true && WritesEnabled && offlineDump is null);
        RollbackCommand = Command(RollbackAsync, () => Connected && rollback is not null && WritesEnabled);
        ConfirmWorkingCommand = Command(ConfirmWorkingAsync, () => testedPlan is not null && Connected && !SimulationConnection && !hardwareConfirmed);
        ExportCommand = Command(ExportAsync, () => port?.CanTest == true);
        ExportSourceCommand = Command(ExportSourceAsync, () => SelectedTrainer is not null);
        DiagnosticsCommand = Command(RunDiagnosticsAsync, () => Connected);
        ExportDiagnosticsCommand = Command(async ct => { var path = dialogs.Save("Save diagnostics (includes connection IP and logs)", "CheatStudio-diagnostics.json", "JSON report|*.json"); if (path is not null) { await File.WriteAllTextAsync(path, log.Report(Settings(), client.Information, game, SimulationConnection, DiagnosticText, offlineDump is not null), ct); Status = "Diagnostics saved. No memory dumps were included."; } });
        CancelCommand = new AsyncCommand(() => { operation?.Cancel(); return Task.CompletedTask; }, () => IsBusy);
        SetupNextCommand = Command(async ct => { if (SetupStep < 2) SetupStep++; else { await SaveSettingsAsync(ct, true); SetupFinished?.Invoke(); } });
        SetupBackCommand = new AsyncCommand(() => { if (SetupStep > 0) SetupStep--; return Task.CompletedTask; }, () => IsIdle && SetupStep > 0);
        SaveProfileCommand = Command(SaveProfileAsync);
        LoadProfileCommand = Command(LoadProfileAsync);
        LoadDumpCommand = Command(LoadDumpAsync);
        ClearDumpCommand = Command(async _ => { offlineDump = null; Regions.Clear(); ResetAnalysis(); if (Connected) await RefreshGameAsync(CancellationToken.None); RefreshLabels(); Status = "Offline dump closed."; });
        CompareVersionsCommand = Command(_ =>
        {
            var path = dialogs.Open("Compare a trainer from another version", "GoldHEN trainer|*.json;*.shn"); if (path is null) return Task.CompletedTask;
            var other = TrainerCodec.Read(path); var source = SelectedTrainer!;
            if (other.TitleId != source.TitleId || other.Process != source.Process) throw new InvalidDataException("Choose the same title ID and process for a meaningful comparison.");
            var details = new List<string> { $"{source.Version} → {other.Version}: {source.Cheats.Count} → {other.Cheats.Count} cheats." };
            foreach (var c in source.Cheats)
            {
                var next = other.Cheats.FirstOrDefault(n => n.Name == c.Name);
                if (next is null) { details.Add($"{c.Name}: no cheat with the same name in the other version."); continue; }
                var oldLines = c.Patches.Select(p => $"{p.Section}:{p.Offset:X}:{p.OriginalHex}:{p.ReplacementHex}");
                var newLines = next.Patches.Select(p => $"{p.Section}:{p.Offset:X}:{p.OriginalHex}:{p.ReplacementHex}");
                details.Add($"{c.Name}: {(oldLines.SequenceEqual(newLines) ? "same offsets and bytes" : "offsets or bytes differ — reanalysis required")}.");
            }
            VersionComparison = string.Join(Environment.NewLine, details); Status = "Trainer comparison completed; compatibility was not assumed."; return Task.CompletedTask;
        }, () => SelectedTrainer is not null);
        RemoveTrainerCommand = Command(async ct =>
        {
            if (SelectedTrainer is not null) { Library.Remove(SelectedTrainer); SelectedTrainer = Library.FirstOrDefault(); await SaveLibraryAsync(ct); Status = "Trainer removed from the local library. Original file was preserved."; }
        }, () => SelectedTrainer is not null);
        InitializeAdvancedCommands(Command);
        InitializeEditorCommands(Command);
        InitializeEvidenceCommands(Command);
    }
    public async Task InitializeAsync()
    {
        try
        {
            var path = Path.Combine(dataDirectory, "settings.json");
            if (File.Exists(path))
            {
                var saved = await LocalStore.LoadAsync<UserSettings>(path); var c = saved.Connection; c.Validate();
                Host = c.Host; DebugPort = c.DebugPort.ToString(); PayloadPort = c.PayloadPort.ToString(); FtpPort = c.FtpPort.ToString(); InterruptPort = c.InterruptPort.ToString(); Timeout = c.TimeoutSeconds.ToString();
                setupComplete = saved.SetupComplete; foreach (var r in saved.Recent.Take(20)) Recent.Add(r);
            }
            path = Path.Combine(dataDirectory, "library.json");
            if (File.Exists(path)) foreach (var t in await LocalStore.LoadAsync<List<Trainer>>(path)) Library.Add(TrainerCodec.Validate(t));
            path = Path.Combine(dataDirectory, "verification-history.json");
            if (File.Exists(path)) foreach (var record in (await LocalStore.LoadAsync<List<VerificationRecord>>(path)).TakeLast(5000)) Verifications.Add(record);
            SelectedTrainer = Library.FirstOrDefault();
        }
        catch (Exception e) { ErrorMessage = "Local settings could not be loaded: " + e.Message; log.Write("Startup", ErrorMessage, "Warning"); }
    }
    private async Task Run(Func<CancellationToken, Task> action)
    {
        if (IsBusy) return;
        IsBusy = true; ErrorMessage = ""; operation = new();
        try { await action(operation.Token); }
        catch (OperationCanceledException) { Status = "Operation cancelled. Reconnect if a network exchange was interrupted."; log.Write("Cancellation", Status); }
        catch (Exception e) { ErrorMessage = e.Message; Status = "Action stopped. See the message below or export diagnostics."; log.Write("Action", e.ToString(), "Error"); }
        finally { operation.Dispose(); operation = null; IsBusy = false; RefreshLabels(); }
    }
    private ConnectionSettings Settings() => new() { Host = Host.Trim(), DebugPort = int.Parse(DebugPort), PayloadPort = int.Parse(PayloadPort), FtpPort = int.Parse(FtpPort), InterruptPort = int.Parse(InterruptPort), TimeoutSeconds = int.Parse(Timeout) };
    private async Task SaveSettingsAsync(CancellationToken ct, bool finish = false)
    {
        var s = Settings(); s.Validate(); if (finish) setupComplete = true;
        await LocalStore.SaveAsync(Path.Combine(dataDirectory, "settings.json"), new UserSettings(s, setupComplete, Recent.Take(20).ToList()), ct);
    }
    private async Task ConnectAsync(CancellationToken ct)
    {
        await DisconnectAsync(); offlineDump = null;
        var settings = Settings(); settings.Validate();
        if (UseSimulator)
        {
            simulator = new(); simulator.Start(); settings = settings with { Host = "127.0.0.1", DebugPort = simulator.Port };
        }
        Status = "Connecting and detecting the running game…";
        await client.ConnectAsync(settings, ct); session = new(client);
        SimulationConnection = client.Information!.Brand.Contains("[SIMULATOR]", StringComparison.Ordinal);
        await RefreshGameAsync(ct); WritesEnabled = false;
        await RunDiagnosticsAsync(ct);
        Recent.Insert(0, new(DateTimeOffset.UtcNow, game!.TitleId, game.Version, SimulationConnection ? "Simulator" : "Live PS4"));
        while (Recent.Count > 20) Recent.RemoveAt(Recent.Count - 1);
        await SaveSettingsAsync(ct); GuidedStep = 1; Status = SimulationConnection ? "Simulator connected. Import the demo trainer to try the guided workflow." : "Console connected. Select a trainer for the detected game.";
        RefreshLabels();
    }
    private async Task RefreshGameAsync(CancellationToken ct)
    {
        ResetAnalysis(); game = await client.GetGameAsync(ct); Processes.Clear();
        foreach (var p in await client.GetProcessesAsync(ct)) Processes.Add(p);
        Regions.Clear();
        if (game.Pid > 0) foreach (var r in await client.GetMapsAsync(game.Pid, ct)) Regions.Add(r);
        SelectedRegion = Regions.FirstOrDefault(r => r.Executable && r.Readable) ?? Regions.FirstOrDefault();
        if (Regions.Count > 0) ModuleBase = Regions.Min(r => r.Start).ToString("X");
        TargetVersion = game.Version; ProfileTitle = game.TitleId;
        if (!game.IsGame) Status = "No supported foreground game detected. Start a game and reconnect.";
        RefreshLabels();
    }
    private async Task DisconnectAsync()
    {
        if (debug is not null) { await debug.DisposeAsync(); debug = null; }
        client.Disconnect(); if (simulator is not null) { await simulator.DisposeAsync(); simulator = null; }
        game = null; SimulationConnection = false; WritesEnabled = false; rollback = null; scan = null; ResetAnalysis(); Regions.Clear(); Processes.Clear(); RefreshLabels();
    }
    public void ImportTrainer(Trainer trainer)
    {
        TrainerCodec.Validate(trainer); Library.Add(trainer); SelectedTrainer = trainer; ProfileName = trainer.Name; GuidedStep = 1;
        AnalysisSummary = "Ready to search. We will locate original bytes in the target memory; old offsets will not be reused."; Status = "Trainer imported. Select the cheat you want to port.";
        log.Write("Import", $"{trainer.TitleId} source {trainer.Version}, {trainer.Cheats.Count} cheats.");
    }
    public static Trainer DemoTrainer() => new("Hollow Knight: Silksong — SIMULATED reference", "CUSA39420", "01.04", "eboot.bin", [new("Infinite Health — synthetic NOP example", "Demonstrates relocation only. This NOP is not a working Silksong cheat. Old instruction reference: section 14, offset 1B6BFED; old cave 4783800. No real 01.05 patch is supplied.", [new("health", 14, 0x1B6BFED, Hex.Bytes("8943604883C408"), Hex.Bytes("90909090909090"), [])])]);
    private async Task SaveLibraryAsync(CancellationToken ct) => await LocalStore.SaveAsync(Path.Combine(dataDirectory, "library.json"), Library.ToList(), ct);
    private void ResetAnalysis()
    {
        port = null; Candidates.Clear(); Proposed.Clear(); testedPlan = null; hardwareConfirmed = false;
        lastSegments = null; reviewedCaveTokens.Clear(); explicitCandidates.Clear(); SelectedPortCandidate = null; Guidance = "";
        VerificationStatus = "No cheat has been tested."; AnalysisSummary = "Analyze the selected trainer against the target game or local dump.";
    }
    private void RefreshLabels() { Changed(nameof(Connected)); Changed(nameof(ConnectionLabel)); Changed(nameof(GameLabel)); Changed(nameof(DebuggerLabel)); CommandManager.InvalidateRequerySuggested(); }
    private async Task AnalyzeAsync(CancellationToken ct)
    {
        if (SelectedTrainer is null || SelectedCheat is null) throw new InvalidOperationException("Select a trainer and a cheat.");
        ResetAnalysis();
        if (offlineDump is not null)
        {
            if (SelectedRegion is null) throw new InvalidOperationException("Load a dump and specify its base address.");
            lastSegments = [(SelectedRegion, offlineDump)];
        }
        else
        {
            var current = await client.GetGameAsync(ct);
            if (!current.IsGame || !current.HasKnownVersion || current.TitleId != SelectedTrainer.TitleId || current.ProcessName != SelectedTrainer.Process || current.Version != TargetVersion)
                throw new InvalidDataException("Trainer CUSA/process or known XX.YY target version does not match the running game. No patch was attempted.");
            game = current; lastSegments = await ReadTargetSegmentsAsync(current, SelectedCheat, ct);
        }
        await AnalyzeCapturedAsync(ct);
    }
    private PatchPlan CreatePlan()
    {
        if (port?.CanTest != true || game is null || SelectedCheat is null) throw new InvalidOperationException("Analyze a cheat first.");
        var lines = port.ProposedPatches.Select(p =>
        {
            var map = Regions.Single(r => r.Index == p.Section);
            return new ResolvedPatch(p.Id, checked(map.Start + p.Offset), p.Original.ToArray(), p.Replacement.ToArray(), p.Dependencies, p.IsCodeCave, p.CaveUseApproved);
        }).ToList();
        return new(client.ConnectionId, game, SelectedCheat.Name, lines, InstructionPatch);
    }
    private PatchService Patcher() => new(session, Path.Combine(dataDirectory, "rollback"));
    private async Task TestAsync(CancellationToken ct)
    {
        if (!WritesEnabled || offlineDump is not null) throw new InvalidOperationException("Enable explicit write testing on a live connection first.");
        var plan = CreatePlan(); var service = Patcher(); var review = await service.ReviewAsync(plan, ct);
        if (!dialogs.ConfirmPatch(review)) { Status = "Patch test cancelled; no bytes written."; return; }
        rollback = await service.ApplyAsync(review, new(review.Digest, WritesEnabled, true), ct); testedPlan = plan; hardwareConfirmed = false;
        VerificationStatus = SimulationConnection ? "Byte-write/readback passed in simulation. Not hardware verified." : "Byte-write/readback passed. Test the effect in gameplay before confirming it works.";
        await RecordVerification("Byte-write/readback passed", VerificationStatus, ct); GuidedStep = 3; Status = VerificationStatus;
    }
    private async Task RollbackAsync(CancellationToken ct)
    {
        if (rollback is null || !WritesEnabled) throw new InvalidOperationException("There is no active rollback journal, or writes are disabled.");
        var service = Patcher(); var review = await service.ReviewRollbackAsync(rollback, ct);
        if (!dialogs.ConfirmPatch(review)) return;
        await service.ApplyAsync(review, new(review.Digest, WritesEnabled, true), ct);
        rollback = null; testedPlan = null; hardwareConfirmed = false;
        VerificationStatus = "Original bytes restored and read back successfully."; Status = VerificationStatus;
        await RecordVerification("Original bytes restored", VerificationStatus, ct);
    }
    private async Task ConfirmWorkingAsync(CancellationToken ct)
    {
        if (SimulationConnection || testedPlan is null || !Connected) throw new InvalidOperationException("Simulation cannot produce a hardware verification.");
        if (!testedPlan.Game.SameGame(await client.GetGameAsync(ct)) || testedPlan.ConnectionId != client.ConnectionId) throw new InvalidDataException("Game session changed; test again.");
        foreach (var p in testedPlan.Patches) if (!(await client.ReadAsync(game!.Pid, p.Address, p.Replacement.Length, ct)).SequenceEqual(p.Replacement)) throw new InvalidDataException("The tested patch is no longer active; test again.");
        if (!dialogs.Confirm("Confirm actual gameplay test", "Have you tested the intended cheat effect on your real PS4, with the displayed game/version, without unwanted behavior? This records your observation, not independent certification.")) return;
        hardwareConfirmed = true; VerificationStatus = "Confirmed working on real hardware by the user for this exact game/version and patch.";
        await RecordVerification("User-confirmed real hardware gameplay", VerificationStatus, ct); GuidedStep = 4; Status = VerificationStatus;
    }
    private async Task RecordVerification(string outcome, string notes, CancellationToken ct)
    {
        var r = new VerificationRecord(DateTimeOffset.UtcNow, game?.TitleId ?? ProfileTitle, game?.Version ?? TargetVersion, game?.ProcessName ?? "eboot.bin", SelectedCheat?.Name ?? "Manual patch", outcome, notes);
        Verifications.Add(r); await LocalStore.SaveAsync(Path.Combine(dataDirectory, "verification-history.json"), Verifications.ToList(), ct); log.Write("Verification", notes);
    }
    private Trainer ExportTrainer()
    {
        if (SelectedTrainer is null || SelectedCheat is null || port?.CanTest != true) throw new InvalidOperationException("No exportable candidate trainer.");
        // GoldHEN JSON offsets are relative to the lowest mapped main-module address, not a section index.
        var baseAddress = Hex.Address(ModuleBase);
        if (offlineDump is null && !Regions.Any(r => r.Start == baseAddress)) throw new InvalidDataException("The live main-module base must match a mapped region start. Verify the eboot.bin mapping.");
        var lines = Proposed.Select(p =>
        {
            var region = Regions.Single(r => r.Index == p.Section); var address = checked(region.Start + p.Offset);
            if (address < baseAddress) throw new InvalidDataException("A patch is below the chosen main-module base. Correct the export mapping.");
            return p with { Section = 0, Offset = address - baseAddress, Absolute = false };
        }).ToList();
        return SelectedTrainer with { Version = TargetVersion, Cheats = [new(SelectedCheat.Name, SelectedCheat.Description + (hardwareConfirmed ? " [User-confirmed hardware test recorded locally]" : " [UNVERIFIED CANDIDATE: requires gameplay validation]"), lines)] };
    }
    private async Task ExportAsync(CancellationToken ct)
    {
        var trainer = ExportTrainer(); var suffix = hardwareConfirmed ? "" : ".candidate";
        var path = dialogs.Save("Export trainer — " + (hardwareConfirmed ? "user-confirmed hardware test" : "UNVERIFIED candidate"), $"{trainer.TitleId}_{trainer.Version}{suffix}.json", "GoldHEN JSON|*.json|GoldHEN SHN|*.shn");
        if (path is null) return;
        if (!SimulationConnection && !dialogs.Confirm("Confirm GoldHEN module mapping", $"Export uses 0x{Hex.Address(ModuleBase):X} as the main-module base. Confirm this against the original eboot.bin process mapping, especially for a partial offline dump. You can edit it in the Export step. Section indices are not stable identifiers. Export this mapping?")) return;
        await File.WriteAllTextAsync(path, Path.GetExtension(path).Equals(".shn", StringComparison.OrdinalIgnoreCase) ? TrainerCodec.Shn(trainer) : TrainerCodec.Json(trainer), new UTF8Encoding(false), ct);
        await LocalStore.SaveAsync(path + ".verification.json", new { simulation = SimulationConnection, offline = offlineDump is not null, hardwareConfirmedByUser = hardwareConfirmed, candidates = Candidates.ToList(), records = Verifications.ToList(), moduleBase = $"0x{Hex.Address(ModuleBase):X}", warning = "Generated output is not automatically verified. Confirm main-module base and section interpretation on hardware before installation." }, ct);
        Status = "Trainer and verification sidecar exported. " + (hardwareConfirmed ? "Use the titleid_version filename in GoldHEN's cheats folder." : "Candidate filename intentionally includes .candidate; do not install until validated."); log.Write("Export", Status);
    }
    private async Task ExportSourceAsync(CancellationToken ct)
    {
        var t = SelectedTrainer ?? throw new InvalidOperationException("Select a trainer.");
        var path = dialogs.Save("Export imported source trainer (unverified)", $"{t.TitleId}_{t.Version}.shn", "GoldHEN SHN|*.shn|GoldHEN JSON|*.json");
        if (path is not null) await File.WriteAllTextAsync(path, Path.GetExtension(path).Equals(".json", StringComparison.OrdinalIgnoreCase) ? TrainerCodec.Json(t) : TrainerCodec.Shn(t), new UTF8Encoding(false), ct);
    }
    private async Task RunDiagnosticsAsync(CancellationToken ct)
    {
        var lines = new List<string>();
        if (!Connected) throw new InvalidOperationException("Connect before running diagnostics.");
        await client.PingAsync(ct); lines.Add("✓ Debugger responds to the documented keepalive.");
        var g = await client.GetGameAsync(ct); lines.Add(g.IsGame ? $"✓ Running game: {g.TitleId}, exact version {g.Version}." : "! No CUSA eboot.bin game is in the foreground.");
        var maps = g.Pid > 0 ? await client.GetMapsAsync(g.Pid, ct) : [];
        lines.Add($"✓ {maps.Count} memory regions returned; {maps.Count(r => r.Readable)} are readable.");
        var first = maps.FirstOrDefault(r => r.Readable);
        if (first is not null) { await client.ReadAsync(g.Pid, first.Start, (int)Math.Min(first.Size, 16), ct); lines.Add("✓ Representative memory read completed."); }
        lines.Add(client.Information!.VerifiedWrites ? "✓ Verified-write capability 1.1 is advertised. No write was performed by diagnostics." : "! Verified writes are unavailable; patch testing is blocked.");
        lines.Add("• Console scan authentication is not used. Searches read memory and run locally.");
        lines.Add(SimulationConnection ? "• SIMULATION: no firmware, gameplay or real-console compatibility is validated." : "• Live transport checks do not verify that a cheat works. Gameplay testing is still required.");
        DiagnosticText = string.Join(Environment.NewLine + Environment.NewLine, lines); log.Write("Diagnostics", DiagnosticText); Status = "Read-only diagnostics completed.";
    }
    private async Task SaveProfileAsync(CancellationToken ct)
    {
        var profile = new GameProfile { Name = ProfileName, TitleId = ProfileTitle, Version = TargetVersion, Sections = Regions.ToList(), Trainers = Library.Where(t => t.TitleId == ProfileTitle).ToList(), Candidates = Candidates.ToList(), Verifications = Verifications.ToList(), ScanSessions = scan is null ? [] : [$"{scan.Created:O}: {scan.Hits.Count} hits, {scan.Kind}"] };
        if (!System.Text.RegularExpressions.Regex.IsMatch(profile.TitleId, @"^CUSA\d{5}$") || !System.Text.RegularExpressions.Regex.IsMatch(profile.Version, @"^\d{2}\.\d{2}$")) throw new InvalidDataException("Profile requires a CUSA12345 title ID and XX.YY version.");
        var path = dialogs.Save("Save a reusable game profile", profile.TitleId + ".studio-profile.json", "Studio profile|*.json");
        if (path is not null) { await LocalStore.SaveAsync(path, profile, ct); Status = "Game profile saved locally. No code is needed to create another profile."; }
    }
    private async Task LoadProfileAsync(CancellationToken ct)
    {
        var path = dialogs.Open("Open a game profile", "Studio profile|*.json"); if (path is null) return;
        var p = await LocalStore.LoadAsync<GameProfile>(path, ct); ProfileName = p.Name; ProfileTitle = p.TitleId;
        foreach (var t in p.Trainers) ImportTrainer(TrainerCodec.Validate(t));
        // Historical candidates/verification are never promoted into an active plan.
        ResetAnalysis(); await SaveLibraryAsync(ct); Status = $"Loaded {p.Name}. Reconnect and reanalyze to create an active plan; saved addresses were not trusted.";
    }
    private async Task LoadDumpAsync(CancellationToken ct)
    {
        var path = dialogs.Open("Analyze a local raw memory dump (maximum 64 MiB)", "Raw memory|*.bin;*.dump|All files|*.*"); if (path is null) return;
        var length = new FileInfo(path).Length; if (length is < 1 or > MemoryScanner.MaximumBytes) throw new InvalidDataException("Select a 1-byte to 64-MiB raw dump.");
        var start = Hex.Address(DumpBase); if (start > ulong.MaxValue - (ulong)length) throw new InvalidDataException("Dump base overflows the address space.");
        await DisconnectAsync(); offlineDump = await File.ReadAllBytesAsync(path, ct); Regions.Clear(); Regions.Add(new(0, start, start + (ulong)length, 0, 5, "Offline dump")); SelectedRegion = Regions[0]; ModuleBase = start.ToString("X");
        ResetAnalysis(); RefreshLabels(); Status = "Local dump loaded. Set the target version, select a trainer, then analyze. Live testing is disabled."; GuidedStep = 2;
    }
    public async ValueTask DisposeAsync() { operation?.Cancel(); await DisconnectAsync(); await client.DisposeAsync(); }
}
