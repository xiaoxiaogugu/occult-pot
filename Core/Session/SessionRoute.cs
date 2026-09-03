using OccultPot.Core.Adapters;
using OccultPot.Core.Data;
using OccultPot.Models;
using OmenTools.OmenService;

namespace OccultPot.Core.Session;

internal sealed class SessionRoute
{
    private readonly List<(CnDataCenterKind Kind, uint WorldID)> slots = [];

    internal int Index { get; set; }

    internal int Count => slots.Count;

    internal IReadOnlyList<(CnDataCenterKind Kind, uint WorldID)> Slots => slots;

    internal (CnDataCenterKind Kind, uint WorldID) Current
    {
        get
        {
            ClampIndex();
            return slots[Index];
        }
    }

    internal void Build(PluginConfiguration config)
    {
        slots.Clear();
        Index = 0;
        slots.AddRange(Collect(config, persistResolvedWorld: true));
    }

    internal List<(CnDataCenterKind Kind, uint WorldID)> GetEnabled(PluginConfiguration config) =>
        slots.Count > 0 ? [..slots] : Collect(config);

    internal void PrepareStart(ref ushort targetTerritory)
    {
        Index = ResolveStartIndex();
        ClampIndex();
        var currentWorldID = CnWorldCatalog.CurrentWorldID;
        var territoryID    = (ushort)GameState.TerritoryType;
        if (currentWorldID != 0 && Count > 0 && currentWorldID == Current.WorldID && ZoneIds.IsSupportedIsland(territoryID))
            targetTerritory = territoryID;
    }

    internal void RotateToStart()
    {
        if (Count == 0)
            return;

        ClampIndex();
        if (Index == 0)
            return;

        var rotated = new List<(CnDataCenterKind Kind, uint WorldID)>(Count);
        for (var i = 0; i < Count; i++)
            rotated.Add(slots[(Index + i) % Count]);
        slots.Clear();
        slots.AddRange(rotated);
        Index = 0;
    }

    internal void Advance()
    {
        if (Count == 0)
            return;
        Index++;
        if (Index >= Count)
            Index = 0;
    }

    internal void ClampIndex()
    {
        if (Index < 0 || Index >= Count)
            Index = 0;
    }

    internal bool TryFindVisit(PlannedPotVisit visit, out int index)
    {
        index = slots.FindIndex(r => r.Kind == visit.DC && r.WorldID == visit.WorldID);
        if (index < 0)
            index = slots.FindIndex(r => r.Kind == visit.DC);
        return index >= 0;
    }

    internal string FormatSummary() =>
        string.Join(" → ", slots.Select(r =>
            CnWorldCatalog.DCDisplayName(r.Kind) + "/" + CnWorldCatalog.WorldName(r.WorldID)));

    private static List<(CnDataCenterKind Kind, uint WorldID)> Collect(PluginConfiguration config, bool persistResolvedWorld = false)
    {
        config.SyncHomeWorldLock();
        var worlds = new List<(CnDataCenterKind Kind, uint WorldID)>();
        foreach (var (kind, _, _) in CnWorldCatalog.All)
        {
            var routeConfig = config.GetRoute(kind);
            if (!routeConfig.Enabled)
                continue;

            var worldID = CnWorldCatalog.ResolveWorldID(kind, routeConfig.DestinationWorldID);
            if (persistResolvedWorld)
                routeConfig.DestinationWorldID = worldID;
            worlds.Add((kind, worldID));
        }

        return worlds;
    }

    private int ResolveStartIndex()
    {
        var currentWorld = CnWorldCatalog.CurrentWorldID;
        if (currentWorld == 0)
            return 0;

        var worldIndex = slots.FindIndex(r => r.WorldID == currentWorld);
        if (worldIndex >= 0)
            return worldIndex;

        var currentDC = CnWorldCatalog.KindForWorldID(currentWorld);
        if (!currentDC.HasValue)
            return 0;

        var dcIndex = slots.FindIndex(r => r.Kind == currentDC.Value);
        return dcIndex >= 0 ? dcIndex : FindIndexAfterDC(currentDC.Value);
    }

    private int FindIndexAfterDC(CnDataCenterKind currentDC)
    {
        var all          = CnWorldCatalog.All;
        var currentOrder = -1;
        for (var i = 0; i < all.Length; i++)
        {
            if (all[i].Item1 != currentDC)
                continue;
            currentOrder = i;
            break;
        }

        var bestIndex = -1;
        var bestOrder = int.MaxValue;
        for (var i = 0; i < slots.Count; i++)
        {
            var order = -1;
            for (var j = 0; j < all.Length; j++)
            {
                if (all[j].Item1 != slots[i].Kind)
                    continue;
                order = j;
                break;
            }

            if (order > currentOrder && order < bestOrder)
            {
                bestOrder = order;
                bestIndex = i;
            }
        }

        return bestIndex < 0 ? 0 : bestIndex;
    }
}
