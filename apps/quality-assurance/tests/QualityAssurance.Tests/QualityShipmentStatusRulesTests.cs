using QualityAssurance.Api.Services;

namespace QualityAssurance.Tests;

public sealed class QualityShipmentStatusRulesTests
{
    [Theory]
    [InlineData("Quality", "WIP")]
    [InlineData("Source Inspection", "Pending Source")]
    [InlineData("Customer Service", "Pending Customer Service")]
    [InlineData("FAI Team", "Pending FAI Approval")]
    public void Assignment_group_names_map_to_supported_queue_statuses(string group, string expected)
    {
        Assert.Equal(expected, QualityShipmentStatusRules.ForAssignedGroup(group));
    }

    [Fact]
    public void Users_can_only_select_the_four_supported_statuses()
    {
        Assert.Equal("Pending Customer Service", QualityShipmentStatusRules.NormalizeUserStatus("pending customer service"));
        Assert.Throws<ArgumentException>(() => QualityShipmentStatusRules.NormalizeUserStatus("On Hold"));
    }
}
