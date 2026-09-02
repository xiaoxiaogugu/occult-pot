using OccultPot.Core.Adapters;
using OccultPot.Core.Data;
using OccultPot.Core.Dig;
using OccultPot.Core.Game;
using OccultPot.Localization;
using OccultPot.Models;
using OmenTools.OmenService;

namespace OccultPot.Core.Session;

internal static class SessionBriefFormatter
{
    internal static string Activity(OccultPotService service)
    {
        if (!service.IsRunning)
            return OccultPotLoc.Get("ActivityStopped");

        if (service.IsDigOnlyMode || service.IsDigActive && !service.IsSessionRunning)
            return OccultPotLoc.Get("ActivityDigging");

        return Activity(service.Phase, service.Status);
    }

    internal static string NextTarget(OccultPotService service) =>
        service.TryGetNextTargetLabel(out var label)
            ? label
            : OccultPotLoc.Get("StatusNextPending");

    private static string Activity(SessionPhase phase, RuntimeStatus status) =>
        phase switch
        {
            SessionPhase.Digging or SessionPhase.ElixirUse => OccultPotLoc.Get("ActivityDigging"),
            SessionPhase.PrepareEntry
                or SessionPhase.PlanRoute
                or SessionPhase.EnsureWorld
                or SessionPhase.EnterIsland
                or SessionPhase.WaitEnter
                or SessionPhase.WaitLeave
                or SessionPhase.WorldTravel => OccultPotLoc.Get("ActivityTraveling"),
            SessionPhase.ReadyIsland
                or SessionPhase.FindPot
                or SessionPhase.WaitCampReturn => OccultPotLoc.Get("ActivityFinding"),
            SessionPhase.WaitFight => IsFighting(status)
                ? OccultPotLoc.Get("ActivityFighting")
                : OccultPotLoc.Get("ActivityWaiting"),
            SessionPhase.Completed or SessionPhase.Failed or SessionPhase.Idle => OccultPotLoc.Get("ActivityStopped"),
            _ => OccultPotLoc.Get("ActivityFinding"),
        };

    private static bool IsFighting(RuntimeStatus status) =>
        status.Code is RuntimeStatusCode.Fight_InProgress;

    internal static string FormatVisitShort(PlannedPotVisit visit)
    {
        var island = visit.Territory switch
        {
            ZoneIds.SouthHorn => OccultPotLoc.Get("SummaryIslandSouth"),
            ZoneIds.NorthHorn => OccultPotLoc.Get("SummaryIslandNorth"),
            _ => IslandPotLayout.IslandLabel(visit.Territory),
        };
        var pot = visit.Kind == PotKind.North
            ? OccultPotLoc.Get("SummaryPotNorth")
            : OccultPotLoc.Get("SummaryPotSouth");
        return $"{CnWorldCatalog.DCDisplayName(visit.DC)} {island} {pot}";
    }
}
