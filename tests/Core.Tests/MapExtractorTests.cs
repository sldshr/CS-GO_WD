using System.IO.Compression;
using System.Text;
using CsgoWorkshop.Core;

namespace Core.Tests;

public class MapExtractorTests
{
    private static byte[] FakeBsp(int size = 2048)
    {
        var data = new byte[size];
        Encoding.ASCII.GetBytes("VBSP").CopyTo(data, 0);
        BitConverter.GetBytes(21).CopyTo(data, 4);
        new Random(42).NextBytes(data.AsSpan(8));
        return data;
    }

    private static void WriteZip(string path, params (string Name, byte[] Data)[] entries)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, data) in entries)
        {
            using var stream = zip.CreateEntry(name, CompressionLevel.Optimal).Open();
            stream.Write(data);
        }
    }

    [Fact]
    public void ExtractsBspFromBin()
    {
        using var temp = new TempDir();
        var item = temp.Sub("item");
        var maps = temp.Sub("maps");
        var bsp = FakeBsp();
        WriteZip(Path.Combine(item, "123_legacy.bin"), ("aim_botz.bsp", bsp));

        var percents = new List<double?>();
        var reporter = new Reporter { Status = (_, p) => percents.Add(p) };
        var result = MapExtractor.Extract(item, maps, reporter);

        var map = Assert.Single(result.Maps);
        Assert.Equal("aim_botz", map.Name);
        Assert.False(map.Replaced);
        Assert.Equal(Path.Combine(maps, "aim_botz.bsp"), map.Path);
        Assert.Equal(bsp, File.ReadAllBytes(map.Path));
        Assert.Equal(bsp.Length, map.Size);
        Assert.Equal(0, result.SkippedEntries);
        Assert.Empty(Directory.GetFiles(maps, "*.part"));
        Assert.Equal(100.0, percents[^1]);
    }

    [Fact]
    public void IgnoresMacOsAppleDoubleMetadataEntries()
    {
        using var temp = new TempDir();
        var item = temp.Sub("item");
        var maps = temp.Sub("maps");
        var bsp = FakeBsp();
        WriteZip(
            Path.Combine(item, "mac.bin"),
            ("aim_botz.bsp", bsp),
            ("__MACOSX/._aim_botz.bsp", [0, 5, 22, 7]),
            ("._aim_botz.bsp", [0, 5, 22, 7]));

        var result = MapExtractor.Extract(item, maps);

        var map = Assert.Single(result.Maps);
        Assert.Equal("aim_botz", map.Name);
        Assert.Equal(0, result.SkippedEntries);
    }

    [Fact]
    public void CleansUpPartFileWhenArchiveStreamIsCorrupted()
    {
        using var temp = new TempDir();
        var item = temp.Sub("item");
        var maps = temp.Sub("maps");
        var binPath = Path.Combine(item, "corrupt.bin");
        WriteZip(binPath, ("broken.bsp", FakeBsp(65536)));

        // Corrupt compressed payload bytes near the middle of the ZIP file while keeping central directory intact.
        var bytes = File.ReadAllBytes(binPath);
        for (var i = 40; i < Math.Min(120, bytes.Length / 2); i++)
            bytes[i] ^= 0xFF;
        File.WriteAllBytes(binPath, bytes);

        Assert.Throws<InstallException>(() => MapExtractor.Extract(item, maps));
        Assert.Empty(Directory.GetFiles(maps));
    }

    [Fact]
    public void RespectsCancellationToken()
    {
        using var temp = new TempDir();
        var item = temp.Sub("item");
        var maps = temp.Sub("maps");
        WriteZip(Path.Combine(item, "1.bin"), ("aim_botz.bsp", FakeBsp()));

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() => MapExtractor.Extract(item, maps, ct: cts.Token));
        Assert.Empty(Directory.GetFiles(maps));
    }

    [Fact]
    public void ReplacesExistingMap()
    {
        using var temp = new TempDir();
        var item = temp.Sub("item");
        var maps = temp.Sub("maps");
        File.WriteAllBytes(Path.Combine(maps, "aim_botz.bsp"), [1, 2, 3]);
        var bsp = FakeBsp(4096);
        WriteZip(Path.Combine(item, "1.bin"), ("aim_botz.bsp", bsp));

        var map = Assert.Single(MapExtractor.Extract(item, maps).Maps);

        Assert.True(map.Replaced);
        Assert.Equal(bsp, File.ReadAllBytes(Path.Combine(maps, "aim_botz.bsp")));
    }

    [Fact]
    public void FlattensSubfoldersInArchive()
    {
        using var temp = new TempDir();
        var item = temp.Sub("item");
        var maps = temp.Sub("maps");
        WriteZip(Path.Combine(item, "1.bin"), ("maps/de_test.bsp", FakeBsp()));

        var map = Assert.Single(MapExtractor.Extract(item, maps).Maps);

        Assert.Equal("de_test", map.Name);
        Assert.True(File.Exists(Path.Combine(maps, "de_test.bsp")));
    }

    [Theory]
    [InlineData("../../evil.bsp")]
    [InlineData("..\\..\\evil.bsp")]
    [InlineData("/etc/evil.bsp")]
    [InlineData("C:\\Windows\\evil.bsp")]
    public void NeverWritesOutsideMapsFolder(string entryName)
    {
        using var temp = new TempDir();
        var item = temp.Sub("item");
        var maps = temp.Sub("deep", "er", "maps");
        WriteZip(Path.Combine(item, "1.bin"), (entryName, FakeBsp()));

        var map = Assert.Single(MapExtractor.Extract(item, maps).Maps);

        Assert.Equal("evil", map.Name);
        Assert.Equal(maps, Path.GetDirectoryName(map.Path));
        Assert.Equal([map.Path], Directory.GetFiles(temp.Path, "evil.bsp", SearchOption.AllDirectories));
    }

    [Fact]
    public void RejectsConsoleInjectionInName()
    {
        using var temp = new TempDir();
        var item = temp.Sub("item");
        var maps = temp.Sub("maps");
        WriteZip(Path.Combine(item, "1.bin"), ("x;quit.bsp", FakeBsp()));

        Assert.Throws<InstallException>(() => MapExtractor.Extract(item, maps));
        Assert.Empty(Directory.GetFiles(maps));
    }

    [Fact]
    public void RejectsNonBspContent()
    {
        using var temp = new TempDir();
        var item = temp.Sub("item");
        var maps = temp.Sub("maps");
        WriteZip(Path.Combine(item, "1.bin"), ("fake.bsp", Encoding.ASCII.GetBytes("not a map at all")));

        var ex = Assert.Throws<InstallException>(() => MapExtractor.Extract(item, maps));
        Assert.Contains("VBSP", ex.Message);
        Assert.Empty(Directory.GetFiles(maps));
    }

    [Fact]
    public void CountsSkippedFiles()
    {
        using var temp = new TempDir();
        var item = temp.Sub("item");
        var maps = temp.Sub("maps");
        WriteZip(Path.Combine(item, "1.bin"), ("a.bsp", FakeBsp()), ("a.nav", [1, 2, 3]), ("readme.txt", [4]));

        var result = MapExtractor.Extract(item, maps);

        Assert.Single(result.Maps);
        Assert.Equal(2, result.SkippedEntries);
        Assert.Equal(["a.bsp"], Directory.GetFiles(maps).Select(Path.GetFileName));
    }

    [Fact]
    public void ExtractsEveryBspAndKeepsArchiveOrder()
    {
        using var temp = new TempDir();
        var item = temp.Sub("item");
        var maps = temp.Sub("maps");
        WriteZip(Path.Combine(item, "1.bin"), ("zeta.bsp", FakeBsp()), ("alpha.bsp", FakeBsp(4096)));

        var result = MapExtractor.Extract(item, maps);

        Assert.Equal(["zeta", "alpha"], result.Maps.Select(m => m.Name));
    }

    [Fact]
    public void CopiesRawBsp()
    {
        using var temp = new TempDir();
        var item = temp.Sub("item");
        var maps = temp.Sub("maps");
        var bsp = FakeBsp();
        File.WriteAllBytes(Path.Combine(item, "gm_raw.bsp"), bsp);

        var map = Assert.Single(MapExtractor.Extract(item, maps).Maps);

        Assert.Equal("gm_raw", map.Name);
        Assert.Equal(bsp, File.ReadAllBytes(Path.Combine(maps, "gm_raw.bsp")));
    }

    [Fact]
    public void ExplainsCs2OnlyItems()
    {
        using var temp = new TempDir();
        var item = temp.Sub("item");
        var maps = temp.Sub("maps");
        File.WriteAllBytes(Path.Combine(item, "de_new.vpk"), [1, 2, 3]);

        var ex = Assert.Throws<InstallException>(() => MapExtractor.Extract(item, maps));
        Assert.Contains("CS2", ex.Message);
    }

    [Fact]
    public void RejectsBinThatIsNotZip()
    {
        using var temp = new TempDir();
        var item = temp.Sub("item");
        var maps = temp.Sub("maps");
        File.WriteAllBytes(Path.Combine(item, "1.bin"), Encoding.ASCII.GetBytes("definitely not a zip file"));

        var ex = Assert.Throws<InstallException>(() => MapExtractor.Extract(item, maps));
        Assert.Contains("ZIP", ex.Message);
    }

    [Fact]
    public void RejectsEmptyItem()
    {
        using var temp = new TempDir();
        var item = temp.Sub("item");
        var maps = temp.Sub("maps");

        Assert.Throws<InstallException>(() => MapExtractor.Extract(item, maps));
    }

    [Fact]
    public void RejectsMissingFolders()
    {
        using var temp = new TempDir();
        var item = temp.Sub("item");

        Assert.Throws<InstallException>(() => MapExtractor.Extract(Path.Combine(temp.Path, "none"), item));
        Assert.Throws<InstallException>(() => MapExtractor.Extract(item, Path.Combine(temp.Path, "none")));
    }
}
