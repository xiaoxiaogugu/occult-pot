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

    private CnDataCenterKind? skippedDC;

    private ushort skippedTerritory;

    private DateTime? campIdleSinceUTC;

    private const double EnterSettleSeconds = 2.5;

    private const float EnterCampReadyRadius = 80f;

    private const double CampReturnMaxSeconds = 90.0;

    private const double CampReturnIdleGiveUpSeconds = 8.0;

    private const double PlanWaitSeconds = 10.0;

    internal SessionPhase Phase => phase;

    internal bool IsPotFateCombat => phase == SessionPhase.WaitFight;

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

    internal bool TryGetNextTargetLabel(out string label)
    {
        label = string.Empty;
        var worlds = route.GetEnabled(getConfig());
        if (worlds.Count == 0)
            return false;

        var currentWorldID = CnWorldCatalog.CurrentWorldID;
        var currentTerritory = (ushort)GameState.TerritoryType;
        if (TryGetCurrentPotTarget(out var potTerritory, out var potKind, out var potDC)
            && tracker.TryPickNextVisit(worlds, currentWorldID, currentTerritory, out var nextVisit, skippedTerritory, skippedDC, potTerritory, potKind, potDC))
        {
            label = SessionBriefFormatter.FormatVisitShort(nextVisit);
            return true;
        }

        if (tracker.TryPickVisit(worlds, currentWorldID, currentTerritory, out var visit, skippedTerritory, skippedDC))
        {
            label = SessionBriefFormatter.FormatVisitShort(visit);
            return true;
        }

        return false;
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
        }
        else
        {
            Enter(SessionPhase.PlanRoute, RuntimeStatus.Of(RuntimeStatusCode.Plan_OutsidePick));
            tracker.ForceCatalogRefresh();
        }
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
        tracker.Reset();
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
        targetTerritory        = 0;
        afterLeave             = AfterLeave.Advance;
        ClearSkippedIsland();
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
        if (Elapsed() >= 10.0)
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
        int value = (int)Math.Ceiling(10.0 - Elapsed());
        status = tracker.CatalogStatus.IsNone
            ? RuntimeStatus.Of(RuntimeStatusCode.Plan_FetchCatalog, value)
            : RuntimeStatus.Of(RuntimeStatusCode.Plan_CatalogDetail, tracker.CatalogStatus, value);
    }

    private bool TryCommitOnlinePlan()
    {
        if (!tracker.TryPickVisit(route.Slots, CnWorldCatalog.CurrentWorldID, (ushort)GameState.TerritoryType, out var visit, skippedTerritory, skippedDC))
            return false;
        if (!route.TryFindVisit(visit, out var index))
            return false;

        ClearSkippedIsland();
        route.Index     = index;
        targetTerritory = visit.Territory;
        plannedKind     = visit.Kind;
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
        DispatchIslandWork();
    }

    private void DispatchIslandWork()
    {
        if (HasPotReward())
        {
            BeginDig(waitCamp: false);
        }
        else
        {
            BeginFind();
        }
    }

    private void BeginFind()
    {
        PotKind? potKind = plannedKind;
        plannedKind = null;
        Enter(SessionPhase.FindPot, RuntimeStatus.Of(RuntimeStatusCode.Find_StartOnline));
        try
        {
            find.Start(targetTerritory, potKind);
            if (!find.Status.IsNone)
            {
                status = find.Status;
            }
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
        if (PlayerReader.IsBetweenAreas() || !PlayerReader.IsAvailable())
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
            PotKind kind = ((potSideLayout.Kind == PotKind.North) ? PotKind.South : PotKind.North);
            PotSideLayout potSideLayout2 = IslandPotLayout.ByKind(targetTerritory, kind);
            if (potSideLayout2 != null && FateReader.IsActive(potSideLayout2.FateID))
            {
                ExternalCommands.Echo("[找罐] 本地校准：" + potSideLayout2.KindLabel + " FATE 进行中，改去 " + potSideLayout2.KindLabel);
                CorrectFightSide(potSideLayout2.Kind);
                return;
            }
        }
        if (!sawFateActive && PlayerReader.IsOnMount() && !fateWaitDismountIssued)
        {
            MountActions.TryDismount();
            fateWaitDismountIssued = true;
            status = RuntimeStatus.Of(RuntimeStatusCode.Fight_DismountWait, potSideLayout.KindLabel);
        }
        if (FateReader.IsActive(potSideLayout.FateID))
        {
            if (!sawFateActive)
            {
                sawFateActive = true;
                BmrAi.On();
            }
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
        else
        {
            TryAcceptPartyIfEnabled();
            status = RuntimeStatus.Of(RuntimeStatusCode.Fight_WaitFate, activeLayout.KindLabel);
        }
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
            InventoryReader.TryUseElixir();
            StartDig(medicineAlreadyUsed: false);
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

    private void Enter(SessionPhase phase, RuntimeStatus status)
    {
        SessionPhase num = this.phase;
        this.phase = phase;
        phaseStartedUTC = DateTime.UtcNow;
        this.status = status;
        if (num != phase)
        {
            OnPhaseEntered(phase);
        }
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

    private bool TryGetCurrentPotTarget(out ushort territory, out PotKind kind, out CnDataCenterKind dc)
    {
        territory = 0;
        kind = PotKind.North;
        dc = default;
        if (!IsRunning)
            return false;

        if (activeLayout != null && phase is SessionPhase.WaitFight or SessionPhase.FindPot or SessionPhase.Digging or SessionPhase.ElixirUse or SessionPhase.WaitCampReturn)
        {
            territory = targetTerritory;
            kind = activeLayout.Kind;
            return TryResolveRouteDC(out dc);
        }

        if (plannedKind.HasValue && targetTerritory != 0)
        {
            territory = targetTerritory;
            kind = plannedKind.Value;
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
