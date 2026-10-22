using System.IO;
using System.Text.Json.Nodes;
using System.Text.Json;
using System.Text;

namespace FesticalDoomsday.Client.Backend;

/// <summary>
/// CaptureExtractor.cs - read the game's own login out of a packet capture and
/// save its session data to an account JSON.
///
/// The capture has to contain the LoginServer request and the GameServer login
/// of the real game. Ported from capture_f3.py; the JSON contains live account
/// credentials, so it must be kept private.
/// </summary>
internal static class CaptureExtractor
{
    /// <summary>Extract the login fields from a capture file.</summary>
    public static JsonObject? ExtractLogin(string tshark, string pcapPath, Action<string>? log = null)
    {
        var streams = LiveCapture.ReadStreams(tshark, pcapPath);
        JsonObject? best = null;
        JsonObject? lsReply = null;
        JsonObject? lsRequest = null;

        foreach (var stream in streams.OrderBy(item => item.Key).Select(item => item.Value))
        {
            foreach (var side in new[] { "a", "b" })
            {
                var blob = side == "a" ? stream.DataA : stream.DataB;
                if (blob.Length == 0)
                    continue;
                var peer = side == "a" ? stream.B : stream.A;
                foreach (var (opcode, body) in LiveCapture.DecodeStream(blob, log))
                {
                    if (opcode == Protocol.LsLoginReq)
                    {
                        List<PbField> fields;
                        try
                        {
                            fields = Protocol.Parse(body);
                        }
                        catch (ProtocolError)
                        {
                            continue;
                        }

                        if (!Protocol.LooksLikeLsRequest(fields))
                            continue;
                        lsRequest = BuildLsRequest(Protocol.LsLoginFieldsOf(fields));
                        // The LoginServer the client actually talked to.
                        lsRequest["login_server"] = $"{peer.Ip}:{peer.Port}";
                    }
                    else if (opcode == Protocol.LsLoginReply && lsReply is null)
                    {
                        List<PbField> fields;
                        try
                        {
                            fields = Protocol.Parse(body);
                        }
                        catch (ProtocolError)
                        {
                            continue;
                        }

                        if (!Protocol.LooksLikeLsReply(fields))
                            continue;
                        lsReply = BuildLsReply(Protocol.ParseLsLoginReply(body));
                    }
                    else if (opcode == Protocol.GsLogin)
                    {
                        List<PbField> fields;
                        try
                        {
                            fields = Protocol.Parse(body);
                        }
                        catch (ProtocolError)
                        {
                            continue;
                        }

                        if (!Protocol.LooksLikeGsLogin(fields))
                            continue;
                        var session = Protocol.DecodeText(Protocol.GetBytes(fields, 3));
                        if (session is null || best is not null)
                            continue;
                        var account = Protocol.Get(fields, 2);
                        if (account is null || account.Varint == 0)
                            account = Protocol.Get(fields, 1);
                        best = new JsonObject
                        {
                            ["game_server"] = $"{peer.Ip}:{peer.Port}",
                            ["f3"] = session,
                            ["account"] = account is { IsVarint: true } ? JsonValue.Create(account.Varint) : null,
                            ["player_id"] = Protocol.Get(fields, 1) is { IsVarint: true } player
                                ? JsonValue.Create(player.Varint)
                                : null,
                            ["device_id"] = ToJsonValue(Protocol.Get(fields, 9)),
                            ["version"] = VersionText(Protocol.Get(fields, 10))
                        };
                    }
                }
            }
        }

        if (best is null)
            return null;
        if (lsReply is not null)
            best["ls_reply"] = lsReply;
        if (lsRequest is not null)
        {
            best["ls_request"] = lsRequest;
            best["web_session"] = lsRequest["web_session"]?.DeepClone();
        }

        return best;
    }

    private static string VersionText(PbField? field)
    {
        var text = Protocol.DecodeText(field?.Data);
        return string.IsNullOrEmpty(text) ? "1.61.5" : text;
    }

    private static JsonNode? ToJsonValue(PbField? field)
    {
        if (field is null)
            return null;
        return field.Kind switch
        {
            PbKind.Varint => JsonValue.Create(field.Varint),
            PbKind.Bytes => JsonValue.Create(field.Text()),
            _ => JsonValue.Create(Convert.ToHexString(field.Data).ToLowerInvariant())
        };
    }

    /// <summary>
    /// Name the fields of MsgCL2LSLoginRequest (0x279D) as seen on the wire and
    /// keep only the ones the account JSON needs.
    /// </summary>
    private static JsonObject BuildLsRequest(LsLoginFields fields)
    {
        var allowed = new HashSet<string>
        {
            "account", "web_session", "udid", "game_id",
            "language", "area_id", "camp_id", "mac", "ai_robot"
        };
        var source = new Dictionary<string, JsonNode?>
        {
            ["account"] = fields.Account is null ? null : JsonValue.Create(fields.Account.Value),
            ["web_session"] = fields.WebSession is null ? null : JsonValue.Create(Protocol.DecodeText(fields.WebSession) ?? string.Empty),
            ["mac"] = fields.Mac is null ? null : JsonValue.Create(Protocol.DecodeText(fields.Mac) ?? string.Empty),
            ["language"] = fields.Language is null ? null : JsonValue.Create(fields.Language.Value),
            ["area_id"] = fields.AreaId is null ? null : JsonValue.Create(fields.AreaId.Value),
            ["game_id"] = fields.GameId is null ? null : JsonValue.Create(Protocol.DecodeText(fields.GameId) ?? string.Empty),
            ["camp_id"] = fields.CampId is null ? null : JsonValue.Create(fields.CampId.Value),
            ["udid"] = fields.Udid is null ? null : JsonValue.Create(Protocol.DecodeText(fields.Udid) ?? string.Empty),
            ["be_managed_id"] = fields.BeManagedId is null ? null : JsonValue.Create(fields.BeManagedId.Value),
            ["ai_robot"] = fields.AiRobot is null ? null : JsonValue.Create(fields.AiRobot.Value)
        };

        var result = new JsonObject();
        foreach (var (key, value) in source)
        {
            if (allowed.Contains(key))
                result[key] = value?.DeepClone();
        }

        return result;
    }

    private static JsonObject BuildLsReply(LsLoginReply reply) => new()
    {
        ["player_id"] = reply.PlayerId is null ? null : JsonValue.Create(reply.PlayerId.Value),
        ["net_ip"] = reply.NetIp,
        ["net_port"] = reply.NetPort is null ? null : JsonValue.Create(reply.NetPort.Value),
        ["login_session"] = reply.LoginSession,
        ["server_version"] = reply.ServerVersion,
        ["kingdom_id"] = reply.KingdomId is null ? null : JsonValue.Create(reply.KingdomId.Value),
        ["country"] = reply.Country,
        ["error_code"] = reply.ErrorCode is null ? null : JsonValue.Create(reply.ErrorCode.Value)
    };

    /// <summary>Write the account JSON without ever leaving a half-written file behind.</summary>
    public static string WriteJsonAtomic(string path, JsonObject data)
    {
        path = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory ?? string.Empty,
            $".capture_f3_{Guid.NewGuid():N}.tmp");
        try
        {
            var options = new JsonSerializerOptions { WriteIndented = true };
            File.WriteAllText(temporaryPath, data.ToJsonString(options) + "\n",
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(temporaryPath, path, overwrite: true);
        }
        catch
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
            throw;
        }

        return path;
    }

    /// <summary>
    /// The full "build an account JSON from a capture" flow the client's
    /// "Build from capture" button runs.
    /// </summary>
    public static string ExtractToJson(string pcapPath, string jsonPath, Action<string>? log = null)
    {
        var tshark = LiveCapture.FindTshark();
        log?.Invoke("[capture] decoding the game's login frames...");
        var result = ExtractLogin(tshark, pcapPath, log);
        if (result is null)
        {
            throw new InvalidDataException(
                "no complete GameServer login was found. Make sure the game " +
                "reached the world before the capture ended, then retry.");
        }

        var outputPath = WriteJsonAtomic(jsonPath, result);
        log?.Invoke("[capture] login found; session values were not printed.");
        log?.Invoke($"[capture] account/player/device fields present: " +
                   $"{Truthy(result["account"])}/{Truthy(result["player_id"])}/{Truthy(result["device_id"])}");
        log?.Invoke($"[capture] LoginServer request captured: {Truthy(result["ls_request"])}");
        log?.Invoke($"[capture] saved: {outputPath}");
        log?.Invoke("Treat this JSON as a password; it contains live login credentials.");
        return outputPath;
    }

    private static bool Truthy(JsonNode? node)
    {
        if (node is null)
            return false;
        if (node is JsonValue value)
        {
            if (value.TryGetValue<bool>(out var boolean))
                return boolean;
            if (value.TryGetValue<ulong>(out var number))
                return number != 0;
            if (value.TryGetValue<string>(out var text))
                return text.Length > 0;
        }

        return true;
    }
}
