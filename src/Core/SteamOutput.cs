using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace CsgoWorkshop.Core;

public enum SteamStage
{
    Starting,
    CheckingUpdates,
    DownloadingUpdate,
    ApplyingUpdate,
    VerifyingInstallation,
    LoadingApi,
    Connecting,
    LoggingIn,
    DownloadingItem,
    Finishing,
}

public sealed record SteamDownloadResult(bool Success, ulong ItemId, string? Path, string? Reason);

public sealed record SteamUpdateBytes(long DownloadedBytes, long TotalBytes);

// steamcmd output arrives with ANSI codes, '\r' progress redraws and messages glued together
// without a newline. This turns it into clean lines, tracks execution stages and extracts the verdict.
public sealed partial class SteamOutput(
    Action<string> onLine,
    Action<double?> onPercent,
    Action<SteamStage>? onStage = null,
    Action<SteamUpdateBytes>? onUpdateBytes = null)
{
    [GeneratedRegex(@"(?:\x1B\[[0-9;?]*[ -/]*[@-~]|\x1B\][^\x07\x1B]*(?:\x07|\x1B\\))")]
    private static partial Regex Ansi();

    [GeneratedRegex(@"(?<=\S\s*)(?=Success\. Downloaded|ERROR! |Downloading item |Unloading Steam API|Connecting anonymously|Waiting for client config|Waiting for user info|Loading Steam API|Steam Console Client|\[\s*\d{1,3}%\]|\[-{4}\])")]
    private static partial Regex Glue();

    [GeneratedRegex(@"Success\. Downloaded item (\d+) to ""(.+?)"" \((\d+) bytes\)")]
    private static partial Regex SuccessLine();

    [GeneratedRegex(@"ERROR! Download item (\d+) failed \((.+?)\)")]
    private static partial Regex FailureLine();

    [GeneratedRegex(@"^\[\s*(\d{1,3})%\]")]
    private static partial Regex UpdatePercent();

    [GeneratedRegex(@"Downloading update\s*\(\s*([\d,]+)\s+of\s+([\d,]+)\s*KB\)", RegexOptions.IgnoreCase)]
    private static partial Regex UpdateKilobytes();

    [GeneratedRegex(@"progress:\s*(\d{1,3}(?:\.\d+)?)")]
    private static partial Regex DownloadPercent();

    [GeneratedRegex(@"^Downloading item \d+ \.\.\.$")]
    private static partial Regex DownloadingPrompt();

    [GeneratedRegex(@"\d[\d,.]*")]
    private static partial Regex Digits();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    private readonly StringBuilder _pending = new();
    private readonly StringBuilder _transcript = new();
    private string _lastProgressKey = "";
    private SteamStage _stage = SteamStage.Starting;

    public SteamDownloadResult? Download { get; private set; }
    public SteamStage Stage => _stage;
    public string Transcript => _transcript.ToString();

    public void Feed(string chunk)
    {
        foreach (var ch in chunk)
        {
            if (ch is '\n' or '\r')
                EndLine();
            else if (ch != '\0')
                _pending.Append(ch);
        }

        InspectPending();
    }

    public void Flush() => EndLine();

    private void InspectPending()
    {
        if (_pending.Length == 0 || HasIncompleteAnsi(_pending))
            return;

        var preview = Ansi().Replace(_pending.ToString(), "").Trim();
        if (preview.Length == 0)
            return;

        // steamcmd prints "Downloading item <id> ..." without a newline and blocks until the whole item
        // finishes downloading. Flushing it immediately makes it show up in the log and status right away.
        if (DownloadingPrompt().IsMatch(preview))
        {
            EndLine();
            return;
        }

        if (preview.Contains("Unloading Steam API", StringComparison.OrdinalIgnoreCase))
            SetStage(SteamStage.Finishing);
        else if (preview.Contains("Connecting anonymously", StringComparison.OrdinalIgnoreCase))
            SetStage(SteamStage.Connecting);
        else if (preview.Contains("Waiting for client config", StringComparison.OrdinalIgnoreCase)
                 || preview.Contains("Waiting for user info", StringComparison.OrdinalIgnoreCase))
            SetStage(SteamStage.LoggingIn);
        else if (preview.StartsWith("Loading Steam API", StringComparison.OrdinalIgnoreCase))
            SetStage(SteamStage.LoadingApi);
    }

    private static bool HasIncompleteAnsi(StringBuilder sb)
    {
        for (var i = sb.Length - 1; i >= 0 && i >= sb.Length - 32; i--)
        {
            var ch = sb[i];
            if (ch == '\x1B')
                return true;
            if (ch is (>= '@' and <= '~') or '\x07')
                return false;
        }

        return false;
    }

    private void EndLine()
    {
        if (_pending.Length == 0)
            return;

        // Escape sequences may be split across reads, so strip them per line, not per chunk.
        var raw = Ansi().Replace(_pending.ToString(), "");
        _pending.Clear();

        foreach (var part in Glue().Replace(raw, "\n").Split('\n'))
        {
            var line = part.Trim();
            if (line.Length > 0)
                Handle(line);
        }
    }

    private void Handle(string line)
    {
        var update = UpdatePercent().Match(line);
        var updateKb = UpdateKilobytes().Match(line);
        var download = DownloadPercent().Match(line);
        var isDashStatus = line.StartsWith("[----]", StringComparison.Ordinal);

        if (updateKb.Success
            && TryParseLongNoCommas(updateKb.Groups[1].Value, out var doneKb)
            && TryParseLongNoCommas(updateKb.Groups[2].Value, out var totalKb)
            && totalKb > 0)
        {
            SetStage(SteamStage.DownloadingUpdate);
            onUpdateBytes?.Invoke(new SteamUpdateBytes(doneKb * 1024L, totalKb * 1024L));
            if (doneKb > 0)
                onPercent(Math.Clamp(doneKb * 100.0 / totalKb, 0, 100));
            else if (update.Success && double.TryParse(update.Groups[1].Value, CultureInfo.InvariantCulture, out var pct) && pct > 0)
                onPercent(Math.Min(100, pct));
            else
                onPercent(null);
        }
        else if (line.Contains("Checking for available update", StringComparison.OrdinalIgnoreCase))
        {
            // Never emit 0% on startup update check: otherwise the UI switches out of indeterminate
            // mode and stays stuck at 0% for the entire workshop download.
            SetStage(SteamStage.CheckingUpdates);
            onPercent(null);
        }
        else if (update.Success && double.TryParse(update.Groups[1].Value, CultureInfo.InvariantCulture, out var updatePercent))
        {
            if (line.Contains("Verifying installation", StringComparison.OrdinalIgnoreCase))
            {
                SetStage(SteamStage.VerifyingInstallation);
                onPercent(updatePercent > 0 ? Math.Min(100, updatePercent) : null);
            }
            else
            {
                onPercent(Math.Min(100, updatePercent));
            }
        }
        else if (download.Success && double.TryParse(download.Groups[1].Value, CultureInfo.InvariantCulture, out var downloadPercent))
        {
            SetStage(SteamStage.DownloadingItem);
            onPercent(Math.Min(100, downloadPercent));
        }
        else if (isDashStatus)
        {
            if (line.Contains("Verifying installation", StringComparison.OrdinalIgnoreCase))
                SetStage(SteamStage.VerifyingInstallation);
            else if (line.Contains("Downloading update", StringComparison.OrdinalIgnoreCase))
                SetStage(SteamStage.DownloadingUpdate);
            else
                SetStage(SteamStage.ApplyingUpdate);

            onPercent(null);
        }
        else
        {
            UpdateStageFromLine(line);
        }

        if (update.Success || updateKb.Success || download.Success || isDashStatus)
        {
            // Progress redraws are repeated hundreds of times; log only the first of each kind.
            var key = Spaces().Replace(Digits().Replace(line, "#"), "");
            if (key == _lastProgressKey)
                return;
            _lastProgressKey = key;
        }
        else
        {
            _lastProgressKey = "";
        }

        _transcript.Append(line).Append('\n');
        onLine(line);

        var success = SuccessLine().Match(line);
        if (success.Success && ulong.TryParse(success.Groups[1].Value, CultureInfo.InvariantCulture, out var okId))
        {
            Download = new SteamDownloadResult(true, okId, success.Groups[2].Value, null);
            return;
        }

        var failure = FailureLine().Match(line);
        if (failure.Success && ulong.TryParse(failure.Groups[1].Value, CultureInfo.InvariantCulture, out var failId))
            Download = new SteamDownloadResult(false, failId, null, failure.Groups[2].Value);
    }

    private void UpdateStageFromLine(string line)
    {
        if (line.StartsWith("Downloading item ", StringComparison.OrdinalIgnoreCase))
            SetStage(SteamStage.DownloadingItem);
        else if (line.Contains("Unloading Steam API", StringComparison.OrdinalIgnoreCase))
            SetStage(SteamStage.Finishing);
        else if (line.Contains("Waiting for client config", StringComparison.OrdinalIgnoreCase)
                 || line.Contains("Waiting for user info", StringComparison.OrdinalIgnoreCase))
            SetStage(SteamStage.LoggingIn);
        else if (line.Contains("Connecting anonymously", StringComparison.OrdinalIgnoreCase))
            SetStage(SteamStage.Connecting);
        else if (line.StartsWith("Loading Steam API", StringComparison.OrdinalIgnoreCase))
            SetStage(SteamStage.LoadingApi);
    }

    private void SetStage(SteamStage stage)
    {
        if (_stage == stage)
            return;

        _stage = stage;
        onStage?.Invoke(stage);
    }

    private static bool TryParseLongNoCommas(string text, out long value) =>
        long.TryParse(text.Replace(",", ""), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
}
