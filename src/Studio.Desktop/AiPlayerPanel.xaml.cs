using System.Windows.Controls;

namespace Studio.Desktop;

public partial class AiPlayerPanel : UserControl
{
    public AiPlayerPanel()
    {
        InitializeComponent();
        DataContext = new AiPlayerPanelViewModel();
    }
}
