// ============================================================================
//  ZFTP.App — Dialogs
//  ---------------------------------------------------------------------------
//  Avalonia has no MessageBox built in (unlike WPF) - this wraps FluentAvalonia's
//  ContentDialog with the two shapes this app actually needs (an OK notice, and
//  a Yes/No confirmation), so call sites read the same as the old
//  System.Windows.MessageBox.Show(...) calls did, just awaited.
// ============================================================================

using FluentAvalonia.UI.Controls;

namespace ZFTP.App;

internal static class Dialogs
{
    public static Task ShowMessageAsync(string title, string message) =>
        new ContentDialog { Title = title, Content = message, CloseButtonText = "OK" }.ShowAsync();

    public static async Task<bool> ShowConfirmAsync(string title, string message, string yesText = "Yes", string noText = "No")
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = message,
            PrimaryButtonText = yesText,
            CloseButtonText = noText,
            DefaultButton = ContentDialogButton.Close,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }
}
