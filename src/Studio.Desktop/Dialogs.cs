using Microsoft.Win32;
using System.Windows;
using Studio.Core;

namespace Studio.Desktop;

public interface IUserDialogs
{
    string? Open(string title, string filter);
    string? Save(string title, string filename, string filter);
    bool Confirm(string title, string message);
    bool ConfirmPatch(PatchReview review);
}
public sealed class WindowsDialogs : IUserDialogs
{
    public string? Open(string title, string filter) { var d = new OpenFileDialog { Title = title, Filter = filter }; return d.ShowDialog() == true ? d.FileName : null; }
    public string? Save(string title, string filename, string filter) { var d = new SaveFileDialog { Title = title, FileName = filename, Filter = filter }; return d.ShowDialog() == true ? d.FileName : null; }
    public bool Confirm(string title, string message) => MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;
    public bool ConfirmPatch(PatchReview review)
    {
        var dialog = new PatchReviewWindow(review) { Owner = Application.Current.MainWindow };
        return dialog.ShowDialog() == true;
    }
}
