namespace ProjectTracker.Api.Services.Reports;

public sealed record ReportFile(byte[] Content, string ContentType, string FileName);

