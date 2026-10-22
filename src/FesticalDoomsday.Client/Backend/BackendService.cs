using System.Collections.Concurrent;
using System.IO;
using System.Net.Sockets;
using System.Net;
using System.Text.Json.Nodes;
using System.Text.Json;
using System.Text.RegularExpressions;

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
                GuildName = GetString(root, "guild_name")
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
                case "guild_join":
                    PerformGuildAction("join", command.GuildId, null);
                    break;
                case "guild_join_by_name":
                    PerformGuildAction("join_by_name", null, command.GuildName);
                    break;
                case "guild_leave":
                    PerformGuildAction("leave", null, null);
                    break;
                default:
                    throw new ArgumentException("Unknown backend command.");
            }
        }

        private void Log(string message, string level = "info") =>
            _emit("log", Id, new JsonObject { ["level"] = level, ["message"] = message });

        // ---- alliance auto help --------------------------------------------

        private void EmitHelpState(string state, bool? enabled = null, int pendingCount = 0, int errorCode = 0)
        {
            bool currentEnabled;
            lock (_helpLock)
            {
                currentEnabled = _helpEnabled;
            }

            _emit("alliance_help_state", Id, new JsonObject
            {
                ["enabled"] = enabled ?? currentEnabled,
                ["state"] = state,
                ["pending_count"] = pendingCount,
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

            EmitHelpState("waiting", true);
            if (!RequestAllianceAssistList(true))
            {
                DisableAllianceAutoHelp("error");
                throw new InvalidOperationException("Could not request the alliance assistance list.");
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
                    EmitHelpState("help_confirmed", true, (int)assistCount);
                    Log($"Game server accepted Alliance Auto Help for {assistCount} assist(s).");
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
                    Log($"Alliance assistance list returned error {result.ErrorCode}.", "warn");
                    EmitHelpState("error", false);
                    lock (_helpLock)
                    {
                        _helpEnabled = false;
                    }

                    return;
                }

                var pendingCount = result.PendingCount;
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

        // ---- protocol frame routing ----------------------------------------

        private void ReceiveFrame(ushort opcode, byte[] body)
        {
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
                            var replyGuildId = Protocol.ParseGuildJoinReply(entry.Body);
                            if (replyGuildId != guildId!.Value)
                            {
                                throw new InvalidOperationException(
                                    "The server reply did not confirm the requested Alliance ID.");
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
                    ReceiveFrame(frame.Opcode, frame.Body);
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
            var version = ReadText(capture, "version");
            if (string.IsNullOrEmpty(deviceId) || string.IsNullOrEmpty(version))
                throw new ArgumentException("Capture is missing GameServer device or client version.");

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

                game.Login(playerId, session.LoginSession!, deviceId!, version);
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

                _game = game;
                StartReader(game);
                _emit("connection", Id, new JsonObject
                {
                    ["connected"] = true,
                    ["player_id"] = JsonValue.Create(playerId),
                    ["army_id"] = game.ArmyId is null ? null : JsonValue.Create(game.ArmyId.Value),
                    ["world_available"] = worldAvailable
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
            DisableAllianceAutoHelp();
            _stopReader = true;
            _reader = null;
            var game = _game;
            _game = null;
            DrainGuildReplies();
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
