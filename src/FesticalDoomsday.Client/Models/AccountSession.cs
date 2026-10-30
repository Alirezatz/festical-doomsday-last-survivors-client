using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
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
    private ulong _helpCount;
    private ulong _bountyTickets;
    private ulong _bountyTicketLastReward;
    private bool _bountyTicketKnown;
    private bool _guildKnown;
    private bool _inGuild;
    private string _guildName = string.Empty;
    private bool _playerKnown;
    private string _playerName = string.Empty;
    private ulong _level;
    private ulong _might;
    private ulong _food;
    private ulong _wood;
    private ulong _steel;
    private ulong _oil;
    private string _bountyMode = "unknown";
    private bool _autoSpinOn;
    private string _spinState = "off";
    private readonly Dictionary<uint, ulong> _bagCounts = new();
    private bool _bagKnown;
    private bool _tradingPostKnown;
    private uint _tradingPostLevel;

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

    /// <summary>True once the backend has read the account's Alliance from the game server.</summary>
    public bool GuildKnown
    {
        get => _guildKnown;
        set
        {
            if (Set(ref _guildKnown, value))
                Raise(nameof(GuildStatusText));
            Raise(nameof(ShowGuildStatus));
        }
    }

    /// <summary>Whether the account currently belongs to an Alliance.</summary>
    public bool InGuild
    {
        get => _inGuild;
        set
        {
            if (Set(ref _inGuild, value))
                Raise(nameof(GuildStatusText));
        }
    }

    /// <summary>The Alliance name/tag shown on the card.</summary>
    public string GuildName
    {
        get => _guildName;
        set
        {
            if (Set(ref _guildName, value))
                Raise(nameof(GuildStatusText));
        }
    }

    public bool ShowGuildStatus => IsConnected && GuildKnown;

    public string GuildStatusText => !GuildKnown
        ? Localizer.Instance["GuildUnknown"]
        : InGuild
            ? Localizer.Instance.Format("GuildStatus", GuildName)
            : Localizer.Instance["GuildNone"];

    // ---- account identity and bag resources --------------------------------

    /// <summary>True once the server reported the account's own base data.</summary>
    public bool PlayerKnown
    {
        get => _playerKnown;
        set
        {
            if (!Set(ref _playerKnown, value))
                return;
            RaisePlayerTexts();
        }
    }

    public bool ShowPlayerInfo => IsConnected && PlayerKnown;

    /// <summary>The in-game name of the account, straight from the server.</summary>
    public string PlayerNameText => PlayerKnown && !string.IsNullOrWhiteSpace(_playerName)
        ? _playerName
        : "—";

    /// <summary>Might (the game calls the same number Power) in a short form.</summary>
    public string MightText => PlayerKnown ? Compact(_might) : "—";

    public string MightFullText => PlayerKnown ? Full(_might) : "—";

    public string LevelText => PlayerKnown ? Full(_level) : "—";

    public string FoodText => PlayerKnown ? Compact(_food) : "—";

    public string WoodText => PlayerKnown ? Compact(_wood) : "—";

    public string SteelText => PlayerKnown ? Compact(_steel) : "—";

    public string OilText => PlayerKnown ? Compact(_oil) : "—";

    public string FoodFullText => PlayerKnown ? Full(_food) : "—";

    public string WoodFullText => PlayerKnown ? Full(_wood) : "—";

    public string SteelFullText => PlayerKnown ? Full(_steel) : "—";

    public string OilFullText => PlayerKnown ? Full(_oil) : "—";

    /// <summary>Name, level, Might and the four bag resources of the account.</summary>
    public void SetPlayerBase(string? name, ulong level, ulong might,
        ulong food, ulong wood, ulong steel, ulong oil)
    {
        _playerName = name ?? string.Empty;
        _level = level;
        _might = might;
        _food = food;
        _wood = wood;
        _steel = steel;
        _oil = oil;
        _playerKnown = true;
        Raise(nameof(PlayerKnown));
        Raise(nameof(ShowPlayerInfo));
        RaisePlayerTexts();
    }

    public void ResetPlayerBase()
    {
        _playerName = string.Empty;
        _level = 0;
        _might = 0;
        _food = 0;
        _wood = 0;
        _steel = 0;
        _oil = 0;
        _playerKnown = false;
        Raise(nameof(PlayerKnown));
        Raise(nameof(ShowPlayerInfo));
        RaisePlayerTexts();
    }

    private void RaisePlayerTexts()
    {
        Raise(nameof(PlayerNameText));
        Raise(nameof(MightText));
        Raise(nameof(MightFullText));
        Raise(nameof(LevelText));
        Raise(nameof(FoodText));
        Raise(nameof(WoodText));
        Raise(nameof(SteelText));
        Raise(nameof(OilText));
        Raise(nameof(FoodFullText));
        Raise(nameof(WoodFullText));
        Raise(nameof(SteelFullText));
        Raise(nameof(OilFullText));
    }

    // ---- the real bag (the stacks the game server reports) -----------------

    /// <summary>Every usable resource stack of the account's bag.</summary>
    public ObservableCollection<BagStack> BagStacks { get; } = [];

    /// <summary>True once the server sent the whole bag of this session.</summary>
    public bool BagKnown
    {
        get => _bagKnown;
        private set
        {
            if (_bagKnown == value)
                return;
            _bagKnown = value;
            RaiseBagTexts();
        }
    }

    public bool HasBagStacks => BagStacks.Count > 0;

    public string BagStacksSummaryText => !BagKnown
        ? Localizer.Instance["BagWaiting"]
        : BagStacks.Count == 0
            ? Localizer.Instance["BagNoStacks"]
            : Localizer.Instance.Format("BagStacksCount", BagStacks.Count);

    public string BagFoodText => BagTotal("food");

    public string BagWoodText => BagTotal("wood");

    public string BagSteelText => BagTotal("steel");

    public string BagOilText => BagTotal("oil");

    private string BagTotal(string resource)
    {
        if (!BagKnown)
            return "—";
        ulong total = 0;
        foreach (var stack in BagStacks)
        {
            if (stack.ResourceKey == resource)
                total += stack.AmountPerUnit * stack.Count;
        }

        return total.ToString("N0", CultureInfo.InvariantCulture);
    }

    /// <summary>Replaces the whole bag with the list the backend just reported.</summary>
    public void SetBagItems(IEnumerable<(uint Id, ulong Count)> items, bool known)
    {
        _bagCounts.Clear();
        foreach (var (id, count) in items)
        {
            if (id != 0)
                _bagCounts[id] = count;
        }

        _bagKnown = known;
        RebuildBag();
    }

    /// <summary>One stack changed, for example after a Use the server accepted.</summary>
    public void UpdateBagItem(uint id, ulong count)
    {
        if (count == 0)
            _bagCounts.Remove(id);
        else
            _bagCounts[id] = count;

        if (ItemCatalog.Find(id)?.Resource is null)
            return;

        var existing = BagStacks.FirstOrDefault(stack => stack.ItemId == id);
        if (count == 0)
        {
            if (existing is not null)
                BagStacks.Remove(existing);
        }
        else if (existing is not null)
        {
            existing.Count = count;
        }
        else
        {
            RebuildBag();
            return;
        }

        RaiseBagTexts();
    }

    /// <summary>The account was disconnected: the bag of the session is gone.</summary>
    public void ResetBag()
    {
        _bagCounts.Clear();
        _bagKnown = false;
        BagStacks.Clear();
        RaiseBagTexts();
    }

    private void RebuildBag()
    {
        BagStacks.Clear();
        foreach (var pair in _bagCounts.OrderBy(pair => ResourceOrder(pair.Key))
                     .ThenByDescending(pair => ItemCatalog.Find(pair.Key)?.Amount ?? 0))
        {
            if (ItemCatalog.Find(pair.Key) is { } definition && definition.Resource is not null)
                BagStacks.Add(new BagStack(definition, pair.Value));
        }

        RaiseBagTexts();
    }

    private static int ResourceOrder(uint itemId) => ItemCatalog.Find(itemId)?.Resource switch
    {
        "food" => 0,
        "wood" => 1,
        "steel" => 2,
        "oil" => 3,
        _ => 4
    };

    private void RaiseBagTexts()
    {
        Raise(nameof(BagKnown));
        Raise(nameof(HasBagStacks));
        Raise(nameof(BagStacksSummaryText));
        Raise(nameof(BagFoodText));
        Raise(nameof(BagWoodText));
        Raise(nameof(BagSteelText));
        Raise(nameof(BagOilText));
    }

    // ---- Bounty mode and the wheel ----------------------------------------

    /// <summary>
    /// "on" while the account keeps starting Bounty matches, "unavailable"
    /// after the game answered error 14000 for it, otherwise "off".
    /// </summary>
    public string BountyMode
    {
        get => _bountyMode;
        set
        {
            if (!Set(ref _bountyMode, value))
                return;
            Raise(nameof(BountyChipText));
            Raise(nameof(BountyOn));
            Raise(nameof(BountyUnavailable));
            Raise(nameof(BountyChipTip));
        }
    }

    public bool BountyOn => _bountyMode == "on";

    public bool BountyUnavailable => _bountyMode == "unavailable";

    public string BountyChipText => BountyOn
        ? Localizer.Instance["BountyOn"]
        : Localizer.Instance["BountyOff"];

    public string BountyChipTip => BountyUnavailable
        ? Localizer.Instance["BountyUnavailableTip"]
        : Localizer.Instance["BountyChipTip"];

    /// <summary>True while the wheel is spending this account's Bounty Tickets.</summary>
    public bool AutoSpinOn => _autoSpinOn;

    public string SpinState => _spinState;

    public void SetAutoSpin(bool enabled, string state)
    {
        if (_autoSpinOn != enabled)
        {
            _autoSpinOn = enabled;
            Raise(nameof(AutoSpinOn));
            Raise(nameof(AutoSpinLabel));
        }

        if (_spinState == state)
            return;
        _spinState = state;
        Raise(nameof(SpinState));
    }

    /// <summary>The label of the tiny wheel button of the card.</summary>
    public string AutoSpinLabel => Localizer.Instance[AutoSpinOn ? "SpinStop" : "SpinStart"];

    // ---- Trading Post (sending resources to another Commander) -------------

    /// <summary>True once the server reported this account's own city.</summary>
    public bool TradingPostKnown => _tradingPostKnown;

    /// <summary>The Trading Post level of the account; 0 means it is not built.</summary>
    public uint TradingPostLevel => _tradingPostLevel;

    /// <summary>
    /// True only when the server said the account has no Trading Post. An
    /// account whose city is not known yet is not flagged: it may well have one.
    /// </summary>
    public bool TransportMissing => _tradingPostKnown && _tradingPostLevel == 0;

    /// <summary>What the Transport Resource button says.</summary>
    public string TransportTip => TransportMissing
        ? Localizer.Instance["TransportNoBuilding"]
        : Localizer.Instance["TransportTip"];

    /// <summary>The city reply of the server: the Trading Post level, or none.</summary>
    public void SetTradingPost(uint level)
    {
        var changed = !_tradingPostKnown || _tradingPostLevel != level;
        _tradingPostKnown = true;
        _tradingPostLevel = level;
        Raise(nameof(TradingPostKnown));
        Raise(nameof(TradingPostLevel));
        if (!changed)
            return;
        Raise(nameof(TransportMissing));
        Raise(nameof(TransportTip));
    }

    public void ResetTradingPost()
    {
        if (!_tradingPostKnown)
            return;
        _tradingPostKnown = false;
        _tradingPostLevel = 0;
        Raise(nameof(TradingPostKnown));
        Raise(nameof(TradingPostLevel));
        Raise(nameof(TransportMissing));
        Raise(nameof(TransportTip));
    }

    /// <summary>Alliance helps this account completed since the app started.</summary>
    public string HelpCountText => _helpCount.ToString("N0", CultureInfo.InvariantCulture);

    /// <summary>Current Bounty Ticket balance, or a dash until the server reports it.</summary>
    public string BountyTicketText => _bountyTicketKnown
        ? _bountyTickets.ToString("N0", CultureInfo.InvariantCulture)
        : "—";

    /// <summary>The whole Bounty Ticket balance, for the bag form.</summary>
    public string BountyTicketsFullText => _bountyTicketKnown ? Full(_bountyTickets) : "—";

    /// <summary>Bounty Tickets the last finished Bounty Ground match paid.</summary>
    public string LastRewardText => HasLastReward
        ? "+" + _bountyTicketLastReward.ToString("N0", CultureInfo.InvariantCulture)
        : string.Empty;

    public bool HasLastReward => _bountyTicketKnown && _bountyTicketLastReward > 0;

    public void SetHelpCount(ulong count)
    {
        if (_helpCount == count)
            return;
        _helpCount = count;
        Raise(nameof(HelpCountText));
    }

    public void SetBountyTickets(ulong total, ulong lastReward)
    {
        _bountyTickets = total;
        _bountyTicketLastReward = lastReward;
        _bountyTicketKnown = true;
        Raise(nameof(BountyTicketText));
        Raise(nameof(BountyTicketsFullText));
        Raise(nameof(LastRewardText));
        Raise(nameof(HasLastReward));
    }

    public void ResetBountyTickets()
    {
        _bountyTickets = 0;
        _bountyTicketLastReward = 0;
        _bountyTicketKnown = false;
        Raise(nameof(BountyTicketText));
        Raise(nameof(BountyTicketsFullText));
        Raise(nameof(LastRewardText));
        Raise(nameof(HasLastReward));
    }

    /// <summary>Completed when the backend reports the outcome of a connect attempt.</summary>
    public TaskCompletionSource<bool>? ConnectionAttempt { get; set; }

    /// <summary>Completed when the backend reports the outcome of an alliance action.</summary>
    public TaskCompletionSource<GuildActionResult>? GuildActionAttempt { get; set; }

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;
        field = value;
        Raise(propertyName);
        return true;
    }

    private void Raise(string? propertyName)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        if (propertyName is not (nameof(IsConnected) or nameof(IsConnecting)))
            return;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DisplayName)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StatusText)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ActionText)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowGuildStatus)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowPlayerInfo)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(BountyChipText)));
    }

    /// <summary>Refreshes the texts that depend on the language of the window.</summary>
    public void RefreshLocalizedText()
    {
        Raise(nameof(StatusText));
        Raise(nameof(ActionText));
        Raise(nameof(GuildStatusText));
        Raise(nameof(BountyChipText));
        Raise(nameof(BountyChipTip));
        Raise(nameof(AutoSpinLabel));
        Raise(nameof(TransportTip));
        RaiseBagTexts();
        foreach (var stack in BagStacks)
            stack.RefreshLocalizedText();
    }

    /// <summary>Short form of a big number: 1.2M, 845K, 12,300.</summary>
    private static string Compact(ulong value)
    {
        var culture = CultureInfo.InvariantCulture;
        if (value >= 1_000_000_000)
            return (value / 1_000_000_000d).ToString("0.##", culture) + "B";
        if (value >= 1_000_000)
            return (value / 1_000_000d).ToString("0.##", culture) + "M";
        if (value >= 10_000)
            return (value / 1_000d).ToString("0.#", culture) + "K";
        return value.ToString("N0", culture);
    }

    private static string Full(ulong value) => value.ToString("N0", CultureInfo.InvariantCulture);
}
