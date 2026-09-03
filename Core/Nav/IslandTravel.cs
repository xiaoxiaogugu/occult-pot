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

    private const float PtpArriveRadius          = 50f;
    private const float DestArriveRadius         = 12f;
    private const double ReturnWaitSeconds       = 15.0;
    private const double PtpWaitSeconds          = 12.0;
    private const double PtpResendSeconds        = 2.0;
    private const int PtpMaxSends                = 4;
    private const double MountWaitSeconds        = 5.0;
    private const double AfterMountSeconds       = 1.0;
    private const double StopSettleSeconds       = 0.5;
    private const double StopGiveUpSeconds       = 3.0;
    private const double PtpAfterDismountSeconds = 2.0;
    private const double PtpReadySettleSeconds   = 0.5;
    private const double SourceWalkSeconds       = 90.0;
    private const double DestWalkSeconds         = 120.0;

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

    internal bool IsRunning =>
        phase is not Phase.Idle and not Phase.Done and not Phase.Failed;

    internal bool PtpSucceeded => ptpSucceeded;

    internal IslandTravel(VNavController vnav) =>
        this.vnav = vnav;

    internal void Stop()
    {
        ClearPending();
        vnav.Stop();
        phase        = Phase.Idle;
        route        = null;
        ptpShard     = null;
        ptpIssued    = false;
        ptpSucceeded = false;
        returnSent   = false;
        Status       = RuntimeStatus.Of(RuntimeStatusCode.Travel_Stopped);
    }

    internal void Begin(ushort territory, Vector3 dest, string label)
    {
        Stop();
        this.label    = label;
        this.territory = territory;
        finalDest     = dest;
        walkDest      = dest;
        var position = PlayerReader.Position;
        if (!position.HasValue)
        {
            phase  = Phase.Failed;
            Status = RuntimeStatus.Of(RuntimeStatusCode.Travel_NoPosition);
            return;
        }

        if (PlayerReader.DistanceTo(finalDest) <= DestArriveRadius)
        {
            Finish(RuntimeStatus.Of(RuntimeStatusCode.Travel_AlreadyAt, label));
            return;
        }

        route = AethernetRouter.Decide(territory, position.Value, finalDest);
        if (route.Kind == AethernetRouteKind.WalkTeleportWalk && route.Source != null && route.Destination != null)
        {
            ExternalCommands.Echo($"[寻路] {route.Source.Name} → {route.Destination.Name}，前往{label}");
            BeginHop(route.Source, route.Destination);
            return;
        }

        ExternalCommands.Echo($"[寻路] 前往{label}");
        StartWalkDest();
    }

    internal void NoteArrivalIfClose()
    {
        if (ptpSucceeded || ptpShard == null)
            return;
        if (phase is not (Phase.WaitReadyPtp or Phase.WaitPtp))
            return;
        if (PlayerReader.DistanceTo(ptpShard.Landing) <= PtpArriveRadius || AethernetRouter.NearLanding(ptpShard, PtpArriveRadius))
            MarkPtpArrived();
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
            return;
        }

        if (AethernetRouter.AtSource(src, territory))
        {
            StartPtp(dst);
            return;
        }

        StartWalk(src.Stand, 4.7f, SourceWalkSeconds, Phase.WaitWalkSource, RuntimeStatus.Of(RuntimeStatusCode.Travel_ToAetheryte, src.Name));
    }

    private void StartReturn(AfterReturn next)
    {
        afterReturn = next;
        returnSent  = false;
        RequestStopThen(null, Phase.WaitReturn, RuntimeStatus.Of(RuntimeStatusCode.Travel_PrepareReturn));
    }

    private void TickWaitReturn()
    {
        if (AethernetRouter.NearCamp(route?.Source))
        {
            FinishReturn();
            return;
        }

        if (!returnSent)
        {
            if ((PlayerReader.IsBusy() || PlayerReader.IsInCombat()) && Elapsed() < 3.0)
            {
                Status = RuntimeStatus.Of(RuntimeStatusCode.Travel_ReturnIdle);
                return;
            }

            ExternalCommands.Run("/ac 返回");
            returnSent = true;
            phaseUTC   = DateTime.UtcNow;
            Status     = RuntimeStatus.Of(RuntimeStatusCode.Travel_ReturnCamp);
            return;
        }

        if (Elapsed() >= ReturnWaitSeconds)
        {
            FallbackAfterFailedReturn();
            return;
        }

        Status = RuntimeStatus.Of(RuntimeStatusCode.Travel_Returning);
    }

    private void FinishReturn()
    {
        if (afterReturn == AfterReturn.Ptp && route?.Destination != null)
            StartPtp(route.Destination);
        else
            StartWalkDest();
    }

    private void FallbackAfterFailedReturn()
    {
        var dest = route?.Destination;
        var from = PlayerReader.Position;
        if (dest == null || !from.HasValue)
        {
            StartWalkDest();
            return;
        }

        if (AethernetRouter.NearLanding(dest))
        {
            StartWalkDest();
            return;
        }

        var shards = IslandAethernet.ForTerritory(territory);
        var nearest = shards.Count == 0
            ? null
            : shards.OrderBy(s => Vector3.DistanceSquared(from.Value, s.Stand)).First();
        if (nearest == null || nearest.Name == dest.Name)
        {
            StartWalkDest();
            return;
        }

        BeginHop(nearest, dest);
    }

    private void StartWalkDest() =>
        StartWalk(finalDest, DestArriveRadius, DestWalkSeconds, Phase.WaitWalkDest, RuntimeStatus.Of(RuntimeStatusCode.Travel_ToDest, label));

    private void StartWalk(Vector3 dest, float arrive, double timeout, Phase waitPhase, RuntimeStatus status)
    {
        walkDest         = dest;
        walkArrive       = arrive;
        walkTimeout      = timeout;
        afterMount       = waitPhase;
        afterMountStatus = status;
        mountIssued      = false;
        RequestStopThen(AfterStopForWalk, Phase.WaitMounted, RuntimeStatus.Of(RuntimeStatusCode.Travel_WaitMount));
    }

    private void AfterStopForWalk()
    {
        mountIssued  = false;
        mountWaitUTC = DateTime.UtcNow;
        mountedAtUTC = PlayerReader.IsOnMount() ? DateTime.UtcNow : null;
    }

    private void TickWaitMounted()
    {
        if (PlayerReader.IsOnMount())
        {
            mountedAtUTC ??= DateTime.UtcNow;
            if ((DateTime.UtcNow - mountedAtUTC.Value).TotalSeconds >= AfterMountSeconds)
                StartNavAfterMount();
            return;
        }

        if (mountWaitUTC == default)
            mountWaitUTC = DateTime.UtcNow;
        if ((DateTime.UtcNow - mountWaitUTC).TotalSeconds >= (mountIssued ? MountWaitSeconds : 2.0))
        {
            StartNavAfterMount();
            return;
        }

        if (!mountIssued && MountActions.TryMount())
            mountIssued = true;
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
                StartPtp(route.Destination);
            else
                StartWalkDest();
            return;
        }

        if (!vnav.IsRunning())
            vnav.MoveTo(walkDest);
        Status = RuntimeStatus.Of(RuntimeStatusCode.Travel_ToSource, route?.Source?.Name ?? string.Empty);
    }

    private void TickWaitWalkDest()
    {
        if (vnav.HasArrived(walkDest, walkArrive) || Elapsed() >= walkTimeout)
        {
            Finish(RuntimeStatus.Literal("[找罐] 已到" + label));
            return;
        }

        if (!vnav.IsRunning())
            vnav.MoveTo(walkDest);
        Status = vnav.IsReady()
            ? RuntimeStatus.Of(RuntimeStatusCode.Travel_ToDest, label)
            : RuntimeStatus.Of(RuntimeStatusCode.Travel_WaitVnav, vnav.LastDetail);
    }

    private void StartPtp(IslandAethernetShard shard)
    {
        ptpShard       = shard;
        ptpIssued      = false;
        ptpSucceeded   = false;
        ptpSendCount   = 0;
        ptpReadyUTC    = null;
        ptpFarSinceUTC = null;
        dismountIssued = false;
        if (route?.Source != null && AethernetRouter.AtSource(route.Source, territory))
        {
            if (PlayerReader.IsOnMount())
                TryDismountOnce();
            FirePtp();
            Enter(Phase.WaitPtp, RuntimeStatus.Of(RuntimeStatusCode.Travel_Ptp, shard.Name));
            return;
        }

        RequestStopThen(null, Phase.WaitReadyPtp, RuntimeStatus.Of(RuntimeStatusCode.Travel_PreparePtp, shard.Name));
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
            if ((DateTime.UtcNow - dismountUTC).TotalSeconds < PtpAfterDismountSeconds)
                return;
        }

        if (PlayerReader.IsBusy() && Elapsed() < 3.0)
        {
            Status = RuntimeStatus.Of(RuntimeStatusCode.Travel_WaitIdlePtp);
            return;
        }

        if (!PlayerReader.IsBusy())
        {
            ptpReadyUTC ??= DateTime.UtcNow;
            if ((DateTime.UtcNow - ptpReadyUTC.Value).TotalSeconds < PtpReadySettleSeconds)
                return;
        }

        FirePtp();
        Enter(Phase.WaitPtp, RuntimeStatus.Of(RuntimeStatusCode.Travel_WaitPtpArrive, ptpShard.Name));
    }

    private void TickWaitPtp()
    {
        if (ptpShard == null)
        {
            phase = Phase.Failed;
            return;
        }

        if (HasPtpArrived())
        {
            StartWalkDest();
            return;
        }

        if (!ptpIssued)
        {
            Status = RuntimeStatus.Of(RuntimeStatusCode.Travel_WaitStopPtpNamed, ptpShard.Name);
            return;
        }

        if (TryResendPtp())
        {
            Status = RuntimeStatus.Of(RuntimeStatusCode.Travel_PtpResend, ptpShard.Name, ptpSendCount, PtpMaxSends);
            return;
        }

        if (Elapsed() >= PtpWaitSeconds)
        {
            StartWalkDest();
            return;
        }

        Status = RuntimeStatus.Of(RuntimeStatusCode.Travel_WaitPtp, ptpShard.Name, ptpSendCount, PtpMaxSends);
    }

    private bool HasPtpArrived()
    {
        if (ptpSucceeded)
            return true;
        if (ptpShard != null && PlayerReader.DistanceTo(ptpShard.Landing) <= PtpArriveRadius)
        {
            MarkPtpArrived();
            return true;
        }

        return false;
    }

    private void MarkPtpArrived()
    {
        if (ptpSucceeded)
            return;
        ptpSucceeded   = true;
        ptpFarSinceUTC = null;
    }

    private void FirePtp()
    {
        if (ptpShard == null || ptpSucceeded)
            return;
        if (PlayerReader.DistanceTo(ptpShard.Landing) <= PtpArriveRadius)
        {
            MarkPtpArrived();
            return;
        }

        ExternalCommands.Run("/pdr ptp " + ptpShard.Name);
        ptpSendCount++;
        ptpIssued      = true;
        lastPtpUTC     = DateTime.UtcNow;
        ptpFarSinceUTC = DateTime.UtcNow;
    }

    private bool TryResendPtp()
    {
        if (ptpSucceeded || HasPtpArrived())
            return false;
        if (ptpSendCount >= PtpMaxSends)
            return false;

        ptpFarSinceUTC ??= DateTime.UtcNow;
        if ((DateTime.UtcNow - ptpFarSinceUTC.Value).TotalSeconds < PtpResendSeconds)
            return false;
        if ((DateTime.UtcNow - lastPtpUTC).TotalSeconds < PtpResendSeconds)
            return false;

        FirePtp();
        return !ptpSucceeded;
    }

    private void TryDismountOnce()
    {
        if (dismountIssued || !PlayerReader.IsOnMount())
            return;
        MountCommand.Dismount();
        dismountIssued = true;
        dismountUTC    = DateTime.UtcNow;
    }

    private void RequestStopThen(Action? action, Phase resume, RuntimeStatus resumeStatus)
    {
        pendingAfterStop    = action;
        afterStopped        = resume;
        afterStoppedStatus  = resumeStatus;
        stopSettleUTC       = null;
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
            Status        = RuntimeStatus.Of(RuntimeStatusCode.Travel_WaitStop);
            return;
        }

        if (PlayerReader.IsBusy())
        {
            stopSettleUTC ??= DateTime.UtcNow;
            if ((DateTime.UtcNow - stopSettleUTC.Value).TotalSeconds < StopGiveUpSeconds)
            {
                Status = RuntimeStatus.Of(RuntimeStatusCode.Travel_WaitIdleWalk);
                return;
            }
        }
        else
        {
            stopSettleUTC ??= DateTime.UtcNow;
            if ((DateTime.UtcNow - stopSettleUTC.Value).TotalSeconds < StopSettleSeconds)
                return;
        }

        FirePendingThenResume();
    }

    private void FirePendingThenResume()
    {
        var action = pendingAfterStop;
        pendingAfterStop = null;
        action?.Invoke();
        lastActionUTC = DateTime.UtcNow;
        Enter(afterStopped, afterStoppedStatus);
    }

    private void ClearPending()
    {
        pendingAfterStop = null;
        vnavStopIssued   = false;
        stopBeganUTC     = null;
        mountIssued      = false;
        dismountIssued   = false;
        returnSent       = false;
        stopSettleUTC    = null;
        mountedAtUTC     = null;
        mountWaitUTC     = default;
    }

    private bool EnsureVnavStopped()
    {
        if (!vnav.IsRunning())
        {
            vnavStopIssued = false;
            stopBeganUTC   = null;
            return true;
        }

        var now = DateTime.UtcNow;
        if (!vnavStopIssued)
        {
            vnav.Stop();
            vnavStopIssued = true;
            stopBeganUTC   = now;
            lastStopUTC    = now;
            return false;
        }

        if ((now - lastStopUTC).TotalSeconds >= 1.0)
        {
            vnav.Stop();
            lastStopUTC = now;
        }

        return stopBeganUTC is { } began && (now - began).TotalSeconds >= StopGiveUpSeconds;
    }

    private void Finish(RuntimeStatus status)
    {
        phase  = Phase.Done;
        Status = status;
    }

    private void Enter(Phase phase, RuntimeStatus status)
    {
        this.phase = phase;
        phaseUTC   = DateTime.UtcNow;
        Status     = status;
    }

    private double Elapsed() =>
        (DateTime.UtcNow - phaseUTC).TotalSeconds;
}
