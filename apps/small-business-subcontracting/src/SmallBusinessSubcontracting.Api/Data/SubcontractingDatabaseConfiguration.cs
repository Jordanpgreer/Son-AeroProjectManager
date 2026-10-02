using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace SmallBusinessSubcontracting.Api;

public static class SubcontractingDatabaseConfiguration
{
    public static void Validate(IConfiguration configuration, IHostEnvironment environment)
    {
        var mode = configuration["Authentication:Mode"] ?? (environment.IsDevelopment() ? "Development" : "Windows");
        if (!string.Equals(mode, "Windows", StringComparison.OrdinalIgnoreCase)
            && !(environment.IsDevelopment() && string.Equals(mode, "Development", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Windows authentication is required outside Development.");

        ValidateProvider(configuration["Database:Provider"], environment);
        ValidateProvider(configuration["SubcontractingDatabase:Provider"], environment);
        if (environment.IsDevelopment()) return;

        var own = SqlConnection(configuration, "SubcontractingStore");
        var roles = SqlConnection(configuration, "RoleStore");
        if (string.Equals(own.InitialCatalog, roles.InitialCatalog, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("SubcontractingStore must use its own database, separate from RoleStore.");
    }

    public static void Configure(DbContextOptionsBuilder options, string? provider, string connection)
    {
        if (string.Equals(provider, "Sqlite", StringComparison.OrdinalIgnoreCase)) options.UseSqlite(connection);
        else if (provider is null || string.Equals(provider, "SqlServer", StringComparison.OrdinalIgnoreCase))
            options.UseSqlServer(connection);
        else throw new InvalidOperationException("The database provider must be SqlServer or Sqlite.");
    }

    private static void ValidateProvider(string? provider, IHostEnvironment environment)
    {
        if (provider is null || string.Equals(provider, "SqlServer", StringComparison.OrdinalIgnoreCase)) return;
        if (environment.IsDevelopment() && string.Equals(provider, "Sqlite", StringComparison.OrdinalIgnoreCase)) return;
        throw new InvalidOperationException("SQL Server is required outside Development; an unknown database provider is not allowed.");
    }

    private static SqlConnectionStringBuilder SqlConnection(IConfiguration configuration, string name)
    {
        var value = configuration.GetConnectionString(name);
        if (string.IsNullOrWhiteSpace(value)) throw new InvalidOperationException($"ConnectionStrings:{name} is required.");
        var connection = new SqlConnectionStringBuilder(value);
        if (string.IsNullOrWhiteSpace(connection.DataSource) || string.IsNullOrWhiteSpace(connection.InitialCatalog))
            throw new InvalidOperationException($"ConnectionStrings:{name} must specify a server and database.");
        return connection;
    }
}
