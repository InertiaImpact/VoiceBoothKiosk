using System.Windows;

namespace VoiceBoothKiosk.App.Services;

public interface IUserPromptService
{
    bool Confirm(string title, string message, string confirmText = "Continue");
    void ShowError(string title, string message);
    void ShowInformation(string title, string message);
}

public sealed class UserPromptService : IUserPromptService
{
    public bool Confirm(string title, string message, string confirmText = "Continue") =>
        MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;

    public void ShowError(string title, string message) =>
        MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Error);

    public void ShowInformation(string title, string message) =>
        MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Information);
}
