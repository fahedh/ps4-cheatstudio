using System.IO;
using System.Windows;

namespace Studio.Desktop;
public partial class App : Application
{
    private StudioViewModel? viewModel;
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try
        {
            if (e.Args.FirstOrDefault() == "--remote-play")
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown;
                try
                {
                    var args = e.Args.Skip(1).ToArray();
                    if(args.Length < 1 || !Path.IsPathFullyQualified(args[0]) || !args[0].EndsWith(".png",StringComparison.OrdinalIgnoreCase))
                        throw new ArgumentException("Supply an absolute path for a new PNG frame.");
                    using var output = new FileStream(args[0],FileMode.CreateNew,FileAccess.Write,FileShare.None);
                    var live = await ChiakiController.ConnectAsync(CancellationToken.None);
                    if(args.Length > 1)
                    {
                        if(args.Length < 3 || !int.TryParse(args[1],out var duration))
                            throw new ArgumentException("Supply a duration and button names.");
                        await live.PressAsync(args[2..],duration,CancellationToken.None);
                        await Task.Delay(350);
                    }
                    var bytes = LiveGameAgent.Encode(live.Capture());
                    await output.WriteAsync(bytes);
                    Shutdown(0);
                }
                catch(Exception error) { Console.Error.WriteLine(error.Message); Shutdown(2); }
                return;
            }
            if (e.Args.Contains("--smoke-test")) { await DesktopSmokeTest.RunAsync(this); return; }
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PS4GoldHENCheatStudio");
            viewModel = new(new WindowsDialogs(), directory); await viewModel.InitializeAsync();
            if (!viewModel.SetupComplete)
            {
                var setup = new SetupWindow(viewModel);
                if (setup.ShowDialog() != true) { await viewModel.DisposeAsync(); Shutdown(); return; }
            }
            var main = new MainWindow { DataContext = viewModel }; MainWindow = main;
            var closing = false;
            main.Closing += async (_, args) =>
            {
                if (closing) return;
                args.Cancel = true; main.IsEnabled = false;
                await viewModel.DisposeAsync(); closing = true; main.Close(); Shutdown();
            };
            main.Show();
        }
        catch (Exception ex) { MessageBox.Show(ex.ToString(), "Cheat Studio startup error"); Shutdown(1); }
    }
}

