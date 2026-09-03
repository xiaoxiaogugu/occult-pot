using System.Numerics;
using Dalamud.Game.ClientState.Fates;
using Dalamud.Game.ClientState.Objects.Enums;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using OccultPot.Core.Data;
using OmenTools;
using OmenTools.Dalamud.Services.Game.Object.Abstractions.ObjectKinds;
using OmenTools.OmenService;
using GameObject = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;

namespace OccultPot.Core.Game;

internal static unsafe class PotFateTargeter
{
    private const uint AutoAttackActionID = 7;
    private const float CircleSlack = 25f;

    private static bool keeping;
    private static nint keptAddress;

    internal static void Idle()
    {
        keeping     = false;
        keptAddress = 0;
    }

    internal static void Tick(bool allow)
    {
        if (!allow)
        {
            if (keeping)
                ClearHostileTarget();
            Idle();
            return;
        }

        keeping = true;
        if (DService.Instance().ObjectTable.LocalPlayer is not { IsDead: false } localPlayer)
        {
            keptAddress = 0;
            ClearHostileTarget();
            return;
        }

        var activePotFateID = FindActivePotFateID(localPlayer.Position);
        var targetSystem = TargetSystem.Instance();
        if (targetSystem == null)
        {
            keptAddress = 0;
            return;
        }

        IBattleNPC? kept    = null;
        IBattleNPC? nearest = null;
        var nearestDistance = float.MaxValue;

        foreach (var obj in DService.Instance().ObjectTable)
        {
            if (obj is not IBattleNPC enemy || !IsValidPotFateEnemy(enemy, activePotFateID))
                continue;

            if (keptAddress != 0 && enemy.Address == keptAddress)
                kept = enemy;

            var distance = Vector3.DistanceSquared(localPlayer.Position, enemy.Position);
            if (distance >= nearestDistance)
                continue;

            nearest         = enemy;
            nearestDistance = distance;
        }

        var selected = kept ?? nearest;
        if (selected == null)
        {
            keptAddress = 0;
            ClearHostileTarget();
            return;
        }

        keptAddress = selected.Address;
        if (selected.Address != (nint)targetSystem->Target)
            targetSystem->Target = (GameObject*)selected.Address;
    }

    private static ushort FindActivePotFateID(Vector3 playerPos)
    {
        ushort id     = 0;
        var    nearest = float.MaxValue;
        foreach (var fate in DService.Instance().Fate)
        {
            if (!IslandPotLayout.IsPotFate(fate.FateId) || fate.State != FateState.Running || fate.Radius <= 0f)
                continue;

            var offset         = playerPos - fate.Position;
            var centerDistance = offset.X * offset.X + offset.Z * offset.Z;
            var reach          = fate.Radius + CircleSlack;
            if (centerDistance > reach * reach || centerDistance >= nearest)
                continue;

            id      = fate.FateId;
            nearest = centerDistance;
        }

        return id;
    }

    private static void ClearHostileTarget()
    {
        var targetSystem = TargetSystem.Instance();
        if (targetSystem == null)
            return;

        if (IsHostileBattleNPC((nint)targetSystem->Target))
            targetSystem->Target = null;
        if (IsHostileBattleNPC((nint)targetSystem->SoftTarget))
            targetSystem->SoftTarget = null;
    }

    private static bool IsHostileBattleNPC(nint address)
    {
        if (address == 0)
            return false;

        foreach (var obj in DService.Instance().ObjectTable)
        {
            if (obj.Address != address || obj is not IBattleNPC enemy)
                continue;
            return enemy.ObjectKind == ObjectKind.BattleNpc
                   && (enemy.StatusFlags & StatusFlags.Hostile) != 0;
        }

        return false;
    }

    private static bool IsValidPotFateEnemy(IBattleNPC enemy, ushort activePotFateID)
    {
        if (enemy.Address == 0 ||
            enemy.ObjectKind != ObjectKind.BattleNpc ||
            (enemy.StatusFlags & StatusFlags.Hostile) == 0 ||
            enemy.IsDead ||
            enemy.CurrentHp == 0 ||
            !enemy.IsTargetable)
            return false;

        var gameObject = (GameObject*)enemy.Address;
        if (gameObject == null ||
            enemy.SubKind != 5 ||
            !IslandPotLayout.IsPotFate(gameObject->FateId) ||
            !ActionManager.CanUseActionOnTarget(AutoAttackActionID, gameObject))
            return false;

        // 人还在圈里：只打当前这个罐的怪。追出去后 FateId 对上就留着，避免清目标。
        if (activePotFateID != 0 && gameObject->FateId != activePotFateID)
            return false;

        return true;
    }
}
