namespace NightlyDawn.Host;

/// <summary>
/// Where NightlyDawn keeps per-user data. macOS: <c>~/Library/Application Support/NightlyDawn</c>;
/// Linux (Omarchy): <c>$XDG_DATA_HOME/NightlyDawn</c>, falling back to <c>~/.local/share/NightlyDawn</c>.
/// <c>NIGHTLYDAWN_DATA_DIR</c> overrides both (tests, portable runs). The directory is created with mode 0700
/// because the NIP-49 key file lives in it; the file itself is 0600 (FileKeyFileStore).
/// </summary>
public static class AppDataPaths
{
    public const string EnvironmentVariable = "NIGHTLYDAWN_DATA_DIR";
    public const string DirectoryName = "NightlyDawn";
    public const string LocalKeyFileName = "local-key.ncryptsec";

    public static string Resolve(string? overrideDir, string? xdgDataHome, string home, bool isMacOS)
    {
        if (!string.IsNullOrWhiteSpace(overrideDir))
        {
            return Path.GetFullPath(overrideDir.Trim());
        }

        if (isMacOS)
        {
            return Path.Combine(home, "Library", "Application Support", DirectoryName);
        }

        var baseDir = string.IsNullOrWhiteSpace(xdgDataHome) ? Path.Combine(home, ".local", "share") : xdgDataHome.Trim();
        return Path.Combine(baseDir, DirectoryName);
    }

    public static string ResolveFromEnvironment() => Resolve(
        Environment.GetEnvironmentVariable(EnvironmentVariable),
        Environment.GetEnvironmentVariable("XDG_DATA_HOME"),
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        OperatingSystem.IsMacOS());

    /// <summary>Creates the directory (0700 on Unix) if needed and returns the key file path inside it.</summary>
    public static string EnsureLocalKeyFile(string dataDir)
    {
        if (!Directory.Exists(dataDir))
        {
            Directory.CreateDirectory(dataDir);
        }

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(dataDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return Path.Combine(dataDir, LocalKeyFileName);
    }
}
