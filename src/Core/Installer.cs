namespace CsgoWorkshop.Core;

public sealed record InstallRequest(ulong ItemId, string MapsDir);

public sealed record InstallResult(
    string MapName,
    string Command,
    string MapPath,
    long Size,
    bool Replaced,
    IReadOnlyList<string> OtherMaps);

public sealed class Installer(SteamCmd steam, MapLibrary library)
{
    public async Task<InstallResult> RunAsync(InstallRequest request, Reporter reporter, CancellationToken ct)
    {
        reporter.Log($"Предмет Workshop: {request.ItemId}");
        reporter.Log($"Папка maps: {request.MapsDir}");

        reporter.Status($"Проверка предмета {request.ItemId} в Steam Workshop…", null);
        var metadata = await WorkshopMetadata.TryFetchAsync(request.ItemId, ct);
        WorkshopMetadata.ValidateForCsgo(metadata);

        if (metadata is { Result: 1 })
        {
            if (!string.IsNullOrWhiteSpace(metadata.Title))
                reporter.Log($"Название: {metadata.Title}");
            if (metadata.FileSize is > 0)
                reporter.Log($"Размер в Workshop: {Format.Size(metadata.FileSize.Value)}");
        }

        await steam.EnsureInstalledAsync(reporter, ct);
        await steam.PrimeAsync(reporter, ct);
        var itemDir = await steam.DownloadItemAsync(request.ItemId, reporter, ct, metadata);

        ct.ThrowIfCancellationRequested();
        reporter.Status("Распаковка карты…", null);

        ExtractResult extracted;
        try
        {
            extracted = MapExtractor.Extract(itemDir, request.MapsDir, reporter, ct);
        }
        finally
        {
            steam.WipeWorkshopCache();
        }

        foreach (var map in extracted.Maps)
        {
            reporter.Log($"Записано: {map.Path} ({Format.Size(map.Size)})" + (map.Replaced ? ", существующий файл заменён" : ""));
        }

        if (extracted.SkippedEntries > 0)
            reporter.Log($"В архиве были другие файлы ({extracted.SkippedEntries}), они пропущены: нужен только .bsp.");

        try
        {
            var now = DateTimeOffset.UtcNow;
            library.Add(extracted.Maps.Select(m => new InstalledMap(request.ItemId, m.Name, m.Path, m.Size, now, m.Replaced)));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            reporter.Log($"Не удалось обновить список скачанных карт: {e.Message}");
        }

        var main = extracted.Maps[0];
        var others = extracted.Maps.Skip(1).Select(m => m.Name).ToList();

        return new InstallResult(main.Name, MapName.Command(main.Name), main.Path, main.Size, main.Replaced, others);
    }
}
