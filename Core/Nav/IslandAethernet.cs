using System.Numerics;
using OccultPot.Core.Data;

namespace OccultPot.Core.Nav;

internal sealed record IslandAethernetShard(
    string Name,
    Vector3 Stand,
    Vector3 Landing,
    bool IsCamp = false);

internal static class IslandAethernet
{
    private static readonly IslandAethernetShard[] South =
    [
        new("调查队营地", new(834.46564f, 73f, -695.5838f), new(835.3f, 73f, -695.9f), IsCamp: true),
        new("放浪神圣域遗迹", new(-170.1402f, 6.5f, -608.8823f), new(-169.1f, 6.5f, -609.4f)),
        new("水晶洞窟", new(-354.6388f, 99.993385f, -120.4032f), new(-354.6f, 100f, -120.7f)),
        new("古树湿原", new(302.4757f, 102.99427f, 305.8504f), new(306.94f, 103f, 306f)),
        new("石塔水沼", new(-384.55502f, 97.29398f, 277.75458f), new(-384f, 97.2f, 278.1f)),
    ];

    private static readonly IslandAethernetShard[] North =
    [
        new("北部调查队营地", new(881.846f, 258.5f, 881.904f), new(881.9951f, 258.5f, 881.9271f), IsCamp: true),
        new("沉没圣堂前", new(358.047f, 44.75f, -551.287f), new(358.2281f, 45.13132f, -557.2622f)),
        new("浮游遗迹", new(-545.184f, 67.25f, 596.173f), new(-549.5803f, 67.20449f, 596.822f)),
        new("腐坏的街道前", new(-391.456f, 40.75f, -442.441f), new(-387.1999f, 39.27517f, -437.6127f)),
        new("妖火渔村", new(-15.482f, 2.324f, -38.658f), new(-15.021f, 2.038227f, -43.83533f)),
        new("卡纳克城塞", new(450f, 70.5f, 530.75f), new(454.3429f, 69.99997f, 530.9988f)),
    ];

    internal static IReadOnlyList<IslandAethernetShard> ForTerritory(ushort territory) =>
        territory switch
        {
            ZoneIds.SouthHorn => South,
            ZoneIds.NorthHorn => North,
            _ => [],
        };
}
