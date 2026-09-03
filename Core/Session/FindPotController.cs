using System.Numerics;
using OccultPot.Core;
using OccultPot.Core.Adapters;
using OccultPot.Core.Data;
using OccultPot.Core.Game;
using OccultPot.Core.Nav;
using OccultPot.Models;

namespace OccultPot.Core.Session;

internal sealed class FindPotController
{
    private enum Step
    {
        Idle,
        WaitOnline,
        Travel,
        WaitPeekSouth,
        DecideSouth,
        WaitPeekNorth,
        DecideNorth,
        WaitRetry,
        WaitRestart,
        Done,
        Miss
    }

    private enum AfterTravel
    {
        PeekSouth,
        PeekNorth,
        AtPot
    }

    private const double PeekSeconds        = 2.0;
    private const double OnlineWaitSeconds  = 10.0;

    private readonly VNavController vnav = new();
    private readonly IslandTravel travel;
    private readonly Random rng = new();
    private readonly KeitaPotTracker tracker;

    private Step step;
    private AfterTravel afterTravel;
    private ushort territory;
    private PotSideLayout? south;
    private PotSideLayout? north;
    private PotSideLayout? chosen;
    private DateTime stepStartedUTC;
    private int retryCount;
    private bool observeFallback;

    internal RuntimeStatus Status { get; private set; } = RuntimeStatus.Of(RuntimeStatusCode.SessionNotStarted);

    internal PotKind? ChosenKind => chosen?.Kind;

    internal PotSideLayout? Chosen => chosen;

    internal bool IsDone => step == Step.Done;

    internal bool IsMiss => step == Step.Miss;

    internal bool IsRunning =>
        step is not Step.Idle and not Step.Done and not Step.Miss;

    internal FindPotController(KeitaPotTracker tracker)
    {
        this.tracker = tracker;
        travel       = new IslandTravel(vnav);
    }

    internal void Start(ushort territoryID, PotKind? plannedKind = null)
    {
        Stop();
        territory = territoryID;
        EnsureLayouts();
        if (south == null || north == null)
        {
            Status = RuntimeStatus.Of(RuntimeStatusCode.Find_NoTerritoryConfig, territoryID);
            step   = Step.Miss;
            return;
        }

        if (!TryStartFromOnlineTable(plannedKind))
            BeginFind();
    }

    private bool TryStartFromOnlineTable(PotKind? plannedKind)
    {
        if (plannedKind.HasValue)
        {
            var layout = IslandPotLayout.ByKind(territory, plannedKind.Value);
            if (layout != null)
            {
                ExternalCommands.Echo("[找罐] 按在线表直达 " + layout.KindLabel);
                BeginTravelPot(layout);
                return true;
            }
        }

        if (tracker.TryGetCatalogTarget(territory, out var kind, out var reason))
        {
            var layout = IslandPotLayout.ByKind(territory, kind);
            if (layout != null)
            {
                ExternalCommands.Echo("[找罐] " + reason + "，按在线表直达 " + layout.KindLabel);
                BeginTravelPot(layout);
                return true;
            }
        }

        return false;
    }

    private void RestartFullPrediction()
    {
        vnav.Stop();
        travel.Stop();
        retryCount      = 0;
        chosen          = null;
        observeFallback = false;
        EnsureLayouts();
        if (south == null || north == null)
        {
            Status = RuntimeStatus.Of(RuntimeStatusCode.Find_NoTerritoryConfig, territory);
            step   = Step.Miss;
            return;
        }

        if (!TryStartFromOnlineTable(null))
            BeginFind();
    }

    private void BeginFind()
    {
        if (TryCommitPredicted())
            return;

        var fallback = south ?? north;
        if (fallback != null)
        {
            ExternalCommands.Echo("[找罐] 在线表未就绪，先去 " + fallback.KindLabel);
            BeginTravelPot(fallback);
            return;
        }

        Enter(Step.WaitOnline, RuntimeStatus.Of(RuntimeStatusCode.Find_WaitTracker, tracker.StatusLine));
    }

    internal void Stop()
    {
        travel.Stop();
        vnav.Stop();
        step            = Step.Idle;
        chosen          = null;
        south           = null;
        north           = null;
        retryCount      = 0;
        observeFallback = false;
        Status          = RuntimeStatus.Of(RuntimeStatusCode.Find_Stopped);
    }

    internal void ApplyChatCorrection(PotKind kind) =>
        ForceTravelTo(kind);

    internal void ForceTravelTo(PotKind kind)
    {
        EnsureLayouts();
        var layout = IslandPotLayout.ByKind(territory, kind);
        if (layout == null)
            return;
        BeginTravelPot(layout);
    }

    internal void TrySkipToActiveFate()
    {
        if (!IsRunning)
            return;

        PotSideLayout? active = null;
        if (south != null && FateReader.IsActive(south.FateID))
            active = south;
        else if (north != null && FateReader.IsActive(north.FateID))
            active = north;

        if (active == null)
            return;
        if (chosen != null && chosen.Kind == active.Kind && afterTravel == AfterTravel.AtPot)
            return;

        ExternalCommands.Echo("[找罐] 本地校准：" + active.KindLabel + " FATE 进行中");
        BeginTravelPot(active);
    }

    internal void Tick()
    {
        if (!IsRunning)
            return;

        travel.NoteArrivalIfClose();
        TrySkipToActiveFate();
        if (step == Step.Done || ((observeFallback || chosen == null) && TryCommitPredicted()))
            return;

        switch (step)
        {
            case Step.WaitOnline:
                TickWaitOnline();
                break;
            case Step.WaitPeekSouth:
                if (south != null)
                    TickWaitPeek(south, AfterTravel.PeekSouth);
                break;
            case Step.WaitPeekNorth:
                if (north != null)
                    TickWaitPeek(north, AfterTravel.PeekNorth);
                break;
            case Step.WaitRetry:
                TickWaitRetry();
                break;
            case Step.WaitRestart:
                if (Elapsed() >= 2.0)
                    RestartFullPrediction();
                break;
            case Step.Travel:
                TickTravel();
                break;
            case Step.DecideSouth:
            case Step.DecideNorth:
                TickDecide();
                break;
        }
    }

    private void TickDecide()
    {
        if (!PlayerReader.IsAvailable() || !PlayerReader.Position.HasValue)
        {
            Status = RuntimeStatus.Of(RuntimeStatusCode.Find_WaitPlayer);
            return;
        }

        if (step == Step.DecideSouth)
        {
            if (south == null)
                return;
            if (PlayerTargeting.HasPlayersNearPot(south.PotCenter))
                CommitPot(south);
            else if (north != null)
                BeginTravelObserve(north, AfterTravel.PeekNorth);
            return;
        }

        if (north == null)
            return;
        if (PlayerTargeting.HasPlayersNearPot(north.PotCenter))
        {
            CommitPot(north);
            return;
        }

        if (retryCount < 1)
        {
            retryCount++;
            ExternalCommands.Echo("[找罐] 两侧均无玩家，等待 120s 再试");
            Enter(Step.WaitRetry, RuntimeStatus.Of(RuntimeStatusCode.Find_NoPlayersBoth));
            return;
        }

        Enter(Step.WaitRestart, RuntimeStatus.Of(RuntimeStatusCode.Find_RestartPeek));
    }

    private void TickTravel()
    {
        travel.Tick();
        Status = travel.Status;
        if (travel.IsFailed)
        {
            step   = Step.Miss;
            Status = RuntimeStatus.Of(RuntimeStatusCode.Find_PathFailed);
            return;
        }

        if (!travel.IsDone)
            return;

        switch (afterTravel)
        {
            case AfterTravel.PeekSouth:
                MountActions.TryMount();
                Enter(Step.WaitPeekSouth, RuntimeStatus.Of(RuntimeStatusCode.Find_AtSouthPeek));
                break;
            case AfterTravel.PeekNorth:
                MountActions.TryMount();
                Enter(Step.WaitPeekNorth, RuntimeStatus.Of(RuntimeStatusCode.Find_AtNorthPeek));
                break;
            case AfterTravel.AtPot:
                step   = Step.Done;
                Status = RuntimeStatus.Of(RuntimeStatusCode.Find_AtPot, chosen?.KindLabel ?? string.Empty);
                ExternalCommands.Echo("[找罐] 已到 " + (chosen?.KindLabel ?? "") + "，等待 FATE");
                break;
        }
    }

    private void TickWaitOnline()
    {
        if (Elapsed() < OnlineWaitSeconds)
        {
            Status = RuntimeStatus.Of(RuntimeStatusCode.Find_WaitOnline, tracker.StatusLine, (int)(OnlineWaitSeconds - Elapsed()));
            return;
        }

        if (!TryStartFromOnlineTable(null) && south != null)
        {
            ExternalCommands.Echo("[找罐] " + tracker.StatusLine + "，改看人");
            BeginTravelObserve(south, AfterTravel.PeekSouth);
        }
    }

    private bool TryCommitPredicted()
    {
        var live = TryGetLiveKind(out var kind, out var reason);
        if (!live && !tracker.TryGetSoonestTarget(territory, out kind, out reason))
            return false;

        var layout = IslandPotLayout.ByKind(territory, kind);
        if (layout == null)
            return false;
        if (chosen != null && chosen.Kind == kind && !observeFallback)
            return false;

        var overrideObserve = observeFallback || (chosen != null && afterTravel == AfterTravel.AtPot);
        if (overrideObserve && !live && !tracker.HasOnlineData && !tracker.HasCatalog)
            return false;

        if (live && chosen != null && chosen.Kind != kind)
            ExternalCommands.Echo("[找罐] 本地校准：" + reason + "，改去 " + layout.KindLabel);
        else
            ExternalCommands.Echo(overrideObserve
                ? $"[找罐] {reason}，直达 {layout.KindLabel}（覆盖看人）"
                : "[找罐] " + reason + "，直达 " + layout.KindLabel);

        BeginTravelPot(layout);
        return true;
    }

    private bool TryGetLiveKind(out PotKind kind, out string reason)
    {
        kind   = PotKind.North;
        reason = string.Empty;
        if (south != null && FateReader.IsActive(south.FateID))
        {
            kind   = south.Kind;
            reason = south.KindLabel + " FATE 进行中";
            return true;
        }

        if (north != null && FateReader.IsActive(north.FateID))
        {
            kind   = north.Kind;
            reason = north.KindLabel + " FATE 进行中";
            return true;
        }

        return false;
    }

    private void BeginTravelObserve(PotSideLayout side, AfterTravel after)
    {
        observeFallback = true;
        var dest = IslandPotLayout.RandomObserveStand(side.ObservePoint, side.PotCenter, rng);
        afterTravel = after;
        travel.Begin(territory, dest, side.KindLabel + "观测点");
        Enter(Step.Travel, travel.Status);
    }

    private void BeginTravelPot(PotSideLayout layout)
    {
        observeFallback = false;
        chosen          = layout;
        afterTravel     = AfterTravel.AtPot;
        travel.Begin(territory, layout.PotCenter, layout.KindLabel);
        Enter(Step.Travel, travel.Status);
    }

    private void TickWaitPeek(PotSideLayout side, AfterTravel which)
    {
        if (PlayerTargeting.HasPlayersNearPot(side.PotCenter))
        {
            CommitPot(side);
            return;
        }

        if (Elapsed() < PeekSeconds)
        {
            var nearby = PlayerTargeting.CountOtherPlayersNear(side.PotCenter, 50f);
            Status = RuntimeStatus.Of(RuntimeStatusCode.Find_PeekPlayers, side.KindLabel, nearby);
            return;
        }

        if (which == AfterTravel.PeekSouth)
        {
            if (north == null)
                return;
            ExternalCommands.Echo("[找罐] 南罐附近无人，改去北罐");
            BeginTravelObserve(north, AfterTravel.PeekNorth);
            return;
        }

        ExternalCommands.Echo("[找罐] 北罐附近无人");
        Enter(Step.DecideNorth, RuntimeStatus.Of(RuntimeStatusCode.Find_JudgeNorth));
    }

    private void TickWaitRetry()
    {
        var northCount = north != null ? PlayerTargeting.CountOtherPlayersNear(north.PotCenter, 50f) : 0;
        var southCount = south != null ? PlayerTargeting.CountOtherPlayersNear(south.PotCenter, 50f) : 0;
        if (northCount > 0 && north != null)
        {
            ExternalCommands.Echo($"[找罐] 等待中北罐来人（{northCount}），留下等待 FATE");
            CommitPot(north);
            return;
        }

        if (southCount > 0 && south != null)
        {
            ExternalCommands.Echo($"[找罐] 等待中南罐来人（{southCount}），留下等待 FATE");
            CommitPot(south);
            return;
        }

        if (Elapsed() >= 120.0)
        {
            Enter(Step.WaitPeekNorth, RuntimeStatus.Of(RuntimeStatusCode.Find_RetryNorthPeek));
            return;
        }

        Status = RuntimeStatus.Of(RuntimeStatusCode.Find_WaitRetry, 120 - (int)Elapsed(), northCount, southCount);
    }

    private void CommitPot(PotSideLayout side)
    {
        var nearby = PlayerTargeting.CountOtherPlayersNear(side.PotCenter, 50f);
        ExternalCommands.Echo($"[找罐] {side.KindLabel}附近有人（{nearby}），留下等待 FATE");
        BeginTravelPot(side);
    }

    private void EnsureLayouts()
    {
        if (south != null && north != null || territory == 0)
            return;
        south = IslandPotLayout.South(territory);
        north = IslandPotLayout.North(territory);
    }

    private double Elapsed() =>
        (DateTime.UtcNow - stepStartedUTC).TotalSeconds;

    private void Enter(Step step, RuntimeStatus status)
    {
        this.step      = step;
        stepStartedUTC = DateTime.UtcNow;
        Status         = status;
    }
}
