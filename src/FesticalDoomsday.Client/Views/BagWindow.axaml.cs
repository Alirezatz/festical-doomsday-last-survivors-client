using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using FesticalDoomsday.Client.Models;
using FesticalDoomsday.Client.Services;

namespace FesticalDoomsday.Client.Views;

/// <summary>
/// The real bag of one account: the resource stacks the game server reported
/// for it, each with a Use button. The window binds to the live
/// <see cref="AccountSession"/>, so a stack the server updates while the window
/// is open changes its count on its own.
/// </summary>
public partial class BagWindow : Window
{
    private readonly AccountSession? _account;
    private readonly Action<AccountSession, BagStack, bool>? _useItem;

    public BagWindow() : this(null)
    {
    }

    public BagWindow(AccountSession? account, Action<AccountSession, BagStack, bool>? useItem = null)
    {
        _account = account;
        _useItem = useItem;
        InitializeComponent();
        if (account is not null)
            DataContext = account;
        FlowDirection = Localizer.Instance.IsPersian
            ? Avalonia.Media.FlowDirection.RightToLeft
            : Avalonia.Media.FlowDirection.LeftToRight;
        // The form has no window border; it is moved by pressing it anywhere.
        WindowDrag.Attach(this);
    }

    private void Use_Click(object? sender, RoutedEventArgs e) => Use(sender, all: false);

    private void UseAll_Click(object? sender, RoutedEventArgs e) => Use(sender, all: true);

    private void Use(object? sender, bool all)
    {
        if (_account is null || sender is not Button { Tag: BagStack stack })
            return;
        // The hint on the list already tells the user that the account has to
        // be connected; a click while it is offline must not reach the backend.
        if (!_account.IsConnected)
            return;
        _useItem?.Invoke(_account, stack, all);
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape)
            Close();
    }
}
