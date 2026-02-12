using System.Windows;

namespace ResearchHive.Services;

/// <summary>
/// Service for showing confirmation dialogs and notifications.
/// Abstracted behind an interface for testability.
/// </summary>
public interface IDialogService
{
    bool Confirm(string message, string title = "Confirm");
    void Info(string message, string title = "Information");
    void Error(string message, string title = "Error");
}

public class DialogService : IDialogService
{
    public bool Confirm(string message, string title = "Confirm")
    {
        var result = MessageBox.Show(message, $"ResearchHive — {title}",
            MessageBoxButton.YesNo, MessageBoxImage.Question);
        return result == MessageBoxResult.Yes;
    }

    public void Info(string message, string title = "Information")
    {
        MessageBox.Show(message, $"ResearchHive — {title}",
            MessageBoxButton.OK, MessageBoxImage.Information);
    }

    public void Error(string message, string title = "Error")
    {
        MessageBox.Show(message, $"ResearchHive — {title}",
            MessageBoxButton.OK, MessageBoxImage.Error);
    }
}
