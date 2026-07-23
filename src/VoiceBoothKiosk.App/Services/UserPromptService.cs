using System.Windows;

namespace VoiceBoothKiosk.App.Services;

public interface IUserPromptService
{
    bool Confirm(string title, string message, string confirmText = "Continue");
}

public sealed class UserPromptService : IUserPromptService
{
    public bool Confirm(string title, string message, string confirmText = "Continue") =>
        MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;

}
