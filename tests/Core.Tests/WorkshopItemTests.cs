using CsgoWorkshop.Core;

namespace Core.Tests;

public class WorkshopItemTests
{
    [Theory]
    [InlineData("213240871", 213240871UL)]
    [InlineData("  213240871  ", 213240871UL)]
    [InlineData("\"213240871\"", 213240871UL)]
    [InlineData("https://steamcommunity.com/sharedfiles/filedetails/?id=213240871", 213240871UL)]
    [InlineData("https://steamcommunity.com/sharedfiles/filedetails/?id=213240871&searchtext=aim", 213240871UL)]
    [InlineData("https://steamcommunity.com/sharedfiles/filedetails/?l=russian&id=213240871", 213240871UL)]
    [InlineData("https://steamcommunity.com/workshop/filedetails/?id=213240871", 213240871UL)]
    [InlineData("steamcommunity.com/sharedfiles/filedetails/?id=213240871#description", 213240871UL)]
    [InlineData("steam://url/CommunityFilePage/213240871", 213240871UL)]
    [InlineData("id=213240871", 213240871UL)]
    public void Parses(string input, ulong expected)
    {
        Assert.True(WorkshopItem.TryParseId(input, out var id));
        Assert.Equal(expected, id);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc")]
    [InlineData("0")]
    [InlineData("123abc")]
    [InlineData("12 34")]
    [InlineData("-5")]
    [InlineData("99999999999999999999999")]
    [InlineData("https://steamcommunity.com/app/730/workshop/")]
    [InlineData("https://steamcommunity.com/sharedfiles/filedetails/?foo=1")]
    public void Rejects(string? input)
    {
        Assert.False(WorkshopItem.TryParseId(input, out _));
    }
}
