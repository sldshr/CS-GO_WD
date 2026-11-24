using System.IO.Compression;

namespace CsgoWorkshop.Core;

public sealed record ExtractedMap(string Name, string Path, long Size, bool Replaced);

public sealed record ExtractResult(IReadOnlyList<ExtractedMap> Maps, int SkippedEntries);

public static class MapExtractor
{
    private const long MaxMapSize = 1536L * 1024 * 1024;
    private static readonly byte[] BspMagic = "VBSP"u8.ToArray();

    public static ExtractResult Extract(string itemDir, string mapsDir)
    {
        if (!Directory.Exists(itemDir))
            throw new InstallException($"Папка предмета не найдена: {itemDir}");
        if (!Directory.Exists(mapsDir))
            throw new InstallException($"Папка maps не найдена: {mapsDir}");

        var files = Directory.EnumerateFiles(itemDir, "*", SearchOption.AllDirectories)
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();

        var maps = new OrderedDictionary<string, ExtractedMap>(StringComparer.OrdinalIgnoreCase);
        var skipped = 0;
        var vpkOnly = false;

        foreach (var file in files)
        {
            switch (System.IO.Path.GetExtension(file).ToLowerInvariant())
            {
                case ".bsp":
                {
                    using var source = File.OpenRead(file);
                    var name = System.IO.Path.GetFileNameWithoutExtension(file);
                    var map = Write(source, source.Length, name, mapsDir);
                    maps[map.Name] = map;
                    break;
                }
                case ".bin" or ".zip":
                    skipped += ExtractArchive(file, mapsDir, maps);
                    break;
                case ".vpk":
                    vpkOnly = true;
                    break;
            }
        }

        if (maps.Count > 0)
            return new ExtractResult([.. maps.Values], skipped);

        if (vpkOnly)
            throw new InstallException(
                "Предмет собран для CS2 (.vpk) и не содержит карты для CS:GO Legacy. " +
                "Нужна версия карты, загруженная автором для CS:GO.");

        throw new InstallException("В скачанном предмете нет файла карты (.bin с .bsp внутри или .bsp).");
    }

    private static int ExtractArchive(string archivePath, string mapsDir, OrderedDictionary<string, ExtractedMap> maps)
    {
        ZipArchive archive;
        try
        {
            archive = ZipFile.OpenRead(archivePath);
        }
        catch (InvalidDataException)
        {
            throw new InstallException(
                $"Файл {System.IO.Path.GetFileName(archivePath)} не является ZIP-архивом, формат не поддерживается.");
        }

        using (archive)
        {
            var skipped = 0;
            foreach (var entry in archive.Entries)
            {
                var baseName = BaseName(entry.FullName);
                if (baseName.Length == 0)
                    continue;

                if (!baseName.EndsWith(".bsp", StringComparison.OrdinalIgnoreCase))
                {
                    skipped++;
                    continue;
                }

                using var stream = entry.Open();
                var map = Write(stream, entry.Length, baseName[..^4], mapsDir);
                maps[map.Name] = map;
            }

            return skipped;
        }
    }

    private static string BaseName(string entryName)
    {
        var slash = entryName.LastIndexOfAny(['/', '\\']);
        return slash >= 0 ? entryName[(slash + 1)..] : entryName;
    }

    private static ExtractedMap Write(Stream source, long length, string name, string mapsDir)
    {
        if (!MapName.IsSafe(name))
            throw new InstallException($"Недопустимое имя карты в архиве: \"{name}\".");
        if (length > MaxMapSize)
            throw new InstallException($"Файл карты слишком большой ({Format.Size(length)}).");

        var header = new byte[BspMagic.Length];
        if (source.ReadAtLeast(header, header.Length, throwOnEndOfStream: false) < header.Length || !header.AsSpan().SequenceEqual(BspMagic))
            throw new InstallException($"Файл {name}.bsp не похож на карту Source (нет заголовка VBSP).");

        var destination = System.IO.Path.Combine(mapsDir, name + ".bsp");
        var temp = destination + ".part";
        var replaced = File.Exists(destination);

        try
        {
            using (var target = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                target.Write(header);
                source.CopyTo(target);
            }

            File.Move(temp, destination, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            TryDelete(temp);
            throw new InstallException(
                $"Не удалось записать {destination}: {e.Message}\n" +
                "Если карта сейчас загружена в игре, закройте игру или смените карту и повторите.");
        }

        return new ExtractedMap(name, destination, new FileInfo(destination).Length, replaced);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception)
        {
            // Nothing sensible to do about a leftover temp file.
        }
    }
}
