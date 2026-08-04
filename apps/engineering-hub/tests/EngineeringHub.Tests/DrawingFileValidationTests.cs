using EngineeringHub.Api.Services;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace EngineeringHub.Tests;

public sealed class DrawingFileValidationTests
{
    public static TheoryData<string, string, byte[], string> SupportedFiles => new()
    {
        { "drawing.pdf", "application/pdf", "%PDF-1.7"u8.ToArray(), "application/pdf" },
        { "drawing.jpg", "image/jpeg", [0xFF, 0xD8, 0xFF, 0xE0], "image/jpeg" },
        { "drawing.png", "image/png", [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A], "image/png" },
        { "drawing.gif", "image/gif", "GIF89a"u8.ToArray(), "image/gif" },
        { "drawing.bmp", "image/bmp", "BMdrawing"u8.ToArray(), "image/bmp" },
        { "drawing.webp", "image/webp", "RIFF0000WEBP"u8.ToArray(), "image/webp" },
        { "drawing.tiff", "image/tiff", [0x49, 0x49, 0x2A, 0x00], "image/tiff" },
        { "drawing.heic", "image/heic", [0, 0, 0, 0, 0x66, 0x74, 0x79, 0x70, 0x68, 0x65, 0x69, 0x63], "image/heic" },
        { "drawing.avif", "image/avif", [0, 0, 0, 0, 0x66, 0x74, 0x79, 0x70, 0x61, 0x76, 0x69, 0x66], "image/avif" }
    };

    [Theory]
    [MemberData(nameof(SupportedFiles))]
    public async Task SupportedDrawingFilesAreIdentifiedBySignature(
        string fileName,
        string submittedContentType,
        byte[] content,
        string expectedContentType)
    {
        var result = await DrawingFileValidation.InspectAsync(
            FormFile(content, fileName, submittedContentType),
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(expectedContentType, result.ContentType);
    }

    [Theory]
    [InlineData("drawing.svg", "image/svg+xml", "<svg></svg>")]
    [InlineData("drawing.exe", "image/png", "not a png")]
    [InlineData("drawing.png", "image/png", "%PDF-1.7")]
    public async Task UnsupportedOrMismatchedFilesAreRejected(string fileName, string contentType, string content)
    {
        var result = await DrawingFileValidation.InspectAsync(
            FormFile(System.Text.Encoding.UTF8.GetBytes(content), fileName, contentType),
            CancellationToken.None);

        Assert.Null(result);
    }

    [Theory]
    [InlineData("analysis.docx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document")]
    [InlineData("dimensions.dwg", "application/acad")]
    [InlineData("model.step", "application/step")]
    [InlineData("package.zip", "application/zip")]
    public async Task SupplementalEngineeringDocumentsAreAccepted(string fileName, string expectedContentType)
    {
        var result = await SupplementalFileValidation.InspectAsync(
            FormFile("supplemental"u8.ToArray(), fileName, "application/octet-stream"),
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(expectedContentType, result.ContentType);
    }

    private static FormFile FormFile(byte[] content, string fileName, string contentType)
    {
        var stream = new MemoryStream(content);
        return new FormFile(stream, 0, content.Length, "file", fileName)
        {
            Headers = new HeaderDictionary(),
            ContentType = contentType
        };
    }
}
