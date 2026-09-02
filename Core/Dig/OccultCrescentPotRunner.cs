using System;
using System.Collections.Generic;
using System.Numerics;
using OccultPot.Core;
using OccultPot.Core.Adapters;
using OccultPot.Core.Game;
using OmenTools;
using OmenTools.Extensions;

namespace OccultPot.Core.Dig;

internal sealed class OccultCrescentPotRunner
{
	private readonly OccultPotHooks hooks;

	private readonly Queue<PotChatEvent> chatQueue = new Queue<PotChatEvent>();

	private OccultPotStatus status;

	private Vector3 target;

	private readonly List<Vector3> candidates = new List<Vector3>();

	private int candidateIndex;

	private int hintCount;

	private string? lastHint;

	private RuntimeStatus? failure;

	private double phaseStarted;

	private double? elixirUsedAt;

	private double? chestWaitUntil;

	private double? chestGoneAt;

	private bool moveSent;

	private bool elixirSent;

	private bool waypointElixirSent;

	private bool awaitingContinuation;

	private bool foundTreasure;

	private bool lureAcquired;

	private bool lureExhausted;

	private bool sawRewardBuff;

	private bool sawChestObject;

	private bool farewellPending;

	private bool stoppedAtChest;

	private uint startTerritory;

	private string? lastTalkLine;

	private const double CandidateDwellSeconds = 4.0;

	private const double TreasureSpawnWaitSeconds = 20.0;

	private const double ChestGoneSettleSeconds = 1.0;

	private const float ChestDetectRange = 12f;

	private const float ChestOpenRange = 5f;

	public OccultPotStatus Status => status;

	private bool IsGreenDig => !hooks.PreferTp;

	public OccultCrescentPotRunner(OccultPotHooks hooks)
	{
		this.hooks = hooks;
	}

	public OccultPotSnapshot GetSnapshot()
	{
		return new OccultPotSnapshot
		{
			Status = status,
			TargetPosition = target,
			RemainingCandidates = Math.Max(0, candidates.Count - candidateIndex),
			HintCount = hintCount,
			LastHint = lastHint,
			Failure = failure
		};
	}

	public StartResult Start(bool medicineAlreadyUsed = false)
	{
		OccultPotStatus occultPotStatus = status;
		if ((occultPotStatus != OccultPotStatus.Idle && (uint)(occultPotStatus - 5) > 1u) || 1 == 0)
		{
			return StartResult.Failed("挖箱已在运行");
		}
		uint territoryID = hooks.TerritoryID;
		if (OccultPotChestTables.GetAll(territoryID).Count == 0)
		{
			return StartResult.Failed($"区域 {territoryID} 无罐箱点表");
		}
		ResetRuntime();
		startTerritory = territoryID;
		hooks.SnapshotExistingChests();
		Enter(OccultPotStatus.WaitingMedicine);
		return StartResult.Ok();
	}

	public void Stop(StopReason reason = StopReason.UserRequested)
	{
		OccultPotStatus occultPotStatus = status;
		if ((occultPotStatus != OccultPotStatus.Idle && (uint)(occultPotStatus - 5) > 1u) || 1 == 0)
		{
			hooks.StopMove();
			status = ((reason == StopReason.Error) ? OccultPotStatus.Failed : OccultPotStatus.Idle);
			if (reason == StopReason.Error && failure == null)
			{
				failure = RuntimeStatus.Of(RuntimeStatusCode.Dig_Stopped);
			}
			hooks.OnStopped(reason);
		}
	}

	public bool EnqueueChat(string? text)
	{
		if (string.IsNullOrWhiteSpace(text))
		{
			return false;
		}
		string text2 = text.Trim();
		if (text2 == lastTalkLine)
		{
			return false;
		}
		if (!PotHintParser.TryParseChat(text2, out var evt))
		{
			return false;
		}
		OccultPotStatus occultPotStatus = status;
		if ((occultPotStatus == OccultPotStatus.Idle || (uint)(occultPotStatus - 5) <= 1u) ? true : false)
		{
			return false;
		}
		lastTalkLine = text2;
		chatQueue.Enqueue(evt);
		return true;
	}

	public void Tick(double nowSeconds)
	{
		OccultPotStatus occultPotStatus = status;
		if ((occultPotStatus == OccultPotStatus.Idle || (uint)(occultPotStatus - 5) <= 1u) ? true : false)
		{
			return;
		}
		if (hooks.TerritoryID != startTerritory)
		{
			Fail(RuntimeStatusCode.Dig_TerritoryChanged, StopReason.TerritoryChanged);
			return;
		}
		NoteLureBuff();
		DrainChat();
		occultPotStatus = status;
		if ((occultPotStatus != OccultPotStatus.Idle && (uint)(occultPotStatus - 5) > 1u) || 1 == 0)
		{
			switch (status)
			{
			case OccultPotStatus.WaitingMedicine:
				TickWaitingMedicine(nowSeconds);
				break;
			case OccultPotStatus.WaitingHint:
				TickWaitingHint(nowSeconds);
				break;
			case OccultPotStatus.WaitingChest:
				TickWaitingChest(nowSeconds);
				break;
			case OccultPotStatus.OpeningChest:
				TickOpeningChest(nowSeconds);
				break;
			}
		}
	}

	private void DrainChat()
	{
		while (chatQueue.Count > 0)
		{
			PotChatEvent evt = chatQueue.Dequeue();
			switch (evt.Type)
			{
			case PotChatEventType.NeedsMedicine:
				if (status == OccultPotStatus.WaitingChest)
				{
					waypointElixirSent = false;
				}
				else if (status == OccultPotStatus.WaitingHint)
				{
					Enter(OccultPotStatus.WaitingMedicine);
				}
				break;
			case PotChatEventType.MoreMedicine:
				awaitingContinuation = true;
				hooks.PreferRerollChests();
				break;
			case PotChatEventType.Farewell:
				farewellPending = true;
				BeginOpeningIfNeeded();
				break;
			case PotChatEventType.TreasureFound:
				lureAcquired = true;
				foundTreasure = true;
				BeginOpeningIfNeeded();
				break;
			case PotChatEventType.LureExhausted:
				lureExhausted = true;
				break;
			case PotChatEventType.DirectionHint:
				if (!foundTreasure && status != OccultPotStatus.OpeningChest)
				{
					ApplyDirectionHint(evt);
				}
				break;
			}
		}
	}

	private void ApplyDirectionHint(PotChatEvent evt)
	{
		Vector3 playerPosition = hooks.PlayerPosition;
		IReadOnlyList<Vector3> chestTable = hooks.GetChestTable(hooks.TerritoryID);
		IReadOnlyList<Vector3> collection = PotHintParser.FilterCandidates(playerPosition, chestTable, evt.Direction, evt.Distance);
		hintCount++;
		CardinalDirection? direction = evt.Direction;
		object obj;
		if (direction.HasValue)
		{
			CardinalDirection valueOrDefault = direction.GetValueOrDefault();
			HintDistance? distance = evt.Distance;
			obj = ((!distance.HasValue) ? PotHintParser.DirectionLabel(valueOrDefault) : string.Concat(str2: PotHintParser.DistanceLabel(distance.GetValueOrDefault()), str0: PotHintParser.DirectionLabel(valueOrDefault), str1: " · "));
		}
		else
		{
			obj = evt.RawText;
		}
		lastHint = (string?)obj;
		candidates.Clear();
		candidates.AddRange(collection);
		candidateIndex = 0;
		moveSent = false;
		if (candidates.Count == 0)
		{
			Enter(OccultPotStatus.WaitingHint);
			return;
		}
		Enter(OccultPotStatus.WaitingChest);
		AdvanceToCandidate(hooks.NowSeconds);
	}

	private void TickWaitingMedicine(double nowSeconds)
	{
		if (PlayerReader.HasStatus(1531u))
		{
			lureAcquired = true;
			sawRewardBuff = true;
		}
		TryRepeatElixir(nowSeconds);
		double? num = elixirUsedAt;
		if (num.HasValue)
		{
			double valueOrDefault = num.GetValueOrDefault();
			if (nowSeconds - valueOrDefault >= 1.2)
			{
				awaitingContinuation = false;
				TryMountForGreenTravel();
				ResumeHuntOrWaitHint(nowSeconds);
				return;
			}
		}
		if (!elixirSent && nowSeconds - phaseStarted > 60.0)
		{
			Fail(RuntimeStatusCode.Dig_ElixirTimeout);
		}
	}

	private void TickWaitingHint(double nowSeconds)
	{
		TryRepeatElixir(nowSeconds);
		if ((!foundTreasure || !BeginOpeningIfNeeded()) && !TryMountForGreenTravel())
		{
			if (HasPendingCandidates())
			{
				ResumeHuntOrWaitHint(nowSeconds);
			}
			else if (nowSeconds - phaseStarted > 60.0)
			{
				Fail(RuntimeStatusCode.Dig_HintTimeout);
			}
		}
	}

	private void TickWaitingChest(double nowSeconds)
	{
		if (foundTreasure && BeginOpeningIfNeeded())
		{
			return;
		}
		if (candidateIndex >= candidates.Count)
		{
			WaitForReveal(nowSeconds);
			return;
		}
		Vector3 position = (target = candidates[candidateIndex]);
		if (Vector2.Distance(new Vector2(hooks.PlayerPosition.X, hooks.PlayerPosition.Z), new Vector2(position.X, position.Z)) <= 6f)
		{
			if (hooks.IsNavigating())
			{
				hooks.StopMove();
			}
			if (hooks.HasPotChest(12f))
			{
				BeginOpeningIfNeeded();
				return;
			}
			if (!waypointElixirSent)
			{
				if (hooks.TryUseElixir())
				{
					waypointElixirSent = true;
					chestWaitUntil = nowSeconds + 4.0;
				}
				else
				{
					if (nowSeconds - phaseStarted < 12.0)
					{
						return;
					}
					waypointElixirSent = true;
				}
			}
			double valueOrDefault = chestWaitUntil.GetValueOrDefault();
			if (!chestWaitUntil.HasValue)
			{
				valueOrDefault = nowSeconds + 4.0;
				chestWaitUntil = valueOrDefault;
			}
			if (!(nowSeconds < chestWaitUntil.Value))
			{
				TryNextCandidate(nowSeconds);
			}
			return;
		}
		bool num = TryMountForGreenTravel();
		if (num)
		{
			moveSent = false;
		}
		if (!num)
		{
			if (!moveSent)
			{
				if (hooks.MoveTo(position))
				{
					moveSent = true;
				}
			}
			else if (IsGreenDig && !hooks.IsNavigating() && hooks.MoveTo(position))
			{
				moveSent = true;
			}
		}
		if (nowSeconds - phaseStarted > (double)(IsGreenDig ? 180 : 90))
		{
			TryNextCandidate(nowSeconds);
		}
	}

	private void TickOpeningChest(double nowSeconds)
	{
		if (PlayerReader.IsOnMount())
		{
			MountActions.TryDismount();
			return;
		}
		Vector3? vector = hooks.NearbyPotChestPosition(12f);
		if (vector.HasValue)
		{
			Vector3 valueOrDefault = vector.GetValueOrDefault();
			sawChestObject = true;
			chestGoneAt = null;
			if (PlayerReader.DistanceTo(valueOrDefault) > 5f || hooks.IsNavigating())
			{
				stoppedAtChest = false;
				hooks.MoveToInteract(valueOrDefault);
				return;
			}
			if (!stoppedAtChest)
			{
				hooks.StopMove();
				stoppedAtChest = true;
			}
			if (IConditionExtension.get_IsCasting(DService.Instance().Condition))
			{
				return;
			}
			double valueOrDefault2 = chestWaitUntil.GetValueOrDefault();
			if (!chestWaitUntil.HasValue)
			{
				valueOrDefault2 = nowSeconds + 0.35;
				chestWaitUntil = valueOrDefault2;
			}
			if (nowSeconds < chestWaitUntil.Value)
			{
				return;
			}
		}
		if (hooks.TryOpenPotChest(5f))
		{
			return;
		}
		if (vector.HasValue)
		{
			Vector3 valueOrDefault3 = vector.GetValueOrDefault();
			if (PlayerReader.DistanceTo(valueOrDefault3) > 5f)
			{
				hooks.MoveToInteract(valueOrDefault3);
				return;
			}
		}
		hooks.StopMove();
		if (sawChestObject || hooks.TrackedChestGone())
		{
			double valueOrDefault2 = chestGoneAt.GetValueOrDefault();
			if (!chestGoneAt.HasValue)
			{
				valueOrDefault2 = nowSeconds;
				chestGoneAt = valueOrDefault2;
			}
			if (!(nowSeconds - chestGoneAt.Value < 1.0))
			{
				AfterChestOpened();
			}
		}
		else if (!(nowSeconds - phaseStarted < 20.0) && !foundTreasure)
		{
			AfterChestOpened();
		}
	}

	private void WaitForReveal(double nowSeconds)
	{
		if (BeginOpeningIfNeeded())
		{
			return;
		}
		double valueOrDefault = chestWaitUntil.GetValueOrDefault();
		if (!chestWaitUntil.HasValue)
		{
			valueOrDefault = nowSeconds + 20.0;
			chestWaitUntil = valueOrDefault;
		}
		if (!(nowSeconds < chestWaitUntil.Value))
		{
			if (lureExhausted || (sawRewardBuff && !PlayerReader.HasStatus(1531u)))
			{
				AfterChestOpened();
			}
			else if (farewellPending)
			{
				AfterChestOpened();
			}
		}
	}

	private bool BeginOpeningIfNeeded()
	{
		if (status == OccultPotStatus.OpeningChest)
		{
			return true;
		}
		if (!foundTreasure && !hooks.HasPotChest(12f))
		{
			return false;
		}
		hooks.StopMove();
		chestWaitUntil = null;
		Enter(OccultPotStatus.OpeningChest);
		return true;
	}

	private void NoteLureBuff()
	{
		if (PlayerReader.HasStatus(1531u))
		{
			sawRewardBuff = true;
			lureAcquired = true;
		}
	}

	private void AfterChestOpened()
	{
		hooks.StopMove();
		if (farewellPending || lureExhausted)
		{
			FinishCompleted();
		}
		else if (PlayerReader.HasStatus(1531u) || awaitingContinuation)
		{
			awaitingContinuation = true;
			foundTreasure = false;
			sawChestObject = false;
			chestGoneAt = null;
			chestWaitUntil = null;
			candidates.Clear();
			candidateIndex = 0;
			hooks.PreferRerollChests();
			hooks.SnapshotExistingChests();
			Enter(OccultPotStatus.WaitingMedicine);
		}
		else
		{
			FinishCompleted();
		}
	}

	private bool HasPendingCandidates()
	{
		if (candidates.Count > 0)
		{
			return candidateIndex < candidates.Count;
		}
		return false;
	}

	private void ResumeHuntOrWaitHint(double nowSeconds)
	{
		if (HasPendingCandidates())
		{
			moveSent = false;
			waypointElixirSent = false;
			chestWaitUntil = null;
			status = OccultPotStatus.WaitingChest;
			phaseStarted = nowSeconds;
			target = candidates[candidateIndex];
		}
		else
		{
			Enter(OccultPotStatus.WaitingHint);
			TryMountForGreenTravel();
		}
	}

	private bool TryMountForGreenTravel()
	{
		if (!IsGreenDig || PlayerReader.IsOnMount() || PlayerReader.IsInCombat())
		{
			return false;
		}
		if (!MountActions.CanMount())
		{
			return false;
		}
		MountActions.TryMount();
		return true;
	}

	private void TryRepeatElixir(double nowSeconds)
	{
		if ((!elixirSent || status == OccultPotStatus.WaitingMedicine) && hooks.TryUseElixir())
		{
			if (!elixirSent)
			{
				ExternalCommands.Echo("[挖箱] 已使用圣灵药");
			}
			elixirSent = true;
			double valueOrDefault = elixirUsedAt.GetValueOrDefault();
			if (!elixirUsedAt.HasValue)
			{
				valueOrDefault = nowSeconds;
				elixirUsedAt = valueOrDefault;
			}
			hooks.SnapshotExistingChests();
		}
	}

	private void AdvanceToCandidate(double nowSeconds)
	{
		if (candidateIndex < candidates.Count)
		{
			target = candidates[candidateIndex];
			chestWaitUntil = null;
			waypointElixirSent = false;
			phaseStarted = nowSeconds;
			moveSent = !IsGreenDig && hooks.MoveTo(target);
		}
	}

	private void TryNextCandidate(double nowSeconds)
	{
		candidateIndex++;
		moveSent = false;
		chestWaitUntil = null;
		if (candidateIndex < candidates.Count)
		{
			AdvanceToCandidate(nowSeconds);
		}
	}

	private void FinishCompleted()
	{
		hooks.StopMove();
		status = OccultPotStatus.Completed;
		hooks.OnStopped(StopReason.Completed);
	}

	private void Enter(OccultPotStatus status)
	{
		this.status = status;
		phaseStarted = hooks.NowSeconds;
		if (status == OccultPotStatus.WaitingMedicine)
		{
			elixirSent = false;
			elixirUsedAt = null;
			hooks.SnapshotExistingChests();
		}
		if (status == OccultPotStatus.OpeningChest)
		{
			chestGoneAt = null;
			chestWaitUntil = null;
			stoppedAtChest = false;
		}
	}

	private void Fail(RuntimeStatusCode code, StopReason reason = StopReason.Error)
	{
		failure = RuntimeStatus.Of(code);
		hooks.StopMove();
		status = OccultPotStatus.Failed;
		hooks.OnStopped(reason);
	}

	private void ResetRuntime()
	{
		chatQueue.Clear();
		candidates.Clear();
		candidateIndex = 0;
		hintCount = 0;
		lastHint = null;
		failure = null;
		target = Vector3.Zero;
		moveSent = false;
		elixirSent = false;
		waypointElixirSent = false;
		elixirUsedAt = null;
		chestWaitUntil = null;
		chestGoneAt = null;
		awaitingContinuation = false;
		foundTreasure = false;
		lureAcquired = false;
		lureExhausted = false;
		sawRewardBuff = false;
		sawChestObject = false;
		farewellPending = false;
		stoppedAtChest = false;
		lastTalkLine = null;
		TreasureChestInteractor.ClearSnapshot();
	}
}
