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
        var typed = "\"" + maps + System.IO.Path.DirectorySeparatorChar + "\"";
        Assert.Equal(System.IO.Path.TrimEndingDirectorySeparator(maps), System.IO.Path.TrimEndingDirectorySeparator(MapsFolder.Resolve(typed)));
    }

    [Fact]
    public void DescendsFromCsgoFolder()
    {
        using var temp = new TempDir();
        var maps = temp.Sub("csgo legacy", "csgo", "maps");
        Assert.Equal(maps, MapsFolder.Resolve(System.IO.Path.GetDirectoryName(maps)));
    }

    [Fact]
    public void DescendsFromGameFolder()
    {
        using var temp = new TempDir();
        var maps = temp.Sub("csgo legacy", "csgo", "maps");
        Assert.Equal(maps, MapsFolder.Resolve(temp.Sub("csgo legacy")));
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
        var ex = Assert.Throws<InstallException>(() => MapsFolder.Resolve(System.IO.Path.Combine(temp.Path, "nope", "maps")));
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
