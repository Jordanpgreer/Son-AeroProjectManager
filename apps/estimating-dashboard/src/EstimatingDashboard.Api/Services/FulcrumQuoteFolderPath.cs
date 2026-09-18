using System.Text.RegularExpressions;

namespace EstimatingDashboard.Api.Services;

internal static partial class FulcrumQuoteFolderPath
{
    private static readonly char[] InvalidWindowsPathCharacters = ['<', '>', '"', '|', '?', '*'];

    public static string? Extract(string? internalNotes)
    {
        if (string.IsNullOrWhiteSpace(internalNotes)) return null;

        // Prefer explicit labels so unrelated note text containing a slash cannot win.
        foreach (Match match in LabeledPathPattern().Matches(internalNotes))
            if (Normalize(match.Groups["path"].Value) is { } labeled)
                return labeled;

        foreach (var line in Lines(internalNotes))
            if (StartsWithSDrive(line) && Normalize(line) is { } driven)
                return driven;

        // Last-resort compatibility for the existing convention where Internal Notes
        // contains only a relative Windows path and intentionally omits "S:".
        foreach (var line in Lines(internalNotes))
            if (line.Contains('\\')
                && !line.Contains(':')
                && Normalize(line) is { } relative)
                return relative;

        return null;
    }

    internal static string? Normalize(string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate)) return null;

        var value = TrimDecoration(candidate);
        if (value.Length == 0
            || value.Contains("://", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
            return null;

        value = value.Replace('/', '\\');
        if (value.StartsWith("\\\\", StringComparison.Ordinal)) return null;

        var drive = DrivePattern().Match(value);
        if (drive.Success)
        {
            if (!string.Equals(drive.Groups["drive"].Value, "S", StringComparison.OrdinalIgnoreCase))
                return null;
            value = value[drive.Length..];
            if (!value.StartsWith('\\')) return null;
        }
        else if (value.Contains(':'))
        {
            return null;
        }

        value = value.TrimStart('\\');
        var segments = value.Split('\\', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length == 0) return null;
        foreach (var segment in segments)
        {
            if (segment is "." or ".."
                || segment.Length == 0
                || segment.EndsWith(' ')
                || segment.EndsWith('.')
                || segment.Any(character => char.IsControl(character)
                    || InvalidWindowsPathCharacters.Contains(character)
                    || character == ':'))
                return null;
        }

        return $"S:\\{string.Join('\\', segments)}";
    }

    private static IEnumerable<string> Lines(string value) => value
        .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(TrimDecoration)
        .Where(line => line.Length > 0);

    private static bool StartsWithSDrive(string value) =>
        DrivePattern().Match(value) is { Success: true } match
        && string.Equals(match.Groups["drive"].Value, "S", StringComparison.OrdinalIgnoreCase);

    private static string TrimDecoration(string value)
    {
        value = value.Trim();
        if (value.StartsWith("- ", StringComparison.Ordinal)
            || value.StartsWith("* ", StringComparison.Ordinal))
            value = value[2..].TrimStart();

        var changed = true;
        while (changed && value.Length > 0)
        {
            changed = false;
            var withoutPunctuation = value.TrimEnd('.', ',', ';');
            if (withoutPunctuation.Length != value.Length)
            {
                value = withoutPunctuation.TrimEnd();
                changed = true;
            }

            if (value.Length >= 2
                && ((value[0] == '"' && value[^1] == '"')
                    || (value[0] == '\'' && value[^1] == '\'')
                    || (value[0] == '`' && value[^1] == '`')))
            {
                value = value[1..^1].Trim();
                changed = true;
            }
        }
        return value;
    }

    [GeneratedRegex(
        @"(?im)^\s*(?:quote\s+)?(?:file\s*path|filepath|folder\s*path|folder|path)\s*(?:is\s*)?[:=\-]\s*(?<path>[^\r\n]+?)\s*$",
        RegexOptions.CultureInvariant)]
    private static partial Regex LabeledPathPattern();

    [GeneratedRegex(@"^(?<drive>[A-Za-z])\s*:\s*", RegexOptions.CultureInvariant)]
    private static partial Regex DrivePattern();
}
