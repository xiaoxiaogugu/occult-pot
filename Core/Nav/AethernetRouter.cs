using System.Numerics;
using OccultPot.Core.Game;

namespace OccultPot.Core.Nav;

internal enum AethernetRouteKind
{
    Walk,
    ReturnWalk,
    ReturnTeleportWalk,
    WalkTeleportWalk,
}

internal sealed record AethernetRoute(
    AethernetRouteKind Kind,
    IslandAethernetShard? Source,
    IslandAethernetShard? Destination,
    float Cost,
    Vector3 From,
    Vector3 To);

internal static class AethernetRouter
{
    internal const float TeleportCost = 50f;
    internal const float ShardRange = 4.2f;
    internal const float CampArriveRange = 30f;

    internal static AethernetRoute Decide(ushort territory, Vector3 from, Vector3 to)
    {
        var shards = IslandAethernet.ForTerritory(territory);
        var walkCost = Vector3.Distance(from, to);
        var walk = new AethernetRoute(AethernetRouteKind.Walk, null, null, walkCost, from, to);
        if (shards.Count == 0)
            return walk;

        var source = shards.OrderBy(s => Vector3.DistanceSquared(from, s.Stand)).First();
        var dest = shards.OrderBy(s => Vector3.DistanceSquared(to, s.Landing)).First();
        if (source.Name == dest.Name)
            return walk;

        var hopCost = Vector3.Distance(from, source.Stand) + TeleportCost + Vector3.Distance(dest.Landing, to);
        var hop = new AethernetRoute(AethernetRouteKind.WalkTeleportWalk, source, dest, hopCost, from, to);
        return hopCost < walkCost ? hop : walk;
    }

    internal static bool NearStand(IslandAethernetShard shard, float range = ShardRange) =>
        PlayerReader.DistanceTo(shard.Stand) <= range;

    internal static bool NearLanding(IslandAethernetShard shard, float range = ShardRange) =>
        PlayerReader.DistanceTo(shard.Landing) <= range;

    internal static bool NearCamp(IslandAethernetShard? camp, float range = CampArriveRange) =>
        camp is { IsCamp: true }
        && (NearLanding(camp, range) || NearStand(camp, range));

    internal static bool PlayerNearCamp(ushort territory, float range = CampArriveRange)
    {
        var camp = IslandAethernet.ForTerritory(territory).FirstOrDefault(s => s.IsCamp);
        return NearCamp(camp, range);
    }

    internal static bool AtSource(IslandAethernetShard source, ushort territory) =>
        NearStand(source)
        || NearLanding(source)
        || (source.IsCamp && PlayerNearCamp(territory));
}
