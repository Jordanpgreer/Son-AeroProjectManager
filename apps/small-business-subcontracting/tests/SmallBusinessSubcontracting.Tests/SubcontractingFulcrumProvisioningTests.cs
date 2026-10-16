using System.Reflection;
using System.Text.RegularExpressions;
using SonAero.Platform.Integrations;

namespace SmallBusinessSubcontracting.Tests;

public sealed class SubcontractingFulcrumProvisioningTests
{
    [Theory]
    [InlineData("Initialize-SmallBusinessSubcontractingDatabase.sql")]
    [InlineData("Upgrade-SubcontractingFulcrumSync.sql")]
    public void Dba_snapshot_table_contract_matches_the_runtime_store(string file)
    {
        var runtime = (string)typeof(FulcrumSnapshotStore)
            .GetField("SqlServerSchema", BindingFlags.NonPublic | BindingFlags.Static)!
            .GetRawConstantValue()!;
        Assert.Equal(TableDefinition(runtime), TableDefinition(Read(file)));
        Assert.Contains("Arda.FulcrumReadSnapshots.Schema", Read(file));
        Assert.Contains("IX_FulcrumReadSnapshots_Expiry ON dbo.FulcrumReadSnapshots (ExpiresTicks)", Read(file));
    }

    [Fact]
    public void Installed_module_upgrade_grants_only_cache_crud_and_provider_setting_read()
    {
        var sql = Read("Upgrade-SubcontractingFulcrumSync.sql");
        var grants = Regex.Matches(sql, @"GRANT\s+(.+?)\s+ON OBJECT::(\S+)\s+TO\s+\[\$\(AppServiceAccount\)\];")
            .Select(match => (Permissions: match.Groups[1].Value, Object: match.Groups[2].Value)).ToArray();
        Assert.Equal(2, grants.Length);
        Assert.Contains(("SELECT", "dbo.EnterpriseIntegrationSettings"), grants);
        Assert.Contains(("SELECT, INSERT, UPDATE, DELETE", "dbo.FulcrumReadSnapshots"), grants);
        var statements = Regex.Replace(sql, @"(?m)--.*$", "");
        Assert.DoesNotMatch(@"(?i)\b(CREATE|ALTER|DROP)\s+(DATABASE|LOGIN|USER|ROLE)\b", statements);
        Assert.DoesNotMatch(@"(?im)^\s*(UPDATE|DELETE|INSERT|TRUNCATE|DROP)\s", statements);
        Assert.Contains(":setvar AppServiceAccount \"SON4L\\SON-IIS2$\"", sql);
        Assert.Contains("EXECUTE AS USER = N'$(AppServiceAccount)'", sql);
        Assert.Contains("HAS_PERMS_BY_NAME", sql);
    }

    [Fact]
    public void Upgrade_is_guarded_for_existing_installation_and_rolls_back_failed_verification()
    {
        var sql = Read("Upgrade-SubcontractingFulcrumSync.sql");
        Assert.Contains(":on error exit", sql);
        Assert.Contains("SERVERPROPERTY(N'MachineName')", sql);
        Assert.Contains("DB_ID(N'SmallBusinessSubcontracting') IS NULL", sql);
        Assert.Contains("sys.server_principals WHERE name = N'$(AppServiceAccount)'", sql);
        Assert.Contains("default_schema_name", sql);
        Assert.Contains("IF OBJECT_ID(N'dbo.FulcrumReadSnapshots', N'U') IS NULL", sql);
        Assert.Contains("TYPE_NAME(actual.user_type_id)", sql);
        Assert.Contains("IF @impersonating = 1 REVERT;", sql);
        Assert.Contains("IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;", sql);
        Assert.True(sql.IndexOf("COMMIT TRANSACTION;", StringComparison.Ordinal)
            < sql.IndexOf("PRINT 'SUBCONTRACTING_FULCRUM_SYNC_DATABASE_READY'", StringComparison.Ordinal));
    }

    private static string TableDefinition(string sql)
    {
        var match = Regex.Match(sql, @"CREATE TABLE (?:dbo\.)?FulcrumReadSnapshots\s*\((.*?)\r?\n\s*\);", RegexOptions.Singleline);
        Assert.True(match.Success, "Snapshot table definition is missing.");
        return Regex.Replace(match.Groups[1].Value, @"\s+", " ").Trim();
    }

    private static string Read(string file)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !Directory.Exists(Path.Combine(root.FullName, "deployment"))) root = root.Parent;
        Assert.NotNull(root);
        return File.ReadAllText(Path.Combine(root.FullName, "deployment", file));
    }
}
