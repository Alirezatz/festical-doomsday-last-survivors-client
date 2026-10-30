using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using FesticalDoomsday.Client.Models;
using FesticalDoomsday.Client.Services;

namespace FesticalDoomsday.Client.Views;

/// <summary>
/// The bag of one account: its in-game name, Might and the resources the server
/// reported for it. The window binds to the live <see cref="AccountSession"/>, so
/// numbers that arrive while it is open update on their own.
/// </summary>
public partial class InventoryWindow : Window
{
    public InventoryWindow() : this(null)
    {
    }

    public InventoryWindow(AccountSession? account)
    {
        InitializeComponent();
        if (account is not null)
            DataContext = account;
        FlowDirection = Localizer.Instance.IsPersian
            ? Avalonia.Media.FlowDirection.RightToLeft
            : Avalonia.Media.FlowDirection.LeftToRight;
        // The form has no window border; it is moved by pressing it anywhere.
        WindowDrag.Attach(this);
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape)
            Close();
    }
}
