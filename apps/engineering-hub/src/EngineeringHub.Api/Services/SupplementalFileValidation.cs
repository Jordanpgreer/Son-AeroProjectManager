namespace EngineeringHub.Api.Services;

public static class SupplementalFileValidation
{
    private static readonly IReadOnlyDictionary<string, string> DocumentTypes =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [".doc"] = "application/msword",
            [".docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            [".xls"] = "application/vnd.ms-excel",
            [".xlsx"] = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            [".csv"] = "text/csv",
            [".txt"] = "text/plain",
            [".rtf"] = "application/rtf",
            [".dwg"] = "application/acad",
            [".dxf"] = "application/dxf",
            [".step"] = "application/step",
            [".stp"] = "application/step",
            [".iges"] = "model/iges",
            [".igs"] = "model/iges",
            [".zip"] = "application/zip"
        };

    private static readonly IReadOnlyDictionary<string, string> DrawingTypes =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [".pdf"] = "application/pdf",
            [".jpg"] = "image/jpeg",
            [".jpeg"] = "image/jpeg",
            [".jpe"] = "image/jpeg",
            [".jfif"] = "image/jpeg",
            [".png"] = "image/png",
            [".gif"] = "image/gif",
            [".bmp"] = "image/bmp",
            [".dib"] = "image/bmp",
            [".webp"] = "image/webp",
            [".tif"] = "image/tiff",
            [".tiff"] = "image/tiff",
            [".heic"] = "image/heic",
            [".heif"] = "image/heif",
            [".avif"] = "image/avif"
        };

    public static async Task<DrawingFileMetadata?> InspectAsync(IFormFile file, CancellationToken cancellationToken)
    {
        var controlledDrawingType = await DrawingFileValidation.InspectAsync(file, cancellationToken);
        if (controlledDrawingType is not null) return controlledDrawingType;

        var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
        return DocumentTypes.TryGetValue(extension, out var contentType)
            ? new DrawingFileMetadata(contentType, extension)
            : null;
    }

    public static string GetContentType(string fileName)
    {
        var extension = Path.GetExtension(fileName);
        if (DrawingTypes.TryGetValue(extension, out var drawingType)) return drawingType;
        return DocumentTypes.TryGetValue(extension, out var documentType)
            ? documentType
            : "application/octet-stream";
    }
}
