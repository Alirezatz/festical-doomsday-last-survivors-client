using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace FesticalDoomsday.Client.Views;

/// <summary>The small yes/no dialog the window uses instead of a platform message box.</summary>
public partial class ConfirmDialog : Window
{
    public ConfirmDialog()
    {
        InitializeComponent();
        ConfirmButton.Click += OnConfirm;
        CancelButton.Click += OnCancel;
    }

    /// <summary>Creates a dialog with the given text and buttons.</summary>
    public static ConfirmDialog Create(string title, string message, string confirmText, string cancelText)
    {
        var dialog = new ConfirmDialog { Title = title };
        dialog.MessageTitle.Text = title;
        dialog.MessageText.Text = message;
        dialog.ConfirmButton.Content = confirmText;
        dialog.CancelButton.Content = cancelText;
        return dialog;
    }

    /// <summary>Marks the confirmation as a destructive action.</summary>
    public static ConfirmDialog CreateDanger(string title, string message, string confirmText, string cancelText)
    {
        var dialog = Create(title, message, confirmText, cancelText);
        dialog.ConfirmButton.Classes.Set("danger", true);
        dialog.IconBadge.Background = FindBrush("DangerBrush");
        dialog.IconGlyph.Foreground = FindBrush("DangerTextBrush");
        return dialog;
    }

    private static IBrush FindBrush(string key) =>
        Application.Current?.TryFindResource(key, out var value) == true && value is IBrush brush
            ? brush
            : Brushes.Transparent;

    private void OnConfirm(object? sender, RoutedEventArgs e) => Close(true);

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
}
