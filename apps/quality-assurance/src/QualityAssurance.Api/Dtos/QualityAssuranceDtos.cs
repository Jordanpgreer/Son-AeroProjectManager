namespace QualityAssurance.Api.Dtos;

public sealed record MeDto(
    string AccountName,
    string DisplayName,
    string ModuleKey,
    string Role,
    IReadOnlyList<string> Permissions,
    IReadOnlyList<string> Groups);

public sealed record ErrorDto(string Code, string Message);
