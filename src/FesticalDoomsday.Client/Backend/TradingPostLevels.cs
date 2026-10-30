namespace FesticalDoomsday.Client.Backend;

/// <summary>One row of the game's <c>CityTradingpostLv</c> config.</summary>
public sealed class TradingPostLevel
{
    public uint Level { get; init; }

    /// <summary>Daily Supply Limit: how much this account may send in one day.</summary>
    public ulong DeliveryLoad { get; init; }

    /// <summary>Daily Receiving Limit: how much this account may receive in one day.</summary>
    public ulong ReceivingLoad { get; init; }

    /// <summary>Tax rate in per-mille: 80 is the 8% of level 25.</summary>
    public int TradingTax { get; init; }

    /// <summary>Transport Speed Up in per-mille: 1000 is the 100% of level 25.</summary>
    public int TransportationSpeed { get; init; }

    /// <summary>Transportation Capacity: what one single transport may carry.</summary>
    public ulong TradingLoad { get; init; }

    /// <summary>The tax shown in the game, as a percentage.</summary>
    public double TaxPercent => TradingTax / 10.0;

    /// <summary>The speed up shown in the game, as a percentage.</summary>
    public double SpeedPercent => TransportationSpeed / 10.0;
}

/// <summary>
/// The Trading Post levels exactly as the game ships them in
/// <c>CityTradingpostLvConfig</c>: the level 25 row is the one the game shows as
/// 5,000,000 Transportation Capacity, 8% Tax Rate, 200,000,000 Daily Receiving
/// Limit, 100,000,000 Daily Supply Limit and 100% Transport Speed Up.
/// </summary>
public static class TradingPostLevels
{
    private static readonly TradingPostLevel[] Rows =
    [
        Row(1, 2_500_000, 5_000_000, 300, 0, 20_000),
        Row(2, 3_750_000, 7_500_000, 290, 0, 50_000),
        Row(3, 5_000_000, 10_000_000, 290, 0, 100_000),
        Row(4, 6_250_000, 12_500_000, 280, 0, 150_000),
        Row(5, 7_500_000, 15_000_000, 280, 0, 200_000),
        Row(6, 9_000_000, 18_000_000, 270, 0, 300_000),
        Row(7, 10_500_000, 21_000_000, 270, 0, 400_000),
        Row(8, 12_000_000, 24_000_000, 260, 0, 500_000),
        Row(9, 13_500_000, 27_000_000, 260, 0, 600_000),
        Row(10, 15_000_000, 30_000_000, 250, 0, 700_000),
        Row(11, 17_500_000, 35_000_000, 250, 0, 850_000),
        Row(12, 20_000_000, 40_000_000, 240, 0, 1_000_000),
        Row(13, 22_500_000, 45_000_000, 230, 0, 1_200_000),
        Row(14, 25_000_000, 50_000_000, 220, 0, 1_400_000),
        Row(15, 27_500_000, 55_000_000, 210, 0, 1_600_000),
        Row(16, 30_000_000, 60_000_000, 200, 0, 1_800_000),
        Row(17, 35_000_000, 70_000_000, 190, 0, 2_000_000),
        Row(18, 40_000_000, 80_000_000, 180, 0, 2_200_000),
        Row(19, 45_000_000, 90_000_000, 170, 0, 2_400_000),
        Row(20, 50_000_000, 100_000_000, 160, 0, 2_600_000),
        Row(21, 55_000_000, 110_000_000, 150, 0, 2_800_000),
        Row(22, 60_000_000, 120_000_000, 140, 0, 3_000_000),
        Row(23, 65_000_000, 130_000_000, 120, 0, 3_500_000),
        Row(24, 70_000_000, 140_000_000, 100, 0, 4_000_000),
        Row(25, 100_000_000, 200_000_000, 80, 1_000, 5_000_000)
    ];

    private static readonly Dictionary<uint, TradingPostLevel> ByLevel =
        Rows.ToDictionary(row => row.Level);

    private static TradingPostLevel Row(uint level, ulong deliveryLoad, ulong receivingLoad,
        int tax, int speed, ulong tradingLoad) => new()
    {
        Level = level,
        DeliveryLoad = deliveryLoad,
        ReceivingLoad = receivingLoad,
        TradingTax = tax,
        TransportationSpeed = speed,
        TradingLoad = tradingLoad
    };

    public static IReadOnlyList<TradingPostLevel> All => Rows;

    /// <summary>The row of one level, or null when the server reported a level the game does not have.</summary>
    public static TradingPostLevel? Find(uint level) =>
        ByLevel.GetValueOrDefault(level);
}
