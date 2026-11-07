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
