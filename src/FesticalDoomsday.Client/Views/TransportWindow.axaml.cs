using System.Globalization;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using FesticalDoomsday.Client.Backend;
using FesticalDoomsday.Client.Models;
using FesticalDoomsday.Client.Services;

namespace FesticalDoomsday.Client.Views;

/// <summary>
/// The Trading Post form of one account: who receives the resources, which of
/// the four city resources is sent, and how much. The amount slider is as long
/// as the account has resources, but never longer than what one transport may
/// carry (Transportation Capacity of the Trading Post level).
/// </summary>
public partial class TransportWindow : Window
{
    private readonly Action<AccountSession, ulong>? _requestCapacity;
    private readonly Action<AccountSession, ulong, uint, ulong>? _send;

    private TransportSnapshot? _snapshot;
    private TransportCapacity? _capacity;
    private ulong _capacityTargetId;
    private ulong _requestedTargetId;
    private uint _subType = Protocol.FoodSubType;
    private ulong _amount;
    private bool _sending;
    private bool _syncing;

    public TransportWindow() : this(null)
    {
    }

    public TransportWindow(AccountSession? account,
        Action<AccountSession, ulong>? requestCapacity = null,
        Action<AccountSession, ulong, uint, ulong>? send = null)
    {
        Account = account;
        _requestCapacity = requestCapacity;
        _send = send;
        InitializeComponent();
        if (account is not null)
            DataContext = account;
        FlowDirection = Localizer.Instance.IsPersian
            ? FlowDirection.RightToLeft
            : FlowDirection.LeftToRight;
        // The form has no window border; it is moved by pressing it anywhere.
        WindowDrag.Attach(this);

        FoodPick.IsChecked = true;
        AmountBox.Text = "0";
        UpdateAmountTexts();
    }

    /// <summary>The account this form belongs to.</summary>
    public AccountSession? Account { get; }

    private static uint ResourceSubType(object? tag) =>
        tag is string text && uint.TryParse(text, out var value) ? value : Protocol.FoodSubType;

    // ---- what the backend reported ----------------------------------------

    /// <summary>The city reply: Trading Post level, its limits and the resources.</summary>
    public void ApplyInfo(TransportSnapshot snapshot)
    {
        _snapshot = snapshot;
        LevelText.Text = !snapshot.Known
            ? "—"
            : snapshot.Level == 0
                ? Localizer.Instance["TransportNoBuildingShort"]
                : Localizer.Instance.Format("TransportLevelShort", snapshot.Level);
        PerSendValue.Text = Number(snapshot.PerSend);
        DailySupplyValue.Text = Number(snapshot.DailySupply);
        TaxRateValue.Text = Percent(snapshot.TaxPermille);
        SpeedValue.Text = Percent(snapshot.SpeedPermille);
        FoodValue.Text = Number(snapshot.Food);
        WoodValue.Text = Number(snapshot.Wood);
        SteelValue.Text = Number(snapshot.Steel);
        OilValue.Text = Number(snapshot.Oil);

        NoBuildingBanner.IsVisible = snapshot.Known && snapshot.Level == 0;
        NoBuildingDetail.Text = Localizer.Instance["TransportNoBuildingHelp"];

        ApplySupplyUsed();
        TargetList.ItemsSource = snapshot.Targets;
        NearbyEmpty.IsVisible = snapshot.Targets.Count == 0;
        RecomputeAmount();
        UpdateSendState();
    }

    /// <summary>The capacity reply of the receiver the form last asked about.</summary>
    public void ApplyCapacity(TransportCapacity capacity)
    {
        _capacity = capacity;
        _capacityTargetId = _requestedTargetId;
        ApplySupplyUsed();
        UpdateTargetStatus(_capacityTargetId);
        RecomputeAmount();
        UpdateSendState();
    }

    /// <summary>Connect or that the account has no Trading Post.</summary>
    public void ApplySnapshotMissing()
    {
        _snapshot = null;
        _capacity = null;
        FoodValue.Text = WoodValue.Text = SteelValue.Text = OilValue.Text = "—";
        PerSendValue.Text = DailySupplyValue.Text = TaxRateValue.Text = SpeedValue.Text = "—";
        LevelText.Text = "—";
        TargetStatus.Text = Localizer.Instance["TransportNeedConnect"];
        TargetBar.Value = 0;
        RecomputeAmount();
        UpdateSendState();
    }

    /// <summary>The outcome of the transport this form sent.</summary>
    public void ApplyResult(TransportReport report)
    {
        _sending = false;
        if (report.Success)
        {
            var name = report.TargetName.Length == 0
                ? report.TargetId.ToString(CultureInfo.InvariantCulture)
                : report.TargetName;
            SetStatus(Localizer.Instance.Format("TransportDone", Number(report.Amount), name), "success");
            if (_snapshot is { } snapshot && report.Amount > 0)
            {
                // The city numbers of the notice are from login time; keep the
                // form honest by taking what was just sent out of them.
                _snapshot = new TransportSnapshot
                {
                    Known = snapshot.Known,
                    Connected = snapshot.Connected,
                    Level = snapshot.Level,
                    PlayerName = snapshot.PlayerName,
                    Food = report.SubType == Protocol.FoodSubType ? Subtract(snapshot.Food, report.Amount) : snapshot.Food,
                    Wood = report.SubType == Protocol.WoodSubType ? Subtract(snapshot.Wood, report.Amount) : snapshot.Wood,
                    Steel = report.SubType == Protocol.SteelSubType ? Subtract(snapshot.Steel, report.Amount) : snapshot.Steel,
                    Oil = report.SubType == Protocol.OilSubType ? Subtract(snapshot.Oil, report.Amount) : snapshot.Oil,
                    PerSend = snapshot.PerSend,
                    DailySupply = snapshot.DailySupply,
                    DailyReceive = snapshot.DailyReceive,
                    TaxPermille = snapshot.TaxPermille,
                    SpeedPermille = snapshot.SpeedPermille,
                    Targets = snapshot.Targets
                };
                FoodValue.Text = Number(_snapshot.Food);
                WoodValue.Text = Number(_snapshot.Wood);
                SteelValue.Text = Number(_snapshot.Steel);
                OilValue.Text = Number(_snapshot.Oil);
                _sentToday += report.Amount;
                // The counters of the Check reply are from before this truck:
                // move them too, or the daily bar and the next amount cap would
                // be computed without what was just sent.
                if (_capacity is { } capacity)
                {
                    _capacity = new TransportCapacity
                    {
                        ErrorCode = capacity.ErrorCode,
                        Self = capacity.Self is { } self
                            ? self with { SentToday = self.SentToday + report.Amount }
                            : null,
                        Target = capacity.Target is { } target
                            ? target with { ReceivedToday = target.ReceivedToday + report.Amount }
                            : null
                    };
                    ApplySupplyUsed();
                    UpdateTargetStatus(_capacityTargetId);
                }
            }

            SetAmount(0);
        }
        else if (report.Reason.Length > 0)
        {
            SetStatus(FailureText(report), "error");
        }
        else
        {
            SetStatus(ErrorText(report.ErrorCode), "error");
        }

        RecomputeAmount();
        UpdateSendState();
    }

    private ulong _sentToday;

    private static ulong Subtract(ulong value, ulong amount) => amount >= value ? 0 : value - amount;

    private string FailureText(TransportReport report) => report.Reason switch
    {
        "no_trading_post" => Localizer.Instance["TransportErrorNoBuilding"],
        "target_not_found" => Localizer.Instance["TransportTargetNotFound"],
        "over_capacity" => Localizer.Instance.Format("TransportOverCapacity", Number(_snapshot?.PerSend ?? 0)),
        "offline" => Localizer.Instance["TransportNeedConnect"],
        "bad_amount" => Localizer.Instance["TransportNeedTarget"],
        "bad_target" => Localizer.Instance["TransportNeedTarget"],
        "bad_resource" => Localizer.Instance["TransportErrorResource"],
        _ => report.Message.Length > 0 ? report.Message : Localizer.Instance["TransportErrorTrading"]
    };

    private string ErrorText(int code) => code switch
    {
        8201 => Localizer.Instance["TransportErrorNoBuilding"],
        8202 => Localizer.Instance["TransportErrorTargetNoBuilding"],
        8203 => Localizer.Instance["TransportErrorTrading"],
        8204 => Localizer.Instance["TransportErrorTrading"],
        8205 => Localizer.Instance["TransportErrorResource"],
        8206 => Localizer.Instance["TransportErrorSupplyLimit"],
        8207 => Localizer.Instance["TransportErrorCapacity"],
        8208 => Localizer.Instance["TransportErrorWorld"],
        _ => Localizer.Instance.Format("TransportRefused", code)
    };

    private void ApplySupplyUsed()
    {
        var supply = _snapshot?.DailySupply ?? 0;
        var sent = _capacity?.Self?.SentToday ?? _sentToday;
        if (_capacity?.Self is { SentToday: var capacitySent })
            _sentToday = capacitySent;

        SupplyBar.Maximum = supply == 0 ? 1 : supply;
        SupplyBar.Value = Math.Min(sent, SupplyBar.Maximum);
        SupplyUsedText.Text = supply == 0
            ? Localizer.Instance.Format("TransportSupplyUsed", Number(sent), "—")
            : Localizer.Instance.Format("TransportSupplyUsed", Number(sent), Number(supply));
    }

    // ---- the receiver ------------------------------------------------------

    private bool TryReadTargetId(out ulong targetId)
    {
        targetId = 0;
        var text = (TargetBox.Text ?? string.Empty)
            .Replace(",", string.Empty)
            .Replace(" ", string.Empty)
            .Replace("_", string.Empty)
            .Trim();
        return ulong.TryParse(text, out targetId) && targetId > 0;
    }

    private void UpdateTargetStatus(ulong targetId)
    {
        if (_capacity is null || _capacityTargetId != targetId)
        {
            TargetBar.Value = 0;
            return;
        }

        if (_capacity.Target is not { } target)
        {
            TargetStatus.Text = Localizer.Instance["TransportTargetNotFound"];
            TargetBar.Value = 0;
            return;
        }

        if (target.Level == 0)
        {
            TargetStatus.Text = Localizer.Instance["TransportTargetNoBuilding"];
            TargetBar.Value = 0;
            return;
        }

        var name = NameOf(target.PlayerId);
        var limit = _snapshot is { DailyReceive: > 0 } snapshot ? snapshot.DailyReceive : target.DailyReceive;
        var remaining = target.ReceivedToday >= limit ? 0 : limit - target.ReceivedToday;
        TargetStatus.Text = Localizer.Instance.Format("TransportTargetLine", name, target.Level) + " · " +
                            Localizer.Instance.Format("TransportTargetRemaining", Number(remaining));
        TargetBar.Maximum = limit == 0 ? 1 : limit;
        TargetBar.Value = Math.Min(target.ReceivedToday, TargetBar.Maximum);
    }

    private string NameOf(ulong playerId) =>
        _snapshot?.Targets.FirstOrDefault(target => target.PlayerId == playerId)?.Display
        ?? playerId.ToString(CultureInfo.InvariantCulture);

    // ---- amount ------------------------------------------------------------

    private ulong ResourceAmount(uint subType) => subType switch
    {
        Protocol.WoodSubType => _snapshot?.Wood ?? 0,
        Protocol.SteelSubType => _snapshot?.Steel ?? 0,
        Protocol.OilSubType => _snapshot?.Oil ?? 0,
        _ => _snapshot?.Food ?? 0
    };

    /// <summary>The longest the slider may be: resources, one transport, and today's supply.</summary>
    private ulong MaxAmount()
    {
        var max = ResourceAmount(_subType);
        if (_snapshot is { PerSend: > 0 } limit)
            max = Math.Min(max, limit.PerSend);

        if (_snapshot is { DailySupply: > 0 } snapshot)
        {
            var sent = _capacity?.Self?.SentToday ?? _sentToday;
            var left = sent >= snapshot.DailySupply ? 0 : snapshot.DailySupply - sent;
            max = Math.Min(max, left);
        }

        return max;
    }

    private void RecomputeAmount()
    {
        var max = MaxAmount();
        _syncing = true;
        try
        {
            AmountSlider.Maximum = max;
            if (_amount > max)
                _amount = max;
            AmountSlider.Value = _amount;
            AmountBox.Text = Number(_amount);
        }
        finally
        {
            _syncing = false;
        }

        PerSendText.Text = Localizer.Instance.Format("TransportOneTrip", Number(_snapshot?.PerSend ?? 0));
        AvailableText.Text = Localizer.Instance.Format("TransportAvailable", Number(ResourceAmount(_subType)));
        MaxText.Text = Number(max);
        UpdateAmountTexts();
        UpdateSendState();
    }

    private void SetAmount(ulong amount)
    {
        _amount = Math.Min(amount, MaxAmount());
        _syncing = true;
        try
        {
            AmountSlider.Value = _amount;
            AmountBox.Text = Number(_amount);
        }
        finally
        {
            _syncing = false;
        }

        UpdateAmountTexts();
        UpdateSendState();
    }

    private void UpdateAmountTexts()
    {
        var taxPermille = _snapshot?.TaxPermille ?? 0;
        var tax = _amount * (ulong)Math.Max(taxPermille, 0) / 1000;
        TaxText.Text = Localizer.Instance.Format("TransportTaxLine", Percent(taxPermille), Number(tax));
        ReceiveText.Text = Localizer.Instance.Format("TransportReceiveLine", Number(Subtract(_amount, tax)));
    }

    // ---- send --------------------------------------------------------------

    private void UpdateSendState()
    {
        var connected = Account?.IsConnected ?? false;
        var hasBuilding = _snapshot is null || !_snapshot.Known || _snapshot.Level > 0;

        // Send needs a receiver and an amount, nothing more: the game server is
        // the authority on the level of the receiver and on the daily limits,
        // and whatever it answers lands in the status line below.
        SendButton.IsEnabled = !_sending && connected && hasBuilding &&
                               TryReadTargetId(out _) && _amount > 0;
    }

    private void Send()
    {
        if (Account is not { } account || _send is null || !SendButton.IsEnabled)
            return;

        if (!TryReadTargetId(out var targetId))
        {
            SetStatus(Localizer.Instance["TransportNeedTarget"], "error");
            return;
        }

        _sending = true;
        SendButton.IsEnabled = false;
        StatusText.Text = Localizer.Instance["TransportSending"];
        SetStatus(Localizer.Instance["TransportSending"], "info");
        _send(account, targetId, _subType, Math.Min(_amount, MaxAmount()));

        // A lost reply must not leave the button dead forever.
        DispatcherTimer.RunOnce(() =>
        {
            if (!_sending)
                return;
            _sending = false;
            SetStatus(Localizer.Instance["TransportErrorTrading"], "error");
            UpdateSendState();
        }, TimeSpan.FromSeconds(20));
    }

    private void SetStatus(string text, string level)
    {
        StatusText.Text = text;
        StatusText.Foreground = level switch
        {
            "success" => Brush("AccentLightTextBrush"),
            "error" => Brush("DangerTextBrush"),
            _ => Brush("MutedStrongBrush")
        };
    }

    private IBrush Brush(string key) =>
        this.TryFindResource(key, out var value) && value is IBrush brush ? brush : Brushes.Gray;

    private static string Number(ulong value) => value.ToString("N0", CultureInfo.InvariantCulture);

    private static string Percent(int permille) =>
        (permille / 10.0).ToString("0.#", CultureInfo.InvariantCulture) + "%";

    // ---- events ------------------------------------------------------------

    private void Check_Click(object? sender, RoutedEventArgs e)
    {
        if (Account is not { } account || !account.IsConnected)
        {
            SetStatus(Localizer.Instance["TransportNeedConnect"], "error");
            return;
        }

        if (!TryReadTargetId(out var targetId))
        {
            SetStatus(Localizer.Instance["TransportNeedTarget"], "error");
            return;
        }

        _capacity = null;
        _capacityTargetId = 0;
        _requestedTargetId = targetId;
        TargetStatus.Text = Localizer.Instance["TransportSending"];
        TargetBar.Value = 0;
        RecomputeAmount();
        UpdateSendState();
        _requestCapacity?.Invoke(account, targetId);
    }

    private void TargetList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (TargetList.SelectedItem is not TransportTarget target)
            return;

        TargetBox.Text = target.PlayerId.ToString(CultureInfo.InvariantCulture);
        Check_Click(sender, new RoutedEventArgs());
    }

    private void Resource_Click(object? sender, RoutedEventArgs e)
    {
        var picked = sender is ToggleButton { Tag: { } tag } ? ResourceSubType(tag) : Protocol.FoodSubType;
        _subType = picked;
        FoodPick.IsChecked = picked == Protocol.FoodSubType;
        WoodPick.IsChecked = picked == Protocol.WoodSubType;
        SteelPick.IsChecked = picked == Protocol.SteelSubType;
        OilPick.IsChecked = picked == Protocol.OilSubType;
        SetAmount(0);
        RecomputeAmount();
    }

    private void AmountSlider_ValueChanged(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_syncing)
            return;
        _amount = (ulong)Math.Round(Math.Max(e.NewValue, 0));
        _syncing = true;
        try
        {
            AmountBox.Text = Number(_amount);
        }
        finally
        {
            _syncing = false;
        }

        UpdateAmountTexts();
        UpdateSendState();
    }

    private void AmountBox_TextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_syncing)
            return;
        var text = (AmountBox.Text ?? string.Empty)
            .Replace(",", string.Empty)
            .Replace(" ", string.Empty)
            .Trim();
        if (!ulong.TryParse(text, out var amount))
            return;
        SetAmount(amount);
    }

    private void Quarter_Click(object? sender, RoutedEventArgs e) => SetAmount(MaxAmount() / 4);

    private void Half_Click(object? sender, RoutedEventArgs e) => SetAmount(MaxAmount() / 2);

    private void Max_Click(object? sender, RoutedEventArgs e) => SetAmount(MaxAmount());

    private void Send_Click(object? sender, RoutedEventArgs e) => Send();

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape)
            Close();
    }
}
