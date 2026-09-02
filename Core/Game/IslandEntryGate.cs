using Lumina.Excel.Sheets;
using OccultPot.Core.Data;
using OmenTools.Info.Lumina;
using OmenTools.Interop.Game.Lumina;
using OmenTools.OmenService;
using TerritoryIntendedUse = FFXIVClientStructs.FFXIV.Client.Enums.TerritoryIntendedUse;

namespace OccultPot.Core.Game;

internal static class IslandEntryGate
{
    internal const ushort RequiredLevel = 100;

    internal static bool IsHubTerritory(ushort territoryID) =>
        GameState.TerritoryIntendedUse == TerritoryIntendedUse.OccultCrescent
        && !ZoneIds.IsSupportedIsland(territoryID);

    internal static bool IsCombatClassJob(uint classJobID)
    {
        if (classJobID == 0)
            return false;

        return LuminaGetter.TryGetRow<ClassJob>(classJobID, out var job) && job.Role != 0;
    }

    internal static bool MeetsEntryLevel(uint classJobID) =>
        IsCombatClassJob(classJobID) && LocalPlayerState.GetClassJobLevel(classJobID) >= RequiredLevel;

    internal static bool CanEnterCurrentJob() =>
        MeetsEntryLevel(LocalPlayerState.ClassJob);

    internal static bool ShouldWaitForBaseJob(PluginConfiguration config) =>
        config.AutoBaseClassJobID != 0 && LocalPlayerState.ClassJob != config.AutoBaseClassJobID;
}
