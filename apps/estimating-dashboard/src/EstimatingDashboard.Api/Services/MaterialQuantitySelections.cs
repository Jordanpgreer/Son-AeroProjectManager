using System.Text.Json;
using EstimatingDashboard.Api.Dtos;

namespace EstimatingDashboard.Api.Services;

internal static class MaterialQuantitySelections
{
    public const decimal MaxQuantity = 1_000_000_000m;

    public static IReadOnlyList<MaterialQuantitySelectionDto> Read(string? json) =>
        JsonSerializer.Deserialize<MaterialQuantitySelectionDto[]>(json ?? "[]") ?? [];

    public static IReadOnlyDictionary<string, IReadOnlyList<decimal>> ToOverrides(
        IEnumerable<MaterialQuantitySelectionDto> selections) =>
        selections.ToDictionary(selection => selection.ItemId, selection => selection.GetTiers(), StringComparer.Ordinal);
}
