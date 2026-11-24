using System.Diagnostics;
using System.IO.Compression;

namespace CsgoWorkshop.Core;

public sealed record ExtractedMap(string Name, string Path, long Size, bool Replaced);

public sealed record ExtractResult(IReadOnlyList<ExtractedMap> Maps, int SkippedEntries);

public static class MapExtractor
{
    private const long MaxMapSize = 1536L * 1024 * 1024;
    private static readonly byte[] BspMagic = "VBSP"u8.ToArray();

    public static ExtractResult Extract(
        string itemDir,
        string mapsDir,
        Reporter? reporter = null,
        CancellationToken ct = default)
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
            ct.ThrowIfCancellationRequested();

            switch (System.IO.Path.GetExtension(file).ToLowerInvariant())
            {
                case ".bsp":
                {
                    var baseName = System.IO.Path.GetFileName(file);
                    if (IsAppleDoubleMetadata(file, baseName))
                        break;

                    using var source = File.OpenRead(file);
                    var name = System.IO.Path.GetFileNameWithoutExtension(file);
                    var map = Write(source, source.Length, name, mapsDir, reporter, ct);
                    maps[map.Name] = map;
                    break;
                }
                case ".bin" or ".zip":
                    skipped += ExtractArchive(file, mapsDir, maps, reporter, ct);
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

    private static int ExtractArchive(
        string archivePath,
        string mapsDir,
        OrderedDictionary<string, ExtractedMap> maps,
        Reporter? reporter,
        CancellationToken ct)
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
                ct.ThrowIfCancellationRequested();

                var baseName = BaseName(entry.FullName);
                if (baseName.Length == 0 || IsAppleDoubleMetadata(entry.FullName, baseName))
                    continue;

                if (!baseName.EndsWith(".bsp", StringComparison.OrdinalIgnoreCase))
                {
                    skipped++;
                    continue;
                }

                Stream stream;
                try
                {
                    stream = entry.Open();
                }
                catch (InvalidDataException e)
                {
                    throw new InstallException(
                        $"Архив предмета повреждён ({System.IO.Path.GetFileName(archivePath)}): {e.Message}");
                }

                using (stream)
                {
                    var map = Write(stream, entry.Length, baseName[..^4], mapsDir, reporter, ct);
                    maps[map.Name] = map;
                }
            }

            return skipped;
        }
    }

    private static bool IsAppleDoubleMetadata(string fullName, string baseName) =>
        baseName.StartsWith("._", StringComparison.Ordinal)
        || fullName.StartsWith("__MACOSX/", StringComparison.OrdinalIgnoreCase)
        || fullName.StartsWith("__MACOSX\\", StringComparison.OrdinalIgnoreCase)
        || fullName.Contains("/__MACOSX/", StringComparison.OrdinalIgnoreCase)
        || fullName.Contains("\\__MACOSX\\", StringComparison.OrdinalIgnoreCase);

    private static string BaseName(string entryName)
    {
        var slash = entryName.LastIndexOfAny(['/', '\\']);
        return slash >= 0 ? entryName[(slash + 1)..] : entryName;
    }

    private static ExtractedMap Write(
        Stream source,
        long declaredLength,
        string name,
        string mapsDir,
        Reporter? reporter,
        CancellationToken ct)
    {
        if (!MapName.IsSafe(name))
            throw new InstallException($"Недопустимое имя карты в архиве: \"{name}\".");
        if (declaredLength > MaxMapSize)
            throw new InstallException($"Файл карты слишком большой ({Format.Size(declaredLength)}).");

        var header = new byte[BspMagic.Length];
        int headerRead;
        try
        {
            headerRead = source.ReadAtLeast(header, header.Length, throwOnEndOfStream: false);
        }
        catch (InvalidDataException e)
        {
            throw new InstallException($"Архив карты {name}.bsp повреждён: {e.Message}");
        }

        if (headerRead < header.Length || !header.AsSpan().SequenceEqual(BspMagic))
            throw new InstallException($"Файл {name}.bsp не похож на карту Source (нет заголовка VBSP).");

        var destination = System.IO.Path.Combine(mapsDir, name + ".bsp");
        var temp = destination + ".part";
        var replaced = File.Exists(destination);

        reporter?.Status(
            declaredLength > 0
                ? $"Распаковка {name}.bsp ({Format.Size(declaredLength)})…"
                : $"Распаковка {name}.bsp…",
            declaredLength > 0 ? 0 : null);

        var completed = false;
        try
        {
            using (var target = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                target.Write(header);
                long written = header.Length;
                var buffer = new byte[81920];
                var sw = Stopwatch.StartNew();
                var lastReportMs = -100L;
                int read;

                while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    written += read;
                    if (written > MaxMapSize)
                        throw new InstallException($"Файл карты слишком большой (превышен предел {Format.Size(MaxMapSize)}).");

                    target.Write(buffer, 0, read);

                    if (reporter is not null && sw.ElapsedMilliseconds - lastReportMs >= 80)
                    {
                        lastReportMs = sw.ElapsedMilliseconds;
                        if (declaredLength > 0)
                        {
                            var effectiveTotal = Math.Max(declaredLength, written);
                            reporter.Status(
                                $"Распаковка {name}.bsp: {Format.Size(written)} из {Format.Size(effectiveTotal)}",
                                Math.Clamp(written * 100.0 / effectiveTotal, 0, 100));
                        }
                        else
                        {
                            reporter.Status($"Распаковка {name}.bsp: {Format.Size(written)}…", null);
                        }
                    }
                }
            }

            ct.ThrowIfCancellationRequested();
            File.Move(temp, destination, overwrite: true);
            completed = true;
            reporter?.Status($"Распаковка {name}.bsp завершена", 100);
        }
        catch (InvalidDataException e)
        {
            throw new InstallException($"Архив карты {name}.bsp повреждён: {e.Message}");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new InstallException(
                $"Не удалось записать {destination}: {e.Message}\n" +
                "Если карта сейчас загружена в игре, закройте игру или смените карту и повторите.");
        }
        finally
        {
            if (!completed)
                TryDelete(temp);
        }

        return new ExtractedMap(name, destination, new FileInfo(destination).Length, replaced);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception)
        {
            // Nothing sensible to do about a leftover temp file.
        }
    }
}
