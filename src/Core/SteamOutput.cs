using System.Text;
using System.Text.RegularExpressions;

namespace CsgoWorkshop.Core;

public sealed record SteamDownloadResult(bool Success, ulong ItemId, string? Path, string? Reason);

// steamcmd output arrives with ANSI codes, '\r' progress redraws and messages glued together
// without a newline. This turns it into clean lines and extracts the download verdict.
public sealed partial class SteamOutput(Action<string> onLine, Action<double> onPercent)
{
    [GeneratedRegex(@"\x1B\[[0-9;?]*[ -/]*[@-~]")]
    private static partial Regex Ansi();

    [GeneratedRegex(@"(?<=\S\s*)(?=Success\. Downloaded|ERROR! |Downloading item |Unloading Steam API|\[\s*\d{1,3}%\]|\[-{4}\])")]
    private static partial Regex Glue();

    [GeneratedRegex(@"Success\. Downloaded item (\d+) to ""(.+?)"" \((\d+) bytes\)")]
    private static partial Regex SuccessLine();

    [GeneratedRegex(@"ERROR! Download item (\d+) failed \((.+?)\)")]
    private static partial Regex FailureLine();

    [GeneratedRegex(@"^\[\s*(\d{1,3})%\]")]
    private static partial Regex UpdatePercent();

    [GeneratedRegex(@"progress:\s*(\d{1,3}(?:\.\d+)?)")]
    private static partial Regex DownloadPercent();

    [GeneratedRegex(@"\d[\d,.]*")]
    private static partial Regex Digits();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    private readonly StringBuilder _pending = new();
    private readonly StringBuilder _transcript = new();
    private string _lastProgressKey = "";

    public SteamDownloadResult? Download { get; private set; }
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
    }

    public void Flush() => EndLine();

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
        var download = DownloadPercent().Match(line);

        if (update.Success && double.TryParse(update.Groups[1].Value, out var updatePercent))
            onPercent(Math.Min(100, updatePercent));
        else if (download.Success && double.TryParse(download.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture, out var downloadPercent))
            onPercent(Math.Min(100, downloadPercent));

        if (update.Success || download.Success || line.StartsWith("[----]", StringComparison.Ordinal))
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
        if (success.Success && ulong.TryParse(success.Groups[1].Value, out var okId))
        {
            Download = new SteamDownloadResult(true, okId, success.Groups[2].Value, null);
            return;
        }

        var failure = FailureLine().Match(line);
        if (failure.Success && ulong.TryParse(failure.Groups[1].Value, out var failId))
            Download = new SteamDownloadResult(false, failId, null, failure.Groups[2].Value);
    }
}
