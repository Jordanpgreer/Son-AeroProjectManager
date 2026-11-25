namespace EstimatingDashboard.Api.Dtos;

public sealed record EstimatingQuoteDashboardDto(IReadOnlyList<EstimatingPersonalQuoteDto> Records,
    int Total, int Page, int PageSize, EstimatingQuoteDashboardStatsDto Stats,
    IReadOnlyList<EstimatingPerformanceEstimatorDto> Estimators);

public sealed record EstimatingQuotePerformanceDto(EstimatingQuoteDashboardStatsDto Stats,
    IReadOnlyList<EstimatingPerformanceEstimatorDto> Estimators);

public sealed record EstimatingPerformanceEstimatorDto(string Id, string Name);

public sealed record EstimatingQuoteDashboardStatsDto(decimal? AverageCompletionWorkdays,
    int CompletionSampleSize, int WonCount, int LostCount, int CompletedCount,
    decimal WonTotalValue, decimal LostTotalValue, int ActiveCount, int OverdueCount,
    int OnTimeCount, int OnTimeSampleSize, decimal? OnTimePercentage, int OnTimeMissingCount);
