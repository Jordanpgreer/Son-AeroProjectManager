using System.Text.RegularExpressions;

namespace QualityAssurance.Api.Services;

public static partial class QualityShipmentNumber
{
    public static string Normalize(string value)
    {
        var trimmed = value.Trim();
        var match = ShipmentNumberPattern().Match(trimmed);
        return match.Success ? $"SHP-SO{match.Groups["order"].Value}-{match.Groups["shipment"].Value}" : trimmed;
    }

    public static bool HasShipmentSuffix(string value)
    {
        var number = value.Trim();
        if (number.StartsWith("SHP-", StringComparison.OrdinalIgnoreCase)) number = number[4..];
        var separator = number.LastIndexOf('-');
        return separator > 0 && separator < number.Length - 1;
    }

    [GeneratedRegex(@"^(?:SHP-)?(?:SO)?(?<order>\d+)-(?<shipment>\d+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ShipmentNumberPattern();
}
