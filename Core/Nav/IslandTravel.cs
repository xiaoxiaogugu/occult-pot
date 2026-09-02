using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using OccultPot.Core;
using OccultPot.Core.Adapters;
using OccultPot.Core.Game;
using OmenTools.Interop.Game.ExecuteCommand.Implementations;

namespace OccultPot.Core.Nav;

internal sealed class IslandTravel
{
	private enum Phase
	{
		Idle,
		WaitStopped,
		WaitMounted,
		WaitWalkSource,
		WaitReadyPtp,
		WaitPtp,
		WaitWalkDest,
		WaitReturn,
		Done,
		Failed
	}

	private enum AfterReturn
	{
		WalkDest,
		Ptp
	}

	private const float PtpArriveRadius = 50f;

	private const float DestArriveRadius = 12f;

	private const double ReturnWaitSeconds = 15.0;

	private const double PtpWaitSeconds = 12.0;

	private const double PtpResendSeconds = 2.0;

	private const int PtpMaxSends = 4;

	private const double MountWaitSeconds = 5.0;

	private const double AfterMountSeconds = 1.0;

	private const double StopSettleSeconds = 0.5;

	private const double StopGiveUpSeconds = 3.0;

	private const double PtpAfterDismountSeconds = 2.0;

	private const double PtpReadySettleSeconds = 0.5;

	private const double SourceWalkSeconds = 90.0;

	private const double DestWalkSeconds = 120.0;

	private readonly VNavController vnav;

	private Phase phase;

	private Phase afterStopped;

	private RuntimeStatus afterStoppedStatus;

	private Action? pendingAfterStop;

	private Phase afterMount;

	private RuntimeStatus afterMountStatus;

	private bool mountIssued;

	private DateTime mountWaitUTC;

	private DateTime? stopSettleUTC;

	private DateTime? mountedAtUTC;

	private Vector3 finalDest;

	private Vector3 walkDest;

	private float walkArrive;

	private double walkTimeout;

	private AethernetRoute? route;

	private IslandAethernetShard? ptpShard;

	private DateTime phaseUTC;

	private DateTime lastActionUTC;

	private bool vnavStopIssued;

	private bool ptpIssued;

	private bool ptpSucceeded;

	private int ptpSendCount;

	private DateTime lastPtpUTC;

	private DateTime? ptpReadyUTC;

	private DateTime? ptpFarSinceUTC;

	private bool dismountIssued;

	private DateTime dismountUTC;

	private DateTime? stopBeganUTC;

	private DateTime lastStopUTC;

	private string label = "";

	private ushort territory;

	private AfterReturn afterReturn;

	private bool returnSent;

	internal RuntimeStatus Status { get; private set; } = RuntimeStatus.Of(RuntimeStatusCode.SessionNotStarted);

	internal bool IsIdle => phase == Phase.Idle;

	internal bool IsDone => phase == Phase.Done;

	internal bool IsFailed => phase == Phase.Failed;

	internal bool IsRunning
	{
		get
		{
			Phase phase = this.phase;
			bool flag = ((phase == Phase.Idle || (uint)(phase - 8) <= 1u) ? true : false);
			return !flag;
		}
	}

	internal bool PtpSucceeded => ptpSucceeded;

	internal IslandTravel(VNavController vnav)
	{
		this.vnav = vnav;
	}

	internal void Stop()
	{
		ClearPending();
		vnav.Stop();
		phase = Phase.Idle;
		route = null;
		ptpShard = null;
		ptpIssued = false;
		ptpSucceeded = false;
		returnSent = false;
		Status = RuntimeStatus.Of(RuntimeStatusCode.Travel_Stopped);
	}

	internal void Begin(ushort territory, Vector3 dest, string label)
	{
		Stop();
		this.label = label;
		this.territory = territory;
		finalDest = dest;
		walkDest = dest;
		Vector3? position = PlayerReader.Position;
		if (!position.HasValue)
		{
			phase = Phase.Failed;
			Status = RuntimeStatus.Of(RuntimeStatusCode.Travel_NoPosition);
			return;
		}
		if (PlayerReader.DistanceTo(finalDest) <= 12f)
		{
			Finish(RuntimeStatus.Of(RuntimeStatusCode.Travel_AlreadyAt, label));
			return;
		}
		route = AethernetRouter.Decide(territory, position.Value, finalDest);
		if (route.Kind == AethernetRouteKind.WalkTeleportWalk && route.Source != null && route.Destination != null)
		{
			ExternalCommands.Echo($"[寻路] Bocchi 魔路 {route.Source.Name} → {route.Destination.Name}（代价 {route.Cost:0}）再去{label}");
			BeginHop(route.Source, route.Destination);
		}
		else
		{
			ExternalCommands.Echo($"[寻路] Bocchi 直走去{label}（代价 {route.Cost:0}）");
			StartWalkDest();
		}
	}

	internal void NoteArrivalIfClose()
	{
		if (!ptpSucceeded && !(ptpShard == null))
		{
			Phase phase = this.phase;
			bool flag = (uint)(phase - 4) <= 1u;
			if (flag && (PlayerReader.DistanceTo(ptpShard.Landing) <= 50f || AethernetRouter.NearLanding(ptpShard, 50f)))
			{
				MarkPtpArrived();
			}
		}
	}

	internal void Tick()
	{
		NoteArrivalIfClose();
		switch (phase)
		{
		case Phase.WaitStopped:
			TickWaitStopped();
			break;
		case Phase.WaitMounted:
			TickWaitMounted();
			break;
		case Phase.WaitWalkSource:
			TickWaitWalkSource();
			break;
		case Phase.WaitReadyPtp:
			TickWaitReadyPtp();
			break;
		case Phase.WaitPtp:
			TickWaitPtp();
			break;
		case Phase.WaitWalkDest:
			TickWaitWalkDest();
			break;
		case Phase.WaitReturn:
			TickWaitReturn();
			break;
		}
	}

	private void BeginHop(IslandAethernetShard src, IslandAethernetShard dst)
	{
		if (AethernetRouter.NearLanding(dst))
		{
			StartWalkDest();
		}
		else if (AethernetRouter.AtSource(src, territory))
		{
			StartPtp(dst);
		}
		else
		{
			StartWalk(src.Stand, 4.7f, 90.0, Phase.WaitWalkSource, RuntimeStatus.Of(RuntimeStatusCode.Travel_ToAetheryte, src.Name));
		}
	}

	private void StartReturn(AfterReturn next)
	{
		afterReturn = next;
		returnSent = false;
		RequestStopThen(null, Phase.WaitReturn, RuntimeStatus.Of(RuntimeStatusCode.Travel_PrepareReturn));
	}

	private void TickWaitReturn()
	{
		if (AethernetRouter.NearCamp(route?.Source))
		{
			FinishReturn();
		}
		else if (!returnSent)
		{
			if ((PlayerReader.IsBusy() || PlayerReader.IsInCombat()) && Elapsed() < 3.0)
			{
				Status = RuntimeStatus.Of(RuntimeStatusCode.Travel_ReturnIdle);
				return;
			}
			ExternalCommands.Run("/ac 返回");
			returnSent = true;
			phaseUTC = DateTime.UtcNow;
			Status = RuntimeStatus.Of(RuntimeStatusCode.Travel_ReturnCamp);
		}
		else if (Elapsed() >= 15.0)
		{
			FallbackAfterFailedReturn();
		}
		else
		{
			Status = RuntimeStatus.Of(RuntimeStatusCode.Travel_Returning);
		}
	}

	private void FinishReturn()
	{
		if (afterReturn == AfterReturn.Ptp && route?.Destination != null)
		{
			StartPtp(route.Destination);
		}
		else
		{
			StartWalkDest();
		}
	}

	private void FallbackAfterFailedReturn()
	{
		IslandAethernetShard islandAethernetShard = route?.Destination;
		Vector3? from = PlayerReader.Position;
		if (islandAethernetShard == null || !from.HasValue)
		{
			StartWalkDest();
			return;
		}
		if (AethernetRouter.NearLanding(islandAethernetShard))
		{
			StartWalkDest();
			return;
		}
		IReadOnlyList<IslandAethernetShard> readOnlyList = IslandAethernet.ForTerritory(territory);
		IslandAethernetShard islandAethernetShard2 = ((readOnlyList.Count == 0) ? null : readOnlyList.OrderBy((IslandAethernetShard s) => Vector3.DistanceSquared(from.Value, s.Stand)).First());
		if (islandAethernetShard2 == null || islandAethernetShard2.Name == islandAethernetShard.Name)
		{
			StartWalkDest();
		}
		else
		{
			BeginHop(islandAethernetShard2, islandAethernetShard);
		}
	}

	private void StartWalkDest()
	{
		StartWalk(finalDest, 12f, 120.0, Phase.WaitWalkDest, RuntimeStatus.Of(RuntimeStatusCode.Travel_ToDest, label));
	}

	private void StartWalk(Vector3 dest, float arrive, double timeout, Phase waitPhase, RuntimeStatus status)
	{
		walkDest = dest;
		walkArrive = arrive;
		walkTimeout = timeout;
		afterMount = waitPhase;
		afterMountStatus = status;
		mountIssued = false;
		RequestStopThen(AfterStopForWalk, Phase.WaitMounted, RuntimeStatus.Of(RuntimeStatusCode.Travel_WaitMount));
	}

	private void AfterStopForWalk()
	{
		mountIssued = false;
		mountWaitUTC = DateTime.UtcNow;
		mountedAtUTC = (PlayerReader.IsOnMount() ? new DateTime?(DateTime.UtcNow) : ((DateTime?)null));
	}

	private void TickWaitMounted()
	{
		if (PlayerReader.IsOnMount())
		{
			DateTime valueOrDefault = mountedAtUTC.GetValueOrDefault();
			if (!mountedAtUTC.HasValue)
			{
				valueOrDefault = DateTime.UtcNow;
				mountedAtUTC = valueOrDefault;
			}
			if (!((DateTime.UtcNow - mountedAtUTC.Value).TotalSeconds < 1.0))
			{
				StartNavAfterMount();
			}
			return;
		}
		if (mountWaitUTC == default(DateTime))
		{
			mountWaitUTC = DateTime.UtcNow;
		}
		if ((DateTime.UtcNow - mountWaitUTC).TotalSeconds >= (mountIssued ? 5.0 : 2.0))
		{
			StartNavAfterMount();
			return;
		}
		if (!mountIssued && MountActions.TryMount())
		{
			mountIssued = true;
		}
		Status = RuntimeStatus.Of(RuntimeStatusCode.Travel_WaitMount);
	}

	private void StartNavAfterMount()
	{
		vnav.MoveTo(walkDest);
		lastActionUTC = DateTime.UtcNow;
		Enter(afterMount, afterMountStatus);
	}

	private void TickWaitWalkSource()
	{
		if (PlayerReader.DistanceTo(walkDest) <= walkArrive || Elapsed() >= walkTimeout)
		{
			if (route?.Destination != null)
			{
				StartPtp(route.Destination);
			}
			else
			{
				StartWalkDest();
			}
			return;
		}
		if (!vnav.IsRunning())
		{
			vnav.MoveTo(walkDest);
		}
		Status = RuntimeStatus.Of(RuntimeStatusCode.Travel_ToSource, route?.Source?.Name);
	}

	private void TickWaitWalkDest()
	{
		if (vnav.HasArrived(walkDest, walkArrive) || Elapsed() >= walkTimeout)
		{
			Finish(RuntimeStatus.Literal("[找罐] 已到" + label));
			return;
		}
		if (!vnav.IsRunning())
		{
			vnav.MoveTo(walkDest);
		}
		Status = vnav.IsReady()
			? RuntimeStatus.Of(RuntimeStatusCode.Travel_ToDest, label)
			: RuntimeStatus.Of(RuntimeStatusCode.Travel_WaitVnav, vnav.LastDetail);
	}

	private void StartPtp(IslandAethernetShard shard)
	{
		ptpShard = shard;
		ptpIssued = false;
		ptpSucceeded = false;
		ptpSendCount = 0;
		ptpReadyUTC = null;
		ptpFarSinceUTC = null;
		dismountIssued = false;
		if (route?.Source != null && AethernetRouter.AtSource(route.Source, territory))
		{
			if (PlayerReader.IsOnMount())
			{
				TryDismountOnce();
			}
			FirePtp();
			Enter(Phase.WaitPtp, RuntimeStatus.Of(RuntimeStatusCode.Travel_Ptp, shard.Name));
		}
		else
		{
			RequestStopThen(null, Phase.WaitReadyPtp, RuntimeStatus.Of(RuntimeStatusCode.Travel_PreparePtp, shard.Name));
		}
	}

	private void TickWaitReadyPtp()
	{
		if (ptpShard == null)
		{
			phase = Phase.Failed;
			return;
		}
		if (!EnsureVnavStopped())
		{
			Status = RuntimeStatus.Of(RuntimeStatusCode.Travel_WaitStopPtp);
			return;
		}
		if (PlayerReader.IsOnMount())
		{
			TryDismountOnce();
			Status = RuntimeStatus.Of(RuntimeStatusCode.Travel_DismountPtp, ptpShard.Name);
			if ((DateTime.UtcNow - dismountUTC).TotalSeconds < 2.0)
			{
				return;
			}
		}
		if (PlayerReader.IsBusy() && Elapsed() < 3.0)
		{
			Status = RuntimeStatus.Of(RuntimeStatusCode.Travel_WaitIdlePtp);
			return;
		}
		if (!PlayerReader.IsBusy())
		{
			DateTime valueOrDefault = ptpReadyUTC.GetValueOrDefault();
			if (!ptpReadyUTC.HasValue)
			{
				valueOrDefault = DateTime.UtcNow;
				ptpReadyUTC = valueOrDefault;
			}
			if ((DateTime.UtcNow - ptpReadyUTC.Value).TotalSeconds < 0.5)
			{
				return;
			}
		}
		FirePtp();
		Enter(Phase.WaitPtp, RuntimeStatus.Of(RuntimeStatusCode.Travel_WaitPtpArrive, ptpShard.Name));
	}

	private void TickWaitPtp()
	{
		if (ptpShard == null)
		{
			phase = Phase.Failed;
		}
		else if (HasPtpArrived())
		{
			StartWalkDest();
		}
		else if (!ptpIssued)
		{
			Status = RuntimeStatus.Of(RuntimeStatusCode.Travel_WaitStopPtpNamed, ptpShard.Name);
		}
		else if (TryResendPtp())
		{
			Status = RuntimeStatus.Of(RuntimeStatusCode.Travel_PtpResend, ptpShard.Name, ptpSendCount, 4);
		}
		else if (Elapsed() >= 12.0)
		{
			StartWalkDest();
		}
		else
		{
			Status = RuntimeStatus.Of(RuntimeStatusCode.Travel_WaitPtp, ptpShard.Name, ptpSendCount, 4);
		}
	}

	private bool HasPtpArrived()
	{
		if (ptpSucceeded)
		{
			return true;
		}
		if (ptpShard != null && PlayerReader.DistanceTo(ptpShard.Landing) <= 50f)
		{
			MarkPtpArrived();
			return true;
		}
		return false;
	}

	private void MarkPtpArrived()
	{
		if (!ptpSucceeded)
		{
			ptpSucceeded = true;
			ptpFarSinceUTC = null;
		}
	}

	private void FirePtp()
	{
		if (!(ptpShard == null) && !ptpSucceeded)
		{
			if (PlayerReader.DistanceTo(ptpShard.Landing) <= 50f)
			{
				MarkPtpArrived();
				return;
			}
			ExternalCommands.Run("/pdr ptp " + ptpShard.Name);
			ptpSendCount++;
			ptpIssued = true;
			lastPtpUTC = DateTime.UtcNow;
			ptpFarSinceUTC = DateTime.UtcNow;
		}
	}

	private bool TryResendPtp()
	{
		if (ptpSucceeded || HasPtpArrived())
		{
			return false;
		}
		if (ptpSendCount >= 4)
		{
			return false;
		}
		DateTime valueOrDefault = ptpFarSinceUTC.GetValueOrDefault();
		if (!ptpFarSinceUTC.HasValue)
		{
			valueOrDefault = DateTime.UtcNow;
			ptpFarSinceUTC = valueOrDefault;
		}
		if ((DateTime.UtcNow - ptpFarSinceUTC.Value).TotalSeconds < 2.0)
		{
			return false;
		}
		if ((DateTime.UtcNow - lastPtpUTC).TotalSeconds < 2.0)
		{
			return false;
		}
		FirePtp();
		return !ptpSucceeded;
	}

	private void TryDismountOnce()
	{
		if (!dismountIssued && PlayerReader.IsOnMount())
		{
			MountCommand.Dismount();
			dismountIssued = true;
			dismountUTC = DateTime.UtcNow;
		}
	}

	private void RequestStopThen(Action? action, Phase resume, RuntimeStatus resumeStatus)
	{
		pendingAfterStop = action;
		afterStopped = resume;
		afterStoppedStatus = resumeStatus;
		stopSettleUTC = null;
		EnsureVnavStopped();
		Enter(Phase.WaitStopped, RuntimeStatus.Of(RuntimeStatusCode.Travel_WaitStop));
	}

	private void TickWaitStopped()
	{
		if (AethernetRouter.PlayerNearCamp(territory, 80f))
		{
			FirePendingThenResume();
			return;
		}
		if (!EnsureVnavStopped())
		{
			stopSettleUTC = null;
			Status = RuntimeStatus.Of(RuntimeStatusCode.Travel_WaitStop);
			return;
		}
		if (PlayerReader.IsBusy())
		{
			DateTime valueOrDefault = stopSettleUTC.GetValueOrDefault();
			if (!stopSettleUTC.HasValue)
			{
				valueOrDefault = DateTime.UtcNow;
				stopSettleUTC = valueOrDefault;
			}
			if ((DateTime.UtcNow - stopSettleUTC.Value).TotalSeconds < 3.0)
			{
				Status = RuntimeStatus.Of(RuntimeStatusCode.Travel_WaitIdleWalk);
				return;
			}
		}
		else
		{
			DateTime valueOrDefault = stopSettleUTC.GetValueOrDefault();
			if (!stopSettleUTC.HasValue)
			{
				valueOrDefault = DateTime.UtcNow;
				stopSettleUTC = valueOrDefault;
			}
			if ((DateTime.UtcNow - stopSettleUTC.Value).TotalSeconds < 0.5)
			{
				return;
			}
		}
		FirePendingThenResume();
	}

	private void FirePendingThenResume()
	{
		Action? action = pendingAfterStop;
		pendingAfterStop = null;
		action?.Invoke();
		lastActionUTC = DateTime.UtcNow;
		Enter(afterStopped, afterStoppedStatus);
	}

	private void ClearPending()
	{
		pendingAfterStop = null;
		vnavStopIssued = false;
		stopBeganUTC = null;
		mountIssued = false;
		dismountIssued = false;
		returnSent = false;
		stopSettleUTC = null;
		mountedAtUTC = null;
		mountWaitUTC = default(DateTime);
	}

	private bool EnsureVnavStopped()
	{
		if (!vnav.IsRunning())
		{
			vnavStopIssued = false;
			stopBeganUTC = null;
			return true;
		}
		DateTime utcNow = DateTime.UtcNow;
		if (!vnavStopIssued)
		{
			vnav.Stop();
			vnavStopIssued = true;
			stopBeganUTC = utcNow;
			lastStopUTC = utcNow;
			return false;
		}
		if ((utcNow - lastStopUTC).TotalSeconds >= 1.0)
		{
			vnav.Stop();
			lastStopUTC = utcNow;
		}
		DateTime? dateTime = stopBeganUTC;
		if (dateTime.HasValue)
		{
			DateTime valueOrDefault = dateTime.GetValueOrDefault();
			return (utcNow - valueOrDefault).TotalSeconds >= 3.0;
		}
		return false;
	}

	private void Finish(RuntimeStatus status)
	{
		phase = Phase.Done;
		Status = status;
	}

	private void Enter(Phase phase, RuntimeStatus status)
	{
		this.phase = phase;
		phaseUTC = DateTime.UtcNow;
		Status = status;
	}

	private double Elapsed()
	{
		return (DateTime.UtcNow - phaseUTC).TotalSeconds;
	}
}
