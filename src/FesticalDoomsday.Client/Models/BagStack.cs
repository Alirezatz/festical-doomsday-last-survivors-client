using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using FesticalDoomsday.Client.Services;

namespace FesticalDoomsday.Client.Models;

/// <summary>
/// One usable resource stack of the real in-game bag, for example
/// "1,000 Food × 34". The bag form shows a tile per stack and a Use button that
/// sends the game's own item-use request, so the resources move into the city.
/// </summary>
public sealed class BagStack : INotifyPropertyChanged
{
    private readonly ItemDefinition _definition;
    private ulong _count;

    public BagStack(ItemDefinition definition, ulong count)
    {
        _definition = definition;
        _count = count;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public uint ItemId => _definition.Id;

    /// <summary>The game's own name of the stack, for example "1,000 Food".</summary>
    public string Name => _definition.Name;

    /// <summary>food, wood, steel or oil.</summary>
    public string ResourceKey => _definition.Resource ?? string.Empty;

    /// <summary>How much of the resource one copy of the stack is worth.</summary>
    public ulong AmountPerUnit => _definition.Amount;

    /// <summary>The localized name of the resource family, for the tile label.</summary>
    public string ResourceLabel => Localizer.Instance[ResourceKey switch
    {
        "food" => "FoodLabel",
        "wood" => "WoodLabel",
        "steel" => "SteelLabel",
        "oil" => "OilLabel",
        _ => "BagOtherGroup"
    }];

    public IImage? Icon => BagResourceIcons.For(ResourceKey);

    public ulong Count
    {
        get => _count;
        set
        {
            if (_count == value)
                return;
            _count = value;
            Raise(nameof(Count));
            Raise(nameof(CountText));
            Raise(nameof(TotalText));
            Raise(nameof(CanUseAll));
        }
    }

    /// <summary>"× 34".</summary>
    public string CountText => "× " + _count.ToString("N0", CultureInfo.InvariantCulture);

    /// <summary>What the whole stack is worth, for example "34,000".</summary>
    public string TotalText => (_definition.Amount * _count).ToString("N0", CultureInfo.InvariantCulture);

    public bool CanUseAll => _count > 1;

    /// <summary>Refreshes the label after the window language changed.</summary>
    public void RefreshLocalizedText() => Raise(nameof(ResourceLabel));

    private void Raise([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

/// <summary>
/// The four bag resource icons, loaded once and shared by every tile of the
/// bag form. They are the same assets the account card uses, only larger.
/// </summary>
internal static class BagResourceIcons
{
    private static readonly Dictionary<string, IImage?> Cache = new();

    internal static IImage? For(string resource)
    {
        if (Cache.TryGetValue(resource, out var cached))
            return cached;

        IImage? image = null;
        var file = resource switch
        {
            "food" => "Food.png",
            "wood" => "Wood.png",
            "steel" => "Steel.png",
            "oil" => "Oil.png",
            _ => null
        };
        if (file is not null)
        {
            try
            {
                using var stream = AssetLoader.Open(
                    new Uri($"avares://FesticalDoomsdayClient/Assets/{file}"));
                image = new Bitmap(stream);
            }
            catch (Exception)
            {
                image = null;
            }
        }

        Cache[resource] = image;
        return image;
    }
}
