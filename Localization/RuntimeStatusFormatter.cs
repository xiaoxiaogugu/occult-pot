using OccultPot.Core;
using OccultPot.Core.Dig;

namespace OccultPot.Localization;

internal static class RuntimeStatusFormatter
{
    internal static string Format(RuntimeStatus status)
    {
        if (status.IsNone)
            return string.Empty;

        if (status.Code == RuntimeStatusCode.Literal)
            return status.Args.Length > 0 ? status.Args[0]?.ToString() ?? string.Empty : string.Empty;

        object[] args = status.Args.Length == 0
            ? []
            : status.Args.Select(FormatArg).ToArray();

        return OccultPotLoc.Format(status.Code.ToString(), args);
    }

    internal static string FormatDig(OccultPotSnapshot? snapshot)
    {
        if (!snapshot.HasValue)
            return Format(RuntimeStatus.Of(RuntimeStatusCode.DigOnly_NotStarted));

        if (snapshot.Value.Failure is { } failure && snapshot.Value.Status == OccultPotStatus.Failed)
            return Format(failure);

        var hint = string.IsNullOrEmpty(snapshot.Value.LastHint) ? "-" : snapshot.Value.LastHint;
        return Format(RuntimeStatus.Of
        (
            RuntimeStatusCode.Dig_InProgressDetail,
            snapshot.Value.Status,
            hint,
            snapshot.Value.RemainingCandidates
        ));
    }

    private static object FormatArg(object arg) =>
        arg switch
        {
            RuntimeStatus nested => Format(nested),
            OccultPotStatus digStatus => OccultPotLoc.Get($"DigStatus_{digStatus}"),
            _ => arg,
        };
}
