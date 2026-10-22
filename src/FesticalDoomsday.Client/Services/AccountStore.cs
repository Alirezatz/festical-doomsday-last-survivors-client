using System.Text.Json;
using System.Text.Json.Nodes;
using FesticalDoomsday.Client.Models;

namespace FesticalDoomsday.Client.Services;

/// <summary>
/// Reads and writes the account files: validates a capture-derived JSON, keeps
/// the remembered account list, and hands out a unique GameServer device id for
/// every newly imported account.
/// </summary>
public sealed class AccountStore
{
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    private readonly string _registryPath;

    public AccountStore(string registryPath)
    {
        _registryPath = registryPath;
        var directory = Path.GetDirectoryName(registryPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
    }

    /// <summary>Default location of the remembered account list.</summary>
    public static string DefaultRegistryPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DlsResourceFarm", "accounts.json");

    /// <summary>Validates that the file really is a game login capture.</summary>
    public static void Validate(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("Account JSON file was not found.", path);
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        if (!root.TryGetProperty("ls_request", out var request) ||
            request.ValueKind != JsonValueKind.Object ||
            !HasIdentifier(request, "account") ||
            !HasText(request, "web_session") ||
            !HasText(request, "udid") ||
            !HasText(request, "login_server") ||
            !HasText(root, "device_id") ||
            !HasText(root, "version"))
        {
            throw new InvalidDataException("This JSON does not contain the required game login capture.");
        }
    }

    private static bool HasText(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.String &&
        !string.IsNullOrWhiteSpace(value.GetString());

    private static bool HasIdentifier(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) &&
        ((value.ValueKind == JsonValueKind.Number &&
          value.TryGetInt64(out var number) && number > 0) ||
         (value.ValueKind == JsonValueKind.String &&
          !string.IsNullOrWhiteSpace(value.GetString())));

    /// <summary>Reads the remembered account files.</summary>
    public IReadOnlyList<string> Load()
    {
        if (!File.Exists(_registryPath))
            return [];
        try
        {
            var registry = JsonSerializer.Deserialize<AccountRegistry>(File.ReadAllText(_registryPath));
            return registry?.CaptureFiles ?? [];
        }
        catch (Exception)
        {
            return [];
        }
    }

    /// <summary>Stores the account files the window currently shows.</summary>
    public void Save(IEnumerable<AccountSession> accounts)
    {
        var directory = Path.GetDirectoryName(_registryPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        File.WriteAllText(_registryPath, JsonSerializer.Serialize(
            new AccountRegistry { CaptureFiles = accounts.Select(account => account.CapturePath).ToList() },
            WriteOptions));
    }

    /// <summary>Device ids that are already taken by the listed accounts.</summary>
    public static HashSet<string> CollectAssignedDeviceIds(IEnumerable<AccountSession> accounts)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var account in accounts)
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(account.CapturePath));
                if (document.RootElement.ValueKind == JsonValueKind.Object &&
                    document.RootElement.TryGetProperty("device_id", out var deviceId) &&
                    deviceId.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(deviceId.GetString()))
                {
                    ids.Add(deviceId.GetString()!);
                }
            }
            catch (Exception)
            {
                // A broken file simply does not reserve an id.
            }
        }

        return ids;
    }

    /// <summary>
    /// Give the account a unique random 40-character hexadecimal GameServer
    /// device id. Only the root device_id changes; the login fields stay as they
    /// were captured.
    /// </summary>
    public static string AssignRandomDeviceId(string path, HashSet<string> assignedIds)
    {
        var root = JsonNode.Parse(File.ReadAllText(path)) as JsonObject
                   ?? throw new InvalidDataException("Account JSON root must be an object.");
        string deviceId;
        do
        {
            deviceId = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(20))
                .ToLowerInvariant();
        } while (assignedIds.Contains(deviceId));

        root["device_id"] = deviceId;
        var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporaryPath,
                root.ToJsonString(WriteOptions),
                new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(temporaryPath, path, overwrite: true);
            assignedIds.Add(deviceId);
            return deviceId;
        }
        catch
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
            throw;
        }
    }
}
