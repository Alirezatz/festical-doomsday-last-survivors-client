using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.Sockets;
using System.Net;
using System.Text.Json;
using System.Text;

namespace FesticalDoomsday.Client.Backend;

/// <summary>Mirrors the Python <c>RouteError(OSError)</c>: no candidate accepted a TCP connection.</summary>
public sealed class RouteError : IOException
{
    public RouteError(string message) : base(message)
    {
    }
}

/// <summary>One row of the game's <c>vn_server_edge</c> table.</summary>
public sealed class EdgeRow
{
    public long KingdomId { get; init; }
    public string InternalHost { get; init; } = string.Empty;
    public string EdgeHost { get; init; } = string.Empty;
    public int InternalGamePort { get; init; }
    public int EdgeGamePort { get; init; }
    public int InternalChatPort { get; init; }
    public int EdgeChatPort { get; init; }
}

/// <summary>The decoded edge table plus the lookups the backend needs.</summary>
public sealed class EdgeTable
{
    public EdgeTable(string? source, string defaultEdgeHost, Dictionary<string, EdgeRow> byHost,
        Dictionary<long, EdgeRow> byKingdom)
    {
        Source = source;
        DefaultEdgeHost = defaultEdgeHost;
        ByHost = byHost;
        ByKingdom = byKingdom;
    }

    public string? Source { get; }
    public string DefaultEdgeHost { get; }
    public Dictionary<string, EdgeRow> ByHost { get; }
    public Dictionary<long, EdgeRow> ByKingdom { get; }
}

/// <summary>The public GameServer endpoint derived from a LoginServer route.</summary>
public sealed class Route
{
    public string Host { get; init; } = string.Empty;
    public int Port { get; init; }
    public int ChatPort { get; init; }
    public string InternalHost { get; init; } = string.Empty;
    public int InternalPort { get; init; }
    public int InternalGamePort { get; init; }
    public int EdgeGamePort { get; init; }
    public long KingdomId { get; init; }
    public string Source { get; init; } = string.Empty;
}

/// <summary>A resolved and verified address for an edge host.</summary>
public sealed class RouteChoice
{
    public RouteChoice(string host, int port, string ip, string source,
        List<(string Ip, string Source)> candidates)
    {
        Host = host;
        Port = port;
        Ip = ip;
        Source = source;
        Candidates = candidates;
    }

    public string Host { get; }
    public int Port { get; }
    public string Ip { get; }
    public string Source { get; }
    public List<(string Ip, string Source)> Candidates { get; }
}

/// <summary>
/// EdgeRoute.cs - resolve the public GameServer endpoint the real client would use.
///
/// The client never connects to the private address that the LoginServer puts in
/// MsgLS2CLLoginReply.NetAddress. It looks the address up in the
/// <c>vn_server_edge</c> config table and rewrites it. The public IP is not
/// fixed: that hostname is resolved with DNS on the network you are on.
///
/// DNS alone is not enough on filtered or split-tunnel networks, so
/// <see cref="ResolveRoute"/> collects answers from the system resolver,
/// DNS-over-HTTPS and public UDP resolvers, probes every candidate with a short
/// TCP connect, and returns the first address that actually accepts a
/// connection. Ported from edge_route.py.
/// </summary>
internal static class EdgeRoute
{
    public const string DefaultEdgeHost = "204-141-172-33.dls.igotgames.net";

    public static readonly HashSet<int> LoginPorts = new() { 11030, 11031, 11032, 11033, 9030 };

    /// <summary>LoginServer routes from the game's own server_config.</summary>
    public static readonly (string Host, int Port)[] LoginServers =
    {
        ("192-243-42-109.ip.igotgames.net", 11032),
        ("192-243-42-109.ip.igotgames.net", 11031),
        ("192-243-42-109.ip.igotgames.net", 11030),
        ("192-243-42-109.ip.igotgames.net", 11033),
        ("192-243-44-147.ip.igotgames.net", 11030),
        ("192-243-44-147.ip.igotgames.net", 11031)
    };

    private static readonly string[] PublicDns = { "1.1.1.1", "8.8.8.8", "9.9.9.9" };

    /// <summary>
    /// Last-resort addresses seen in captures for the current edge hostname. They
    /// are only probed when both the system resolver and the public resolvers
    /// yield nothing reachable.
    /// </summary>
    private static readonly Dictionary<string, string[]> HistoricEdgeIps = new(StringComparer.OrdinalIgnoreCase)
    {
        // The hostname itself encodes .33; .24 and .254 were seen in captures.
        ["204-141-172-33.dls.igotgames.net"] = new[]
        {
            "204.141.172.33", "204.141.172.24", "34.187.190.254"
        }
    };

    private static readonly (string Label, string Template)[] DohEndpoints =
    {
        ("doh:cloudflare", "https://cloudflare-dns.com/dns-query?name={name}&type=A"),
        ("doh:google", "https://dns.google/resolve?name={name}&type=A")
    };

    private static readonly object RouteCacheLock = new();
    private static readonly Dictionary<(string Host, int Port), CacheEntry> RouteCache = new();

    private sealed class CacheEntry
    {
        public required RouteChoice Route { get; init; }
        public required double Expires { get; init; }
    }

    private static double RouteCacheTtl
    {
        get
        {
            var configured = Environment.GetEnvironmentVariable("DLS_ROUTE_CACHE_TTL");
            if (!string.IsNullOrWhiteSpace(configured) &&
                double.TryParse(configured, NumberStyles.Float, CultureInfo.InvariantCulture, out var ttl))
            {
                return ttl;
            }

            return 600.0;
        }
    }

    // --------------------------------------------------------------------------
    // table
    // --------------------------------------------------------------------------

    /// <summary>
    /// The edge table that ships inside the executable, so the client does not
    /// need any file next to it. A newer table dropped next to the executable
    /// still wins, which keeps the game config refreshable without a rebuild.
    /// </summary>
    public static EdgeTable LoadTable()
    {
        var path = DefaultTablePath();
        if (File.Exists(path))
        {
            try
            {
                return LoadTable(path);
            }
            catch (Exception)
            {
                // A broken override must not stop the embedded table from working.
            }
        }

        using var stream = OpenEmbeddedTable();
        return LoadTable(stream);
    }

    public static EdgeTable LoadTable(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return BuildTable(document.RootElement);
    }

    public static EdgeTable LoadTable(Stream stream)
    {
        using var document = JsonDocument.Parse(stream);
        return BuildTable(document.RootElement);
    }

    private static Stream OpenEmbeddedTable()
    {
        var assembly = typeof(EdgeRoute).Assembly;
        var name = assembly.GetManifestResourceNames()
            .FirstOrDefault(item => item.EndsWith("edge_servers.json", StringComparison.OrdinalIgnoreCase));
        if (name is null)
        {
            throw new InvalidOperationException(
                "The edge server table is missing from the executable.");
        }

        return assembly.GetManifestResourceStream(name)
               ?? throw new InvalidOperationException("The edge server table is missing from the executable.");
    }

    private static EdgeTable BuildTable(JsonElement root)
    {
        var columns = new List<string>();
        foreach (var column in root.GetProperty("columns").EnumerateArray())
            columns.Add(column.GetString() ?? string.Empty);

        var byHost = new Dictionary<string, EdgeRow>(StringComparer.Ordinal);
        var byKingdom = new Dictionary<long, EdgeRow>();
        foreach (var row in root.GetProperty("rows").EnumerateArray())
        {
            var values = row.EnumerateArray().ToList();
            var item = new Dictionary<string, JsonElement>();
            for (var index = 0; index < columns.Count && index < values.Count; index++)
                item[columns[index]] = values[index];

            var edge = new EdgeRow
            {
                KingdomId = GetInt64(item, "kingdom_id"),
                InternalHost = GetString(item, "internal_host"),
                EdgeHost = GetString(item, "edge_host"),
                InternalGamePort = (int)GetInt64(item, "internal_game_port"),
                EdgeGamePort = (int)GetInt64(item, "edge_game_port"),
                InternalChatPort = (int)GetInt64(item, "internal_chat_port"),
                EdgeChatPort = (int)GetInt64(item, "edge_chat_port")
            };
            byHost[edge.InternalHost] = edge;
            byKingdom[edge.KingdomId] = edge;
        }

        var source = root.TryGetProperty("source", out var sourceValue)
            ? sourceValue.GetString()
            : null;
        var defaultHost = byHost.Count > 0
            ? byHost.Values.First().EdgeHost
            : DefaultEdgeHost;
        return new EdgeTable(source, defaultHost, byHost, byKingdom);
    }

    private static long GetInt64(IReadOnlyDictionary<string, JsonElement> item, string key) =>
        item.TryGetValue(key, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt64()
            : 0;

    private static string GetString(IReadOnlyDictionary<string, JsonElement> item, string key) =>
        item.TryGetValue(key, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    /// <summary>
    /// Return the public GameServer route for a LoginServer route.
    /// When the table cannot be loaded, the current client's rule (kingdom id to
    /// edge port, one global edge host) is used instead.
    /// </summary>
    public static Route RouteFor(string netIp, int netPort, long? kingdomId, EdgeTable? table)
    {
        if (table is null)
        {
            try
            {
                table = LoadTable(DefaultTablePath());
            }
            catch (IOException)
            {
                table = new EdgeTable("built-in formula", DefaultEdgeHost,
                    new Dictionary<string, EdgeRow>(StringComparer.Ordinal),
                    new Dictionary<long, EdgeRow>());
            }
        }

        EdgeRow? row = table.ByHost.GetValueOrDefault(netIp);
        var source = "table:host";
        if (row is null && kingdomId is not null)
        {
            row = table.ByKingdom.GetValueOrDefault(kingdomId.Value);
            source = "table:kingdom";
        }

        if (row is null)
        {
            if (kingdomId is null)
            {
                throw new KeyNotFoundException(
                    $"internal host {netIp} is not in {table.Source} and no kingdom id was given");
            }

            // Current client config: Id == kingdom id and edge game port is
            // 10000 + 10 * id for every row; the edge host is global.
            row = new EdgeRow
            {
                KingdomId = kingdomId.Value,
                InternalHost = netIp,
                EdgeHost = table.DefaultEdgeHost,
                InternalGamePort = 11000,
                EdgeGamePort = 10000 + (int)(kingdomId.Value * 10),
                InternalChatPort = 21000,
                EdgeChatPort = 10000 + (int)(kingdomId.Value * 10) + 8
            };
            source = "formula:kingdom";
        }

        var port = row.EdgeGamePort + (netPort - row.InternalGamePort);
        if (port is <= 0 or > 65535)
            throw new ArgumentException($"computed public port {port} is invalid");
        var chatPort = row.EdgeChatPort + (row.InternalChatPort - 21000);
        return new Route
        {
            Host = row.EdgeHost,
            Port = port,
            ChatPort = chatPort,
            InternalHost = row.InternalHost,
            InternalPort = netPort,
            InternalGamePort = row.InternalGamePort,
            EdgeGamePort = row.EdgeGamePort,
            KingdomId = row.KingdomId,
            Source = source
        };
    }

    public static string DefaultTablePath() =>
        Path.Combine(AppContext.BaseDirectory, "Backend", "edge_servers.json");

    // --------------------------------------------------------------------------
    // resolution
    // --------------------------------------------------------------------------

    /// <summary>Return the addresses the operating system resolver gives for host.</summary>
    public static List<string> ResolveHost(string host)
    {
        var result = new List<string>();
        IPAddress[] addresses;
        try
        {
            addresses = Dns.GetHostAddresses(host);
        }
        catch (SocketException)
        {
            return result;
        }

        foreach (var address in addresses
                     .OrderBy(item => item.AddressFamily == AddressFamily.InterNetwork ? 0 : 1))
        {
            var ip = address.ToString();
            if (!result.Contains(ip))
                result.Add(ip);
        }

        return result;
    }

    private static List<(string Ip, string Source)> Dedup(IEnumerable<(string Ip, string Source)> candidates)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<(string Ip, string Source)>();
        foreach (var candidate in candidates)
        {
            if (!seen.Add(candidate.Ip))
                continue;
            result.Add(candidate);
        }

        return result;
    }

    /// <summary>
    /// Return the IPv4 encoded in a hostname like 204-141-172-33.dls...
    /// IGG names its edge hosts after one of their addresses, which can be missing
    /// from the DNS answer set, so it is a useful extra candidate.
    /// </summary>
    public static string? EncodedEdgeIp(string host)
    {
        var labels = host.Split('.', 2)[0].Split('-');
        if (labels.Length != 4 || labels.Any(label => !label.All(char.IsDigit)))
            return null;
        var numbers = new int[4];
        for (var index = 0; index < 4; index++)
        {
            if (!int.TryParse(labels[index], NumberStyles.Integer, CultureInfo.InvariantCulture, out numbers[index]))
                return null;
            if (numbers[index] is < 0 or > 255)
                return null;
        }

        return string.Join(".", numbers.Select(number => number.ToString(CultureInfo.InvariantCulture)));
    }

    // ---- DNS-over-HTTPS and public UDP resolvers --------------------------

    private static async Task<List<string>> DohLookupAsync(string template, string host, double timeout)
    {
        var url = template.Replace("{name}", Uri.EscapeDataString(host));
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(timeout + 5) };
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("accept", "application/dns-json");
        request.Headers.TryAddWithoutValidation("user-agent", "dls-route/1");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(timeout));
        using var response = await client.SendAsync(request, cancellation.Token);
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadAsStringAsync(cancellation.Token);
        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;
        if (!root.TryGetProperty("Status", out var status) || status.ValueKind != JsonValueKind.Number ||
            status.GetInt32() != 0)
        {
            return new List<string>();
        }

        var addresses = new List<string>();
        if (!root.TryGetProperty("Answer", out var answers) || answers.ValueKind != JsonValueKind.Array)
            return addresses;
        foreach (var answer in answers.EnumerateArray())
        {
            if (!answer.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.Number ||
                type.GetInt32() != 1)
            {
                continue;
            }

            if (answer.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.String)
            {
                var value = data.GetString();
                if (!string.IsNullOrEmpty(value))
                    addresses.Add(value);
            }
        }

        return addresses;
    }

    private static byte[] BuildDnsQuery(string host, ushort queryId)
    {
        var labels = host.TrimEnd('.').Split('.', StringSplitOptions.RemoveEmptyEntries);
        var name = new List<byte>();
        foreach (var label in labels)
        {
            var bytes = Encoding.ASCII.GetBytes(label);
            name.Add((byte)bytes.Length);
            name.AddRange(bytes);
        }

        name.Add(0);
        var packet = new byte[12 + name.Count + 4];
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(0, 2), queryId);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2, 2), 0x0100);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(4, 2), 1);
        name.CopyTo(packet, 12);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(packet.Length - 4, 2), 1);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(packet.Length - 2, 2), 1);
        return packet;
    }

    private static (string Name, int Offset) ReadDnsName(byte[] data, int offset)
    {
        var labels = new List<string>();
        var jumps = 0;
        int? end = null;
        while (true)
        {
            if (offset >= data.Length)
                throw new ArgumentException("truncated DNS name");
            var length = data[offset];
            if (length == 0)
                return (string.Join(".", labels), end ?? offset + 1);
            if ((length & 0xC0) == 0xC0)
            {
                if (offset + 1 >= data.Length)
                    throw new ArgumentException("truncated DNS pointer");
                var pointer = ((length & 0x3F) << 8) | data[offset + 1];
                end ??= offset + 2;
                jumps++;
                if (jumps > 16)
                    throw new ArgumentException("DNS compression loop");
                offset = pointer;
                continue;
            }

            if (offset + 1 + length > data.Length)
                throw new ArgumentException("truncated DNS label");
            labels.Add(Encoding.ASCII.GetString(data, offset + 1, length));
            offset += 1 + length;
        }
    }

    private static List<string> ParseDnsAnswers(byte[] data, ushort? queryId)
    {
        if (data.Length < 12)
            return new List<string>();
        var qid = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(0, 2));
        if (queryId is not null && qid != queryId.Value)
            return new List<string>();
        var flags = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(2, 2));
        if ((flags & 0x8000) == 0 || (flags & 0x000F) != 0)
            return new List<string>();
        var questions = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(4, 2));
        var answers = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(6, 2));

        var offset = 12;
        for (var index = 0; index < questions; index++)
        {
            try
            {
                (_, offset) = ReadDnsName(data, offset);
            }
            catch (ArgumentException)
            {
                return new List<string>();
            }

            offset += 4;
            if (offset > data.Length)
                return new List<string>();
        }

        var result = new List<string>();
        for (var index = 0; index < answers; index++)
        {
            try
            {
                (_, offset) = ReadDnsName(data, offset);
            }
            catch (ArgumentException)
            {
                break;
            }

            if (offset + 10 > data.Length)
                break;
            var rtype = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset, 2));
            var rdlength = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset + 6, 4));
            offset += 10;
            if (offset + rdlength > data.Length)
                break;
            if (rtype == 1 && rdlength == 4)
                result.Add(string.Join(".", data.Skip(offset).Take(4).Select(b => b.ToString(CultureInfo.InvariantCulture))));
            offset += rdlength;
        }

        return result;
    }

    private static async Task<List<string>> UdpLookupAsync(string server, string host, double timeout)
    {
        var queryId = (ushort)Random.Shared.Next(0, 65536);
        var packet = BuildDnsQuery(host, queryId);
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(timeout));
        try
        {
            await socket.SendToAsync(packet, new IPEndPoint(IPAddress.Parse(server), 53), cancellation.Token);
            var buffer = new byte[4096];
            var received = await socket.ReceiveAsync(buffer, SocketFlags.None, cancellation.Token);
            return ParseDnsAnswers(buffer.AsSpan(0, received).ToArray(), queryId);
        }
        catch (OperationCanceledException)
        {
            return new List<string>();
        }
        catch (SocketException)
        {
            return new List<string>();
        }
    }

    /// <summary>Ask public DoH/UDP resolvers in parallel and return all A records.</summary>
    public static async Task<List<(string Ip, string Source)>> PublicCandidatesAsync(string host, double timeout = 4.0)
    {
        var jobs = new List<(string Label, Func<Task<List<string>>> Work)>();
        foreach (var (label, template) in DohEndpoints)
            jobs.Add((label, () => DohLookupAsync(template, host, timeout)));
        foreach (var server in PublicDns)
            jobs.Add(($"udp:{server}", () => UdpLookupAsync(server, host, timeout)));

        var tasks = jobs.Select(async job =>
        {
            try
            {
                var addresses = await job.Work();
                return (job.Label, addresses);
            }
            catch (Exception)
            {
                return (job.Label, (List<string>)new List<string>());
            }
        }).ToList();

        var completed = await Task.WhenAny(Task.WhenAll(tasks),
            Task.Delay(TimeSpan.FromSeconds(timeout + 0.5)));
        List<(string Ip, string Source)> results = new();
        foreach (var task in tasks)
        {
            if (!task.IsCompleted)
                continue;
            var (label, addresses) = task.Result;
            foreach (var address in addresses)
                results.Add((address, label));
        }

        _ = completed;
        return Dedup(results);
    }

    // ---- reachability ---------------------------------------------------------

    private static async Task<bool> ProbeAsync(string ip, int port, double timeout)
    {
        try
        {
            using var client = new TcpClient();
            var connect = client.ConnectAsync(IPAddress.Parse(ip), port);
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(timeout));
            await connect.WaitAsync(cancellation.Token);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Return the first candidate that accepts a TCP connection, or null.</summary>
    public static async Task<(string Ip, string Source)?> ProbeCandidatesAsync(
        IEnumerable<(string Ip, string Source)> candidates, int port, double timeout = 5.0)
    {
        var unique = Dedup(candidates);
        if (unique.Count == 0)
            return null;
        var tasks = unique
            .Select(async candidate =>
            {
                var reachable = await ProbeAsync(candidate.Ip, port, timeout);
                return reachable ? (ValueTuple<string, string>?)candidate : null;
            })
            .ToList();
        var pending = new HashSet<Task<(string, string)?>>(tasks);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(timeout + 1.0);
        while (pending.Count > 0)
        {
            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero)
                break;
            var finished = await Task.WhenAny(pending).WaitAsync(remaining);
            pending.Remove(finished);
            var result = await finished;
            if (result is not null)
                return result.Value;
        }

        return null;
    }

    /// <summary>
    /// Resolve and verify a GameServer route, independent of the network.
    /// The system resolver is tried first. If none of its answers accepts a TCP
    /// connection, public DoH/UDP resolvers are asked and every answer is probed.
    /// </summary>
    public static async Task<RouteChoice> ResolveRouteAsync(string host, int port, double timeout = 5.0,
        bool useCache = true)
    {
        if (IPAddress.TryParse(host, out var literalAddress))
        {
            if (await ProbeAsync(literalAddress.ToString(), port, timeout))
            {
                return new RouteChoice(host, port, literalAddress.ToString(), "literal",
                    new List<(string, string)> { (literalAddress.ToString(), "literal") });
            }

            throw new RouteError($"{host}:{port} did not accept a TCP connection");
        }

        var key = (host, port);
        if (useCache)
        {
            CacheEntry? entry;
            lock (RouteCacheLock)
            {
                RouteCache.TryGetValue(key, out entry);
            }

            if (entry is not null && entry.Expires > Monotonic.Now)
            {
                // Re-validate briefly: switching VPN/network must not keep using an
                // address that is no longer reachable.
                if (await ProbeAsync(entry.Route.Ip, port, Math.Min(timeout, 3.0)))
                    return entry.Route;
                lock (RouteCacheLock)
                {
                    RouteCache.Remove(key);
                }
            }
        }

        var system = ResolveHost(host).Select(ip => (ip, "system")).ToList();
        List<(string Ip, string Source)> candidates = new(system);
        var chosen = await ProbeCandidatesAsync(system, port, timeout);
        if (chosen is null && host.Contains('.'))
        {
            var tried = system.Select(item => item.Item1).ToHashSet(StringComparer.Ordinal);
            var extra = (await PublicCandidatesAsync(host))
                .Where(item => !tried.Contains(item.Ip))
                .ToList();
            candidates = Dedup(candidates.Concat(extra));
            chosen = await ProbeCandidatesAsync(extra, port, timeout);
        }

        if (chosen is null)
        {
            var tried = candidates.Select(item => item.Item1).ToHashSet(StringComparer.Ordinal);
            List<(string Ip, string Source)> fallback = new();
            var encoded = EncodedEdgeIp(host);
            if (encoded is not null && !tried.Contains(encoded))
                fallback.Add((encoded, "hostname"));
            if (HistoricEdgeIps.TryGetValue(host, out var historic))
            {
                foreach (var ip in historic)
                {
                    if (!tried.Contains(ip))
                        fallback.Add((ip, "historic"));
                }
            }

            fallback = Dedup(fallback);
            if (fallback.Count > 0)
            {
                candidates = Dedup(candidates.Concat(fallback));
                chosen = await ProbeCandidatesAsync(fallback, port, timeout);
            }
        }

        if (chosen is null)
        {
            var detail = candidates.Count > 0
                ? string.Join(", ", candidates.Select(item => $"{item.Ip} ({item.Source})"))
                : "no DNS answers";
            throw new RouteError(
                $"no address for {host}:{port} accepted a TCP connection; tried {detail}");
        }

        var result = new RouteChoice(host, port, chosen.Value.Ip, chosen.Value.Source, candidates);
        if (useCache)
        {
            lock (RouteCacheLock)
            {
                RouteCache[key] = new CacheEntry { Route = result, Expires = Monotonic.Now + RouteCacheTtl };
            }
        }

        return result;
    }

    /// <summary>Drop cached routes (used by tests and by an explicit reconnect).</summary>
    public static void ClearRouteCache()
    {
        lock (RouteCacheLock)
        {
            RouteCache.Clear();
        }
    }

    /// <summary>Backwards-compatible helper: return the reachable IP for a route.</summary>
    public static async Task<string> TcpProbeAsync(string host, int port, double timeout = 5.0) =>
        (await ResolveRouteAsync(host, port, timeout, false)).Ip;

    /// <summary>True when the address is a public (globally routable) IP.</summary>
    public static bool IsGlobalIp(string value)
    {
        if (!IPAddress.TryParse(value, out var address))
            return true;
        if (IPAddress.IsLoopback(address))
            return false;
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var octets = address.GetAddressBytes();
            if (octets[0] == 0 || octets[0] == 10 || octets[0] == 127)
                return false;
            if (octets[0] == 169 && octets[1] == 254)
                return false;
            if (octets[0] == 172 && octets[1] >= 16 && octets[1] <= 31)
                return false;
            if (octets[0] == 192 && octets[1] == 168)
                return false;
            if (octets[0] == 100 && octets[1] >= 64 && octets[1] <= 127)
                return false;
            if (octets[0] == 192 && octets[1] == 0 && octets[2] == 0)
                return false;
            if (octets[0] == 198 && (octets[1] == 18 || octets[1] == 19))
                return false;
            if (octets[0] >= 224)
                return false;
            return true;
        }

        if (address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast)
            return false;
        if (address.Equals(IPAddress.IPv6Any))
            return false;
        var bytes = address.GetAddressBytes();
        if ((bytes[0] & 0xFE) == 0xFC)
            return false;
        return true;
    }
}
