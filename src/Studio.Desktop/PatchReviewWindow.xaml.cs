using System.Windows;
using Studio.Core;

namespace Studio.Desktop;
public partial class PatchReviewWindow : Window
{
    public PatchReviewWindow(PatchReview review)
    {
        InitializeComponent(); GameText.Text = $"{review.Plan.Game.TitleId} · {review.Plan.Game.Version} · {review.Plan.Game.ProcessName} · PID {review.Plan.Game.Pid}\n{review.Plan.CheatName}"; Changes.Text = review.Description;
    }
    private void RiskChanged(object sender, RoutedEventArgs e) { if (Apply is not null) Apply.IsEnabled = Risk.IsChecked == true; }
    private void ApplyClicked(object sender, RoutedEventArgs e) => DialogResult = true;
}
