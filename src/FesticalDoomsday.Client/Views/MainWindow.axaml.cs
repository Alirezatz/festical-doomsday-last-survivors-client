using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using FesticalDoomsday.Client.Backend;
using FesticalDoomsday.Client.Models;
using FesticalDoomsday.Client.Services;

namespace FesticalDoomsday.Client.Views;

/// <summary>
/// The one window of the client: it shows one card per account, the shared
/// console and the controls for connecting accounts and running alliance
/// actions.
/// </summary>
public partial class MainWindow : Window
{
    private readonly ObservableCollection<ConsoleEntry> _console = [];
    private readonly ObservableCollection<AccountSession> _accounts = [];
    private readonly Queue<ConsoleEntry> _pendingLogs = new();
    private readonly AccountStore _store;
    private readonly DispatcherTimer _memoryTimer = new();
    private readonly DispatcherTimer _logFlushTimer = new();
    private readonly Process _selfProcess = Process.GetCurrentProcess();

    private BackendBridge? _bridge;
    private TransportWindow? _transportWindow;
    private bool _isPersian;
    private bool _bulkConnectRunning;
    private bool _guildActionRunning;
    private bool _disconnectAllRunning;
    private bool _customServerEnabled;
    private bool _autoBountyEnabled;
    private bool _consoleVisible = true;
    private string? _customServerHost;
    private DotState _dotState = (DotState)(-1);
    private string _statusMessage = string.Empty;
    private string _statusLevel = "info";

    private enum DotState
    {
        Offline,
        Online,
        Busy
    }

    public MainWindow() : this(AccountStore.DefaultRegistryPath, false)
    {
    }

    /// <summary>
    /// Creates the window on top of a specific account registry and in one
    /// language. The snapshot tool uses this to render the window without
    /// touching the real account list.
    /// </summary>
    internal MainWindow(string registryPath, bool persian = false)
    {
        _store = new AccountStore(registryPath);
        // The remembered level has to be known before the first line is logged.
        AppLog.Load(registryPath);
        _isPersian = persian;
        // The language has to be set before the window is built: every text of
        // the window reads the localizer while it is created.
        Localizer.Instance.SetLanguage(persian);
        InitializeComponent();
        DataContext = this;
        Icon = LoadWindowIcon();
        AccountCards.ItemsSource = _accounts;
        ConsoleList.ItemsSource = _console;
        _logFlushTimer.Interval = TimeSpan.FromMilliseconds(120);
        _logFlushTimer.Tick += (_, _) => FlushConsoleQueue();
        Localizer.Instance.LanguageChanged += OnLanguageChanged;
        ApplyLanguage(persian);
        UpdateLogLevelButton();
        // Logging off means no console at all: the panel goes away instead of
        // sitting there empty.
        _consoleVisible = !AppLog.IsOff;
        ApplyConsoleVisibility();
        AddLog(Localize("Application ready. Credentials are stored in LocalAppData.",
            "برنامه آماده است. اطلاعات ورود در LocalAppData نگهداری می‌شود."));
        LoadAccounts();
        RefreshUi();
        _memoryTimer.Interval = TimeSpan.FromSeconds(1);
        _memoryTimer.Tick += (_, _) => UpdateMemoryUsage();
        UpdateMemoryUsage();
        _memoryTimer.Start();
        Closed += (_, _) =>
        {
            _memoryTimer.Stop();
            _logFlushTimer.Stop();
            Localizer.Instance.LanguageChanged -= OnLanguageChanged;
            _bridge?.Dispose();
            _bridge = null;
        };
    }

    private static WindowIcon? LoadWindowIcon()
    {
        try
        {
            using var stream = AssetLoader.Open(
                new Uri("avares://FesticalDoomsdayClient/Assets/ICON.ico"));
            return new WindowIcon(stream);
        }
        catch (Exception)
        {
            return null;
        }
    }

    // ---- shared backend service -------------------------------------------

    private BackendBridge EnsureBridge()
    {
        if (_bridge is { IsRunning: true })
            return _bridge;
        _bridge?.Dispose();
        _bridge = new BackendBridge(HandleBackendEvent);
        _bridge.Start();
        return _bridge;
    }

    private void ResetBridgeIfDead()
    {
        if (_bridge is { IsRunning: false })
        {
            _bridge.Dispose();
            _bridge = null;
        }
    }

    private bool TrySendToBackend(AccountSession? account, object command)
    {
        try
        {
            if (_bridge is not { IsRunning: true })
                throw new InvalidOperationException("The shared backend is not running.");
            _bridge.Send(command);
            return true;
        }
        catch (Exception ex)
        {
            var prefix = account is null ? string.Empty : $"{account.Name}: ";
            AddLog(prefix + Localize($"Backend command failed: {ex.Message}",
                $"ارسال فرمان به بک‌اند ناموفق بود: {ex.Message}"), "error");
            return false;
        }
    }

    private void AccountConnection_Click(object? sender, RoutedEventArgs e)
    {
        if (_guildActionRunning || _bulkConnectRunning || _disconnectAllRunning)
            return;
        if (sender is not Button { Tag: AccountSession account })
            return;
        if (account.IsConnected)
        {
            DisconnectAccount(account);
            return;
        }

        _ = ConnectAccountAsync(account, GetCustomServerHostForConnection());
    }

    private async void RemoveAccount_Click(object? sender, RoutedEventArgs e)
    {
        if (_guildActionRunning || _bulkConnectRunning || _disconnectAllRunning)
            return;
        if (sender is not Button { Tag: AccountSession account })
            return;

        var confirmed = await Dialogs.ConfirmAsync(this,
            Localize("Remove account", "حذف اکانت"),
            Localize($"Remove {account.DisplayName} from this account list? Its connection will be closed.",
                $"اکانت {account.DisplayName} از فهرست حذف شود؟ اتصال آن نیز قطع خواهد شد."),
            Localizer.Instance["DialogRemove"], Localizer.Instance["DialogCancel"]);
        if (!confirmed)
            return;

        if (_bridge is { IsRunning: true })
            TrySendToBackend(account, new { action = "remove", account = account.CapturePath });
        ResetAccountState(account);
        _accounts.Remove(account);
        try
        {
            PersistAccounts();
        }
        catch (Exception ex)
        {
            AddLog(Localize($"Account was removed, but the account list could not be saved: {ex.Message}",
                $"اکانت حذف شد، اما ذخیرهٔ فهرست اکانت‌ها ناموفق بود: {ex.Message}"), "error");
        }

        RefreshUi();
        AddLog(Localize($"Removed account {account.CaptureFileName}.",
            $"اکانت {account.CaptureFileName} حذف شد."), "success");
    }

    /// <summary>Opens the information of one account: its Might and its resources.</summary>
    private void Information_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: AccountSession account })
            return;

        var window = new InventoryWindow(account);
        window.Show(this);
    }

    /// <summary>
    /// Opens the real bag of one account: the resource stacks the game server
    /// reported for it, with Use buttons. The list is asked for again so the
    /// form never shows a stale bag.
    /// </summary>
    private void Bag_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: AccountSession account })
            return;

        if (account.IsConnected)
            TrySendToBackend(account, new { action = "bag_items", account = account.CapturePath });

        var window = new BagWindow(account, UseBagItem);
        window.Show(this);
    }

    /// <summary>
    /// Opens the Transport Resource form of one account: the Trading Post level
    /// and its limits, the receiver, the resource and the amount. An account
    /// without a Trading Post opens the same form, which then says in red that
    /// the building has to be built first.
    /// </summary>
    private void Transport_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: AccountSession account })
            return;

        if (_transportWindow is { } open)
        {
            if (ReferenceEquals(open.Account, account))
            {
                open.Activate();
                return;
            }

            open.Close();
        }

        var window = new TransportWindow(account, RequestTransportCapacity, SendTransport)
        {
            Title = Localizer.Instance.Format("TransportSubtitle", account.PlayerNameText)
        };
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_transportWindow, window))
                _transportWindow = null;
        };
        _transportWindow = window;

        if (account.IsConnected)
            TrySendToBackend(account, new { action = "transport_info", account = account.CapturePath });
        else
            window.ApplySnapshotMissing();

        window.Show(this);
    }

    /// <summary>The form asks for the limits of the receiver it shows.</summary>
    private void RequestTransportCapacity(AccountSession account, ulong targetId)
    {
        if (!account.IsConnected)
            return;
        TrySendToBackend(account, new
        {
            action = "transport_capacity",
            account = account.CapturePath,
            target_id = targetId
        });
    }

    /// <summary>The form sends one transport; the backend builds the game request.</summary>
    private void SendTransport(AccountSession account, ulong targetId, uint subType, ulong amount)
    {
        if (!account.IsConnected || targetId == 0 || amount == 0)
            return;
        TrySendToBackend(account, new
        {
            action = "transport_send",
            account = account.CapturePath,
            target_id = targetId,
            sub_type = subType,
            amount
        });
    }

    /// <summary>One Use click of the bag form; the backend sends the game request.</summary>
    private void UseBagItem(AccountSession account, BagStack stack, bool all)
    {
        if (!account.IsConnected)
            return;

        var count = all ? Math.Max(1UL, stack.Count) : 1UL;
        count = Math.Min(count, uint.MaxValue);
        if (!TrySendToBackend(account, new
            {
                action = "item_use",
                account = account.CapturePath,
                item_id = stack.ItemId,
                count
            }))
        {
            return;
        }

        AddLog(Localize(
            $"{account.Name}: using {count:N0} × {stack.Name} from the bag.",
            $"{account.Name}: استفاده از {count:N0} عدد {stack.Name} از کیف."));
    }

    /// <summary>
    /// Starts or stops the Bounty wheel of one account. The backend keeps the
    /// ticket balance, spends 100 per spin (at most 5 spins a turn) and stops by
    /// itself when the balance can no longer pay one spin.
    /// </summary>
    private void Spin_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: AccountSession account })
            return;

        if (!account.IsConnected)
        {
            var warning = Localize(
                $"{account.Name}: connect this account before spinning the Bounty wheel.",
                $"{account.Name}: پیش از چرخاندن گردونه، اکانت را وصل کن.");
            SetStatus(warning, "warn");
            AddLog(warning, "warn");
            return;
        }

        var enable = !account.AutoSpinOn;
        if (!TrySendToBackend(account, new
            {
                action = "bounty_spin",
                account = account.CapturePath,
                enabled = enable
            }))
        {
            return;
        }

        account.SetAutoSpin(enable, enable ? "spinning" : "off");
        AddLog(Localize(
            $"{account.Name}: Bounty wheel {(enable ? "on" : "off")}.",
            $"{account.Name}: گردونه {(enable ? "روشن" : "خاموش")} شد."), enable ? "info" : "success");
    }

    private static void ResetAccountState(AccountSession account)
    {
        account.IsConnected = false;
        account.IsConnecting = false;
        account.AllianceHelpEnabled = false;
        account.AllianceHelpState = "off";
        account.AllianceHelpPendingCount = 0;
        account.AllianceHelpErrorCode = 0;
        account.GuildKnown = false;
        account.InGuild = false;
        account.GuildName = string.Empty;
        account.ResetBountyTickets();
        account.ResetPlayerBase();
        account.ResetBag();
        account.BountyMode = "off";
        account.SetAutoSpin(false, "off");
        account.ConnectionAttempt?.TrySetResult(false);
    }

    private void DisconnectAccount(AccountSession account)
    {
        if (_bridge is { IsRunning: true })
            TrySendToBackend(account, new { action = "disconnect", account = account.CapturePath });
        ResetAccountState(account);
        RefreshUi();
    }

    private async Task<bool> ConnectAccountAsync(AccountSession account, string? customServerHost = null)
    {
        if (account.IsConnected)
            return true;
        if (account.IsConnecting)
            return false;

        account.IsConnecting = true;
        account.ResetBountyTickets();
        account.ConnectionAttempt = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        RefreshUi();
        try
        {
            var bridge = EnsureBridge();
            bridge.Send(new
            {
                action = "connect",
                account = account.CapturePath,
                capture_path = account.CapturePath,
                custom_server_host = customServerHost
            });
            AddLog(Localize($"{account.Name}: connecting to the game server.",
                $"{account.Name}: در حال اتصال به سرور بازی."));

            var connected = await account.ConnectionAttempt.Task.WaitAsync(TimeSpan.FromSeconds(45));
            if (connected)
            {
                // Sync the current toggle state even when it is off: the
                // backend account object survives a disconnect.
                TrySendToBackend(account, new
                {
                    action = "bounty_auto",
                    account = account.CapturePath,
                    enabled = _autoBountyEnabled
                });
            }

            return connected;
        }
        catch (TimeoutException)
        {
            account.IsConnected = false;
            account.IsConnecting = false;
            CancelBackendConnection(account);
            AddLog(Localize($"{account.Name}: connection timed out; moving on.",
                $"{account.Name}: اتصال بیش از حد طول کشید؛ ادامه می‌دهم."), "error");
            return false;
        }
        catch (Exception ex)
        {
            account.IsConnected = false;
            account.IsConnecting = false;
            ResetBridgeIfDead();
            AddLog(Localize($"{account.Name}: could not connect: {ex.Message}",
                $"{account.Name}: اتصال شروع نشد: {ex.Message}"), "error");
            return false;
        }
        finally
        {
            account.ConnectionAttempt = null;
            RefreshUi();
            EnableAllianceHelpForConnectedAccounts();
        }
    }

    private void CancelBackendConnection(AccountSession account)
    {
        if (_bridge is { IsRunning: true })
            TrySendToBackend(account, new { action = "disconnect", account = account.CapturePath });
        ResetBridgeIfDead();
    }

    /// <summary>The account sessions the window currently shows.</summary>
    internal IReadOnlyList<AccountSession> Accounts => _accounts;

    /// <summary>The language the window currently shows.</summary>
    internal string Language => _isPersian ? "fa" : "en";

    /// <summary>The title the window currently shows.</summary>
    internal string HeaderTitleText => HeaderTitle.Text ?? string.Empty;

    private void UpdateAccountCards()
    {
        EmptyAccountsHint.IsVisible = _accounts.Count == 0;
    }

    /// <summary>Repaints every part of the window that depends on account state.</summary>
    internal void RefreshUi()
    {
        UpdateAccountCards();
        UpdateControls();
        UpdateConnectionStatus();
        UpdateStatusLine();
    }

    // ---- language ---------------------------------------------------------

    private void English_Click(object? sender, RoutedEventArgs e) => ApplyLanguage(false);

    private void Persian_Click(object? sender, RoutedEventArgs e) => ApplyLanguage(true);

    internal void ApplyLanguage(bool persian)
    {
        _isPersian = persian;
        Localizer.Instance.SetLanguage(persian);
        FlowDirection = persian ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        EnglishButton.Classes.Set("active", !persian);
        PersianButton.Classes.Set("active", persian);
        RefreshUi();
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        foreach (var account in _accounts)
            account.RefreshLocalizedText();
        UpdateLogLevelButton();
        ApplyConsoleVisibility();
        RefreshUi();
    }

    // ---- files ------------------------------------------------------------

    private async void BuildCapture_Click(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Localize("Select network capture", "انتخاب فایل ضبط شبکه"),
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Wireshark capture")
                {
                    Patterns = ["*.pcapng", "*.pcap"]
                }
            ]
        });
        if (files.Count == 0)
            return;
        var capturePath = files[0].TryGetLocalPath();
        if (string.IsNullOrEmpty(capturePath))
            return;

        var outputPath = Path.ChangeExtension(capturePath, ".json");
        if (_accounts.Any(account =>
                string.Equals(account.CapturePath, outputPath, StringComparison.OrdinalIgnoreCase) &&
                (account.IsConnected || account.IsConnecting)))
        {
            AddLog(Localize("Disconnect this account before replacing its JSON file.",
                "پیش از جایگزینی JSON، اتصال این اکانت را قطع کن."), "warn");
            return;
        }

        if (File.Exists(outputPath))
        {
            var confirmed = await Dialogs.ConfirmAsync(this,
                Localize("Overwrite account JSON", "جایگزینی فایل JSON"),
                Localize($"The output file already exists and will be overwritten:\n{outputPath}\n\nContinue?",
                    $"فایل خروجی از قبل وجود دارد و جایگزین می‌شود:\n{outputPath}\n\nادامه می‌دهی؟"),
                Localizer.Instance["DialogOverwrite"], Localizer.Instance["DialogCancel"]);
            if (!confirmed)
                return;
        }

        BuildCaptureButton.IsEnabled = false;
        try
        {
            AddLog(Localize("Decoding network capture; token values will not be printed.",
                "در حال decode ضبط شبکه؛ مقدار توکن چاپ نخواهد شد."));

            var builtPath = await Task.Run(() => CaptureExtractor.ExtractToJson(
                capturePath, outputPath, line => Dispatcher.UIThread.Post(() => AddLog(line))));
            AddAccountFile(builtPath, randomizeDeviceId: true);
            AddLog(Localize($"Created account JSON: {builtPath}", $"فایل JSON اکانت ساخته شد: {builtPath}"), "success");
        }
        catch (Exception ex)
        {
            AddLog(Localize($"Capture extraction error: {ex.Message}", $"خطای استخراج: {ex.Message}"), "error");
        }
        finally
        {
            BuildCaptureButton.IsEnabled = true;
        }
    }

    private async void ImportAccount_Click(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Localize("Add account JSON files", "افزودن فایل‌های JSON اکانت"),
            AllowMultiple = true,
            FileTypeFilter =
            [
                new FilePickerFileType("Account JSON")
                {
                    Patterns = ["*.json"]
                }
            ]
        });

        foreach (var file in files)
        {
            var path = file.TryGetLocalPath();
            if (string.IsNullOrEmpty(path))
                continue;
            try
            {
                AddAccountFile(path, randomizeDeviceId: true);
            }
            catch (Exception ex)
            {
                AddLog(Localize($"Could not add {Path.GetFileName(path)}: {ex.Message}",
                    $"افزودن {Path.GetFileName(path)} ناموفق بود: {ex.Message}"), "error");
            }
        }
    }

    internal void AddAccountFile(string path, bool persist = true, bool randomizeDeviceId = false)
    {
        path = Path.GetFullPath(path);
        AccountStore.Validate(path);

        var existing = _accounts.FirstOrDefault(account =>
            string.Equals(account.CapturePath, path, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
            return;

        if (randomizeDeviceId)
        {
            var assignedIds = AccountStore.CollectAssignedDeviceIds(_accounts);
            AccountStore.AssignRandomDeviceId(path, assignedIds);
            AddLog(Localize($"Assigned a unique random GameServer Device ID to {Path.GetFileName(path)}.",
                $"یک GameServer Device ID تصادفی و یکتا به {Path.GetFileName(path)} اختصاص داده شد."));
        }

        var account = new AccountSession(path);
        _accounts.Add(account);
        if (persist)
            PersistAccounts();
        RefreshUi();
        AddLog(Localize($"Added account {account.DisplayName}. Credentials remain private.",
            $"اکانت {account.DisplayName} اضافه شد؛ اطلاعات ورود محرمانه باقی می‌ماند."), "success");
    }

    private void LoadAccounts()
    {
        foreach (var path in _store.Load())
        {
            if (!File.Exists(path))
            {
                AddLog(Localize($"Saved account file is missing: {path}",
                    $"فایل اکانت ذخیره‌شده پیدا نشد: {path}"), "warn");
                continue;
            }

            try
            {
                AddAccountFile(path, persist: false);
            }
            catch (Exception ex)
            {
                AddLog(Localize($"Could not load {Path.GetFileName(path)}: {ex.Message}",
                    $"بارگذاری {Path.GetFileName(path)} ناموفق بود: {ex.Message}"), "warn");
            }
        }
    }

    private void PersistAccounts() => _store.Save(_accounts);

    // ---- status ------------------------------------------------------------

    private void UpdateConnectionStatus()
    {
        var total = _accounts.Count;
        var connected = _accounts.Count(account => account.IsConnected);
        var busy = _bulkConnectRunning || _disconnectAllRunning || _guildActionRunning
                   || _accounts.Any(account => account.IsConnecting);

        DotState state;
        string label;
        if (total > 0 && connected == total)
        {
            state = DotState.Online;
            label = _isPersian ? "همه متصل" : "All connected";
        }
        else if (connected > 0)
        {
            state = DotState.Online;
            label = _isPersian ? $"{connected}/{total} متصل" : $"{connected}/{total} connected";
        }
        else if (busy)
        {
            state = DotState.Busy;
            label = _isPersian ? "در حال انجام..." : "Working...";
        }
        else
        {
            state = DotState.Offline;
            label = _isPersian ? "قطع" : "Disconnected";
        }

        // Only rebuild the dot brush/glow when the state actually changes; the
        // console burst during login can call this hundreds of times.
        if (state != _dotState)
        {
            _dotState = state;
            var color = state switch
            {
                DotState.Online => Color.FromRgb(0x2B, 0xE3, 0x8B),
                DotState.Busy => Color.FromRgb(0xE8, 0xB8, 0x4B),
                _ => Color.FromRgb(0x3E, 0x4B, 0x45)
            };
            ConnectionDot.Background = new SolidColorBrush(color);
            ConnectionDot.BoxShadow = state == DotState.Offline
                ? default
                : new BoxShadows(BoxShadow.Parse($"0 0 14 0 {color}"));
        }

        ConnectionLabel.Text = label;
    }

    /// <summary>
    /// Working set of the client process. The shared backend runs inside this
    /// process, so the header shows what the whole program actually costs.
    /// </summary>
    private void UpdateMemoryUsage()
    {
        var clientBytes = TryWorkingSet(_selfProcess);
        MemoryValue.Text = FormatMemory(clientBytes);
        ToolTip.SetTip(MemoryValue, Localize(
            $"Client {FormatMemory(clientBytes)} (shared backend included)",
            $"کلاینت {FormatMemory(clientBytes)} (بک‌اند مشترک شامل می‌شود)"));
    }

    private static long TryWorkingSet(Process process)
    {
        try
        {
            process.Refresh();
            return process.WorkingSet64;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    private static string FormatMemory(long bytes)
    {
        if (bytes <= 0)
            return "—";
        var megabytes = bytes / 1024.0 / 1024.0;
        return megabytes >= 1024
            ? $"{megabytes / 1024.0:0.0} GB"
            : $"{megabytes:0} MB";
    }

    private void UpdateControls()
    {
        var busy = _bulkConnectRunning || _guildActionRunning || _disconnectAllRunning
                   || _accounts.Any(account => account.IsConnecting);
        var connectedAccounts = _accounts.Where(account => account.IsConnected).ToArray();
        var connectingCount = _accounts.Count(account => account.IsConnecting);

        ConnectButton.Content = Localizer.Instance[
            _bulkConnectRunning ? "ConnectAllBusy" : "ConnectAll"];
        ConnectButton.IsEnabled = _accounts.Count > 0
                                  && !busy
                                  && _accounts.Any(account => !account.IsConnected);

        DisconnectAll.Content = Localizer.Instance[
            _disconnectAllRunning ? "DisconnectAllBusy" : "DisconnectAll"];
        DisconnectAll.IsEnabled = connectedAccounts.Length > 0 && !busy;

        LeaveAlliance.IsEnabled = connectedAccounts.Length > 0 && !busy;
        JoinAlliance.IsEnabled = connectedAccounts.Length > 0
                                        && !busy
                                        && !string.IsNullOrWhiteSpace(AllianceNameTextBox.Text);
        AllianceNameTextBox.IsEnabled = !busy;

        var hasConnectingAccounts = _bulkConnectRunning || _disconnectAllRunning
                                    || _accounts.Any(account => account.IsConnecting);
        CustomServerHostTextBox.IsEnabled = !_customServerEnabled && !hasConnectingAccounts;
        CustomServerButton.IsEnabled = !hasConnectingAccounts
                                       && (_customServerEnabled || !string.IsNullOrWhiteSpace(CustomServerHostTextBox.Text));
        CustomServerButton.Content = Localizer.Instance[
            _customServerEnabled ? "UsingCustomServer" : "UseCustomServer"];
        CustomServerButton.Background = _customServerEnabled
            ? FindBrush("AccentLightBrush")
            : FindBrush("SurfaceBrush");
        CustomServerButton.Foreground = _customServerEnabled
            ? FindBrush("AccentLightTextBrush")
            : FindBrush("InkBrush");
        CustomServerButton.BorderBrush = _customServerEnabled
            ? FindBrush("AccentBrush")
            : FindBrush("LineStrongBrush");

        AutoBountyButton.IsEnabled = _accounts.Count > 0;
        AutoBountyButton.Classes.Set("active", _autoBountyEnabled);
        AutoBountyButton.Content = Localizer.Instance[
            _autoBountyEnabled ? "AutoBountyOn" : "AutoBounty"];

        var total = _accounts.Count;
        ConnectionSummary.Text = total == 0
            ? Localize("No accounts yet", "هنوز اکانتی نیست")
            : Localize($"{total} accounts · {connectedAccounts.Length} online",
                $"{total} اکانت · {connectedAccounts.Length} متصل");

        AccountsSummary.Text = total == 0
            ? string.Empty
            : Localizer.Instance.Format("AccountsSummary", total, connectedAccounts.Length,
                connectingCount, total - connectedAccounts.Length - connectingCount);
    }

    private void UpdateStatusLine()
    {
        if (!string.IsNullOrEmpty(_statusMessage))
        {
            StatusLine.Text = _statusMessage;
            StatusDot.Background = _statusLevel switch
            {
                "error" => FindBrush("DangerTextBrush"),
                "warn" => FindBrush("WarnBrush"),
                "success" => FindBrush("AccentBrush"),
                _ => FindBrush("MutedBrush")
            };
            return;
        }

        StatusDot.Background = FindBrush("MutedBrush");
        if (_accounts.Count == 0)
        {
            StatusLine.Text = Localize("Add account JSON files to get started.",
                "برای شروع، فایل JSON اکانت‌ها را اضافه کن.");
            return;
        }

        var connected = _accounts.Count(account => account.IsConnected);
        StatusLine.Text = _customServerEnabled
            ? Localize($"New connections use {_customServerHost}; every account keeps its own port.",
                $"اتصال‌های جدید از {_customServerHost} استفاده می‌کنند و پورت هر اکانت حفظ می‌شود.")
            : connected == 0
                ? Localize("Ready. Connect the accounts when you want to start.",
                    "آماده. برای شروع اکانت‌ها را وصل کن.")
                : Localize($"{connected} of {_accounts.Count} accounts are online.",
                    $"{connected} اکانت از {_accounts.Count} اکانت متصل است.");
    }

    /// <summary>Shows a message in the status line of the control panel.</summary>
    private void SetStatus(string message, string level = "info")
    {
        _statusMessage = message;
        _statusLevel = level;
        UpdateStatusLine();
    }

    private static IBrush FindBrush(string key) =>
        Application.Current?.TryFindResource(key, out var value) == true && value is IBrush brush
            ? brush
            : Brushes.Transparent;

    private string? GetCustomServerHostForConnection() =>
        _customServerEnabled ? _customServerHost : null;

    private static bool TryNormalizeServerHost(string value, out string host)
    {
        host = value.Trim();
        if (host.Length >= 2 && host[0] == '[' && host[^1] == ']')
            host = host[1..^1];
        if (IPAddress.TryParse(host, out var address))
        {
            host = address.ToString();
            return true;
        }

        return !host.Contains(':') && Uri.CheckHostName(host) == UriHostNameType.Dns;
    }

    private void CustomServerHost_TextChanged(object? sender, TextChangedEventArgs e)
    {
        if (!_customServerEnabled)
            UpdateControls();
    }

    private void AutoBounty_Click(object? sender, RoutedEventArgs e)
    {
        _autoBountyEnabled = !_autoBountyEnabled;
        var targets = _accounts.Where(account => account.IsConnected).ToArray();
        foreach (var account in targets)
        {
            TrySendToBackend(account, new
            {
                action = "bounty_auto",
                account = account.CapturePath,
                enabled = _autoBountyEnabled
            });
        }

        if (_autoBountyEnabled)
        {
            var message = targets.Length == 0
                ? Localize(
                    "Bounty auto start is on; it will begin when accounts connect. Each match uses 1 troop and 1 hero.",
                    "شروع خودکار بازی Bounty روشن است؛ با اتصال اکانت‌ها آغاز می‌شود. هر بازی با ۱ نیرو و ۱ قهرمان انجام می‌شود.")
                : Localize(
                    $"Bounty auto start enabled for {targets.Length} connected account(s); each match uses 1 troop and 1 hero.",
                    $"شروع خودکار Bounty برای {targets.Length} اکانت متصل فعال شد؛ هر بازی با ۱ نیرو و ۱ قهرمان انجام می‌شود.");
            SetStatus(message, targets.Length == 0 ? "warn" : "success");
            AddLog(message, targets.Length == 0 ? "warn" : "success");
        }
        else
        {
            var message = Localize("Bounty auto start is off. Matches already in progress are left untouched.",
                "شروع خودکار Bounty خاموش شد؛ بازی‌هایی که در جریان هستند دست‌نخورده می‌مانند.");
            SetStatus(message);
            AddLog(message);
        }

        UpdateControls();
    }

    private void CustomServer_Click(object? sender, RoutedEventArgs e)
    {
        if (_customServerEnabled)
        {
            _customServerEnabled = false;
            _customServerHost = null;
            SetStatus(Localize(
                "Custom server is off. New connections will use each account's normal server route.",
                "سرور سفارشی خاموش شد؛ اتصال‌های جدید از مسیر عادی هر اکانت استفاده می‌کنند."));
            AddLog(Localize("Custom server override disabled for new connections.",
                "تغییر سرور برای اتصال‌های جدید غیرفعال شد."));
        }
        else
        {
            if (!TryNormalizeServerHost(CustomServerHostTextBox.Text ?? string.Empty, out var host))
            {
                SetStatus(Localize(
                    "Enter a valid hostname or IP address first. Do not include a port.",
                    "ابتدا نام میزبان یا IP معتبر را وارد کن؛ پورت را وارد نکن."), "warn");
                AddLog(StatusLine.Text ?? string.Empty, "warn");
                CustomServerHostTextBox.Focus();
                return;
            }

            _customServerHost = host;
            _customServerEnabled = true;
            var connectedCount = _accounts.Count(account => account.IsConnected);
            SetStatus(Localize(
                $"Using {host} for new connections; each account keeps its JSON port. Disconnect {connectedCount} already-connected account(s) and reconnect them to switch.",
                $"برای اتصال‌های جدید از {host} استفاده می‌شود و پورت JSON هر اکانت حفظ می‌شود. برای تغییر {connectedCount} اکانت متصل، آن‌ها را قطع و دوباره وصل کن."), "warn");
            AddLog(Localize("Custom server enabled for new connections; each account's JSON port will be kept.",
                "سرور سفارشی برای اتصال‌های جدید فعال شد؛ پورت JSON هر اکانت حفظ می‌شود."));
        }

        UpdateControls();
    }

    // ---- bulk actions ------------------------------------------------------

    private async void Connect_Click(object? sender, RoutedEventArgs e)
    {
        if (_bulkConnectRunning || _guildActionRunning || _disconnectAllRunning)
            return;
        if (_accounts.Count == 0)
        {
            AddLog(Localize("Add at least one account JSON file first.",
                "ابتدا حداقل یک فایل JSON اکانت اضافه کن."), "warn");
            return;
        }

        if (_accounts.Any(account => account.IsConnecting))
            return;
        var pending = _accounts.Where(account => !account.IsConnected).ToArray();
        if (pending.Length == 0)
        {
            AddLog(Localize("All accounts are already connected.",
                "همهٔ اکانت‌ها از قبل متصل هستند."), "info");
            return;
        }

        _bulkConnectRunning = true;
        AccountCards.IsEnabled = false;
        RefreshUi();
        var customServerHost = GetCustomServerHostForConnection();
        try
        {
            foreach (var account in pending)
            {
                SetStatus(Localize($"Connecting {account.Name} ({_accounts.Count(a => a.IsConnected)}/{pending.Length})…",
                    $"در حال اتصال {account.Name} ({_accounts.Count(a => a.IsConnected)}/{pending.Length})…"));
                await ConnectAccountAsync(account, customServerHost);
            }
        }
        finally
        {
            _bulkConnectRunning = false;
            AccountCards.IsEnabled = true;
            RefreshUi();
        }
    }

    private async void DisconnectAll_Click(object? sender, RoutedEventArgs e)
    {
        if (_bulkConnectRunning || _guildActionRunning || _disconnectAllRunning)
            return;
        var connectedAccounts = _accounts.Where(account => account.IsConnected).ToArray();
        if (connectedAccounts.Length == 0)
        {
            AddLog(Localize("No connected accounts to disconnect.",
                "اکانت متصلی برای قطع اتصال وجود ندارد."), "warn");
            return;
        }

        var confirmed = await Dialogs.ConfirmAsync(this,
            Localize("Disconnect all connected", "قطع اتصال همهٔ متصل‌ها"),
            Localize(
                $"Disconnect all {connectedAccounts.Length} connected account(s), waiting a random 1-5 second delay before each?",
                $"اتصال همهٔ {connectedAccounts.Length} اکانت متصل قطع شود؟ پیش از هر مورد، تأخیر تصادفی ۱ تا ۵ ثانیه اعمال می‌شود."),
            Localizer.Instance["DialogDisconnect"], Localizer.Instance["DialogCancel"]);
        if (!confirmed)
            return;

        _disconnectAllRunning = true;
        AccountCards.IsEnabled = false;
        RefreshUi();
        try
        {
            for (var index = 0; index < connectedAccounts.Length; index++)
            {
                var account = connectedAccounts[index];
                if (!account.IsConnected)
                    continue;
                var delay = RandomHumanDelay();
                SetStatus(Localize(
                    $"Disconnecting {index + 1}/{connectedAccounts.Length} — {account.Name} in {delay.TotalSeconds:0.0}s",
                    $"قطع اتصال {index + 1}/{connectedAccounts.Length} — {account.Name} تا {delay.TotalSeconds:0.0} ثانیه"));
                await Task.Delay(delay);
                DisconnectAccount(account);
                AddLog(Localize($"Disconnected {account.Name}.",
                    $"{account.Name} قطع شد."), "success");
            }
        }
        finally
        {
            _disconnectAllRunning = false;
            AccountCards.IsEnabled = true;
            RefreshUi();
        }
    }

    private void EnableAllianceHelpForConnectedAccounts()
    {
        // An account does not have to wait for every other account: as soon as
        // it is in the world, its own alliance help can run. The backend keeps
        // the state per account and pauses it while the account is guildless.
        var toEnable = _accounts
            .Where(account => account.IsConnected && !account.AllianceHelpEnabled)
            .ToArray();
        if (toEnable.Length == 0)
            return;

        var enabled = 0;
        var failed = 0;
        foreach (var account in toEnable)
        {
            if (TrySendToBackend(account, new
                {
                    action = "alliance_auto_help",
                    account = account.CapturePath,
                    enabled = true
                }))
            {
                enabled++;
            }
            else
            {
                failed++;
            }
        }

        var summary = Localize(
            $"Alliance Auto Help enabled for {enabled} connected account(s).",
            $"کمک خودکار اتحاد برای {enabled} اکانت متصل فعال شد.");
        if (failed > 0)
            summary += Localize($" {failed} account(s) failed.", $" {failed} اکانت ناموفق بود.");
        SetStatus(summary, failed == 0 ? "success" : "warn");
        AddLog(summary, failed == 0 ? "success" : "warn");
    }

    private void AllianceName_TextChanged(object? sender, TextChangedEventArgs e)
    {
        if (IsLoaded)
            UpdateControls();
    }

    private static TimeSpan RandomHumanDelay() =>
        TimeSpan.FromSeconds(1.0 + Random.Shared.NextDouble() * 4.0);

    private async void LeaveAlliance_Click(object? sender, RoutedEventArgs e)
    {
        if (_guildActionRunning || _bulkConnectRunning || _disconnectAllRunning ||
            _accounts.Any(account => account.IsConnecting))
        {
            return;
        }

        var connectedAccounts = _accounts.Where(account => account.IsConnected).ToArray();
        if (connectedAccounts.Length == 0)
            return;

        var confirmed = await Dialogs.ConfirmAsync(this,
            Localize("Confirm leave alliance", "تأیید خروج از اتحاد"),
            Localize(
                $"Send a real leave-alliance request to {connectedAccounts.Length} connected account(s), one at a time with a random 1-5 second delay? This cannot be undone.",
                $"برای {connectedAccounts.Length} اکانت متصل، درخواست واقعی خروج از اتحاد ارسال شود؟ عملیات یکی‌یکی و با تأخیر تصادفی ۱ تا ۵ ثانیه انجام می‌شود و قابل بازگشت نیست."),
            Localizer.Instance["DialogLeave"], Localizer.Instance["DialogCancel"]);
        if (!confirmed)
            return;

        await RunGuildActionAsync(connectedAccounts, "leave", null);
    }

    private async void JoinAlliance_Click(object? sender, RoutedEventArgs e)
    {
        if (_guildActionRunning || _bulkConnectRunning || _disconnectAllRunning ||
            _accounts.Any(account => account.IsConnecting))
        {
            return;
        }

        var connectedAccounts = _accounts.Where(account => account.IsConnected).ToArray();
        if (connectedAccounts.Length == 0)
            return;
        var guildName = AllianceNameTextBox.Text?.Trim() ?? string.Empty;
        if (guildName.Length == 0)
        {
            AddLog(Localize("Enter an Alliance TAG, optionally in [TAG]Name format.",
                "TAG اتحاد را وارد کن؛ در صورت تمایل با قالب [TAG]Name."), "warn");
            AllianceNameTextBox.Focus();
            return;
        }

        var confirmed = await Dialogs.ConfirmAsync(this,
            Localize("Confirm join alliance", "تأیید پیوستن به اتحاد"),
            Localize(
                $"Search for Alliance TAG/name “{guildName}” and send a join request to {connectedAccounts.Length} connected account(s), one at a time with a random 1-5 second delay? The server may require approval.",
                $"اتحاد با TAG/نام «{guildName}» جست‌وجو و درخواست پیوستن برای {connectedAccounts.Length} اکانت متصل، یکی‌یکی و با تأخیر تصادفی ۱ تا ۵ ثانیه ارسال شود؟ ممکن است سرور تأیید مدیر اتحاد را لازم بدارد."),
            Localizer.Instance["DialogJoin"], Localizer.Instance["DialogCancel"]);
        if (!confirmed)
            return;

        await RunGuildActionAsync(connectedAccounts, "join_by_name", guildName);
    }

    private async Task RunGuildActionAsync(
        IReadOnlyList<AccountSession> accounts, string action, string? guildName)
    {
        var actionLabel = action == "leave"
            ? Localize("leave", "خروج")
            : Localize("join", "پیوستن");
        _guildActionRunning = true;
        AccountCards.IsEnabled = false;
        RefreshUi();
        var succeeded = 0;
        var failed = 0;
        try
        {
            for (var index = 0; index < accounts.Count; index++)
            {
                var account = accounts[index];
                if (!account.IsConnected)
                {
                    failed++;
                    AddLog(Localize($"{account.Name} disconnected before its alliance action.",
                        $"{account.Name} پیش از عملیات اتحاد قطع شد."), "warn");
                    continue;
                }

                if (_bridge is not { IsRunning: true })
                {
                    failed++;
                    AddLog(Localize($"Could not process {account.Name}: the shared backend is not running.",
                        $"عملیات {account.Name} انجام نشد: بک‌اند مشترک اجرا نیست."), "error");
                    continue;
                }

                var delay = RandomHumanDelay();
                SetStatus(Localize(
                    $"{(action == "leave" ? "Leaving" : "Joining")} alliance in {delay.TotalSeconds:0.0}s: {index + 1}/{accounts.Count} — {account.Name}",
                    $"{(action == "leave" ? "خروج" : "پیوستن")} به اتحاد تا {delay.TotalSeconds:0.0} ثانیه دیگر: {index + 1}/{accounts.Count} — {account.Name}"));
                AddLog(Localize(
                    $"{account.Name}: waiting {delay.TotalSeconds:0.0}s before its alliance {actionLabel} request.",
                    $"{account.Name}: {delay.TotalSeconds:0.0} ثانیه پیش از درخواست {actionLabel} اتحاد صبر می‌کنم."));
                await Task.Delay(delay);

                if (!account.IsConnected)
                {
                    failed++;
                    AddLog(Localize($"{account.Name} disconnected before its alliance action.",
                        $"{account.Name} پیش از عملیات اتحاد قطع شد."), "warn");
                    continue;
                }

                var completion = new TaskCompletionSource<GuildActionResult>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                account.GuildActionAttempt = completion;
                SetStatus(Localize(
                    $"{(action == "leave" ? "Leaving" : "Joining")} alliance: {index + 1}/{accounts.Count} — {account.Name}",
                    $"عملیات {actionLabel} به اتحاد: {index + 1}/{accounts.Count} — {account.Name}"));
                try
                {
                    var sent = action == "leave"
                        ? TrySendToBackend(account, new { action = "guild_leave", account = account.CapturePath })
                        : TrySendToBackend(account,
                            new { action = "guild_join_by_name", account = account.CapturePath, guild_name = guildName });
                    if (!sent)
                    {
                        failed++;
                        continue;
                    }

                    var result = await completion.Task.WaitAsync(TimeSpan.FromSeconds(30));
                    if (result.Success)
                    {
                        succeeded++;
                        var message = Localize(
                            $"{account.Name}: alliance {actionLabel}" +
                            (string.IsNullOrWhiteSpace(result.GuildName)
                                ? " confirmed by the game server"
                                : $" with {result.GuildName}") +
                            (result.GuildId is > 0 ? $" (ID {result.GuildId})." : "."),
                            $"{account.Name}: عملیات {actionLabel} اتحاد" +
                            (string.IsNullOrWhiteSpace(result.GuildName)
                                ? " توسط سرور بازی تأیید شد"
                                : $" برای {result.GuildName}") +
                            (result.GuildId is > 0 ? $" (شناسه {result.GuildId})." : "."));
                        AddLog(message, "success");
                    }
                    else
                    {
                        failed++;
                        AddLog(Localize(
                            $"{account.Name}: alliance {actionLabel} failed: {result.Error}",
                            $"{account.Name}: عملیات {actionLabel} اتحاد ناموفق بود: {result.Error}"),
                            "error");
                    }
                }
                catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or IOException)
                {
                    failed++;
                    AddLog(Localize(
                        $"{account.Name}: alliance {actionLabel} result is unknown: {ex.Message}. Do not retry until you verify its in-game state.",
                        $"{account.Name}: نتیجهٔ عملیات {actionLabel} اتحاد نامشخص است: {ex.Message}. تا بررسی وضعیت داخل بازی، دوباره تلاش نکن."),
                        "error");
                }
                finally
                {
                    account.GuildActionAttempt = null;
                }
            }
        }
        finally
        {
            _guildActionRunning = false;
            AccountCards.IsEnabled = true;
            var summary = Localize(
                $"Alliance {actionLabel} finished: {succeeded} confirmed, {failed} failed or unknown.",
                $"عملیات {actionLabel} اتحاد تمام شد: {succeeded} تأییدشده، {failed} ناموفق یا نامشخص.");
            SetStatus(summary, failed == 0 ? "success" : "warn");
            AddLog(summary, failed == 0 ? "success" : "warn");
            RefreshUi();
        }
    }

    // ---- backend events ----------------------------------------------------

    private void HandleBackendEvent(JsonElement message)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (!message.TryGetProperty("type", out var typeValue))
                return;
            var type = typeValue.GetString();
            if (type == "ready")
            {
                AddLog(Localize("Shared backend service ready.",
                    "بک‌اند مشترک آماده است."));
                return;
            }

            if (type == "pong")
                return;

            var accountId = GetString(message, "account");
            var account = string.IsNullOrEmpty(accountId)
                ? null
                : _accounts.FirstOrDefault(candidate =>
                    string.Equals(candidate.CapturePath, accountId, StringComparison.OrdinalIgnoreCase));

            if (account is null)
            {
                // A global backend message that is not tied to one account.
                if (type == "error")
                    AddLog(GetString(message, "message") ?? "Backend error", "error");
                else if (type == "log")
                    AddLog(GetString(message, "message") ?? "", GetString(message, "level") ?? "info");
                return;
            }

            var prefix = $"{account.Name}: ";
            switch (type)
            {
                case "connection":
                    account.IsConnected = message.TryGetProperty("connected", out var connected) &&
                                         connected.GetBoolean();
                    account.IsConnecting = false;
                    account.ConnectionAttempt?.TrySetResult(account.IsConnected);
                    if (!account.IsConnected)
                    {
                        account.AllianceHelpEnabled = false;
                        account.AllianceHelpState = "off";
                        account.AllianceHelpPendingCount = 0;
                        account.AllianceHelpErrorCode = 0;
                        account.GuildKnown = false;
                        account.InGuild = false;
                        account.GuildName = string.Empty;
                        account.ResetPlayerBase();
                        account.ResetBag();
                        account.ResetTradingPost();
                        if (IsTransportWindowOf(account))
                            _transportWindow!.ApplySnapshotMissing();
                        account.BountyMode = "off";
                        account.SetAutoSpin(false, "off");
                    }
                    else if (message.TryGetProperty("guild_known", out var guildKnown) &&
                             guildKnown.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    {
                        account.GuildKnown = guildKnown.GetBoolean();
                        account.InGuild = message.TryGetProperty("in_guild", out var inGuild) &&
                                          inGuild.ValueKind == JsonValueKind.True;
                        account.GuildName = GetString(message, "guild_name") ?? string.Empty;
                    }

                    if (message.TryGetProperty("player_id", out var player) &&
                        player.ValueKind == JsonValueKind.Number)
                    {
                        AddLog(prefix + Localize($"Login accepted. Player ID: {player.GetInt64()}",
                            $"ورود پذیرفته شد. Player ID: {player.GetInt64()}"), "success");
                    }

                    if (GetString(message, "message") is { Length: > 0 } statusMessage)
                        AddLog(prefix + statusMessage, account.IsConnected ? "info" : "warn");
                    break;
                case "alliance_help_state":
                    account.AllianceHelpEnabled = message.TryGetProperty("enabled", out var helpEnabled) &&
                                                  helpEnabled.GetBoolean();
                    account.AllianceHelpState = GetString(message, "state") ?? "off";
                    account.AllianceHelpPendingCount = message.TryGetProperty("pending_count", out var pendingCount)
                        ? pendingCount.GetInt32() : 0;
                    account.AllianceHelpErrorCode = message.TryGetProperty("error_code", out var errorCode)
                        ? errorCode.GetInt32() : 0;
                    if (message.TryGetProperty("total_helps", out var totalHelps) &&
                        totalHelps.ValueKind == JsonValueKind.Number && totalHelps.TryGetUInt64(out var helped))
                    {
                        account.SetHelpCount(helped);
                    }

                    LogAllianceHelpState(account, message, prefix);
                    break;
                case "player_base":
                    if (message.TryGetProperty("known", out var baseKnown) &&
                        baseKnown.ValueKind == JsonValueKind.True)
                    {
                        account.SetPlayerBase(
                            GetString(message, "name"),
                            GetNumber(message, "level"),
                            GetNumber(message, "might"),
                            GetNumber(message, "food"),
                            GetNumber(message, "wood"),
                            GetNumber(message, "steel"),
                            GetNumber(message, "oil"));
                    }
                    else
                    {
                        account.ResetPlayerBase();
                    }

                    break;
                case "bounty_state":
                {
                    var bountyState = GetString(message, "state") ?? "off";
                    var bountyEnabled = message.TryGetProperty("enabled", out var bountyEnabledValue) &&
                                        bountyEnabledValue.ValueKind == JsonValueKind.True;
                    account.BountyMode = bountyState == "unavailable"
                        ? "unavailable"
                        : bountyEnabled ? "on" : "off";
                    if (bountyState == "unavailable")
                    {
                        AddLog(prefix + Localize(
                            "Bounty OFF for this account (error 14000); it is not retried, the other accounts continue.",
                            "Bounty برای این اکانت خاموش شد (خطای 14000)؛ دوباره تلاش نمی‌شود و بقیهٔ اکانت‌ها ادامه می‌دهند."),
                            "error");
                    }

                    break;
                }
                case "bounty_spin":
                {
                    var spinState = GetString(message, "state") ?? "off";
                    var spinEnabled = message.TryGetProperty("enabled", out var spinEnabledValue) &&
                                      spinEnabledValue.ValueKind == JsonValueKind.True;
                    account.SetAutoSpin(spinEnabled, spinState);
                    if (spinState == "insufficient")
                    {
                        AddLog(prefix + Localize(
                            "Bounty wheel stopped: not enough Bounty Tickets for one spin (100).",
                            "گردونه متوقف شد: Bounty Ticket کافی برای یک دور (۱۰۰) نیست."), "error");
                    }
                    else if (spinState == "error")
                    {
                        var code = GetNumber(message, "error_code");
                        AddLog(prefix + Localize(
                            $"Bounty wheel stopped after the game server error {code}.",
                            $"گردونه پس از خطای {code} سرور بازی متوقف شد."), "error");
                    }

                    break;
                }
                case "bounty_turntable":
                    // Only a routing detail of the wheel; nothing to draw.
                    return;
                case "bounty_tickets":
                    if (message.TryGetProperty("known", out var ticketsKnown) && ticketsKnown.GetBoolean() &&
                        message.TryGetProperty("tickets", out var ticketValue) &&
                        ticketValue.TryGetUInt64(out var tickets))
                    {
                        var lastReward = message.TryGetProperty("last_reward", out var rewardValue) &&
                                         rewardValue.TryGetUInt64(out var reward)
                            ? reward
                            : 0UL;
                        account.SetBountyTickets(tickets, lastReward);
                    }
                    else
                    {
                        account.ResetBountyTickets();
                    }

                    break;
                case "guild_state":
                    account.GuildKnown = !message.TryGetProperty("known", out var known) ||
                                         known.ValueKind != JsonValueKind.False;
                    account.InGuild = message.TryGetProperty("in_guild", out var guildIn) &&
                                      guildIn.ValueKind == JsonValueKind.True;
                    account.GuildName = GetString(message, "guild_name") ?? string.Empty;
                    break;
                case "guild_action_result":
                {
                    ulong? actionGuildId = null;
                    if (message.TryGetProperty("guild_id", out var guildIdValue) &&
                        guildIdValue.ValueKind == JsonValueKind.Number &&
                        guildIdValue.TryGetUInt64(out var receivedGuildId))
                    {
                        actionGuildId = receivedGuildId;
                    }

                    account.GuildActionAttempt?.TrySetResult(new GuildActionResult(
                        message.TryGetProperty("success", out var actionSuccess) &&
                        actionSuccess.GetBoolean(),
                        GetString(message, "error"),
                        actionGuildId,
                        GetString(message, "guild_name")));
                    break;
                }
                case "bag_items":
                {
                    var bagListKnown = message.TryGetProperty("known", out var bagKnownValue) &&
                                       bagKnownValue.ValueKind == JsonValueKind.True;
                    var parsed = new List<(uint Id, ulong Count)>();
                    if (message.TryGetProperty("items", out var itemsValue) &&
                        itemsValue.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var entry in itemsValue.EnumerateArray())
                        {
                            var entryId = GetNumber(entry, "id");
                            if (entryId is > 0 and <= uint.MaxValue)
                                parsed.Add(((uint)entryId, GetNumber(entry, "count")));
                        }
                    }

                    account.SetBagItems(parsed, bagListKnown);
                    // A bag refresh changes nothing on the card, so skip RefreshUi.
                    return;
                }
                case "bag_item":
                {
                    var changedId = GetNumber(message, "id");
                    if (changedId is > 0 and <= uint.MaxValue)
                        account.UpdateBagItem((uint)changedId, GetNumber(message, "count"));
                    return;
                }
                case "item_use_result":
                {
                    var success = message.TryGetProperty("success", out var successValue) &&
                                  successValue.ValueKind == JsonValueKind.True;
                    var usedId = GetNumber(message, "id");
                    var usedCount = GetNumber(message, "count");
                    var itemName = (usedId is > 0 and <= uint.MaxValue
                                       ? ItemCatalog.Find((uint)usedId)?.Name
                                       : null)
                                   ?? (usedId > 0 ? $"item {usedId}" : Localize("item", "آیتم"));
                    if (success)
                    {
                        AddLog(prefix + Localize(
                            $"Bag use accepted: {usedCount:N0} × {itemName}.",
                            $"استفاده از کیف تأیید شد: {usedCount:N0} عدد {itemName}."), "success");
                    }
                    else if (GetString(message, "message") is { Length: > 0 } failure)
                    {
                        AddLog(prefix + failure, "warn");
                    }
                    else
                    {
                        AddLog(prefix + Localize(
                            $"The game server rejected the bag use of {itemName} (error {GetNumber(message, "error_code")}).",
                            $"سرور بازی استفاده از {itemName} را نپذیرفت (خطای {GetNumber(message, "error_code")})."), "error");
                    }

                    return;
                }
                case "transport_info":
                {
                    var transportInfoKnown = message.TryGetProperty("known", out var transportKnown) &&
                                             transportKnown.ValueKind == JsonValueKind.True;
                    if (transportInfoKnown)
                    {
                        var snapshot = ParseTransportSnapshot(message);
                        account.SetTradingPost(snapshot.Level);
                        if (IsTransportWindowOf(account))
                            _transportWindow!.ApplyInfo(snapshot);
                    }
                    else
                    {
                        account.ResetTradingPost();
                        if (IsTransportWindowOf(account))
                            _transportWindow!.ApplySnapshotMissing();
                    }

                    return;
                }
                case "transport_capacity":
                {
                    if (!IsTransportWindowOf(account))
                        return;
                    var capacity = ParseTransportCapacity(message);
                    _transportWindow!.ApplyCapacity(capacity);
                    return;
                }
                case "transport_result":
                {
                    var success = message.TryGetProperty("success", out var transportSuccess) &&
                                  transportSuccess.ValueKind == JsonValueKind.True;
                    var report = ParseTransportReport(message);
                    if (IsTransportWindowOf(account))
                        _transportWindow!.ApplyResult(report);

                    if (success)
                    {
                        var receiver = report.TargetName.Length == 0
                            ? report.TargetId.ToString(System.Globalization.CultureInfo.InvariantCulture)
                            : report.TargetName;
                        AddLog(prefix + Localize(
                            $"Transport accepted: {report.Amount:N0} to {receiver}.",
                            $"ارسال تأیید شد: {report.Amount:N0} برای {receiver}."), "success");
                    }
                    else if (report.Message.Length > 0)
                    {
                        AddLog(prefix + report.Message, "warn");
                    }
                    else
                    {
                        AddLog(prefix + Localize(
                            $"The game server refused the transport (error {report.ErrorCode}).",
                            $"سرور بازی ارسال را رد کرد (خطای {report.ErrorCode})."), "warn");
                    }

                    return;
                }
                case "log":
                    AddLog(prefix + (GetString(message, "message") ?? ""), GetString(message, "level") ?? "info");
                    // A log line changes nothing on the cards, so skip RefreshUi.
                    return;
                case "error":
                    if (account.IsConnecting)
                    {
                        account.IsConnecting = false;
                        account.ConnectionAttempt?.TrySetResult(false);
                    }

                    AddLog(prefix + (GetString(message, "message") ?? "Backend error"), "error");
                    break;
                case "notice":
                    AddLog(prefix + Localize($"Received {GetString(message, "name")} from server.",
                        $"دریافت {GetString(message, "name")} از سرور."));
                    return;
            }

            RefreshUi();
        });
    }

    private bool IsTransportWindowOf(AccountSession account) =>
        _transportWindow is { } window && ReferenceEquals(window.Account, account);

    private static TransportSnapshot ParseTransportSnapshot(JsonElement message)
    {
        var targets = new List<TransportTarget>();
        if (message.TryGetProperty("targets", out var roster) && roster.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in roster.EnumerateArray())
            {
                var playerId = GetNumber(entry, "player_id");
                if (playerId == 0)
                    continue;
                targets.Add(new TransportTarget
                {
                    PlayerId = playerId,
                    EntityId = GetNumber(entry, "entity_id"),
                    Name = GetString(entry, "name") ?? string.Empty,
                    Guild = GetString(entry, "guild") ?? string.Empty
                });
            }
        }

        return new TransportSnapshot
        {
            Known = message.TryGetProperty("known", out var known) && known.ValueKind == JsonValueKind.True,
            Connected = message.TryGetProperty("connected", out var connected) &&
                        connected.ValueKind == JsonValueKind.True,
            Level = (uint)GetNumber(message, "level"),
            PlayerName = GetString(message, "player_name") ?? string.Empty,
            Food = GetNumber(message, "food"),
            Wood = GetNumber(message, "wood"),
            Steel = GetNumber(message, "steel"),
            Oil = GetNumber(message, "oil"),
            PerSend = GetNumber(message, "per_send"),
            DailySupply = GetNumber(message, "daily_supply"),
            DailyReceive = GetNumber(message, "daily_receive"),
            TaxPermille = (int)GetNumber(message, "tax_permille"),
            SpeedPermille = (int)GetNumber(message, "speed_permille"),
            Targets = targets
        };
    }

    private static TransportCapacity ParseTransportCapacity(JsonElement message)
    {
        TransportLimit? self = null;
        TransportLimit? target = null;
        var selfId = GetNumber(message, "self_id");
        var targetId = GetNumber(message, "target_id");
        var fallback = new List<TransportLimit>();
        if (message.TryGetProperty("entries", out var entries) && entries.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in entries.EnumerateArray())
            {
                var limit = new TransportLimit
                {
                    PlayerId = GetNumber(entry, "player_id"),
                    Level = (uint)GetNumber(entry, "level"),
                    SentToday = GetNumber(entry, "sent_today"),
                    ReceivedToday = GetNumber(entry, "received_today"),
                    PerSend = GetNumber(entry, "per_send"),
                    DailySupply = GetNumber(entry, "daily_supply"),
                    DailyReceive = GetNumber(entry, "daily_receive"),
                    TaxPermille = (int)GetNumber(entry, "tax_permille")
                };

                if (selfId != 0 && limit.PlayerId == selfId)
                    self = limit;
                else if (targetId != 0 && limit.PlayerId == targetId)
                    target = limit;
                else
                    fallback.Add(limit);
            }
        }

        // Without the ids in the message the server order is the only hint: it
        // lists the sender of the request first.
        foreach (var limit in fallback)
        {
            if (self is null)
                self = limit;
            else if (target is null)
                target = limit;
        }

        return new TransportCapacity
        {
            ErrorCode = (int)GetNumber(message, "error_code"),
            Self = self,
            Target = target
        };
    }

    private static TransportReport ParseTransportReport(JsonElement message) => new()
    {
        Success = message.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.True,
        ErrorCode = (int)GetNumber(message, "error_code"),
        Reason = GetString(message, "reason") ?? string.Empty,
        Message = GetString(message, "message") ?? string.Empty,
        Amount = GetNumber(message, "amount"),
        SubType = (uint)GetNumber(message, "sub_type"),
        TargetId = GetNumber(message, "target_id"),
        TargetName = GetString(message, "target_name") ?? string.Empty,
        TaxPermille = (int)GetNumber(message, "tax_permille")
    };

    private void LogAllianceHelpState(AccountSession account, JsonElement message, string prefix)
    {
        switch (GetString(message, "state"))
        {
            case "help_sent":
                AddLog(prefix + Localize("Alliance Auto Help request sent.",
                    "درخواست کمک خودکار اتحاد ارسال شد."));
                break;
            case "help_confirmed":
                AddLog(prefix + Localize("Server confirmed Alliance Auto Help.",
                    "سرور کمک خودکار اتحاد را تأیید کرد."), "success");
                break;
            case "help_rejected":
                AddLog(prefix + Localize(
                    $"The game server rejected the help request (error {GetString(message, "error_code")}).",
                    $"سرور بازی درخواست کمک را نپذیرفت (خطای {GetString(message, "error_code")})."), "warn");
                break;
            case "error":
                AddLog(prefix + Localize("Alliance Auto Help stopped after an error.",
                    "کمک خودکار اتحاد پس از خطا متوقف شد."), "error");
                break;
        }
    }

    private static ulong GetNumber(JsonElement message, string property)
    {
        if (!message.TryGetProperty(property, out var value))
            return 0;
        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetUInt64(out var number) => number,
            JsonValueKind.String when ulong.TryParse(value.GetString(), out var number) => number,
            _ => 0
        };
    }

    private static string? GetString(JsonElement message, string property)
    {
        if (!message.TryGetProperty(property, out var value))
            return null;
        if (value.ValueKind == JsonValueKind.Null)
            return null;
        return value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
    }

    // ---- console -----------------------------------------------------------

    private const int ConsoleLineLimit = 500;
    private const int DebugConsoleLineLimit = 2000;
    private const int ConsoleLinesPerFlush = 120;

    private void ClearConsole_Click(object? sender, RoutedEventArgs e)
    {
        _pendingLogs.Clear();
        _logFlushTimer.Stop();
        _console.Clear();
        UpdateConsoleSummary();
    }

    /// <summary>
    /// Queues one console line. Filtering happens here, so a level that does
    /// not want the line costs nothing; the visual list is only touched by
    /// <see cref="FlushConsoleQueue"/>, which appends a burst of lines from every
    /// account in one batch instead of one layout pass per line.
    /// </summary>
    internal void AddLog(string message, string level = "info")
    {
        if (string.IsNullOrWhiteSpace(message) || !AppLog.Allows(level))
            return;

        _pendingLogs.Enqueue(new ConsoleEntry(DateTime.Now, level, message.Trim()));
        if (!_logFlushTimer.IsEnabled)
            _logFlushTimer.Start();
    }

    private void FlushConsoleQueue()
    {
        if (AppLog.IsOff)
        {
            // Logging was switched off while lines were still queued.
            _pendingLogs.Clear();
            _logFlushTimer.Stop();
            return;
        }

        if (_pendingLogs.Count == 0)
        {
            _logFlushTimer.Stop();
            return;
        }

        for (var added = 0; added < ConsoleLinesPerFlush && _pendingLogs.Count > 0; added++)
            _console.Add(_pendingLogs.Dequeue());

        var limit = AppLog.IsDebug ? DebugConsoleLineLimit : ConsoleLineLimit;
        while (_console.Count > limit)
            _console.RemoveAt(0);

        UpdateConsoleSummary();
        if (ConsoleList.Items.Count > 0)
            ConsoleList.ScrollIntoView(ConsoleList.Items[^1]!);
    }

    private void ConsoleToggle_Click(object? sender, RoutedEventArgs e)
    {
        _consoleVisible = !_consoleVisible;
        ApplyConsoleVisibility();
    }

    /// <summary>
    /// Shows or hides the console panel. Hiding it takes the whole row to zero,
    /// so the window does not keep an empty strip on the bottom.
    /// </summary>
    private void ApplyConsoleVisibility()
    {
        var visible = _consoleVisible;
        RootGrid.RowDefinitions[3].Height = visible ? GridLength.Auto : new GridLength(0);
        RootGrid.RowDefinitions[4].Height = visible ? new GridLength(190) : new GridLength(0);
        RootGrid.RowDefinitions[4].MinHeight = visible ? 100 : 0;
        ConsoleSplitter.IsVisible = visible;
        ConsolePanel.IsVisible = visible;
        ConsoleToggleButton.Content = Localizer.Instance[visible ? "ConsoleHide" : "ConsoleShow"];
    }

    private void LogLevel_Click(object? sender, RoutedEventArgs e)
    {
        SetLogLevel(AppLog.Level switch
        {
            LogVerbosity.Off => LogVerbosity.Normal,
            LogVerbosity.Normal => LogVerbosity.Debug,
            _ => LogVerbosity.Off
        });
    }

    private void SetLogLevel(LogVerbosity level)
    {
        AppLog.Level = level;
        AppLog.Save();
        UpdateLogLevelButton();

        if (level == LogVerbosity.Off)
        {
            _pendingLogs.Clear();
            _logFlushTimer.Stop();
            // Off means no console at all, not an empty one; the footer button
            // brings the panel back.
            _consoleVisible = false;
            ApplyConsoleVisibility();
            SetStatus(Localize("Logging is off: no log work is done for any account.",
                "لاگ خاموش است: برای هیچ اکانتی کار لاگ انجام نمی‌شود."));
            return;
        }

        _consoleVisible = true;
        ApplyConsoleVisibility();

        var message = level == LogVerbosity.Debug
            ? Localize("Log level: Debug. Every game message the server sends is written to the console.",
                "سطح لاگ: کامل. هر پیام بازی که سرور می‌فرستد در کنسول نوشته می‌شود.")
            : Localize("Log level: Normal. Events are logged; per-message game traffic is not.",
                "سطح لاگ: معمولی. رویدادها لاگ می‌شوند؛ ترافیک پیام‌به‌پیام بازی نه.");
        SetStatus(message);
        AddLog(message, "success");
    }

    private void UpdateLogLevelButton()
    {
        LogLevelButton.Content = AppLog.Level switch
        {
            LogVerbosity.Off => Localizer.Instance["LogLevelOff"],
            LogVerbosity.Debug => Localizer.Instance["LogLevelDebug"],
            _ => Localizer.Instance["LogLevelNormal"]
        };
        LogLevelButton.Classes.Set("active", AppLog.Level == LogVerbosity.Debug);
    }

    private void UpdateConsoleSummary()
    {
        ConsoleEmptyHint.IsVisible = _console.Count == 0;
        ConsoleSummary.Text = _console.Count switch
        {
            0 => string.Empty,
            1 => Localize("1 line", "۱ خط"),
            _ => Localize($"{_console.Count} lines", $"{_console.Count} خط")
        };
    }

    private string Localize(string english, string persian) => Localizer.Instance.T(english, persian);

    // ---- community links ----------------------------------------------------

    private static readonly Uri TelegramGroupUri = new("https://t.me/DoomsdayFTGroup");
    private static readonly Uri YouTubeChannelUri = new("https://www.youtube.com/@DoomsdayFT");

    private async void Telegram_Click(object? sender, RoutedEventArgs e) =>
        await OpenInBrowserAsync(TelegramGroupUri, Localize("Telegram group", "گروه تلگرام"));

    private async void YouTube_Click(object? sender, RoutedEventArgs e) =>
        await OpenInBrowserAsync(YouTubeChannelUri, Localize("YouTube channel", "کانال یوتیوب"));

    /// <summary>
    /// Opens a link with the default browser of the system. The launcher of
    /// the platform is used first, because it is the way the desktop shell
    /// wants it; a shell call is the fallback for the platforms that do not
    /// offer a launcher.
    /// </summary>
    private async Task OpenInBrowserAsync(Uri uri, string name)
    {
        try
        {
            var launcher = TopLevel.GetTopLevel(this)?.Launcher;
            if (launcher is not null && await launcher.LaunchUriAsync(uri))
            {
                AddLog(Localize($"Opened the {name} in the browser: {uri}",
                    $"{name} در مرورگر باز شد: {uri}"), "success");
                return;
            }
        }
        catch (Exception ex)
        {
            AddLog(Localize($"The {name} could not be opened by the launcher: {ex.Message}",
                $"{name} با سرویس سیستم باز نشد: {ex.Message}"), "warn");
        }

        try
        {
            using var browser = Process.Start(new ProcessStartInfo(uri.AbsoluteUri)
            {
                UseShellExecute = true
            });
            if (browser is not null)
            {
                AddLog(Localize($"Opened the {name} in the browser: {uri}",
                    $"{name} در مرورگر باز شد: {uri}"), "success");
                return;
            }
        }
        catch (Exception ex)
        {
            AddLog(Localize($"The {name} could not be opened: {ex.Message}",
                $"{name} باز نشد: {ex.Message}"), "error");
            return;
        }

        AddLog(Localize($"The {name} could not be opened: no browser was found.",
            $"{name} باز نشد: مرورگری پیدا نشد."), "error");
    }

    // ---- keyboard ----------------------------------------------------------

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        // A window only builds its visual tree when it is opened, so the texts
        // of the window are applied again now that every binding is alive.
        ApplyLanguage(_isPersian);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (!e.KeyModifiers.HasFlag(KeyModifiers.Control))
            return;

        switch (e.Key)
        {
            case Key.O:
                ImportAccount_Click(this, new RoutedEventArgs());
                e.Handled = true;
                break;
            case Key.B:
                BuildCapture_Click(this, new RoutedEventArgs());
                e.Handled = true;
                break;
            case Key.L:
                ClearConsole_Click(this, new RoutedEventArgs());
                e.Handled = true;
                break;
        }
    }
}
