using System;
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

	private const double PeekSeconds = 2.0;

	private const double OnlineWaitSeconds = 10.0;

	private readonly VNavController vnav = new VNavController();

	private readonly IslandTravel travel;

	private readonly Random rng = new Random();

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

	internal bool IsRunning
	{
		get
		{
			Step step = this.step;
			bool flag = ((step == Step.Idle || (uint)(step - 9) <= 1u) ? true : false);
			return !flag;
		}
	}

	internal FindPotController(KeitaPotTracker tracker)
	{
		this.tracker = tracker;
		travel = new IslandTravel(vnav);
	}

	internal void Start(ushort territoryID, PotKind? plannedKind = null)
	{
		Stop();
		territory = territoryID;
		EnsureLayouts();
		if (south == null || north == null)
		{
			Status = RuntimeStatus.Of(RuntimeStatusCode.Find_NoTerritoryConfig, territoryID);
			step = Step.Miss;
		}
		else if (!TryStartFromOnlineTable(plannedKind))
		{
			BeginFind();
		}
	}

	private bool TryStartFromOnlineTable(PotKind? plannedKind)
	{
		if (plannedKind.HasValue)
		{
			PotSideLayout potSideLayout = IslandPotLayout.ByKind(territory, plannedKind.Value);
			if (potSideLayout != null)
			{
				ExternalCommands.Echo("[找罐] 按在线表直达 " + potSideLayout.KindLabel);
				BeginTravelPot(potSideLayout);
				return true;
			}
		}
		if (tracker.TryGetCatalogTarget(territory, out PotKind kind, out string reason))
		{
			PotSideLayout potSideLayout2 = IslandPotLayout.ByKind(territory, kind);
			if (potSideLayout2 != null)
			{
				ExternalCommands.Echo("[找罐] " + reason + "，按在线表直达 " + potSideLayout2.KindLabel);
				BeginTravelPot(potSideLayout2);
				return true;
			}
		}
		return false;
	}

	private void RestartFullPrediction()
	{
		vnav.Stop();
		travel.Stop();
		retryCount = 0;
		chosen = null;
		observeFallback = false;
		EnsureLayouts();
		if (south == null || north == null)
		{
			Status = RuntimeStatus.Of(RuntimeStatusCode.Find_NoTerritoryConfig, territory);
			step = Step.Miss;
		}
		else if (!TryStartFromOnlineTable(null))
		{
			BeginFind();
		}
	}

	private void BeginFind()
	{
		if (!TryCommitPredicted())
		{
			PotSideLayout potSideLayout = south ?? north;
			if (potSideLayout != null)
			{
				ExternalCommands.Echo("[找罐] 在线表未就绪，先去 " + potSideLayout.KindLabel);
				BeginTravelPot(potSideLayout);
			}
			else
			{
				Enter(Step.WaitOnline, RuntimeStatus.Of(RuntimeStatusCode.Find_WaitTracker, tracker.StatusLine));
			}
		}
	}

	internal void Stop()
	{
		travel.Stop();
		vnav.Stop();
		step = Step.Idle;
		chosen = null;
		south = null;
		north = null;
		retryCount = 0;
		observeFallback = false;
		Status = RuntimeStatus.Of(RuntimeStatusCode.Find_Stopped);
	}

	internal void ApplyChatCorrection(PotKind kind)
	{
		ForceTravelTo(kind);
	}

	internal void ForceTravelTo(PotKind kind)
	{
		EnsureLayouts();
		PotSideLayout potSideLayout = IslandPotLayout.ByKind(territory, kind);
		if (!(potSideLayout == null))
		{
			BeginTravelPot(potSideLayout);
		}
	}

	internal void TrySkipToActiveFate()
	{
		Step step = this.step;
		if ((step != Step.Idle && (uint)(step - 9) > 1u) || 1 == 0)
		{
			PotSideLayout potSideLayout = null;
			if (south != null && FateReader.IsActive(south.FateID))
			{
				potSideLayout = south;
			}
			else if (north != null && FateReader.IsActive(north.FateID))
			{
				potSideLayout = north;
			}
			if (!(potSideLayout == null) && (!(chosen != null) || chosen.Kind != potSideLayout.Kind || afterTravel != AfterTravel.AtPot))
			{
				ExternalCommands.Echo("[找罐] 本地校准：" + potSideLayout.KindLabel + " FATE 进行中");
				BeginTravelPot(potSideLayout);
			}
		}
	}

	internal void Tick()
	{
		Step step = this.step;
		if ((step == Step.Idle || (uint)(step - 9) <= 1u) ? true : false)
		{
			return;
		}
		travel.NoteArrivalIfClose();
		TrySkipToActiveFate();
		if (this.step == Step.Done || ((observeFallback || chosen == null) && TryCommitPredicted()))
		{
			return;
		}
		bool flag;
		switch (this.step)
		{
		case Step.WaitOnline:
		case Step.WaitPeekSouth:
		case Step.WaitPeekNorth:
		case Step.WaitRetry:
		case Step.WaitRestart:
			flag = true;
			break;
		default:
			flag = false;
			break;
		}
		if (flag)
		{
			switch (this.step)
			{
			case Step.WaitOnline:
				TickWaitOnline();
				break;
			case Step.WaitPeekSouth:
				TickWaitPeek(south, AfterTravel.PeekSouth);
				break;
			case Step.WaitPeekNorth:
				TickWaitPeek(north, AfterTravel.PeekNorth);
				break;
			case Step.WaitRetry:
				TickWaitRetry();
				break;
			case Step.WaitRestart:
				if (Elapsed() >= 2.0)
				{
					RestartFullPrediction();
				}
				break;
			case Step.Travel:
			case Step.DecideSouth:
			case Step.DecideNorth:
				break;
			}
			return;
		}
		if (this.step == Step.Travel)
		{
			TickTravel();
			return;
		}
		if (!PlayerReader.IsAvailable() || !PlayerReader.Position.HasValue)
		{
			Status = RuntimeStatus.Of(RuntimeStatusCode.Find_WaitPlayer);
			return;
		}
		switch (this.step)
		{
		case Step.WaitOnline:
			TickWaitOnline();
			break;
		case Step.WaitPeekSouth:
			TickWaitPeek(south, AfterTravel.PeekSouth);
			break;
		case Step.DecideSouth:
			if (PlayerTargeting.HasPlayersNearPot(south.PotCenter))
			{
				CommitPot(south);
			}
			else
			{
				BeginTravelObserve(north, AfterTravel.PeekNorth);
			}
			break;
		case Step.WaitPeekNorth:
			TickWaitPeek(north, AfterTravel.PeekNorth);
			break;
		case Step.DecideNorth:
			if (PlayerTargeting.HasPlayersNearPot(north.PotCenter))
			{
				CommitPot(north);
			}
			else if (retryCount < 1)
			{
				retryCount++;
				ExternalCommands.Echo("[找罐] 两侧均无玩家，等待 120s 再试");
				Enter(Step.WaitRetry, RuntimeStatus.Of(RuntimeStatusCode.Find_NoPlayersBoth));
			}
			else
			{
				Enter(Step.WaitRestart, RuntimeStatus.Of(RuntimeStatusCode.Find_RestartPeek));
			}
			break;
		case Step.WaitRetry:
			TickWaitRetry();
			break;
		case Step.WaitRestart:
			if (Elapsed() >= 2.0)
			{
				RestartFullPrediction();
			}
			break;
		case Step.Travel:
			break;
		}
	}

	private void TickTravel()
	{
		travel.Tick();
		Status = travel.Status;
		if (travel.IsFailed)
		{
			step = Step.Miss;
			Status = RuntimeStatus.Of(RuntimeStatusCode.Find_PathFailed);
		}
		else if (travel.IsDone)
		{
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
				step = Step.Done;
				Status = RuntimeStatus.Of(RuntimeStatusCode.Find_AtPot, chosen?.KindLabel);
				ExternalCommands.Echo("[找罐] 已到 " + chosen?.KindLabel + "，等待 FATE");
				break;
			}
		}
	}

	private void TickWaitOnline()
	{
		if (Elapsed() >= 10.0)
		{
			if (!TryStartFromOnlineTable(null))
			{
				ExternalCommands.Echo("[找罐] " + tracker.StatusLine + "，改看人");
				BeginTravelObserve(south, AfterTravel.PeekSouth);
			}
		}
		else
		{
			Status = RuntimeStatus.Of(RuntimeStatusCode.Find_WaitOnline, tracker.StatusLine, (int)(10.0 - Elapsed()));
		}
	}

	private bool TryCommitPredicted()
	{
		bool flag = TryGetLiveKind(out PotKind kind, out string reason);
		if (!flag && !tracker.TryGetSoonestTarget(territory, out kind, out reason))
		{
			return false;
		}
		PotSideLayout potSideLayout = IslandPotLayout.ByKind(territory, kind);
		if (potSideLayout == null)
		{
			return false;
		}
		if (chosen != null && chosen.Kind == kind && !observeFallback)
		{
			return false;
		}
		bool flag2 = observeFallback || (chosen != null && afterTravel == AfterTravel.AtPot);
		if (flag2 && !flag && !tracker.HasOnlineData && !tracker.HasCatalog)
		{
			return false;
		}
		if (flag && chosen != null && chosen.Kind != kind)
		{
			ExternalCommands.Echo("[找罐] 本地校准：" + reason + "，改去 " + potSideLayout.KindLabel);
		}
		else
		{
			ExternalCommands.Echo(flag2 ? $"[找罐] {reason}，直达 {potSideLayout.KindLabel}（覆盖看人）" : ("[找罐] " + reason + "，直达 " + potSideLayout.KindLabel));
		}
		BeginTravelPot(potSideLayout);
		return true;
	}

	private bool TryGetLiveKind(out PotKind kind, out string reason)
	{
		kind = PotKind.North;
		reason = string.Empty;
		if (south != null && FateReader.IsActive(south.FateID))
		{
			kind = south.Kind;
			reason = south.KindLabel + " FATE 进行中";
			return true;
		}
		if (north != null && FateReader.IsActive(north.FateID))
		{
			kind = north.Kind;
			reason = north.KindLabel + " FATE 进行中";
			return true;
		}
		return false;
	}

	private void BeginTravelObserve(PotSideLayout side, AfterTravel after)
	{
		observeFallback = true;
		Vector3 dest = IslandPotLayout.RandomObserveStand(side.ObservePoint, side.PotCenter, rng);
		afterTravel = after;
		travel.Begin(territory, dest, side.KindLabel + "观测点");
		Enter(Step.Travel, travel.Status);
	}

	private void BeginTravelPot(PotSideLayout layout)
	{
		observeFallback = false;
		chosen = layout;
		afterTravel = AfterTravel.AtPot;
		travel.Begin(territory, layout.PotCenter, layout.KindLabel);
		Enter(Step.Travel, travel.Status);
	}

	private void TickWaitPeek(PotSideLayout side, AfterTravel which)
	{
		if (PlayerTargeting.HasPlayersNearPot(side.PotCenter))
		{
			CommitPot(side);
		}
		else if (Elapsed() >= 2.0)
		{
			if (which == AfterTravel.PeekSouth)
			{
				ExternalCommands.Echo("[找罐] 南罐附近无人，改去北罐");
				BeginTravelObserve(north, AfterTravel.PeekNorth);
			}
			else
			{
				ExternalCommands.Echo("[找罐] 北罐附近无人");
				Enter(Step.DecideNorth, RuntimeStatus.Of(RuntimeStatusCode.Find_JudgeNorth));
			}
		}
		else
		{
			int value = PlayerTargeting.CountOtherPlayersNear(side.PotCenter, 50f);
			Status = RuntimeStatus.Of(RuntimeStatusCode.Find_PeekPlayers, side.KindLabel, value);
		}
	}

	private void TickWaitRetry()
	{
		int num = ((!(north == null)) ? PlayerTargeting.CountOtherPlayersNear(north.PotCenter, 50f) : 0);
		int num2 = ((!(south == null)) ? PlayerTargeting.CountOtherPlayersNear(south.PotCenter, 50f) : 0);
		if (num > 0 && north != null)
		{
			ExternalCommands.Echo($"[找罐] 等待中北罐来人（{num}），留下等待 FATE");
			CommitPot(north);
		}
		else if (num2 > 0 && south != null)
		{
			ExternalCommands.Echo($"[找罐] 等待中南罐来人（{num2}），留下等待 FATE");
			CommitPot(south);
		}
		else if (Elapsed() >= 120.0)
		{
			Enter(Step.WaitPeekNorth, RuntimeStatus.Of(RuntimeStatusCode.Find_RetryNorthPeek));
		}
		else
		{
			Status = RuntimeStatus.Of(RuntimeStatusCode.Find_WaitRetry, 120 - (int)Elapsed(), num, num2);
		}
	}

	private void CommitPot(PotSideLayout side)
	{
		int value = PlayerTargeting.CountOtherPlayersNear(side.PotCenter, 50f);
		ExternalCommands.Echo($"[找罐] {side.KindLabel}附近有人（{value}），留下等待 FATE");
		BeginTravelPot(side);
	}

	private void EnsureLayouts()
	{
		if ((!(south != null) || !(north != null)) && territory != 0)
		{
			south = IslandPotLayout.South(territory);
			north = IslandPotLayout.North(territory);
		}
	}

	private double Elapsed()
	{
		return (DateTime.UtcNow - stepStartedUTC).TotalSeconds;
	}

	private void Enter(Step step, RuntimeStatus status)
	{
		this.step = step;
		stepStartedUTC = DateTime.UtcNow;
		Status = status;
	}
}
