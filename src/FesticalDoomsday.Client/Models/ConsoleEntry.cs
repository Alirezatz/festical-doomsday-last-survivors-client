namespace FesticalDoomsday.Client.Models;

/// <summary>
/// One line of the shared console. The time, the level and the message are
/// separate so the console can align them in columns and colour the level.
/// </summary>
public sealed class ConsoleEntry
{
    public ConsoleEntry(DateTime timestamp, string level, string message)
    {
        Time = timestamp.ToString("HH:mm:ss");
        Level = level;
        Message = message;
    }

    /// <summary>The time of the line, as HH:mm:ss.</summary>
    public string Time { get; }

    /// <summary>The level of the line: info, success, warn or error.</summary>
    public string Level { get; }

    /// <summary>The text of the line.</summary>
    public string Message { get; }

    /// <summary>True when the line starts with a right to left character.</summary>
    public bool IsRtl => StartsRightToLeft(Message);

    public bool IsInfo => Level == "info";

    public bool IsSuccess => Level == "success";

    public bool IsWarn => Level == "warn";

    public bool IsError => Level == "error";

    /// <summary>
    /// Finds the first character that has a direction and reports whether it is
    /// a right to left one. A log line that starts with a latin word but
    /// continues in Persian still reads from the left, so only the first strong
    /// character decides.
    /// </summary>
    private static bool StartsRightToLeft(string text)
    {
        foreach (var character in text)
        {
            if (IsRightToLeft(character))
            {
                return true;
            }

            if (char.IsLetter(character))
            {
                return false;
            }
        }

        return false;
    }

    /// <summary>True for the Hebrew, Arabic, Syriac, Thaana and N'Ko blocks.</summary>
    private static bool IsRightToLeft(char character)
    {
        var code = (int)character;
        return code >= 0x0590 && code <= 0x08FF
               || code >= 0xFB1D && code <= 0xFDFF
               || code >= 0xFE70 && code <= 0xFEFF;
    }
}
