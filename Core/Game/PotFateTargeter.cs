using System.Numerics;
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
    private static bool keeping;

    internal static void Idle() => keeping = false;

    internal static void Tick(bool allow)
    {
        if (!allow)
        {
            if (keeping)
                ClearHostileTarget();
            keeping = false;
            return;
        }

        keeping = true;
        if (DService.Instance().ObjectTable.LocalPlayer is not { IsDead: false } localPlayer)
        {
            ClearHostileTarget();
            return;
        }

        ushort activePotFateID = 0;
        var nearestFateCenterDistance = float.MaxValue;
        foreach (var fate in DService.Instance().Fate)
        {
            if (!IslandPotLayout.IsPotFate(fate.FateId) || fate.Radius <= 0f)
                continue;

            var offset = localPlayer.Position - fate.Position;
            var centerDistance = offset.X * offset.X + offset.Z * offset.Z;
            if (centerDistance > fate.Radius * fate.Radius || centerDistance >= nearestFateCenterDistance)
                continue;

            activePotFateID = fate.FateId;
            nearestFateCenterDistance = centerDistance;
        }

        var targetSystem = TargetSystem.Instance();
        if (activePotFateID == 0 || targetSystem == null)
        {
            ClearHostileTarget();
            return;
        }

        IBattleNPC? selected = null;
        IBattleNPC? nearest = null;
        var nearestDistance = float.MaxValue;

        foreach (var obj in DService.Instance().ObjectTable)
        {
            if (obj is not IBattleNPC enemy || !IsValidPotFateEnemy(enemy, activePotFateID))
                continue;

            if (enemy.Address == (nint)targetSystem->Target)
            {
                selected = enemy;
                break;
            }

            var distance = Vector3.DistanceSquared(localPlayer.Position, enemy.Position);
            if (distance >= nearestDistance)
                continue;

            nearest = enemy;
            nearestDistance = distance;
        }

        selected ??= nearest;
        if (selected == null)
        {
            ClearHostileTarget();
            return;
        }

        if (selected.Address != (nint)targetSystem->Target)
            targetSystem->Target = (GameObject*)selected.Address;
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
        return gameObject != null &&
               enemy.SubKind == 5 &&
               gameObject->FateId == activePotFateID &&
               ActionManager.CanUseActionOnTarget(AutoAttackActionID, gameObject);
    }
}
