namespace SonAero.Platform.Engineering;

public static class EngineeringStorageSchema
{
    public const int SettingsId = 1;

    public const string Sqlite = """
        CREATE TABLE IF NOT EXISTS "EngineeringStorageSettings" (
            "Id" INTEGER NOT NULL CONSTRAINT "PK_EngineeringStorageSettings" PRIMARY KEY,
            "RootPath" TEXT NOT NULL,
            "PreviousRootPathsJson" TEXT NOT NULL,
            "UpdatedAt" TEXT NOT NULL,
            "UpdatedBy" TEXT NOT NULL
        );
        """;

    public const string SqlServer = """
        IF OBJECT_ID(N'[EngineeringStorageSettings]', N'U') IS NULL
        BEGIN
            CREATE TABLE [EngineeringStorageSettings] (
                [Id] int NOT NULL,
                [RootPath] nvarchar(2048) NOT NULL,
                [PreviousRootPathsJson] nvarchar(max) NOT NULL,
                [UpdatedAt] datetimeoffset NOT NULL,
                [UpdatedBy] nvarchar(160) NOT NULL,
                CONSTRAINT [PK_EngineeringStorageSettings] PRIMARY KEY ([Id])
            );
        END;
        """;
}

public static class EngineeringStoragePolicy
{
    public static string NormalizeRoot(string value, bool requireUncPath)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException("A drawing storage root is required.");

        var root = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(Environment.ExpandEnvironmentVariables(value.Trim())));
        if (requireUncPath && !IsUncPath(root))
            throw new InvalidOperationException(
                "Production drawing storage must use a UNC path such as \\\\server\\share\\Engineering\\Drawings. Mapped drive letters are not reliable for server services.");
        return root;
    }

    public static bool IsUncPath(string path) => path.StartsWith(@"\\", StringComparison.Ordinal);

    public static string NormalizeDesignAuthority(string value)
    {
        var name = value.Trim();
        if (string.IsNullOrWhiteSpace(name))
            throw new InvalidOperationException("A design authority name is required.");
        if (name is "." or ".." || name.StartsWith(".", StringComparison.Ordinal))
            throw new InvalidOperationException("Design authority names cannot be hidden or relative folder names.");
        if (name.Length > 200)
            throw new InvalidOperationException("Design authority names must be 200 characters or fewer.");
        if (name.EndsWith(".", StringComparison.Ordinal))
            throw new InvalidOperationException("Design authority names cannot end with a period.");
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.Contains(Path.DirectorySeparatorChar) || name.Contains(Path.AltDirectorySeparatorChar))
            throw new InvalidOperationException("The design authority name contains characters that are not valid in a folder name.");
        var deviceName = name.Split('.')[0].ToUpperInvariant();
        if (ReservedDeviceNames.Contains(deviceName))
            throw new InvalidOperationException("That design authority name is reserved by Windows and cannot be used as a folder.");
        return name;
    }

    public static string AuthorityPath(string root, string authority)
    {
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var path = Path.GetFullPath(Path.Combine(normalizedRoot, NormalizeDesignAuthority(authority)));
        var prefix = normalizedRoot.EndsWith(Path.DirectorySeparatorChar)
            ? normalizedRoot
            : normalizedRoot + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The design authority folder must remain inside the configured drawing root.");
        return path;
    }

    public static IReadOnlyList<string> EnumerateAuthorities(string root) => Directory
        .EnumerateDirectories(root)
        .Select(path => new DirectoryInfo(path))
        .Where(directory =>
            !directory.Name.StartsWith(".", StringComparison.Ordinal) &&
            !directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
        .Select(directory => directory.Name)
        .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
        .ToArray();

    public static void VerifyWritable(string root)
    {
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException("The selected drawing storage root does not exist.");

        var probe = Path.Combine(root, $".drawing-storage-probe-{Guid.NewGuid():N}.tmp");
        try
        {
            using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
        }
        finally
        {
            if (File.Exists(probe)) File.Delete(probe);
        }
    }

    private static readonly HashSet<string> ReservedDeviceNames =
    [
        "CON", "PRN", "AUX", "NUL", "CLOCK$",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    ];
}
