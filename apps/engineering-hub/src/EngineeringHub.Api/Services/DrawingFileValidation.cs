using System.Text;

namespace EngineeringHub.Api.Services;

public sealed record DrawingFileMetadata(string ContentType, string Extension);

public static class DrawingFileValidation
{
    private static readonly IReadOnlyDictionary<string, string[]> AllowedExtensions =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["application/pdf"] = [".pdf"],
            ["image/jpeg"] = [".jpg", ".jpeg", ".jpe", ".jfif"],
            ["image/png"] = [".png"],
            ["image/gif"] = [".gif"],
            ["image/bmp"] = [".bmp", ".dib"],
            ["image/webp"] = [".webp"],
            ["image/tiff"] = [".tif", ".tiff"],
            ["image/heic"] = [".heic"],
            ["image/heif"] = [".heif"],
            ["image/avif"] = [".avif"]
        };

    public static async Task<DrawingFileMetadata?> InspectAsync(IFormFile file, CancellationToken cancellationToken)
    {
        var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(extension)) return null;

        await using var stream = file.OpenReadStream();
        var header = new byte[32];
        var bytesRead = await stream.ReadAsync(header.AsMemory(), cancellationToken);
        var contentType = DetectContentType(header, bytesRead);
        if (contentType is null ||
            !AllowedExtensions.TryGetValue(contentType, out var extensions) ||
            !extensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
            return null;

        return new DrawingFileMetadata(contentType, extension);
    }

    private static string? DetectContentType(byte[] header, int length)
    {
        if (StartsWith(header, length, "%PDF-"u8.ToArray())) return "application/pdf";
        if (StartsWith(header, length, [0xFF, 0xD8, 0xFF])) return "image/jpeg";
        if (StartsWith(header, length, [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A])) return "image/png";
        if (StartsWith(header, length, "GIF87a"u8.ToArray()) || StartsWith(header, length, "GIF89a"u8.ToArray())) return "image/gif";
        if (StartsWith(header, length, "BM"u8.ToArray())) return "image/bmp";
        if (length >= 12 && Encoding.ASCII.GetString(header, 0, 4) == "RIFF" && Encoding.ASCII.GetString(header, 8, 4) == "WEBP") return "image/webp";
        if (StartsWith(header, length, [0x49, 0x49, 0x2A, 0x00]) || StartsWith(header, length, [0x4D, 0x4D, 0x00, 0x2A])) return "image/tiff";
        if (length >= 12 && Encoding.ASCII.GetString(header, 4, 4) == "ftyp")
        {
            return Encoding.ASCII.GetString(header, 8, 4) switch
            {
                "heic" or "heix" or "hevc" or "hevx" => "image/heic",
                "mif1" or "msf1" => "image/heif",
                "avif" or "avis" => "image/avif",
                _ => null
            };
        }
        return null;
    }

    private static bool StartsWith(byte[] value, int length, byte[] signature)
    {
        if (length < signature.Length) return false;
        for (var index = 0; index < signature.Length; index++)
            if (value[index] != signature[index]) return false;
        return true;
    }
}
