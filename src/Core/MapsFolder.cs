using System.Text.RegularExpressions;

namespace CsgoWorkshop.Core;

public static partial class MapsFolder
{
    private static readonly string[] GameDirNames = ["csgo legacy", "Counter-Strike Global Offensive"];

    [GeneratedRegex("\"path\"\\s+\"((?:[^\"\\\\]|\\\\.)*)\"")]
    private static partial Regex LibraryPath();

    public static string Resolve(string? input)
    {
        var text = input?.Trim().Trim('"');
        if (string.IsNullOrEmpty(text))
            throw new InstallException("Не указана папка maps.");

        string path;
        try
        {
            path = Path.GetFullPath(ExpandHome(text));
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new InstallException($"Некорректный путь: {text}");
        }

        if (!Directory.Exists(path))
            throw new InstallException($"Папка не найдена: {path}");

        var name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.Equals(name, "maps", StringComparison.OrdinalIgnoreCase))
            return path;

        foreach (var relative in new[] { "maps", Path.Combine("csgo", "maps") })
        {
            var candidate = Path.Combine(path, relative);
            if (Directory.Exists(candidate))
                return candidate;
        }

        throw new InstallException(
            $"Это не папка maps: {path}\nУкажите папку вида .../steamapps/common/csgo legacy/csgo/maps");
    }

    public static string? Detect()
    {
        try
        {
            foreach (var library in SteamLibraries())
            {
                foreach (var gameDir in GameDirNames)
                {
                    var maps = Path.Combine(library, "steamapps", "common", gameDir, "csgo", "maps");
                    if (Directory.Exists(maps))
                        return Path.GetFullPath(maps);
                }
            }
        }
        catch (Exception)
        {
            // Detection is a convenience only.
        }

        return null;
    }

    private static string ExpandHome(string path)
    {
        if (path == "~" || path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith("~\\", StringComparison.Ordinal))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(home, path.Length > 2 ? path[2..] : "");
        }

        return path;
    }

    private static IEnumerable<string> SteamLibraries()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var root in SteamRoots())
        {
            if (!Directory.Exists(root) || !seen.Add(Path.GetFullPath(root)))
                continue;

            yield return root;

            var vdf = Path.Combine(root, "steamapps", "libraryfolders.vdf");
            if (!File.Exists(vdf))
                continue;

            foreach (Match match in LibraryPath().Matches(File.ReadAllText(vdf)))
            {
                var library = match.Groups[1].Value.Replace(@"\\", @"\");
                if (Directory.Exists(library) && seen.Add(Path.GetFullPath(library)))
                    yield return library;
            }
        }
    }

    private static IEnumerable<string> SteamRoots()
    {
        if (OperatingSystem.IsWindows())
        {
            if (Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) is string current)
                yield return current;
            if (Microsoft.Win32.Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath", null) is string machine)
                yield return machine;

            yield return @"C:\Program Files (x86)\Steam";
            yield break;
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        yield return Path.Combine(home, ".local", "share", "Steam");
        yield return Path.Combine(home, ".steam", "steam");
        yield return Path.Combine(home, ".steam", "root");
        yield return Path.Combine(home, ".var", "app", "com.valvesoftware.Steam", ".local", "share", "Steam");
        yield return Path.Combine(home, "snap", "steam", "common", ".local", "share", "Steam");
    }
}
