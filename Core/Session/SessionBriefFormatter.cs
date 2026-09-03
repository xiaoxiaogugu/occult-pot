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

    internal static string CurrentTarget(OccultPotService service) =>
        service.TryGetCurrentTargetLabel(out var label)
            ? label
            : OccultPotLoc.Get("StatusCurrentPending");

    internal static string NextTarget(OccultPotService service) =>
        service.TryGetNextTargetLabel(out var label)
            ? label
            : OccultPotLoc.Get("StatusNextPending");

    private static string Activity(SessionPhase phase, RuntimeStatus status) =>
        phase switch
        {
            SessionPhase.Digging or SessionPhase.ElixirUse or SessionPhase.WaitCampReturn => OccultPotLoc.Get("ActivityDigging"),
            SessionPhase.PrepareEntry
                or SessionPhase.PlanRoute
                or SessionPhase.EnsureWorld
                or SessionPhase.EnterIsland
                or SessionPhase.WaitEnter
                or SessionPhase.WaitLeave
                or SessionPhase.WorldTravel => OccultPotLoc.Get("ActivityTraveling"),
            SessionPhase.ReadyIsland
                or SessionPhase.FindPot => OccultPotLoc.Get("ActivityFinding"),
            SessionPhase.WaitFight => IsFighting(status)
                ? OccultPotLoc.Get("ActivityFighting")
                : OccultPotLoc.Get("ActivityWaiting"),
            SessionPhase.Completed or SessionPhase.Failed or SessionPhase.Idle => OccultPotLoc.Get("ActivityStopped"),
            _ => OccultPotLoc.Get("ActivityFinding"),
        };

    private static bool IsFighting(RuntimeStatus status) =>
        status.Code is RuntimeStatusCode.Fight_InProgress;

    internal static string FormatVisitShort(PlannedPotVisit visit) =>
        FormatVisitShort(visit.DC, visit.WorldID, visit.Territory, visit.Kind, visit.Alive, visit.WaitSeconds, visit.UntilGoneSeconds, forNext: true);

    internal static string FormatVisitShort(CnDataCenterKind dc, uint worldID, ushort territory, PotKind kind) =>
        FormatVisitPlace(dc, worldID, territory, kind);

    internal static string FormatVisitShort(
        CnDataCenterKind dc,
        uint worldID,
        ushort territory,
        PotKind kind,
        bool alive,
        int wait,
        int untilGone,
        bool forNext = false) =>
        $"{FormatVisitPlace(dc, worldID, territory, kind)} {FormatVisitWhen(alive, wait, untilGone, forNext)}";

    private static string FormatVisitPlace(CnDataCenterKind dc, uint worldID, ushort territory, PotKind kind)
    {
        var place = worldID != 0
            ? CnWorldCatalog.WorldName(worldID)
            : CnWorldCatalog.DCDisplayName(dc);
        var island = territory switch
        {
            ZoneIds.SouthHorn => OccultPotLoc.Get("SummaryIslandSouth"),
            ZoneIds.NorthHorn => OccultPotLoc.Get("SummaryIslandNorth"),
            _ => IslandPotLayout.IslandLabel(territory),
        };
        var pot = kind == PotKind.North
            ? OccultPotLoc.Get("SummaryPotNorth")
            : OccultPotLoc.Get("SummaryPotSouth");
        return $"{place} {island} {pot}";
    }

    private static string FormatVisitWhen(bool alive, int wait, int untilGone, bool forNext)
    {
        if (alive)
            return OccultPotLoc.Format(
                forNext ? "SummaryPotAvailable" : "SummaryPotAlive",
                OccultTrackerPlanner.FormatMmSs(untilGone));
        // wait=0 且还有窗口：众包认为该刷新，现场 Fate 还没 Running。不是过期。
        if (wait <= 0)
            return untilGone > 0
                ? OccultPotLoc.Get("SummaryPotImminent")
                : OccultPotLoc.Get("SummaryPotStale");
        return OccultPotLoc.Format("SummaryPotWait", OccultTrackerPlanner.FormatMmSs(wait));
    }
}
