using System.Text;
using CsgoWorkshop.Core;

namespace Core.Tests;

public sealed class OnlineFactAttribute : FactAttribute
{
    public OnlineFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("CSGO_WS_ONLINE") != "1")
            Skip = "Talks to Steam; set CSGO_WS_ONLINE=1 to run.";
    }
}

// One shared steamcmd installation (set CSGO_WS_IT_DIR to reuse it between runs).
public sealed class SteamFixture : IDisposable
{
    private readonly bool _ownsDir;

    public string Root { get; }
    public SteamCmd Steam { get; }
    public List<string> Log { get; } = [];

    public SteamFixture()
    {
        var configured = Environment.GetEnvironmentVariable("CSGO_WS_IT_DIR");
        _ownsDir = string.IsNullOrEmpty(configured);
        Root = configured is { Length: > 0 } ? configured : Path.Combine(Path.GetTempPath(), "csgo-ws-it-" + Guid.NewGuid().ToString("N"));
        Steam = new SteamCmd(Path.Combine(Root, "steamcmd"), Path.Combine(Root, "home"), Path.Combine(Root, "content"));
    }

    public Reporter Reporter => new()
    {
        Log = line =>
        {
            lock (Log)
            {
                Log.Add(line);
                Directory.CreateDirectory(Root);
                File.AppendAllText(Path.Combine(Root, "it.log"), line + "\n");
            }
        },
        Status = (_, _) => { },
    };

    public void Dispose()
    {
        if (_ownsDir && Directory.Exists(Root))
            Directory.Delete(Root, recursive: true);
    }
}

public class OnlineTests(SteamFixture fixture) : IClassFixture<SteamFixture>
{
    [OnlineFact]
    public async Task DownloadsRealItemAndInstallsBsp()
    {
        using var temp = new TempDir();
        var maps = temp.Sub("csgo legacy", "csgo", "maps");

        var library = new MapLibrary(Path.Combine(temp.Path, "installed.json"));
        var request = new InstallRequest(243702660, MapsFolder.Resolve(maps));
        var result = await new Installer(fixture.Steam, library).RunAsync(request, fixture.Reporter, CancellationToken.None);

        Assert.True(File.Exists(result.MapPath));
        Assert.Equal(Path.Combine(maps, result.MapName + ".bsp"), result.MapPath);
        Assert.Equal("map " + result.MapName, result.Command);

        var recorded = Assert.Single(library.Load());
        Assert.Equal(243702660UL, recorded.ItemId);
        Assert.Equal(result.MapPath, recorded.Path);
        Assert.Equal(result.Size, recorded.Size);

        var header = new byte[4];
        using (var file = File.OpenRead(result.MapPath))
            file.ReadExactly(header);
        Assert.Equal("VBSP", Encoding.ASCII.GetString(header));

        Assert.False(Directory.Exists(Path.Combine(fixture.Root, "content", "steamapps", "workshop")));
        Assert.Empty(Directory.GetFiles(maps, "*.part"));
    }

    [OnlineFact]
    public async Task ReportsMissingItem()
    {
        using var temp = new TempDir();
        var maps = temp.Sub("maps");

        var library = new MapLibrary(Path.Combine(temp.Path, "installed.json"));
        var request = new InstallRequest(1, maps);
        var ex = await Assert.ThrowsAsync<InstallException>(() => new Installer(fixture.Steam, library).RunAsync(request, fixture.Reporter, CancellationToken.None));

        Assert.Contains("не найден", ex.Message);
        Assert.Empty(Directory.GetFiles(maps));
        Assert.Empty(library.Load());
    }

    [OnlineFact]
    public async Task CancelStopsSteamcmd()
    {
        using var temp = new TempDir();
        var maps = temp.Sub("maps");
        using var cts = new CancellationTokenSource();

        var library = new MapLibrary(Path.Combine(temp.Path, "installed.json"));
        var request = new InstallRequest(213240871, maps);
        var task = new Installer(fixture.Steam, library).RunAsync(request, fixture.Reporter, cts.Token);
        await Task.Delay(TimeSpan.FromSeconds(2));
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task.WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.Empty(Directory.GetFiles(maps));
    }
}
