using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Studio.Core;

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
            var bitmap = new RenderTargetBitmap((int)main.ActualWidth, (int)main.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(main);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using (var stream = File.Create(Path.Combine(output, "guided-analysis.png"))) encoder.Save(stream);
            vm.WritesEnabled = true; await Execute(vm, vm.TestCommand);
            Require(vm.VerificationStatus.Contains("simulation"), "Simulation byte test has an honest outcome.");
            await Execute(vm, vm.RollbackCommand); Require(vm.VerificationStatus.Contains("restored"), "Rollback reads original bytes back.");
            result.Add("PASS exact-byte review, patch/readback, rollback/readback");
            await Execute(vm, vm.ExportCommand); Require(File.Exists(Path.Combine(output, "trainer.json")), "Candidate trainer exported.");
            var trainer = TrainerCodec.Read(Path.Combine(output, "trainer.json")); Require(trainer.Version == "01.05", "Export uses target version.");
            await Execute(vm, vm.ExportDiagnosticsCommand); Require(File.Exists(Path.Combine(output, "diagnostics.json")), "Diagnostics report exported.");
            result.Add("PASS candidate trainer and diagnostics exports");
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
        public string? Open(string title, string filter) => null;
        public string Save(string title, string filename, string filter) => Path.Combine(directory, title.StartsWith("Save diagnostics", StringComparison.Ordinal) ? "diagnostics.json" : "trainer.json");
        public bool Confirm(string title, string message) => false;
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
