using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using OccultPot.Core;
using OccultPot.Core.Adapters;
using OccultPot.Core.Data;
using OccultPot.Core.Game;
using OccultPot.Core.Nav;
using OccultPot.Core.Dig;
using OccultPot.Localization;
using OccultPot.Models;
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

	private readonly Action saveConfig;

	private readonly TaskHelper taskHelper = new TaskHelper
	{
		TimeoutMS = 180000
	};

	private SessionPhase phase;

	private readonly List<(CnDataCenterKind Kind, uint WorldID)> route = new List<(CnDataCenterKind, uint)>();

	private int routeIndex;

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
		var worlds = GetEnabledRoute();
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

	internal PotSessionOrchestrator(Func<PluginConfiguration> getConfig, Action saveConfig, PotDigController dig)
	{
		this.getConfig = getConfig;
		this.saveConfig = saveConfig;
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
		StopInternal(clearEnabled: false);
		BuildRoute();
		if (route.Count == 0)
		{
			Fail(RuntimeStatus.Of(RuntimeStatusCode.ErrorEmptyRoute));
			return;
		}
		ChooseStartFromCurrentWorld();
		RotateRouteToStart();
		RouteSummary = string.Join(" → ", route.Select(((CnDataCenterKind Kind, uint WorldID) r) => CnWorldCatalog.DCDisplayName(r.Kind) + "/" + CnWorldCatalog.WorldName(r.WorldID)));
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
		StopInternal(clearEnabled: true);
		Enter(SessionPhase.Idle, RuntimeStatus.Of(RuntimeStatusCode.SessionStopped));
	}

	private void StopInternal(bool clearEnabled)
	{
		taskHelper.Abort();
		find.Stop();
		vnav.Stop();
		tracker.Reset();
		IslandLeave.Reset();
		BmrAi.ForceOff();
		if (dig.IsActive)
		{
			dig.Stop();
		}
		taskHelper.Abort();
		IslandLeave.Reset();
		activeLayout = null;
		sawFateActive = false;
		rewardPollUntilUTC = null;
		digStarted = false;
		leaveAfterDig = false;
		playerSettleUTC = null;
		unavailableSinceUTC = null;
		enteredIslandUTC = null;
		fateWaitDismountIssued = false;
		campIdleSinceUTC = null;
		plannedKind = null;
		targetTerritory = 0;
		afterLeave = AfterLeave.Advance;
		ClearSkippedIsland();
		PartyInviteActions.Reset();
		if (clearEnabled)
		{
			getConfig().Enabled = false;
			saveConfig();
		}
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
		catch
		{
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

	private void BuildRoute()
	{
		route.Clear();
		PluginConfiguration pluginConfiguration = getConfig();
		pluginConfiguration.SyncHomeWorldLock();
		(CnDataCenterKind, string, string)[] all = CnWorldCatalog.All;
		for (int i = 0; i < all.Length; i++)
		{
			CnDataCenterKind item = all[i].Item1;
			DataCenterRouteConfig dataCenterRouteConfig = pluginConfiguration.GetRoute(item);
			if (dataCenterRouteConfig.Enabled)
			{
				uint item2 = (dataCenterRouteConfig.DestinationWorldID = CnWorldCatalog.ResolveWorldID(item, dataCenterRouteConfig.DestinationWorldID));
				route.Add((item, item2));
			}
		}
		saveConfig();
	}

	private void ChooseStartFromCurrentWorld()
	{
		routeIndex = ResolveStartRouteIndex();
		if (routeIndex < 0 || routeIndex >= route.Count)
		{
			routeIndex = 0;
		}
		uint currentWorldID = CnWorldCatalog.CurrentWorldID;
		ushort territoryID = (ushort)GameState.TerritoryType;
		if (currentWorldID != 0 && currentWorldID == route[routeIndex].WorldID && ZoneIds.IsSupportedIsland(territoryID))
		{
			targetTerritory = territoryID;
		}
	}

	private int ResolveStartRouteIndex()
	{
		uint currentWorld = CnWorldCatalog.CurrentWorldID;
		if (currentWorld == 0)
		{
			return 0;
		}
		int num = route.FindIndex(((CnDataCenterKind Kind, uint WorldID) r) => r.WorldID == currentWorld);
		if (num >= 0)
		{
			return num;
		}
		CnDataCenterKind? currentDC = CnWorldCatalog.KindForWorldID(currentWorld);
		if (!currentDC.HasValue)
		{
			return 0;
		}
		int num2 = route.FindIndex(((CnDataCenterKind Kind, uint WorldID) r) => r.Kind == currentDC.Value);
		if (num2 >= 0)
		{
			return num2;
		}
		return FindRouteIndexAfterDC(currentDC.Value);
	}

	private void RotateRouteToStart()
	{
		if (route.Count == 0)
		{
			return;
		}
		if (routeIndex < 0 || routeIndex >= route.Count)
		{
			routeIndex = 0;
		}
		if (routeIndex != 0)
		{
			List<(CnDataCenterKind, uint)> list = new List<(CnDataCenterKind, uint)>(route.Count);
			for (int i = 0; i < route.Count; i++)
			{
				list.Add(route[(routeIndex + i) % route.Count]);
			}
			route.Clear();
			route.AddRange(list);
			routeIndex = 0;
		}
	}

	private int FindRouteIndexAfterDC(CnDataCenterKind currentDC)
	{
		(CnDataCenterKind, string, string)[] all = CnWorldCatalog.All;
		int num = -1;
		for (int i = 0; i < all.Length; i++)
		{
			if (all[i].Item1 == currentDC)
			{
				num = i;
				break;
			}
		}
		int num2 = -1;
		int num3 = int.MaxValue;
		for (int j = 0; j < route.Count; j++)
		{
			int num4 = -1;
			for (int k = 0; k < all.Length; k++)
			{
				if (all[k].Item1 == route[j].Kind)
				{
					num4 = k;
					break;
				}
			}
			if (num4 > num && num4 < num3)
			{
				num3 = num4;
				num2 = j;
			}
		}
		if (num2 < 0)
		{
			return 0;
		}
		return num2;
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
				routeIndex++;
				if (routeIndex >= route.Count)
				{
					routeIndex = 0;
				}

				ClearSkippedIsland();
			}

			RouteSummary = string.Join(" → ", route.Select(((CnDataCenterKind Kind, uint WorldID) r) => CnWorldCatalog.DCDisplayName(r.Kind) + "/" + CnWorldCatalog.WorldName(r.WorldID)));
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
		if (!tracker.TryPickVisit(route, CnWorldCatalog.CurrentWorldID, (ushort)GameState.TerritoryType, out var visit, skippedTerritory, skippedDC))
		{
			return false;
		}
		int num = route.FindIndex(((CnDataCenterKind Kind, uint WorldID) r) => r.Kind == visit.DC && r.WorldID == visit.WorldID);
		if (num < 0)
		{
			num = route.FindIndex(((CnDataCenterKind Kind, uint WorldID) r) => r.Kind == visit.DC);
		}
		if (num < 0)
		{
			return false;
		}

		ClearSkippedIsland();
		if (num < 0)
		{
			num = route.FindIndex(((CnDataCenterKind Kind, uint WorldID) r) => r.Kind == visit.DC);
		}
		if (num < 0)
		{
			return false;
		}
		routeIndex = num;
		targetTerritory = visit.Territory;
		plannedKind = visit.Kind;
		RouteSummary = visit.Reason;
		ExternalCommands.Echo("[规划] " + visit.Reason);
		EnterEnsureWorldForCurrentSlot(visit.Reason);
		return true;
	}

	private void EnterEnsureWorldForCurrentSlot(string why)
	{
		if (routeIndex < 0 || routeIndex >= route.Count)
		{
			routeIndex = 0;
		}
		(CnDataCenterKind, uint) tuple = route[routeIndex];
		string value = IslandPotLayout.IslandLabel(targetTerritory);
		Enter(SessionPhase.EnsureWorld, RuntimeStatus.Of(RuntimeStatusCode.Session_GoWorldDetail, tuple.Item1.Display(), CnWorldCatalog.WorldName(tuple.Item2), value, why));
	}

	private void BeginIslandVisit()
	{
		if (!ZoneIds.IsSupportedIsland(targetTerritory))
		{
			ushort num = (ushort)GameState.TerritoryType;
			targetTerritory = (ushort)(ZoneIds.IsSupportedIsland(num) ? num : 1252);
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
		ushort num2 = (ushort)GameState.TerritoryType;
		if (num2 == targetTerritory)
		{
			Enter(SessionPhase.ReadyIsland, RuntimeStatus.Of(RuntimeStatusCode.Enter_AlreadyOn, IslandPotLayout.IslandLabel(targetTerritory)));
		}
		else if (ZoneIds.IsSupportedIsland(num2))
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
		PluginConfiguration pluginConfiguration = getConfig();
		if (JobSwitcher.NeedsSwitch(pluginConfiguration) && !JobSwitcher.IsSatisfied(pluginConfiguration) && !TimedOut(8.0))
		{
			JobSwitcher.TrySwitch(pluginConfiguration);
			status = JobSwitcher.Status(pluginConfiguration);
			return;
		}
		try
		{
			BmrAi.Off();
		}
		catch
		{
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
		DateTime valueOrDefault = enteredIslandUTC.GetValueOrDefault();
		if (!enteredIslandUTC.HasValue)
		{
			valueOrDefault = DateTime.UtcNow;
			enteredIslandUTC = valueOrDefault;
		}
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
		SessionPhase sessionPhase = phase;
		bool flag = (uint)(sessionPhase - 5) <= 2u;
		if (flag && (phase != SessionPhase.WaitFight || !PlayerReader.IsInCombat()))
		{
			PluginConfiguration pluginConfiguration = getConfig();
			if (JobSwitcher.NeedsSwitch(pluginConfiguration) && !JobSwitcher.IsSatisfied(pluginConfiguration))
			{
				JobSwitcher.TrySwitch(pluginConfiguration);
			}
		}
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

	private double Elapsed()
	{
		return (DateTime.UtcNow - phaseStartedUTC).TotalSeconds;
	}

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
		DateTime valueOrDefault = campIdleSinceUTC.GetValueOrDefault();
		if (!campIdleSinceUTC.HasValue)
		{
			valueOrDefault = DateTime.UtcNow;
			campIdleSinceUTC = valueOrDefault;
		}
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
		ushort num = (ushort)GameState.TerritoryType;
		if (!ZoneIds.IsSupportedIsland(num))
		{
			afterLeave = AfterLeave.Advance;
			AdvanceAfterLeave();
			return true;
		}
		if (num != targetTerritory)
		{
			BeginLeave(RuntimeStatus.Of(RuntimeStatusCode.Leave_WrongIsland));
			return true;
		}
		if (!PlayerReader.IsAvailable())
		{
			DateTime valueOrDefault = unavailableSinceUTC.GetValueOrDefault();
			if (!unavailableSinceUTC.HasValue)
			{
				valueOrDefault = DateTime.UtcNow;
				unavailableSinceUTC = valueOrDefault;
			}
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
		{
			return;
		}
		if (routeIndex < 0 || routeIndex >= route.Count)
		{
			routeIndex = 0;
		}
		uint worldID = route[routeIndex].WorldID;
		DateTime lastSendUTC = DateTime.MinValue;
		taskHelper.Abort();
		taskHelper.Enqueue(delegate
		{
			SessionPhase sessionPhase = phase;
			if ((sessionPhase != SessionPhase.EnsureWorld && sessionPhase != SessionPhase.WorldTravel) || 1 == 0)
			{
				return true;
			}
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
			DateTime utcNow = DateTime.UtcNow;
			int num = ((phase == SessionPhase.EnsureWorld) ? 8 : 20);
			if ((utcNow - lastSendUTC).TotalSeconds >= (double)num)
			{
				ExternalCommands.Run("/pdr worldtravel " + CnWorldCatalog.WorldName(worldID));
				lastSendUTC = utcNow;
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
			SessionPhase sessionPhase = phase;
			if ((uint)(sessionPhase - 3) > 1u)
			{
				return true;
			}
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
			ushort num = (ushort)GameState.TerritoryType;
			if (num == targetTerritory)
			{
				if (!IsIslandEntryReady(requireCamp: true, out RuntimeStatus value))
				{
					if (!value.IsNone)
					{
						status = value;
					}
					return false;
				}
				Enter(SessionPhase.ReadyIsland, RuntimeStatus.Of(RuntimeStatusCode.Enter_EnteredCamp));
				return true;
			}
			if (IslandEntryGate.IsHubTerritory(num) && TimedOut(30.0) && !IslandEntryGate.CanEnterCurrentJob())
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
					if (ZoneIds.IsSupportedIsland(num))
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

	private bool TimedOut(double seconds)
	{
		return (DateTime.UtcNow - phaseStartedUTC).TotalSeconds >= seconds;
	}

	private void Fail(RuntimeStatus status)
	{
		StopInternal(clearEnabled: true);
		Enter(SessionPhase.Failed, status);
	}

	private List<(CnDataCenterKind Kind, uint WorldID)> GetEnabledRoute()
	{
		if (route.Count > 0)
			return route.ToList();

		PluginConfiguration config = getConfig();
		config.SyncHomeWorldLock();
		List<(CnDataCenterKind, uint)> worlds = new List<(CnDataCenterKind, uint)>();
		(CnDataCenterKind, string, string)[] all = CnWorldCatalog.All;
		for (int i = 0; i < all.Length; i++)
		{
			CnDataCenterKind kind = all[i].Item1;
			DataCenterRouteConfig routeConfig = config.GetRoute(kind);
			if (!routeConfig.Enabled)
				continue;

			uint worldID = CnWorldCatalog.ResolveWorldID(kind, routeConfig.DestinationWorldID);
			worlds.Add((kind, worldID));
		}

		return worlds;
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
		if (routeIndex >= 0 && routeIndex < route.Count)
		{
			dc = route[routeIndex].Kind;
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

internal static class CnDataCenterKindUI
{
	internal static string Display(this CnDataCenterKind kind) =>
		CnWorldCatalog.DCDisplayName(kind);
}
