using CsgoWorkshop.Core;

namespace Core.Tests;

public class SteamOutputTests
{
    private const string Esc = "\u001b";

    private static (SteamOutput Output, List<string> Lines, List<double> Percents) Create()
    {
        var lines = new List<string>();
        var percents = new List<double>();
        return (new SteamOutput(lines.Add, percents.Add), lines, percents);
    }

    [Fact]
    public void ParsesSuccessGluedToPreviousMessage()
    {
        var (output, lines, _) = Create();

        output.Feed("Downloading item 213240871 ...Success. Downloaded item 213240871 to \"/tmp/c/steamapps/workshop/content/730/213240871\" (12345678 bytes) \n");
        output.Flush();

        Assert.Equal(["Downloading item 213240871 ...", "Success. Downloaded item 213240871 to \"/tmp/c/steamapps/workshop/content/730/213240871\" (12345678 bytes)"], lines);
        Assert.Equal(new SteamDownloadResult(true, 213240871, "/tmp/c/steamapps/workshop/content/730/213240871", null), output.Download);
    }

    [Fact]
    public void ParsesRealSuccessLineWithTrailingShutdownMessage()
    {
        var (output, _, _) = Create();

        output.Feed("Downloading item 243702660 ...\nSuccess. Downloaded item 243702660 to \"/tmp/ws-it/content/steamapps/workshop/content/730/243702660/2223150142816934629_legacy.bin\" (10546197 bytes) Unloading Steam API...OK\n");
        output.Flush();

        Assert.Equal(
            new SteamDownloadResult(true, 243702660, "/tmp/ws-it/content/steamapps/workshop/content/730/243702660/2223150142816934629_legacy.bin", null),
            output.Download);
    }

    [Fact]
    public void ParsesSuccessWithoutTrailingNewline()
    {
        var (output, _, _) = Create();

        output.Feed("Success. Downloaded item 5 to \"C:\\Data\\content\\steamapps\\workshop\\content\\730\\5\" (10 bytes)");
        Assert.Null(output.Download);
        output.Flush();

        Assert.Equal(new SteamDownloadResult(true, 5, "C:\\Data\\content\\steamapps\\workshop\\content\\730\\5", null), output.Download);
    }

    [Fact]
    public void ParsesFailureReason()
    {
        var (output, _, _) = Create();

        output.Feed("Downloading item 1 ...ERROR! Download item 1 failed (File Not Found).\r\n");
        output.Flush();

        Assert.Equal(new SteamDownloadResult(false, 1, null, "File Not Found"), output.Download);
    }

    [Fact]
    public void ParsesRealFailureLineWithTrailingShutdownMessage()
    {
        var (output, _, _) = Create();

        output.Feed("Downloading item 1 ...\nERROR! Download item 1 failed (File Not Found).Unloading Steam API...OK\n");
        output.Flush();

        Assert.Equal(new SteamDownloadResult(false, 1, null, "File Not Found"), output.Download);
    }

    [Fact]
    public void SplitsShutdownMessageGluedToError()
    {
        var (output, lines, _) = Create();

        output.Feed("Downloading item 1 ...\nERROR! Download item 1 failed (File Not Found).Unloading Steam API...OK\n");
        output.Flush();

        Assert.Equal(["Downloading item 1 ...", "ERROR! Download item 1 failed (File Not Found).", "Unloading Steam API...OK"], lines);
    }

    [Fact]
    public void SplitsShutdownMessageSeparatedFromSuccessByASpace()
    {
        var (output, lines, _) = Create();

        output.Feed("Downloading item 1 ...\nSuccess. Downloaded item 1 to \"C:\\x\\1\\2_legacy.bin\" (10 bytes) Unloading Steam API...OK\n");
        output.Flush();

        Assert.Equal(["Downloading item 1 ...", "Success. Downloaded item 1 to \"C:\\x\\1\\2_legacy.bin\" (10 bytes)", "Unloading Steam API...OK"], lines);
        Assert.Equal(new SteamDownloadResult(true, 1, "C:\\x\\1\\2_legacy.bin", null), output.Download);
    }

    [Fact]
    public void StripsAnsiSequences()
    {
        var (output, lines, _) = Create();

        output.Feed($"{Esc}[0m{Esc}[1;32mLogging in user 'anonymous' to Steam Public...OK{Esc}[0m\n");

        Assert.Equal(["Logging in user 'anonymous' to Steam Public...OK"], lines);
    }

    [Fact]
    public void HandlesArbitraryChunkBoundaries()
    {
        const string text = $"{Esc}[0m[  0%] Checking for available updates...\r[----] Verifying installation...\nDownloading item 7 ...Success. Downloaded item 7 to \"/x/7\" (99 bytes) \n";

        var (whole, wholeLines, _) = Create();
        whole.Feed(text);
        whole.Flush();

        var (split, splitLines, _) = Create();
        foreach (var ch in text)
            split.Feed(ch.ToString());
        split.Flush();

        Assert.Equal(wholeLines, splitLines);
        Assert.Equal(whole.Download, split.Download);
        Assert.True(split.Download?.Success);
    }

    [Fact]
    public void SeparatesGluedProgressMessages()
    {
        var (output, lines, _) = Create();

        output.Feed("[  0%] Checking for available updates...[----] Verifying installation...\n");

        Assert.Equal(["[  0%] Checking for available updates...", "[----] Verifying installation..."], lines);
    }

    [Fact]
    public void ReportsPercentButLogsProgressRedrawsOnce()
    {
        var (output, lines, percents) = Create();

        for (var i = 1; i <= 50; i++)
            output.Feed($"[{i * 2,3}%] Downloading update ({i * 1000:N0} of 50,000 KB)...\r");
        output.Flush();

        Assert.Single(lines);
        Assert.Equal(50, percents.Count);
        Assert.Equal(100, percents[^1]);
    }

    [Fact]
    public void CollapsesRepeatedBootstrapperLines()
    {
        var (output, lines, _) = Create();

        output.Feed("[----] Extracting package...\n[----] Extracting package...\n[----] Extracting package...\n[----] Installing update...\n[----] Installing update...\n");

        Assert.Equal(["[----] Extracting package...", "[----] Installing update..."], lines);
    }

    [Fact]
    public void ReportsDownloadProgressPercent()
    {
        var (output, _, percents) = Create();

        output.Feed(" Update state (0x61) downloading, progress: 45.23 (12345 / 27283)\n");

        Assert.Equal([45.23], percents);
    }

    [Fact]
    public void KeepsDistinctMessagesBetweenProgressLines()
    {
        var (output, lines, _) = Create();

        output.Feed("[  1%] Downloading update (1 of 5 KB)...\r");
        output.Feed("Some other message\n");
        output.Feed("[  9%] Downloading update (4 of 5 KB)...\r");

        Assert.Equal(3, lines.Count);
    }

    [Fact]
    public void TranscriptContainsLoggedLinesOnly()
    {
        var (output, _, _) = Create();

        output.Feed("one\n\n\ntwo\r\n");
        output.Flush();

        Assert.Equal("one\ntwo\n", output.Transcript);
    }
}
