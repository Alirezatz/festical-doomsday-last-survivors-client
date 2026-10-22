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

/// <summary>Result of <c>parse_guild_assist_list_reply</c>.</summary>
public sealed class GuildAssistListReply
{
    public int PendingCount { get; init; }
    public int DetailsCount { get; init; }
    public ulong ErrorCode { get; init; }
}

/// <summary>Result of <c>parse_guild_assist_help_other_reply</c>.</summary>
public sealed class GuildAssistHelpReply
{
    public ulong AssistCount { get; init; }
    public ulong Contribution { get; init; }
    public ulong ErrorCode { get; init; }
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
    public const ushort GuildJoinNotice = 0x2744;
    public const ushort GuildQuitNotice = 0x2745;
    public const ushort GuildJoinRequest = 0x2746;
    public const ushort GuildJoinReply = 0x2747;
    public const ushort GuildQuitRequest = 0x2748;
    public const ushort GuildQuitReply = 0x2749;
    public const ushort GuildSearchRequest = 0x275F;
    public const ushort GuildSearchReply = 0x2760;
    public const ushort GuildRecommendListRequest = 0x2761;
    public const ushort GuildRecommendListReply = 0x2762;

    // ---- two-stage login opcodes (from the IL2CPP dump: MsgType enum) -----
    public const ushort LsLoginReq = 0x279D;
    public const ushort LsLoginReply = 0x279E;
    public const ushort GsLogin = Login;
    public const ushort GsLoginAck = LoginAck;

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

    public static byte[] BuildLogin(ulong playerId, string sessionToken, string deviceId, string version = "1.61.5") =>
        Pack(Login, Concat(
            FVarint(1, playerId), FVarint(2, playerId),
            FBytes(3, sessionToken), FBytes(9, deviceId), FBytes(10, version)));

    public static byte[] BuildPing() => Pack(Ping);

    public static byte[] BuildPong() => Pack(Pong);

    /// <summary>KMsgCl2GsenterGameRequest - the empty 0x27A1 that starts the world load.</summary>
    public static byte[] BuildEnterGame() => Pack(EnterGame);

    /// <summary>KMsgCl2GsnoviceRecordRequest (0x2C96) {f1 = GuideId}.</summary>
    public static byte[] BuildNoviceRecord(ulong guideId = 10112001) => Pack(NoviceRecord, FVarint(1, guideId));

    public static byte[] BuildGuildAssistListRequest() => Pack(GuildAssistListRequest);

    /// <summary>Builds the normal, non-premium Guild Assist help request.</summary>
    public static byte[] BuildGuildAssistHelpOther() => Pack(GuildAssistHelpOtherRequest, FVarint(1, 0));

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

    /// <summary>Count active assist request records; WaitAssists contains helpers, not requests.</summary>
    public static GuildAssistListReply ParseGuildAssistListReply(byte[] body)
    {
        var fields = Parse(body);
        var detailsCount = 0;
        foreach (var field in fields)
        {
            if (field.Number != 1 || !field.IsBytes)
                continue;
            Parse(field.Data);
            detailsCount++;
        }

        var errorCode = Get(fields, 99);
        if (errorCode is null)
            errorCode = Get(fields, 4);
        return new GuildAssistListReply
        {
            PendingCount = detailsCount,
            DetailsCount = detailsCount,
            ErrorCode = errorCode is { IsVarint: true } ? errorCode.Varint : 0
        };
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
}
