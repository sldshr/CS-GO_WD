using System.Globalization;
using System.Text.Json;

namespace CsgoWorkshop.Core;

public sealed record WorkshopItemInfo(
    ulong ItemId,
    int Result,
    int ConsumerAppId,
    string? Title,
    string? FileName,
    long? FileSize,
    bool IsCs2Only,
    bool IsCollection = false);

public static class WorkshopMetadata
{
    private const int CsgoAppId = 730;
    private const string FileDetailsUrl = "https://api.steampowered.com/ISteamRemoteStorage/GetPublishedFileDetails/v1/";
    private const string CollectionDetailsUrl = "https://api.steampowered.com/ISteamRemoteStorage/GetCollectionDetails/v1/";

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(6) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("csgo-workshop/1.2");
        return client;
    }

    public static async Task<WorkshopItemInfo?> TryFetchAsync(ulong itemId, CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(5));
        var linked = timeoutCts.Token;

        try
        {
            using var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["itemcount"] = "1",
                ["publishedfileids[0]"] = itemId.ToString(CultureInfo.InvariantCulture),
            });

            using var response = await Http.PostAsync(FileDetailsUrl, content, linked);
            if (!response.IsSuccessStatusCode)
                return null;

            var json = await response.Content.ReadAsStringAsync(linked);
            var info = ParseFileDetails(itemId, json);

            if (info is { Result: 9 } && await IsCollectionAsync(itemId, linked))
                return info with { IsCollection = true };

            return info;
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            // Metadata is best-effort; steamcmd still performs the authoritative download.
            return null;
        }
    }

    public static void ValidateForCsgo(WorkshopItemInfo? info)
    {
        if (info is null)
            return;

        if (info.IsCollection)
        {
            throw new InstallException(
                $"Предмет {info.ItemId} — это коллекция Workshop, а не отдельная карта. " +
                "Откройте нужную карту внутри коллекции и вставьте ссылку на неё.");
        }

        if (info.Result != 1)
            return;

        if (info.ConsumerAppId is > 0 and not CsgoAppId)
        {
            var titlePart = !string.IsNullOrWhiteSpace(info.Title) ? $" («{info.Title}»)" : "";
            throw new InstallException(
                $"Предмет {info.ItemId}{titlePart} относится к другой игре (AppID {info.ConsumerAppId}), а не к CS:GO ({CsgoAppId}).");
        }

        if (info.IsCs2Only)
        {
            var titlePart = !string.IsNullOrWhiteSpace(info.Title) ? $" «{info.Title}»" : "";
            throw new InstallException(
                $"Предмет{titlePart} собран для CS2 (.vpk) и не содержит карты для CS:GO Legacy. " +
                "Нужна версия карты, загруженная автором для CS:GO.");
        }
    }

    public static WorkshopItemInfo? ParseFileDetails(ulong itemId, string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("response", out var response)
                || !response.TryGetProperty("publishedfiledetails", out var details)
                || details.ValueKind != JsonValueKind.Array
                || details.GetArrayLength() == 0)
            {
                return null;
            }

            var item = details[0];
            var result = ReadInt32(item, "result") ?? 0;
            var consumerAppId = ReadInt32(item, "consumer_app_id") ?? 0;
            var title = ReadString(item, "title")?.Trim();
            var fileName = ReadString(item, "filename")?.Trim();
            var fileUrl = ReadString(item, "file_url")?.Trim();
            var fileSize = ReadInt64(item, "file_size");
            if (fileSize is <= 0)
                fileSize = null;

            var hasCs2Tag = false;
            if (item.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Array)
            {
                foreach (var tagObj in tags.EnumerateArray())
                {
                    var tag = ReadString(tagObj, "tag");
                    if (string.Equals(tag, "Cs2", StringComparison.OrdinalIgnoreCase))
                    {
                        hasCs2Tag = true;
                        break;
                    }
                }
            }

            var isCs2Only = result == 1
                            && consumerAppId == CsgoAppId
                            && hasCs2Tag
                            && string.IsNullOrEmpty(fileName)
                            && string.IsNullOrEmpty(fileUrl);

            return new WorkshopItemInfo(
                itemId,
                result,
                consumerAppId,
                string.IsNullOrEmpty(title) ? null : title,
                string.IsNullOrEmpty(fileName) ? null : fileName,
                fileSize,
                isCs2Only);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static bool ParseIsCollection(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("response", out var response)
                || !response.TryGetProperty("collectiondetails", out var details)
                || details.ValueKind != JsonValueKind.Array
                || details.GetArrayLength() == 0)
            {
                return false;
            }

            var first = details[0];
            return ReadInt32(first, "result") == 1
                   && first.TryGetProperty("children", out var children)
                   && children.ValueKind == JsonValueKind.Array
                   && children.GetArrayLength() > 0;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static async Task<bool> IsCollectionAsync(ulong itemId, CancellationToken ct)
    {
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["collectioncount"] = "1",
            ["publishedfileids[0]"] = itemId.ToString(CultureInfo.InvariantCulture),
        });

        using var response = await Http.PostAsync(CollectionDetailsUrl, content, ct);
        if (!response.IsSuccessStatusCode)
            return false;

        var json = await response.Content.ReadAsStringAsync(ct);
        return ParseIsCollection(json);
    }

    private static string? ReadString(JsonElement obj, string property) =>
        obj.TryGetProperty(property, out var val) && val.ValueKind == JsonValueKind.String
            ? val.GetString()
            : null;

    private static int? ReadInt32(JsonElement obj, string property)
    {
        if (!obj.TryGetProperty(property, out var val))
            return null;

        return val.ValueKind switch
        {
            JsonValueKind.Number when val.TryGetInt32(out var n) => n,
            JsonValueKind.String when int.TryParse(val.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var s) => s,
            _ => null,
        };
    }

    private static long? ReadInt64(JsonElement obj, string property)
    {
        if (!obj.TryGetProperty(property, out var val))
            return null;

        return val.ValueKind switch
        {
            JsonValueKind.Number when val.TryGetInt64(out var n) => n,
            JsonValueKind.String when long.TryParse(val.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var s) => s,
            _ => null,
        };
    }
}
