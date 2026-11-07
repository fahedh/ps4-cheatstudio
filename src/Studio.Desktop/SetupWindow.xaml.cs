using System.Windows;
namespace Studio.Desktop;
public partial class SetupWindow : Window
{
    public SetupWindow(StudioViewModel vm)
    {
        InitializeComponent(); DataContext = vm;
        void Finish() => DialogResult = true;
        vm.SetupFinished += Finish; Closed += (_, _) => vm.SetupFinished -= Finish;
    }
}
