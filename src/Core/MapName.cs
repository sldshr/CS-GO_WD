using System.Text.RegularExpressions;

namespace CsgoWorkshop.Core;

public static partial class MapName
{
    [GeneratedRegex(@"^[\p{L}\p{N}_\-. ()+\[\]]+$")]
    private static partial Regex Allowed();

    [GeneratedRegex(@"^[A-Za-z0-9_\-.]+$")]
    private static partial Regex Plain();

    // Names come from an untrusted archive and end up in a game console: reject anything
    // that could smuggle a second command (';', quotes, newlines, ...).
    public static bool IsSafe(string name) =>
        name.Length is > 0 and <= 128
        && !name.StartsWith('.')
        && !name.EndsWith('.')
        && name.Trim() == name
        && Allowed().IsMatch(name);

    public static string Command(string name) =>
        Plain().IsMatch(name) ? $"map {name}" : $"map \"{name}\"";
}
