using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Studio.Core;
using Studio.Mock;

namespace Studio.Desktop;

/// <summary>Runs the real WPF bindings and guided commands against a real loopback simulator on Windows CI.</summary>
public static class DesktopSmokeTest
{
    public static async Task RunAsync(Application app)
    {
        var output = Path.GetFullPath("artifacts/ui-smoke"); Directory.CreateDirectory(output);
        var data = Path.Combine(Path.GetTempPath(), "CheatStudioSmoke-" + Guid.NewGuid().ToString("N"));
        var trace = new BindingTrace(); PresentationTraceSources.DataBindingSource.Listeners.Add(trace);
        StudioViewModel? vm = null; MainWindow? main = null;
        var result = new List<string>();
        try
        {
            var dialogs = new SimulatedDialogs(output);
            vm = new(dialogs, data); await vm.InitializeAsync();
            var setup = new SetupWindow(vm);
            var setupCompletion = new TaskCompletionSource();
            _ = app.Dispatcher.InvokeAsync(async () =>
            {
                try
                {
                    await Execute(vm, vm.SetupNextCommand); vm.UseSimulator = true;
                    await Execute(vm, vm.ConnectCommand); await Execute(vm, vm.SetupNextCommand); await Execute(vm, vm.SetupNextCommand);
                    setupCompletion.TrySetResult();
                }
                catch (Exception e) { setupCompletion.TrySetException(e); setup.Close(); }
            });
            var setupResult = setup.ShowDialog(); await setupCompletion.Task;
            Require(setupResult == true && vm.SetupComplete && File.Exists(Path.Combine(data, "settings.json")), "The real first-launch wizard connects and saves settings.");
            result.Add("PASS first-launch wizard navigation, simulator connection and persisted setup");
            vm.GuidedStep = 0;
            main = new MainWindow { DataContext = vm }; app.MainWindow = main; main.Show();
            await app.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            Require(Descendants<Button>(main).Any(b => ReferenceEquals(b.Command, vm.ConnectCommand)), "Connect command is bound in the real window.");
            Require(vm.Connected && vm.SimulationConnection && vm.Regions.Count == 2, "UI connects to the simulator and exposes readable game maps.");
            result.Add("PASS connect, exact game version, maps, automatic diagnostics");
            await Execute(vm, vm.DemoTrainerCommand); Require(vm.Cheats.Count == 1, "Demo trainer is imported.");
            await Execute(vm, vm.AnalyzeCommand); Require(vm.Proposed.Count == 1 && vm.Candidates.Single().Address == 0x402800, "Guided workflow locates the relocated signature.");
            Require(!vm.ConfirmWorkingCommand.CanExecute(null), "Simulator cannot be labeled hardware verified.");
            result.Add("PASS trainer import, guided relocation, no false hardware verification");
            await app.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
            await app.Dispatcher.InvokeAsync(() => main.UpdateLayout(), DispatcherPriority.ContextIdle);
            await Task.Delay(500);
            var bitmap = new RenderTargetBitmap((int)main.ActualWidth, (int)main.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(main);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using (var stream = File.Create(Path.Combine(output, "guided-analysis.png"))) encoder.Save(stream);
            vm.WritesEnabled = true; await Execute(vm, vm.TestCommand);
            Require(vm.VerificationStatus.Contains("simulation"), "Simulation byte test has an honest outcome.");
            await Execute(vm, vm.RollbackCommand); Require(vm.VerificationStatus.Contains("restored"), "Rollback reads original bytes back.");
            result.Add("PASS exact-byte review, patch/readback, rollback/readback");
            await Execute(vm, vm.ExportCommand); Require(File.Exists(Path.Combine(output, "trainer.json")), "Candidate trainer exported.");
            var trainer = TrainerCodec.Read(Path.Combine(output, "trainer.json")); Require(trainer.Version == "01.05" && trainer.Cheats[0].Patches[0].Offset == 0x2800, "Export uses the target version and main-module-relative offset.");
            await Execute(vm, vm.ExportDiagnosticsCommand); Require(File.Exists(Path.Combine(output, "diagnostics.json")), "Diagnostics report exported.");
            result.Add("PASS candidate trainer and diagnostics exports");
            dialogs.OpenFiles.Enqueue(Path.GetFullPath("examples/silksong-metadata-structure.shn"));
            await Execute(vm, vm.ImportCommand);
            Require(vm.SelectedTrainer!.Metadata!.Authors["Moder"] == "Alternate Trainer Author" && vm.SelectedCheat!.Patches[0].Section == 14 && vm.SelectedCheat.Patches[0].Ps5Offset is not null, "Real-world SHN shape imports through the UI without using PS5 metadata for PS4 addressing.");
            Require(!vm.TestCommand.CanExecute(null), "Automatic cave testing is gated before manual review.");
            await ExecuteExpectFailure(vm, vm.CaptureEvidenceCommand, "To capture original context");
            await Execute(vm, vm.OpenEditorCommand);
            Require(vm.EditorLines.Count == 2 && !vm.SaveEditorCommand.CanExecute(null), "Editor exposes multiple lines and reports the missing cave dependency.");
            vm.EditorLines[0].Dependencies = "line-2";
            Require(vm.SaveEditorCommand.CanExecute(null), vm.EditorStatus);
            await ExecuteExpectFailure(vm, vm.TestEditorCommand, "Edited trainer CUSA");
            await Execute(vm, vm.AddEditorLineCommand); Require(vm.EditorLines.Count == 3 && !vm.SaveEditorCommand.CanExecute(null), "Incomplete new lines block saving.");
            await Execute(vm, vm.RemoveEditorLineCommand); Require(vm.EditorLines.Count == 2 && vm.SaveEditorCommand.CanExecute(null), vm.EditorStatus);
            await Execute(vm, vm.SaveEditorProjectCommand); await Execute(vm, vm.ExportEditorCommand);
            var edited = TrainerCodec.Read(Path.Combine(output, "edited.shn"));
            Require(edited.Cheats.Count == 2 && edited.Cheats[0].Patches.Any(p => p.Ps5Offset is not null) && edited.Metadata!.Authors["Author"] == "Metadata Fixture Contributor", "Editor round-trip preserves other cheats, authors and PS5 metadata.");
            Require(vm.EditorJumps.Count == 2 && vm.EditorInstructions.Any(i => i.Assembly.Contains("original:")), "Editor displays hook/return jumps and original/replacement disassembly.");
            await app.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            Require(Descendants<System.Windows.Controls.DataGrid>(main).Any(g => ReferenceEquals(g.ItemsSource, vm.EditorLines) && !g.IsReadOnly), "The actual multi-line grid is editable and bound.");
            await Execute(vm, vm.SaveEditorCommand); await Execute(vm, vm.AnalyzeCommand);
            vm.SelectedPortCandidate = vm.Candidates.Single(c => c.CodeCave);
            await Execute(vm, vm.ReviewCaveCommand); Require(vm.Proposed.Count == 2 && vm.TestCommand.CanExecute(null), vm.AnalysisSummary);
            vm.EditVersion = "01.05"; vm.EditorOnlyCheat = true;
            foreach (var row in vm.EditorLines)
            {
                var proposal = vm.Proposed.Single(p => p.Id == row.Id);
                row.Section = proposal.Section.ToString(); row.Offset = proposal.Offset.ToString("X"); row.Original = proposal.OriginalHex; row.Replacement = proposal.ReplacementHex;
                row.Dependencies = string.Join(", ", proposal.Dependencies); row.Ps5Offset = "";
            }
            await Execute(vm, vm.TestEditorCommand); Require(vm.VerificationStatus.Contains("simulation"), "Direct graphical-editor testing is explicitly reviewed and unverified.");
            await Execute(vm, vm.RollbackCommand);
            await Execute(vm, vm.TestCommand); await Execute(vm, vm.RollbackCommand);
            Require(!vm.ConfirmWorkingCommand.CanExecute(null), "Multi-line simulation still cannot be hardware confirmed.");
            result.Add("PASS PS5Offset SHN import, graphical multi-line add/remove/edit/validate, project/SHN export and jump disassembly");
            result.Add("PASS separate cave confirmation, multi-line dependency-ordered patch/readback and rollback/readback");
            File.Move(Path.Combine(output, "edited.shn"), Path.Combine(output, "metadata-edited.shn"), true);
            dialogs.OpenFiles.Enqueue(Path.GetFullPath("examples/silksong-synthetic-modified.bin"));
            await Execute(vm, vm.LoadDumpCommand); Require(!vm.Connected, "Offline analysis disconnects the console.");
            dialogs.OpenFiles.Enqueue(Path.GetFullPath("examples/silksong-context-demo.shn")); await Execute(vm, vm.ImportCommand);
            Require(vm.Candidates.Count == 1 && !vm.Candidates[0].ExactBytes && !vm.TestCommand.CanExecute(null), "Changed code is a candidate, not an applicable live patch.");
            dialogs.OpenFiles.Enqueue(Path.GetFullPath("examples/silksong-synthetic-old.studio-evidence.json")); await Execute(vm, vm.LoadEvidenceCommand);
            Require(vm.Candidates[0].Evidence!.FunctionComparison.Contains("Bounded function evidence"), "Old source evidence feeds actual function/instruction comparison.");
            await Execute(vm, vm.InspectCandidateCommand); Require(vm.EditVersion == "01.05" && vm.EditorLines.Count == 1 && vm.EditorLines[0].Original.Contains("68"), "Guided fallback opens actual target bytes in an unverified editor draft.");
            await Execute(vm, vm.SaveEditorCommand); await Execute(vm, vm.AnalyzeCommand); Require(!vm.TestCommand.CanExecute(null), "Offline review cannot become a live/hardware test.");
            await Execute(vm, vm.ExportCommand);
            result.Add("PASS changed-code offline import, old evidence/function comparison, guided editor fallback and unverified export");
            await using var oldConsole = new MockDebuggerServer { Game = new(100, "Synthetic old version", "CUSA39420", "01.04", "eboot.bin") };
            oldConsole.Start(); vm.UseSimulator = false; vm.Host = "127.0.0.1"; vm.DebugPort = oldConsole.Port.ToString(); await Execute(vm, vm.ConnectCommand);
            var oldCheat = new Cheat("Synthetic old context capture", "Not a gameplay cheat", [
                new("hook", 14, 0x2800, Hex.Bytes("8943604883C408"), Hex.Bytes("90909090909090"), []),
                new("cave", 14, 0x4783800, new byte[12], Hex.Bytes("909090909090909090909090"), [])]);
            vm.ImportTrainer(new("Old context capture fixture", "CUSA39420", "01.04", "eboot.bin", [oldCheat])); vm.SourceMappings = "14=400000";
            await Execute(vm, vm.CaptureEvidenceCommand);
            var captured = await LocalStore.LoadAsync<EvidencePackage>(Path.Combine(output, "captured-old.studio-evidence.json"));
            Require(captured.Version == "01.04" && captured.Sources.Count == 1 && captured.Sources[0].SectionBase == 0x400000, "Old capture verifies coordinates/bytes and does not treat unavailable old cave padding as function evidence.");
            result.Add("PASS capture refuses the new version and captures verified old hook context with explicit original section mapping");
            vm.AdvancedMode = true;
            var workspace = (TabControl)main.FindName("WorkspaceTabs"); var boundButtons = 0;
            for (var tabIndex = 0; tabIndex < workspace.Items.Count; tabIndex++)
            {
                workspace.SelectedIndex = tabIndex; await app.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
                foreach (var nested in Descendants<TabControl>(workspace).Where(t => !ReferenceEquals(t, workspace)).ToArray())
                {
                    for (var child = 0; child < nested.Items.Count; child++)
                    {
                        nested.SelectedIndex = child; await app.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
                        foreach (var button in Descendants<Button>(main).Where(b => b.GetBindingExpression(Button.CommandProperty) is not null))
                        { Require(button.Command is not null, "A displayed bound action has no command: " + button.Content); boundButtons++; }
                    }
                }
            }
            Require(boundButtons > 50, "All main/advanced/editor screens must instantiate and bind their actions.");
            result.Add("PASS every guided/library/diagnostics/editor/advanced screen instantiates with functional command bindings");
            await app.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            Require(trace.Errors.Length == 0, "No WPF binding errors: " + trace.Errors);
            result.Add("PASS WPF bindings and screenshot render");
            await File.WriteAllLinesAsync(Path.Combine(output, "result.txt"), result);
            await vm.DisposeAsync(); main.Close(); app.Shutdown(0);
        }
        catch (Exception e)
        {
            result.Add("FAIL " + e); await File.WriteAllLinesAsync(Path.Combine(output, "result.txt"), result);
            if (vm is not null) await vm.DisposeAsync(); main?.Close(); app.Shutdown(1);
        }
        finally { PresentationTraceSources.DataBindingSource.Listeners.Remove(trace); }
    }
    private static async Task Execute(StudioViewModel vm, System.Windows.Input.ICommand command)
    {
        Require(command.CanExecute(null), "Command must be enabled."); command.Execute(null);
        var until = Stopwatch.StartNew(); while (vm.IsBusy) { if (until.Elapsed > TimeSpan.FromSeconds(40)) throw new TimeoutException("UI command timed out."); await Task.Delay(20); }
        Require(string.IsNullOrEmpty(vm.ErrorMessage), vm.ErrorMessage);
    }
    private static async Task ExecuteExpectFailure(StudioViewModel vm, System.Windows.Input.ICommand command, string expected)
    {
        Require(command.CanExecute(null), "Expected-failure command must be enabled before its preflight."); command.Execute(null);
        var until = Stopwatch.StartNew(); while (vm.IsBusy) { if (until.Elapsed > TimeSpan.FromSeconds(40)) throw new TimeoutException("UI preflight timed out."); await Task.Delay(20); }
        Require(vm.ErrorMessage.Contains(expected, StringComparison.Ordinal), "Expected preflight rejection: " + vm.ErrorMessage);
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i); if (child is T typed) yield return typed;
            foreach (var found in Descendants<T>(child)) yield return found;
        }
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private sealed class SimulatedDialogs(string directory) : IUserDialogs
    {
        public Queue<string> OpenFiles { get; } = new();
        public string? Open(string title, string filter) => OpenFiles.TryDequeue(out var path) ? path : null;
        public string Save(string title, string filename, string filter) => Path.Combine(directory,
            title.StartsWith("Save diagnostics", StringComparison.Ordinal) ? "diagnostics.json" : title.StartsWith("Export edited", StringComparison.Ordinal) ? "edited.shn" : title.StartsWith("Save editable", StringComparison.Ordinal) ? "editor.studio-project.json" : title.StartsWith("Save original context", StringComparison.Ordinal) ? "captured-old.studio-evidence.json" : "trainer.json");
        public bool Confirm(string title, string message) => title is "Manual code-cave confirmation" or "Confirm code-cave use" && message.Contains("0x403800", StringComparison.Ordinal);
        public bool ConfirmPatch(PatchReview review)
        {
            Require(review.Plan.Game.Pid == 100 && review.Plan.Game.TitleId == "CUSA39420", "Smoke test approvals are only for the simulator."); return true;
        }
    }
    private sealed class BindingTrace : TraceListener
    {
        public StringBuilder Errors { get; } = new();
        public override void Write(string? message) { if (message?.Contains("Error", StringComparison.Ordinal) == true) Errors.Append(message); }
        public override void WriteLine(string? message) => Write(message);
    }
}
