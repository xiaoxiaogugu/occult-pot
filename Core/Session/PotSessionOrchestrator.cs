using System.Numerics;
using OccultPot.Core;
using OccultPot.Core.Adapters;
using OccultPot.Core.Data;
using OccultPot.Core.Game;
using OccultPot.Core.Nav;
using OccultPot.Core.Dig;
using OccultPot.Localization;
using OccultPot.Models;
using OmenTools;
using OmenTools.Dalamud;
using OmenTools.OmenService;
using OmenTools.OmenService.Abstractions;
using OmenTools.Threading.TaskHelper;
using OmenTools.Threading.TaskHelper.Enums;

namespace OccultPot.Core.Session;

internal sealed class PotSessionOrchestrator
{
    private enum AfterLeave
    {
        Advance,
        ResumeEnsureWorld,
        ResumeIslandVisit
    }

    private readonly FindPotController find;

    private readonly KeitaPotTracker tracker = new KeitaPotTracker();

    private readonly VNavController vnav = new VNavController();

    private readonly PotDigController dig;

    private readonly Func<PluginConfiguration> getConfig;

    private readonly TaskHelper taskHelper = new TaskHelper
    {
        TimeoutMS = 180000
    };

    private SessionPhase phase;

    private readonly SessionRoute route = new();

    private ushort targetTerritory;

    private PotSideLayout? activeLayout;

    private bool sawFateActive;

    private DateTime phaseStartedUTC;

    private DateTime? rewardPollUntilUTC;

    private DateTime? playerSettleUTC;

    private DateTime? unavailableSinceUTC;

    private AfterLeave afterLeave;

    private RuntimeStatus status = RuntimeStatus.Of(RuntimeStatusCode.SessionStopped);

    private bool digStarted;

    private DateTime? enteredIslandUTC;

    private bool leaveAfterDig;

    private bool fateWaitDismountIssued;

    private PotKind? plannedKind;

    private uint committedWorldID;

    private CnDataCenterKind? skippedDC;

    private ushort skippedTerritory;

    private CnDataCenterKind? completedDC;

    private PotKind? completedKind;

    private ushort completedTerritory;

    private DateTime? campIdleSinceUTC;

    private const double EnterSettleSeconds = 2.5;

    private const float EnterCampReadyRadius = 80f;

    private const double CampReturnMaxSeconds = 90.0;

    private const double CampReturnIdleGiveUpSeconds = 8.0;

    private const double PlanWaitSeconds = 20.0;

    private const double PlanWaitMaxSeconds = 25.0;

    private const double LocalReconcileWaitSeconds = 8.0;

    internal SessionPhase Phase => phase;

    internal bool IsPotFateCombat => phase == SessionPhase.WaitFight;

    // 只在罐 Fate Running 时选敌；等罐 / 结束中 / 领奖都不动目标。
    internal bool ShouldKeepPotFateTarget() =>
        phase == SessionPhase.WaitFight
        && activeLayout != null
        && FateReader.IsActive(activeLayout.FateID);

    internal RuntimeStatus Status => status;

    internal bool IsRunning =>
        phase is not SessionPhase.Idle and not SessionPhase.Completed and not SessionPhase.Failed;

    internal ushort TargetTerritory => targetTerritory;

    internal bool CanSkipCurrentIsland
    {
        get
        {
            if (!IsRunning || !ZoneIds.IsSupportedIsland((ushort)GameState.TerritoryType))
            {
                return false;
            }

            return phase is SessionPhase.ReadyIsland
                or SessionPhase.FindPot
                or SessionPhase.WaitFight
                or SessionPhase.WaitCampReturn
                or SessionPhase.ElixirUse
                or SessionPhase.Digging
                or SessionPhase.WaitLeave;
        }
    }

    internal PotKind? ActiveKind => activeLayout?.Kind;

    internal bool TryGetCurrentTargetLabel(out string label)
    {
        label = string.Empty;
        if (!TryGetCurrentVisit(out var dc, out var territory, out var kind, out var worldID))
            return false;

        var onTargetIsland = ZoneIds.IsSupportedIsland((ushort)GameState.TerritoryType)
            && (ushort)GameState.TerritoryType == territory;

        // 已进目标岛：现场 Fate / 本地推算优先。
        if (onTargetIsland
            && tracker.TryGetLocalPreferred(territory, out var localKind, out var localWait, out var localGone, out var localAlive))
        {
            kind = localKind;
            plannedKind = localKind;
            committedWorldID = CnWorldCatalog.CurrentWorldID;
            if (!localAlive)
            {
                localWait = OccultTrackerPlanner.WaitAfterCrowdWindow(localWait, localGone, true);
                if (localWait > 0)
                    localGone = localWait + OccultTrackerPlanner.FateAliveSeconds;
            }

            label = SessionBriefFormatter.FormatVisitShort(dc, committedWorldID, territory, kind, localAlive, localWait, localGone);
            return true;
        }

        if (tracker.TryGetNextTiming(dc, territory, out var nextKind, out var wait, out var untilGone, out var alive))
        {
            if (alive
                && onTargetIsland
                && phase == SessionPhase.WaitFight
                && activeLayout != null
                && !FateReader.IsActive(activeLayout.FateID))
            {
                var other = IslandPotLayout.ByKind(territory, activeLayout.Kind == PotKind.North ? PotKind.South : PotKind.North);
                if (other == null || !FateReader.IsActive(other.FateID))
                    alive = false;
            }

            if (!alive)
            {
                wait = OccultTrackerPlanner.WaitAfterCrowdWindow(wait, untilGone, onTargetIsland);
                if (wait > 0)
                    untilGone = wait + OccultTrackerPlanner.FateAliveSeconds;
            }

            label = SessionBriefFormatter.FormatVisitShort(dc, worldID, territory, nextKind, alive, wait, untilGone);
            return true;
        }

        label = SessionBriefFormatter.FormatVisitShort(dc, worldID, territory, kind);
        return true;
    }

    internal bool TryGetNextTargetLabel(out string label)
    {
        label = string.Empty;
        var worlds = route.GetEnabled(getConfig());
        if (worlds.Count == 0 || !TryGetCurrentVisit(out var dc, out var territory, out var kind, out _))
            return false;

        var currentWorldID = CnWorldCatalog.CurrentWorldID;
        var currentTerritory = (ushort)GameState.TerritoryType;
        if (!tracker.TryPickNextVisit(worlds, currentWorldID, currentTerritory, out var nextVisit, skippedTerritory, skippedDC, territory, kind, dc))
            return false;

        label = SessionBriefFormatter.FormatVisitShort(nextVisit);
        return true;
    }

    internal string RouteSummary { get; private set; } = string.Empty;

    internal RuntimeStatus TrackerStatus => tracker.StatusLine;

    internal RuntimeStatus TrackerCatalog => tracker.CatalogStatus;

    internal PotSessionOrchestrator(Func<PluginConfiguration> getConfig, PotDigController dig)
    {
        this.getConfig = getConfig;
        this.dig = dig;
        find = new FindPotController(tracker);
        GameState.Instance().EnterFate += OnEnterFate;
    }

    internal void Start()
    {
        if (!OccultPotRuntime.IsSupported)
        {
            Fail(OccultPotRuntime.UnsupportedStatus);
            return;
        }
        StopInternal();
        route.Build(getConfig());
        if (route.Count == 0)
        {
            Fail(RuntimeStatus.Of(RuntimeStatusCode.ErrorEmptyRoute));
            return;
        }
        route.PrepareStart(ref targetTerritory);
        route.RotateToStart();
        RouteSummary = route.FormatSummary();
        if (!BeginStartEntryGate())
        {
            return;
        }

        ContinueStartAfterEntryGate();
    }

    private bool BeginStartEntryGate()
    {
        PluginConfiguration config = getConfig();
        if (config.AutoBaseClassJobID != 0)
        {
            JobSwitcher.TrySwitchToBaseOnStart(config);
            if (!JobSwitcher.IsOnBaseJob(config))
            {
                Enter(SessionPhase.PrepareEntry, RuntimeStatus.Of(RuntimeStatusCode.Enter_WaitBaseJob));
                return true;
            }
        }
        else if (!IslandEntryGate.CanEnterCurrentJob())
        {
            NotifyEntryDenied();
            Fail(RuntimeStatus.Of(RuntimeStatusCode.ErrorEntryJobLevel));
            return false;
        }

        return true;
    }

    private void ContinueStartAfterEntryGate()
    {
        ushort territoryID = (ushort)GameState.TerritoryType;
        if (ZoneIds.IsSupportedIsland(territoryID))
        {
            targetTerritory = territoryID;
            plannedKind = null;
            BeginIslandVisit();
            return;
        }

        Enter(SessionPhase.PlanRoute, RuntimeStatus.Of(RuntimeStatusCode.Plan_OutsidePick));
        if (!tracker.HasCatalog)
            tracker.ForceCatalogRefresh();
    }

    private void TickPrepareEntry()
    {
        PluginConfiguration config = getConfig();
        if (config.AutoBaseClassJobID != 0)
        {
            JobSwitcher.TrySwitchToBaseOnStart(config);
            if (!JobSwitcher.IsOnBaseJob(config))
            {
                status = RuntimeStatus.Of(RuntimeStatusCode.Enter_WaitBaseJob);
                if (TimedOut(15.0))
                {
                    Fail(RuntimeStatus.Of(RuntimeStatusCode.ErrorEntryJobSwitchTimeout));
                }

                return;
            }
        }

        if (!IslandEntryGate.CanEnterCurrentJob())
        {
            NotifyEntryDenied();
            Fail(RuntimeStatus.Of(RuntimeStatusCode.ErrorEntryJobLevel));
            return;
        }

        ContinueStartAfterEntryGate();
    }

    private static void NotifyEntryDenied() =>
        NotifyHelper.ToastError(OccultPotLoc.Get("NotifyEntryJobLevel"));

    internal void RequestSkipIsland()
    {
        if (!CanSkipCurrentIsland)
        {
            return;
        }

        RememberSkippedIsland();
        if (phase == SessionPhase.WaitLeave)
        {
            afterLeave = AfterLeave.Advance;
            status = RuntimeStatus.Of(RuntimeStatusCode.SkipIsland_Replan);
            return;
        }

        find.Stop();
        afterLeave = AfterLeave.Advance;
        BeginLeave(RuntimeStatus.Of(RuntimeStatusCode.SkipIsland_Leave));
    }

    internal void Stop()
    {
        StopInternal();
        Enter(SessionPhase.Idle, RuntimeStatus.Of(RuntimeStatusCode.SessionStopped));
    }

    private void StopInternal()
    {
        taskHelper.Abort();
        find.Stop();
        vnav.Stop();
        tracker.ResetIsland();
        IslandLeave.Reset();
        BmrAi.ForceOff();
        if (dig.IsActive)
            dig.Stop();
        taskHelper.Abort();
        IslandLeave.Reset();
        activeLayout           = null;
        sawFateActive          = false;
        rewardPollUntilUTC     = null;
        digStarted             = false;
        leaveAfterDig          = false;
        playerSettleUTC        = null;
        unavailableSinceUTC    = null;
        enteredIslandUTC       = null;
        fateWaitDismountIssued = false;
        campIdleSinceUTC       = null;
        plannedKind            = null;
        committedWorldID       = 0;
        targetTerritory        = 0;
        afterLeave             = AfterLeave.Advance;
        ClearSkippedIsland();
        ClearCompletedPot();
        PartyInviteActions.Reset();
    }

    internal void OnChatMessage(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }
        if (phase == SessionPhase.FindPot || phase == SessionPhase.WaitFight)
        {
            PotKind? potKind = IslandPotLayout.GuessKindFromChat(text);
            if (potKind.HasValue)
            {
                if (phase == SessionPhase.FindPot)
                {
                    find.ApplyChatCorrection(potKind.Value);
                }
                else if (activeLayout != null && activeLayout.Kind != potKind.Value)
                {
                    CorrectFightSide(potKind.Value);
                }
            }
        }
        dig.OnChatText(text);
    }

    internal void OnDigStopped(StopReason reason)
    {
        if (phase != SessionPhase.Digging)
        {
            return;
        }

        if (reason is StopReason.UserRequested or StopReason.Disposed)
        {
            return;
        }

        RememberCompletedPot();
        leaveAfterDig = true;
        BeginLeave(RuntimeStatus.Of(RuntimeStatusCode.Leave_DigDone));
    }

    internal void Tick()
    {
        if (!IsRunning)
        {
            return;
        }
        dig.Tick();
        try
        {
            tracker.Tick();
        }
        catch (Exception ex)
        {
            DLog.Error("[规划] Tracker.Tick 失败", ex);
        }
        PartyInviteActions.TickLeave();
        if (!TickLeaveGuards())
        {
            TrySwitchJobsInBackground();
            switch (phase)
            {
            case SessionPhase.PrepareEntry:
                TickPrepareEntry();
                break;
            case SessionPhase.PlanRoute:
                TickPlanRoute();
                break;
            case SessionPhase.ReadyIsland:
                TickReadyIsland();
                break;
            case SessionPhase.FindPot:
                TickFindPot();
                break;
            case SessionPhase.WaitFight:
                TickWaitFight();
                break;
            case SessionPhase.WaitCampReturn:
                TickWaitCampReturn();
                break;
            case SessionPhase.ElixirUse:
                TickElixirUse();
                break;
            case SessionPhase.Digging:
                TickDigging();
                break;
            case SessionPhase.EnsureWorld:
            case SessionPhase.EnterIsland:
            case SessionPhase.WaitEnter:
                break;
            }
        }
    }

    private void TickPlanRoute()
    {
        if (TryCommitOnlinePlan())
        {
            return;
        }
        var waitForFetch = !tracker.HasCatalog && tracker.CatalogInFlight;
        var limit = waitForFetch ? PlanWaitMaxSeconds : PlanWaitSeconds;
        if (Elapsed() >= limit)
        {
            ExternalCommands.Echo("[规划] 四区数据不足，按勾选顺序");
            if (skippedTerritory != 0)
            {
                route.Advance();
                ClearSkippedIsland();
            }

            RouteSummary = route.FormatSummary();
            EnterEnsureWorldForCurrentSlot("按勾选顺序");
            return;
        }
        int value = (int)Math.Ceiling(limit - Elapsed());
        status = tracker.CatalogStatus.IsNone
            ? RuntimeStatus.Of(RuntimeStatusCode.Plan_FetchCatalog, value)
            : RuntimeStatus.Of(RuntimeStatusCode.Plan_CatalogDetail, tracker.CatalogStatus, value);
    }

    private bool TryCommitOnlinePlan()
    {
        if (!tracker.TryPickVisit(route.Slots, CnWorldCatalog.CurrentWorldID, (ushort)GameState.TerritoryType, out var visit, skippedTerritory, skippedDC, completedTerritory, completedKind, completedDC))
            return false;
        if (!route.TryFindVisit(visit, out var index))
            return false;

        ClearSkippedIsland();
        if (completedKind.HasValue && (visit.DC != completedDC || visit.Territory != completedTerritory))
            ClearCompletedPot();
        route.Index     = index;
        targetTerritory = visit.Territory;
        plannedKind     = visit.Kind;
        committedWorldID = visit.WorldID;
        RouteSummary    = visit.Reason;
        ExternalCommands.Echo("[规划] " + visit.Reason);
        EnterEnsureWorldForCurrentSlot(visit.Reason);
        return true;
    }

    private void EnterEnsureWorldForCurrentSlot(string why)
    {
        route.ClampIndex();
        var slot   = route.Current;
        var island = IslandPotLayout.IslandLabel(targetTerritory);
        Enter(SessionPhase.EnsureWorld, RuntimeStatus.Of(RuntimeStatusCode.Session_GoWorldDetail, slot.Kind.Display(), CnWorldCatalog.WorldName(slot.WorldID), island, why));
    }

    private void BeginIslandVisit()
    {
        if (!ZoneIds.IsSupportedIsland(targetTerritory))
        {
            var current = (ushort)GameState.TerritoryType;
            targetTerritory = ZoneIds.IsSupportedIsland(current) ? current : ZoneIds.SouthHorn;
        }
        activeLayout = null;
        sawFateActive = false;
        rewardPollUntilUTC = null;
        digStarted = false;
        find.Stop();
        tracker.ResetIsland();
        IslandLeave.Reset();
        PartyInviteActions.Reset();
        unavailableSinceUTC = null;
        enteredIslandUTC = null;
        fateWaitDismountIssued = false;
        campIdleSinceUTC = null;
        playerSettleUTC = null;
        var currentTerritory = (ushort)GameState.TerritoryType;
        if (currentTerritory == targetTerritory)
        {
            Enter(SessionPhase.ReadyIsland, RuntimeStatus.Of(RuntimeStatusCode.Enter_AlreadyOn, IslandPotLayout.IslandLabel(targetTerritory)));
        }
        else if (ZoneIds.IsSupportedIsland(currentTerritory))
        {
            afterLeave = AfterLeave.ResumeIslandVisit;
            BeginLeave(RuntimeStatus.Of(RuntimeStatusCode.Enter_LeaveCurrent, IslandPotLayout.IslandLabel(targetTerritory)));
        }
        else
        {
            Enter(SessionPhase.EnterIsland, RuntimeStatus.Of(RuntimeStatusCode.Enter_Command, IslandPotLayout.EntryCommand(targetTerritory)));
        }
    }

    private void TickReadyIsland()
    {
        if (!PlayerReader.IsAvailable())
        {
            status = RuntimeStatus.Of(RuntimeStatusCode.Enter_WaitPlayer);
            return;
        }
        if (PlayerReader.IsTransitionLocked())
        {
            status = RuntimeStatus.Of(RuntimeStatusCode.Enter_WaitBetweenAreas);
            return;
        }
        if (!PlayerReader.Position.HasValue)
        {
            status = RuntimeStatus.Of(RuntimeStatusCode.Enter_WaitPosition);
            return;
        }
        var config = getConfig();
        if (JobSwitcher.NeedsSwitch(config) && !JobSwitcher.IsSatisfied(config) && !TimedOut(8.0))
        {
            JobSwitcher.TrySwitch(config);
            status = JobSwitcher.Status(config);
            return;
        }
        try
        {
            BmrAi.Off();
        }
        catch (Exception ex)
        {
            DLog.Error("[会话] 关闭 BMR 失败", ex);
        }

        enteredIslandUTC ??= DateTime.UtcNow;
        if (!HasPotReward() && !LocalReconcileReady())
        {
            var left = Math.Max(0, LocalReconcileWaitSeconds - (DateTime.UtcNow - enteredIslandUTC.Value).TotalSeconds);
            status = RuntimeStatus.Of(RuntimeStatusCode.Find_WaitLocal, (int)Math.Ceiling(left));
            return;
        }

        DispatchIslandWork();
    }

    /// <summary>
    /// 进岛后按实际 CurrentWorld（众包 server）核对本访侧；不在此处退岛。
    /// </summary>
    private void RebindCommittedVisit()
    {
        tracker.DecideIslandRebind(plannedKind, out var kind, out _, out _, out _);
        var actualWorld = CnWorldCatalog.CurrentWorldID;
        if (actualWorld != 0)
            committedWorldID = actualWorld;
        plannedKind = kind;
    }

    private void DispatchIslandWork()
    {
        if (HasPotReward())
        {
            BeginDig(waitCamp: false);
            return;
        }

        RebindCommittedVisit();
        if (TryLeaveForSoonerVisit())
            return;
        BeginFind();
    }

    private bool LocalReconcileReady()
    {
        if (tracker.HasTrustedLocal(targetTerritory))
            return true;
        enteredIslandUTC ??= DateTime.UtcNow;
        return (DateTime.UtcNow - enteredIslandUTC.Value).TotalSeconds >= LocalReconcileWaitSeconds;
    }

    private void BeginFind()
    {
        // 保留 plannedKind：Find 期间当前目标不重新选罐。
        Enter(SessionPhase.FindPot, RuntimeStatus.Of(RuntimeStatusCode.Find_StartOnline));
        try
        {
            find.Start(targetTerritory, plannedKind);
            if (!find.Status.IsNone)
                status = find.Status;
        }
        catch (Exception ex)
        {
            ExternalCommands.Echo("[找罐] 出发失败：" + ex.Message);
            status = RuntimeStatus.Of(RuntimeStatusCode.Find_StartFailed, ex.Message);
        }
    }

    private void BeginDig(bool waitCamp)
    {
        find.Stop();
        BmrAi.Off();
        rewardPollUntilUTC = null;
        if (waitCamp)
        {
            BeginWaitCampReturn();
        }
        else
        {
            Enter(SessionPhase.ElixirUse, RuntimeStatus.Of(RuntimeStatusCode.Dig_SkipFind));
        }
    }

    private bool IsIslandEntryReady(bool requireCamp, out RuntimeStatus status)
    {
        if (PlayerReader.IsTransitionLocked() || !PlayerReader.IsAvailable())
        {
            status = RuntimeStatus.Of(RuntimeStatusCode.Enter_WaitBetweenAreas);
            return false;
        }
        if (!PlayerReader.Position.HasValue)
        {
            status = RuntimeStatus.Of(RuntimeStatusCode.Enter_WaitPosition);
            return false;
        }
        enteredIslandUTC ??= DateTime.UtcNow;
        if (requireCamp && IslandPotLayout.TryCamp(targetTerritory, out Vector3 spawn, out string name) && PlayerReader.DistanceTo(spawn) > 80f && (DateTime.UtcNow - enteredIslandUTC.Value).TotalSeconds < 8.0)
        {
            status = RuntimeStatus.Of(RuntimeStatusCode.Enter_WaitLandCamp, name);
            return false;
        }
        if ((DateTime.UtcNow - enteredIslandUTC.Value).TotalSeconds < 2.5)
        {
            status = RuntimeStatus.Of(RuntimeStatusCode.Enter_CampReady);
            return false;
        }
        status = RuntimeStatus.None;
        return true;
    }

    private void TrySwitchJobsInBackground()
    {
        if (phase is not (SessionPhase.ReadyIsland or SessionPhase.FindPot or SessionPhase.WaitFight))
            return;
        if (phase == SessionPhase.WaitFight && PlayerReader.IsInCombat())
            return;

        var config = getConfig();
        if (JobSwitcher.NeedsSwitch(config) && !JobSwitcher.IsSatisfied(config))
            JobSwitcher.TrySwitch(config);
    }

    private void TickFindPot()
    {
        if (HasPotReward())
        {
            BeginDig(waitCamp: false);
            return;
        }

        if (TryLeaveForSoonerVisit())
            return;

        find.Tick();
        status = find.Status;
        if (find.IsDone && find.Chosen != null)
        {
            activeLayout = find.Chosen;
            fateWaitDismountIssued = false;
            Enter(SessionPhase.WaitFight, RuntimeStatus.Of(RuntimeStatusCode.Fight_Positioned, activeLayout.KindLabel));
            PartyInviteActions.Reset();
        }
        else if (find.IsMiss)
        {
            BeginLeave(RuntimeStatus.Of(RuntimeStatusCode.Leave_FindMiss));
        }
    }

    private void TickWaitFight()
    {
        PotSideLayout potSideLayout = activeLayout;
        if (potSideLayout == null)
        {
            BeginLeave(RuntimeStatus.Of(RuntimeStatusCode.Leave_NoPotConfig));
            return;
        }
        if (HasPotReward())
        {
            if (sawFateActive)
            {
                TryLeavePartyIfEnabled();
            }
            BeginDig(waitCamp: sawFateActive);
            return;
        }
        if (!sawFateActive)
        {
            if (FateReader.IsActive(potSideLayout.FateID))
            {
                sawFateActive = true;
            }
            else
            {
                PotKind kind = potSideLayout.Kind == PotKind.North ? PotKind.South : PotKind.North;
                PotSideLayout potSideLayout2 = IslandPotLayout.ByKind(targetTerritory, kind);
                if (potSideLayout2 != null && FateReader.IsActive(potSideLayout2.FateID))
                {
                    ExternalCommands.Echo("[找罐] 本地校准：" + potSideLayout2.KindLabel + " FATE 进行中，改去 " + potSideLayout2.KindLabel);
                    CorrectFightSide(potSideLayout2.Kind);
                    return;
                }
            }
        }
        if (!sawFateActive && PlayerReader.IsOnMount() && !fateWaitDismountIssued)
        {
            MountActions.TryDismount();
            fateWaitDismountIssued = true;
            status = RuntimeStatus.Of(RuntimeStatusCode.Fight_DismountWait, potSideLayout.KindLabel);
        }

        // Fate 开了或已经进战：持续确保 BMR 开着（命令失败会隔 2 秒再发）。
        if (FateReader.IsActive(potSideLayout.FateID) || PlayerReader.IsInCombat())
            BmrAi.On();

        if (FateReader.IsActive(potSideLayout.FateID))
        {
            sawFateActive = true;
            TryAcceptPartyIfEnabled();
            status = RuntimeStatus.Of(RuntimeStatusCode.Fight_InProgress, potSideLayout.KindLabel);
        }
        else if (sawFateActive)
        {
            TryLeavePartyIfEnabled();
            if (!rewardPollUntilUTC.HasValue)
            {
                BmrAi.Off();
                rewardPollUntilUTC = DateTime.UtcNow.AddSeconds(3.0);
                status = RuntimeStatus.Of(RuntimeStatusCode.Fight_WaitGuide, 3.0);
                return;
            }
            double totalSeconds = (rewardPollUntilUTC.Value - DateTime.UtcNow).TotalSeconds;
            if (totalSeconds > 0.0)
            {
                status = RuntimeStatus.Of(RuntimeStatusCode.Fight_WaitGuide, totalSeconds.ToString("0.0"));
            }
            else
            {
                rewardPollUntilUTC = null;
                BeginLeave(RuntimeStatus.Of(RuntimeStatusCode.Leave_NoElixir));
            }
        }
        else if (TimedOut(1200.0))
        {
            BeginLeave(RuntimeStatus.Of(RuntimeStatusCode.Leave_FateTimeout));
        }
        else if (TryLeaveForSoonerVisit())
        {
            return;
        }
        else
        {
            TryAcceptPartyIfEnabled();
            status = RuntimeStatus.Of(RuntimeStatusCode.Fight_WaitFate, activeLayout.KindLabel);
        }
    }

    /// <summary>
    /// 现场 Running 留下；众包窗开头留下等。窗过了或本地不可信，别处更近才退岛。
    /// </summary>
    private bool TryLeaveForSoonerVisit()
    {
        if (KeitaPotTracker.IsLocalFateAlive(targetTerritory))
            return false;
        if (!TryResolveRouteDC(out var dc))
            return false;

        var worlds = route.GetEnabled(getConfig());
        if (worlds.Count == 0)
            return false;

        if (!tracker.TryPickVisit(
                worlds,
                CnWorldCatalog.CurrentWorldID,
                (ushort)GameState.TerritoryType,
                out var best,
                skippedTerritory,
                skippedDC,
                completedTerritory,
                completedKind,
                completedDC))
            return false;

        if (best.DC == dc && best.Territory == targetTerritory)
        {
            var kind = activeLayout?.Kind ?? plannedKind;
            if (!kind.HasValue || best.Kind == kind.Value)
                return false;

            ExternalCommands.Echo("[规划] 本地核对：" + best.Reason);
            plannedKind = best.Kind;
            if (phase == SessionPhase.WaitFight)
                CorrectFightSide(best.Kind);
            else if (phase == SessionPhase.FindPot)
                find.ForceTravelTo(best.Kind);
            return false;
        }

        if (best.Alive)
            return false;

        var localWait = OccultTrackerPlanner.AbandonWaitSeconds;
        if (tracker.TryGetLocalPreferred(targetTerritory, out _, out var wait, out var gone, out var localAlive))
        {
            if (localAlive)
                return false;
            localWait = OccultTrackerPlanner.WaitAfterCrowdWindow(wait, gone, true);
        }

        var hop = best.DC == dc
            ? OccultTrackerPlanner.SameDCHopSeconds
            : OccultTrackerPlanner.CrossDCBufferSeconds;
        if (best.DC != dc
            && OccultTrackerPlanner.TooLateToCrossDC(best.WaitSeconds, best.UntilGoneSeconds, best.Alive))
            return false;

        var bestEta = best.Alive || best.WaitSeconds == 0
            ? hop
            : Math.Max(best.WaitSeconds, hop);
        if (localWait <= bestEta)
            return false;

        RememberSkippedIsland();
        afterLeave = AfterLeave.Advance;
        BeginLeave(RuntimeStatus.Of(RuntimeStatusCode.WorldTravel_NextReplan));
        return true;
    }

    private void TryAcceptPartyIfEnabled()
    {
        if (getConfig().AutoAcceptPartyAtFate)
        {
            PartyInviteActions.TickAccept();
        }
    }

    private void TryLeavePartyIfEnabled()
    {
        if (getConfig().AutoAcceptPartyAtFate)
        {
            PartyInviteActions.LeaveOnce();
        }
    }

    private double Elapsed() =>
        (DateTime.UtcNow - phaseStartedUTC).TotalSeconds;

    private void BeginWaitCampReturn()
    {
        BmrAi.Off();
        campIdleSinceUTC = null;
        Enter(SessionPhase.WaitCampReturn, RuntimeStatus.Of(RuntimeStatusCode.Camp_WaitBocchi));
    }

    private void TickWaitCampReturn()
    {
        if (!PlayerReader.IsAvailable())
        {
            status = RuntimeStatus.Of(RuntimeStatusCode.Camp_WaitActionable);
            return;
        }
        if (PlayerReader.IsBetweenAreas() || PlayerReader.IsCasting())
        {
            campIdleSinceUTC = null;
            status = RuntimeStatus.Of(RuntimeStatusCode.Camp_WaitLand);
            return;
        }
        if (AethernetRouter.PlayerNearCamp(targetTerritory))
        {
            campIdleSinceUTC = null;
            Enter(SessionPhase.ElixirUse, RuntimeStatus.Of(RuntimeStatusCode.Dig_ReadyAtCamp));
            return;
        }
        if (TimedOut(90.0))
        {
            campIdleSinceUTC = null;
            Enter(SessionPhase.ElixirUse, RuntimeStatus.Of(RuntimeStatusCode.Dig_CampTimeout));
            return;
        }
        if (PlayerReader.IsBusy() || PlayerReader.IsMoving || PlayerReader.IsInCombat() || vnav.IsRunning())
        {
            campIdleSinceUTC = null;
            status = RuntimeStatus.Of(RuntimeStatusCode.Camp_BocchiReturning);
            return;
        }
        campIdleSinceUTC ??= DateTime.UtcNow;
        double totalSeconds = (DateTime.UtcNow - campIdleSinceUTC.Value).TotalSeconds;
        if (totalSeconds >= 8.0)
        {
            campIdleSinceUTC = null;
            Enter(SessionPhase.ElixirUse, RuntimeStatus.Of(RuntimeStatusCode.Dig_NoReturn));
            return;
        }
        status = RuntimeStatus.Of(RuntimeStatusCode.Camp_WaitBocchiTimer, totalSeconds);
    }

    private void CorrectFightSide(PotKind kind)
    {
        PotSideLayout potSideLayout = IslandPotLayout.ByKind(targetTerritory, kind);
        if (!(potSideLayout == null) && (!(activeLayout != null) || activeLayout.Kind != kind))
        {
            vnav.Stop();
            BmrAi.Off();
            activeLayout = potSideLayout;
            plannedKind = kind;
            sawFateActive = FateReader.IsActive(potSideLayout.FateID);
            rewardPollUntilUTC = null;
            PartyInviteActions.Reset();
            find.ForceTravelTo(kind);
            Enter(SessionPhase.FindPot, RuntimeStatus.Of(RuntimeStatusCode.Correct_Ptp, potSideLayout.PtpName, potSideLayout.KindLabel));
        }
    }

    private void TickElixirUse()
    {
        BmrAi.Off();
        if (!digStarted)
        {
            if (!PlayerReader.IsAvailable() || PlayerReader.IsBetweenAreas())
            {
                status = RuntimeStatus.Of(RuntimeStatusCode.Dig_WaitActionable);
                return;
            }
            var used = InventoryReader.TryUseElixir() || InventoryReader.GetElixirRecastRemaining() > 0.15f;
            StartDig(used);
        }
    }

    private void StartDig(bool medicineAlreadyUsed)
    {
        vnav.Stop();
        if (!dig.Start(medicineAlreadyUsed, activeLayout?.Kind).Success)
        {
            BeginLeave(RuntimeStatus.Of(RuntimeStatusCode.Leave_DigStartFailed));
            return;
        }
        digStarted = true;
        ExternalCommands.Echo("[挖箱] 挖箱已启动");
        Enter(SessionPhase.Digging, medicineAlreadyUsed
            ? RuntimeStatus.Of(RuntimeStatusCode.Dig_InProgressWaitHint)
            : RuntimeStatus.Of(RuntimeStatusCode.Dig_InProgressWithMedicine));
    }

    private void TickDigging()
    {
        if (!digStarted)
        {
            return;
        }
        if (!dig.IsActive)
        {
            if (!leaveAfterDig)
            {
                BeginLeave(RuntimeStatus.Of(RuntimeStatusCode.Leave_DigEnd));
            }
            return;
        }
        OccultPotSnapshot? snapshot = dig.GetSnapshot();
        status = !snapshot.HasValue
            ? RuntimeStatus.Of(RuntimeStatusCode.Dig_InProgress)
            : RuntimeStatus.Of
            (
                RuntimeStatusCode.Dig_InProgressDetail,
                snapshot.Value.Status,
                snapshot.Value.LastHint ?? "-",
                snapshot.Value.RemainingCandidates
            );
    }

    private bool TickLeaveGuards()
    {
        if (phase is SessionPhase.PrepareEntry
            or SessionPhase.PlanRoute
            or SessionPhase.EnsureWorld
            or SessionPhase.EnterIsland
            or SessionPhase.WaitEnter
            or SessionPhase.WaitLeave
            or SessionPhase.WorldTravel)
        {
            return false;
        }
        if (PlayerReader.IsBetweenAreas())
        {
            return false;
        }
        var territoryID = (ushort)GameState.TerritoryType;
        if (!ZoneIds.IsSupportedIsland(territoryID))
        {
            afterLeave = AfterLeave.Advance;
            AdvanceAfterLeave();
            return true;
        }
        if (territoryID != targetTerritory)
        {
            BeginLeave(RuntimeStatus.Of(RuntimeStatusCode.Leave_WrongIsland));
            return true;
        }
        if (!PlayerReader.IsAvailable())
        {
            unavailableSinceUTC ??= DateTime.UtcNow;
            if ((DateTime.UtcNow - unavailableSinceUTC.Value).TotalSeconds >= 180.0)
            {
                BeginLeave(RuntimeStatus.Of(RuntimeStatusCode.Leave_PlayerUnavailable));
                return true;
            }
            return false;
        }
        unavailableSinceUTC = null;
        if (phase == SessionPhase.FindPot && TimedOut(1200.0))
        {
            BeginLeave(RuntimeStatus.Of(RuntimeStatusCode.Leave_FindTimeout));
            return true;
        }
        if (phase == SessionPhase.Digging && TimedOut(900.0))
        {
            BeginLeave(RuntimeStatus.Of(RuntimeStatusCode.Leave_DigTimeout));
            return true;
        }
        return false;
    }

    private void BeginLeave(RuntimeStatus status)
    {
        BmrAi.ForceOff();
        vnav.Stop();
        if (dig.IsActive)
        {
            dig.Stop();
        }
        IslandLeave.Reset();
        Enter(SessionPhase.WaitLeave, status);
        IslandLeave.TickLeave();
    }

    private void AdvanceAfterLeave()
    {
        plannedKind  = null;
        activeLayout = null;
        IslandLeave.Reset();
        Enter(SessionPhase.PlanRoute, RuntimeStatus.Of(RuntimeStatusCode.Plan_NextIsland));
        tracker.ForceCatalogRefresh();
    }

    private void RememberSkippedIsland()
    {
        skippedTerritory = (ushort)GameState.TerritoryType;
        skippedDC = CnWorldCatalog.KindForWorldID(CnWorldCatalog.CurrentWorldID)
            ?? CnWorldCatalog.KindForDataCenterID(GameState.CurrentDataCenter);
    }

    private void ClearSkippedIsland()
    {
        skippedDC = null;
        skippedTerritory = 0;
    }

    private bool ShouldSkipLateCrossDC(uint worldID, out CnDataCenterKind destDC)
    {
        destDC = default;
        if (targetTerritory == 0)
            return false;
        if (PlayerReader.IsBusy() || PlayerReader.IsTransitionLocked())
            return false;

        var dest = CnWorldCatalog.KindForWorldID(worldID);
        var current = CnWorldCatalog.KindForWorldID(CnWorldCatalog.CurrentWorldID)
            ?? CnWorldCatalog.KindForDataCenterID(GameState.CurrentDataCenter);
        if (!dest.HasValue || !current.HasValue || dest.Value == current.Value)
            return false;
        if (!tracker.TryGetDCIslandTiming(dest.Value, targetTerritory, out _, out var wait, out var gone, out var alive))
            return false;
        if (!OccultTrackerPlanner.TooLateToCrossDC(wait, gone, alive))
            return false;

        destDC = dest.Value;
        return true;
    }

    private void RememberCompletedPot()
    {
        var kind = activeLayout?.Kind ?? plannedKind;
        if (!kind.HasValue)
            return;

        completedKind      = kind;
        completedTerritory = targetTerritory != 0 ? targetTerritory : (ushort)GameState.TerritoryType;
        TryResolveRouteDC(out var dc);
        completedDC        = dc;
    }

    private void ClearCompletedPot()
    {
        completedDC        = null;
        completedKind      = null;
        completedTerritory = 0;
    }

    private void Enter(SessionPhase phase, RuntimeStatus status)
    {
        var previous = this.phase;
        this.phase   = phase;
        phaseStartedUTC = DateTime.UtcNow;
        this.status  = status;
        if (previous != phase)
            OnPhaseEntered(phase);
    }

    private void OnPhaseEntered(SessionPhase entered)
    {
        switch (entered)
        {
        case SessionPhase.EnsureWorld:
            ScheduleTravelToWorldTask();
            break;
        case SessionPhase.EnterIsland:
            ScheduleEnterIslandTask();
            break;
        case SessionPhase.WaitLeave:
            ScheduleLeaveTask();
            break;
        }
    }

    private void OnEnterFate(uint fateID)
    {
        if (phase == SessionPhase.WaitFight && !(activeLayout == null) && fateID == activeLayout.FateID)
        {
            sawFateActive = true;
        }
    }

    private void ScheduleTravelToWorldTask()
    {
        if (route.Count == 0)
            return;

        route.ClampIndex();
        var worldID = route.Current.WorldID;
        DateTime lastSendUTC = DateTime.MinValue;
        taskHelper.Abort();
        taskHelper.Enqueue(delegate
        {
            if (phase is not (SessionPhase.EnsureWorld or SessionPhase.WorldTravel))
                return true;
            if (CnWorldCatalog.CurrentWorldID == worldID)
            {
                if (phase == SessionPhase.WorldTravel)
                {
                    if (!PlayerReader.IsAvailable() || PlayerReader.IsBusy())
                    {
                        return false;
                    }
                    if (Elapsed() < 3.0)
                    {
                        return false;
                    }
                }
                BeginIslandVisit();
                return true;
            }
            if (ShouldSkipLateCrossDC(worldID, out var destDC))
            {
                skippedDC        = destDC;
                skippedTerritory = targetTerritory;
                ExternalCommands.Echo("[规划] 跨大区剩余不足 3 分钟，改规划");
                Enter(SessionPhase.PlanRoute, RuntimeStatus.Of(RuntimeStatusCode.WorldTravel_NextReplan));
                return true;
            }
            ushort territoryID = (ushort)GameState.TerritoryType;
            if (phase == SessionPhase.EnsureWorld && ZoneIds.IsSupportedIsland(territoryID))
            {
                afterLeave = AfterLeave.ResumeEnsureWorld;
                BeginLeave(RuntimeStatus.Of(RuntimeStatusCode.WorldTravel_LeaveBeforeTravel));
                return true;
            }
            if (!PlayerReader.WaitPlayer(ref playerSettleUTC))
            {
                return false;
            }
            var now = DateTime.UtcNow;
            var interval = phase == SessionPhase.EnsureWorld ? 8 : 20;
            if ((now - lastSendUTC).TotalSeconds >= interval)
            {
                ExternalCommands.Run("/pdr worldtravel " + CnWorldCatalog.WorldName(worldID));
                lastSendUTC = now;
                if (phase == SessionPhase.EnsureWorld)
                {
                    Enter(SessionPhase.WorldTravel, RuntimeStatus.Of(RuntimeStatusCode.WorldTravel_Command, CnWorldCatalog.WorldName(worldID)));
                }
            }
            status = RuntimeStatus.Of(RuntimeStatusCode.WorldTravel_WaitArrive, CnWorldCatalog.WorldName(worldID));
            return false;
        }, "WorldTravel", 180000, TaskAbortBehaviour.AbortCurrent, null, delegate
        {
            Fail(RuntimeStatus.Of(RuntimeStatusCode.WorldTravel_Timeout, CnWorldCatalog.WorldName(worldID)));
        });
    }

    private void ScheduleEnterIslandTask()
    {
        DateTime lastSendUTC = DateTime.MinValue;
        taskHelper.Abort();
        taskHelper.Enqueue(delegate
        {
            if (phase is not (SessionPhase.EnterIsland or SessionPhase.WaitEnter))
                return true;
            if (phase == SessionPhase.EnterIsland)
            {
                if (!PlayerReader.WaitPlayer(ref playerSettleUTC))
                {
                    return false;
                }
                DateTime utcNow = DateTime.UtcNow;
                if ((utcNow - lastSendUTC).TotalSeconds < 3.0)
                {
                    return false;
                }
                ExternalCommands.Run(IslandPotLayout.EntryCommand(targetTerritory));
                lastSendUTC = utcNow;
                playerSettleUTC = null;
                enteredIslandUTC = null;
                Enter(SessionPhase.WaitEnter, RuntimeStatus.Of(RuntimeStatusCode.Enter_WaitEnter, IslandPotLayout.IslandLabel(targetTerritory)));
                return false;
            }
            var territoryID = (ushort)GameState.TerritoryType;
            if (territoryID == targetTerritory)
            {
                if (!IsIslandEntryReady(requireCamp: true, out var readyStatus))
                {
                    if (!readyStatus.IsNone)
                        status = readyStatus;
                    return false;
                }
                Enter(SessionPhase.ReadyIsland, RuntimeStatus.Of(RuntimeStatusCode.Enter_EnteredCamp));
                return true;
            }
            if (IslandEntryGate.IsHubTerritory(territoryID) && TimedOut(30.0) && !IslandEntryGate.CanEnterCurrentJob())
            {
                NotifyEntryDenied();
                Fail(RuntimeStatus.Of(RuntimeStatusCode.Enter_HubJobBlocked));
                return true;
            }
            enteredIslandUTC = null;
            playerSettleUTC = null;
            if (TimedOut(90.0))
            {
                DateTime utcNow2 = DateTime.UtcNow;
                if ((utcNow2 - lastSendUTC).TotalSeconds >= 15.0)
                {
                    ExternalCommands.Run(IslandPotLayout.EntryCommand(targetTerritory));
                    lastSendUTC = utcNow2;
                }
                if (TimedOut(150.0))
                {
                    if (ZoneIds.IsSupportedIsland(territoryID))
                    {
                        BeginLeave(RuntimeStatus.Of(RuntimeStatusCode.Enter_TimeoutWrongIsland));
                    }
                    else
                    {
                        Fail(RuntimeStatus.Of(RuntimeStatusCode.Enter_Timeout));
                    }
                    return true;
                }
            }
            return false;
        }, "EnterIsland", 150000, TaskAbortBehaviour.AbortCurrent, null, delegate
        {
            Fail(RuntimeStatus.Of(RuntimeStatusCode.Enter_Timeout));
        });
    }

    private void ScheduleLeaveTask()
    {
        taskHelper.Abort();
        taskHelper.Enqueue(delegate
        {
            if (phase != SessionPhase.WaitLeave)
            {
                return true;
            }
            IslandLeave.TickLeave();
            if (!ZoneIds.IsSupportedIsland((ushort)GameState.TerritoryType))
            {
                AfterLeave afterLeave = this.afterLeave;
                this.afterLeave = AfterLeave.Advance;
                IslandLeave.Reset();
                switch (afterLeave)
                {
                case AfterLeave.ResumeEnsureWorld:
                    Enter(SessionPhase.EnsureWorld, RuntimeStatus.Of(RuntimeStatusCode.WorldTravel_AfterLeave));
                    return true;
                case AfterLeave.ResumeIslandVisit:
                    BeginIslandVisit();
                    return true;
                default:
                    AdvanceAfterLeave();
                    return true;
                }
            }
            if (TimedOut(60.0))
            {
                Fail(RuntimeStatus.Of(RuntimeStatusCode.Leave_Timeout));
                return true;
            }
            status = RuntimeStatus.Of(RuntimeStatusCode.Leave_WaitingIsland);
            return false;
        }, "LeaveIsland", 60000, TaskAbortBehaviour.AbortCurrent, null, delegate
        {
            Fail(RuntimeStatus.Of(RuntimeStatusCode.Leave_Timeout));
        });
    }

    private static bool HasPotReward() =>
        PlayerReader.HasStatus(PotConstants.StatusReward) || InventoryReader.HasElixir();

    private bool TimedOut(double seconds) =>
        (DateTime.UtcNow - phaseStartedUTC).TotalSeconds >= seconds;

    private void Fail(RuntimeStatus status)
    {
        StopInternal();
        Enter(SessionPhase.Failed, status);
    }

    private bool TryGetCurrentVisit(out CnDataCenterKind dc, out ushort territory, out PotKind kind, out uint worldID)
    {
        if (TryGetCurrentPotTarget(out territory, out kind, out dc, out worldID))
            return true;

        var worlds = route.GetEnabled(getConfig());
        if (worlds.Count == 0
            || !tracker.TryPickVisit(worlds, CnWorldCatalog.CurrentWorldID, (ushort)GameState.TerritoryType, out var visit, skippedTerritory, skippedDC, completedTerritory, completedKind, completedDC))
        {
            dc        = default;
            territory = 0;
            kind      = PotKind.North;
            worldID   = 0;
            return false;
        }

        dc        = visit.DC;
        territory = visit.Territory;
        kind      = visit.Kind;
        worldID   = visit.WorldID;
        return true;
    }

    private bool TryGetCurrentPotTarget(out ushort territory, out PotKind kind, out CnDataCenterKind dc, out uint worldID)
    {
        territory = 0;
        kind = PotKind.North;
        dc = default;
        worldID = committedWorldID;
        if (!IsRunning)
            return false;

        if (activeLayout != null && phase is SessionPhase.WaitFight or SessionPhase.FindPot or SessionPhase.Digging or SessionPhase.ElixirUse or SessionPhase.WaitCampReturn)
        {
            territory = targetTerritory;
            kind = activeLayout.Kind;
            if (worldID == 0)
                worldID = CnWorldCatalog.CurrentWorldID;
            return TryResolveRouteDC(out dc);
        }

        if (phase == SessionPhase.FindPot && find.ChosenKind is { } finding && targetTerritory != 0)
        {
            territory = targetTerritory;
            kind      = finding;
            if (worldID == 0)
                worldID = CnWorldCatalog.CurrentWorldID;
            return TryResolveRouteDC(out dc);
        }

        if (plannedKind.HasValue && targetTerritory != 0)
        {
            territory = targetTerritory;
            kind = plannedKind.Value;
            if (worldID == 0 && route.Count > 0)
                worldID = route.Current.WorldID;
            return TryResolveRouteDC(out dc);
        }

        return false;
    }

    private bool TryResolveRouteDC(out CnDataCenterKind dc)
    {
        if (route.Count > 0)
        {
            dc = route.Current.Kind;
            return true;
        }

        dc = CnWorldCatalog.KindForWorldID(CnWorldCatalog.CurrentWorldID)
            ?? CnWorldCatalog.KindForDataCenterID(GameState.CurrentDataCenter)
            ?? CnDataCenterKind.Chocobo;
        return true;
    }

    internal void Uninit()
    {
        Stop();
        GameState.Instance().EnterFate -= OnEnterFate;
        taskHelper.Dispose();
    }
}
