namespace EstimatingDashboard.Api.Services;

public sealed record EstimatingControlledRateAssumptions(
    decimal Burden,
    decimal LaborGa,
    decimal MaterialGa,
    decimal ProcessGa,
    decimal LaborProfit,
    decimal MaterialProfit,
    decimal ProcessProfit);

public static class EstimatingControlledRates
{
    private static readonly int[] Years = [2023, 2024, 2025, 2026, 2027, 2028, 2029];

    private static readonly decimal[] Program = [2.08m, 2.08m, 2.08m, 2.08m, 2.08m, 2.08m, 2.08m];
    private static readonly decimal[] Fixtures = [2.5m, 2.5m, 2.5m, 2.5m, 2.5m, 2.5m, 2.5m];
    private static readonly decimal[] Metals =
    [
        0.45866666666666667m,
        0.4545m,
        0.4681666666666667m,
        0.48683333333333334m,
        0.5063333333333333m,
        0.5316666666666666m,
        0.5583333333333333m
    ];
    private static readonly decimal[] Rubber =
    [
        0.35883333333333334m,
        0.374m,
        0.38516666666666666m,
        0.4005m,
        0.4165m,
        0.4373333333333333m,
        0.4593333333333333m
    ];
    private static readonly decimal[] PlasticInjection =
    [
        0.4618333333333334m,
        0.3m,
        0.309m,
        0.32133333333333336m,
        0.33416666666666667m,
        0.35083333333333333m,
        0.3685m
    ];
    private static readonly decimal[] PlasticCompression =
    [
        0.30050000000000004m,
        0.39566666666666667m,
        0.4075m,
        0.42383333333333334m,
        0.4408333333333333m,
        0.4628333333333333m,
        0.486m
    ];
    private static readonly decimal[] Assembly =
    [
        0.3236666666666667m,
        0.37116666666666664m,
        0.38233333333333336m,
        0.3975m,
        0.4135m,
        0.4341666666666667m,
        0.45583333333333337m
    ];
    private static readonly decimal[] Quality =
    [
        0m,
        0.612m,
        0.6303333333333333m,
        0.6555m,
        0.6818333333333333m,
        0.7158333333333334m,
        0.7516666666666667m
    ];
    private static readonly decimal[] IdAndPack =
    [
        0m,
        0.35433333333333333m,
        0.36483333333333334m,
        0.3795m,
        0.39466666666666667m,
        0.41450000000000004m,
        0.43516666666666665m
    ];
    private static readonly decimal[] Zero = [0m, 0m, 0m, 0m, 0m, 0m, 0m];
    private static readonly decimal[] Purchase = [1m, 1m, 1m, 1m, 1m, 1m, 1m];
    private static readonly decimal[] ToolingInHouse =
    [
        0.7140000000000001m,
        0.7038333333333333m,
        0.725m,
        0.754m,
        0.7841666666666666m,
        0.8233333333333334m,
        0.8644999999999999m
    ];

    public static bool TryGetRate(string key, int year, out decimal value)
    {
        value = default;
        var yearIndex = Array.IndexOf(Years, year);
        if (yearIndex < 0) return false;
        var series = Series(key);
        if (series is null) return false;
        value = series[yearIndex];
        return true;
    }

    public static bool TryGetAssumptions(int year, out EstimatingControlledRateAssumptions assumptions)
    {
        assumptions = year switch
        {
            2023 => new(5.75m, 0.21m, 0.21m, 0.21m, 0.2m, 0.2m, 0.2m),
            >= 2024 and <= 2029 => new(4.15m, 0.2m, 0.2m, 0.2m, 0.2m, 0.2m, 0.2m),
            _ => null!
        };
        return assumptions is not null;
    }

    private static decimal[]? Series(string key) => key switch
    {
        "manufacturing:5" => Program,
        "manufacturing:6" => Fixtures,
        "manufacturing:7" or "manufacturing:8" or "manufacturing:15" or
            "manufacturing:16" or "manufacturing:17" => Metals,
        "manufacturing:9" or "rubber-breakdown:18" or "rubber-breakdown:19" or
            "rubber-breakdown:20" or "rubber-breakdown:21" or "rubber-breakdown:22" or
            "rubber-breakdown:23" or "rubber-breakdown:24" or "rubber-breakdown:25" or
            "rubber-breakdown:28" or "rubber-breakdown:29" or "rubber-breakdown:31" or
            "rubber-breakdown:32" or "rubber-breakdown:33" or "rubber-breakdown:34" or
            "rubber-breakdown:35" or "rubber-breakdown:36" or "rubber-breakdown:38" or
            "rubber-breakdown:39" or "rubber-breakdown:40" or "rubber-breakdown:41" or
            "rubber-breakdown:44" => Rubber,
        "manufacturing:10" => PlasticInjection,
        "manufacturing:11" => PlasticCompression,
        "manufacturing:12" => Assembly,
        "manufacturing:13" or "rubber-breakdown:37" => Quality,
        "manufacturing:14" => IdAndPack,
        "rubber-breakdown:26" or "rubber-breakdown:27" or "rubber-breakdown:30" => Zero,
        "rubber-breakdown:42" or "rubber-breakdown:43" => Purchase,
        "rubber-breakdown:45" => ToolingInHouse,
        _ => null
    };
}
