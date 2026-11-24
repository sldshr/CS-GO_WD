using System.Text.RegularExpressions;

namespace CsgoWorkshop.Core;

public static partial class MapsFolder
{
    private static readonly string[] GameDirNames = ["csgo legacy", "CSGO Legacy", "Counter-Strike Global Offensive"];
    private static readonly string[] SteamAppsNames = ["steamapps", "SteamApps"];

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

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

        var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var name = Path.GetFileName(trimmed);
        if (string.Equals(name, "maps", StringComparison.OrdinalIgnoreCase))
        {
            RejectIfCs2Folder(trimmed);
            return trimmed;
        }

        // Check .../csgo/maps before .../maps so picking the game root never gets confused.
        foreach (var relative in new[] { Path.Combine("csgo", "maps"), "maps" })
        {
            var candidate = Path.Combine(trimmed, relative);
            if (Directory.Exists(candidate))
            {
                RejectIfCs2Folder(candidate);
                return candidate;
            }
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
                foreach (var steamApps in SteamAppsNames)
                {
                    foreach (var gameDir in GameDirNames)
                    {
                        var maps = Path.Combine(library, steamApps, "common", gameDir, "csgo", "maps");
                        if (Directory.Exists(maps) && !IsCs2MapsDir(maps))
                            return Path.GetFullPath(maps);
                    }
                }
            }
        }
        catch (Exception)
        {
            // Detection is a convenience only.
        }

        return null;
    }

    public static string ExpandHome(string path)
    {
        if (path == "~" || path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith("~\\", StringComparison.Ordinal))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(home, path.Length > 2 ? path[2..] : "");
        }

        return path;
    }

    private static void RejectIfCs2Folder(string mapsDir)
    {
        if (!IsCs2MapsDir(mapsDir))
            return;

        // .../Counter-Strike Global Offensive/game/csgo/maps -> check if .../Counter-Strike Global Offensive/csgo/maps exists
        var csgoDir = Path.GetDirectoryName(mapsDir);
        var gameDir = csgoDir is not null ? Path.GetDirectoryName(csgoDir) : null;
        var rootDir = gameDir is not null ? Path.GetDirectoryName(gameDir) : null;
        var legacyCandidate = rootDir is not null ? Path.Combine(rootDir, "csgo", "maps") : null;

        var hint = legacyCandidate is not null && Directory.Exists(legacyCandidate)
            ? $"Используйте папку CS:GO Legacy: {legacyCandidate}"
            : "Укажите папку CS:GO Legacy вида .../csgo/maps (без подпапки game).";

        throw new InstallException(
            $"Указана папка карт CS2 ({mapsDir}), а не CS:GO Legacy.\n{hint}");
    }

    private static bool IsCs2MapsDir(string mapsDir)
    {
        var csgoDir = Path.GetDirectoryName(mapsDir);
        return csgoDir is not null && File.Exists(Path.Combine(csgoDir, "gameinfo.gi"));
    }

    private static IEnumerable<string> SteamLibraries()
    {
        var seen = new HashSet<string>(PathComparer);

        foreach (var root in SteamRoots())
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
                continue;

            var fullRoot = Path.GetFullPath(root);
            if (!seen.Add(fullRoot))
                continue;

            yield return fullRoot;

            foreach (var steamApps in SteamAppsNames)
            {
                var vdf = Path.Combine(fullRoot, steamApps, "libraryfolders.vdf");
                if (!File.Exists(vdf))
                    continue;

                foreach (Match match in LibraryPath().Matches(File.ReadAllText(vdf)))
                {
                    var library = match.Groups[1].Value.Replace(@"\\", @"\");
                    if (Directory.Exists(library))
                    {
                        var fullLib = Path.GetFullPath(library);
                        if (seen.Add(fullLib))
                            yield return fullLib;
                    }
                }
            }
        }
    }

    private static IEnumerable<string> SteamRoots()
    {
        if (OperatingSystem.IsWindows())
        {
            if (Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) is string current)
                yield return current;
            if (Microsoft.Win32.Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath", null) is string machine32)
                yield return machine32;
            if (Microsoft.Win32.Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Valve\Steam", "InstallPath", null) is string machine64)
                yield return machine64;

            yield return @"C:\Program Files (x86)\Steam";
            yield return @"C:\Program Files\Steam";
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
