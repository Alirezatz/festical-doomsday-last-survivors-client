using System.Text.Json.Nodes;

namespace FesticalDoomsday.Client.Services;

/// <summary>How much detail the shared console shows.</summary>
public enum LogVerbosity
{
    /// <summary>No logging at all: the logging work itself is skipped.</summary>
    Off,

    /// <summary>Events only: connections, matches, alliance actions and errors.</summary>
    Normal,

    /// <summary>Everything, including every decoded game message from the server.</summary>
    Debug
}

/// <summary>
/// The one log switch of the process. The window and the shared backend read
/// the same value, so a change applies to every account immediately.
///
/// The level is checked <em>before</em> a message is formatted or a JSON event
/// is built, which is what keeps <see cref="LogVerbosity.Off"/> free: an
/// account that only produces logs costs nothing on that account's thread.
/// </summary>
public static class AppLog
{
    private static readonly object SettingsLock = new();

    private static string? _settingsPath;
    private static int _level = (int)LogVerbosity.Normal;

    /// <summary>The current logging detail, safe to read from any thread.</summary>
    public static LogVerbosity Level
    {
        get => (LogVerbosity)Volatile.Read(ref _level);
        set => Volatile.Write(ref _level, (int)value);
    }

    public static bool IsOff => Level == LogVerbosity.Off;

    public static bool IsDebug => Level == LogVerbosity.Debug;

    /// <summary>
    /// The cheap gate every log call goes through first. Debug lines are only
    /// allowed while the level is <see cref="LogVerbosity.Debug"/>.
    /// </summary>
    public static bool Allows(string level) => Level switch
    {
        LogVerbosity.Off => false,
        LogVerbosity.Debug => true,
        _ => level != "debug"
    };

    /// <summary>Reads the remembered level next to the account list.</summary>
    public static void Load(string accountsRegistryPath)
    {
        var directory = Path.GetDirectoryName(accountsRegistryPath);
        lock (SettingsLock)
        {
            if (string.IsNullOrEmpty(directory))
            {
                _settingsPath = null;
                return;
            }

            _settingsPath = Path.Combine(directory, "settings.json");
            try
            {
                if (!File.Exists(_settingsPath))
                    return;
                if (JsonNode.Parse(File.ReadAllText(_settingsPath)) is not JsonObject settings)
                    return;
                if (settings["log_level"] is JsonValue value &&
                    value.TryGetValue<string>(out var text) &&
                    Enum.TryParse<LogVerbosity>(text, ignoreCase: true, out var level))
                {
                    Level = level;
                }
            }
            catch (Exception)
            {
                // A broken settings file simply falls back to the default level.
            }
        }
    }

    /// <summary>Stores the picked level so the next run starts with it.</summary>
    public static void Save()
    {
        lock (SettingsLock)
        {
            if (_settingsPath is null)
                return;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
                var settings = new JsonObject
                {
                    ["log_level"] = Level.ToString().ToLowerInvariant()
                };
                File.WriteAllText(_settingsPath, settings.ToJsonString());
            }
            catch (Exception)
            {
                // Logging must never be able to take the window down.
            }
        }
    }
}
