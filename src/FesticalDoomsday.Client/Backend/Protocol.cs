using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace FesticalDoomsday.Client.Backend;

/// <summary>Mirrors the Python <c>ProtocolError</c> exception.</summary>
public sealed class ProtocolError : Exception
{
    public ProtocolError(string message) : base(message)
    {
    }
}

public enum PbKind
{
    Varint,
    Bytes,
    Fixed64,
    Fixed32
}

/// <summary>One decoded protobuf field.</summary>
public sealed class PbField
{
    public PbField(int number, PbKind kind, ulong varint, byte[] data)
    {
        Number = number;
        Kind = kind;
        Varint = varint;
        Data = data;
    }

    public int Number { get; }
    public PbKind Kind { get; }
    public ulong Varint { get; }
    public byte[] Data { get; }

    public bool IsVarint => Kind == PbKind.Varint;
    public bool IsBytes => Kind == PbKind.Bytes;

    /// <summary>Decodes the field as text, replacing invalid sequences like Python's errors="replace".</summary>
    public string Text() => Data.Length == 0 ? string.Empty : Encoding.UTF8.GetString(Data);
}

/// <summary>An Alliance (guild) record as returned by the search / recommend list replies.</summary>
public sealed class GuildInfo
{
    public ulong Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public string ShortName { get; init; } = string.Empty;
}

/// <summary>Decoded <c>MsgLS2CLLoginReply</c> (opcode 0x279E).</summary>
public sealed class LsLoginReply
{
    public ulong? PlayerId { get; init; }
    public string? NetIp { get; init; }
    public int? NetPort { get; init; }
    public string? LoginSession { get; init; }
    public string? ServerVersion { get; init; }
    public ulong? KingdomId { get; init; }
    public string? Country { get; init; }
    public ulong? ErrorCode { get; init; }
}

/// <summary>Decoded <c>MsgCL2LSLoginRequest</c> (opcode 0x279D) field layout.</summary>
public sealed class LsLoginFields
{
    public ulong? Account { get; init; }
    public byte[]? WebSession { get; init; }
    public byte[]? Mac { get; init; }
    public ulong? Language { get; init; }
    public ulong? AreaId { get; init; }
    public byte[]? GameId { get; init; }
    public ulong? CampId { get; init; }
    public byte[]? Udid { get; init; }
    public ulong? BeManagedId { get; init; }
    public ulong? AiRobot { get; init; }
}

/// <summary>One active alliance assistance request from <c>MsgGS2CLGuildAssistListReply</c>.</summary>
public sealed class GuildAssistDetailRecord
{
    public ulong PlayerId { get; init; }
    public ulong AssistType { get; init; }
    public ulong ItemId { get; init; }
    public ulong ItemLevel { get; init; }
    public ulong ItemParam { get; init; }
    public ulong MaxTimes { get; init; }
    public ulong HelpedTimes { get; init; }
    public bool IsHelped { get; init; }
    public int CompletedHelpers { get; init; }
    public int WaitingHelpers { get; init; }

    /// <summary>
    /// The viewer has not helped this request yet and the request still needs
    /// help. The game keeps answered requests in the same list until they
    /// expire, so this is what decides whether a help request is worth sending.
    /// </summary>
    public bool NeedsHelp => PlayerId != 0 && !IsHelped && (MaxTimes == 0 || HelpedTimes < MaxTimes);

    public string Describe() =>
        $"player {PlayerId} type {AssistType} item {ItemId} level {ItemLevel} " +
        $"helped {HelpedTimes}/{MaxTimes} helpers {CompletedHelpers} waiting {WaitingHelpers} " +
        $"{(IsHelped ? "(already helped by us)" : NeedsHelp ? "(open)" : "(full)")}";
}

/// <summary>Result of <c>parse_guild_assist_list_reply</c>.</summary>
public sealed class GuildAssistListReply
{
    public int PendingCount { get; init; }
    public int DetailsCount { get; init; }
    public ulong ErrorCode { get; init; }
    public IReadOnlyList<GuildAssistDetailRecord> Details { get; init; } =
        Array.Empty<GuildAssistDetailRecord>();
}

/// <summary>Result of <c>parse_guild_assist_help_other_reply</c>.</summary>
public sealed class GuildAssistHelpReply
{
    public ulong AssistCount { get; init; }
    public ulong Contribution { get; init; }
    public ulong ErrorCode { get; init; }
}

/// <summary>
/// Whether the player currently belongs to an Alliance, and which one.
/// A guildless player is a real state: the assist list then fails with
/// <see cref="Protocol.GuildNotJoinedError"/> until the player joins a guild.
/// </summary>
public sealed class GuildState
{
    public static GuildState Unknown { get; } = new();

    public bool Known { get; init; }
    public ulong? GuildId { get; init; }
    public string? Name { get; init; }
    public string? ShortName { get; init; }
    public ulong Position { get; init; }
    public int ErrorCode { get; init; }

    public bool InGuild => GuildId is > 0;

    public string DisplayName
    {
        get
        {
            if (!InGuild)
                return string.Empty;
            if (string.IsNullOrEmpty(ShortName))
                return Name ?? $"#{GuildId}";
            return string.IsNullOrEmpty(Name) ? $"[{ShortName}]" : $"[{ShortName}]{Name}";
        }
    }
}

/// <summary>One Battle Royale (Bounty) formation entry.</summary>
public sealed class EatChickenArmy
{
    public ulong HeroId { get; init; }
    public ulong HeroLevel { get; init; }
    public bool HeroAwakened { get; init; }
    public ulong TroopId { get; init; }
    public ulong TroopCount { get; init; }
}

/// <summary>Decoded <c>MsgGS2CLEatChickenPairingInfoReply</c> (0x30D6).</summary>
public sealed class EatChickenPairingInfo
{
    public ulong ErrorCode { get; init; }
    public ulong Status { get; init; }
    public EatChickenArmy? Army { get; init; }
}

/// <summary>Decoded <c>MsgGS2CLEatChickenPairingRegisterReply</c> (0x30D8).</summary>
public sealed class EatChickenRegisterReply
{
    public ulong ErrorCode { get; init; }
    public EatChickenArmy? Army { get; init; }
}

/// <summary>One owned hero, learned from <c>MsgGS2CLAllHeroNotice</c> (0x277E).</summary>
public sealed class HeroRecord
{
    public ulong Id { get; init; }
    public ulong Level { get; init; }
    public bool Awakened { get; init; }
}

/// <summary>One owned troop stack, learned from <c>MsgGS2CLAllArmyNotice</c> (0x2711).</summary>
public sealed class TroopRecord
{
    public ulong Id { get; init; }
    public ulong Count { get; init; }
}

/// <summary>One stackable item, learned from <c>MsgGS2CLAllItemNotice</c> (0x2780).</summary>
public sealed class ItemRecord
{
    public uint Id { get; init; }
    public ulong Count { get; init; }
}

/// <summary>One resource paid out by the server, for example 1,000 Food.</summary>
public sealed class ResourceGrant
{
    public uint ResType { get; init; }
    public uint SubType { get; init; }
    public ulong Value { get; init; }
}

/// <summary>Decoded <c>MsgGS2CLItemUseReply</c> (0x2782).</summary>
public sealed class ItemUseResult
{
    public uint Id { get; init; }
    public uint Count { get; init; }
    public int ErrorCode { get; init; }
    public IReadOnlyList<ResourceGrant> Rewards { get; init; } = Array.Empty<ResourceGrant>();

    public bool Success => ErrorCode == 0;
}

/// <summary>One side (the sender or the target) of a Trading Post capacity reply.</summary>
public sealed class CarriageCapacityEntry
{
    public ulong PlayerId { get; init; }

    /// <summary>Resources this side already sent today (field 2 of the message).</summary>
    public ulong SentToday { get; init; }

    /// <summary>Resources this side already received today (field 3 of the message).</summary>
    public ulong ReceivedToday { get; init; }

    /// <summary>Trading Post level; 0 means the player has no Trading Post.</summary>
    public uint TradingPostLevel { get; init; }
}

/// <summary>Decoded <c>MsgGS2CLGetCarriageCapacityReply</c> (0x2A6F).</summary>
public sealed class CarriageCapacityReply
{
    public ulong RegionId { get; init; }
    public int ErrorCode { get; init; }
    public IReadOnlyList<CarriageCapacityEntry> Entries { get; init; } =
        Array.Empty<CarriageCapacityEntry>();
}

/// <summary>
/// One castle that <c>MsgGS2CLSyncEntitiesDataNotice</c> (0x298D) announced. Its
/// <see cref="EntityId"/> is the id a transport truck is addressed to.
/// </summary>
public sealed class WorldCastle
{
    public ulong EntityId { get; init; }
    public ulong PlayerId { get; init; }
    public string PlayerName { get; init; } = string.Empty;
    public string GuildShortName { get; init; } = string.Empty;
}

/// <summary>Decoded <c>MsgGS2CLCreateCarriageReply</c> (0x2A6D).</summary>
public sealed class CarriageCreateResult
{
    public int ErrorCode { get; init; }
    public uint MarchIndex { get; init; }
    public ulong EntityId { get; init; }

    /// <summary>The tax the server charged for this transport.</summary>
    public uint Tax { get; init; }

    /// <summary>The resource amount the server accepted for the carriage.</summary>
    public ulong Value { get; init; }

    public bool Success => ErrorCode == 0;
}

/// <summary>
/// Decoded <c>MsgGS2CLPlayerBaseNotice</c> (0x2713): the account's own name, its
/// Might (the message calls it Power) and the four resources of its bag. The
/// server pushes it as part of the login burst. A record so the backend can
/// copy one with a single resource changed (<c>info with { Food = ... }</c>).
/// </summary>
public sealed record PlayerBaseInfo
{
    public ulong PlayerId { get; init; }
    public string Name { get; init; } = string.Empty;
    public ulong Level { get; init; }
    public ulong Might { get; init; }
    public ulong Food { get; init; }
    public ulong Wood { get; init; }
    public ulong Steel { get; init; }
    public ulong Oil { get; init; }
}

/// <summary>Decoded <c>MsgGS2CLPlayerActivityChickenTurntableNotice</c> (0x30AA).</summary>
public sealed class BountyTurntableInfo
{
    public uint ActivityId { get; init; }
    public uint RefreshCount { get; init; }
}

/// <summary>One reward entry of the Bounty turntable reply.</summary>
public sealed class BountyTurntableReward
{
    public uint ResType { get; init; }
    public uint SubType { get; init; }
    public ulong Value { get; init; }

    public string Describe() => $"{ResType}:{SubType}x{Value}";
}

/// <summary>Decoded <c>MsgCL2GSActivityChickenTurntableReply</c> (0x30AC).</summary>
public sealed class BountyTurntableReply
{
    public uint ActivityId { get; init; }
    public uint Count { get; init; }
    public int ErrorCode { get; init; }
    public IReadOnlyList<BountyTurntableReward> Rewards { get; init; } =
        Array.Empty<BountyTurntableReward>();
    public IReadOnlyList<uint> RewardIds { get; init; } = Array.Empty<uint>();
}

/// <summary>
/// Protocol.cs - the wire format of the game's raw TCP channel, as reconstructed
/// from the captures.
///
///     [ length : u16 LE ][ opcode : u16 LE ][ protobuf body ]
///       length = size of the WHOLE frame (the 4-byte header is included)
///
/// Ported from proto.py; a tiny protobuf encoder/decoder is included, so no
/// third-party packages are required.
/// </summary>
internal static class Protocol
{
    public const int Header = 4;

    // ---- opcodes seen in the captures -------------------------------------
    public const ushort Ping = 0x2716;
    public const ushort Pong = 0x2717;
    public const ushort Move = 0x2790;
    public const ushort MoveAck = 0x2791;
    public const ushort Login = 0x279F;
    public const ushort LoginAck = 0x27A0;
    public const ushort GuildAssistListRequest = 0x28DD;
    public const ushort GuildAssistListReply = 0x28DE;
    public const ushort GuildAssistUpdateNotice = 0x28DF;
    public const ushort GuildAssistHelpOtherRequest = 0x28E2;
    public const ushort GuildAssistHelpOtherReply = 0x28E3;
    public const ushort PlayerBaseNotice = 0x2713;
    public const ushort GuildJoinNotice = 0x2744;
    public const ushort GuildQuitNotice = 0x2745;
    public const ushort GuildJoinRequest = 0x2746;
    public const ushort GuildJoinReply = 0x2747;
    public const ushort GuildQuitRequest = 0x2748;
    public const ushort GuildQuitReply = 0x2749;
    public const ushort GuildBaseInfoRequest = 0x2740;
    public const ushort GuildBaseInfoReply = 0x2741;
    public const ushort AllArmyNotice = 0x2711;
    public const ushort AllHeroNotice = 0x277E;
    public const ushort AllItemNotice = 0x2780;
    public const ushort ItemUseRequest = 0x2781;
    public const ushort ItemUseReply = 0x2782;
    public const ushort UpdateItemNotice = 0x2783;
    public const ushort GuildSearchRequest = 0x275F;
    public const ushort GuildSearchReply = 0x2760;
    public const ushort GuildRecommendListRequest = 0x2761;
    public const ushort GuildRecommendListReply = 0x2762;

    // ---- two-stage login opcodes (from the IL2CPP dump: MsgType enum) -----
    public const ushort LsLoginReq = 0x279D;
    public const ushort LsLoginReply = 0x279E;
    public const ushort GsLogin = Login;
    public const ushort GsLoginAck = LoginAck;

    // ---- Battle Royale / Bounty (solo EatChicken) -------------------------
    public const ushort EatChickenIntoInstanceRequest = 0x30B0;
    public const ushort EatChickenIntoInstanceReply = 0x30B1;
    public const ushort EatChickenGameWaitStartNotice = 0x30C5;
    public const ushort EatChickenGameDataNotice = 0x30C6;
    public const ushort EatChickenGameEndDataNotice = 0x30C7;
    public const ushort EatChickenPairingSucceedNotice = 0x30D4;
    public const ushort EatChickenPairingInfoRequest = 0x30D5;
    public const ushort EatChickenPairingInfoReply = 0x30D6;
    public const ushort EatChickenPairingRegisterRequest = 0x30D7;
    public const ushort EatChickenPairingRegisterReply = 0x30D8;
    public const ushort EatChickenDataNotice = 0x30D0;
    public const ushort EatChickenPlayerDeadNotice = 0x30E5;
    public const ushort EatChickenPairingFailedNotice = 0x30FB;
    public const ushort EatChickenLeaveRequest = 0x30F8;
    public const ushort EatChickenLeaveReply = 0x30F9;

    // ---- Bounty Ground turntable -----------------------------------------
    // MsgGS2CLPlayerActivityChickenTurntableNotice / MsgCL2GSActivityChickenTurntable*
    // The wheel spends Bounty Tickets: ChickenParam 17 is {100070, 100} (item,
    // cost per spin) and ChickenParam 18 is {1, 5} (1..5 spins per request).
    public const ushort ChickenTurntableNotice = 0x30AA;
    public const ushort ChickenTurntableRequest = 0x30AB;
    public const ushort ChickenTurntableReply = 0x30AC;

    // ---- Trading Post (resource transport) ----------------------------------
    public const ushort CarriageCreateRequest = 0x2A6C;
    public const ushort CarriageCreateReply = 0x2A6D;
    public const ushort CarriageCapacityRequest = 0x2A6E;
    public const ushort CarriageCapacityReply = 0x2A6F;
    public const ushort CarriageRemoveNotice = 0x2A70;

    /// <summary>The castle roster of the current view, pushed on every load.</summary>
    public const ushort SyncEntitiesNotice = 0x298D;

    /// <summary>The account's own city, which carries its Trading Post level.</summary>
    public const ushort CityInfoRequest = 0x272B;
    public const ushort CityInfoReply = 0x272C;

    /// <summary>MarchCommandTarget.CarriageSend: a transport truck, not a squad.</summary>
    public const ulong CarriageSendTargetType = 11;

    /// <summary>Resource.ResType of the four city resources (food, wood, steel, oil).</summary>
    public const uint BasicResourceType = 1;

    /// <summary>Resource.SubType values, straight from the game's BaseResType enum.</summary>
    public const uint FoodSubType = 3;
    public const uint WoodSubType = 4;
    public const uint SteelSubType = 5;
    public const uint OilSubType = 2;

    // ---- error codes that the client reacts to ---------------------------
    /// <summary>
    /// Bounty Ticket (game icon <c>item_chicken_coin_turntable</c>), the reward
    /// currency of Bounty Ground, the Battle Royale (Bounty) mode. A match pays
    /// them by placement, so even the weakest finish pays exactly one.
    /// Item 100072 is Hunt Coin, the Bounty Ground *shop* currency, and is not
    /// what a match pays out.
    /// </summary>
    public const uint BountyTicketItemId = 100070;

    /// <summary>Bounty Tickets one turntable spin costs (ChickenParam 17).</summary>
    public const ulong BountySpinCost = 100;

    /// <summary>The most spins one turntable request may ask for (ChickenParam 18).</summary>
    public const uint BountyMaxSpinsPerRequest = 5;

    /// <summary>
    /// The Battle Royale turntable activity: gameconfig_activity id 104053, whose
    /// type is 42 (KActivityTypeChickenTurntable) and which repeats every 7 days.
    /// The server's own notice carries the id as well; this is the fallback.
    /// </summary>
    public const uint BountyTurntableActivityId = 104053;

    public const int GuildNotJoinedError = 1302;
    public const int EatChickenUnknownError = 14000;
    public const int EatChickenFormError = 14010;

    // ---- post-login sequence ---------------------------------------------
    public const ushort EnterGame = 0x27A1;
    public const ushort EnterGameReply = 0x27A2;
    public const ushort NoviceRecord = 0x2C96;
    public const ushort NoviceRecordAck = 0x2C97;
    public const ushort CompressPackage = 0x04B3;
    public const ushort GuildBldgList = 0x2870;
    public const ushort GuildCapture = 0x2EEF;

    // Aliases kept so the older reports/scripts keep running.
    public const ushort HsAck = EnterGame;
    public const ushort HsConfig = NoviceRecord;
    public const ushort HsConfigAck = NoviceRecordAck;
    public const ushort HsArmy = GuildBldgList;
    public const ushort ArmyAnnounce = GuildCapture;

    // --- 0x04B3 container layout -------------------------------------------
    public const int PkgHeader = 8;
    public const int PkgRaw = 0;
    public const int PkgZlib = 1;

    public const string DefaultGameId = "11200107071";

    private static readonly Dictionary<int, string> Names = new()
    {
        [Ping] = "PING",
        [Pong] = "PONG",
        [Move] = "MOVE",
        [MoveAck] = "MOVE_ACK",
        [LsLoginReq] = "LS_LOGIN_REQ",
        [LsLoginReply] = "LS_LOGIN_REPLY"
    };

    // ----------------------------- protobuf basics --------------------------

    public static byte[] EncVarint(ulong n)
    {
        var output = new List<byte>(10);
        while (true)
        {
            var b = (byte)(n & 0x7F);
            n >>= 7;
            if (n != 0)
            {
                output.Add((byte)(b | 0x80));
            }
            else
            {
                output.Add(b);
                return output.ToArray();
            }
        }
    }

    public static (ulong Value, int Pos) DecVarint(byte[] buf, int pos)
    {
        var shift = 0;
        ulong result = 0;
        while (true)
        {
            if (pos >= buf.Length)
                throw new ProtocolError("truncated varint");
            var b = buf[pos];
            pos++;
            result |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
                return (result, pos);
            shift += 7;
            if (shift > 70)
                throw new ProtocolError("varint too long");
        }
    }

    public static byte[] FVarint(int num, ulong val) =>
        Concat(EncVarint((ulong)(num << 3)), EncVarint(val));

    public static byte[] FBytes(int num, byte[] data) =>
        Concat(EncVarint((ulong)((num << 3) | 2)), EncVarint((ulong)data.Length), data);

    public static byte[] FBytes(int num, string data) => FBytes(num, Encoding.UTF8.GetBytes(data));

    public static byte[] FMsg(int num, params byte[][] parts)
    {
        var total = parts.Sum(part => part.Length);
        var joined = new byte[total];
        var offset = 0;
        foreach (var part in parts)
        {
            Array.Copy(part, 0, joined, offset, part.Length);
            offset += part.Length;
        }

        return FBytes(num, joined);
    }

    /// <summary>Decodes a protobuf message into its fields.</summary>
    public static List<PbField> Parse(byte[] buf)
    {
        var output = new List<PbField>();
        var pos = 0;
        while (pos < buf.Length)
        {
            var tag = DecVarint(buf, pos);
            pos = tag.Pos;
            var num = (int)(tag.Value >> 3);
            var wt = (int)(tag.Value & 7);
            if (num == 0)
                throw new ProtocolError("field number 0");
            switch (wt)
            {
                case 0:
                {
                    var value = DecVarint(buf, pos);
                    pos = value.Pos;
                    output.Add(new PbField(num, PbKind.Varint, value.Value, Array.Empty<byte>()));
                    break;
                }
                case 2:
                {
                    var length = DecVarint(buf, pos);
                    pos = length.Pos;
                    var ln = checked((int)length.Value);
                    if (pos + ln > buf.Length)
                        throw new ProtocolError("length-delimited field overruns buffer");
                    var data = new byte[ln];
                    Array.Copy(buf, pos, data, 0, ln);
                    pos += ln;
                    output.Add(new PbField(num, PbKind.Bytes, 0, data));
                    break;
                }
                case 1:
                {
                    if (pos + 8 > buf.Length)
                        throw new ProtocolError("length-delimited field overruns buffer");
                    var data = new byte[8];
                    Array.Copy(buf, pos, data, 0, 8);
                    pos += 8;
                    output.Add(new PbField(num, PbKind.Fixed64, 0, data));
                    break;
                }
                case 5:
                {
                    if (pos + 4 > buf.Length)
                        throw new ProtocolError("length-delimited field overruns buffer");
                    var data = new byte[4];
                    Array.Copy(buf, pos, data, 0, 4);
                    pos += 4;
                    output.Add(new PbField(num, PbKind.Fixed32, 0, data));
                    break;
                }
                default:
                    throw new ProtocolError($"unsupported wire type {wt}");
            }
        }

        return output;
    }

    public static PbField? Get(List<PbField> fields, int number)
    {
        foreach (var field in fields)
        {
            if (field.Number == number)
                return field;
        }

        return null;
    }

    public static ulong GetVarint(List<PbField> fields, int number, ulong fallback = 0)
    {
        var field = Get(fields, number);
        return field is { IsVarint: true } ? field.Varint : fallback;
    }

    public static byte[]? GetBytes(List<PbField> fields, int number)
    {
        var field = Get(fields, number);
        return field is { IsBytes: true } ? field.Data : null;
    }

    /// <summary>Human-readable protobuf dump; nested messages are detected heuristically.</summary>
    public static string Dump(byte[] buf, int indent = 0)
    {
        var pad = new string(' ', indent * 2);
        var lines = new List<string>();
        foreach (var field in Parse(buf))
        {
            switch (field.Kind)
            {
                case PbKind.Varint:
                    lines.Add($"{pad}f{field.Number} = {field.Varint}");
                    break;
                case PbKind.Bytes:
                {
                    var value = field.Data;
                    if (value.Length >= 4 && value.All(c => c >= 32 && c < 127))
                    {
                        lines.Add($"{pad}f{field.Number} = \"{Encoding.ASCII.GetString(value)}\"");
                        continue;
                    }

                    string? inner = null;
                    if (value.Length > 0)
                    {
                        try
                        {
                            inner = Dump(value, indent + 1);
                        }
                        catch (ProtocolError)
                        {
                            inner = null;
                        }
                    }

                    if (!string.IsNullOrEmpty(inner))
                        lines.Add($"{pad}f{field.Number} = {{\n{inner}\n{pad}}}");
                    else
                        lines.Add($"{pad}f{field.Number} = bytes({Convert.ToHexString(value).ToLowerInvariant()})");
                    break;
                }
                default:
                    lines.Add($"{pad}f{field.Number} = raw({Convert.ToHexString(field.Data).ToLowerInvariant()})");
                    break;
            }
        }

        return string.Join("\n", lines);
    }

    // ------------------------------- framing -------------------------------

    public static byte[] Pack(ushort opcode, byte[]? body = null)
    {
        body ??= Array.Empty<byte>();
        var total = Header + body.Length;
        if (total > 0xFFFF)
            throw new ProtocolError("frame too large");
        var frame = new byte[total];
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(0, 2), (ushort)total);
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(2, 2), opcode);
        Array.Copy(body, 0, frame, Header, body.Length);
        return frame;
    }

    /// <summary>Reassembles frames from an arbitrary TCP byte stream.</summary>
    public sealed class FrameReader
    {
        private byte[] _buffer = Array.Empty<byte>();
        private int _start;
        private int _end;

        public List<(ushort Opcode, byte[] Body)> Feed(byte[] data)
        {
            Append(data);
            var frames = new List<(ushort, byte[])>();
            while (_end - _start >= Header)
            {
                var total = BinaryPrimitives.ReadUInt16LittleEndian(_buffer.AsSpan(_start, 2));
                var opcode = BinaryPrimitives.ReadUInt16LittleEndian(_buffer.AsSpan(_start + 2, 2));
                if (total < Header)
                    throw new ProtocolError($"bad frame length {total}");
                if (_end - _start < total)
                    break;
                var body = new byte[total - Header];
                Array.Copy(_buffer, _start + Header, body, 0, body.Length);
                frames.Add((opcode, body));
                _start += total;
            }

            Compact();
            return frames;
        }

        private void Append(byte[] data)
        {
            if (data.Length == 0)
                return;
            if (_buffer.Length - _end < data.Length)
            {
                var used = _end - _start;
                var capacity = Math.Max(_buffer.Length * 2, used + data.Length);
                var grown = new byte[capacity];
                Array.Copy(_buffer, _start, grown, 0, used);
                _buffer = grown;
                _start = 0;
                _end = used;
            }

            Array.Copy(data, 0, _buffer, _end, data.Length);
            _end += data.Length;
        }

        private void Compact()
        {
            if (_start == 0)
                return;
            var used = _end - _start;
            if (used == 0)
            {
                _start = 0;
                _end = 0;
                return;
            }

            Array.Copy(_buffer, _start, _buffer, 0, used);
            _start = 0;
            _end = used;
        }
    }

    /// <summary>Human name for an opcode: a short local alias, else the game's own name.</summary>
    public static string Name(ushort opcode)
    {
        if (Names.TryGetValue(opcode, out var local) && !string.IsNullOrEmpty(local))
            return local;
        if (Opcodes.Names.TryGetValue(opcode, out var gameName) && !string.IsNullOrEmpty(gameName))
            return gameName.StartsWith("KMsg", StringComparison.Ordinal)
                ? gameName[4..]
                : gameName;
        return $"0x{opcode:x4}";
    }

    // --------------------- the 0x04B3 container (compressPackage) ------------

    public static List<(ushort Opcode, byte[] Body)> UnpackPackage(byte[] body, out PackageMeta meta)
    {
        if (body.Length < PkgHeader)
            throw new ProtocolError("package shorter than its header");
        var payloadLen = BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(0, 2));
        var count = BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(2, 2));
        var flag = BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(4, 2));
        var rawLen = BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(6, 2));
        if (body.Length < PkgHeader + payloadLen)
            throw new ProtocolError("package payload is truncated");
        var payload = new byte[payloadLen];
        Array.Copy(body, PkgHeader, payload, 0, payloadLen);
        if (flag == PkgZlib)
        {
            try
            {
                payload = Inflate(payload);
            }
            catch (InvalidDataException exception)
            {
                throw new ProtocolError($"package zlib stream is broken: {exception.Message}");
            }

            if (rawLen != 0 && payload.Length != rawLen)
                throw new ProtocolError($"inflated to {payload.Length}, header said {rawLen}");
        }
        else if (flag != PkgRaw)
        {
            throw new ProtocolError($"unknown package compression flag {flag}");
        }

        var frames = new FrameReader().Feed(payload);
        if (frames.Count != count)
            throw new ProtocolError($"package held {frames.Count} frames, header said {count}");
        meta = new PackageMeta(payloadLen, count, flag, rawLen);
        return frames;
    }

    public static byte[] PackPackage(IReadOnlyList<byte[]> frames, bool compress = true)
    {
        var blob = Concat(frames.ToArray());
        var flag = PkgRaw;
        var payload = blob;
        var rawLen = 0;
        if (compress)
        {
            payload = Deflate(blob);
            flag = PkgZlib;
            rawLen = blob.Length;
        }

        if (payload.Length > 0xFFFF)
            throw new ProtocolError("package payload too large for a u16 length");
        var head = new byte[PkgHeader];
        BinaryPrimitives.WriteUInt16LittleEndian(head.AsSpan(0, 2), (ushort)payload.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(head.AsSpan(2, 2), (ushort)frames.Count);
        BinaryPrimitives.WriteUInt16LittleEndian(head.AsSpan(4, 2), (ushort)flag);
        BinaryPrimitives.WriteUInt16LittleEndian(head.AsSpan(6, 2), (ushort)rawLen);
        return Pack(CompressPackage, Concat(head, payload));
    }

    public sealed class PackageMeta
    {
        public PackageMeta(int payloadLen, int messageCount, int compressFlag, int uncompressedLen)
        {
            PayloadLen = payloadLen;
            MessageCount = messageCount;
            CompressFlag = compressFlag;
            UncompressedLen = uncompressedLen;
        }

        public int PayloadLen { get; }
        public int MessageCount { get; }
        public int CompressFlag { get; }
        public int UncompressedLen { get; }
    }

    private static byte[] Inflate(byte[] payload)
    {
        using var input = new MemoryStream(payload);
        using var zlib = new ZLibStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        zlib.CopyTo(output);
        return output.ToArray();
    }

    private static byte[] Deflate(byte[] payload)
    {
        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionLevel.Optimal, true))
        {
            zlib.Write(payload, 0, payload.Length);
        }

        return output.ToArray();
    }

    // --------------------------- message builders ---------------------------

    /// <summary>f3 as report15.md describes it: 128 random bits written as 32 hex characters.</summary>
    public static string NewSessionKey() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    /// <summary>
    /// The GameServer login. The client version (field 10) is optional: current
    /// 1.62 servers accept the login without it, so an old account JSON never
    /// has to be edited after a game update. When a version is supplied it is
    /// still sent for older server builds that expect the field.
    /// </summary>
    public static byte[] BuildLogin(ulong playerId, string sessionToken, string deviceId, string? version = null)
    {
        var body = Concat(
            FVarint(1, playerId), FVarint(2, playerId),
            FBytes(3, sessionToken), FBytes(9, deviceId));
        if (!string.IsNullOrWhiteSpace(version))
            body = Concat(body, FBytes(10, version));
        return Pack(Login, body);
    }

    public static byte[] BuildPing() => Pack(Ping);

    public static byte[] BuildPong() => Pack(Pong);

    /// <summary>KMsgCl2GsenterGameRequest - the empty 0x27A1 that starts the world load.</summary>
    public static byte[] BuildEnterGame() => Pack(EnterGame);

    /// <summary>KMsgCl2GsnoviceRecordRequest (0x2C96) {f1 = GuideId}.</summary>
    public static byte[] BuildNoviceRecord(ulong guideId = 10112001) => Pack(NoviceRecord, FVarint(1, guideId));

    public static byte[] BuildGuildAssistListRequest() => Pack(GuildAssistListRequest);

    public static byte[] BuildGuildBaseInfoRequest() => Pack(GuildBaseInfoRequest);

    public static byte[] BuildEatChickenPairingInfoRequest() => Pack(EatChickenPairingInfoRequest);

    public static byte[] BuildEatChickenIntoInstanceRequest(ulong regionId) =>
        Pack(EatChickenIntoInstanceRequest, FVarint(1, regionId));

    /// <summary>
    /// One Bounty turntable draw. MsgCL2GSActivityChickenTurntableRequest carries
    /// ActivityId(1) and Count(2), where Count is the number of spins (1..5).
    /// </summary>
    public static byte[] BuildChickenTurntableRequest(uint activityId, uint count)
    {
        if (activityId == 0)
            throw new ArgumentException("Bounty turntable needs its activity id.", nameof(activityId));
        if (count is 0 or > BountyMaxSpinsPerRequest)
            throw new ArgumentException(
                $"Bounty turntable takes 1 to {BountyMaxSpinsPerRequest} spins per request.", nameof(count));
        var body = Concat(FVarint(1, activityId), FVarint(2, count));
        return Pack(ChickenTurntableRequest, body);
    }

    /// <summary>
    /// Register the one-troop Bounty formation the automated client uses.
    /// The game accepts a single hero and a single troop as long as the
    /// formation passes its power check; the two hero slots are not mandatory.
    /// </summary>
    public static byte[] BuildEatChickenPairingRegister(EatChickenArmy army)
    {
        if (army.HeroId == 0)
            throw new ArgumentException("Bounty formation needs a hero.", nameof(army));
        if (army.TroopId == 0 || army.TroopCount == 0)
            throw new ArgumentException("Bounty formation needs at least one troop.", nameof(army));

        var troops = FMsg(3, Concat(
            FVarint(1, army.TroopId),
            FVarint(2, army.TroopCount)));
        var currentTroops = FMsg(8, Concat(
            FVarint(1, army.TroopId),
            FVarint(2, army.TroopCount)));
        var armyData = Concat(
            FVarint(1, army.HeroId),
            troops,
            FVarint(4, army.HeroLevel),
            FVarint(5, army.HeroAwakened ? 1UL : 0UL),
            currentTroops);
        return Pack(EatChickenPairingRegisterRequest, FMsg(1, armyData));
    }

    /// <summary>Builds the normal, non-premium Guild Assist help request.</summary>
    public static byte[] BuildGuildAssistHelpOther() => Pack(GuildAssistHelpOtherRequest, FVarint(1, 0));

    /// <summary>
    /// Use one stack from the bag. MsgCL2GSItemUseRequest carries Id(1),
    /// Count(2) and the optional target Params(3); a resource stack (food,
    /// wood, steel, oil) needs no target, the server simply moves the
    /// resources into the city.
    /// </summary>
    public static byte[] BuildItemUseRequest(uint itemId, uint count)
    {
        if (itemId == 0)
            throw new ArgumentException("Item id must be a positive number.", nameof(itemId));
        if (count == 0)
            throw new ArgumentException("Item count must be a positive number.", nameof(count));
        return Pack(ItemUseRequest, Concat(FVarint(1, itemId), FVarint(2, count)));
    }

    public static byte[] BuildGuildJoinRequest(ulong guildId)
    {
        if (guildId == 0)
            throw new ArgumentException("Alliance ID must be a positive 64-bit integer.", nameof(guildId));
        return Pack(GuildJoinRequest, FVarint(1, guildId));
    }

    public static byte[] BuildGuildQuitRequest() => Pack(GuildQuitRequest);

    public static byte[] BuildGuildRecommendListRequest() => Pack(GuildRecommendListRequest, FVarint(2, 1));

    public static byte[] BuildGuildSearchRequest(string tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
            throw new ArgumentException("Alliance TAG must not be empty.", nameof(tag));
        var criteria = Concat(FVarint(1, 1), FBytes(3, tag.Trim()));
        return Pack(GuildSearchRequest, FMsg(1, criteria));
    }

    // ------------------------------- parsers --------------------------------

    /// <summary>Decode <c>MsgGS2CLAllHeroNotice</c>; returns every owned hero.</summary>
    public static List<HeroRecord> ParseAllHeroNotice(byte[] body)
    {
        var heroes = new List<HeroRecord>();
        foreach (var outer in Parse(body))
        {
            if (outer.Number != 1 || !outer.IsBytes)
                continue;
            try
            {
                var fields = Parse(outer.Data);
                var id = GetVarint(fields, 1);
                if (id == 0)
                    continue;
                heroes.Add(new HeroRecord
                {
                    Id = id,
                    Level = GetVarint(fields, 2),
                    Awakened = GetVarint(fields, 18) != 0
                });
            }
            catch (ProtocolError)
            {
            }
        }

        return heroes;
    }

    /// <summary>Decode <c>MsgGS2CLAllItemNotice</c>; returns every stackable item.</summary>
    public static List<ItemRecord> ParseAllItemNotice(byte[] body)
    {
        var items = new List<ItemRecord>();
        foreach (var outer in Parse(body))
        {
            if (outer.Number != 1 || !outer.IsBytes)
                continue;
            try
            {
                var info = Parse(outer.Data);
                var id = (uint)GetVarint(info, 1);
                if (id != 0)
                    items.Add(new ItemRecord { Id = id, Count = GetVarint(info, 2) });
            }
            catch (ProtocolError)
            {
            }
        }

        return items;
    }

    /// <summary>Decode <c>MsgGS2CLUpdateItemNotice</c>; one changed item stack.</summary>
    public static ItemRecord? ParseUpdateItemNotice(byte[] body)
    {
        if (GetBytes(Parse(body), 1) is not { } info)
            return null;
        try
        {
            var fields = Parse(info);
            var id = (uint)GetVarint(fields, 1);
            return id == 0 ? null : new ItemRecord { Id = id, Count = GetVarint(fields, 2) };
        }
        catch (ProtocolError)
        {
            return null;
        }
    }

    /// <summary>
    /// Decode <c>MsgGS2CLItemUseReply</c>: the stack that was used, the server
    /// error code (0 on success) and the resources it paid out.
    /// </summary>
    public static ItemUseResult ParseItemUseReply(byte[] body)
    {
        var fields = Parse(body);
        var rewards = new List<ResourceGrant>();
        if (GetBytes(fields, 4) is { } rewardSet)
        {
            try
            {
                foreach (var field in Parse(rewardSet))
                {
                    if (field.Number != 1 || !field.IsBytes)
                        continue;
                    try
                    {
                        var resource = Parse(field.Data);
                        rewards.Add(new ResourceGrant
                        {
                            ResType = (uint)GetVarint(resource, 1),
                            SubType = (uint)GetVarint(resource, 2),
                            Value = GetVarint(resource, 3)
                        });
                    }
                    catch (ProtocolError)
                    {
                        // One malformed reward must not hide the result.
                    }
                }
            }
            catch (ProtocolError)
            {
            }
        }

        return new ItemUseResult
        {
            Id = (uint)GetVarint(fields, 1),
            Count = (uint)GetVarint(fields, 2),
            ErrorCode = (int)GetVarint(fields, 99),
            Rewards = rewards
        };
    }

    /// <summary>Decode <c>MsgGS2CLAllArmyNotice</c>; returns every owned troop stack.</summary>
    public static List<TroopRecord> ParseAllArmyNotice(byte[] body)
    {
        var troops = new Dictionary<ulong, ulong>();
        foreach (var outer in Parse(body))
        {
            if (outer.Number != 1 || !outer.IsBytes)
                continue;
            List<PbField> group;
            try
            {
                group = Parse(outer.Data);
            }
            catch (ProtocolError)
            {
                continue;
            }

            foreach (var entry in group)
            {
                if (entry.Number != 1 || !entry.IsBytes)
                    continue;
                try
                {
                    var info = Parse(entry.Data);
                    var id = GetVarint(info, 1);
                    var count = GetVarint(info, 2);
                    if (id != 0 && count != 0)
                        troops[id] = Math.Max(troops.GetValueOrDefault(id), count);
                }
                catch (ProtocolError)
                {
                }
            }
        }

        return troops.Select(pair => new TroopRecord { Id = pair.Key, Count = pair.Value }).ToList();
    }

    public static GuildState ParseGuildBaseInfoReply(byte[] body)
    {
        var fields = Parse(body);
        var errorCode = Get(fields, 99) is { IsVarint: true } error ? (int)error.Varint : 0;
        var info = GetBytes(fields, 1);
        if (info is null)
        {
            return new GuildState
            {
                Known = true,
                ErrorCode = errorCode
            };
        }

        return ParsePlayerGuildInfo(info, errorCode);
    }

    public static GuildState ParsePlayerGuildInfo(byte[] info, int errorCode = 0)
    {
        var fields = Parse(info);
        var guildId = GetVarint(fields, 2);
        return new GuildState
        {
            Known = true,
            GuildId = guildId > 0 ? guildId : null,
            Position = GetVarint(fields, 3),
            Name = DecodeText(GetBytes(fields, 4), 128),
            ShortName = DecodeText(GetBytes(fields, 5), 32),
            ErrorCode = errorCode
        };
    }

    public static GuildState ParseGuildJoinNoticeState(byte[] body)
    {
        var fields = Parse(body);
        var info = GetBytes(fields, 1);
        if (info is null)
            throw new ProtocolError("guildJoinNotice is missing its member record.");
        return ParsePlayerGuildInfo(info);
    }

    public static (ulong GuildId, ulong ErrorCode) ParseGuildJoinReplyDetails(byte[] body)
    {
        var fields = Parse(body);
        var guildId = Get(fields, 1) is { IsVarint: true } guild ? guild.Varint : 0;
        var errorCode = Get(fields, 99) is { IsVarint: true } error ? error.Varint : 0;
        return (guildId, errorCode);
    }

    public static ulong ParseGuildQuitReplyError(byte[] body)
    {
        var fields = Parse(body);
        return Get(fields, 99) is { IsVarint: true } error ? error.Varint : 0;
    }

    public static EatChickenPairingInfo ParseEatChickenPairingInfoReply(byte[] body)
    {
        var fields = Parse(body);
        return new EatChickenPairingInfo
        {
            ErrorCode = GetVarint(fields, 1),
            Status = GetVarint(fields, 2),
            Army = GetBytes(fields, 3) is { } army ? ParseEatChickenArmy(army) : null
        };
    }

    public static EatChickenRegisterReply ParseEatChickenPairingRegisterReply(byte[] body)
    {
        var fields = Parse(body);
        return new EatChickenRegisterReply
        {
            ErrorCode = GetVarint(fields, 1),
            Army = GetBytes(fields, 2) is { } army ? ParseEatChickenArmy(army) : null
        };
    }

    public static EatChickenArmy ParseEatChickenArmy(byte[] body)
    {
        var fields = Parse(body);
        var troops = GetBytes(fields, 3) ?? GetBytes(fields, 8);
        var troopId = 0UL;
        var troopCount = 0UL;
        if (troops is not null)
        {
            try
            {
                var troopFields = Parse(troops);
                troopId = GetVarint(troopFields, 1);
                troopCount = GetVarint(troopFields, 2);
            }
            catch (ProtocolError)
            {
            }
        }

        return new EatChickenArmy
        {
            HeroId = GetVarint(fields, 1),
            HeroLevel = GetVarint(fields, 4),
            HeroAwakened = GetVarint(fields, 5) != 0,
            TroopId = troopId,
            TroopCount = troopCount
        };
    }

    public static ulong ParseEatChickenRegionId(byte[] body) => GetVarint(Parse(body), 1);

    /// <summary>Decode the account's own name, Might and bag resources.</summary>
    public static PlayerBaseInfo ParsePlayerBaseNotice(byte[] body)
    {
        var fields = Parse(body);
        return new PlayerBaseInfo
        {
            PlayerId = GetVarint(fields, 1),
            Name = DecodeText(GetBytes(fields, 2), 128) ?? string.Empty,
            Level = GetVarint(fields, 3),
            Oil = GetVarint(fields, 8),
            Food = GetVarint(fields, 14),
            Wood = GetVarint(fields, 15),
            Steel = GetVarint(fields, 16),
            Might = GetVarint(fields, 19)
        };
    }

    /// <summary>Decode the turntable state notice; it tells us the live activity id.</summary>
    public static BountyTurntableInfo ParseChickenTurntableNotice(byte[] body)
    {
        var fields = Parse(body);
        var info = GetBytes(fields, 1);
        if (info is null)
            return new BountyTurntableInfo();
        var inner = Parse(info);
        return new BountyTurntableInfo
        {
            ActivityId = (uint)GetVarint(inner, 1),
            RefreshCount = (uint)GetVarint(inner, 3)
        };
    }

    /// <summary>Decode the result of one turntable draw, including its error code.</summary>
    public static BountyTurntableReply ParseChickenTurntableReply(byte[] body)
    {
        var fields = Parse(body);
        var rewards = new List<BountyTurntableReward>();
        foreach (var field in fields)
        {
            if (field.Number != 3 || !field.IsBytes)
                continue;
            try
            {
                var inner = Parse(field.Data);
                rewards.Add(new BountyTurntableReward
                {
                    ResType = (uint)GetVarint(inner, 1),
                    SubType = (uint)GetVarint(inner, 2),
                    Value = GetVarint(inner, 3)
                });
            }
            catch (ProtocolError)
            {
                // One malformed reward must not hide the rest of the reply.
            }
        }

        var rewardIds = new List<uint>();
        foreach (var field in fields)
        {
            if (field.Number != 4)
                continue;
            if (field.IsVarint)
            {
                rewardIds.Add((uint)field.Varint);
                continue;
            }

            if (!field.IsBytes)
                continue;
            var pos = 0;
            while (pos < field.Data.Length)
            {
                var value = DecVarint(field.Data, pos);
                pos = value.Pos;
                rewardIds.Add((uint)value.Value);
            }
        }

        return new BountyTurntableReply
        {
            ActivityId = (uint)GetVarint(fields, 1),
            Count = (uint)GetVarint(fields, 2),
            ErrorCode = (int)GetVarint(fields, 99),
            Rewards = rewards,
            RewardIds = rewardIds
        };
    }

    public static List<GuildInfo> ParseGuildRecommendListReply(byte[] body)
    {
        var guilds = new Dictionary<ulong, GuildInfo>();
        foreach (var outer in Parse(body))
        {
            if (outer.Number is not (1 or 2 or 3) || !outer.IsBytes)
                continue;
            List<PbField> fields;
            try
            {
                fields = Parse(outer.Data);
            }
            catch (ProtocolError)
            {
                continue;
            }

            var guildField = Get(fields, 1);
            if (guildField is not { IsVarint: true } || guildField.Varint == 0)
                continue;
            var name = GetBytes(fields, 2);
            var shortName = GetBytes(fields, 3) ?? Array.Empty<byte>();
            if (name is null)
                continue;
            string decodedName;
            string decodedShortName;
            try
            {
                var strict = new UTF8Encoding(false, true);
                decodedName = strict.GetString(name);
                decodedShortName = strict.GetString(shortName);
            }
            catch (DecoderFallbackException)
            {
                continue;
            }

            guilds[guildField.Varint] = new GuildInfo
            {
                Id = guildField.Varint,
                Name = decodedName,
                ShortName = decodedShortName
            };
        }

        return guilds.Values.ToList();
    }

    public static List<GuildInfo> ParseGuildSearchReply(byte[] body) => ParseGuildRecommendListReply(body);

    public static ulong ParseGuildJoinReply(byte[] body)
    {
        var fields = Parse(body);
        var guild = Get(fields, 1);
        if (guild is not { IsVarint: true } || guild.Varint == 0)
            throw new ProtocolError("guildJoinReply is missing its Alliance ID.");
        return guild.Varint;
    }

    public static ulong ParseGuildJoinNotice(byte[] body)
    {
        var fields = Parse(body);
        var member = GetBytes(fields, 1);
        if (member is null)
            throw new ProtocolError("guildJoinNotice is missing its member record.");
        var inner = Parse(member);
        var guild = Get(inner, 2);
        if (guild is not { IsVarint: true } || guild.Varint == 0)
            throw new ProtocolError("guildJoinNotice member has no valid Alliance ID.");
        return guild.Varint;
    }

    /// <summary>
    /// Decode the active assist request records. Field 1 holds one
    /// <c>GuildAssistDetail</c> per requester; field 3 is the already-helped
    /// history and must not be treated as pending work.
    /// </summary>
    public static GuildAssistListReply ParseGuildAssistListReply(byte[] body)
    {
        var fields = Parse(body);
        var details = new List<GuildAssistDetailRecord>();
        foreach (var field in fields)
        {
            if (field.Number != 1 || !field.IsBytes)
                continue;
            try
            {
                details.Add(ParseGuildAssistDetail(field.Data));
            }
            catch (ProtocolError)
            {
                // One malformed record must not hide the remaining requests.
            }
        }

        var errorCode = Get(fields, 99);
        if (errorCode is null)
            errorCode = Get(fields, 4);
        return new GuildAssistListReply
        {
            PendingCount = details.Count(detail => detail.NeedsHelp),
            DetailsCount = details.Count,
            Details = details,
            ErrorCode = errorCode is { IsVarint: true } ? errorCode.Varint : 0
        };
    }

    public static GuildAssistDetailRecord ParseGuildAssistDetail(byte[] body)
    {
        var fields = Parse(body);
        var type = 0UL;
        var itemId = 0UL;
        var itemLevel = 0UL;
        var itemParam = 0UL;
        if (GetBytes(fields, 2) is { } item)
        {
            var itemFields = Parse(item);
            type = GetVarint(itemFields, 1);
            itemId = GetVarint(itemFields, 2);
            itemLevel = GetVarint(itemFields, 3);
            itemParam = GetVarint(itemFields, 4);
        }

        return new GuildAssistDetailRecord
        {
            PlayerId = GetVarint(fields, 1),
            AssistType = type,
            ItemId = itemId,
            ItemLevel = itemLevel,
            ItemParam = itemParam,
            MaxTimes = GetVarint(fields, 5),
            HelpedTimes = GetVarint(fields, 10),
            IsHelped = GetVarint(fields, 11) != 0,
            CompletedHelpers = CountVarints(fields, 3),
            WaitingHelpers = CountVarints(fields, 4)
        };
    }

    /// <summary>Count a repeated varint field, accepting both packed and unpacked encodings.</summary>
    private static int CountVarints(List<PbField> fields, int number)
    {
        var count = 0;
        foreach (var field in fields)
        {
            if (field.Number != number)
                continue;
            if (field.IsVarint)
            {
                count++;
                continue;
            }

            if (!field.IsBytes)
                continue;
            var pos = 0;
            while (pos < field.Data.Length)
            {
                var value = DecVarint(field.Data, pos);
                pos = value.Pos;
                count++;
            }
        }

        return count;
    }

    /// <summary>Decode the help result; recent game builds use ErrorCode field 99.</summary>
    public static GuildAssistHelpReply ParseGuildAssistHelpOtherReply(byte[] body)
    {
        var fields = Parse(body);
        var errorCode = Get(fields, 99);
        if (errorCode is null)
            errorCode = Get(fields, 3);
        return new GuildAssistHelpReply
        {
            AssistCount = GetVarint(fields, 1),
            Contribution = GetVarint(fields, 2),
            ErrorCode = errorCode is { IsVarint: true } ? errorCode.Varint : 0
        };
    }

    // --------------------- two-stage login (IL2CPP MsgType) ------------------

    public static byte[] LsLogin(ulong account, string webSession, string udid,
        ulong campId = 0, ulong areaId = 0, string gameId = DefaultGameId,
        ulong language = 1, string mac = "", ulong beManagedId = 0, bool aiRobot = false)
    {
        var body = Array.Empty<byte>();
        if (account != 0)
            body = Concat(body, FVarint(3, account));
        if (!string.IsNullOrEmpty(webSession))
            body = Concat(body, FBytes(4, webSession));
        if (!string.IsNullOrEmpty(mac))
            body = Concat(body, FBytes(5, mac));
        if (language != 0)
            body = Concat(body, FVarint(6, language));
        if (areaId != 0)
            body = Concat(body, FVarint(7, areaId));
        if (!string.IsNullOrEmpty(gameId))
            body = Concat(body, FBytes(8, gameId));
        if (campId != 0)
            body = Concat(body, FVarint(9, campId));
        if (!string.IsNullOrEmpty(udid))
            body = Concat(body, FBytes(10, udid));
        if (beManagedId != 0)
            body = Concat(body, FVarint(11, beManagedId));
        if (aiRobot)
            body = Concat(body, FVarint(12, 1));
        return Pack(LsLoginReq, body);
    }

    public static LsLoginFields LsLoginFieldsOf(List<PbField> fields) => new()
    {
        Account = Get(fields, 3) is { IsVarint: true } account ? account.Varint : null,
        WebSession = GetBytes(fields, 4),
        Mac = GetBytes(fields, 5),
        Language = Get(fields, 6) is { IsVarint: true } language ? language.Varint : null,
        AreaId = Get(fields, 7) is { IsVarint: true } area ? area.Varint : null,
        GameId = GetBytes(fields, 8),
        CampId = Get(fields, 9) is { IsVarint: true } camp ? camp.Varint : null,
        Udid = GetBytes(fields, 10),
        BeManagedId = Get(fields, 11) is { IsVarint: true } managed ? managed.Varint : null,
        AiRobot = Get(fields, 12) is { IsVarint: true } robot ? robot.Varint : null
    };

    /// <summary>A real 0x279D carries Account(3), a long WebSession(4) and a Udid(10).</summary>
    public static bool LooksLikeLsRequest(List<PbField> fields)
    {
        var account = Get(fields, 3);
        if (account is not { IsVarint: true } || account.Varint == 0)
            return false;
        var webSession = GetBytes(fields, 4);
        return webSession is not null && webSession.Length >= 32;
    }

    /// <summary>Decode MsgLS2CLLoginReply (opcode 0x279E) into its named fields.</summary>
    public static LsLoginReply ParseLsLoginReply(byte[] body)
    {
        var fields = Parse(body);
        var netRaw = GetBytes(fields, 2);
        var net = new List<PbField>();
        if (netRaw is not null)
        {
            try
            {
                net = Parse(netRaw);
            }
            catch (ProtocolError)
            {
                net = new List<PbField>();
            }
        }

        var playerId = Get(fields, 1);
        var kingdomId = Get(fields, 5);
        var errorCode = Get(fields, 8);
        var netPort = Get(net, 1);
        return new LsLoginReply
        {
            PlayerId = playerId is { IsVarint: true } value ? value.Varint : null,
            NetIp = DecodeText(GetBytes(net, 2), 64),
            NetPort = netPort is { IsVarint: true } port && port.Varint <= int.MaxValue
                ? (int)port.Varint
                : null,
            LoginSession = DecodeText(GetBytes(fields, 3), 128),
            ServerVersion = DecodeText(GetBytes(fields, 4), 128),
            KingdomId = kingdomId is { IsVarint: true } kingdom ? kingdom.Varint : null,
            Country = DecodeText(GetBytes(fields, 7), 64),
            ErrorCode = errorCode is { IsVarint: true } error ? error.Varint : null
        };
    }

    /// <summary>A real 0x279E carries PlayerId(1) and a short LoginSession string(3).</summary>
    public static bool LooksLikeLsReply(List<PbField> fields)
    {
        var player = Get(fields, 1);
        if (player is not { IsVarint: true })
            return false;
        var session = GetBytes(fields, 3);
        return session is not null && session.Length is >= 4 and <= 128;
    }

    /// <summary>A real 0x279F carries PlayerId(1), Account(2) and LoginSession(3).</summary>
    public static bool LooksLikeGsLogin(List<PbField> fields)
    {
        var player = Get(fields, 1);
        if (player is not { IsVarint: true } || player.Varint == 0)
            return false;
        var session = GetBytes(fields, 3);
        return session is not null && session.Length is >= 4 and <= 128;
    }

    /// <summary>
    /// Decode a field to text, but only if it really is bytes of sane length.
    /// Frames found by scanning arbitrary captured traffic can be false positives,
    /// so every field is type-checked instead of assumed.
    /// </summary>
    public static string? DecodeText(byte[]? value, int limit = 0)
    {
        if (value is null)
            return null;
        if (limit != 0 && value.Length > limit)
            return null;
        var text = Encoding.UTF8.GetString(value);
        return text.Length == 0 ? null : text;
    }

    // --------------------------- startup burst ------------------------------

    /// <summary>
    /// The startup request burst the real client sends after the world arrives.
    /// These are ~45 ordinary client requests (guild/mail/pet/arena info lists)
    /// that the real client fires while its UI built itself - not a required
    /// handshake.
    /// </summary>
    public static byte[] StartupBurst(ulong? armyId = null, ulong? guildId = null,
        ulong? playerId = null, string? publicIp = null, string? sessionUuid = null)
    {
        var output = new List<byte[]>();
        void Add(ushort op, byte[]? body = null) => output.Add(Pack(op, body));

        if (armyId is > 0)
            Add(HsArmy, FVarint(1, armyId.Value));
        Add(0x3810);
        Add(0x35F2);
        Add(0x3559, Concat(FVarint(1, 130413), FVarint(2, 12)));
        Add(0x3753);
        if (guildId is > 0)
            Add(0x34BA, FVarint(2, guildId.Value));
        Add(0x3570);
        Add(0x3595);
        Add(0x2949);
        Add(0x3D26);
        Add(0x382E, FBytes(1, new byte[] { 0 }));
        Add(0x39F9);
        if (guildId is > 0)
            Add(0x2752, FVarint(1, guildId.Value));
        Add(0x274A);
        Add(0x2991);
        Add(0x2C7F);
        Add(0x2C7B);
        Add(0x2E90);
        Add(0x32D2);
        Add(0x3D8E);
        Add(0x3AF8, FVarint(2, 4));
        if (!string.IsNullOrEmpty(sessionUuid))
            Add(0x2CB8, FBytes(1, sessionUuid));
        Add(0x31D0);
        Add(0x31DF);
        Add(0x2F66);
        Add(0x3D0A);
        Add(0x2B0D);
        Add(0x32F9);
        Add(0x3337);
        Add(0x2CCF);
        Add(0x2C1F);
        Add(0x359F);
        Add(0x28EF);
        Add(0x311E, FVarint(1, 14));
        Add(0x2CE3);
        Add(0x33FC);
        Add(0x379D);
        Add(0x32F4, Concat(FVarint(1, 1001), FVarint(2, 124)));
        if (!string.IsNullOrEmpty(publicIp))
            Add(0x3A42, FBytes(1, publicIp));
        Add(0x311E, FVarint(1, 10));
        Add(0x2AE9);
        if (playerId is > 0)
        {
            Add(0x280B, FVarint(1, playerId.Value));
            Add(0x280F, FVarint(1, playerId.Value));
        }

        Add(0x36B5);
        Add(0x36F4, Concat(FVarint(1, 1985), FVarint(3, 3)));
        Add(0x2D9D);
        Add(0x2CDE);
        Add(0x3224, FVarint(1, 100355));
        Add(0x3224, FVarint(1, 100355));
        return Concat(output.ToArray());
    }

    // ------------------------------- movement --------------------------------

    private static byte[] Coord(long x, long y) => FMsg(2, FVarint(1, (ulong)x), FVarint(2, (ulong)y));

    public static byte[] MoveSimple(long x, long y, ulong armyId, ulong marchType = 4) =>
        Pack(Move, Concat(
            FVarint(1, marchType),
            FMsg(2, Coord(x, y), FVarint(3, 1), FVarint(5, 1)),
            FVarint(3, armyId)));

    public static byte[] MoveToTarget(long x, long y, ulong targetId, ulong armyId, ulong marchType = 4) =>
        Pack(Move, Concat(
            FVarint(1, marchType),
            FMsg(2, Coord(x, y), FVarint(3, 2), FVarint(4, targetId), FVarint(5, 1)),
            FVarint(3, armyId)));

    public static byte[] ReturnToShelter(ulong armyId, ulong marchType = 4) =>
        Pack(Move, Concat(
            FVarint(1, marchType),
            FMsg(2, FVarint(3, 3), FVarint(5, 1)),
            FVarint(3, armyId)));

    // ----------------------------- Trading Post ------------------------------

    /// <summary>
    /// Ask what the sender and the target already moved today and which Trading
    /// Post level each of them has. A region of 0 means "the region this
    /// session is in", which is exactly what the real client relies on.
    /// </summary>
    public static byte[] BuildCarriageCapacityRequest(ulong targetPlayerId, ulong regionId = 0)
    {
        if (targetPlayerId == 0)
            throw new ArgumentException("Target player id must not be zero.", nameof(targetPlayerId));
        var body = FVarint(1, targetPlayerId);
        if (regionId != 0)
            body = Concat(body, FVarint(3, regionId));
        return Pack(CarriageCapacityRequest, body);
    }

    /// <summary>Ask the server for the account's city, including its buildings.</summary>
    public static byte[] BuildCityInfoRequest() => Pack(CityInfoRequest);

    /// <summary>
    /// Dispatch a transport truck (MarchCommandTarget 11) that carries one
    /// resource stack to the castle entity standing on (x, y).
    /// </summary>
    public static byte[] BuildCreateCarriageRequest(long x, long y, ulong targetEntityId,
        uint subType, ulong value, ulong regionId = 0)
    {
        if (targetEntityId == 0)
            throw new ArgumentException("Target entity id must not be zero.", nameof(targetEntityId));
        if (value == 0)
            throw new ArgumentOutOfRangeException(nameof(value), "A transport must carry at least one resource.");
        var command = Concat(
            FMsg(2, FVarint(1, (ulong)x), FVarint(2, (ulong)y)),
            FVarint(3, CarriageSendTargetType),
            FVarint(4, targetEntityId));
        var resource = FMsg(1, FVarint(1, BasicResourceType), FVarint(2, subType), FVarint(3, value));
        return Pack(CarriageCreateRequest, Concat(FMsg(1, command), FMsg(2, resource)));
    }

    /// <summary>Decode <c>MsgGS2CLGetCarriageCapacityReply</c>.</summary>
    public static CarriageCapacityReply ParseCarriageCapacityReply(byte[] body)
    {
        var fields = Parse(body);
        var entries = new List<CarriageCapacityEntry>();
        if (GetBytes(fields, 1) is { } info)
        {
            foreach (var field in Parse(info))
            {
                if (field.Number != 1 || !field.IsBytes)
                    continue;
                try
                {
                    var entry = Parse(field.Data);
                    entries.Add(new CarriageCapacityEntry
                    {
                        PlayerId = GetVarint(entry, 4),
                        SentToday = GetVarint(entry, 2),
                        ReceivedToday = GetVarint(entry, 3),
                        TradingPostLevel = (uint)GetVarint(entry, 5)
                    });
                }
                catch (ProtocolError)
                {
                    // One malformed side must not hide the other one.
                }
            }
        }

        return new CarriageCapacityReply
        {
            RegionId = GetVarint(fields, 2),
            ErrorCode = (int)GetVarint(fields, 99),
            Entries = entries
        };
    }

    /// <summary>
    /// Decode <c>MsgGS2CLSyncEntitiesDataNotice</c> (0x298D) and keep the castles
    /// among the announced entities.
    /// <para>
    /// Field 2 is an entity blob: a fixed four-byte entity count, then per entity a
    /// uint32 kind, a uint64 entity id, a uint32 (unknown, always 1) and a
    /// uint32 component count, followed by {uint32 component kind, uint32 length,
    /// bytes} components. Component kind 2 holds the player profile
    /// (<c>PlayerShowInfo</c>: 1 id, 2 name, 6 guild short name), which only a
    /// castle has. Field 4 says the blob is zlib-compressed.
    /// </para>
    /// </summary>
    public static IReadOnlyList<WorldCastle> ParseSyncEntitiesNotice(byte[] body)
    {
        var fields = Parse(body);
        if (GetBytes(fields, 2) is not { } blob)
            return Array.Empty<WorldCastle>();

        var compressed = GetVarint(fields, 4) != 0;
        if (compressed)
        {
            try
            {
                blob = Inflate(blob);
            }
            catch (Exception exception) when (exception is InvalidDataException or IOException)
            {
                throw new ProtocolError("The entity blob could not be decompressed: " + exception.Message);
            }
        }

        var reader = new SpanReader(blob);
        var count = reader.UInt32();
        if (count > 4096)
            throw new ProtocolError($"The entity blob claims {count} entities, which is too many.");

        var castles = new List<WorldCastle>();
        for (var i = 0u; i < count; i++)
        {
            reader.UInt32();   // entity kind: 1 castle, 2 resource node, 3 army, ...
            var entityId = reader.UInt64();
            reader.UInt32();   // unknown, always 1
            var components = reader.UInt32();
            ulong playerId = 0;
            string name = string.Empty;
            string guild = string.Empty;
            for (var c = 0u; c < components; c++)
            {
                var kind = reader.UInt32();
                var length = checked((int)reader.UInt32());
                var data = reader.Bytes(length);
                if (kind != 2)
                    continue;
                try
                {
                    var show = Parse(data);
                    playerId = GetVarint(show, 1);
                    name = DecodeText(GetBytes(show, 2), 128) ?? string.Empty;
                    guild = DecodeText(GetBytes(show, 6), 32) ?? string.Empty;
                }
                catch (ProtocolError)
                {
                    // A profile this build cannot read still tells us the castle exists.
                }
            }

            if (playerId != 0 && entityId != 0)
                castles.Add(new WorldCastle
                {
                    EntityId = entityId,
                    PlayerId = playerId,
                    PlayerName = name,
                    GuildShortName = guild
                });
        }

        return castles;
    }

    /// <summary>
    /// The level of the account's own Trading Post as <c>MsgGS2CLCityInfoReply</c>
    /// (0x272C) reports it: CityInfo(1) → BuildingInfo(1) → Type(2) 1031 → Level(3).
    /// Returns null when the city has no Trading Post yet.
    /// </summary>
    public static uint? ParseTradingPostLevel(byte[] body)
    {
        if (GetBytes(Parse(body), 1) is not { } city)
            return null;

        uint? level = null;
        foreach (var field in Parse(city))
        {
            if (field.Number != 1 || !field.IsBytes)
                continue;
            try
            {
                var building = Parse(field.Data);
                if (GetVarint(building, 2) != TradingPostBuildingType)
                    continue;
                var value = (uint)GetVarint(building, 3);
                if (value != 0 && (level is null || value > level))
                    level = value;
            }
            catch (ProtocolError)
            {
                // Skip a building record this build cannot read.
            }
        }

        return level;
    }

    /// <summary>The game's <c>BuildType</c> of the Trading Post.</summary>
    public const uint TradingPostBuildingType = 1031;

    /// <summary>Decode <c>MsgGS2CLCreateCarriageReply</c>.</summary>
    public static CarriageCreateResult ParseCreateCarriageReply(byte[] body)
    {
        var fields = Parse(body);
        var result = new CarriageCreateResult
        {
            ErrorCode = (int)GetVarint(fields, 99)
        };
        if (GetBytes(fields, 1) is not { } data)
            return result;
        try
        {
            var carriage = Parse(data);
            var value = 0UL;
            if (GetBytes(carriage, 6) is { } resources)
            {
                foreach (var field in Parse(resources))
                {
                    if (field.Number != 1 || !field.IsBytes)
                        continue;
                    try
                    {
                        value += GetVarint(Parse(field.Data), 3);
                    }
                    catch (ProtocolError)
                    {
                    }
                }
            }

            return new CarriageCreateResult
            {
                ErrorCode = result.ErrorCode,
                MarchIndex = (uint)GetVarint(carriage, 1),
                EntityId = GetVarint(carriage, 3),
                Tax = (uint)GetVarint(carriage, 5),
                Value = value
            };
        }
        catch (ProtocolError)
        {
            return result;
        }
    }

    /// <summary>The region id carried by <c>MsgG2CLIntoRegionNotice</c>.</summary>
    public static ulong ParseRegionId(byte[] body) => GetVarint(Parse(body), 1);

    // --------------------------------- helpers -------------------------------

    internal static byte[] Concat(params byte[][] parts)
    {
        var total = parts.Sum(part => part.Length);
        var joined = new byte[total];
        var offset = 0;
        foreach (var part in parts)
        {
            Array.Copy(part, 0, joined, offset, part.Length);
            offset += part.Length;
        }

        return joined;
    }

    /// <summary>Little-endian cursor over the binary entity blob of 0x298D.</summary>
    private struct SpanReader
    {
        private readonly byte[] _data;
        private int _offset;

        public SpanReader(byte[] data)
        {
            _data = data;
            _offset = 0;
        }

        public uint UInt32()
        {
            Take(4);
            var value = BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(_offset, 4));
            _offset += 4;
            return value;
        }

        public ulong UInt64()
        {
            Take(8);
            var value = BinaryPrimitives.ReadUInt64LittleEndian(_data.AsSpan(_offset, 8));
            _offset += 8;
            return value;
        }

        public byte[] Bytes(int length)
        {
            if (length < 0)
                throw new ProtocolError("The entity blob has a negative field length.");
            Take(length);
            var slice = _data.AsSpan(_offset, length).ToArray();
            _offset += length;
            return slice;
        }

        private void Take(int count)
        {
            if (count > _data.Length - _offset)
                throw new ProtocolError("The entity blob ended in the middle of a record.");
        }
    }
}
