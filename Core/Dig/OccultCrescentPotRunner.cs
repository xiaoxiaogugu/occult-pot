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
    private readonly Queue<PotChatEvent> chatQueue = [];
    private readonly List<Vector3> candidates = [];

    private OccultPotStatus status;
    private Vector3 target;
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
    private bool continuationReady;
    private bool foundTreasure;
    private bool lureAcquired;
    private bool lureExhausted;
    private bool sawRewardBuff;
    private bool sawChestObject;
    private bool farewellPending;
    private bool stoppedAtChest;
    private uint startTerritory;
    private string? lastTalkLine;

    private const double CandidateDwellSeconds           = 4.0;
    private const double TreasureSpawnWaitSeconds        = 20.0;
    private const double ChestGoneSettleSeconds          = 1.0;
    private const double ContinuationElixirDelaySeconds  = 8.0;
    private const float ChestDetectRange           = 12f;
    private const float ChestApproachRange         = 80f;
    private const float ChestOpenRange             = 5f;

    public OccultPotStatus Status => status;

    private bool IsGreenDig => !hooks.PreferTp;

    private bool IsActive =>
        status is not OccultPotStatus.Idle
            and not OccultPotStatus.Completed
            and not OccultPotStatus.Failed;

    public OccultCrescentPotRunner(OccultPotHooks hooks) =>
        this.hooks = hooks;

    public OccultPotSnapshot GetSnapshot() =>
        new()
        {
            Status              = status,
            TargetPosition      = target,
            RemainingCandidates = Math.Max(0, candidates.Count - candidateIndex),
            HintCount           = hintCount,
            LastHint            = lastHint,
            Failure             = failure,
        };

    public StartResult Start(bool medicineAlreadyUsed = false)
    {
        if (IsActive)
            return StartResult.Failed("挖箱已在运行");

        var territoryID = hooks.TerritoryID;
        if (OccultPotChestTables.GetAll(territoryID).Count == 0)
            return StartResult.Failed($"区域 {territoryID} 无罐箱点表");

        ResetRuntime();
        startTerritory = territoryID;
        hooks.SnapshotExistingChests();
        if (medicineAlreadyUsed || InventoryReader.GetElixirRecastRemaining() > 0.15f)
        {
            elixirSent   = true;
            elixirUsedAt = hooks.NowSeconds;
            Enter(OccultPotStatus.WaitingHint);
            return StartResult.Ok();
        }

        Enter(OccultPotStatus.WaitingMedicine);
        return StartResult.Ok();
    }

    public void Stop(StopReason reason = StopReason.UserRequested)
    {
        if (!IsActive)
            return;

        hooks.StopMove();
        status = reason == StopReason.Error ? OccultPotStatus.Failed : OccultPotStatus.Idle;
        if (reason == StopReason.Error && failure == null)
            failure = RuntimeStatus.Of(RuntimeStatusCode.Dig_Stopped);
        hooks.OnStopped(reason);
    }

    public bool EnqueueChat(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var trimmed = text.Trim();
        if (trimmed == lastTalkLine)
            return false;
        if (!PotHintParser.TryParseChat(trimmed, out var evt))
            return false;
        if (!IsActive)
            return false;

        lastTalkLine = trimmed;
        chatQueue.Enqueue(evt);
        return true;
    }

    public void Tick(double nowSeconds)
    {
        if (!IsActive)
            return;
        hooks.Tick();
        if (hooks.TerritoryID != startTerritory)
        {
            Fail(RuntimeStatusCode.Dig_TerritoryChanged, StopReason.TerritoryChanged);
            return;
        }

        NoteLureBuff();
        DrainChat();
        if (!IsActive)
            return;

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

    private void DrainChat()
    {
        while (chatQueue.Count > 0)
        {
            var evt = chatQueue.Dequeue();
            switch (evt.Type)
            {
                case PotChatEventType.NeedsMedicine:
                    if (status == OccultPotStatus.WaitingChest)
                        waypointElixirSent = false;
                    else if (status == OccultPotStatus.WaitingHint)
                        Enter(OccultPotStatus.WaitingMedicine);
                    break;
                case PotChatEventType.MoreMedicine:
                    awaitingContinuation = true;
                    continuationReady    = false;
                    hooks.PreferRerollChests();
                    break;
                case PotChatEventType.ContinuationReady:
                    awaitingContinuation = true;
                    continuationReady    = true;
                    break;
                case PotChatEventType.ElixirRejected:
                    elixirSent   = false;
                    elixirUsedAt = null;
                    break;
                case PotChatEventType.Farewell:
                    farewellPending = true;
                    BeginOpeningIfNeeded();
                    break;
                case PotChatEventType.TreasureFound:
                    lureAcquired  = true;
                    foundTreasure = true;
                    BeginOpeningIfNeeded();
                    break;
                case PotChatEventType.LureExhausted:
                    lureExhausted = true;
                    break;
                case PotChatEventType.DirectionHint:
                    if (!foundTreasure && status != OccultPotStatus.OpeningChest)
                        ApplyDirectionHint(evt);
                    break;
            }
        }
    }

    private void ApplyDirectionHint(PotChatEvent evt)
    {
        var playerPosition = hooks.PlayerPosition;
        var filtered       = PotHintParser.ResolveCandidates(
            playerPosition,
            hooks.GetChestTable(hooks.TerritoryID),
            OccultPotChestTables.GetAll(hooks.TerritoryID),
            evt.Direction,
            evt.Distance);
        hintCount++;
        lastHint = FormatHint(evt);
        candidates.Clear();
        candidates.AddRange(filtered);
        candidateIndex = 0;
        moveSent       = false;
        if (candidates.Count == 0)
        {
            Enter(OccultPotStatus.WaitingHint);
            return;
        }

        Enter(OccultPotStatus.WaitingChest);
        AdvanceToCandidate(hooks.NowSeconds);
    }

    private static string FormatHint(PotChatEvent evt)
    {
        if (evt.Direction is not { } direction)
            return evt.RawText;
        if (evt.Distance is not { } distance)
            return PotHintParser.DirectionLabel(direction);
        return $"{PotHintParser.DirectionLabel(direction)} · {PotHintParser.DistanceLabel(distance)}";
    }

    private void TickWaitingMedicine(double nowSeconds)
    {
        if (PlayerReader.HasStatus(1531u))
        {
            lureAcquired  = true;
            sawRewardBuff = true;
        }

        NoteElixirAlreadyUsed(nowSeconds);

        // 续罐要等「能够告知第二处」；开箱动画里喝会被系统拒掉。
        if (awaitingContinuation && !continuationReady && nowSeconds - phaseStarted < ContinuationElixirDelaySeconds)
            return;

        if (!PlayerReader.IsBusy())
            TryRepeatElixir(nowSeconds);
        if (elixirUsedAt is { } usedAt && nowSeconds - usedAt >= 1.2)
        {
            awaitingContinuation = false;
            ResumeHuntOrWaitHint(nowSeconds);
            return;
        }

        if (!elixirSent && nowSeconds - phaseStarted > 60.0)
            Fail(RuntimeStatusCode.Dig_ElixirTimeout);
    }

    private void TickWaitingHint(double nowSeconds)
    {
        TryRepeatElixir(nowSeconds);
        if (foundTreasure && BeginOpeningIfNeeded())
            return;
        if (TryMountForGreenTravel())
            return;
        if (HasPendingCandidates())
        {
            ResumeHuntOrWaitHint(nowSeconds);
            return;
        }

        if (nowSeconds - phaseStarted > 60.0)
            Fail(RuntimeStatusCode.Dig_HintTimeout);
    }

    private void TickWaitingChest(double nowSeconds)
    {
        if (foundTreasure && BeginOpeningIfNeeded())
            return;
        if (candidateIndex >= candidates.Count)
        {
            WaitForReveal(nowSeconds);
            return;
        }

        var position = target = candidates[candidateIndex];
        if (Vector2.Distance(
                new Vector2(hooks.PlayerPosition.X, hooks.PlayerPosition.Z),
                new Vector2(position.X, position.Z)) <= 6f)
        {
            TickWaitingChestAtWaypoint(nowSeconds);
            return;
        }

        var mounting = TryMountForGreenTravel();
        if (mounting)
            moveSent = false;
        else if (!moveSent)
        {
            if (hooks.MoveTo(position))
                moveSent = true;
        }
        else if (!hooks.IsNavigating() && hooks.MoveTo(position))
        {
            moveSent = true;
        }

        if (nowSeconds - phaseStarted > (IsGreenDig ? 180 : 90))
            TryNextCandidate(nowSeconds);
    }

    private void TickWaitingChestAtWaypoint(double nowSeconds)
    {
        hooks.StopMove();
        if (hooks.HasPotChest(ChestDetectRange))
        {
            BeginOpeningIfNeeded();
            return;
        }

        if (!waypointElixirSent)
        {
            if (InventoryReader.GetElixirRecastRemaining() > 0.15f)
            {
                waypointElixirSent = true;
                TryNextCandidate(nowSeconds);
                return;
            }

            if (hooks.TryUseElixir())
            {
                waypointElixirSent = true;
                chestWaitUntil     = nowSeconds + CandidateDwellSeconds;
            }
            else
            {
                if (nowSeconds - phaseStarted < 12.0)
                    return;
                waypointElixirSent = true;
            }
        }

        chestWaitUntil ??= nowSeconds + CandidateDwellSeconds;
        if (nowSeconds >= chestWaitUntil.Value)
            TryNextCandidate(nowSeconds);
    }

    private void TickOpeningChest(double nowSeconds)
    {
        if (PlayerReader.IsOnMount())
        {
            MountActions.TryDismount();
            return;
        }

        var chest = hooks.NearbyPotChestPosition(ChestApproachRange);
        if (chest is { } chestPos)
        {
            sawChestObject = true;
            chestGoneAt    = null;
            if (PlayerReader.DistanceTo(chestPos) > ChestOpenRange || hooks.IsNavigating())
            {
                stoppedAtChest = false;
                hooks.MoveToInteract(chestPos);
                return;
            }

            if (!stoppedAtChest)
            {
                hooks.StopMove();
                stoppedAtChest = true;
            }

            if (DService.Instance().Condition.IsCasting)
                return;

            chestWaitUntil ??= nowSeconds + 0.35;
            if (nowSeconds < chestWaitUntil.Value)
                return;
        }

        if (hooks.TryOpenPotChest(ChestOpenRange))
            return;

        if (chest is { } stillThere && PlayerReader.DistanceTo(stillThere) > ChestOpenRange)
        {
            hooks.MoveToInteract(stillThere);
            return;
        }

        hooks.StopMove();
        if (sawChestObject || hooks.TrackedChestGone())
        {
            chestGoneAt ??= nowSeconds;
            if (nowSeconds - chestGoneAt.Value >= ChestGoneSettleSeconds)
                AfterChestOpened();
            return;
        }

        if (nowSeconds - phaseStarted >= TreasureSpawnWaitSeconds && !foundTreasure)
            AfterChestOpened();
    }

    private void WaitForReveal(double nowSeconds)
    {
        if (BeginOpeningIfNeeded())
            return;

        chestWaitUntil ??= nowSeconds + TreasureSpawnWaitSeconds;
        if (nowSeconds < chestWaitUntil.Value)
            return;

        if (lureExhausted || (sawRewardBuff && !PlayerReader.HasStatus(1531u)) || farewellPending)
            AfterChestOpened();
    }

    private bool BeginOpeningIfNeeded()
    {
        if (status == OccultPotStatus.OpeningChest)
            return true;
        if (!foundTreasure && !hooks.HasPotChest(ChestDetectRange))
            return false;

        hooks.StopMove();
        chestWaitUntil = null;
        Enter(OccultPotStatus.OpeningChest);
        return true;
    }

    private void NoteLureBuff()
    {
        if (!PlayerReader.HasStatus(1531u))
            return;
        sawRewardBuff = true;
        lureAcquired  = true;
    }

    private void AfterChestOpened()
    {
        hooks.StopMove();
        if (farewellPending || lureExhausted)
        {
            FinishCompleted();
            return;
        }

        if (PlayerReader.HasStatus(1531u) || awaitingContinuation)
        {
            awaitingContinuation = true;
            foundTreasure        = false;
            sawChestObject       = false;
            chestGoneAt          = null;
            chestWaitUntil       = null;
            candidates.Clear();
            candidateIndex = 0;
            hooks.PreferRerollChests();
            hooks.SnapshotExistingChests();
            Enter(OccultPotStatus.WaitingMedicine);
            return;
        }

        FinishCompleted();
    }

    private bool HasPendingCandidates() =>
        candidates.Count > 0 && candidateIndex < candidates.Count;

    private void ResumeHuntOrWaitHint(double nowSeconds)
    {
        if (HasPendingCandidates())
        {
            moveSent           = false;
            waypointElixirSent = false;
            chestWaitUntil     = null;
            status             = OccultPotStatus.WaitingChest;
            phaseStarted       = nowSeconds;
            target             = candidates[candidateIndex];
            return;
        }

        Enter(OccultPotStatus.WaitingHint);
        TryMountForGreenTravel();
    }

    private bool TryMountForGreenTravel()
    {
        if (!IsGreenDig || PlayerReader.IsOnMount() || PlayerReader.IsInCombat())
            return false;
        if (!MountActions.CanMount())
            return false;
        MountActions.TryMount();
        return true;
    }

    private void NoteElixirAlreadyUsed(double nowSeconds)
    {
        if (elixirUsedAt != null || InventoryReader.GetElixirRecastRemaining() <= 0.15f)
            return;

        elixirSent   = true;
        elixirUsedAt = nowSeconds;
    }

    private void TryRepeatElixir(double nowSeconds)
    {
        if (PlayerReader.IsBusy())
            return;
        if ((elixirSent && status != OccultPotStatus.WaitingMedicine) || !hooks.TryUseElixir())
            return;

        if (!elixirSent)
            ExternalCommands.Echo("[挖箱] 已使用圣灵药");
        elixirSent    = true;
        elixirUsedAt ??= nowSeconds;
        hooks.SnapshotExistingChests();
    }

    private void AdvanceToCandidate(double nowSeconds)
    {
        if (candidateIndex >= candidates.Count)
            return;

        target             = candidates[candidateIndex];
        chestWaitUntil     = null;
        waypointElixirSent = false;
        phaseStarted       = nowSeconds;
        moveSent           = !IsGreenDig && hooks.MoveTo(target);
    }

    private void TryNextCandidate(double nowSeconds)
    {
        candidateIndex++;
        moveSent       = false;
        chestWaitUntil = null;
        if (candidateIndex < candidates.Count)
            AdvanceToCandidate(nowSeconds);
    }

    private void FinishCompleted()
    {
        hooks.StopMove();
        status = OccultPotStatus.Completed;
        hooks.OnStopped(StopReason.Completed);
    }

    private void Enter(OccultPotStatus status)
    {
        this.status  = status;
        phaseStarted = hooks.NowSeconds;
        if (status == OccultPotStatus.WaitingMedicine)
        {
            elixirSent   = false;
            elixirUsedAt = null;
            hooks.SnapshotExistingChests();
        }

        if (status != OccultPotStatus.OpeningChest)
            return;

        chestGoneAt    = null;
        chestWaitUntil = null;
        stoppedAtChest = false;
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
        candidateIndex       = 0;
        hintCount            = 0;
        lastHint             = null;
        failure              = null;
        target               = Vector3.Zero;
        moveSent             = false;
        elixirSent           = false;
        waypointElixirSent   = false;
        elixirUsedAt         = null;
        chestWaitUntil       = null;
        chestGoneAt          = null;
        awaitingContinuation = false;
        continuationReady    = false;
        foundTreasure        = false;
        lureAcquired         = false;
        lureExhausted        = false;
        sawRewardBuff        = false;
        sawChestObject       = false;
        farewellPending      = false;
        stoppedAtChest       = false;
        lastTalkLine         = null;
        TreasureChestInteractor.ClearSnapshot();
    }
}
