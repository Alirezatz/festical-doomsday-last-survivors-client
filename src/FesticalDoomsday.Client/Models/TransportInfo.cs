namespace FesticalDoomsday.Client.Models;

/// <summary>One castle of the loaded region, offered as a receiver.</summary>
public sealed class TransportTarget
{
    public ulong PlayerId { get; init; }

    /// <summary>The castle entity a transport truck is addressed to.</summary>
    public ulong EntityId { get; init; }

    public string Name { get; init; } = string.Empty;

    public string Guild { get; init; } = string.Empty;

    public string Display => Guild.Length == 0 ? Name : $"[{Guild}] {Name}";

    public string Line => $"{Display} · {PlayerId}";
}

/// <summary>
/// What the server reported about this account's Trading Post: its level, the
/// limits of that level and the four resources of the city.
/// </summary>
public sealed class TransportSnapshot
{
    public bool Known { get; init; }
    public bool Connected { get; init; }

    /// <summary>Trading Post level; 0 means the account has not built it.</summary>
    public uint Level { get; init; }

    public string PlayerName { get; init; } = string.Empty;
    public ulong Food { get; init; }
    public ulong Wood { get; init; }
    public ulong Steel { get; init; }
    public ulong Oil { get; init; }

    /// <summary>Transportation Capacity: the most one transport may carry.</summary>
    public ulong PerSend { get; init; }

    /// <summary>Daily Supply Limit: the most this account sends in one day.</summary>
    public ulong DailySupply { get; init; }

    /// <summary>Daily Receiving Limit: the most the receiver may receive in one day.</summary>
    public ulong DailyReceive { get; init; }

    public int TaxPermille { get; init; }
    public int SpeedPermille { get; init; }

    public IReadOnlyList<TransportTarget> Targets { get; init; } = Array.Empty<TransportTarget>();
}

/// <summary>
/// One side of the capacity reply: what it already moved today. A record so
/// the form can copy it with one counter moved by the transport it just sent.
/// </summary>
public sealed record TransportLimit
{
    public ulong PlayerId { get; init; }
    public uint Level { get; init; }
    public ulong SentToday { get; init; }
    public ulong ReceivedToday { get; init; }
    public ulong PerSend { get; init; }
    public ulong DailySupply { get; init; }
    public ulong DailyReceive { get; init; }
    public int TaxPermille { get; init; }
}

/// <summary>The capacity reply of one receiver, as the two sides stand today.</summary>
public sealed record TransportCapacity
{
    public int ErrorCode { get; init; }

    /// <summary>The sender's own entry, when the server listed it.</summary>
    public TransportLimit? Self { get; init; }

    public TransportLimit? Target { get; init; }
}

/// <summary>The outcome of one transport.</summary>
public sealed class TransportReport
{
    public bool Success { get; init; }
    public int ErrorCode { get; init; }
    public string Reason { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public ulong Amount { get; init; }
    public uint SubType { get; init; }
    public ulong TargetId { get; init; }
    public string TargetName { get; init; } = string.Empty;
    public int TaxPermille { get; init; }
}
