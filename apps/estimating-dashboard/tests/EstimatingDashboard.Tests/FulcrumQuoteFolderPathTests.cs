using EstimatingDashboard.Api.Services;

namespace EstimatingDashboard.Tests;

public sealed class FulcrumQuoteFolderPathTests
{
    [Theory]
    [InlineData("Folder path: Estimating\\Quotes\\Acme\\Q-100", @"S:\Estimating\Quotes\Acme\Q-100")]
    [InlineData("File path: \\Estimating\\Quotes\\Acme\\Q-100", @"S:\Estimating\Quotes\Acme\Q-100")]
    [InlineData("FILEPATH: 'S:/Estimating/Quotes/Acme/Q-100'.", @"S:\Estimating\Quotes\Acme\Q-100")]
    [InlineData("Folder: `Estimating\\Quotes\\Acme\\Q-100`;", @"S:\Estimating\Quotes\Acme\Q-100")]
    [InlineData("Path: S:\\Estimating\\Quotes\\Acme\\Q-100", @"S:\Estimating\Quotes\Acme\Q-100")]
    [InlineData("Unrelated note first\r\nEstimating\\Quotes\\Acme\\Q-100", @"S:\Estimating\Quotes\Acme\Q-100")]
    [InlineData("S:\\Estimating\\Quotes\\Acme\\Q-100", @"S:\Estimating\Quotes\Acme\Q-100")]
    public void Extract_normalizes_supported_internal_note_formats(string notes, string expected)
    {
        Assert.Equal(expected, FulcrumQuoteFolderPath.Extract(notes));
    }

    [Theory]
    [InlineData("Folder path: ..\\Secrets")]
    [InlineData("Folder path: C:\\Estimating\\Quotes")]
    [InlineData("Folder path: \\\\server\\share\\Quotes")]
    [InlineData("Folder path: https://example.test/Quotes")]
    [InlineData("Folder path: Estimating\\Quotes\\Bad*Name")]
    [InlineData("This note has no folder path")]
    [InlineData("")]
    public void Extract_rejects_unsafe_or_non_path_notes(string notes)
    {
        Assert.Null(FulcrumQuoteFolderPath.Extract(notes));
    }

    [Fact]
    public void First_valid_labeled_path_wins_over_unlabeled_fallback_text()
    {
        var notes = "Fallback\\Should Not Win\r\nFolder: Estimating\\Quotes\\Chosen";

        Assert.Equal(@"S:\Estimating\Quotes\Chosen", FulcrumQuoteFolderPath.Extract(notes));
    }
}
