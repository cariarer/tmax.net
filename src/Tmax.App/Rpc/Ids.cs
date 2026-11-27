using System.Text.RegularExpressions;

namespace Tmax.App.Rpc;

public static partial class Ids
{
    public static bool IsValidPaneId(string? id) => id is not null && PaneId().IsMatch(id);

    [GeneratedRegex(@"^[A-Za-z0-9_\-:.]{1,128}$")]
    private static partial Regex PaneId();
}
