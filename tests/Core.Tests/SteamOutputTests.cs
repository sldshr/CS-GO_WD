using CsgoWorkshop.Core;

namespace Core.Tests;

public class SteamOutputTests
{
    private const string Esc = "\u001b";

    private static (SteamOutput Output, List<string> Lines, List<double?> Percents, List<SteamStage> Stages) Create()
    {
        var lines = new List<string>();
        var percents = new List<double?>();
        var stages = new List<SteamStage>();
        return (new SteamOutput(lines.Add, percents.Add, stages.Add), lines, percents, stages);
    }

    [Fact]
    public void ParsesSuccessGluedToPreviousMessage()
    {
        var (output, lines, _, _) = Create();

        output.Feed("Downloading item 213240871 ...Success. Downloaded item 213240871 to \"/tmp/c/steamapps/workshop/content/730/213240871\" (12345678 bytes) \n");
        output.Flush();

        Assert.Equal(["Downloading item 213240871 ...", "Success. Downloaded item 213240871 to \"/tmp/c/steamapps/workshop/content/730/213240871\" (12345678 bytes)"], lines);
        Assert.Equal(new SteamDownloadResult(true, 213240871, "/tmp/c/steamapps/workshop/content/730/213240871", null), output.Download);
    }

    [Fact]
    public void ParsesRealSuccessLineWithTrailingShutdownMessage()
    {
        var (output, _, _, _) = Create();

        output.Feed("Downloading item 243702660 ...\nSuccess. Downloaded item 243702660 to \"/tmp/ws-it/content/steamapps/workshop/content/730/243702660/2223150142816934629_legacy.bin\" (10546197 bytes) Unloading Steam API...OK\n");
        output.Flush();

        Assert.Equal(
            new SteamDownloadResult(true, 243702660, "/tmp/ws-it/content/steamapps/workshop/content/730/243702660/2223150142816934629_legacy.bin", null),
            output.Download);
    }

    [Fact]
    public void ParsesSuccessWithoutTrailingNewline()
    {
        var (output, _, _, _) = Create();

        output.Feed("Success. Downloaded item 5 to \"C:\\Data\\content\\steamapps\\workshop\\content\\730\\5\" (10 bytes)");
        Assert.Null(output.Download);
        output.Flush();

        Assert.Equal(new SteamDownloadResult(true, 5, "C:\\Data\\content\\steamapps\\workshop\\content\\730\\5", null), output.Download);
    }

    [Fact]
    public void ParsesFailureReason()
    {
        var (output, _, _, _) = Create();

        output.Feed("Downloading item 1 ...ERROR! Download item 1 failed (File Not Found).\r\n");
        output.Flush();

        Assert.Equal(new SteamDownloadResult(false, 1, null, "File Not Found"), output.Download);
    }

    [Fact]
    public void ParsesRealFailureLineWithTrailingShutdownMessage()
    {
        var (output, _, _, _) = Create();

        output.Feed("Downloading item 1 ...\nERROR! Download item 1 failed (File Not Found).Unloading Steam API...OK\n");
        output.Flush();

        Assert.Equal(new SteamDownloadResult(false, 1, null, "File Not Found"), output.Download);
    }

    [Fact]
    public void SplitsShutdownMessageGluedToError()
    {
        var (output, lines, _, _) = Create();

        output.Feed("Downloading item 1 ...\nERROR! Download item 1 failed (File Not Found).Unloading Steam API...OK\n");
        output.Flush();

        Assert.Equal(["Downloading item 1 ...", "ERROR! Download item 1 failed (File Not Found).", "Unloading Steam API...OK"], lines);
    }

    [Fact]
    public void SplitsShutdownMessageSeparatedFromSuccessByASpace()
    {
        var (output, lines, _, _) = Create();

        output.Feed("Downloading item 1 ...\nSuccess. Downloaded item 1 to \"C:\\x\\1\\2_legacy.bin\" (10 bytes) Unloading Steam API...OK\n");
        output.Flush();

        Assert.Equal(["Downloading item 1 ...", "Success. Downloaded item 1 to \"C:\\x\\1\\2_legacy.bin\" (10 bytes)", "Unloading Steam API...OK"], lines);
        Assert.Equal(new SteamDownloadResult(true, 1, "C:\\x\\1\\2_legacy.bin", null), output.Download);
    }

    [Fact]
    public void StripsAnsiAndOscSequences()
    {
        var (output, lines, _, _) = Create();

        output.Feed($"{Esc}]0;Steam Console Client\a{Esc}[0m{Esc}[1;32mLogging in user 'anonymous' to Steam Public...OK{Esc}[0m\n");

        Assert.Equal(["Logging in user 'anonymous' to Steam Public...OK"], lines);
    }

    [Fact]
    public void HandlesArbitraryChunkBoundaries()
    {
        const string text = $"{Esc}[0m[  0%] Checking for available updates...\r[----] Verifying installation...\nDownloading item 7 ...Success. Downloaded item 7 to \"/x/7\" (99 bytes) \n";

        var (whole, wholeLines, _, _) = Create();
        whole.Feed(text);
        whole.Flush();

        var (split, splitLines, _, _) = Create();
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
        var (output, lines, _, _) = Create();

        output.Feed("[  0%] Checking for available updates...[----] Verifying installation...\n");

        Assert.Equal(["[  0%] Checking for available updates...", "[----] Verifying installation..."], lines);
    }

    [Fact]
    public void StartupUpdateCheckNeverFreezesProgressAtZero()
    {
        var (output, _, percents, stages) = Create();

        output.Feed("[  0%] Checking for available updates...\r[----] Verifying installation...\n");
        output.Flush();

        Assert.All(percents, p => Assert.Null(p));
        Assert.Contains(SteamStage.CheckingUpdates, stages);
        Assert.Contains(SteamStage.VerifyingInstallation, stages);
    }

    [Fact]
    public void ComputesTrueUpdatePercentFromKilobytesEvenWhenBracketSaysZero()
    {
        var (output, _, percents, stages) = Create();

        output.Feed("[  0%] Downloading update (10,000 of 40,000 KB)...\r");
        output.Flush();

        Assert.Contains(SteamStage.DownloadingUpdate, stages);
        var pct = Assert.Single(percents);
        Assert.NotNull(pct);
        Assert.Equal(25.0, pct.Value, precision: 2);
    }

    [Fact]
    public void ResetsProgressToIndeterminateDuringPostDownloadExtraction()
    {
        var (output, _, percents, stages) = Create();

        output.Feed("[100%] Download Complete.\n[----] Extracting package...\n[----] Installing update...\n");
        output.Flush();

        Assert.Equal([100.0, null, null], percents);
        Assert.Contains(SteamStage.ApplyingUpdate, stages);
    }

    [Fact]
    public void FlushesDownloadingItemPromptImmediatelyWithoutWaitingForNewline()
    {
        var (output, lines, _, stages) = Create();

        output.Feed($"Connecting anonymously to Steam Public...");
        Assert.Contains(SteamStage.Connecting, stages);

        output.Feed("OK\nDownloading item 243702660 ...\u001b[0m");

        // Must already be logged and in DownloadingItem stage before the download finishes!
        Assert.Contains("Downloading item 243702660 ...", lines);
        Assert.Equal(SteamStage.DownloadingItem, output.Stage);
    }

    [Fact]
    public void ReportsPercentButLogsProgressRedrawsOnce()
    {
        var (output, lines, percents, _) = Create();

        for (var i = 1; i <= 50; i++)
            output.Feed($"[{i * 2,3}%] Downloading update ({i * 1000:N0} of 50,000 KB)...\r");
        output.Flush();

        Assert.Single(lines);
        Assert.Equal(50, percents.Count);
        Assert.Equal(100.0, percents[^1]);
    }

    [Fact]
    public void CollapsesRepeatedBootstrapperLines()
    {
        var (output, lines, _, _) = Create();

        output.Feed("[----] Extracting package...\n[----] Extracting package...\n[----] Extracting package...\n[----] Installing update...\n[----] Installing update...\n");

        Assert.Equal(["[----] Extracting package...", "[----] Installing update..."], lines);
    }

    [Fact]
    public void ReportsDownloadProgressPercent()
    {
        var (output, _, percents, _) = Create();

        output.Feed(" Update state (0x61) downloading, progress: 45.23 (12345 / 27283)\n");

        Assert.Equal([45.23], percents);
    }

    [Fact]
    public void KeepsDistinctMessagesBetweenProgressLines()
    {
        var (output, lines, _, _) = Create();

        output.Feed("[  1%] Downloading update (1 of 5 KB)...\r");
        output.Feed("Some other message\n");
        output.Feed("[  9%] Downloading update (4 of 5 KB)...\r");

        Assert.Equal(3, lines.Count);
    }

    [Fact]
    public void TranscriptContainsLoggedLinesOnly()
    {
        var (output, _, _, _) = Create();

        output.Feed("one\n\n\ntwo\r\n");
        output.Flush();

        Assert.Equal("one\ntwo\n", output.Transcript);
    }
}
