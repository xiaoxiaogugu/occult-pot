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
    long SouthDeath,
    uint WorldID = 0);

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

internal enum CrowdRebindAction
{
    Confirm,
    Flip,
    Abandon,
}

/// <summary>
/// 众包路线：每大区只进一座南岛、一座北岛；同岛南北罐 30 分钟对侧轮换。
/// </summary>
internal static class OccultTrackerPlanner
{
    internal const int SameDCBufferSeconds = 60;
    internal const int SameDCHopSeconds = 60;
    internal const int CrossDCBufferSeconds = 300;
    internal const int CrossDCMinRemainSeconds = 3 * 60;
    internal const int AbandonWaitSeconds = 300;
    internal const int FateAliveSeconds = 15 * 60;
    internal const long RespawnSeconds = 1800;
    internal const long CatalogStaleSeconds = 4 * 3600;
    internal const int InstanceAlignSeconds = 240;
    internal const int FreshUpdateSeconds = 20 * 60;
    internal const int FreshSpawnSeconds = 50 * 60;
    internal const int LocalStayGraceSeconds = 120;

    private const long CycleSeconds = RespawnSeconds * 2;

    private sealed class IslandCluster
    {
        public long Epoch;
        public readonly List<RemoteIslandSnapshot> Rows = [];
    }

    /// <param name="localRunningKind">
    /// 当前所在岛游戏 Fate 表里正在 Running 的罐。只有这项能标进行中；众包时间窗只用来推下一罐。
    /// </param>
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
        CnDataCenterKind? excludePotDC = null,
        PotKind? localRunningKind = null,
        long localNorthSpawn = 0,
        long localSouthSpawn = 0)
    {
        visit = default;
        PlannedPotVisit? best = null;
        var bestEta = int.MaxValue;
        var bestTravel = 2;
        var bestGone = -1;
        currentDC ??= CnWorldCatalog.KindForWorldID(currentWorldID);

        foreach (var (dc, routeWorldID) in worlds)
        {
            foreach (var territory in new[] { ZoneIds.SouthHorn, ZoneIds.NorthHorn })
            {
                if (excludeDC is { } skipDC && excludeTerritory != 0 && dc == skipDC && territory == excludeTerritory)
                    continue;

                var onThisIsland = currentDC == dc
                    && territory == currentTerritory
                    && ZoneIds.IsSupportedIsland(currentTerritory);
                var sameDC = currentDC == dc;
                var travelCost = onThisIsland ? 0 : sameDC ? SameDCHopSeconds : CrossDCBufferSeconds;
                var travel = routeWorldID != 0 && currentWorldID != 0 && routeWorldID != currentWorldID;

                PotKind kind;
                int wait;
                int untilGone;
                bool alive;
                if (onThisIsland && localRunningKind is { } running)
                {
                    kind = running;
                    wait = 0;
                    untilGone = FateAliveSeconds;
                    alive = true;
                }
                else
                {
                    var preferNorth = onThisIsland ? localNorthSpawn : 0;
                    var preferSouth = onThisIsland ? localSouthSpawn : 0;
                    if (!TryMergeDCIsland(islands, dc, territory, now, out var merged, preferNorth, preferSouth))
                        continue;
                    if (!TryComputeCandidate(merged, now, excludePotTerritory, excludePotKind, excludePotDC, dc, out kind, out wait, out untilGone, out alive))
                        continue;

                    // 跨大区：窗内剩余不足 3 分钟不当这口，改等下一罐。
                    if (!sameDC && TooLateToCrossDC(wait, untilGone, alive))
                    {
                        alive = false;
                        wait = WaitAfterCrowdWindow(0, untilGone, false);
                        if (wait > 0)
                            untilGone = wait + FateAliveSeconds;
                    }
                    else
                    {
                        // 众包窗不算 Running。本岛只在窗开头留下等；过后和别处一样改等下一罐。
                        alive = false;
                        wait = WaitAfterCrowdWindow(wait, untilGone, onThisIsland);
                        if (wait > 0)
                            untilGone = wait + FateAliveSeconds;
                    }
                }

                if (alive && untilGone < (onThisIsland ? SameDCBufferSeconds : travelCost))
                    continue;

                // 赶到才能打：本岛现场 Running 为 0，同区换岛 / 跨区分别加 hop。
                var eta = alive ? travelCost : Math.Max(wait, travelCost);
                var travelRank = onThisIsland ? 0 : sameDC ? 1 : 2;

                if (best != null)
                {
                    if (eta > bestEta)
                        continue;
                    if (eta == bestEta)
                    {
                        if (alive && untilGone < bestGone)
                            continue;
                        if ((!alive || untilGone == bestGone) && travelRank >= bestTravel)
                            continue;
                    }
                }

                var islandLabel = IslandPotLayout.IslandLabel(territory);
                var kindLabel = kind == PotKind.North ? "北罐" : "南罐";
                var when = alive
                    ? $"{kindLabel}进行中 剩{FormatMmSs(untilGone)}"
                    : $"{kindLabel} {FormatMmSs(wait)}后";
                var worldName = CnWorldCatalog.WorldName(routeWorldID);
                best = new PlannedPotVisit(
                    dc,
                    routeWorldID,
                    territory,
                    kind,
                    wait,
                    untilGone,
                    alive,
                    travel,
                    $"{CnWorldCatalog.DCDisplayName(dc)}/{worldName} {islandLabel} {when}");
                bestEta = eta;
                bestTravel = travelRank;
                bestGone = alive ? untilGone : -1;
            }
        }

        if (best == null)
            return false;
        visit = best.Value;
        return true;
    }

    /// <summary>
    /// 同大区同地图：按 30 分钟相位归成新旧岛，只取会进的那一座。
    /// </summary>
    internal static bool TryMergeDCIsland(
        IReadOnlyList<RemoteIslandSnapshot> islands,
        CnDataCenterKind dc,
        ushort territory,
        long now,
        out RemoteIslandSnapshot merged,
        long preferNorthSpawn = 0,
        long preferSouthSpawn = 0)
    {
        merged = default;
        List<IslandCluster> clusters = [];

        foreach (var island in islands)
        {
            if (island.DC != dc || island.Territory != territory)
                continue;
            if (IsCatalogStale(island, now))
                continue;
            if (!TryCycleEpoch(island, out var epoch))
                continue;

            IslandCluster? hit = null;
            foreach (var cluster in clusters)
            {
                if (Math.Abs(cluster.Epoch - epoch) > InstanceAlignSeconds)
                    continue;
                hit = cluster;
                break;
            }

            if (hit == null)
            {
                hit = new IslandCluster { Epoch = epoch };
                clusters.Add(hit);
            }

            hit.Rows.Add(island);
            if (island.LastUpdate >= NewestUpdate(hit))
                hit.Epoch = epoch;
        }

        if (clusters.Count == 0)
        {
            if (preferNorthSpawn <= 0 && preferSouthSpawn <= 0)
                return false;
            merged = new RemoteIslandSnapshot(dc, territory, now, preferNorthSpawn, preferSouthSpawn, 0, 0);
            return true;
        }

        var picked = PickCluster(clusters, now, preferNorthSpawn, preferSouthSpawn);
        if (picked == null)
            return false;

        // 现场相位对不上众包簇：只用本地，避免新旧岛对侧串台。
        if ((preferNorthSpawn > 0 || preferSouthSpawn > 0)
            && TryCycleEpoch(new RemoteIslandSnapshot(dc, territory, now, preferNorthSpawn, preferSouthSpawn, 0, 0), out var preferEpoch)
            && Math.Abs(picked.Epoch - preferEpoch) > InstanceAlignSeconds)
        {
            merged = new RemoteIslandSnapshot(dc, territory, now, preferNorthSpawn, preferSouthSpawn, 0, 0);
            return true;
        }

        merged = FlattenCluster(picked, dc, territory);
        return merged.NorthSpawn > 0 || merged.SouthSpawn > 0;
    }

    /// <summary>
    /// 进岛后核对：现场 Fate 优先；否则用大区选定的那一座岛。
    /// </summary>
    internal static CrowdRebindAction DecideRebind(
        PotKind? committedKind,
        bool northFateAlive,
        bool southFateAlive,
        RemoteIslandSnapshot? boundIsland,
        long now,
        out PotKind kind,
        out int waitSeconds,
        out int untilGoneSeconds,
        out bool alive)
    {
        kind = committedKind ?? PotKind.North;
        waitSeconds = int.MaxValue;
        untilGoneSeconds = 0;
        alive = false;

        if (northFateAlive || southFateAlive)
        {
            if (northFateAlive && (!southFateAlive || committedKind == PotKind.North))
                kind = PotKind.North;
            else if (southFateAlive)
                kind = PotKind.South;
            else
                kind = PotKind.North;

            alive = true;
            waitSeconds = 0;
            untilGoneSeconds = FateAliveSeconds;
            if (committedKind is { } committed && committed != kind)
                return CrowdRebindAction.Flip;
            return CrowdRebindAction.Confirm;
        }

        if (boundIsland is { } island
            && TryComputeNext(island, now, out kind, out waitSeconds, out untilGoneSeconds, out alive))
        {
            if (committedKind is { } committed && committed != kind)
                return CrowdRebindAction.Flip;
            return CrowdRebindAction.Confirm;
        }

        if (committedKind.HasValue)
        {
            kind = committedKind.Value;
            return CrowdRebindAction.Confirm;
        }

        return CrowdRebindAction.Abandon;
    }

    private static bool IsCatalogStale(RemoteIslandSnapshot island, long now) =>
        island.LastUpdate > 0 && now - island.LastUpdate > CatalogStaleSeconds;

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
            || islandDC != excludePotDC)
            return true;

        var excluded = excludePotKind.Value;
        if (kind != excluded)
            return true;

        var thisSpawn = alive
            ? now - (FateAliveSeconds - untilGoneSeconds)
            : now + waitSeconds;
        kind = excluded == PotKind.North ? PotKind.South : PotKind.North;
        alive = false;
        waitSeconds = (int)Math.Max(0, thisSpawn + RespawnSeconds - now);
        untilGoneSeconds = waitSeconds + FateAliveSeconds;
        return waitSeconds > 0;
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

        if (!TryLastSpawn(island, out var lastSpawn, out var lastNorth))
            return false;

        if (now < lastSpawn)
        {
            kind = lastNorth ? PotKind.North : PotKind.South;
            waitSeconds = (int)(lastSpawn - now);
            untilGoneSeconds = waitSeconds + FateAliveSeconds;
            return waitSeconds > 0;
        }

        var lastDeath = lastNorth ? island.NorthDeath : island.SouthDeath;
        var lastDead = lastDeath > lastSpawn && lastDeath - lastSpawn >= 180;
        if (!lastDead && now < lastSpawn + FateAliveSeconds)
        {
            kind = lastNorth ? PotKind.North : PotKind.South;
            waitSeconds = 0;
            untilGoneSeconds = (int)(lastSpawn + FateAliveSeconds - now);
            return untilGoneSeconds > 0;
        }

        var spawn = lastSpawn;
        var isNorth = lastNorth;
        for (var i = 0; i < 8; i++)
        {
            spawn += RespawnSeconds;
            isNorth = !isNorth;
            var death = isNorth ? island.NorthDeath : island.SouthDeath;
            var knownDead = death > spawn && death - spawn >= 180;

            if (now < spawn)
            {
                kind = isNorth ? PotKind.North : PotKind.South;
                waitSeconds = (int)(spawn - now);
                untilGoneSeconds = waitSeconds + FateAliveSeconds;
                return true;
            }

            if (!knownDead && now < spawn + FateAliveSeconds)
            {
                kind = isNorth ? PotKind.North : PotKind.South;
                waitSeconds = 0;
                untilGoneSeconds = (int)(spawn + FateAliveSeconds - now);
                return untilGoneSeconds > 0;
            }
        }

        return false;
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
        if (wait <= 0)
            return $"{label}即将刷新";
        return $"下个{label} {FormatMmSs(wait)}";
    }

    internal static bool TooLateToCrossDC(int wait, int untilGone, bool alive) =>
        (alive || wait == 0) && untilGone < CrossDCMinRemainSeconds;

    /// <summary>
    ///     窗内但没 Running：开头两分钟 wait=0 留下；过后按下一罐算，避免钉死不换线。
    /// </summary>
    internal static int WaitAfterCrowdWindow(int wait, int untilGone, bool stayIfEarly)
    {
        if (wait > 0)
            return wait;
        if (stayIfEarly && untilGone > FateAliveSeconds - LocalStayGraceSeconds)
            return 0;
        var next = untilGone + (int)(RespawnSeconds - FateAliveSeconds);
        return next > 0 ? next : (int)RespawnSeconds;
    }

    internal static string FormatMmSs(int seconds)
    {
        if (seconds < 0)
            seconds = 0;
        var m = seconds / 60;
        var s = seconds % 60;
        return $"{m:00}:{s:00}";
    }

    private static IslandCluster? PickCluster(
        List<IslandCluster> clusters,
        long now,
        long preferNorthSpawn,
        long preferSouthSpawn)
    {
        if (TryCycleEpoch(new RemoteIslandSnapshot(default, 0, now, preferNorthSpawn, preferSouthSpawn, 0, 0), out var preferEpoch))
        {
            foreach (var cluster in clusters)
            {
                if (Math.Abs(cluster.Epoch - preferEpoch) <= InstanceAlignSeconds)
                    return cluster;
            }
        }

        IslandCluster? best = null;
        var bestFresh = -1;
        var bestUpdate = long.MinValue;
        var bestRows = -1;
        foreach (var cluster in clusters)
        {
            var fresh = CountFresh(cluster, now);
            var update = NewestUpdate(cluster);
            var rows = cluster.Rows.Count;
            if (best != null)
            {
                if (fresh < bestFresh)
                    continue;
                if (fresh == bestFresh && update < bestUpdate)
                    continue;
                if (fresh == bestFresh && update == bestUpdate && rows <= bestRows)
                    continue;
            }

            best = cluster;
            bestFresh = fresh;
            bestUpdate = update;
            bestRows = rows;
        }

        return best;
    }

    private static RemoteIslandSnapshot FlattenCluster(IslandCluster cluster, CnDataCenterKind dc, ushort territory)
    {
        var newest = cluster.Rows[0];
        foreach (var row in cluster.Rows)
        {
            if (row.LastUpdate > newest.LastUpdate)
                newest = row;
        }

        if (!TryCycleEpoch(newest, out var epoch))
            epoch = cluster.Epoch;

        long northSpawn = 0;
        long southSpawn = 0;
        long northDeath = 0;
        long southDeath = 0;
        long lastUpdate = 0;
        foreach (var row in cluster.Rows)
        {
            if (FitsSide(row.NorthSpawn, epoch, south: false) && row.NorthSpawn >= northSpawn)
            {
                northSpawn = row.NorthSpawn;
                northDeath = row.NorthDeath;
            }

            if (FitsSide(row.SouthSpawn, epoch, south: true) && row.SouthSpawn >= southSpawn)
            {
                southSpawn = row.SouthSpawn;
                southDeath = row.SouthDeath;
            }

            if (row.LastUpdate > lastUpdate)
                lastUpdate = row.LastUpdate;
        }

        return new RemoteIslandSnapshot(dc, territory, lastUpdate, northSpawn, southSpawn, northDeath, southDeath, newest.WorldID);
    }

    private static bool TryCycleEpoch(RemoteIslandSnapshot row, out long epoch)
    {
        if (!TryLastSpawn(row, out var lastSpawn, out var lastNorth))
        {
            epoch = 0;
            return false;
        }

        epoch = lastNorth ? lastSpawn : lastSpawn - RespawnSeconds;
        return true;
    }

    private static bool TryLastSpawn(RemoteIslandSnapshot island, out long lastSpawn, out bool lastNorth)
    {
        lastSpawn = 0;
        lastNorth = false;
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

        return lastSpawn > 0;
    }

    private static bool FitsSide(long spawn, long epoch, bool south)
    {
        if (spawn <= 0)
            return false;
        var origin = south ? epoch + RespawnSeconds : epoch;
        var rem = ((spawn - origin) % CycleSeconds + CycleSeconds) % CycleSeconds;
        return rem <= InstanceAlignSeconds || rem >= CycleSeconds - InstanceAlignSeconds;
    }

    private static bool IsFreshRow(RemoteIslandSnapshot row, long now)
    {
        if (row.LastUpdate > 0 && now - row.LastUpdate > FreshUpdateSeconds)
            return false;
        var last = Math.Max(row.NorthSpawn, row.SouthSpawn);
        return last > 0 && now - last <= FreshSpawnSeconds;
    }

    private static int CountFresh(IslandCluster cluster, long now)
    {
        var n = 0;
        foreach (var row in cluster.Rows)
        {
            if (IsFreshRow(row, now))
                n++;
        }

        return n;
    }

    private static long NewestUpdate(IslandCluster cluster)
    {
        long last = 0;
        foreach (var row in cluster.Rows)
        {
            if (row.LastUpdate > last)
                last = row.LastUpdate;
        }

        return last;
    }
}
