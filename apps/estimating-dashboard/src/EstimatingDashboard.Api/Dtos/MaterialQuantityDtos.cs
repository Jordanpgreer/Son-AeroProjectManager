using System.Text.Json.Serialization;

namespace EstimatingDashboard.Api.Dtos;

public sealed record MaterialQuantitySelectionDto(string ItemId, decimal Minimum, decimal? Maximum = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<decimal>? Tiers = null)
{
    internal IReadOnlyList<decimal> GetTiers() =>
        Tiers ?? (Maximum is { } maximum && maximum != Minimum ? [Minimum, maximum] : [Minimum]);
}

public sealed record SaveMaterialQuantitiesDto(int ExpectedVersion, IReadOnlyList<MaterialQuantitySelectionDto> Quantities);
