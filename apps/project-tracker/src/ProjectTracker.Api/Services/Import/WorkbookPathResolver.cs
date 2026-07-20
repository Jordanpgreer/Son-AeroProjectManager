namespace ProjectTracker.Api.Services.Import;

public static class WorkbookPathResolver
{
    public static string Resolve(string? requestedPath, IConfiguration configuration, IWebHostEnvironment environment)
    {
        var path = string.IsNullOrWhiteSpace(requestedPath) ? configuration["Import:DefaultWorkbookPath"] : requestedPath;
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidOperationException("No workbook path was provided.");
        }

        return Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(environment.ContentRootPath, path));
    }
}
