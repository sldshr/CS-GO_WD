using System.Text.RegularExpressions;

namespace CsgoWorkshop.Core;

public static partial class WorkshopItem
{
    [GeneratedRegex(@"(?:^|[?&#])id=(\d{1,20})(?!\d)", RegexOptions.IgnoreCase)]
    private static partial Regex QueryId();

    [GeneratedRegex(@"(?:CommunityFilePage|filedetails|publishedfileid)[/=](\d{1,20})(?!\d)", RegexOptions.IgnoreCase)]
    private static partial Regex PathId();

    public static bool TryParseId(string? input, out ulong id)
    {
        id = 0;
        var text = input?.Trim().Trim('"', '\'', '<', '>', '(', ')');
        if (string.IsNullOrEmpty(text))
            return false;

        if (text.All(char.IsAsciiDigit))
            return ulong.TryParse(text, out id) && id != 0;

        var match = QueryId().Match(text);
        if (!match.Success)
            match = PathId().Match(text);

        return match.Success && ulong.TryParse(match.Groups[1].Value, out id) && id != 0;
    }
}
