using CsgoWorkshop.Core;

namespace Core.Tests;

public class MapLibraryTests
{
    private static InstalledMap Map(string mapsDir, string name, ulong item = 100, long size = 10) =>
        new(item, name, Path.Combine(mapsDir, name + ".bsp"), size, DateTimeOffset.UnixEpoch);

    private static InstalledMap Create(string mapsDir, string name, ulong item = 100)
    {
        var map = Map(mapsDir, name, item);
        File.WriteAllText(map.Path, "VBSP");
        return map;
    }

    [Fact]
    public void EmptyWhenNoManifest()
    {
        using var temp = new TempDir();
        Assert.Empty(new MapLibrary(Path.Combine(temp.Path, "installed.json")).Load());
    }

    [Fact]
    public void AddPersistsBetweenInstances()
    {
        using var temp = new TempDir();
        var manifest = Path.Combine(temp.Path, "sub", "installed.json");
        var maps = temp.Sub("maps");

        new MapLibrary(manifest).Add([Map(maps, "aim_botz", 243702660)]);

        var loaded = Assert.Single(new MapLibrary(manifest).Load());
        Assert.Equal("aim_botz", loaded.Name);
        Assert.Equal(243702660UL, loaded.ItemId);
        Assert.Equal(Path.Combine(maps, "aim_botz.bsp"), loaded.Path);
        Assert.False(File.Exists(manifest + ".tmp"));
    }

    [Fact]
    public void AddingSamePathReplacesOldEntry()
    {
        using var temp = new TempDir();
        var maps = temp.Sub("maps");
        var library = new MapLibrary(Path.Combine(temp.Path, "installed.json"));

        library.Add([Map(maps, "de_test", item: 1, size: 10)]);
        library.Add([Map(maps, "de_test", item: 2, size: 20)]);

        var only = Assert.Single(library.Load());
        Assert.Equal(2UL, only.ItemId);
        Assert.Equal(20, only.Size);
    }

    [Fact]
    public void ReplacedOriginalIsStoredForNewMap()
    {
        using var temp = new TempDir();
        var maps = temp.Sub("maps");
        var library = new MapLibrary(Path.Combine(temp.Path, "installed.json"));

        library.Add([Map(maps, "de_dust2") with { ReplacedOriginal = true }]);

        Assert.True(Assert.Single(new MapLibrary(Path.Combine(temp.Path, "installed.json")).Load()).ReplacedOriginal);
    }

    [Fact]
    public void ReinstallKeepsOriginalReplacedFlag()
    {
        using var temp = new TempDir();
        var maps = temp.Sub("maps");
        var library = new MapLibrary(Path.Combine(temp.Path, "installed.json"));

        library.Add([Map(maps, "replaced", item: 1) with { ReplacedOriginal = true }]);
        library.Add([Map(maps, "replaced", item: 2) with { ReplacedOriginal = true }]);
        library.Add([Map(maps, "fresh", item: 1)]);
        library.Add([Map(maps, "fresh", item: 2) with { ReplacedOriginal = true }]);

        var loaded = library.Load().ToDictionary(m => m.Name);
        Assert.True(loaded["replaced"].ReplacedOriginal);
        Assert.Equal(2UL, loaded["replaced"].ItemId);
        Assert.False(loaded["fresh"].ReplacedOriginal);
        Assert.Equal(2UL, loaded["fresh"].ItemId);
    }

    [Fact]
    public void ManifestWithoutReplacedFlagStillLoads()
    {
        using var temp = new TempDir();
        var manifest = Path.Combine(temp.Path, "installed.json");
        File.WriteAllText(manifest, "[{\"ItemId\":1,\"Name\":\"old\",\"Path\":\"/m/old.bsp\",\"Size\":1,\"InstalledAt\":\"2026-01-01T00:00:00+00:00\"}]");

        Assert.False(Assert.Single(new MapLibrary(manifest).Load()).ReplacedOriginal);
    }

    [Fact]
    public void AddKeepsOtherMaps()
    {
        using var temp = new TempDir();
        var maps = temp.Sub("maps");
        var library = new MapLibrary(Path.Combine(temp.Path, "installed.json"));

        library.Add([Map(maps, "a")]);
        library.Add([Map(maps, "b"), Map(maps, "c")]);

        Assert.Equal(["a", "b", "c"], library.Load().Select(m => m.Name));
    }

    [Fact]
    public void RemoveDeletesFileAndEntry()
    {
        using var temp = new TempDir();
        var maps = temp.Sub("maps");
        var library = new MapLibrary(Path.Combine(temp.Path, "installed.json"));
        var keep = Create(maps, "keep");
        var drop = Create(maps, "drop");
        library.Add([keep, drop]);

        library.Remove(drop);

        Assert.False(File.Exists(drop.Path));
        Assert.True(File.Exists(keep.Path));
        Assert.Equal("keep", Assert.Single(library.Load()).Name);
    }

    [Fact]
    public void RemoveForgetsMapWhoseFileIsAlreadyGone()
    {
        using var temp = new TempDir();
        var maps = temp.Sub("maps");
        var library = new MapLibrary(Path.Combine(temp.Path, "installed.json"));
        var gone = Map(maps, "gone");
        library.Add([gone]);

        library.Remove(gone);

        Assert.Empty(library.Load());
    }

    [Fact]
    public void RemoveForgetsMapWhoseFolderIsAlreadyGone()
    {
        using var temp = new TempDir();
        var library = new MapLibrary(Path.Combine(temp.Path, "installed.json"));
        var gone = Map(Path.Combine(temp.Path, "no-such-folder"), "gone");
        library.Add([gone]);

        library.Remove(gone);

        Assert.Empty(library.Load());
    }

    [Theory]
    [InlineData("..\\evil")]
    [InlineData("a;b")]
    [InlineData("")]
    [InlineData("quo\"te")]
    public void RemoveRefusesUnsafeNames(string name)
    {
        using var temp = new TempDir();
        var maps = temp.Sub("maps");
        var victim = Path.Combine(maps, "victim.bsp");
        File.WriteAllText(victim, "VBSP");
        var library = new MapLibrary(Path.Combine(temp.Path, "installed.json"));

        var forged = new InstalledMap(1, name, victim, 1, DateTimeOffset.UnixEpoch);

        Assert.Throws<InstallException>(() => library.Remove(forged));
        Assert.True(File.Exists(victim));
    }

    [Fact]
    public void RemoveRefusesUnsafeNameEvenWhenPathMatchesIt()
    {
        using var temp = new TempDir();
        var maps = temp.Sub("maps");
        var file = Path.Combine(maps, "a;b.bsp");
        File.WriteAllText(file, "VBSP");
        var library = new MapLibrary(Path.Combine(temp.Path, "installed.json"));

        Assert.Throws<InstallException>(() => library.Remove(new InstalledMap(1, "a;b", file, 1, DateTimeOffset.UnixEpoch)));
        Assert.True(File.Exists(file));
    }

    [Fact]
    public void RemoveRefusesPathThatIsNotTheMapFile()
    {
        using var temp = new TempDir();
        var maps = temp.Sub("maps");
        var other = Path.Combine(maps, "other.txt");
        File.WriteAllText(other, "data");
        var library = new MapLibrary(Path.Combine(temp.Path, "installed.json"));

        var forged = new InstalledMap(1, "other", other, 1, DateTimeOffset.UnixEpoch);

        Assert.Throws<InstallException>(() => library.Remove(forged));
        Assert.True(File.Exists(other));
    }

    [Fact]
    public void RemoveRefusesRelativePath()
    {
        using var temp = new TempDir();
        var library = new MapLibrary(Path.Combine(temp.Path, "installed.json"));

        var forged = new InstalledMap(1, "x", "x.bsp", 1, DateTimeOffset.UnixEpoch);

        Assert.Throws<InstallException>(() => library.Remove(forged));
    }

    [Fact]
    public void BrokenManifestIsTreatedAsEmptyAndRewritten()
    {
        using var temp = new TempDir();
        var manifest = Path.Combine(temp.Path, "installed.json");
        File.WriteAllText(manifest, "{ not json");
        var maps = temp.Sub("maps");
        var library = new MapLibrary(manifest);

        Assert.Empty(library.Load());

        library.Add([Map(maps, "fresh")]);

        Assert.Equal("fresh", Assert.Single(new MapLibrary(manifest).Load()).Name);
    }

    [Fact]
    public void NullEntriesInManifestAreIgnored()
    {
        using var temp = new TempDir();
        var manifest = Path.Combine(temp.Path, "installed.json");
        File.WriteAllText(manifest, "[null, {\"ItemId\":1,\"Name\":\"ok\",\"Path\":\"/m/ok.bsp\",\"Size\":1,\"InstalledAt\":\"2026-01-01T00:00:00+00:00\"}]");

        Assert.Equal("ok", Assert.Single(new MapLibrary(manifest).Load()).Name);
    }
}
