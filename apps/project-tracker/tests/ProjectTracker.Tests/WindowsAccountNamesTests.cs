using SonAero.Platform.Security;

namespace ProjectTracker.Tests;

public sealed class WindowsAccountNamesTests
{
    [Theory]
    [InlineData(@"SON4L\jordan.greer", @"SON4L\jordan.greer")]
    [InlineData("son4l/jordan.greer", @"son4l\jordan.greer")]
    [InlineData("  SON4L / jordan.greer  ", @"SON4L\jordan.greer")]
    public void Normalize_UsesWindowsDomainAccountFormat(string input, string expected)
    {
        Assert.Equal(expected, WindowsAccountNames.Normalize(input));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("SON4L/")]
    [InlineData("/jordan.greer")]
    [InlineData("SON4L/team/jordan.greer")]
    public void Normalize_RejectsInvalidAccounts(string? input)
    {
        Assert.Null(WindowsAccountNames.Normalize(input));
    }

    [Fact]
    public void Equals_MatchesSlashAndCaseVariantsWithoutMatchingBareUserName()
    {
        Assert.True(WindowsAccountNames.Equals(
            "son4l/jordan.greer",
            @"SON4L\JORDAN.GREER"));
        Assert.False(WindowsAccountNames.Equals(
            "jordan.greer",
            @"SON4L\jordan.greer"));
    }
}
