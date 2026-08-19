using System.Reflection;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using ProjectTracker.Api.Data.Migrations;
using ProjectTracker.Api.Endpoints;

namespace ProjectTracker.Tests;

public sealed class OperationScheduleReleaseSafetyTests
{
    [Theory]
    [InlineData("application/json", true)]
    [InlineData("application/problem+json; charset=utf-8", true)]
    [InlineData("application/x-www-form-urlencoded", false)]
    [InlineData("multipart/form-data; boundary=test", false)]
    [InlineData("text/plain", false)]
    [InlineData(null, false)]
    public void ConfirmationMutation_RequiresPreflightedJsonContentType(string? contentType, bool expected)
    {
        var context = new DefaultHttpContext();
        context.Request.ContentType = contentType;
        var method = typeof(NotificationEndpoints).GetMethod(
            "HasMutationJsonContentType",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);
        Assert.Equal(expected, method.Invoke(null, [context.Request]));
    }

    [Fact]
    public void MigrationDown_NormalizesNewEnumValuesBeforeDroppingColumns()
    {
        var operations = new ExposedMigration().BuildDown();
        var sql = operations.OfType<SqlOperation>().Select(operation => operation.Sql).ToList();
        var firstDrop = operations.FindIndex(operation =>
            operation is DropIndexOperation or DropColumnOperation);

        Assert.Contains(sql, statement =>
            statement.Contains("CompletedLate", StringComparison.Ordinal)
            && statement.Contains("Complete", StringComparison.Ordinal));
        Assert.Contains(sql, statement =>
            statement.Contains("OperationStartConfirmation", StringComparison.Ordinal)
            && statement.Contains("OperationFinishConfirmation", StringComparison.Ordinal));
        Assert.True(firstDrop >= 2, "Compatibility cleanup must run before schema objects are dropped.");
    }

    private sealed class ExposedMigration : AddOperationScheduleConfirmations
    {
        public List<MigrationOperation> BuildDown()
        {
            var builder = new MigrationBuilder("Microsoft.EntityFrameworkCore.SqlServer");
            base.Down(builder);
            return builder.Operations;
        }
    }
}
