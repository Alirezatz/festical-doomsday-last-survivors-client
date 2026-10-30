using Avalonia.Controls;
using FesticalDoomsday.Client.Views;

namespace FesticalDoomsday.Client.Services;

/// <summary>Small helpers around the modal dialogs of the window.</summary>
public static class Dialogs
{
    /// <summary>Asks the user a yes/no question and waits for the answer.</summary>
    public static async Task<bool> ConfirmAsync(Window owner, string title, string message,
        string confirmText, string cancelText)
    {
        var dialog = ConfirmDialog.Create(title, message, confirmText, cancelText);
        return await dialog.ShowDialog<bool>(owner);
    }
}
