using System.Collections.Concurrent;
using System.IO;
using System.Net.Sockets;
using System.Net;
using System.Text.Json.Nodes;
using System.Text.Json;
using System.Text.RegularExpressions;
using FesticalDoomsday.Client.Services;

namespace FesticalDoomsday.Client.Backend;

/// <summary>One command received from the user interface.</summary>
internal sealed class BackendCommand
{
    public string Action { get; init; } = string.Empty;
    public string? Account { get; init; }
    public string? CapturePath { get; init; }
    public string? CustomServerHost { get; init; }
    public bool? Enabled { get; init; }
    public ulong? GuildId { get; init; }
    public string? GuildName { get; init; }
    public uint? ItemId { get; init; }
    public uint? Count { get; init; }
    public ulong? TargetId { get; init; }
    public uint? SubType { get; init; }
    public ulong? Amount { get; init; }

    public static BackendCommand? Parse(string line)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(line);
        }
        catch (JsonException)
        {
            return null;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return null;
            return new BackendCommand
            {
                Action = GetString(root, "action") ?? string.Empty,
                Account = GetString(root, "account"),
                CapturePath = GetString(root, "capture_path"),
                CustomServerHost = GetString(root, "custom_server_host"),
                Enabled = root.TryGetProperty("enabled", out var enabled) &&
                         enabled.ValueKind is JsonValueKind.True or JsonValueKind.False
                    ? enabled.GetBoolean()
                    : null,
                GuildId = root.TryGetProperty("guild_id", out var guildId) &&
                          guildId.ValueKind == JsonValueKind.Number &&
                          guildId.TryGetUInt64(out var id)
                    ? id
                    : null,
                GuildName = GetString(root, "guild_name"),
                ItemId = ReadUInt(root, "item_id"),
                Count = ReadUInt(root, "count"),
                TargetId = ReadULong(root, "target_id"),
                SubType = ReadUInt(root, "sub_type"),
                Amount = ReadULong(root, "amount")
            };
        }
    }

    private static string? GetString(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value))
            return null;
        // A JSON null has to stay null: "no custom server" is signalled by a
        // null custom_server_host, and an empty string would be read as a
        // custom server request with an empty host.
        if (value.ValueKind == JsonValueKind.Null)
            return null;
        return value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
    }

    private static ulong? ReadULong(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value))
            return null;
        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetUInt64(out var number) => number,
            JsonValueKind.String when ulong.TryParse(value.GetString(), out var number) => number,
            _ => null
        };
    }

    private static uint? ReadUInt(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value))
            return null;
        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetUInt32(out var number) => number,
            JsonValueKind.String when uint.TryParse(value.GetString(), out var number) => number,
            _ => null
        };
    }
}

/// <summary>
/// BackendService.cs - the shared backend that serves every account.
///
/// The wire codec (Protocol.cs) and the socket client (GameClient.cs) mirror
/// the Python originals; only the orchestration changed. Instead of one
/// interpreter per account, this service keeps every account connection inside
/// a single process:
///
///   * one worker thread per account   - commands for an account stay ordered,
///   * one receiver thread per account - only while that account is connected,
///   * one output lock                 - JSON lines from all threads stay intact.
///
/// Commands arrive as JSON lines and every event is emitted as a JSON line that
/// carries an "account" id, which is the account JSON path supplied by the
/// client. Ported from wpf_backend.py.
/// </summary>
internal sealed class BackendService : IDisposable
{
    private const double AllianceListRequestInterval = 2.0;
    private const double AllianceHelpCooldown = 5.0;
    private const double GuildRefreshInterval = 20.0;

    private static readonly Dictionary<(string Host, int Port), int> KnownGameServerPortOffsets = new()
    {
        [("34.187.190.254", 10294)] = -710,
        [("34.187.190.254", 22475)] = 11470,
        [("34.187.190.254", 22474)] = 11470,
        [("34.187.190.254", 22355)] = 11350,
        [("34.187.190.254", 22724)] = 11720,
        [("34.187.190.254", 22725)] = 11720
    };

    private readonly BlockingCollection<string> _commands = new();
    private readonly ConcurrentDictionary<string, Account> _accounts = new();
    private readonly object _outputLock = new();
    private readonly object _accountsLock = new();
    private Thread? _mainThread;
    private volatile bool _running;
    private bool _disposed;

    /// <summary>Raised for every event; the payload is one JSON line.</summary>
    public event Action<string>? Output;

    public bool IsRunning => _running;

    public void Start()
    {
        if (_running)
            return;
        _running = true;
        _mainThread = new Thread(MainLoop) { IsBackground = true, Name = "backend-commands" };
        _mainThread.Start();
        Emit("ready", null);
    }

    /// <summary>Queue one command (a JSON line) for the backend.</summary>
    public void Send(string line)
    {
        try
        {
            _commands.Add(line);
        }
        catch (ObjectDisposedException)
        {
            // The backend is shutting down.
        }
        catch (InvalidOperationException)
        {
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _running = false;
        try
        {
            _commands.CompleteAdding();
        }
        catch (ObjectDisposedException)
        {
        }

        try
        {
            _mainThread?.Join(2000);
        }
        catch (Exception)
        {
            // Nothing else to do while tearing down.
        }

        ShutdownAll();
        _commands.Dispose();
    }

    // --------------------------------------------------------------------------
    // output
    // --------------------------------------------------------------------------
    private void Emit(string kind, string? account, JsonObject? data = null)
    {
        var payload = new JsonObject { ["type"] = kind };
        if (account is not null)
            payload["account"] = account;
        if (data is not null)
        {
            foreach (var property in data.ToList())
                payload[property.Key] = property.Value?.DeepClone();
        }

        string line;
        try
        {
            line = payload.ToJsonString();
        }
        catch (Exception)
        {
            return;
        }

        // The whole emission is serialized so that events from all accounts
        // reach the interface in the order they were produced.
        lock (_outputLock)
        {
            Output?.Invoke(line);
        }
    }

    // --------------------------------------------------------------------------
    // command loop
    // --------------------------------------------------------------------------
    private void MainLoop()
    {
        try
        {
            foreach (var line in _commands.GetConsumingEnumerable())
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;
                var command = BackendCommand.Parse(line);
                if (command is null)
                {
                    Emit("error", null, new JsonObject { ["message"] = "Invalid backend command." });
                    continue;
                }

                if (command.Action == "shutdown")
                    break;
                if (command.Action == "ping")
                {
                    Emit("pong", null);
                    continue;
                }

                if (string.IsNullOrEmpty(command.Account))
                {
                    Emit("error", null, new JsonObject { ["message"] = "Backend command is missing an account id." });
                    continue;
                }

                if (command.Action == "connect")
                {
                    GetAccount(command.Account).Enqueue(command);
                    continue;
                }

                if (command.Action == "remove")
                {
                    if (!RemoveAccount(command.Account))
                        Emit("error", command.Account, new JsonObject { ["message"] = "Unknown account." });
                    continue;
                }

                var account = FindAccount(command.Account);
                if (account is null)
                {
                    Emit("error", command.Account,
                        new JsonObject { ["message"] = "Unknown account; connect it first." });
                    continue;
                }

                account.Enqueue(command);
            }
        }
        catch (ObjectDisposedException)
        {
        }
        finally
        {
            ShutdownAll();
        }
    }

    private Account GetAccount(string accountId)
    {
        lock (_accountsLock)
        {
            if (_accounts.TryGetValue(accountId, out var account))
                return account;
            account = new Account(accountId, Emit);
            _accounts[accountId] = account;
            return account;
        }
    }

    private Account? FindAccount(string accountId)
    {
        lock (_accountsLock)
        {
            return _accounts.GetValueOrDefault(accountId);
        }
    }

    private bool RemoveAccount(string accountId)
    {
        Account? account;
        lock (_accountsLock)
        {
            if (!_accounts.TryGetValue(accountId, out account))
                return false;
            _accounts.TryRemove(accountId, out _);
        }

        account?.Shutdown();
        return true;
    }

    private void ShutdownAll()
    {
        List<Account> accounts;
        lock (_accountsLock)
        {
            accounts = _accounts.Values.ToList();
        }

        foreach (var account in accounts)
            account.Shutdown();
    }

    // --------------------------------------------------------------------------
    // helpers shared by every account
    // --------------------------------------------------------------------------
    internal static (string Host, int Port) SplitHostPort(string value)
    {
        if (string.IsNullOrEmpty(value) || !value.Contains(':'))
            throw new ArgumentException("Invalid server address in capture.", nameof(value));
        var index = value.LastIndexOf(':');
        var host = value[..index];
        var portText = value[(index + 1)..];
        if (!int.TryParse(portText, out var port))
            throw new ArgumentException("Invalid server address in capture.", nameof(value));
        return (host, port);
    }

    /// <summary>
    /// Captured LoginServer first, then the game's other known login routes.
    /// A capture freezes the LoginServer that was reachable on the recording
    /// network. On another network that address can be blocked even though the
    /// rest of the game is fine, so fall back to the other routes from the
    /// game's own server_config.
    /// </summary>
    internal static List<(string Host, int Port)> LoginServerCandidates(string value)
    {
        var (host, port) = SplitHostPort(value);
        var candidates = new List<(string, int)> { (host, port) };
        foreach (var candidate in EdgeRoute.LoginServers)
        {
            if (!candidates.Contains(candidate))
                candidates.Add(candidate);
        }

        return candidates;
    }

    internal static string NormalizeCustomServerHost(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Custom server host must be a non-empty hostname or IP address.");
        var host = value.Trim();
        if (host.StartsWith('[') && host.EndsWith(']'))
            host = host[1..^1];
        if (IPAddress.TryParse(host, out var address))
            return address.ToString();
        const string label = @"[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?";
        if (host.Length > 253 || !Regex.IsMatch(host, $"^{label}(?:\\.{label})*\\.?$"))
            throw new ArgumentException("Custom server must be a hostname or IP address without a port.");
        return host;
    }

    internal static (string Host, int Port) CustomServerEndpoint(JsonElement capture, string value)
    {
        var host = NormalizeCustomServerHost(value);
        if (!capture.TryGetProperty("game_server", out var captured) || captured.ValueKind != JsonValueKind.String ||
            string.IsNullOrEmpty(captured.GetString()))
        {
            throw new ArgumentException("Custom server mode requires a GameServer port in the account JSON.");
        }

        var (_, port) = SplitHostPort(captured.GetString()!);
        if (port is <= 0 or > 65535)
            throw new ArgumentException("Account JSON contains an invalid GameServer port.");
        return (host, port);
    }

    /// <summary>
    /// Return the (host, port) for the GameServer the real client would use.
    /// The LoginServer's NetAddress is a private address; the client rewrites it
    /// with the game's vn_server_edge table (kingdom to edge host, edge base
    /// port). We do the same instead of hard-coding an IP from an old capture.
    /// </summary>
    internal static (string Host, int Port) ResolveGameServerAddress(JsonElement capture, LsLoginReply session,
        EdgeTable? table)
    {
        string? capturedAddress = null;
        if (capture.TryGetProperty("game_server", out var gameServer) &&
            gameServer.ValueKind == JsonValueKind.String)
        {
            capturedAddress = gameServer.GetString();
        }

        var netIp = session.NetIp;
        var netPort = session.NetPort;
        if (!string.IsNullOrEmpty(netIp) && netPort is not null)
        {
            if (EdgeRoute.IsGlobalIp(netIp))
                return (netIp, netPort.Value);
            try
            {
                var route = EdgeRoute.RouteFor(netIp, netPort.Value, (long?)session.KingdomId, table);
                return (route.Host, route.Port);
            }
            catch (Exception)
            {
                // Fall through to the captured address.
            }

            if (string.IsNullOrEmpty(capturedAddress))
                throw new ArgumentException(
                    "LoginServer assigned a non-public GameServer address and no edge route was found.");

            var (capturedHost, capturedPort) = SplitHostPort(capturedAddress);
            int? portOffset = null;
            if (capture.TryGetProperty("ls_reply", out var replyElement) &&
                replyElement.ValueKind == JsonValueKind.Object &&
                replyElement.TryGetProperty("net_port", out var netPortValue) &&
                netPortValue.ValueKind == JsonValueKind.Number &&
                netPortValue.TryGetInt32(out var capturedNetPort) &&
                capturedNetPort is > 0 and <= 65535)
            {
                portOffset = capturedPort - capturedNetPort;
            }
            else
            {
                KnownGameServerPortOffsets.TryGetValue((capturedHost, capturedPort), out var known);
                if (known != 0 || KnownGameServerPortOffsets.ContainsKey((capturedHost, capturedPort)))
                    portOffset = known;
            }

            if (portOffset is not null)
            {
                var publicPort = netPort.Value + portOffset.Value;
                if (publicPort is <= 0 or > 65535)
                    throw new ArgumentException("LoginServer GameServer port could not be mapped to a valid public port.");
                return (capturedHost, publicPort);
            }

            return SplitHostPort(capturedAddress);
        }

        if (!string.IsNullOrEmpty(capturedAddress))
        {
            // Old captures have no NetAddress, but the kingdom is enough to
            // replace a frozen edge IP with the current edge hostname.
            if (session.KingdomId is not null && table is not null &&
                table.ByKingdom.TryGetValue((long)session.KingdomId.Value, out var row))
            {
                var (_, port) = SplitHostPort(capturedAddress);
                return (row.EdgeHost, port);
            }

            return SplitHostPort(capturedAddress);
        }

        throw new ArgumentException("LoginServer and capture did not provide a GameServer address.");
    }

    /// <summary>Find the alliance whose TAG (or [TAG]Name) matches the user's input.</summary>
    internal static GuildInfo FindSearchedGuild(string name, List<GuildInfo> guilds)
    {
        var value = name.Trim();
        if (value.Length == 0)
            throw new ArgumentException("Enter an Alliance TAG.");
        var taggedMatch = Regex.Match(value, @"\[([^\]]+)\]\s*(.+)");
        List<GuildInfo> matches;
        if (taggedMatch.Success)
        {
            var expectedTag = taggedMatch.Groups[1].Value;
            var expectedName = taggedMatch.Groups[2].Value;
            matches = guilds
                .Where(guild => string.Equals(guild.ShortName, expectedTag, StringComparison.OrdinalIgnoreCase) &&
                                string.Equals(guild.Name, expectedName, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }
        else
        {
            matches = guilds
                .Where(guild => string.Equals(guild.ShortName, value, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        var unique = matches.GroupBy(guild => guild.Id).Select(group => group.First()).ToList();
        if (unique.Count == 0)
            throw new ArgumentException("No exact Alliance TAG/name match was found in the server's search results.");
        if (unique.Count > 1)
            throw new ArgumentException("More than one Alliance matched this TAG; enter the exact [TAG]Name.");
        return unique[0];
    }

    // --------------------------------------------------------------------------
    // one account
    // --------------------------------------------------------------------------
    private sealed class Account
    {
        private readonly Action<string, string?, JsonObject?> _emit;
        private readonly BlockingCollection<BackendCommand> _queue = new();
        private readonly BlockingCollection<(ushort Opcode, byte[] Body)> _guildReplies = new();
        private readonly object _helpLock = new();
        private readonly Thread _worker;

        private volatile GameClient? _game;
        private Thread? _reader;
        private volatile bool _stopReader;
        private bool _helpEnabled;
        private double _lastListRequest;
        private double _lastHelpRequest;
        private double _lastGuildRequest;
        private readonly object _guildLock = new();
        private GuildState _guild = GuildState.Unknown;

        // ---- Bounty (Battle Royale) auto start ------------------------------
        private readonly object _bountyLock = new();
        private readonly List<HeroRecord> _heroes = new();
        private readonly List<TroopRecord> _troops = new();
        private EatChickenArmy? _bountySavedArmy;
        private EatChickenArmy? _bountyArmy;
        private BountyPhase _bountyPhase = BountyPhase.Off;
        private double _bountyNextAction;
        private int _bountyError;
        private int _bountyFailures;
        private ulong _bountyRegionId;
        private volatile bool _bountyAuto;
        private Thread? _bountyWorker;
        private volatile bool _bountyWorkerStop;

        /// <summary>
        /// Set when the game answered a Bounty registration with error 14000: the
        /// account is below the mode's power requirement (or owns no usable
        /// hero/troop). From then on this account stays Bounty OFF instead of
        /// hammering the server, while every other account keeps starting matches.
        /// A fresh connection clears it, so a fixed account may try again.
        /// </summary>
        private volatile bool _bountyUnavailable;

        // ---- account identity and bag resources -----------------------------
        private readonly object _playerLock = new();
        private PlayerBaseInfo? _playerBase;

        // ---- Trading Post (sending resources to another Commander) ----------
        /// <summary>Every castle of the loaded view, by player id.</summary>
        private readonly object _transportLock = new();
        private readonly Dictionary<ulong, WorldCastle> _castles = new();
        private uint _tradingPostLevel;
        private bool _tradingPostKnown;
        private bool _cityInfoRequested;

        /// <summary>The transport the UI asked for, so its reply can name it.</summary>
        private readonly object _carriageLock = new();
        private ulong? _carriageTarget;
        private uint _carriageSubType;
        private ulong _carriageAmount;
        private ulong _carriageTargetEntity;
        private ulong _capacityQueryTarget;

        // ---- Bounty turntable (the wheel) -----------------------------------
        private readonly object _spinLock = new();
        private volatile bool _autoSpin;
        private Thread? _spinWorker;
        private volatile bool _spinWorkerStop;
        private TaskCompletionSource<BountyTurntableReply>? _spinPending;
        private uint _turntableActivityId = Protocol.BountyTurntableActivityId;
        private bool _turntableActivityFromServer;
        private string _spinStateName = "off";

        // ---- account totals ------------------------------------------------
        private readonly object _itemLock = new();
        /// <summary>Every stack the bag notice reported, item id to count.</summary>
        private readonly Dictionary<uint, ulong> _bagItems = new();
        private bool _bagKnown;
        private ulong _bountyTickets;
        private ulong _bountyTicketLastReward;
        private bool _bountyTicketKnown;
        /// <summary>
        /// Bumped every time the server states the Bounty Ticket balance. A turn
        /// of the wheel compares it before and after the draw: when it moved,
        /// the server already published the new balance and the client must not
        /// book the cost a second time.
        /// </summary>
        private ulong _ticketSeq;
        private ulong _totalHelps;

        private enum BountyPhase
        {
            Off,
            NeedInfo,
            Ready,
            Registering,
            Pairing,
            InBattle,
            Ended,
            Failed,
            Maintenance
        }

        public Account(string id, Action<string, string?, JsonObject?> emit)
        {
            Id = id;
            _emit = emit;
            _worker = new Thread(RunCommands) { IsBackground = true, Name = $"cmd-{id}" };
            _worker.Start();
        }

        public string Id { get; }

        public void Enqueue(BackendCommand command)
        {
            try
            {
                _queue.Add(command);
            }
            catch (ObjectDisposedException)
            {
            }
            catch (InvalidOperationException)
            {
            }
        }

        private void RunCommands()
        {
            try
            {
                foreach (var command in _queue.GetConsumingEnumerable())
                {
                    try
                    {
                        Execute(command);
                    }
                    catch (Exception exception)
                    {
                        // One bad command must not kill the account.
                        _emit("error", Id, new JsonObject { ["message"] = exception.Message });
                    }
                }
            }
            catch (ObjectDisposedException)
            {
            }
        }

        private void Execute(BackendCommand command)
        {
            switch (command.Action)
            {
                case "connect":
                    Connect(command.CapturePath ?? string.Empty, command.CustomServerHost);
                    break;
                case "disconnect":
                    Disconnect();
                    break;
                case "alliance_auto_help":
                    SetAllianceAutoHelp(command.Enabled);
                    break;
                case "bounty_auto":
                    SetBountyAuto(command.Enabled);
                    break;
                case "bounty_spin":
                    SetAutoSpin(command.Enabled);
                    break;
                case "guild_refresh":
                    RequestGuildState();
                    break;
                case "guild_join":
                    PerformGuildAction("join", command.GuildId, null);
                    break;
                case "guild_join_by_name":
                    PerformGuildAction("join_by_name", null, command.GuildName);
                    break;
                case "guild_leave":
                    PerformGuildAction("leave", null, null);
                    break;
                case "bag_items":
                    EmitBagItems();
                    break;
                case "item_use":
                    UseItem(command.ItemId, command.Count);
                    break;
                case "transport_info":
                    EmitTransportInfo();
                    break;
                case "transport_capacity":
                    RequestTransportCapacity(command.TargetId);
                    break;
                case "transport_send":
                    SendTransport(command.TargetId, command.SubType, command.Amount);
                    break;
                default:
                    throw new ArgumentException("Unknown backend command.");
            }
        }

        /// <summary>
        /// Writes one console line. The level is checked first, so with logging
        /// off no JSON is built and nothing is serialized or sent to the window.
        /// </summary>
        private void Log(string message, string level = "info")
        {
            if (!AppLog.Allows(level))
                return;
            _emit("log", Id, new JsonObject { ["level"] = level, ["message"] = message });
        }

        // ---- alliance membership -------------------------------------------

        private GuildState GetGuildState()
        {
            lock (_guildLock)
            {
                return _guild;
            }
        }

        private void EmitGuildState(GuildState state)
        {
            _emit("guild_state", Id, new JsonObject
            {
                ["known"] = state.Known,
                ["in_guild"] = state.InGuild,
                ["guild_id"] = state.GuildId is null ? null : JsonValue.Create(state.GuildId.Value),
                ["guild_name"] = state.DisplayName,
                ["guild_short_name"] = state.ShortName,
                ["position"] = JsonValue.Create(state.Position),
                ["error_code"] = state.ErrorCode
            });
        }

        /// <summary>
        /// Record a fresh Alliance membership. The game only returns the assist
        /// list while the account is in an Alliance (error 1302 otherwise), and
        /// leaving/joining does not repair an already-broken session. Tracking
        /// the state lets Auto Help pause while guildless and resume the moment
        /// the server confirms a join, without reconnecting the account.
        /// </summary>
        private void SetGuildState(GuildState state, string source)
        {
            if (!state.Known)
                return;

            GuildState previous;
            lock (_guildLock)
            {
                previous = _guild;
                _guild = state;
            }

            var changed = !previous.Known || previous.GuildId != state.GuildId ||
                          !string.Equals(previous.Name, state.Name, StringComparison.Ordinal) ||
                          !string.Equals(previous.ShortName, state.ShortName, StringComparison.Ordinal);
            if (!changed)
                return;
            EmitGuildState(state);

            if (state.InGuild)
            {
                Log($"Alliance state ({source}): {state.DisplayName}" +
                    (state.GuildId is > 0 ? $" (ID {state.GuildId})." : "."));
                lock (_helpLock)
                {
                    _lastListRequest = 0.0;
                    _lastHelpRequest = 0.0;
                }

                if (IsHelpEnabled())
                {
                    EmitHelpState("waiting", true);
                    RequestAllianceAssistList(force: true);
                }
            }
            else
            {
                Log("Alliance state: this account is not in an Alliance. " +
                    "Alliance Auto Help will wait until it joins one.", "warn");
                if (IsHelpEnabled())
                    EmitHelpState("no_guild", true);
            }
        }

        private bool IsHelpEnabled()
        {
            lock (_helpLock)
            {
                return _helpEnabled;
            }
        }

        /// <summary>Ask the server for the current Alliance, answered as 0x2741.</summary>
        private void RequestGuildState(bool force = false)
        {
            GameClient? game;
            lock (_guildLock)
            {
                var now = Monotonic.Now;
                if (!force && now - _lastGuildRequest < GuildRefreshInterval)
                    return;
                _lastGuildRequest = now;
                game = _game;
            }

            if (game is null)
                return;
            try
            {
                game.Send(Protocol.BuildGuildBaseInfoRequest());
            }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException or SocketException)
            {
                // The reader reports a real disconnect; a failed refresh is harmless.
            }
        }

        /// <summary>
        /// Synchronous Alliance query used while the connection is still being
        /// established (the reader thread is not running yet, so Recv is safe).
        /// </summary>
        private GuildState? QueryGuildState(GameClient game, double timeout = 3.0)
        {
            try
            {
                game.Send(Protocol.BuildGuildBaseInfoRequest());
                var (body, _) = game.WaitFor(Protocol.GuildBaseInfoReply, timeout);
                return Protocol.ParseGuildBaseInfoReply(body);
            }
            catch (Exception exception) when (exception is TimeoutException or ProtocolError or IOException
                                               or SocketException or ObjectDisposedException)
            {
                return null;
            }
        }

        private void ScheduleGuildRefresh(double seconds = 1.5)
        {
            var game = _game;
            if (game is null)
                return;
            _ = Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(seconds));
                if (!ReferenceEquals(_game, game))
                    return;
                try
                {
                    game.Send(Protocol.BuildGuildBaseInfoRequest());
                }
                catch (Exception exception) when (exception is IOException or ObjectDisposedException
                                                   or SocketException)
                {
                }
            });
        }

        private void StoreHeroes(List<HeroRecord> heroes)
        {
            if (heroes.Count == 0)
                return;
            lock (_bountyLock)
            {
                foreach (var hero in heroes)
                {
                    var index = _heroes.FindIndex(existing => existing.Id == hero.Id);
                    if (index >= 0)
                        _heroes[index] = hero;
                    else
                        _heroes.Add(hero);
                }
            }
        }

        private void StoreTroops(List<TroopRecord> troops)
        {
            if (troops.Count == 0)
                return;
            lock (_bountyLock)
            {
                foreach (var troop in troops)
                {
                    var index = _troops.FindIndex(existing => existing.Id == troop.Id);
                    if (index >= 0)
                        _troops[index] = troop;
                    else
                        _troops.Add(troop);
                }
            }
        }

        // ---- Bounty Tickets (the Bounty Ground payout) ----------------------

        /// <summary>
        /// The full item list that arrives at login. The Bounty Ticket entry in
        /// it is the balance the card shows on the coin.
        /// </summary>
        private void StoreItems(List<ItemRecord> items)
        {
            ulong tickets = 0;
            foreach (var item in items)
            {
                if (item.Id == Protocol.BountyTicketItemId)
                    tickets = item.Count;
            }

            bool hadBalance;
            bool rewardPaid;
            lock (_itemLock)
            {
                _bagItems.Clear();
                foreach (var item in items)
                    _bagItems[item.Id] = item.Count;
                _bagKnown = true;

                hadBalance = _bountyTicketKnown;
                rewardPaid = hadBalance && tickets > _bountyTickets;
                // A balance that did not grow is not a payout, so the green +N
                // badge of the last match has to go with it.
                _bountyTicketLastReward = rewardPaid ? tickets - _bountyTickets : 0;

                _bountyTickets = tickets;
                _bountyTicketKnown = true;
                _ticketSeq++;
            }

            EmitBountyTickets(rewardPaid ? "reward" : hadBalance ? "update" : "login");
            EmitBagItems();
        }

        /// <summary>
        /// One Bounty Ticket stack changed. Every Bounty Ground match pays these
        /// by placement (the weakest finish still pays one), so a growth here is
        /// the reward of the match that just ended.
        /// </summary>
        private void StoreItemUpdate(ItemRecord item)
        {
            lock (_itemLock)
            {
                if (item.Count == 0)
                    _bagItems.Remove(item.Id);
                else
                    _bagItems[item.Id] = item.Count;
            }

            EmitBagItem(item.Id, item.Count);

            if (item.Id != Protocol.BountyTicketItemId)
                return;

            bool hadBalance;
            bool rewardPaid;
            lock (_itemLock)
            {
                hadBalance = _bountyTicketKnown;
                rewardPaid = hadBalance && item.Count > _bountyTickets;
                // Spent tickets (or any balance that did not grow) mean the
                // last payout badge is no longer the reward of this turn.
                _bountyTicketLastReward = rewardPaid ? item.Count - _bountyTickets : 0;

                _bountyTickets = item.Count;
                _bountyTicketKnown = true;
                _ticketSeq++;
            }

            EmitBountyTickets(rewardPaid ? "reward" : "update");
        }

        /// <summary>The whole bag, asked for when a bag form opens.</summary>
        private void EmitBagItems()
        {
            bool known;
            KeyValuePair<uint, ulong>[] items;
            lock (_itemLock)
            {
                known = _bagKnown;
                items = _bagItems.ToArray();
            }

            var array = new JsonArray();
            Array.Sort(items, (left, right) => left.Key.CompareTo(right.Key));
            foreach (var (id, count) in items)
            {
                array.Add(new JsonObject
                {
                    ["id"] = id,
                    ["count"] = JsonValue.Create(count)
                });
            }

            _emit("bag_items", Id, new JsonObject
            {
                ["known"] = known,
                ["items"] = array
            });
        }

        /// <summary>One stack changed: the bag form updates its tile in place.</summary>
        private void EmitBagItem(uint id, ulong count)
        {
            _emit("bag_item", Id, new JsonObject
            {
                ["id"] = id,
                ["count"] = JsonValue.Create(count)
            });
        }

        /// <summary>
        /// Use a stack from the bag. The bag form only offers the four resource
        /// families (food, wood, steel, oil), whose use needs no target: the
        /// server moves the resources into the city and answers 0x2782.
        /// </summary>
        private void UseItem(uint? itemId, uint? count)
        {
            if (itemId is null or 0)
            {
                EmitItemUseFailure(0, 0, "Choose an item from the bag first.");
                return;
            }

            var game = _game;
            if (game is null)
            {
                EmitItemUseFailure(itemId.Value, 0, "Connect this account before using an item.");
                return;
            }

            ulong? available;
            bool known;
            lock (_itemLock)
            {
                known = _bagKnown;
                available = _bagItems.TryGetValue(itemId.Value, out var stored) ? stored : null;
            }

            if (known && available is null)
            {
                EmitItemUseFailure(itemId.Value, 0, "That stack is no longer in the bag of this account.");
                return;
            }

            var requested = count is null or 0 ? 1u : count.Value;
            if (available is { } inBag && requested > inBag)
                requested = inBag == 0 ? 0 : (uint)Math.Min(inBag, uint.MaxValue);
            if (requested == 0)
            {
                EmitItemUseFailure(itemId.Value, 0, "That stack is empty.");
                return;
            }

            try
            {
                game.Send(Protocol.BuildItemUseRequest(itemId.Value, requested));
            }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException
                                               or SocketException or InvalidOperationException)
            {
                EmitItemUseFailure(itemId.Value, requested, $"The bag request could not be sent: {exception.Message}");
                return;
            }

            Log($"Using {requested:N0} × item {itemId.Value} from the bag.");
        }

        private void EmitItemUseFailure(uint id, uint count, string message)
        {
            _emit("item_use_result", Id, new JsonObject
            {
                ["id"] = id,
                ["count"] = count,
                ["success"] = false,
                ["error_code"] = -1,
                ["message"] = message
            });
            Log(message, "warn");
        }

        private void HandleItemUseReply(byte[] body)
        {
            ItemUseResult result;
            try
            {
                result = Protocol.ParseItemUseReply(body);
            }
            catch (ProtocolError exception)
            {
                Log($"Could not decode the bag reply: {exception.Message}", "warn");
                return;
            }

            var rewards = new JsonArray();
            foreach (var reward in result.Rewards)
            {
                rewards.Add(new JsonObject
                {
                    ["res_type"] = reward.ResType,
                    ["sub_type"] = reward.SubType,
                    ["value"] = JsonValue.Create(reward.Value)
                });
            }

            _emit("item_use_result", Id, new JsonObject
            {
                ["id"] = result.Id,
                ["count"] = result.Count,
                ["success"] = result.Success,
                ["error_code"] = result.ErrorCode,
                ["rewards"] = rewards
            });

            if (!result.Success)
                return;

            // A resource pack puts its contents into the city; the reply says
            // how much, so book that at once instead of waiting for a base
            // notice that the server does not push.
            PlayerBaseInfo? updated = null;
            lock (_playerLock)
            {
                if (_playerBase is { } info)
                {
                    var before = info;
                    foreach (var reward in result.Rewards)
                    {
                        if (reward.ResType != Protocol.BasicResourceType)
                            continue;
                        info = ShiftResource(info, reward.SubType,
                            reward.Value > (ulong)long.MaxValue ? long.MaxValue : (long)reward.Value);
                    }

                    if (!ReferenceEquals(info, before))
                    {
                        updated = info;
                        _playerBase = updated;
                    }
                }
            }

            PublishCityResources(updated);

            // The window writes the outcome line; the frame itself is enough here.
        }

        private void EmitBountyTickets(string reason)
        {
            ulong total;
            ulong lastReward;
            bool known;
            lock (_itemLock)
            {
                total = _bountyTickets;
                lastReward = _bountyTicketLastReward;
                known = _bountyTicketKnown;
            }

            if (!known)
                return;

            _emit("bounty_tickets", Id, new JsonObject
            {
                ["item_id"] = Protocol.BountyTicketItemId,
                ["tickets"] = JsonValue.Create(total),
                ["last_reward"] = JsonValue.Create(lastReward),
                ["known"] = true
            });

            if (reason == "reward")
                Log($"Bounty Ticket reward: +{lastReward:N0} (balance {total:N0}).");
            else if (reason == "login")
                Log($"Bounty Tickets: {total:N0}.");
        }

        // ---- account identity and bag resources -----------------------------

        /// <summary>
        /// The account's own base notice: name, Might (the message calls it
        /// Power) and the four bag resources. The card shows them, and the bag
        /// form reads the same numbers.
        /// </summary>
        private void StorePlayerBase(PlayerBaseInfo info)
        {
            lock (_playerLock)
            {
                _playerBase = info;
            }

            EmitPlayerBase(info);
            // The Trading Post form shows the same four numbers, so it has to
            // hear about a new base notice too.
            EmitTransportInfo();
            Log($"Account {info.Name} · Might {info.Might:N0} · bag: food {info.Food:N0}, " +
                $"wood {info.Wood:N0}, steel {info.Steel:N0}, oil {info.Oil:N0}.");
        }

        /// <summary>One resource of the base notice, moved by <paramref name="delta"/>.</summary>
        private static PlayerBaseInfo ShiftResource(PlayerBaseInfo info, uint subType, long delta)
        {
            static ulong Shift(ulong value, long delta)
            {
                if (delta >= 0)
                {
                    var gained = (ulong)delta;
                    return value > ulong.MaxValue - gained ? ulong.MaxValue : value + gained;
                }

                var lost = (ulong)(-delta);
                return value > lost ? value - lost : 0;
            }

            return subType switch
            {
                Protocol.FoodSubType => info with { Food = Shift(info.Food, delta) },
                Protocol.WoodSubType => info with { Wood = Shift(info.Wood, delta) },
                Protocol.SteelSubType => info with { Steel = Shift(info.Steel, delta) },
                Protocol.OilSubType => info with { Oil = Shift(info.Oil, delta) },
                _ => info
            };
        }

        /// <summary>
        /// Show a resource change of the city at once: the card reads
        /// player_base and the Trading Post form reads transport_info. The game
        /// pushes no "resources changed" notice, so this is what keeps the four
        /// numbers exact after a transport or a bag use; the next
        /// PlayerBaseNotice is still the final word.
        /// </summary>
        private void PublishCityResources(PlayerBaseInfo? updated)
        {
            if (updated is null)
                return;
            EmitPlayerBase(updated);
            EmitTransportInfo();
        }

        /// <summary>Move one resource of the city and publish the new numbers.</summary>
        private void ChangeCityResources(uint subType, long delta)
        {
            if (delta == 0)
                return;

            PlayerBaseInfo? updated = null;
            lock (_playerLock)
            {
                if (_playerBase is { } info)
                {
                    updated = ShiftResource(info, subType, delta);
                    _playerBase = updated;
                }
            }

            PublishCityResources(updated);
        }

        private void EmitPlayerBase(PlayerBaseInfo? info)
        {
            _emit("player_base", Id, new JsonObject
            {
                ["known"] = info is not null,
                ["player_id"] = info is null ? null : JsonValue.Create(info.PlayerId),
                ["name"] = info?.Name ?? string.Empty,
                ["level"] = info is null ? null : JsonValue.Create(info.Level),
                ["might"] = info is null ? null : JsonValue.Create(info.Might),
                ["food"] = info is null ? null : JsonValue.Create(info.Food),
                ["wood"] = info is null ? null : JsonValue.Create(info.Wood),
                ["steel"] = info is null ? null : JsonValue.Create(info.Steel),
                ["oil"] = info is null ? null : JsonValue.Create(info.Oil)
            });
        }

        // ---- Trading Post (sending resources to another Commander) ----------

        /// <summary>
        /// Asks the server for this account's own city, which is where the
        /// Trading Post level comes from. The game sends the reply as a notice
        /// (0x272C), so nothing waits for it here.
        /// </summary>
        private void RequestCityInfo(bool force = false)
        {
            var game = _game;
            if (game is null)
                return;
            if (!force && _cityInfoRequested)
                return;

            _cityInfoRequested = true;
            try
            {
                game.Send(Protocol.BuildCityInfoRequest());
            }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException
                                               or SocketException or InvalidOperationException)
            {
                Log($"Could not ask for the city of this account: {exception.Message}", "warn");
            }
        }

        /// <summary>
        /// The whole Trading Post picture of this account: its level and the
        /// limits of that level, the four city resources, and every castle the
        /// session has loaded so the form can offer a receiver.
        /// </summary>
        /// <summary>
        /// The castle roster of the loaded view (0x298D). Its entity ids are what
        /// a transport truck is addressed to, so the form can only send to a
        /// Commander it has already seen on the map.
        /// </summary>
        private void HandleSyncEntities(byte[] body)
        {
            IReadOnlyList<WorldCastle> castles;
            try
            {
                castles = Protocol.ParseSyncEntitiesNotice(body);
            }
            catch (ProtocolError exception)
            {
                Log($"Could not decode the castle roster: {exception.Message}", "warn");
                return;
            }

            if (castles.Count == 0)
                return;

            lock (_transportLock)
            {
                foreach (var castle in castles)
                    _castles[castle.PlayerId] = castle;
            }

            EmitTransportInfo();
        }

        /// <summary>The account's own city, which carries the Trading Post level.</summary>
        private void HandleCityInfoReply(byte[] body)
        {
            uint? level;
            try
            {
                level = Protocol.ParseTradingPostLevel(body);
            }
            catch (ProtocolError exception)
            {
                Log($"Could not decode the city of this account: {exception.Message}", "warn");
                return;
            }

            var before = _tradingPostKnown ? _tradingPostLevel : uint.MaxValue;
            _tradingPostLevel = level ?? 0;
            _tradingPostKnown = true;
            EmitTransportInfo();
            if (before == _tradingPostLevel)
                return;

            if (_tradingPostLevel == 0)
                Log("This account has no Trading Post yet, so it cannot send resources.", "warn");
            else
                Log($"Trading Post level {_tradingPostLevel} " +
                    $"({TradingPostLevels.Find(_tradingPostLevel)?.TradingLoad ?? 0:N0} per transport).");
        }

        private void EmitTransportInfo()
        {
            WorldCastle[] castles;
            uint level;
            bool known;
            lock (_transportLock)
            {
                castles = _castles.Values.ToArray();
                level = _tradingPostLevel;
                known = _tradingPostKnown;
            }

            ulong food = 0, wood = 0, steel = 0, oil = 0;
            string playerName = string.Empty;
            lock (_playerLock)
            {
                if (_playerBase is { } player)
                {
                    food = player.Food;
                    wood = player.Wood;
                    steel = player.Steel;
                    oil = player.Oil;
                    playerName = player.Name;
                }
            }

            var roster = new JsonArray();
            foreach (var castle in castles
                         .OrderBy(castle => castle.PlayerName, StringComparer.OrdinalIgnoreCase)
                         .ThenBy(castle => castle.PlayerId))
            {
                roster.Add(new JsonObject
                {
                    ["player_id"] = JsonValue.Create(castle.PlayerId),
                    ["entity_id"] = JsonValue.Create(castle.EntityId),
                    ["name"] = castle.PlayerName,
                    ["guild"] = castle.GuildShortName
                });
            }

            var row = TradingPostLevels.Find(level);
            _emit("transport_info", Id, new JsonObject
            {
                ["known"] = known,
                ["connected"] = _game is not null,
                ["level"] = level,
                ["player_name"] = playerName,
                ["food"] = JsonValue.Create(food),
                ["wood"] = JsonValue.Create(wood),
                ["steel"] = JsonValue.Create(steel),
                ["oil"] = JsonValue.Create(oil),
                ["per_send"] = JsonValue.Create(row?.TradingLoad ?? 0),
                ["daily_supply"] = JsonValue.Create(row?.DeliveryLoad ?? 0),
                ["daily_receive"] = JsonValue.Create(row?.ReceivingLoad ?? 0),
                ["tax_permille"] = row?.TradingTax ?? 0,
                ["speed_permille"] = row?.TransportationSpeed ?? 0,
                ["targets"] = roster
            });
        }

        /// <summary>
        /// Asks the game what this account and the receiver already moved today
        /// and which Trading Post level each of them has (0x2A6E).
        /// </summary>
        private void RequestTransportCapacity(ulong? targetId)
        {
            var game = _game;
            if (game is null)
            {
                EmitTransportFailure("offline", targetId ?? 0, "Connect this account before asking for its Trading Post limits.");
                return;
            }

            if (targetId is null or 0)
            {
                EmitTransportFailure("bad_target", 0, "Enter the Commander ID of the receiver first.");
                return;
            }

            RequestCityInfo();
            _capacityQueryTarget = targetId.Value;
            try
            {
                game.Send(Protocol.BuildCarriageCapacityRequest(targetId.Value));
            }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException
                                               or SocketException or InvalidOperationException)
            {
                EmitTransportFailure("send_failed", targetId.Value,
                    $"The Trading Post request could not be sent: {exception.Message}");
                return;
            }

            Log($"Checking the Trading Post limits between this account and Commander {targetId.Value}.");
        }

        /// <summary>One transport truck: the server builds the march and answers 0x2A6D.</summary>
        private void SendTransport(ulong? targetId, uint? subType, ulong? amount)
        {
            var game = _game;
            if (game is null)
            {
                EmitTransportFailure("offline", targetId ?? 0, "Connect this account before sending resources.");
                return;
            }

            if (targetId is null or 0)
            {
                EmitTransportFailure("bad_target", 0, "Enter the Commander ID of the receiver first.");
                return;
            }

            if (amount is null or 0)
            {
                EmitTransportFailure("bad_amount", targetId.Value, "Choose how many resources to send.");
                return;
            }

            var resource = subType ?? Protocol.FoodSubType;
            if (resource is not (Protocol.FoodSubType or Protocol.WoodSubType
                or Protocol.SteelSubType or Protocol.OilSubType))
            {
                EmitTransportFailure("bad_resource", targetId.Value, "That resource cannot be transported.");
                return;
            }

            uint level;
            WorldCastle? goal;
            lock (_transportLock)
            {
                level = _tradingPostLevel;
                _castles.TryGetValue(targetId.Value, out goal);
            }

            if (_tradingPostKnown && level == 0)
            {
                EmitTransportFailure("no_trading_post", targetId.Value,
                    "You have not built a Trading Post yet, so this account cannot supply resources.");
                return;
            }

            if (goal is null)
            {
                EmitTransportFailure("target_not_found", targetId.Value,
                    $"Commander {targetId.Value} is not among the castles this session loaded. " +
                    "Open the map around the receiver once so the client knows their castle.");
                return;
            }

            var row = TradingPostLevels.Find(level);
            if (row is not null && amount.Value > row.TradingLoad)
            {
                EmitTransportFailure("over_capacity", targetId.Value,
                    $"One transport carries at most {row.TradingLoad:N0}; this account has Trading Post level {level}.");
                return;
            }

            lock (_carriageLock)
            {
                _carriageTarget = targetId.Value;
                _carriageSubType = resource;
                _carriageAmount = amount.Value;
                _carriageTargetEntity = goal.EntityId;
            }

            try
            {
                game.Send(Protocol.BuildCreateCarriageRequest(0, 0, goal.EntityId, resource, amount.Value));
            }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException
                                               or SocketException or InvalidOperationException)
            {
                EmitTransportFailure("send_failed", targetId.Value,
                    $"The transport could not be sent: {exception.Message}");
                return;
            }

            Log($"Sending {amount.Value:N0} to {goal.PlayerName} (Commander {goal.PlayerId}).");
        }

        private void EmitTransportFailure(string reason, ulong targetId, string message)
        {
            _emit("transport_result", Id, new JsonObject
            {
                ["success"] = false,
                ["reason"] = reason,
                ["error_code"] = -1,
                ["message"] = message,
                ["target_id"] = JsonValue.Create(targetId)
            });
            Log(message, "warn");
        }

        private void HandleTransportCapacityReply(byte[] body)
        {
            CarriageCapacityReply reply;
            try
            {
                reply = Protocol.ParseCarriageCapacityReply(body);
            }
            catch (ProtocolError exception)
            {
                Log($"Could not decode the Trading Post limits: {exception.Message}", "warn");
                return;
            }

            var entries = new JsonArray();
            foreach (var entry in reply.Entries)
            {
                var row = TradingPostLevels.Find(entry.TradingPostLevel);
                entries.Add(new JsonObject
                {
                    ["player_id"] = JsonValue.Create(entry.PlayerId),
                    ["level"] = entry.TradingPostLevel,
                    ["sent_today"] = JsonValue.Create(entry.SentToday),
                    ["received_today"] = JsonValue.Create(entry.ReceivedToday),
                    ["per_send"] = JsonValue.Create(row?.TradingLoad ?? 0),
                    ["daily_supply"] = JsonValue.Create(row?.DeliveryLoad ?? 0),
                    ["daily_receive"] = JsonValue.Create(row?.ReceivingLoad ?? 0),
                    ["tax_permille"] = row?.TradingTax ?? 0
                });
            }

            ulong selfId = 0;
            lock (_playerLock)
            {
                if (_playerBase is { } player)
                    selfId = player.PlayerId;
            }

            _emit("transport_capacity", Id, new JsonObject
            {
                ["error_code"] = reply.ErrorCode,
                ["self_id"] = JsonValue.Create(selfId),
                ["target_id"] = JsonValue.Create(_capacityQueryTarget),
                ["entries"] = entries
            });
        }

        private void HandleTransportCreateReply(byte[] body)
        {
            CarriageCreateResult result;
            try
            {
                result = Protocol.ParseCreateCarriageReply(body);
            }
            catch (ProtocolError exception)
            {
                Log($"Could not decode the transport reply: {exception.Message}", "warn");
                return;
            }

            ulong target;
            uint subType;
            ulong requested;
            lock (_carriageLock)
            {
                target = _carriageTarget ?? 0;
                subType = _carriageSubType;
                requested = _carriageAmount;
                _carriageTarget = null;
            }

            var targetName = string.Empty;
            lock (_transportLock)
            {
                if (_castles.TryGetValue(target, out var castle))
                    targetName = castle.PlayerName;
            }

            var amount = result.Success && result.Value > 0 ? result.Value : requested;
            _emit("transport_result", Id, new JsonObject
            {
                ["success"] = result.Success,
                ["error_code"] = result.ErrorCode,
                ["amount"] = JsonValue.Create(amount),
                ["sub_type"] = subType,
                ["target_id"] = JsonValue.Create(target),
                ["target_name"] = targetName,
                ["tax_permille"] = result.Tax,
                ["march_entity"] = JsonValue.Create(result.EntityId)
            });

            if (result.Success)
            {
                Log($"Transport accepted: {amount:N0} to {(targetName.Length == 0 ? "the receiver" : targetName)}" +
                    (result.Tax > 0 ? $" (tax {result.Tax / 10.0:0.#}%)." : "."));
                // The resources left the city when the server took the truck:
                // take them off the shown numbers now instead of waiting for a
                // base notice that never comes.
                ChangeCityResources(subType,
                    -(long)Math.Min(amount, (ulong)long.MaxValue));
            }
            else
            {
                Log($"The game server refused the transport (error {result.ErrorCode}).", "warn");
            }
        }

        // ---- Bounty turntable (the wheel) -----------------------------------

        /// <summary>
        /// The wheel spends Bounty Tickets, 100 per spin, at most 5 spins in one
        /// request. While it is on it keeps spending until the balance can no
        /// longer pay a single spin; then it stops itself and the button of the
        /// card goes back to its normal colour.
        /// </summary>
        private void SetAutoSpin(bool? enabled)
        {
            if (enabled is null)
                throw new ArgumentException("Bounty wheel enabled must be a boolean.");

            if (!enabled.Value)
            {
                _autoSpin = false;
                lock (_spinLock)
                {
                    _spinPending = null;
                }

                _spinWorkerStop = true;
                EmitSpinState("off", false);
                Log("Bounty wheel stopped.");
                return;
            }

            if (_game is null)
                throw new ArgumentException("Connect this account before spinning the Bounty wheel.");

            _autoSpin = true;
            EnsureSpinWorker();
            EmitSpinState("spinning");
            Log($"Bounty wheel on: each spin spends {Protocol.BountySpinCost:N0} Bounty Tickets, " +
                $"up to {Protocol.BountyMaxSpinsPerRequest} spins per turn.");
        }

        private void EmitSpinState(string state, bool? enabled = null, int errorCode = 0, uint spent = 0)
        {
            _spinStateName = state;
            _emit("bounty_spin", Id, new JsonObject
            {
                ["enabled"] = enabled ?? _autoSpin,
                ["state"] = state,
                ["error_code"] = errorCode,
                ["spent"] = JsonValue.Create(spent),
                ["activity_id"] = JsonValue.Create(_turntableActivityId)
            });
        }

        private void EnsureSpinWorker()
        {
            if (_spinWorker is { IsAlive: true })
                return;
            _spinWorkerStop = false;
            _spinWorker = new Thread(SpinLoop) { IsBackground = true, Name = $"spin-{Id}" };
            _spinWorker.Start();
        }

        private void SpinLoop()
        {
            while (!_spinWorkerStop)
            {
                try
                {
                    SpinTick();
                }
                catch (Exception exception)
                {
                    // A bad tick must never kill the account thread.
                    Log($"Bounty wheel tick failed: {exception.Message}", "warn");
                }

                Thread.Sleep(400);
            }
        }

        private void SpinTick()
        {
            if (!_autoSpin)
                return;
            var game = _game;
            if (game is null)
                return;

            ulong tickets;
            bool known;
            ulong seqBefore;
            lock (_itemLock)
            {
                tickets = _bountyTickets;
                known = _bountyTicketKnown;
                seqBefore = _ticketSeq;
            }

            if (!known)
            {
                // The item list that carries the balance has not arrived yet.
                if (_spinStateName != "waiting")
                    EmitSpinState("waiting");
                return;
            }

            if (tickets < Protocol.BountySpinCost)
            {
                _autoSpin = false;
                EmitSpinState("insufficient", false);
                Log($"Bounty wheel stopped: {tickets:N0} Bounty Ticket(s) left, but one spin costs " +
                    $"{Protocol.BountySpinCost:N0}. Bounty Tickets are not enough to spin.", "error");
                return;
            }

            var spins = SpinsForBalance(tickets);

            TaskCompletionSource<BountyTurntableReply> pending;
            lock (_spinLock)
            {
                if (_spinPending is not null)
                    return; // a draw is already in flight
                pending = new TaskCompletionSource<BountyTurntableReply>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                _spinPending = pending;
            }

            try
            {
                game.Send(Protocol.BuildChickenTurntableRequest(_turntableActivityId, spins));
                EmitSpinState("spinning");
                Log($"Bounty wheel: spinning {spins} time(s) for " +
                    $"{spins * Protocol.BountySpinCost:N0} Bounty Tickets.");
            }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException
                                               or SocketException)
            {
                lock (_spinLock)
                {
                    _spinPending = null;
                }

                _autoSpin = false;
                EmitSpinState("error", false);
                Log($"Bounty wheel stopped: {exception.Message}", "error");
                return;
            }

            BountyTurntableReply reply;
            try
            {
                if (!pending.Task.Wait(TimeSpan.FromSeconds(20)))
                    throw new TimeoutException("Timed out waiting for the Bounty wheel reply.");
                reply = pending.Task.Result;
            }
            catch (Exception exception)
            {
                lock (_spinLock)
                {
                    _spinPending = null;
                }

                _autoSpin = false;
                EmitSpinState("error", false);
                Log($"Bounty wheel stopped: {exception.Message}", "error");
                return;
            }

            lock (_spinLock)
            {
                _spinPending = null;
            }

            if (reply.ErrorCode != 0)
            {
                _autoSpin = false;
                EmitSpinState("error", false, reply.ErrorCode);
                Log($"Bounty wheel stopped: the game server rejected the spin " +
                    $"(error {reply.ErrorCode}).", "error");
                return;
            }

            var drawn = reply.Count > 0 ? reply.Count : spins;
            var rewards = reply.Rewards.Count > 0
                ? string.Join(", ", reply.Rewards.Select(reward => reward.Describe()))
                : $"{reply.RewardIds.Count} reward(s)";
            Log($"Bounty wheel: {drawn} spin(s) done; rewards {rewards}.");

            // The server spends the tickets itself and publishes the new balance
            // as an item update. When that update already arrived, the balance is
            // exact and booking the cost here would take it off twice (168, one
            // spin: 68 from the server, then 68 - 100 clamped to 0). Only a turn
            // whose update has not arrived is booked locally, and always for the
            // spins that were requested: reply.Count is the wheel's own count,
            // not how many spins were paid for.
            ulong seqAfter;
            ulong balance;
            lock (_itemLock)
            {
                seqAfter = _ticketSeq;
                balance = _bountyTickets;
            }

            if (seqAfter == seqBefore)
                SpendBountyTickets((ulong)spins * Protocol.BountySpinCost);
            else
                Log($"Bounty Tickets after the spin: {balance:N0} (reported by the server).");

            // Human-like pacing between turns instead of hammering the server.
            Thread.Sleep(700);
        }

        /// <summary>
        /// How many spins one turn asks for: one per 100 Bounty Tickets, never
        /// more than the 5 the game accepts in a single request. The caller has
        /// already checked that the balance pays at least one spin.
        /// </summary>
        internal static uint SpinsForBalance(ulong tickets) =>
            (uint)Math.Min(tickets / Protocol.BountySpinCost, Protocol.BountyMaxSpinsPerRequest);

        /// <summary>Spend Bounty Tickets and publish the new balance at once.</summary>
        private void SpendBountyTickets(ulong amount)
        {
            lock (_itemLock)
            {
                _bountyTickets = _bountyTickets > amount ? _bountyTickets - amount : 0;
                _bountyTicketLastReward = 0;
                _bountyTicketKnown = true;
            }

            EmitBountyTickets("spend");
        }

        private void HandleChickenTurntableNotice(byte[] body)
        {
            BountyTurntableInfo info;
            try
            {
                info = Protocol.ParseChickenTurntableNotice(body);
            }
            catch (ProtocolError exception)
            {
                Log($"Could not decode the Bounty wheel state: {exception.Message}", "warn");
                return;
            }

            if (info.ActivityId == 0)
                return;

            _turntableActivityId = info.ActivityId;
            if (!_turntableActivityFromServer)
            {
                _turntableActivityFromServer = true;
                Log($"Bounty wheel activity id from the server: {info.ActivityId}.");
            }

            _emit("bounty_turntable", Id, new JsonObject
            {
                ["activity_id"] = JsonValue.Create(info.ActivityId),
                ["refresh_count"] = JsonValue.Create(info.RefreshCount)
            });
        }

        private void HandleChickenTurntableReply(byte[] body)
        {
            BountyTurntableReply reply;
            try
            {
                reply = Protocol.ParseChickenTurntableReply(body);
            }
            catch (ProtocolError exception)
            {
                Log($"Could not decode the Bounty wheel reply: {exception.Message}", "warn");
                return;
            }

            TaskCompletionSource<BountyTurntableReply>? pending;
            lock (_spinLock)
            {
                pending = _spinPending;
                _spinPending = null;
            }

            pending?.TrySetResult(reply);
        }

        // ---- alliance auto help --------------------------------------------

        private void EmitHelpState(string state, bool? enabled = null, int pendingCount = 0, int errorCode = 0)
        {
            bool currentEnabled;
            ulong totalHelps;
            lock (_helpLock)
            {
                currentEnabled = _helpEnabled;
                totalHelps = _totalHelps;
            }

            _emit("alliance_help_state", Id, new JsonObject
            {
                ["enabled"] = enabled ?? currentEnabled,
                ["state"] = state,
                ["pending_count"] = pendingCount,
                ["total_helps"] = JsonValue.Create(totalHelps),
                ["error_code"] = errorCode
            });
        }

        private void DisableAllianceAutoHelp(string state = "off")
        {
            lock (_helpLock)
            {
                _helpEnabled = false;
            }

            EmitHelpState(state, false);
        }

        private bool RequestAllianceAssistList(bool force = false)
        {
            var guild = GetGuildState();
            if (guild.Known && !guild.InGuild)
            {
                EmitHelpState("no_guild", true);
                return false;
            }

            GameClient? game;
            lock (_helpLock)
            {
                if (!_helpEnabled)
                    return false;
                var now = Monotonic.Now;
                if (!force && now - _lastListRequest < AllianceListRequestInterval)
                    return false;
                game = _game;
                if (game is null)
                    return false;
                _lastListRequest = now;
            }

            try
            {
                game.Send(Protocol.BuildGuildAssistListRequest());
            }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException or SocketException)
            {
                Log($"Alliance Auto Help list request failed: {exception.Message}", "error");
                DisableAllianceAutoHelp("error");
                return false;
            }

            return true;
        }

        private void SetAllianceAutoHelp(bool? enabled)
        {
            if (enabled is null)
                throw new ArgumentException("Alliance Auto Help enabled must be a boolean.");
            if (!enabled.Value)
            {
                DisableAllianceAutoHelp();
                Log("Alliance Auto Help stopped.");
                return;
            }

            if (_game is null)
                throw new ArgumentException("Connect this account before starting Alliance Auto Help.");
            lock (_helpLock)
            {
                _helpEnabled = true;
                _lastListRequest = 0.0;
                _lastHelpRequest = 0.0;
            }

            var guild = GetGuildState();
            if (guild.Known && !guild.InGuild)
            {
                EmitHelpState("no_guild", true);
                Log("Alliance Auto Help is on; this account is not in an Alliance yet. " +
                    "It will start helping as soon as the server confirms a join.", "warn");
                return;
            }

            EmitHelpState("waiting", true);
            if (!guild.Known)
                RequestGuildState(force: true);
            if (!RequestAllianceAssistList(true))
            {
                // A guildless account is a waiting state, not a failure: joining
                // the Alliance later must resume help without a reconnect.
                if (IsHelpEnabled())
                    EmitHelpState("waiting", true);
                return;
            }

            Log("Alliance Auto Help started; monitoring this account's alliance.");
        }

        private void HandleAllianceAssistFrame(ushort opcode, byte[] body)
        {
            bool enabled;
            lock (_helpLock)
            {
                enabled = _helpEnabled;
            }

            if (!enabled)
                return;

            if (opcode == Protocol.GuildAssistHelpOtherReply)
            {
                GuildAssistHelpReply result;
                try
                {
                    result = Protocol.ParseGuildAssistHelpOtherReply(body);
                }
                catch (ProtocolError exception)
                {
                    Log($"Could not decode Alliance Auto Help reply: {exception.Message}", "warn");
                    return;
                }

                var errorCode = result.ErrorCode;
                var assistCount = result.AssistCount;
                if (errorCode != 0)
                {
                    Log($"Game server rejected Alliance Auto Help (error {errorCode}).", "warn");
                    EmitHelpState("help_rejected", true, 0, (int)errorCode);
                }
                else
                {
                    lock (_helpLock)
                    {
                        _totalHelps += assistCount;
                    }

                    EmitHelpState("help_confirmed", true, (int)assistCount);
                    Log($"Game server accepted Alliance Auto Help for {assistCount} assist(s) " +
                        $"(contribution {result.Contribution}).");
                    RequestAllianceAssistList();
                }

                return;
            }

            if (opcode == Protocol.GuildAssistUpdateNotice)
            {
                RequestAllianceAssistList();
                return;
            }

            if (opcode == Protocol.GuildAssistListReply)
            {
                GuildAssistListReply result;
                try
                {
                    result = Protocol.ParseGuildAssistListReply(body);
                }
                catch (ProtocolError exception)
                {
                    Log($"Could not decode alliance assistance list: {exception.Message}", "warn");
                    return;
                }

                if (result.ErrorCode != 0)
                {
                    if (result.ErrorCode == Protocol.GuildNotJoinedError)
                    {
                        // The account left its Alliance (or has not joined one
                        // yet). Keep Auto Help enabled and paused; the join
                        // notice will resume it without restarting the client.
                        SetGuildState(new GuildState { Known = true, ErrorCode = (int)result.ErrorCode },
                            "assist list");
                        EmitHelpState("no_guild", true);
                        return;
                    }

                    Log($"Alliance assistance list returned error {result.ErrorCode}.", "warn");
                    EmitHelpState("error", false);
                    lock (_helpLock)
                    {
                        _helpEnabled = false;
                    }

                    return;
                }

                var pendingCount = result.PendingCount;
                foreach (var detail in result.Details)
                    Log($"Alliance assist record: {detail.Describe()}");
                Log($"Alliance assistance list: {result.DetailsCount} request record(s), " +
                    $"{pendingCount} eligible request record(s).");
                if (pendingCount <= 0)
                {
                    EmitHelpState("no_pending", true);
                    if (result.DetailsCount == 0)
                        Log("The game returned no active assistance request records.");
                    return;
                }

                GameClient? game;
                lock (_helpLock)
                {
                    if (!_helpEnabled)
                        return;
                    if (Monotonic.Now - _lastHelpRequest < AllianceHelpCooldown)
                        return;
                    game = _game;
                    if (game is null)
                        return;
                    _lastHelpRequest = Monotonic.Now;
                }

                try
                {
                    game.Send(Protocol.BuildGuildAssistHelpOther());
                }
                catch (Exception exception) when (exception is IOException or ObjectDisposedException or SocketException)
                {
                    Log($"Alliance Auto Help request failed: {exception.Message}", "error");
                    DisableAllianceAutoHelp("error");
                    return;
                }

                EmitHelpState("help_sent", true, pendingCount);
                Log($"Sent an Alliance Auto Help request for {pendingCount} pending assist(s).");
            }
        }

        // ---- Bounty (Battle Royale) auto start -----------------------------

        private void SetBountyAuto(bool? enabled)
        {
            if (enabled is null)
                throw new ArgumentException("Bounty auto start enabled must be a boolean.");

            if (enabled.Value && _bountyUnavailable)
            {
                // Error 14000 already told us this account cannot enter the mode.
                // Keep it out instead of retrying: the other accounts continue.
                _bountyAuto = false;
                EmitBountyState("unavailable", Protocol.EatChickenUnknownError);
                Log("Bounty stays OFF for this account (error 14000 on registration); " +
                    "the other accounts keep starting matches.", "warn");
                return;
            }

            _bountyAuto = enabled.Value;
            if (!enabled.Value)
            {
                SetBountyPhase(BountyPhase.Off, 0, "off");
                Log("Bounty auto start stopped. A match already in progress is left untouched.");
                return;
            }

            lock (_bountyLock)
            {
                _bountyPhase = BountyPhase.NeedInfo;
                _bountyNextAction = 0;
                _bountyFailures = 0;
                _bountyError = 0;
                _bountyStateName = "scanning";
            }

            if (_game is null)
            {
                EmitBountyState("waiting_for_connection");
                Log("Bounty auto start is on; connect the account to start matches.", "warn");
                return;
            }

            EnsureBountyWorker();
            EmitBountyState("scanning");
            Log("Bounty auto start enabled; scanning this account's Battle Royale state.");
        }

        private void EnsureBountyWorker()
        {
            if (_bountyWorker is { IsAlive: true })
                return;
            _bountyWorkerStop = false;
            _bountyWorker = new Thread(BountyLoop) { IsBackground = true, Name = $"bounty-{Id}" };
            _bountyWorker.Start();
        }

        private void BountyLoop()
        {
            while (!_bountyWorkerStop)
            {
                try
                {
                    BountyTick();
                }
                catch (Exception exception)
                {
                    // A bad tick must never kill the account thread.
                    Log($"Bounty auto start tick failed: {exception.Message}", "warn");
                }

                Thread.Sleep(750);
            }
        }

        private void BountyTick()
        {
            if (!_bountyAuto)
                return;
            var game = _game;
            if (game is null)
                return;

            BountyPhase phase;
            double nextAction;
            lock (_bountyLock)
            {
                phase = _bountyPhase;
                nextAction = _bountyNextAction;
            }

            if (Monotonic.Now < nextAction)
                return;

            switch (phase)
            {
                case BountyPhase.Off:
                case BountyPhase.NeedInfo:
                case BountyPhase.Pairing:
                case BountyPhase.InBattle:
                    SendBountyInfo(game);
                    break;
                case BountyPhase.Ended:
                case BountyPhase.Failed:
                    // The backoff expired; ask again and accept the answer.
                    SetBountyPhase(BountyPhase.NeedInfo, Monotonic.Now + 5.0, "scanning");
                    SendBountyInfo(game);
                    break;
                case BountyPhase.Ready:
                {
                    var army = GetBountyArmy();
                    if (army is null)
                        SendBountyInfo(game);
                    else
                        SendBountyRegister(game, army);
                    break;
                }
                case BountyPhase.Registering:
                    // No reply within the window: ask again instead of hanging.
                    SetBountyPhase(BountyPhase.NeedInfo, Monotonic.Now + 15, "scanning");
                    break;
                case BountyPhase.Maintenance:
                    lock (_bountyLock)
                    {
                        _bountyNextAction = Monotonic.Now + 30;
                    }
                    break;
            }
        }

        private void SendBountyInfo(GameClient game)
        {
            try
            {
                game.Send(Protocol.BuildEatChickenPairingInfoRequest());
                lock (_bountyLock)
                {
                    _bountyNextAction = Monotonic.Now + 5.0;
                }
            }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException or SocketException)
            {
                // The reader thread reports the disconnect.
            }
        }

        private void SendBountyRegister(GameClient game, EatChickenArmy army)
        {
            try
            {
                game.Send(Protocol.BuildEatChickenPairingRegister(army));
                SetBountyPhase(BountyPhase.Registering, Monotonic.Now + 15.0, "starting");
                Log($"Starting Bounty with 1 troop (type {army.TroopId}) and 1 hero " +
                    $"(hero {army.HeroId}).");
            }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException or SocketException)
            {
                Log($"Bounty registration could not be sent: {exception.Message}", "warn");
            }
        }

        private EatChickenArmy? GetBountyArmy()
        {
            lock (_bountyLock)
            {
                if (_bountyArmy is not null)
                    return _bountyArmy;

                var saved = _bountySavedArmy;
                if (saved is { HeroId: > 0, TroopId: > 0 })
                {
                    _bountyArmy = new EatChickenArmy
                    {
                        HeroId = saved.HeroId,
                        HeroLevel = saved.HeroLevel,
                        HeroAwakened = saved.HeroAwakened,
                        TroopId = saved.TroopId,
                        TroopCount = 1
                    };
                    return _bountyArmy;
                }

                var hero = _heroes
                    .OrderByDescending(item => item.Level)
                    .ThenByDescending(item => item.Awakened)
                    .FirstOrDefault();
                var troop = _troops
                    .Where(item => item.Count >= 1)
                    .OrderBy(item => item.Count)
                    .FirstOrDefault();
                if (hero is null || troop is null)
                    return null;

                _bountyArmy = new EatChickenArmy
                {
                    HeroId = hero.Id,
                    HeroLevel = hero.Level,
                    HeroAwakened = hero.Awakened,
                    TroopId = troop.Id,
                    TroopCount = 1
                };
                return _bountyArmy;
            }
        }

        private void SetBountyPhase(BountyPhase phase, double nextAction, string stateName,
            string? message = null, string level = "info")
        {
            bool changed;
            lock (_bountyLock)
            {
                changed = _bountyStateName != stateName;
                _bountyPhase = phase;
                _bountyNextAction = nextAction;
                _bountyStateName = stateName;
            }

            if (!changed)
                return;
            EmitBountyState(stateName);
            if (!string.IsNullOrEmpty(message))
                Log(message, level);
        }

        private string _bountyStateName = "off";

        private void EmitBountyState(string state, int errorCode = 0)
        {
            ulong region;
            lock (_bountyLock)
            {
                region = _bountyRegionId;
            }

            if (errorCode == 0 && state is "error" or "unavailable")
            {
                lock (_bountyLock)
                {
                    errorCode = _bountyError;
                }
            }

            _emit("bounty_state", Id, new JsonObject
            {
                ["enabled"] = _bountyAuto,
                ["state"] = state,
                ["region_id"] = region == 0 ? null : JsonValue.Create(region),
                ["error_code"] = errorCode
            });
        }

        private void HandleBountyPairingInfo(byte[] body)
        {
            // A poll sent before the registration may answer late. Ignore it
            // while the registration reply is still pending, otherwise it
            // could start a second matchmaking request.
            lock (_bountyLock)
            {
                if (_bountyPhase is BountyPhase.Registering or BountyPhase.Failed
                    or BountyPhase.Maintenance or BountyPhase.Ended)
                    return;
            }

            EatChickenPairingInfo info;
            try
            {
                info = Protocol.ParseEatChickenPairingInfoReply(body);
            }
            catch (ProtocolError exception)
            {
                Log($"Could not decode the Battle Royale pairing state: {exception.Message}", "warn");
                return;
            }

            if (info.ErrorCode != 0)
            {
                SetBountyPhase(BountyPhase.NeedInfo, Monotonic.Now + 30, "scanning");
                Log($"Battle Royale pairing state returned error {info.ErrorCode}.", "warn");
                return;
            }

            if (info.Army is { HeroId: > 0, TroopId: > 0 } saved)
            {
                lock (_bountyLock)
                {
                    _bountySavedArmy = saved;
                    _bountyArmy = null;
                }
            }

            switch (info.Status)
            {
                case 1: // NotPairing
                    lock (_bountyLock)
                    {
                        _bountyRegionId = 0;
                    }

                    SetBountyPhase(BountyPhase.Ready, Monotonic.Now + RandomDelay(4, 9), "ready",
                        "Bounty state: not in a match; preparing the one-troop start.");
                    break;
                case 2: // InPairing
                    lock (_bountyLock)
                    {
                        _bountyRegionId = 0;
                    }

                    SetBountyPhase(BountyPhase.Pairing, Monotonic.Now + 15.0, "pairing",
                        "Bounty state: already in the matchmaking queue.");
                    break;
                case 3: // InBattle
                    SetBountyPhase(BountyPhase.InBattle, Monotonic.Now + RandomDelay(25, 40), "in_battle",
                        "Bounty state: a match is already in progress; waiting for it to end.");
                    break;
                case 4: // Maintenance
                    SetBountyPhase(BountyPhase.Maintenance, Monotonic.Now + 120.0, "maintenance",
                        "Bounty state: the mode is in maintenance; retrying later.", "warn");
                    break;
                default:
                    SetBountyPhase(BountyPhase.NeedInfo, Monotonic.Now + 30.0, "scanning");
                    break;
            }
        }

        private void HandleBountyRegisterReply(byte[] body)
        {
            EatChickenRegisterReply reply;
            try
            {
                reply = Protocol.ParseEatChickenPairingRegisterReply(body);
            }
            catch (ProtocolError exception)
            {
                Log($"Could not decode the Battle Royale registration reply: {exception.Message}", "warn");
                return;
            }

            if (reply.ErrorCode == 0)
            {
                lock (_bountyLock)
                {
                    _bountyFailures = 0;
                    _bountyError = 0;
                }

                SetBountyPhase(BountyPhase.Pairing, Monotonic.Now + 20.0, "registered",
                    "Bounty registration accepted; waiting for the match to start.");
                return;
            }

            if (reply.ErrorCode == 14012) // already in pairing
            {
                SetBountyPhase(BountyPhase.Pairing, Monotonic.Now + 20.0, "pairing",
                    "Bounty state: the account is already in the matchmaking queue.");
                return;
            }

            if (reply.ErrorCode == Protocol.EatChickenUnknownError)
            {
                // Error 14000: below the mode's power requirement, or no usable
                // hero/troop. Retrying only hammers the server, so this account
                // is marked Bounty OFF and left alone; the others keep going.
                _bountyUnavailable = true;
                _bountyAuto = false;
                lock (_bountyLock)
                {
                    _bountyError = Protocol.EatChickenUnknownError;
                }

                SetBountyPhase(BountyPhase.Off, 0, "unavailable",
                    "Bounty OFF for this account: the game reports error 14000 (below the Battle Royale " +
                    "power requirement, or no usable hero/troop). This account will not retry; " +
                    "the other accounts keep starting matches.", "error");
                return;
            }

            int failures;
            lock (_bountyLock)
            {
                _bountyFailures++;
                _bountyError = (int)reply.ErrorCode;
                failures = _bountyFailures;
            }

            var delay = failures switch
            {
                1 => 30.0,
                2 => 60.0,
                3 => 120.0,
                4 => 300.0,
                _ => 600.0
            };

            SetBountyPhase(BountyPhase.Failed, Monotonic.Now + delay, "error");
            var hint = reply.ErrorCode switch
            {
                Protocol.EatChickenFormError =>
                    "the game rejected the one-hero formation; this account may need a different hero/troop setup",
                Protocol.EatChickenUnknownError =>
                    "the account may be below the Battle Royale power requirement or own no usable hero/troop",
                14004 or 14005 => "the account already has a Battle Royale march or reached its march limit",
                14001 or 14002 => "the Battle Royale mode is not accepting entries right now",
                _ => "the game refused the registration"
            };
            Log($"Bounty registration failed (error {reply.ErrorCode}): {hint}. " +
                $"Retrying in {delay:0}s.", "warn");
        }

        private void HandleBountyPairingSucceed(byte[] body)
        {
            ulong regionId;
            try
            {
                regionId = Protocol.ParseEatChickenRegionId(body);
            }
            catch (ProtocolError)
            {
                return;
            }

            lock (_bountyLock)
            {
                _bountyRegionId = regionId;
            }

            SetBountyPhase(BountyPhase.InBattle, Monotonic.Now + 30.0, "in_battle",
                $"Bounty match started (region {regionId}); entering with the one-troop formation.");

            var game = _game;
            if (game is null || regionId == 0)
                return;
            try
            {
                game.Send(Protocol.BuildEatChickenIntoInstanceRequest(regionId));
            }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException or SocketException)
            {
                Log($"Could not enter the Bounty match: {exception.Message}", "warn");
            }
        }

        private void HandleBountyGameEnd(byte[] body)
        {
            ulong regionId = 0;
            try
            {
                regionId = Protocol.ParseEatChickenRegionId(body);
            }
            catch (ProtocolError)
            {
            }

            var delay = RandomDelay(20, 60);
            SetBountyPhase(BountyPhase.Ended, Monotonic.Now + delay, "ended",
                $"Bounty match ended{(regionId > 0 ? $" (region {regionId})" : string.Empty)}; " +
                $"next start in {delay:0}s.");
            lock (_bountyLock)
            {
                // The Bounty Ticket payout arrives as an item update just after
                // the end notice; that update feeds the last-reward number.
                _bountyRegionId = 0;
            }
        }

        private void HandleBountyPlayerDead()
        {
            // The march died but the match is still running. Wait for the match
            // to end instead of registering into a second game.
            SetBountyPhase(BountyPhase.InBattle, Monotonic.Now + RandomDelay(25, 40), "in_battle",
                "Bounty march was eliminated; waiting for the match to end.");
        }

        private static double RandomDelay(double minSeconds, double maxSeconds) =>
            minSeconds + Random.Shared.NextDouble() * Math.Max(0.0, maxSeconds - minSeconds);

        // ---- protocol frame routing ----------------------------------------

        // Debug frame logging is capped per account: a busy account (a big item
        // list, a map sync) would otherwise flood the window when 60 accounts
        // run at once. Dropped lines are reported instead of piling up.
        private const double DebugFrameLinesPerSecond = 12.0;
        private const double DebugFrameBurst = 40.0;
        private const int DebugFrameDumpBytes = 512;

        // The fleet-wide budget protects the window when many accounts run in
        // Debug at the same time; the per-account bucket below keeps one busy
        // account from using all of it.
        private const double DebugFleetLinesPerSecond = 180.0;
        private const double DebugFleetBurst = 200.0;

        private static readonly object DebugFleetLock = new();
        private static double _debugFleetAllowance;
        private static double _debugFleetLast;

        private readonly object _frameLogLock = new();
        private double _frameLogAllowance;
        private double _frameLogLast;
        private int _frameLogDropped;

        /// <summary>
        /// Debug-only: prints the decoded game message - the game's own name for
        /// the opcode plus its protobuf fields - which is what troubleshooting a
        /// protocol change needs. At any other level this returns immediately.
        /// </summary>
        private void LogFrame(ushort opcode, byte[] body)
        {
            if (!AppLog.IsDebug)
                return;
            if (opcode is Protocol.Ping or Protocol.Pong)
                return;

            int dropped;
            lock (_frameLogLock)
            {
                var now = Monotonic.Now;
                if (_frameLogLast <= 0)
                {
                    _frameLogLast = now;
                    _frameLogAllowance = DebugFrameBurst;
                }

                _frameLogAllowance = Math.Min(DebugFrameBurst,
                    _frameLogAllowance + (now - _frameLogLast) * DebugFrameLinesPerSecond);
                _frameLogLast = now;

                if (_frameLogAllowance < 1.0 || !TakeFleetDebugBudget())
                {
                    _frameLogDropped++;
                    return;
                }

                _frameLogAllowance -= 1.0;
                dropped = _frameLogDropped;
                _frameLogDropped = 0;
            }

            if (dropped > 0)
                Log($"{dropped} game message(s) skipped by the debug rate limit.", "debug");

            var fields = DescribeBody(body);
            Log(fields.Length == 0
                    ? $"0x{opcode:x4} {Protocol.Name(opcode)}"
                    : $"0x{opcode:x4} {Protocol.Name(opcode)} {fields}",
                "debug");
        }

        /// <summary>
        /// Shared across every account: how many debug lines the whole client
        /// may write per second. Without it, 60 accounts in Debug could queue
        /// thousands of lines while the window repaints.
        /// </summary>
        private static bool TakeFleetDebugBudget()
        {
            lock (DebugFleetLock)
            {
                var now = Monotonic.Now;
                if (_debugFleetLast <= 0)
                {
                    _debugFleetLast = now;
                    _debugFleetAllowance = DebugFleetBurst;
                }

                _debugFleetAllowance = Math.Min(DebugFleetBurst,
                    _debugFleetAllowance + (now - _debugFleetLast) * DebugFleetLinesPerSecond);
                _debugFleetLast = now;
                if (_debugFleetAllowance < 1.0)
                    return false;

                _debugFleetAllowance -= 1.0;
                return true;
            }
        }

        /// <summary>One-line protobuf description, bounded so a big frame stays one line.</summary>
        private static string DescribeBody(byte[] body)
        {
            if (body.Length == 0)
                return string.Empty;

            var slice = body.Length > DebugFrameDumpBytes
                ? body[..DebugFrameDumpBytes]
                : body;
            try
            {
                var dump = Protocol.Dump(slice)
                    .Replace("\r", string.Empty)
                    .Replace("\n", "; ");
                if (body.Length > slice.Length)
                    dump += $"; {body.Length - slice.Length} more byte(s)";
                return dump.Length <= 600 ? dump : dump[..600] + "…";
            }
            catch (Exception)
            {
                // A debug line must never be able to break the account thread.
                return $"bytes({body.Length}): {Convert.ToHexString(slice).ToLowerInvariant()}";
            }
        }

        private void ReceiveFrame(ushort opcode, byte[] body)
        {
            LogFrame(opcode, body);
            switch (opcode)
            {
                case Protocol.PlayerBaseNotice:
                    try
                    {
                        StorePlayerBase(Protocol.ParsePlayerBaseNotice(body));
                    }
                    catch (ProtocolError exception)
                    {
                        Log($"Could not decode the account base data: {exception.Message}", "warn");
                    }

                    break;
                case Protocol.SyncEntitiesNotice:
                    HandleSyncEntities(body);
                    break;
                case Protocol.CityInfoReply:
                    HandleCityInfoReply(body);
                    break;
                case Protocol.CarriageCapacityReply:
                    HandleTransportCapacityReply(body);
                    break;
                case Protocol.CarriageCreateReply:
                    HandleTransportCreateReply(body);
                    break;
                case Protocol.ChickenTurntableNotice:
                    HandleChickenTurntableNotice(body);
                    break;
                case Protocol.ChickenTurntableReply:
                    HandleChickenTurntableReply(body);
                    break;
                case Protocol.GuildBaseInfoReply:
                    try
                    {
                        SetGuildState(Protocol.ParseGuildBaseInfoReply(body), "server");
                    }
                    catch (ProtocolError exception)
                    {
                        Log($"Could not decode the Alliance state: {exception.Message}", "warn");
                    }

                    break;
                case Protocol.GuildJoinNotice:
                    try
                    {
                        SetGuildState(Protocol.ParseGuildJoinNoticeState(body), "join notice");
                    }
                    catch (ProtocolError exception)
                    {
                        Log($"Could not decode the Alliance join notice: {exception.Message}", "warn");
                    }

                    break;
                case Protocol.GuildQuitNotice:
                    SetGuildState(new GuildState { Known = true }, "quit notice");
                    break;
                case Protocol.AllHeroNotice:
                    try
                    {
                        StoreHeroes(Protocol.ParseAllHeroNotice(body));
                    }
                    catch (ProtocolError)
                    {
                    }

                    break;
                case Protocol.AllArmyNotice:
                    try
                    {
                        StoreTroops(Protocol.ParseAllArmyNotice(body));
                    }
                    catch (ProtocolError)
                    {
                    }

                    break;
                case Protocol.AllItemNotice:
                    try
                    {
                        StoreItems(Protocol.ParseAllItemNotice(body));
                    }
                    catch (ProtocolError)
                    {
                    }

                    break;
                case Protocol.ItemUseReply:
                    HandleItemUseReply(body);
                    break;
                case Protocol.UpdateItemNotice:
                    try
                    {
                        if (Protocol.ParseUpdateItemNotice(body) is { } item)
                            StoreItemUpdate(item);
                    }
                    catch (ProtocolError)
                    {
                    }

                    break;
                case Protocol.EatChickenPairingInfoReply:
                    if (_bountyAuto)
                        HandleBountyPairingInfo(body);
                    break;
                case Protocol.EatChickenPairingRegisterReply:
                    if (_bountyAuto)
                        HandleBountyRegisterReply(body);
                    break;
                case Protocol.EatChickenPairingSucceedNotice:
                    if (_bountyAuto)
                        HandleBountyPairingSucceed(body);
                    break;
                case Protocol.EatChickenGameEndDataNotice:
                    if (_bountyAuto)
                        HandleBountyGameEnd(body);
                    break;
                case Protocol.EatChickenPlayerDeadNotice:
                    if (_bountyAuto)
                        HandleBountyPlayerDead();
                    break;
            }

            if (opcode is Protocol.GuildJoinReply or Protocol.GuildQuitReply or Protocol.GuildJoinNotice
                or Protocol.GuildQuitNotice or Protocol.GuildRecommendListReply or Protocol.GuildSearchReply)
            {
                try
                {
                    _guildReplies.Add((opcode, body));
                }
                catch (InvalidOperationException)
                {
                }
            }

            if (opcode is Protocol.GuildAssistUpdateNotice or Protocol.GuildAssistListReply
                or Protocol.GuildAssistHelpOtherReply)
            {
                HandleAllianceAssistFrame(opcode, body);
                if (AppLog.Allows("info"))
                    _emit("notice", Id, new JsonObject { ["name"] = Protocol.Name(opcode) });
            }
        }

        private byte[] WaitGuildActionReply(ushort opcode, double timeout = 12.0)
        {
            var deadline = Monotonic.Now + timeout;
            while (true)
            {
                var remaining = deadline - Monotonic.Now;
                if (remaining <= 0)
                    throw new TimeoutException("Timed out waiting for the alliance action reply.");
                if (!_guildReplies.TryTake(out var entry, TimeSpan.FromSeconds(remaining)))
                    throw new TimeoutException("Timed out waiting for the alliance action reply.");
                if (entry.Opcode == opcode)
                    return entry.Body;
            }
        }

        private void PerformGuildAction(string action, ulong? guildId, string? guildName)
        {
            var game = _game;
            if (game is null)
            {
                EmitGuildActionResult(action, false, guildId, guildName,
                    "Connect this account before changing its alliance.");
                return;
            }

            byte[] request;
            ushort expectedOpcode;
            ushort expectedNotice;
            if (action is "join" or "join_by_name")
            {
                if (action == "join_by_name")
                {
                    if (string.IsNullOrWhiteSpace(guildName))
                    {
                        EmitGuildActionResult(action, false, guildId, guildName, "Enter an Alliance TAG.");
                        return;
                    }
                }
                else
                {
                    if (guildId is null)
                    {
                        EmitGuildActionResult(action, false, guildId, guildName, "Enter a numeric Alliance ID.");
                        return;
                    }

                    try
                    {
                        Protocol.BuildGuildJoinRequest(guildId.Value);
                    }
                    catch (Exception exception)
                    {
                        EmitGuildActionResult(action, false, guildId, guildName, exception.Message);
                        return;
                    }
                }

                if (action == "join_by_name")
                {
                    expectedOpcode = Protocol.GuildJoinReply;
                    expectedNotice = Protocol.GuildJoinNotice;
                    request = Array.Empty<byte>();
                }
                else
                {
                    request = Protocol.BuildGuildJoinRequest(guildId!.Value);
                    expectedOpcode = Protocol.GuildJoinReply;
                    expectedNotice = Protocol.GuildJoinNotice;
                }
            }
            else if (action == "leave")
            {
                request = Protocol.BuildGuildQuitRequest();
                expectedOpcode = Protocol.GuildQuitReply;
                expectedNotice = Protocol.GuildQuitNotice;
            }
            else
            {
                throw new ArgumentException("Unknown alliance action.");
            }

            DrainGuildReplies();
            GuildInfo? resolvedGuild = null;
            try
            {
                if (action == "join_by_name")
                {
                    var tagMatch = Regex.Match(guildName!.Trim(), @"\[([^\]]+)\]\s*(.+)");
                    var tag = tagMatch.Success ? tagMatch.Groups[1].Value : guildName!.Trim();
                    game.Send(Protocol.BuildGuildSearchRequest(tag));
                    var searchReply = WaitGuildActionReply(Protocol.GuildSearchReply);
                    var guilds = Protocol.ParseGuildSearchReply(searchReply);
                    resolvedGuild = FindSearchedGuild(guildName!, guilds);
                    guildId = resolvedGuild.Id;
                    request = Protocol.BuildGuildJoinRequest(guildId.Value);
                }

                game.Send(request);
                var gotReply = false;
                var gotNotice = false;
                var deadline = Monotonic.Now + 12.0;
                while (true)
                {
                    if (gotReply && gotNotice)
                        break;
                    var remaining = deadline - Monotonic.Now;
                    if (remaining <= 0)
                        throw new TimeoutException("Timed out waiting for the alliance action reply.");
                    if (!_guildReplies.TryTake(out var entry, TimeSpan.FromSeconds(remaining)))
                        throw new TimeoutException("Timed out waiting for the alliance action reply.");
                    if (entry.Opcode == expectedOpcode)
                    {
                        if (action is "join" or "join_by_name")
                        {
                            var (replyGuildId, replyError) = Protocol.ParseGuildJoinReplyDetails(entry.Body);
                            if (replyError != 0)
                            {
                                throw new InvalidOperationException(
                                    $"The game server rejected the Alliance join (error {replyError}).");
                            }

                            if (replyGuildId != guildId!.Value)
                            {
                                throw new InvalidOperationException(
                                    "The server reply did not confirm the requested Alliance ID.");
                            }
                        }
                        else
                        {
                            var quitError = Protocol.ParseGuildQuitReplyError(entry.Body);
                            if (quitError != 0)
                            {
                                throw new InvalidOperationException(
                                    $"The game server rejected leaving the Alliance (error {quitError}).");
                            }
                        }

                        gotReply = true;
                        continue;
                    }

                    if (entry.Opcode == expectedNotice)
                    {
                        if (action is "join" or "join_by_name")
                        {
                            var noticeGuildId = Protocol.ParseGuildJoinNotice(entry.Body);
                            if (noticeGuildId != guildId!.Value)
                                continue;
                        }

                        gotNotice = true;
                    }
                }
            }
            catch (Exception exception) when (exception is IOException or TimeoutException or ProtocolError
                                                  or InvalidOperationException or ArgumentException
                                                  or SocketException or ObjectDisposedException)
            {
                var message = exception is TimeoutException
                    ? "Timed out waiting for the alliance action reply."
                    : exception.Message;
                EmitGuildActionResult(action, false, guildId, guildName, message);
                return;
            }

            if (action is "join" or "join_by_name")
            {
                // The join notice normally arrives with the reply; refresh the
                // state anyway so a slow notice cannot leave the account guildless.
                if (IsHelpEnabled())
                    EmitHelpState("waiting", true);
                ScheduleGuildRefresh();
            }
            else
            {
                SetGuildState(new GuildState { Known = true }, "leave");
                ScheduleGuildRefresh();
            }

            EmitGuildActionResult(action, true, guildId, guildName, null, resolvedGuild?.Name);
        }

        private void EmitGuildActionResult(string action, bool success, ulong? guildId, string? guildName,
            string? error, string? resolvedGuildName = null)
        {
            var data = new JsonObject
            {
                ["action"] = action,
                ["success"] = success,
                ["guild_id"] = guildId is null ? null : JsonValue.Create(guildId.Value),
                ["guild_name"] = resolvedGuildName ?? guildName
            };
            if (error is not null)
                data["error"] = error;
            _emit("guild_action_result", Id, data);
        }

        private void DrainGuildReplies()
        {
            while (_guildReplies.TryTake(out _))
            {
            }
        }

        // ---- connection -----------------------------------------------------

        private void StartReader(GameClient game)
        {
            _stopReader = false;
            var reader = new Thread(() =>
            {
                var nextPing = Monotonic.Now + 15.0;
                var nextGuildCheck = Monotonic.Now + 30.0;
                string? disconnected = null;
                while (!_stopReader)
                {
                    (ushort Opcode, byte[] Body) frame;
                    try
                    {
                        frame = game.Recv(0.25);
                    }
                    catch (TimeoutException)
                    {
                        // Keep the Alliance state fresh: a join made while this
                        // session is open must resume Auto Help without a reconnect.
                        if (Monotonic.Now >= nextGuildCheck)
                        {
                            RequestGuildState();
                            nextGuildCheck = Monotonic.Now + 30.0;
                        }

                        if (Monotonic.Now >= nextPing)
                        {
                            try
                            {
                                game.Send(Protocol.BuildPing());
                            }
                            catch (Exception exception) when (exception is IOException or ObjectDisposedException
                                                               or SocketException)
                            {
                                disconnected = $"Heartbeat send failed: {exception.Message}";
                                break;
                            }

                            nextPing = Monotonic.Now + 15.0;
                        }

                        continue;
                    }
                    catch (Exception exception) when (exception is ServerClosedException or IOException
                                                       or SocketException or ObjectDisposedException)
                    {
                        disconnected = $"Game server disconnected: {exception.Message}";
                        break;
                    }

                    if (frame.Opcode == Protocol.Ping)
                    {
                        try
                        {
                            game.Send(Protocol.BuildPong());
                        }
                        catch (Exception exception) when (exception is IOException or ObjectDisposedException
                                                           or SocketException)
                        {
                            disconnected = $"Heartbeat send failed: {exception.Message}";
                            break;
                        }

                        continue;
                    }

                    if (frame.Opcode == Protocol.Pong)
                        continue;
                    // Every other frame was already dispatched by
                    // GameClient.OnFrame while it was decoded. Dispatching it
                    // again here would run each notice (help, guild state,
                    // Bounty) twice.
                }

                if (disconnected is not null && !_stopReader)
                {
                    if (ReferenceEquals(_game, game))
                        _game = null;
                    game.Close();
                    DisableAllianceAutoHelp("off");
                    _emit("connection", Id, new JsonObject
                    {
                        ["connected"] = false,
                        ["message"] = disconnected
                    });
                }
            })
            { IsBackground = true, Name = $"recv-{Id}" };
            _reader = reader;
            reader.Start();
        }

        private void Connect(string capturePath, string? customServerHost)
        {
            if (_game is not null)
            {
                Log("Already connected.");
                return;
            }

            lock (_helpLock)
            {
                _helpEnabled = false;
                _lastListRequest = 0.0;
                _lastHelpRequest = 0.0;
            }

            DrainGuildReplies();
            lock (_itemLock)
            {
                // A new session starts with an empty card; the first
                // MsgGS2CLAllItemNotice fills in the real balance and the bag.
                _bountyTickets = 0;
                _bountyTicketLastReward = 0;
                _bountyTicketKnown = false;
                _bagItems.Clear();
                _bagKnown = false;
            }

            // A fresh session may be a fixed account: the 14000 lock and the
            // data of the previous session start over.
            _bountyUnavailable = false;
            _turntableActivityId = Protocol.BountyTurntableActivityId;
            _turntableActivityFromServer = false;
            lock (_playerLock)
            {
                _playerBase = null;
            }

            if (!File.Exists(capturePath))
                throw new ArgumentException("captured_f3.json not found. Import a capture file first.");
            using var captureDocument = JsonDocument.Parse(File.ReadAllText(capturePath));
            var capture = captureDocument.RootElement;
            var request = capture.TryGetProperty("ls_request", out var requestElement) &&
                          requestElement.ValueKind == JsonValueKind.Object
                ? requestElement
                : default;

            var source = capture.TryGetProperty("source", out var sourceElement) &&
                         sourceElement.ValueKind == JsonValueKind.String
                ? sourceElement.GetString()
                : null;
            if (source == "selenium_igg_embed_result" ||
                capture.TryGetProperty("platform_token", out _))
            {
                throw new ArgumentException("This is a platform-token capture, not a game LoginServer capture.");
            }

            var account = ReadIdentifier(request, "account");
            var webSession = ReadText(request, "web_session");
            var udid = ReadText(request, "udid");
            var loginServer = ReadText(request, "login_server");
            if (account is null || string.IsNullOrEmpty(webSession) || string.IsNullOrEmpty(udid) ||
                string.IsNullOrEmpty(loginServer))
            {
                throw new ArgumentException(
                    "Capture is missing the LoginServer account/session/device fields.");
            }

            var deviceId = ReadText(capture, "device_id");
            if (string.IsNullOrEmpty(deviceId))
                throw new ArgumentException("Capture is missing the GameServer device id.");
            // The client version is deliberately not required or sent: 1.62
            // servers accept the GameServer login without field 10, so a
            // capture taken on an older build keeps working after an update.

            Log("Connecting to LoginServer; session values are kept private.");
            var loginFrame = Protocol.LsLogin(account.Value, webSession!, udid!,
                campId: ReadNumber(request, "camp_id") ?? 0,
                areaId: ReadNumber(request, "area_id") ?? 0,
                gameId: ReadText(request, "game_id") ?? Protocol.DefaultGameId,
                language: ReadNumber(request, "language") ?? 1);

            byte[]? reply = null;
            Exception? lastError = null;
            var attempted = LoginServerCandidates(loginServer!);
            for (var index = 0; index < attempted.Count; index++)
            {
                var (loginHost, loginPort) = attempted[index];
                var login = new GameClient(loginHost, loginPort, index == 0 ? 10.0 : 4.0);
                try
                {
                    login.Connect();
                }
                catch (Exception exception) when (exception is SocketException or IOException or TimeoutException)
                {
                    lastError = exception;
                    if (index + 1 < attempted.Count)
                    {
                        Log($"LoginServer {loginHost}:{loginPort} is unreachable ({exception.Message}); " +
                            "trying the next known route.", "warn");
                    }

                    continue;
                }

                try
                {
                    login.Send(loginFrame);
                    try
                    {
                        var (body, _) = login.WaitFor(Protocol.LsLoginReply, 10.0);
                        reply = body;
                    }
                    catch (TimeoutException)
                    {
                        throw new InvalidOperationException(
                            "LoginServer did not reply; check capture freshness and network.");
                    }

                    break;
                }
                finally
                {
                    login.Close();
                }
            }

            if (reply is null)
            {
                var detail = lastError is not null ? $" Last error: {lastError.Message}" : string.Empty;
                throw new InvalidOperationException(
                    "No LoginServer route was reachable; check the network or record a fresh capture." + detail);
            }

            var session = Protocol.ParseLsLoginReply(reply);
            if (session.ErrorCode is not null and not 0)
                throw new InvalidOperationException($"LoginServer rejected the session (error {session.ErrorCode}).");
            if (string.IsNullOrEmpty(session.LoginSession))
                throw new InvalidOperationException("LoginServer returned no fresh game session.");
            var playerId = session.PlayerId is not null and not 0 ? session.PlayerId.Value : account.Value;

            string host;
            int port;
            string? routeHost = null;
            var literal = true;
            if (customServerHost is not null)
            {
                (host, port) = CustomServerEndpoint(capture, customServerHost);
                Log($"Using custom GameServer host and the account JSON port {port}.");
            }
            else
            {
                (host, port) = ResolveGameServerAddress(capture, session, EdgeTable.Value);
                routeHost = host;
                literal = IPAddress.TryParse(host, out _);
                if (!literal)
                {
                    RouteChoice picked;
                    try
                    {
                        picked = EdgeRoute.ResolveRouteAsync(host, port).GetAwaiter().GetResult();
                    }
                    catch (RouteError exception)
                    {
                        throw new InvalidOperationException(
                            $"GameServer {routeHost}:{port} is not reachable from this network: {exception.Message}");
                    }

                    host = picked.Ip;
                    if (picked.Source == "system")
                        Log($"GameServer {routeHost}:{port} -> {host} (system DNS).");
                    else
                    {
                        Log($"GameServer {routeHost}:{port} -> {host} " +
                            $"(via {picked.Source}; system DNS answers were not reachable).", "warn");
                    }
                }
            }

            Log("Connecting to the GameServer and initializing the account session.");
            var game = new GameClient(host, port, 10.0)
            {
                OnFrame = ReceiveFrame
            };
            try
            {
                try
                {
                    game.Connect();
                }
                catch (Exception exception) when (exception is SocketException or IOException or TimeoutException)
                {
                    if (routeHost is null || literal)
                        throw;
                    EdgeRoute.ClearRouteCache();
                    var picked = EdgeRoute.ResolveRouteAsync(routeHost, port, useCache: false).GetAwaiter().GetResult();
                    if (picked.Ip == host)
                        throw;
                    Log($"First GameServer address failed; retrying {routeHost} via {picked.Ip}.", "warn");
                    host = picked.Ip;
                    game = new GameClient(host, port, 10.0) { OnFrame = ReceiveFrame };
                    game.Connect();
                }

                game.Login(playerId, session.LoginSession!, deviceId!);
                if (game.LoginError is not null and not 0)
                    throw new InvalidOperationException($"GameServer rejected login (error {game.LoginError}).");
                var worldDeadline = Monotonic.Now + 5.0;
                while (game.ArmyId is null && Monotonic.Now < worldDeadline)
                {
                    try
                    {
                        game.Recv(Math.Min(0.25, worldDeadline - Monotonic.Now));
                    }
                    catch (TimeoutException)
                    {
                        continue;
                    }
                }

                var worldAvailable = game.ArmyId is not null;
                if (worldAvailable)
                {
                    Log("Game login accepted; completing the world handshake.");
                    game.Handshake(playerId: playerId, burst: true, timeout: 10.0, quiet: 1.0);
                    var until = Monotonic.Now + 2.0;
                    while (Monotonic.Now < until)
                    {
                        try
                        {
                            game.Recv(Math.Min(0.25, until - Monotonic.Now));
                        }
                        catch (TimeoutException)
                        {
                            continue;
                        }
                    }
                }
                else
                {
                    Log("Game login accepted without world/army data. Skipping the remaining " +
                        "world-dependent handshake and startup burst; this connection is Alliance-only.", "warn");
                }

                GuildState? guild = null;
                if (worldAvailable)
                {
                    guild = QueryGuildState(game);
                    if (guild is null)
                        Log("Could not read this account's Alliance state; Auto Help will ask again later.", "warn");
                    else
                        SetGuildState(guild, "login");
                }

                _game = game;
                StartReader(game);
                if (worldAvailable)
                    RequestCityInfo();
                _emit("connection", Id, new JsonObject
                {
                    ["connected"] = true,
                    ["player_id"] = JsonValue.Create(playerId),
                    ["army_id"] = game.ArmyId is null ? null : JsonValue.Create(game.ArmyId.Value),
                    ["world_available"] = worldAvailable,
                    ["guild_known"] = guild?.Known ?? false,
                    ["in_guild"] = guild?.InGuild ?? false,
                    ["guild_name"] = guild?.DisplayName ?? string.Empty
                });
                if (worldAvailable)
                    Log("Connected and ready for Alliance actions.");
                else
                    Log("Connected in Alliance-only mode; world features are unavailable.", "warn");


            }
            catch (Exception)
            {
                game.Close();
                throw;
            }
        }

        private void Disconnect(string message = "Disconnected by user.")
        {
            _autoSpin = false;
            _spinWorkerStop = true;
            lock (_spinLock)
            {
                _spinPending = null;
            }

            if (_spinStateName != "off")
                EmitSpinState("off", false);

            bool hadPlayerBase;
            lock (_playerLock)
            {
                hadPlayerBase = _playerBase is not null;
                _playerBase = null;
            }

            if (hadPlayerBase)
                EmitPlayerBase(null);

            bool hadBag;
            lock (_itemLock)
            {
                hadBag = _bagKnown || _bagItems.Count > 0;
                _bagItems.Clear();
                _bagKnown = false;
            }

            if (hadBag)
                EmitBagItems();

            bool hadTransport;
            lock (_transportLock)
            {
                hadTransport = _tradingPostKnown || _castles.Count > 0;
                _castles.Clear();
                _tradingPostKnown = false;
                _tradingPostLevel = 0;
                _cityInfoRequested = false;
            }

            lock (_carriageLock)
            {
                _carriageTarget = null;
            }

            if (hadTransport)
                EmitTransportInfo();

            DisableAllianceAutoHelp();
            _stopReader = true;
            _reader = null;
            var game = _game;
            _game = null;
            DrainGuildReplies();
            lock (_guildLock)
            {
                _guild = GuildState.Unknown;
            }

            SetBountyPhase(BountyPhase.Off, 0, _bountyAuto ? "waiting_for_connection" : "off");
            if (game is not null)
            {
                game.Close();
                _emit("connection", Id, new JsonObject
                {
                    ["connected"] = false,
                    ["message"] = message
                });
            }
        }

        public void Shutdown()
        {
            _bountyWorkerStop = true;
            _bountyAuto = false;
            _spinWorkerStop = true;
            _autoSpin = false;
            Disconnect();
            try
            {
                _queue.CompleteAdding();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        // ---- capture helpers -------------------------------------------------

        private static string? ReadText(JsonElement element, string property)
        {
            if (element.ValueKind != JsonValueKind.Object)
                return null;
            if (!element.TryGetProperty(property, out var value))
                return null;
            return value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
        }

        private static ulong? ReadNumber(JsonElement element, string property)
        {
            if (element.ValueKind != JsonValueKind.Object)
                return null;
            if (!element.TryGetProperty(property, out var value))
                return null;
            return value.ValueKind switch
            {
                JsonValueKind.Number when value.TryGetUInt64(out var number) => number,
                JsonValueKind.String when ulong.TryParse(value.GetString(), out var number) => number,
                _ => null
            };
        }

        private static ulong? ReadIdentifier(JsonElement element, string property)
        {
            if (element.ValueKind != JsonValueKind.Object)
                return null;
            if (!element.TryGetProperty(property, out var value))
                return null;
            return value.ValueKind switch
            {
                JsonValueKind.Number when value.TryGetUInt64(out var number) => number,
                JsonValueKind.String when ulong.TryParse(value.GetString(), out var number) => number,
                _ => null
            };
        }
    }

    /// <summary>The decoded edge table, loaded once when the backend starts.</summary>
    private static readonly Lazy<EdgeTable?> EdgeTable = new(LoadEdgeTable);

    private static EdgeTable? LoadEdgeTable()
    {
        try
        {
            // The table is embedded in the executable; no external file is needed.
            return EdgeRoute.LoadTable();
        }
        catch (Exception)
        {
            // A missing table is not fatal: edge_route falls back to the
            // kingdom/port formula.
            return null;
        }
    }
}
