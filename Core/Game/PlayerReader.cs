using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Fates;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using OccultPot.Core.Data;
using OmenTools;
using OmenTools.Extensions;
using OmenTools.OmenService;

namespace OccultPot.Core.Game;

internal static class PlayerReader
{
    internal static bool IsLoggedIn =>
        LocalPlayerState.Object != null;

    internal static Vector3? Position =>
        LocalPlayerState.Object?.Position;

    internal static float DistanceTo(Vector3 target) =>
        LocalPlayerState.Object == null ? float.MaxValue : LocalPlayerState.DistanceTo3D(target);

    internal static bool HasStatus(uint statusID) =>
        LocalPlayerState.HasStatus(statusID, out _);

    internal static bool IsAvailable()
    {
        var player = LocalPlayerState.Object;
        return player != null && !player.IsDead;
    }

    internal static bool IsBetweenAreas() =>
        DService.Instance().Condition.IsBetweenAreas;

    internal static bool IsTransitionLocked()
    {
        var condition = DService.Instance().Condition;
        return condition.IsBetweenAreas
            || condition[ConditionFlag.OccupiedInCutSceneEvent]
            || condition[ConditionFlag.WatchingCutscene]
            || condition[ConditionFlag.WatchingCutscene78];
    }

    internal static bool IsCasting()
    {
        var condition = DService.Instance().Condition;
        if (condition.IsCasting)
            return true;
        var player = LocalPlayerState.Object;
        return player != null && player.IsCasting;
    }

    internal static bool IsInCombat() =>
        DService.Instance().Condition[ConditionFlag.InCombat];

    internal static bool IsBusy()
    {
        var condition = DService.Instance().Condition;
        if (condition.IsCasting || condition.IsBetweenAreas)
            return true;

        if (condition[ConditionFlag.OccupiedInEvent]
            || condition[ConditionFlag.OccupiedInCutSceneEvent]
            || condition[ConditionFlag.OccupiedInQuestEvent]
            || condition[ConditionFlag.WatchingCutscene]
            || condition[ConditionFlag.WatchingCutscene78])
            return true;

        var player = LocalPlayerState.Object;
        return player != null && player.IsCasting;
    }

    internal static bool CanSwitchJob()
    {
        if (!IsAvailable() || IsBetweenAreas())
            return false;

        var condition = DService.Instance().Condition;
        if (condition.IsCasting)
            return false;

        if (condition[ConditionFlag.OccupiedInCutSceneEvent]
            || condition[ConditionFlag.OccupiedInQuestEvent]
            || condition[ConditionFlag.WatchingCutscene]
            || condition[ConditionFlag.WatchingCutscene78])
            return false;

        var player = LocalPlayerState.Object;
        return player is not { IsCasting: true };
    }

    internal static bool WaitPlayer(ref DateTime? settleUntilUTC)
    {
        if (!IsAvailable() || IsBusy())
        {
            settleUntilUTC = null;
            return false;
        }

        if (settleUntilUTC == null)
            settleUntilUTC = DateTime.UtcNow.AddMilliseconds(100);

        return DateTime.UtcNow >= settleUntilUTC;
    }

    internal static bool IsOnMount() =>
        DService.Instance().Condition.IsOnMount || HasStatus(PotConstants.StatusMounted);

    internal static unsafe bool IsMoving
    {
        get
        {
            var map = AgentMap.Instance();
            return map != null && map->IsPlayerMoving;
        }
    }
}

internal static class FateReader
{
    internal static bool IsActive(uint fateID)
    {
        foreach (var fate in DService.Instance().Fate)
        {
            if (fate.FateId != fateID)
                continue;

            if (fate.State is FateState.Ended or FateState.Ending or FateState.Failed)
                return false;

            return true;
        }

        return false;
    }
}
