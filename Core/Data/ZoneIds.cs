namespace OccultPot.Core.Data;

internal static class ZoneIds
{
	internal const ushort SouthHorn = 1252;

	internal const ushort NorthHorn = 1346;

	internal static bool IsSupportedIsland(ushort territoryID)
	{
		if (territoryID == 1252 || territoryID == 1346)
		{
			return true;
		}
		return false;
	}
}
