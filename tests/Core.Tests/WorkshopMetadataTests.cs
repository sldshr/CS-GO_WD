using CsgoWorkshop.Core;

namespace Core.Tests;

public class WorkshopMetadataTests
{
    [Fact]
    public void ParsesLegacyCsgoMapMetadata()
    {
        const string json = """
        {
          "response": {
            "result": 1,
            "publishedfiledetails": [
              {
                "publishedfileid": "243702660",
                "result": 1,
                "consumer_app_id": 730,
                "filename": "mymaps/aim_botz.bsp",
                "file_size": "10546197",
                "file_url": "https://cdn.steamusercontent.com/ugc/2223150142816934629/B503/",
                "title": "Aim Botz - Training",
                "tags": [{ "tag": "Custom" }, { "tag": "Map" }]
              }
            ]
          }
        }
        """;

        var info = WorkshopMetadata.ParseFileDetails(243702660, json);

        Assert.NotNull(info);
        Assert.Equal(1, info.Result);
        Assert.Equal(730, info.ConsumerAppId);
        Assert.Equal("Aim Botz - Training", info.Title);
        Assert.Equal(10_546_197L, info.FileSize);
        Assert.False(info.IsCs2Only);
        WorkshopMetadata.ValidateForCsgo(info);
    }

    [Fact]
    public void DetectsAndRejectsCs2OnlyWorkshopMap()
    {
        const string json = """
        {
          "response": {
            "result": 1,
            "publishedfiledetails": [
              {
                "publishedfileid": "3070244462",
                "result": 1,
                "consumer_app_id": 730,
                "filename": "",
                "file_size": 126830394,
                "file_url": "",
                "title": "Aim Botz (CS2)",
                "tags": [{ "tag": "Cs2" }, { "tag": "Map" }]
              }
            ]
          }
        }
        """;

        var info = WorkshopMetadata.ParseFileDetails(3070244462, json);

        Assert.NotNull(info);
        Assert.True(info.IsCs2Only);
        var ex = Assert.Throws<InstallException>(() => WorkshopMetadata.ValidateForCsgo(info));
        Assert.Contains("CS2", ex.Message);
    }

    [Fact]
    public void RejectsWorkshopItemFromDifferentGame()
    {
        var info = new WorkshopItemInfo(104691717, 1, 4000, "PAC3", null, 8708763, IsCs2Only: false);

        var ex = Assert.Throws<InstallException>(() => WorkshopMetadata.ValidateForCsgo(info));
        Assert.Contains("4000", ex.Message);
    }

    [Fact]
    public void RejectsWorkshopCollection()
    {
        const string collectionJson = """
        {
          "response": {
            "result": 1,
            "collectiondetails": [
              {
                "publishedfileid": "12345",
                "result": 1,
                "children": [{ "publishedfileid": "243702660" }]
              }
            ]
          }
        }
        """;

        Assert.True(WorkshopMetadata.ParseIsCollection(collectionJson));

        var info = new WorkshopItemInfo(12345, 9, 0, null, null, null, IsCs2Only: false, IsCollection: true);
        var ex = Assert.Throws<InstallException>(() => WorkshopMetadata.ValidateForCsgo(info));
        Assert.Contains("коллекция", ex.Message);
    }
}
