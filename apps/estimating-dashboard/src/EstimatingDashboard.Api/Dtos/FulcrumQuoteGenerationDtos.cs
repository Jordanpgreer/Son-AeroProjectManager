namespace EstimatingDashboard.Api.Dtos;

public sealed record FulcrumQuoteGenerationDto(int QuoteHistoryId, int QuoteNumber, string Customer,
    DateTimeOffset GeneratedAt, IReadOnlyList<FulcrumGeneratedQuoteLineDto> Items, IReadOnlyList<string> Warnings);
public sealed record FulcrumGeneratedQuoteLineDto(string LineItemId, decimal Quantity,
    IReadOnlyList<decimal> Quantities, FulcrumGeneratedAssemblyDto Item);
public sealed record FulcrumGeneratedAssemblyDto(string ItemId, string PartNumber, string Revision,
    string Description, decimal AvailableStock, string Notes, decimal QuantityPerParent,
    IReadOnlyList<FulcrumGeneratedOperationDto> Operations, IReadOnlyList<FulcrumGeneratedMaterialDto> Materials,
    IReadOnlyList<FulcrumGeneratedProcessDto> Processes, IReadOnlyList<FulcrumGeneratedAssemblyDto> Subassemblies);
public sealed record FulcrumGeneratedOperationDto(string Id, int Order, string SourceOperation,
    string TargetOperation, string? RateReferenceKey, decimal SetupMinutes, decimal RunMinutes,
    decimal MachineMinutes, string Instructions);
public sealed record FulcrumGeneratedMaterialDto(string Id, string PartNumber, string Description,
    decimal QuantityPerParent, string UnitOfMeasure, decimal? UnitCost, string Notes);
public sealed record FulcrumGeneratedProcessDto(string Id, string Description, decimal? UnitCost,
    decimal? LotCost, string Notes);
