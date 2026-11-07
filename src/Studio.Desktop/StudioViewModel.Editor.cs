using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Windows.Input;
using Studio.Core;

namespace Studio.Desktop;

public sealed partial class StudioViewModel
{
    private Trainer? editorBasis;
    private Cheat? editorCheat;
    private string editGame = "New game", editTitle = "CUSA00000", editVersion = "01.00", editProcess = "eboot.bin", editName = "New cheat", editDescription = "", editorStatus = "Open a trainer or create a new draft.", editorSectionBases = "";
    private bool editorOnlyCheat;
    private PatchRowViewModel? selectedEditorLine;
    private int workspaceIndex;
    public int WorkspaceIndex { get => workspaceIndex; set => Set(ref workspaceIndex, value); }
    public string EditGame { get => editGame; set { Set(ref editGame, value); ValidateEditor(); } }
    public string EditTitle { get => editTitle; set { Set(ref editTitle, value); ValidateEditor(); } }
    public string EditVersion { get => editVersion; set { Set(ref editVersion, value); ValidateEditor(); } }
    public string EditProcess { get => editProcess; set { Set(ref editProcess, value); ValidateEditor(); } }
    public string EditName { get => editName; set { Set(ref editName, value); ValidateEditor(); } }
    public string EditDescription { get => editDescription; set { Set(ref editDescription, value); ValidateEditor(); } }
    public string EditorSectionBases { get => editorSectionBases; set { Set(ref editorSectionBases, value); ValidateEditor(); } }
    public string EditorStatus { get => editorStatus; private set => Set(ref editorStatus, value); }
    public bool EditorOnlyCheat { get => editorOnlyCheat; set => Set(ref editorOnlyCheat, value); }
    public PatchRowViewModel? SelectedEditorLine { get => selectedEditorLine; set => Set(ref selectedEditorLine, value); }
    public ObservableCollection<PatchRowViewModel> EditorLines { get; } = [];
    public ObservableCollection<InstructionView> EditorInstructions { get; } = [];
    public ObservableCollection<JumpInspection> EditorJumps { get; } = [];
    private EditorValidation? editorValidation;
    public ICommand OpenEditorCommand { get; private set; } = null!;
    public ICommand NewEditorCommand { get; private set; } = null!;
    public ICommand AddEditorLineCommand { get; private set; } = null!;
    public ICommand RemoveEditorLineCommand { get; private set; } = null!;
    public ICommand ValidateEditorCommand { get; private set; } = null!;
    public ICommand SaveEditorCommand { get; private set; } = null!;
    public ICommand ExportEditorCommand { get; private set; } = null!;
    public ICommand SaveEditorProjectCommand { get; private set; } = null!;
    public ICommand OpenEditorProjectCommand { get; private set; } = null!;
    public ICommand TestEditorCommand { get; private set; } = null!;

    private void InitializeEditorCommands(Func<Func<CancellationToken, Task>, Func<bool>?, AsyncCommand> command)
    {
        OpenEditorCommand = command(_ => { LoadEditor(SelectedTrainer!, SelectedCheat!); return Task.CompletedTask; }, () => SelectedTrainer is not null && SelectedCheat is not null);
        NewEditorCommand = command(_ =>
        {
            var patch = new PatchLine("line-1", 0, 0, [0x90], [0x90], []);
            var cheat = new Cheat("New cheat", "Unverified user-authored draft", [patch]);
            LoadEditor(new(ProfileName, game?.TitleId ?? ProfileTitle, game?.Version is { Length: > 0 } v ? v : TargetVersion, "eboot.bin", [cheat]), cheat);
            EditorOnlyCheat = true; return Task.CompletedTask;
        }, null);
        AddEditorLineCommand = command(_ =>
        {
            var n = 1; while (EditorLines.Any(p => p.Id == "line-" + n)) n++;
            var row = new PatchRowViewModel { Id = "line-" + n, Section = SelectedEditorLine?.Section ?? "0" }; AddEditorRow(row); SelectedEditorLine = row; ValidateEditor(); return Task.CompletedTask;
        }, () => editorBasis is not null);
        RemoveEditorLineCommand = command(_ => { if (SelectedEditorLine is not null) EditorLines.Remove(SelectedEditorLine); SelectedEditorLine = EditorLines.FirstOrDefault(); ValidateEditor(); return Task.CompletedTask; }, () => SelectedEditorLine is not null);
        ValidateEditorCommand = command(_ => { ValidateEditor(); return Task.CompletedTask; }, () => editorBasis is not null);
        SaveEditorCommand = command(async ct =>
        {
            var built = BuildEditor(); ImportTrainer(built); await SaveLibraryAsync(ct); WorkspaceIndex = 0;
            Status = "Edited trainer saved as an unverified library entry. The original file and original library entry were preserved. Analyze the edited cheat before guided testing.";
        }, () => editorValidation?.Valid == true);
        ExportEditorCommand = command(async ct =>
        {
            var built = BuildEditor();
            var path = dialogs.Save("Export edited trainer (unverified; metadata sidecar included)", $"{built.TitleId}_{built.Version}.edited.shn", "GoldHEN SHN|*.shn|GoldHEN JSON|*.json");
            if (path is null) return;
            if (Path.GetExtension(path).Equals(".json", StringComparison.OrdinalIgnoreCase) && built.Cheats.SelectMany(c => c.Patches).Any(p => p.Ps5Offset is not null) &&
                !dialogs.Confirm("JSON metadata limitations", "GoldHEN JSON has no PS5Offset/SHN section fields. PS5 metadata will be kept in the adjacent .studio-project.json, while SHN preserves it directly. Nonzero sections still require explicit module-relative mapping before JSON export. Continue?")) return;
            var output = Path.GetExtension(path).Equals(".json", StringComparison.OrdinalIgnoreCase) ? TrainerCodec.Json(built) : TrainerCodec.Shn(built);
            await File.WriteAllTextAsync(path, output, new UTF8Encoding(false), ct); await LocalStore.SaveAsync(path + ".studio-project.json", built, ct);
            Status = "Edited trainer and Studio sidecar exported. Sidecar preserves line IDs, dependencies, cave declarations and metadata; no gameplay success was inferred.";
        }, () => editorValidation?.Valid == true);
        SaveEditorProjectCommand = command(async ct =>
        {
            var path = dialogs.Save("Save editable Studio project", "cheat.studio-project.json", "Studio project|*.json");
            if (path is not null) await LocalStore.SaveAsync(path, BuildEditor(), ct);
        }, () => editorValidation?.Valid == true);
        OpenEditorProjectCommand = command(async ct =>
        {
            var path = dialogs.Open("Open editable Studio project", "Studio project|*.json"); if (path is null) return;
            var trainer = TrainerCodec.Validate(await LocalStore.LoadAsync<Trainer>(path, ct)); LoadEditor(trainer, trainer.Cheats[0]);
        }, null);
        TestEditorCommand = command(TestEditorAsync, () => Connected && game?.IsGame == true && WritesEnabled && editorValidation?.Valid == true);
    }
    private void AddEditorRow(PatchRowViewModel row)
    {
        row.PropertyChanged += (_, _) => { hardwareConfirmed = false; testedPlan = null; ValidateEditor(); };
        EditorLines.Add(row);
    }
    private void LoadEditor(Trainer trainer, Cheat cheat)
    {
        editorBasis = trainer; editorCheat = cheat; editGame = trainer.Name; editTitle = trainer.TitleId; editVersion = trainer.Version; editProcess = trainer.Process; editName = cheat.Name; editDescription = cheat.Description;
        EditorLines.Clear(); foreach (var line in cheat.Patches) AddEditorRow(PatchRowViewModel.From(line));
        SelectedEditorLine = EditorLines.FirstOrDefault(); EditorOnlyCheat = false; EditorSectionBases = "";
        foreach (var name in new[] { nameof(EditGame), nameof(EditTitle), nameof(EditVersion), nameof(EditProcess), nameof(EditName), nameof(EditDescription) }) Changed(name);
        ValidateEditor(); WorkspaceIndex = 2;
    }
    private static Dictionary<int, ulong> ParseBases(string text)
    {
        var result = new Dictionary<int, ulong>();
        foreach (var entry in text.Split([';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = entry.Split('=');
            if (parts.Length != 2 || !int.TryParse(parts[0].Trim(), out var section) || section < 0 || !result.TryAdd(section, Hex.Address(parts[1])))
                throw new FormatException("Use unique section=hex-base mappings, e.g. 14=400000;18=600000.");
        }
        return result;
    }
    private void ValidateEditor()
    {
        if (editorBasis is null) return;
        try
        {
            editorValidation = CheatEditing.Validate(EditGame, EditTitle, EditVersion, EditProcess, EditName, EditDescription, EditorLines.Select(p => p.Draft).ToList(), InstructionPatch, ParseBases(EditorSectionBases));
            EditorStatus = editorValidation.Summary;
            EditorInstructions.Clear(); foreach (var i in editorValidation.Instructions) EditorInstructions.Add(i);
            EditorJumps.Clear(); foreach (var j in editorValidation.Jumps) EditorJumps.Add(j);
        }
        catch (Exception e) when (e is FormatException or OverflowException or InvalidDataException)
        {
            editorValidation = new([e.Message], [], [], []); EditorStatus = e.Message;
        }
        CommandManager.InvalidateRequerySuggested();
    }
    private Trainer BuildEditor()
    {
        if (editorBasis is null) throw new InvalidOperationException("Open a trainer or create a draft first.");
        ValidateEditor();
        var trainer = CheatEditing.Build(editorBasis, editorCheat, EditGame, EditTitle, EditVersion, EditProcess, EditName, EditDescription,
            EditorLines.Select(p => p.Draft).ToList(), InstructionPatch, ParseBases(EditorSectionBases));
        var edited = EditedCheat(trainer);
        if (EditorOnlyCheat) trainer = trainer with { Cheats = [edited] };
        else if ((EditVersion != editorBasis.Version || EditTitle != editorBasis.TitleId || EditProcess != editorBasis.Process) && trainer.Cheats.Count > 1) throw new InvalidDataException("Changing the target version does not port other cheats. Enable 'Keep only the edited cheat' before saving/exporting a version change.");
        return trainer;
    }
    private Cheat EditedCheat(Trainer trainer)
    {
        if (EditorOnlyCheat && trainer.Cheats.Count == 1) return trainer.Cheats[0];
        var index = editorCheat is null ? -1 : editorBasis!.Cheats.IndexOf(editorCheat);
        return trainer.Cheats[index < 0 ? trainer.Cheats.Count - 1 : index];
    }
    private async Task TestEditorAsync(CancellationToken ct)
    {
        var trainer = BuildEditor(); var cheat = EditedCheat(trainer); var current = await client.GetGameAsync(ct);
        if (!current.IsGame || !current.HasKnownVersion || trainer.TitleId != current.TitleId || trainer.Version != current.Version || trainer.Process != current.ProcessName)
            throw new InvalidDataException("Edited trainer CUSA, exact version and process must match the live game. Importing an old trainer does not authorize its old addresses on a new version.");
        var maps = await client.GetMapsAsync(current.Pid, ct); var patches = new List<ResolvedPatch>();
        var caves = cheat.Patches.Where(p => InstructionPatch && (p.IsCodeCave || PortingEngine.IsCave(p))).ToList();
        List<(MemoryRegion Region, byte[] Bytes)> ranges = caves.Count == 0 ? [] : await ReadTargetSegmentsAsync(current, cheat, ct);
        var references = new List<(ulong From, ulong To, string Kind)>();
        foreach (var (r, bytes) in ranges) references.AddRange(CaveAnalysis.References(bytes, r.Start, ct));
        foreach (var line in cheat.Patches)
        {
            if (line.Absolute) throw new InvalidDataException("Absolute SHN lines must be explicitly converted to a current section and offset before live editor testing.");
            var map = maps.SingleOrDefault(r => r.Index == line.Section) ?? throw new InvalidDataException($"{line.Id}: target section {line.Section} is not mapped.");
            var address = checked(map.Start + line.Offset); var isCave = caves.Contains(line); var caveApproved = false;
            if (isCave)
            {
                var range = ranges.FirstOrDefault(s => s.Region.Contains(address, line.Original.Length) && address - s.Region.Start + (ulong)line.Original.Length <= (ulong)s.Bytes.Length);
                if (range.Bytes is null) throw new InvalidDataException("The complete cave is not inside the captured executable ranges.");
                var expected = range.Bytes.AsSpan((int)(address - range.Region.Start), line.Original.Length).ToArray();
                var assessment = CaveAnalysis.Assess(line, address, expected, map, references, range.Bytes, range.Region.Start, new HashSet<string>());
                if (!assessment.CanReview) throw new InvalidDataException(assessment.Explanation);
                caveApproved = dialogs.Confirm("Confirm code-cave use", $"{line.Id} at 0x{address:X}: {assessment.Explanation}\nHave you manually checked this range is unused, executable, at valid boundaries, and that dependencies/return jumps are correct? This does not confirm gameplay behavior.");
                if (!caveApproved) { Status = "Cave use was not confirmed. No write was performed."; return; }
            }
            patches.Add(new(line.Id, address, line.Original.ToArray(), line.Replacement.ToArray(), line.Dependencies, isCave, caveApproved));
        }
        var service = Patcher(); var plan = new PatchPlan(client.ConnectionId, current, EditName, patches, InstructionPatch); var review = await service.ReviewAsync(plan, ct);
        if (!dialogs.ConfirmPatch(review)) return;
        rollback = await service.ApplyAsync(review, new(review.Digest, WritesEnabled, true), ct); game = current; testedPlan = null; hardwareConfirmed = false;
        VerificationStatus = SimulationConnection ? "Edited multi-line patch read back in simulation; not hardware verified." : "Edited multi-line patch read back. Actual gameplay remains unverified.";
        Status = VerificationStatus; await RecordVerification("Editor multi-line byte readback", VerificationStatus, ct);
    }
}
