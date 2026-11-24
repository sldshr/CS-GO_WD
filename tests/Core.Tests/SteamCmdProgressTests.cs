using CsgoWorkshop.Core;

namespace Core.Tests;

public class SteamCmdProgressTests
{
    [Fact]
    public void MeasuresLargestStagingBucketWithoutDoubleCountingAndIgnoresMetadata()
    {
        using var temp = new TempDir();
        var dir = temp.Sub("steamcmd");
        var home = temp.Sub("home");
        var content = temp.Sub("content");
        var steam = new SteamCmd(dir, home, content);

        var ugcDownload = temp.Sub("home", "Steam", "userdata", "anonymous", "ugc", "download", "999");
        var ugcReferenced = temp.Sub("home", "Steam", "userdata", "anonymous", "ugc", "referenced", "999");
        var wsDownloads = temp.Sub("content", "steamapps", "workshop", "downloads");

        File.WriteAllBytes(Path.Combine(ugcDownload, "data.vdf"), new byte[200]);
        File.WriteAllBytes(Path.Combine(wsDownloads, "state_730.patch"), new byte[64]);
        Assert.Equal(0, steam.MeasureDownloadBytes(243702660));

        File.WriteAllBytes(Path.Combine(ugcDownload, "map.bsp.download"), new byte[4096]);
        Assert.Equal(4096, steam.MeasureDownloadBytes(243702660));

        // When steamcmd copies from ugc/referenced to content/730/<id>, MeasureDownloadBytes must not sum both!
        File.Delete(Path.Combine(ugcDownload, "map.bsp.download"));
        File.WriteAllBytes(Path.Combine(ugcReferenced, "map.bsp"), new byte[10_000]);
        var finalDir = steam.ItemDir(243702660);
        Directory.CreateDirectory(finalDir);
        File.WriteAllBytes(Path.Combine(finalDir, "999_legacy.bin"), new byte[6_000]);

        Assert.Equal(10_000, steam.MeasureDownloadBytes(243702660));
    }

    [Fact]
    public void WipeWorkshopCacheRemovesAllWorkshopAndUgcCaches()
    {
        using var temp = new TempDir();
        var dir = temp.Sub("steamcmd");
        var home = temp.Sub("home");
        var content = temp.Sub("content");
        var steam = new SteamCmd(dir, home, content);

        var wsCache = temp.Sub("content", "steamapps", "workshop", "content", "730", "1");
        var linuxUgc = temp.Sub("home", "Steam", "userdata", "anonymous", "ugc", "referenced", "1");
        var winUgc = temp.Sub("steamcmd", "userdata", "anonymous", "ugc", "referenced", "1");

        File.WriteAllBytes(Path.Combine(wsCache, "a.bin"), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(linuxUgc, "a.bsp"), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(winUgc, "a.bsp"), [1, 2, 3]);

        steam.WipeWorkshopCache();

        Assert.False(Directory.Exists(Path.Combine(content, "steamapps", "workshop")));
        Assert.False(Directory.Exists(Path.Combine(home, "Steam", "userdata", "anonymous", "ugc")));
        Assert.False(Directory.Exists(Path.Combine(dir, "userdata", "anonymous", "ugc")));
    }
}
