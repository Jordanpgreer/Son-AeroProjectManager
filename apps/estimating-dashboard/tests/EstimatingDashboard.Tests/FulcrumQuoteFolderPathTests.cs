using System.Text.Json;
using EstimatingDashboard.Api.Models;
using EstimatingDashboard.Api.Services;

namespace EstimatingDashboard.Tests;

public sealed class FulcrumQuoteFolderPathTests
{
    private const string NetworkQuoteFolder = @"\\SON-FS01\Shared\- ESTIMATES\Quotes\Parts & Packages\Q-100";

    [Theory]
    [InlineData("Folder path: Estimating\\Quotes\\Acme\\Q-100", @"S:\Estimating\Quotes\Acme\Q-100")]
    [InlineData("File path: \\Estimating\\Quotes\\Acme\\Q-100", @"S:\Estimating\Quotes\Acme\Q-100")]
    [InlineData("FILEPATH: 'S:/Estimating/Quotes/Acme/Q-100'.", @"S:\Estimating\Quotes\Acme\Q-100")]
    [InlineData("Folder: `Estimating\\Quotes\\Acme\\Q-100`;", @"S:\Estimating\Quotes\Acme\Q-100")]
    [InlineData("Path: S:\\Estimating\\Quotes\\Acme\\Q-100", @"S:\Estimating\Quotes\Acme\Q-100")]
    [InlineData("Unrelated note first\r\nEstimating\\Quotes\\Acme\\Q-100", @"S:\Estimating\Quotes\Acme\Q-100")]
    [InlineData("S:\\Estimating\\Quotes\\Acme\\Q-100", @"S:\Estimating\Quotes\Acme\Q-100")]
    [InlineData("Part 123, quantity 2\r\n" + NetworkQuoteFolder, NetworkQuoteFolder)]
    [InlineData("Folder path: '" + NetworkQuoteFolder + "'.", NetworkQuoteFolder)]
    public void Extract_normalizes_supported_internal_note_formats(string notes, string expected)
    {
        Assert.Equal(expected, FulcrumQuoteFolderPath.Extract(notes));
    }

    [Theory]
    [InlineData("Folder path: ..\\Secrets")]
    [InlineData("Folder path: C:\\Estimating\\Quotes")]
    [InlineData("Folder path: \\\\server\\share\\Quotes")]
    [InlineData(@"Folder path: \\SON-FS01\Shared\- ESTIMATES\Quotes-Other\Q-100")]
    [InlineData(@"Folder path: \\SON-FS01\Shared\- ESTIMATES\Quotes\..\Q-100")]
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

    [Fact]
    public void Extract_all_returns_each_distinct_valid_path_from_all_note_comments()
    {
        var notes = "File path: S:\\Estimating\\Quotes\\Q4521\r\nMore context\r\nFolder: S:/Estimating/Quotes/Q4521-Support\r\nPath: s:\\estimating\\quotes\\q4521";

        Assert.Equal(
            [@"S:\Estimating\Quotes\Q4521", @"S:\Estimating\Quotes\Q4521-Support"],
            FulcrumQuoteFolderPath.ExtractAll(notes));
    }

    [Fact]
    public void Approved_unc_and_mapped_paths_can_be_kept_in_the_same_quote()
    {
        var notes = $"Folder path: S:\\Estimating\\Q-100\n{NetworkQuoteFolder}";

        Assert.Equal([@"S:\Estimating\Q-100", NetworkQuoteFolder], FulcrumQuoteFolderPath.ExtractAll(notes));
    }

    [Fact]
    public void Manual_network_folder_remains_an_arda_location_when_cached_as_the_primary_path()
    {
        var quote = new EstimatingQuoteHistoryRecord
        {
            QuoteFolderPath = NetworkQuoteFolder,
            ArdaFilePathOverridesJson = JsonSerializer.Serialize(new[] { NetworkQuoteFolder })
        };

        Assert.Equal(NetworkQuoteFolder, FulcrumQuoteFolderPath.Normalize(NetworkQuoteFolder));
        var location = Assert.Single(QuoteStatusService.FileLocations(quote));
        Assert.Equal(NetworkQuoteFolder, location.Path);
        Assert.Equal("Arda", location.Source);
    }
}
