namespace FesticalDoomsday.Client.Models;

/// <summary>Outcome of a leave/join alliance request as reported by the game server.</summary>
public sealed record GuildActionResult(
    bool Success, string? Error, ulong? GuildId, string? GuildName);

/// <summary>The list of account files the client remembers between runs.</summary>
public sealed class AccountRegistry
{
    public List<string> CaptureFiles { get; set; } = [];
}
