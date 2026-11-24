using CsgoWorkshop.Core;

namespace Core.Tests;

public class MapNameTests
{
    [Theory]
    [InlineData("de_dust2")]
    [InlineData("aim_botz")]
    [InlineData("surf_kitsune-v2")]
    [InlineData("kz_map.v1")]
    [InlineData("Карта_1")]
    [InlineData("Mirage (v2)")]
    public void AcceptsOrdinaryNames(string name) => Assert.True(MapName.IsSafe(name));

    [Theory]
    [InlineData("")]
    [InlineData("a;quit")]
    [InlineData("a\"b")]
    [InlineData("a\nb")]
    [InlineData("a\rb")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("..")]
    [InlineData(".hidden")]
    [InlineData("trailing.")]
    [InlineData(" lead")]
    [InlineData("trail ")]
    [InlineData("a$b")]
    [InlineData("a%b")]
    [InlineData("a`b")]
    [InlineData("a|b")]
    [InlineData("a&b")]
    [InlineData("a//b")]
    public void RejectsDangerousNames(string name) => Assert.False(MapName.IsSafe(name));

    [Fact]
    public void RejectsOverlongNames() => Assert.False(MapName.IsSafe(new string('a', 129)));

    [Theory]
    [InlineData("de_dust2", "map de_dust2")]
    [InlineData("aim_botz", "map aim_botz")]
    [InlineData("Mirage (v2)", "map \"Mirage (v2)\"")]
    [InlineData("Карта_1", "map \"Карта_1\"")]
    public void BuildsCommand(string name, string expected) => Assert.Equal(expected, MapName.Command(name));
}
