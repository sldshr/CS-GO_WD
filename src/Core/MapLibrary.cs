using System.Text.Json;

namespace CsgoWorkshop.Core;

// ReplacedOriginal: installing this map overwrote a file the program had not put there (possibly a map of the game itself).
public sealed record InstalledMap(
    ulong ItemId,
    string Name,
    string Path,
    long Size,
    DateTimeOffset InstalledAt,
    bool ReplacedOriginal = false);

// Maps installed by this program. Only these can be deleted from the UI: the maps folder also holds the game's own maps.
public sealed class MapLibrary(string manifestPath)
{
    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public static MapLibrary Default() => new(AppPaths.InstalledFile);

    public IReadOnlyList<InstalledMap> Load()
    {
        lock (Gate)
            return Read();
    }

    public void Add(IEnumerable<InstalledMap> maps)
    {
        lock (Gate)
        {
            var list = Read();
            foreach (var map in maps)
            {
                // The file now belongs to the newest item, but whatever it replaced originally stays replaced.
                var previous = list.FirstOrDefault(m => string.Equals(m.Path, map.Path, PathComparison));
                list.RemoveAll(m => string.Equals(m.Path, map.Path, PathComparison));
                list.Add(previous is null ? map : map with { ReplacedOriginal = previous.ReplacedOriginal });
            }

            Write(list);
        }
    }

    public void Remove(InstalledMap map)
    {
        lock (Gate)
        {
            if (!MapName.IsSafe(map.Name)
                || !System.IO.Path.IsPathFullyQualified(map.Path)
                || !string.Equals(System.IO.Path.GetFileName(map.Path), map.Name + ".bsp", PathComparison))
                throw new InstallException($"Запись о карте \"{map.Name}\" повреждена, файл не удалён.");

            try
            {
                File.Delete(map.Path);
            }
            catch (DirectoryNotFoundException)
            {
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                throw new InstallException(
                    $"Не удалось удалить {map.Path}: {e.Message}\n" +
                    "Если карта сейчас загружена в игре, закройте игру или смените карту и повторите.");
            }

            try
            {
                var list = Read();
                if (list.RemoveAll(m => string.Equals(m.Path, map.Path, PathComparison)) > 0)
                    Write(list);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                throw new InstallException($"Карта \"{map.Name}\" удалена, но список скачанных карт обновить не удалось: {e.Message}");
            }
        }
    }

    private List<InstalledMap> Read()
    {
        try
        {
            if (File.Exists(manifestPath))
            {
                var loaded = JsonSerializer.Deserialize<List<InstalledMap>>(File.ReadAllText(manifestPath));
                if (loaded != null)
                    return loaded.Where(m => m is { Name: not null, Path: not null }).ToList();
            }
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            // An unreadable list must not block installing maps; it is rewritten on the next change.
        }

        return [];
    }

    private void Write(List<InstalledMap> list)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(manifestPath)!);
        var temp = manifestPath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(list, JsonOptions));
        File.Move(temp, manifestPath, overwrite: true);
    }
}
