namespace CsgoWorkshop.Core;

public static class AppPaths
{
    private const string WindowsName = "CsgoWorkshop";
    private const string UnixName = "csgo-workshop";

    private static readonly Lazy<string> LazyConfigDir = new(ResolveConfigDir);
    private static readonly Lazy<string> LazyDataDir = new(ResolveDataDir);

    public static string ConfigDir => LazyConfigDir.Value;
    public static string DataDir => LazyDataDir.Value;

    public static string ConfigFile => Path.Combine(ConfigDir, "config.json");
    public static string InstalledFile => Path.Combine(ConfigDir, "installed.json");
    public static string SteamCmdDir => Path.Combine(DataDir, "steamcmd");
    public static string ContentDir => Path.Combine(DataDir, "content");
    public static string SteamHomeDir => Path.Combine(DataDir, "home");

    private static string ResolveConfigDir()
    {
        if (OperatingSystem.IsWindows())
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), WindowsName);

        var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        var root = !string.IsNullOrEmpty(xdg) ? xdg : Path.Combine(HomeDir(), ".config");
        return Path.Combine(root, UnixName);
    }

    private static string ResolveDataDir()
    {
        if (!OperatingSystem.IsWindows())
        {
            var xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
            var root = !string.IsNullOrEmpty(xdg) ? xdg : Path.Combine(HomeDir(), ".local", "share");
            return Path.Combine(root, UnixName);
        }

        // steamcmd.exe is a 32-bit program and misbehaves with non-ASCII install paths
        // (for example a Cyrillic user name), so prefer an ASCII-only location.
        var systemRoot = Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\";
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), WindowsName),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), WindowsName),
            Path.Combine(Environment.GetEnvironmentVariable("PUBLIC") ?? @"C:\Users\Public", WindowsName),
            Path.Combine(systemRoot, "Temp", WindowsName),
        };

        foreach (var candidate in candidates)
        {
            if (!string.IsNullOrWhiteSpace(candidate) && candidate.All(char.IsAscii) && CanWrite(candidate))
                return candidate;
        }

        return candidates[0];
    }

    private static string HomeDir() =>
        Environment.GetEnvironmentVariable("HOME") is { Length: > 0 } home
            ? home
            : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    private static bool CanWrite(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            var probe = Path.Combine(dir, ".write-test-" + Guid.NewGuid().ToString("N")[..8]);
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
