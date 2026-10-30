using System.Text.Json;
using Avalonia.Platform;

namespace FesticalDoomsday.Client.Services;

/// <summary>What the client knows about one item of the game.</summary>
public sealed class ItemDefinition
{
    public uint Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public uint Type { get; init; }

    /// <summary>The bag resource this stack pays into, or null for other items.</summary>
    public string? Resource { get; init; }

    /// <summary>How much of the resource one copy of this stack is worth.</summary>
    public ulong Amount { get; init; }

    public bool Usable => Resource is not null && Amount > 0;
}

/// <summary>
/// The name and the resource value of every bag item, cut from the game's own
/// <c>gameconfig_item</c> and English string tables and embedded in the client.
/// The bag form reads its labels from here; a stack the catalogue does not know
/// simply keeps its numeric id.
/// </summary>
public static class ItemCatalog
{
    private static readonly Dictionary<uint, ItemDefinition> Items = Load();

    public static ItemDefinition? Find(uint id) => Items.GetValueOrDefault(id);

    private static Dictionary<uint, ItemDefinition> Load()
    {
        var result = new Dictionary<uint, ItemDefinition>();
        try
        {
            using var stream = AssetLoader.Open(
                new Uri("avares://FesticalDoomsdayClient/Assets/item_catalog.json"));
            using var document = JsonDocument.Parse(stream);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                return result;

            foreach (var entry in document.RootElement.EnumerateArray())
            {
                if (!entry.TryGetProperty("id", out var idValue) || !idValue.TryGetUInt32(out var id) ||
                    id == 0)
                {
                    continue;
                }

                var resource = entry.TryGetProperty("res", out var resValue) &&
                               resValue.ValueKind == JsonValueKind.String
                    ? resValue.GetString()
                    : null;
                var amount = entry.TryGetProperty("amt", out var amountValue) &&
                             amountValue.TryGetUInt64(out var parsed)
                    ? parsed
                    : 0UL;

                result[id] = new ItemDefinition
                {
                    Id = id,
                    Name = entry.TryGetProperty("name", out var nameValue) &&
                           nameValue.ValueKind == JsonValueKind.String
                        ? nameValue.GetString() ?? string.Empty
                        : string.Empty,
                    Type = entry.TryGetProperty("type", out var typeValue) &&
                           typeValue.TryGetUInt32(out var type)
                        ? type
                        : 0,
                    Resource = resource,
                    Amount = amount
                };
            }
        }
        catch (Exception)
        {
            // A missing or broken catalogue must never take the window down;
            // the bag then falls back to the item ids.
        }

        return result;
    }
}
