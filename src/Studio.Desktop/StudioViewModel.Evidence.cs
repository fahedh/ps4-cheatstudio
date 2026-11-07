using System.IO;
using System.Windows.Input;
using Studio.Core;

namespace Studio.Desktop;

public sealed partial class StudioViewModel
{
    private List<(MemoryRegion Region, byte[] Bytes)>? lastSegments;
    private EvidencePackage? sourcePackage;
    private readonly HashSet<string> reviewedCaveTokens = [];
    private readonly Dictionary<string, ulong> explicitCandidates = [];
    private string guidance = "", evidenceStatus = "No old-version context loaded.", sourceDumpBase = "0", sourceSection = "14", sourceSectionBase = "0", sourceMappings = "", functionStart = "", functionLength = "0";
    private PortCandidate? selectedPortCandidate;
    public string Guidance { get => guidance; private set => Set(ref guidance, value); }
    public string EvidenceStatus { get => evidenceStatus; private set => Set(ref evidenceStatus, value); }
    public string SourceDumpBase { get => sourceDumpBase; set => Set(ref sourceDumpBase, value); }
    public string SourceSection { get => sourceSection; set => Set(ref sourceSection, value); }
    public string SourceSectionBase { get => sourceSectionBase; set => Set(ref sourceSectionBase, value); }
    public string SourceMappings { get => sourceMappings; set => Set(ref sourceMappings, value); }
    public string FunctionStart { get => functionStart; set => Set(ref functionStart, value); }
    public string FunctionLength { get => functionLength; set => Set(ref functionLength, value); }
    public PortCandidate? SelectedPortCandidate { get => selectedPortCandidate; set { Set(ref selectedPortCandidate, value); Changed(nameof(CandidateDetails)); } }
    public string CandidateDetails => SelectedPortCandidate is null ? "Select a candidate to see its evidence and next action." :
        $"{SelectedPortCandidate.PatchId} at {SelectedPortCandidate.AddressHex}\n{SelectedPortCandidate.Explanation}\nActual target bytes: {Hex.Format(SelectedPortCandidate.Expected)}";
    public ICommand LoadEvidenceCommand { get; private set; } = null!;
    public ICommand CaptureEvidenceCommand { get; private set; } = null!;
    public ICommand ClearEvidenceCommand { get; private set; } = null!;
    public ICommand InspectCandidateCommand { get; private set; } = null!;
    public ICommand ChooseCandidateCommand { get; private set; } = null!;
    public ICommand ReviewCaveCommand { get; private set; } = null!;
    public ICommand OpenMemoryToolsCommand { get; private set; } = null!;

    private void InitializeEvidenceCommands(Func<Func<CancellationToken, Task>, Func<bool>?, AsyncCommand> command)
    {
        LoadEvidenceCommand = command(LoadEvidenceAsync, () => SelectedTrainer is not null && SelectedCheat is not null);
        CaptureEvidenceCommand = command(CaptureEvidenceAsync, () => Connected && SelectedTrainer is not null && SelectedCheat is not null);
        ClearEvidenceCommand = command(_ => { sourcePackage = null; EvidenceStatus = "Old-version context cleared."; ResetAnalysis(); return Task.CompletedTask; }, null);
        InspectCandidateCommand = command(_ => { InspectCandidate(); return Task.CompletedTask; }, () => SelectedPortCandidate is not null && SelectedTrainer is not null && SelectedCheat is not null);
        ChooseCandidateCommand = command(async ct =>
        {
            var c = SelectedPortCandidate!;
            if (!dialogs.Confirm("Choose this reviewed candidate", $"Use {c.AddressHex} for {c.PatchId}?\n{c.Explanation}\nYou must judge which function is relevant. This records a selection, not a successful cheat test.")) return;
            explicitCandidates[c.PatchId] = c.Address; await AnalyzeCapturedAsync(ct);
        }, () => SelectedPortCandidate is { ExactBytes: true, CodeCave: false } && lastSegments is not null);
        ReviewCaveCommand = command(async ct =>
        {
            var c = SelectedPortCandidate!;
            if (c.Cave is not { CanReview: true } cave) throw new InvalidDataException("This candidate conflicts with existing code or mapping and cannot be approved as a cave.");
            if (!dialogs.Confirm("Manual code-cave confirmation", $"{c.PatchId} at {c.AddressHex}\n{cave.Explanation}\nHave you checked that this exact range is unused for the game's lifetime, starts/ends at suitable boundaries, has no relevant incoming references, and has correct dependencies and return jumps? Padding and a matching byte sequence alone do not establish safety.")) return;
            reviewedCaveTokens.Add(cave.ReviewToken); explicitCandidates[c.PatchId] = c.Address; await AnalyzeCapturedAsync(ct);
        }, () => SelectedPortCandidate?.Cave?.CanReview == true && lastSegments is not null);
        OpenMemoryToolsCommand = command(_ =>
        {
            AdvancedMode = true; WorkspaceIndex = 4;
            if (SelectedPortCandidate is { } c) ViewerAddress = c.Address.ToString("X");
            Status = "Advanced tools opened. Use Code & patches to inspect bytes, or Watchpoints to identify a live writer."; return Task.CompletedTask;
        }, null);
    }
    private PortingOptions EvidenceOptions()
    {
        if (sourcePackage is not null && SelectedTrainer is { } trainer &&
            (sourcePackage.TitleId != trainer.TitleId || sourcePackage.Version != trainer.Version || sourcePackage.Process != trainer.Process))
            throw new InvalidDataException("The loaded old-version context belongs to another trainer identity/version. Clear it or load matching evidence.");
        return new() { Sources = sourcePackage?.Sources ?? [], SourceSectionBases = ParseBases(SourceMappings), ReviewedCaves = new(reviewedCaveTokens), SelectedCandidates = new(explicitCandidates) };
    }
    private async Task LoadEvidenceAsync(CancellationToken ct)
    {
        var path = dialogs.Open("Load old-version context package or raw dump", "Context/dump|*.json;*.bin;*.dump|All files|*.*"); if (path is null) return;
        EvidencePackage package;
        if (Path.GetExtension(path).Equals(".json", StringComparison.OrdinalIgnoreCase)) package = await LocalStore.LoadAsync<EvidencePackage>(path, ct);
        else
        {
            var length = new FileInfo(path).Length; if (length is < 1 or > MemoryScanner.MaximumBytes) throw new InvalidDataException("Old-version dumps are limited to 64 MiB.");
            package = new(SelectedTrainer!.TitleId, SelectedTrainer.Version, SelectedTrainer.Process,
                [new(int.Parse(SourceSection), Hex.Address(SourceSectionBase), Hex.Address(SourceDumpBase), await File.ReadAllBytesAsync(path, ct),
                    string.IsNullOrWhiteSpace(FunctionStart) ? null : Hex.Address(FunctionStart), int.Parse(FunctionLength))]);
        }
        ValidateEvidencePackage(package); sourcePackage = package; EvidenceStatus = $"Loaded {package.Sources.Count} old-version context range(s) for {package.TitleId} {package.Version}. Bytes were checked against the selected trainer.";
        ResetAnalysis();
        if (Connected || offlineDump is not null) await AnalyzeAsync(ct);
    }
    private void ValidateEvidencePackage(EvidencePackage package)
    {
        if (SelectedTrainer is null || package.TitleId != SelectedTrainer.TitleId || package.Version != SelectedTrainer.Version || package.Process != SelectedTrainer.Process || package.Sources.Count is < 1 or > 100)
            throw new InvalidDataException("Old evidence must match the imported trainer's exact CUSA/version/process and contain 1–100 bounded ranges.");
        var matched = false;
        foreach (var s in package.Sources)
        {
            if (s.Section < 0 || s.Bytes.Length is < 1 or > MemoryScanner.MaximumBytes || s.Start > ulong.MaxValue - (ulong)s.Bytes.Length || s.FunctionLength is < 0 or > 4096 || s.FunctionStart is ulong start && !s.Contains(start, s.FunctionLength))
                throw new InvalidDataException("Invalid old context range or function bounds. Function evidence must be a complete 1–4096-byte range inside the dump.");
            foreach (var p in SelectedCheat!.Patches.Where(p => p.Section == s.Section && !p.Absolute))
            {
                var address = s.PatchAddress(p); if (!s.Contains(address, p.Original.Length)) continue;
                if (!s.Bytes.AsSpan((int)(address - s.Start), p.Original.Length).SequenceEqual(p.Original)) throw new InvalidDataException($"Old dump bytes do not match {p.Id} at 0x{address:X}. Check the original version and section/base metadata.");
                matched = true;
            }
        }
        if (!matched) throw new InvalidDataException("No selected old patch lies inside this evidence. Check the dump's start address and original section base.");
    }
    private async Task CaptureEvidenceAsync(CancellationToken ct)
    {
        var trainer = SelectedTrainer!; var current = await client.GetGameAsync(ct);
        if (!current.IsGame || current.TitleId != trainer.TitleId || current.Version != trainer.Version || current.ProcessName != trainer.Process)
            throw new InvalidDataException($"To capture original context, run {trainer.TitleId} version {trainer.Version}. The current target version cannot supply old instruction evidence. You can load a previously captured old dump instead.");
        var maps = await client.GetMapsAsync(current.Pid, ct); var explicitBases = ParseBases(SourceMappings); var sources = new List<SourceEvidence>();
        foreach (var p in SelectedCheat!.Patches)
        {
            if (p.Absolute) throw new InvalidDataException("Map absolute SHN addressing manually before old-context capture.");
            if (InstructionPatch && PortingEngine.IsCave(p)) continue; // Old padding is not function/instruction evidence.
            var originalBase = explicitBases.TryGetValue(p.Section, out var mappedBase) ? mappedBase : maps.SingleOrDefault(m => m.Index == p.Section)?.Start;
            if (originalBase is null) throw new InvalidDataException($"Old section {p.Section} has no mapping. Supply its original section=base mapping; indices can change between runs.");
            var address = checked(originalBase.Value + p.Offset);
            var map = maps.SingleOrDefault(m => m.Readable && m.Contains(address, p.Original.Length));
            if (map is null || !map.Readable) throw new InvalidDataException($"Old section {p.Section} has no readable mapping. Supply its original section=base mapping; indices can change between runs.");
            if (!map.Contains(address, p.Original.Length)) throw new InvalidDataException($"{p.Id} lies outside the chosen old section; correct the old section base before capture.");
            var at = address - Math.Min(64UL, address - map.Start); var end = Math.Min(map.End, checked(address + (ulong)p.Original.Length + 96));
            ulong? function = string.IsNullOrWhiteSpace(FunctionStart) ? null : Hex.Address(FunctionStart); var functionBytes = int.Parse(FunctionLength);
            if (function is ulong start)
            {
                if (functionBytes is < 1 or > 4096 || !map.Contains(start, functionBytes)) throw new InvalidDataException("Explicit function bounds must be 1–4096 bytes within the same readable old map.");
                if (address >= start && address + (ulong)p.Original.Length <= start + (ulong)functionBytes)
                { at = Math.Min(at, start); end = Math.Max(end, checked(start + (ulong)functionBytes)); }
                else { function = null; functionBytes = 0; }
            }
            if (end - at > (ulong)MemoryScanner.MaximumBytes) throw new InvalidDataException("Old function/patch ranges are too far apart; capture smaller evidence.");
            var bytes = await new MemoryScanner(session).SnapshotAsync(current.Pid, at, checked((int)(end - at)), ct: ct);
            sources.Add(new(p.Section, originalBase.Value, at, bytes, function, functionBytes));
        }
        if (sources.Count == 0) throw new InvalidDataException("This cheat contains only possible padding/cave lines. Capture a hook's instructions or export an independently mapped old function range; padding alone is not old-function evidence.");
        var package = new EvidencePackage(trainer.TitleId, trainer.Version, trainer.Process, sources); ValidateEvidencePackage(package);
        if (!current.SameGame(await client.GetGameAsync(ct))) throw new InvalidDataException("The game changed during capture. Evidence was discarded.");
        sourcePackage = package; ResetAnalysis(); EvidenceStatus = "Old-version context captured with original-byte checks. Reconnect to the target version, then analyze.";
        var path = dialogs.Save("Save original context locally", trainer.TitleId + "_" + trainer.Version + ".studio-evidence.json", "Context package|*.json");
        if (path is not null) await LocalStore.SaveAsync(path, package, ct);
        Status = EvidenceStatus;
    }
    private void InspectCandidate()
    {
        var c = SelectedPortCandidate!; var source = SelectedCheat!.Patches.Single(p => p.Id == c.PatchId); var region = Regions.Single(r => r.Index == c.Section);
        var patch = source with { Section = c.Section, Offset = c.Address - region.Start, Original = c.Expected.ToArray(), Dependencies = [], Absolute = false, Ps5Offset = null, IsCodeCave = c.CodeCave, CaveUseApproved = false };
        var cheat = new Cheat(SelectedCheat.Name + " — manual target draft", c.Explanation + " Review replacement instructions; old replacement bytes are shown, not adapted or verified.", [patch]);
        var trainer = SelectedTrainer! with { Version = TargetVersion, Cheats = [cheat] };
        LoadEditor(trainer, cheat); EditorOnlyCheat = true;
        EditorSectionBases = string.Join(';', Regions.Select(r => $"{r.Index}={r.Start:X}"));
        Status = "Candidate opened as an unverified editor draft. Compare actual original bytes with the old replacement, fix length/layout/dependencies, then save and analyze or explicitly review testing.";
    }
    private async Task AnalyzeCapturedAsync(CancellationToken ct)
    {
        if (lastSegments is null || SelectedCheat is null) throw new InvalidOperationException("Analyze the target before reviewing a candidate.");
        var options = EvidenceOptions(); var oldSelection = SelectedPortCandidate;
        port = await Task.Run(() => PortingEngine.AnalyzeSegments(SelectedCheat, lastSegments, InstructionPatch, ct, options), ct);
        Candidates.Clear(); foreach (var c in port.Candidates) Candidates.Add(c);
        Proposed.Clear(); foreach (var p in port.ProposedPatches) Proposed.Add(p);
        SelectedPortCandidate = Candidates.FirstOrDefault(c => oldSelection is not null && c.PatchId == oldSelection.PatchId && c.Address == oldSelection.Address) ?? Candidates.FirstOrDefault();
        AnalysisSummary = port.Summary + (SimulationConnection ? " Results are simulated, not real PS4 validation." : "");
        Guidance = string.Join(Environment.NewLine + Environment.NewLine, port.NextSteps);
        testedPlan = null; hardwareConfirmed = false; VerificationStatus = "Generated/reviewed candidate; not gameplay verified.";
        log.Write("Port analysis", AnalysisSummary); GuidedStep = 2; Progress = 100;
        Status = port.CanTest ? "Reviewed candidates are ready for the separate exact-byte test confirmation." : "Analysis finished. Follow the suggested steps to collect evidence or review the candidates.";
    }
    private async Task<List<(MemoryRegion Region, byte[] Bytes)>> ReadTargetSegmentsAsync(GameIdentity current, Cheat cheat, CancellationToken ct)
    {
        var allMaps = await client.GetMapsAsync(current.Pid, ct);
        var maps = allMaps.Where(r => r.Readable && (!InstructionPatch || r.Executable)).ToArray();
        var budget = int.Parse(ScanBudget); if (budget is < 1 or > 1024) throw new ArgumentException("The scan budget must be 1–1024 MiB.");
        if (maps.Aggregate(0UL, (sum, r) => checked(sum + r.Size)) > (ulong)budget * 1048576) throw new InvalidDataException($"Relevant maps exceed the {budget} MiB budget. Increase it in Advanced Mode or analyze a bounded offline dump. A partial search cannot establish uniqueness.");
        Regions.Clear(); foreach (var r in allMaps) Regions.Add(r); SelectedRegion = Regions.FirstOrDefault(r => r.Readable && r.Executable) ?? Regions.FirstOrDefault();
        var ranges = new List<(MemoryRegion Region, byte[] Bytes)>(); var scanner = new MemoryScanner(session);
        foreach (var map in maps)
        {
            for (ulong at = 0; at < map.Size;)
            {
                var n = (int)Math.Min((ulong)MemoryScanner.MaximumBytes, map.Size - at);
                Status = $"Reading {map.Name}, 0x{map.Start + at:X} ({n / 1048576.0:F1} MiB)…";
                var bytes = await scanner.SnapshotAsync(current.Pid, map.Start + at, n, new Progress<double>(v => Progress = v * 100), ct);
                ranges.Add((map with { Start = map.Start + at }, bytes));
                if (at + (ulong)n == map.Size) break;
                at += (ulong)(n - Math.Max(512, cheat.Patches.Max(p => p.Original.Length) - 1));
            }
        }
        if (!current.SameGame(await client.GetGameAsync(ct))) throw new InvalidDataException("The game changed during the scan. Reconnect and analyze again.");
        return ranges;
    }
}
