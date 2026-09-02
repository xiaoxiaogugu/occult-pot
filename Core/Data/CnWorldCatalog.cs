using Lumina.Excel.Sheets;
using OccultPot.Models;
using OmenTools.Info.Lumina;
using OmenTools.Interop.Game.Lumina;
using OmenTools.OmenService;

namespace OccultPot.Core.Data;

internal static class CnWorldCatalog
{
    internal static readonly (CnDataCenterKind Kind, string DCName, string DefaultWorld)[] All =
    [
        (CnDataCenterKind.Chocobo, "陆行鸟", "晨曦王座"),
        (CnDataCenterKind.Moogle, "莫古力", "白金幻象"),
        (CnDataCenterKind.Cat, "猫小胖", "紫水栈桥"),
        (CnDataCenterKind.Atomos, "豆豆柴", "红茶川"),
    ];

    internal static uint HomeWorldID => GameState.HomeWorld;

    internal static uint CurrentWorldID => GameState.CurrentWorld;

    internal static CnDataCenterKind? HomeDCKind =>
        KindForWorldID(HomeWorldID) ?? KindForDataCenterID(GameState.HomeDataCenter);

    internal static string DCDisplayName(CnDataCenterKind kind) =>
        All.First(x => x.Kind == kind).DCName;

    internal static string DefaultWorldName(CnDataCenterKind kind) =>
        All.First(x => x.Kind == kind).DefaultWorld;

    internal static IReadOnlyList<World> WorldsFor(CnDataCenterKind kind)
    {
        var dcName = DCDisplayName(kind);
        return Sheets.CNWorlds.Values
            .Where(w => string.Equals(w.DataCenter.Value.Name.ToString(), dcName, StringComparison.Ordinal))
            .OrderBy(w => w.Name.ToString(), StringComparer.Ordinal)
            .ToList();
    }

    internal static uint ResolveWorldID(CnDataCenterKind kind, uint configuredID)
    {
        var worlds = WorldsFor(kind);
        if (configuredID != 0 && worlds.Any(w => w.RowId == configuredID))
            return configuredID;

        var defaultName = DefaultWorldName(kind);
        var byName = worlds.FirstOrDefault(w =>
            string.Equals(w.Name.ToString(), defaultName, StringComparison.Ordinal));
        if (byName.RowId != 0)
            return byName.RowId;

        return worlds.FirstOrDefault().RowId;
    }

    internal static string WorldName(uint worldID)
    {
        if (worldID == 0)
            return "—";

        var name = LuminaWrapper.GetWorldName(worldID);
        return string.IsNullOrEmpty(name) ? $"#{worldID}" : name;
    }

    internal static uint DataCenterRowID(CnDataCenterKind kind)
    {
        var sample = WorldsFor(kind).FirstOrDefault();
        return sample.RowId == 0 ? 0 : sample.DataCenter.RowId;
    }

    internal static CnDataCenterKind? KindForDataCenterID(uint dcID)
    {
        if (dcID == 0)
            return null;

        foreach (var (kind, _, _) in All)
        {
            var sample = WorldsFor(kind).FirstOrDefault();
            if (sample.RowId != 0 && sample.DataCenter.RowId == dcID)
                return kind;
        }

        try
        {
            var name = GameState.HomeDataCenterData.Name.ToString();
            foreach (var (kind, dcName, _) in All)
            {
                if (string.Equals(name, dcName, StringComparison.Ordinal))
                    return kind;
            }
        }
        catch
        {
            // ignored
        }

        return null;
    }

    internal static CnDataCenterKind? KindForWorldID(uint worldID)
    {
        if (worldID == 0 || !Sheets.CNWorlds.TryGetValue(worldID, out var world))
            return null;

        var dcName = world.DataCenter.Value.Name.ToString();
        foreach (var (kind, name, _) in All)
        {
            if (string.Equals(dcName, name, StringComparison.Ordinal))
                return kind;
        }

        return null;
    }
}
