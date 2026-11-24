namespace CsgoWorkshop.Core;

// Callbacks may be invoked from any thread. Percent is null while the duration is unknown.
public sealed class Reporter
{
    public Action<string> Log { get; init; } = _ => { };
    public Action<string, double?> Status { get; init; } = (_, _) => { };
}
