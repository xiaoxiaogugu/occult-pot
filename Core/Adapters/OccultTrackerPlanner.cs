using OccultPot.Core.Data;
using OccultPot.Models;

namespace OccultPot.Core.Adapters;

internal readonly record struct RemoteIslandSnapshot(
    CnDataCenterKind DC,
    ushort Territory,
    long LastUpdate,
    long NorthSpawn,
    long SouthSpawn,
    long NorthDeath,
    long SouthDeath);

internal readonly record struct PlannedPotVisit(
    CnDataCenterKind DC,
    uint WorldID,
    ushort Territory,
    PotKind Kind,
    int WaitSeconds,
    int UntilGoneSeconds,
    bool Alive,
    bool RequiresWorldTravel,
    string Reason);

internal static class OccultTrackerPlanner
{
    internal const int SameDCBufferSeconds = 60;
    internal const int CrossDCBufferSeconds = 300;
    internal const int FateAliveSeconds = 15 * 60;
    internal const long RespawnSeconds = 1800;
    internal const long CatalogStaleSeconds = 4 * 3600;

    internal static bool TryPickVisit(
        IReadOnlyList<RemoteIslandSnapshot> islands,
        IReadOnlyList<(CnDataCenterKind Kind, uint WorldID)> worlds,
        uint currentWorldID,
        ushort currentTerritory,
        long now,
        CnDataCenterKind? currentDC,
        out PlannedPotVisit visit,
        ushort excludeTerritory = 0,
        CnDataCenterKind? excludeDC = null,
        ushort excludePotTerritory = 0,
        PotKind? excludePotKind = null,
        CnDataCenterKind? excludePotDC = null)
    {
        visit = default;
        PlannedPotVisit? best = null;
        var bestWait = int.MaxValue;
        var bestTravel = 2;
        var bestIsland = 2;
        currentDC ??= CnWorldCatalog.KindForWorldID(currentWorldID);

        foreach (var (dc, worldID) in worlds)
        {
            foreach (var island in islands)
            {
                if (island.DC != dc)
                    continue;
                if (excludeDC is { } skipDC && excludeTerritory != 0 && island.DC == skipDC && island.Territory == excludeTerritory)
                    continue;
                if (!TryComputeCandidate(island, now, excludePotTerritory, excludePotKind, excludePotDC, dc, out var kind, out var wait, out var untilGone, out var alive))
                    continue;

                var travel = worldID != 0 && currentWorldID != 0 && worldID != currentWorldID;
                var sameDC = currentDC != null && currentDC.Value == dc;
                if (!IsReachable(sameDC, travel, alive, wait, untilGone))
                    continue;

                var travelRank = travel ? 1 : 0;
                var islandRank = island.Territory == currentTerritory ? 0 : 1;
                if (best != null)
                {
                    if (wait > bestWait)
                        continue;
                    if (wait == bestWait)
                    {
                        if (travelRank > bestTravel)
                            continue;
                        if (travelRank == bestTravel && islandRank >= bestIsland)
                            continue;
                    }
                }

                var islandLabel = IslandPotLayout.IslandLabel(island.Territory);
                var kindLabel = kind == PotKind.North ? "北罐" : "南罐";
                var when = alive
                    ? $"{kindLabel}进行中 剩{FormatMmSs(untilGone)}"
                    : wait <= 0
                        ? $"{kindLabel}即将刷新"
                        : $"{kindLabel} {FormatMmSs(wait)}后";
                var travelNote = travel
                    ? sameDC
                        ? $"，同区缓冲 {SameDCBufferSeconds / 60} 分钟"
                        : $"，跨区需刷新 >{CrossDCBufferSeconds / 60} 分钟"
                    : "";
                best = new PlannedPotVisit(
                    dc,
                    worldID,
                    island.Territory,
                    kind,
                    wait,
                    untilGone,
                    alive,
                    travel,
                    $"{CnWorldCatalog.DCDisplayName(dc)} {islandLabel} {when}{travelNote}");
                bestWait = wait;
                bestTravel = travelRank;
                bestIsland = islandRank;
            }
        }

        if (best == null)
            return false;
        visit = best.Value;
        return true;
    }

    private static bool IsReachable(bool sameDC, bool travel, bool alive, int wait, int untilGone)
    {
        if (travel && !sameDC)
        {
            if (alive)
                return false;
            return wait >= CrossDCBufferSeconds;
        }

        if (alive)
            return untilGone >= SameDCBufferSeconds;
        return true;
    }

    internal static bool TryComputeKind(
        RemoteIslandSnapshot island,
        long now,
        PotKind kind,
        out int waitSeconds,
        out int untilGoneSeconds,
        out bool alive)
    {
        var spawn = kind == PotKind.North ? island.NorthSpawn : island.SouthSpawn;
        var death = kind == PotKind.North ? island.NorthDeath : island.SouthDeath;
        alive = IsAlive(spawn, death, now);
        if (alive)
        {
            waitSeconds = 0;
            untilGoneSeconds = (int)Math.Max(0, spawn + FateAliveSeconds - now);
            return untilGoneSeconds > 0;
        }

        if (spawn <= 0)
        {
            waitSeconds = int.MaxValue;
            untilGoneSeconds = 0;
            return false;
        }

        var nextAt = spawn + RespawnSeconds;
        waitSeconds = (int)Math.Max(0, nextAt - now);
        untilGoneSeconds = waitSeconds + FateAliveSeconds;
        return true;
    }

    private static bool TryComputeCandidate(
        RemoteIslandSnapshot island,
        long now,
        ushort excludePotTerritory,
        PotKind? excludePotKind,
        CnDataCenterKind? excludePotDC,
        CnDataCenterKind islandDC,
        out PotKind kind,
        out int waitSeconds,
        out int untilGoneSeconds,
        out bool alive)
    {
        if (!TryComputeNext(island, now, out kind, out waitSeconds, out untilGoneSeconds, out alive))
            return false;

        if (!excludePotKind.HasValue
            || island.Territory != excludePotTerritory
            || islandDC != excludePotDC
            || kind != excludePotKind.Value)
            return true;

        var alternate = kind == PotKind.North ? PotKind.South : PotKind.North;
        return TryComputeKind(island, now, alternate, out waitSeconds, out untilGoneSeconds, out alive);
    }

    internal static bool TryComputeNext(
        RemoteIslandSnapshot island,
        long now,
        out PotKind kind,
        out int waitSeconds,
        out int untilGoneSeconds,
        out bool alive)
    {
        kind = PotKind.North;
        waitSeconds = int.MaxValue;
        untilGoneSeconds = 0;
        alive = false;

        var northUp = IsAlive(island.NorthSpawn, island.NorthDeath, now);
        var southUp = IsAlive(island.SouthSpawn, island.SouthDeath, now);
        if (northUp || southUp)
        {
            var northLeft = northUp ? (int)Math.Max(0, island.NorthSpawn + FateAliveSeconds - now) : -1;
            var southLeft = southUp ? (int)Math.Max(0, island.SouthSpawn + FateAliveSeconds - now) : -1;
            if (northUp && (!southUp || northLeft >= southLeft))
            {
                kind = PotKind.North;
                untilGoneSeconds = northLeft;
            }
            else
            {
                kind = PotKind.South;
                untilGoneSeconds = southLeft;
            }

            waitSeconds = 0;
            alive = true;
            return untilGoneSeconds > 0;
        }

        long lastSpawn = 0;
        var lastNorth = false;
        if (island.NorthSpawn > 0)
        {
            lastSpawn = island.NorthSpawn;
            lastNorth = true;
        }

        if (island.SouthSpawn > 0 && island.SouthSpawn >= lastSpawn)
        {
            lastSpawn = island.SouthSpawn;
            lastNorth = false;
        }

        if (lastSpawn <= 0)
            return false;

        kind = lastNorth ? PotKind.South : PotKind.North;
        var nextAt = lastSpawn + RespawnSeconds;
        waitSeconds = (int)Math.Max(0, nextAt - now);
        untilGoneSeconds = waitSeconds + FateAliveSeconds;
        return true;
    }

    internal static string FormatIsland(RemoteIslandSnapshot? island, long now)
    {
        if (island == null)
            return "无数据";
        if (!TryComputeNext(island.Value, now, out var kind, out var wait, out var untilGone, out var alive))
            return "无数据";
        return FormatTarget(kind, wait, untilGone, alive);
    }

    internal static string FormatTarget(PotKind kind, int wait, int untilGone, bool alive)
    {
        var label = kind == PotKind.North ? "北罐" : "南罐";
        if (alive)
            return $"{label}进行中 剩{FormatMmSs(untilGone)}";
        return wait <= 0 ? $"{label}即将刷新" : $"下个{label} {FormatMmSs(wait)}";
    }

    internal static string FormatMmSs(int seconds)
    {
        if (seconds < 0)
            seconds = 0;
        var m = seconds / 60;
        var s = seconds % 60;
        return $"{m:00}:{s:00}";
    }

    private static bool IsAlive(long spawn, long death, long now)
    {
        if (spawn <= 0 || spawn > now)
            return false;
        if (death > spawn)
            return false;
        return now < spawn + FateAliveSeconds;
    }
}
