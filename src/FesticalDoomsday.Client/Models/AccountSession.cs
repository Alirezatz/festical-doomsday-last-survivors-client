using System.ComponentModel;
using System.Runtime.CompilerServices;
using FesticalDoomsday.Client.Services;

namespace FesticalDoomsday.Client.Models;

/// <summary>
/// One managed game account. The account JSON path doubles as this account's id
/// in the shared backend, so the UI and the backend always talk about the same
/// account with the same key.
/// </summary>
public sealed class AccountSession : INotifyPropertyChanged
{
    private bool _isConnected;
    private bool _isConnecting;
    private bool _allianceHelpEnabled;
    private string _allianceHelpState = "off";
    private int _allianceHelpPendingCount;
    private int _allianceHelpErrorCode;

    public AccountSession(string capturePath)
    {
        CapturePath = capturePath;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>The account JSON path, which doubles as this account's id in the shared backend.</summary>
    public string CapturePath { get; }

    public string CaptureFileName => Path.GetFileName(CapturePath);

    public string Name => Path.GetFileNameWithoutExtension(CapturePath);

    public string DisplayName => Name +
        (IsConnected ? " (Connected)" : IsConnecting ? " (Connecting)" : "");

    /// <summary>The state of the account, in the language of the window.</summary>
    public string StatusText => Localizer.Instance[
        IsConnected ? "StatusOnline" : IsConnecting ? "StatusConnecting" : "StatusOffline"];

    /// <summary>The label of the connect button of the account card.</summary>
    public string ActionText => Localizer.Instance[
        IsConnected ? "ActionDisconnect" : IsConnecting ? "ActionConnecting" : "ActionConnect"];

    public bool IsConnected
    {
        get => _isConnected;
        set => Set(ref _isConnected, value);
    }

    public bool IsConnecting
    {
        get => _isConnecting;
        set => Set(ref _isConnecting, value);
    }

    public bool AllianceHelpEnabled
    {
        get => _allianceHelpEnabled;
        set => Set(ref _allianceHelpEnabled, value);
    }

    public string AllianceHelpState
    {
        get => _allianceHelpState;
        set => Set(ref _allianceHelpState, value);
    }

    public int AllianceHelpPendingCount
    {
        get => _allianceHelpPendingCount;
        set => Set(ref _allianceHelpPendingCount, value);
    }

    public int AllianceHelpErrorCode
    {
        get => _allianceHelpErrorCode;
        set => Set(ref _allianceHelpErrorCode, value);
    }

    /// <summary>Completed when the backend reports the outcome of a connect attempt.</summary>
    public TaskCompletionSource<bool>? ConnectionAttempt { get; set; }

    /// <summary>Completed when the backend reports the outcome of an alliance action.</summary>
    public TaskCompletionSource<GuildActionResult>? GuildActionAttempt { get; set; }

    private void Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return;
        field = value;
        Raise(propertyName);
    }

    private void Raise(string? propertyName)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        if (propertyName is not (nameof(IsConnected) or nameof(IsConnecting)))
            return;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DisplayName)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StatusText)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ActionText)));
    }

    /// <summary>Refreshes the texts that depend on the language of the window.</summary>
    public void RefreshLocalizedText()
    {
        Raise(nameof(StatusText));
        Raise(nameof(ActionText));
    }
}
