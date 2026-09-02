using System.Numerics;
using OccultPot.Models;

namespace OccultPot.Core.Data;

internal sealed record PotSideLayout(
    PotKind Kind,
    uint FateID,
    string FateName,
    string PtpName,
    Vector3 PtpSpawn,
    Vector3 PotCenter,
    Vector3 ObservePoint)
{
    public string KindLabel => Kind == PotKind.North ? "北罐" : "南罐";
}

internal static class IslandPotLayout
{
    internal const string NorthFateName = "幸福的魔法罐";
    internal const string SouthFateName = "瑟瑟发抖的魔法罐";

    private static readonly PotSideLayout SouthHornSouth = new(
        PotKind.South,
        1977,
        SouthFateName,
        "石塔水沼",
        new Vector3(-384.55502f, 97.29398f, 277.75458f),
        new Vector3(-481f, 75f, 528f),
        new Vector3(-441.29837f, 74.90292f, 484.32523f));

    private static readonly PotSideLayout SouthHornNorth = new(
        PotKind.North,
        1976,
        NorthFateName,
        "古树湿原",
        new Vector3(302.4757f, 102.99427f, 305.8504f),
        new Vector3(200f, 111.7266f, -215f),
        new Vector3(187.74405f, 111.51086f, -191.35019f));

    private static readonly PotSideLayout NorthHornSouth = CreateNorthIsland(
        PotKind.South,
        2073,
        SouthFateName,
        "浮游遗迹",
        new Vector3(-549.806f, 67.189f, 569.251f),
        new Vector3(-504.939f, 53.139f, 243.969f));

    private static readonly PotSideLayout NorthHornNorth = CreateNorthIsland(
        PotKind.North,
        2072,
        NorthFateName,
        "沉没圣堂前",
        new Vector3(358.155f, 45.168f, -557.77f),
        new Vector3(233.020f, 7.659f, -470.041f));

    private static readonly Vector3 SouthHornCamp = new(834f, 73f, -694.6f);
    private static readonly Vector3 SouthHornCampLua = new(834.46564f, 73f, -695.5838f);
    private static readonly Vector3 NorthHornCamp = new(882f, 258.5f, 882f);

    internal static string EntryCommand(ushort territoryID) =>
        territoryID == ZoneIds.NorthHorn ? "/pdrfe ocn" : "/pdrfe ocs";

    internal static string IslandLabel(ushort territoryID) =>
        territoryID switch
        {
            ZoneIds.SouthHorn => "南征之章",
            ZoneIds.NorthHorn => "北征之章",
            _ => $"区域 {territoryID}",
        };

    internal static PotSideLayout? South(ushort territoryID) =>
        territoryID switch
        {
            ZoneIds.SouthHorn => SouthHornSouth,
            ZoneIds.NorthHorn => NorthHornSouth,
            _ => null,
        };

    internal static PotSideLayout? North(ushort territoryID) =>
        territoryID switch
        {
            ZoneIds.SouthHorn => SouthHornNorth,
            ZoneIds.NorthHorn => NorthHornNorth,
            _ => null,
        };

    internal static PotSideLayout? ByKind(ushort territoryID, PotKind kind) =>
        kind == PotKind.North ? North(territoryID) : South(territoryID);

    internal static bool IsPotFate(uint fateID) =>
        fateID == SouthHornSouth.FateID ||
        fateID == SouthHornNorth.FateID ||
        fateID == NorthHornSouth.FateID ||
        fateID == NorthHornNorth.FateID;

    internal static bool TryCamp(ushort territoryID, out Vector3 spawn, out string name)
    {
        switch (territoryID)
        {
            case ZoneIds.SouthHorn:
                spawn = SouthHornCamp;
                name = "调查队营地";
                return true;
            case ZoneIds.NorthHorn:
                spawn = NorthHornCamp;
                name = "初始营地";
                return true;
            default:
                spawn = default;
                name = "";
                return false;
        }
    }

    internal static bool TryCampLuaStand(ushort territoryID, out Vector3 lua)
    {
        if (territoryID == ZoneIds.SouthHorn)
        {
            lua = SouthHornCampLua;
            return true;
        }

        lua = default;
        return false;
    }

    internal static PotKind? GuessKindFromChat(string text)
    {
        if (string.IsNullOrEmpty(text))
            return null;
        if (text.Contains(NorthFateName, StringComparison.Ordinal))
            return PotKind.North;
        if (text.Contains(SouthFateName, StringComparison.Ordinal))
            return PotKind.South;
        return null;
    }

    internal static Vector3 RandomOffset(Vector3 center, Random rng, float maxRadius = 25f)
    {
        var angle = (float)(rng.NextDouble() * Math.PI * 2);
        var dist = (float)(rng.NextDouble() * maxRadius);
        return new Vector3(
            center.X + MathF.Cos(angle) * dist,
            center.Y,
            center.Z + MathF.Sin(angle) * dist);
    }

    internal static Vector3 RandomObserveStand(Vector3 observe, Vector3 potCenter, Random rng)
    {
        var p = RandomOffset(observe, rng, PotConstants.ObserveRandomRadius);
        var flat = new Vector3(p.X - potCenter.X, 0f, p.Z - potCenter.Z);
        var d = flat.Length();
        if (d <= PotConstants.MaxObserveFromPot || d < 0.1f)
            return p;

        flat = Vector3.Normalize(flat) * PotConstants.MaxObserveFromPot;
        return new Vector3(potCenter.X + flat.X, p.Y, potCenter.Z + flat.Z);
    }

    private static PotSideLayout CreateNorthIsland(
        PotKind kind,
        uint fateID,
        string fateName,
        string ptpName,
        Vector3 spawn,
        Vector3 center) =>
        new(kind, fateID, fateName, ptpName, spawn, center, DeriveObserve(center, spawn));

    private static Vector3 DeriveObserve(Vector3 center, Vector3 spawn, float distance = 45f)
    {
        var flat = new Vector3(spawn.X - center.X, 0f, spawn.Z - center.Z);
        if (flat.LengthSquared() < 1f)
            return center;

        flat = Vector3.Normalize(flat) * distance;
        return new Vector3(center.X + flat.X, center.Y, center.Z + flat.Z);
    }
}
