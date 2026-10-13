namespace EstimatingDashboard.Api.Dtos;

public sealed record MaterialQuantitySelectionDto(string ItemId, decimal Minimum, decimal? Maximum = null);
public sealed record SaveMaterialQuantitiesDto(int ExpectedVersion, IReadOnlyList<MaterialQuantitySelectionDto> Quantities);
