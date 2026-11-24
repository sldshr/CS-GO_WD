using CsgoWorkshop.Core;

namespace Core.Tests;

public class MapsFolderTests
{
    [Fact]
    public void AcceptsMapsFolderItself()
    {
        using var temp = new TempDir();
        var maps = temp.Sub("csgo legacy", "csgo", "maps");
        Assert.Equal(maps, MapsFolder.Resolve(maps));
    }

    [Fact]
    public void AcceptsTrailingSeparatorAndQuotes()
    {
        using var temp = new TempDir();
        var maps = temp.Sub("csgo legacy", "csgo", "maps");
        var typed = "\"" + maps + Path.DirectorySeparatorChar + "\"";
        Assert.Equal(Path.TrimEndingDirectorySeparator(maps), Path.TrimEndingDirectorySeparator(MapsFolder.Resolve(typed)));
    }

    [Fact]
    public void DescendsFromCsgoFolder()
    {
        using var temp = new TempDir();
        var maps = temp.Sub("csgo legacy", "csgo", "maps");
        Assert.Equal(maps, MapsFolder.Resolve(Path.GetDirectoryName(maps)));
    }

    [Fact]
    public void DescendsFromGameFolder()
    {
        using var temp = new TempDir();
        var maps = temp.Sub("csgo legacy", "csgo", "maps");
        Assert.Equal(maps, MapsFolder.Resolve(temp.Sub("csgo legacy")));
    }

    [Fact]
    public void RejectsCs2MapsFolderAndSuggestsLegacyPath()
    {
        using var temp = new TempDir();
        var cs2Csgo = temp.Sub("Counter-Strike Global Offensive", "game", "csgo");
        var cs2Maps = temp.Sub("Counter-Strike Global Offensive", "game", "csgo", "maps");
        var legacyMaps = temp.Sub("Counter-Strike Global Offensive", "csgo", "maps");
        File.WriteAllText(Path.Combine(cs2Csgo, "gameinfo.gi"), "\"GameInfo\" {}");

        var ex = Assert.Throws<InstallException>(() => MapsFolder.Resolve(cs2Maps));
        Assert.Contains("CS2", ex.Message);
        Assert.Contains(legacyMaps, ex.Message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void RejectsEmpty(string? input) => Assert.Throws<InstallException>(() => MapsFolder.Resolve(input));

    [Fact]
    public void RejectsMissingFolder()
    {
        using var temp = new TempDir();
        var ex = Assert.Throws<InstallException>(() => MapsFolder.Resolve(Path.Combine(temp.Path, "nope", "maps")));
        Assert.Contains("не найдена", ex.Message);
    }

    [Fact]
    public void RejectsUnrelatedFolder()
    {
        using var temp = new TempDir();
        var other = temp.Sub("Documents");
        var ex = Assert.Throws<InstallException>(() => MapsFolder.Resolve(other));
        Assert.Contains("не папка maps", ex.Message);
    }
}
