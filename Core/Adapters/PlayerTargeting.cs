using System;
using System.Numerics;
using OmenTools;
using OmenTools.Dalamud.Services.Game.Object.Abstractions.ObjectKinds;
using OmenTools.OmenService;

namespace OccultPot.Core.Adapters;

internal static class PlayerTargeting
{
	internal const float PotScanRadius = 50f;

	internal static bool HasPlayersNearPot(Vector3 potCenter)
	{
		return CountOtherPlayersNear(potCenter, 50f) > 0;
	}

	internal static int CountOtherPlayersNear(Vector3 origin, float radius2d)
	{
		//IL_002f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0035: Invalid comparison between Unknown and I4
		IPlayerCharacter playerCharacter = LocalPlayerState.Object;
		if (playerCharacter == null)
		{
			return 0;
		}
		int num = 0;
		foreach (IGameObject item in DService.Instance().ObjectTable)
		{
			if (item.IsValid() && (int)item.ObjectKind == 1 && item.GameObjectID != playerCharacter.GameObjectID && Distance2D(origin, item.Position) <= radius2d)
			{
				num++;
			}
		}
		return num;
	}

	private static float Distance2D(Vector3 a, Vector3 b)
	{
		float num = a.X - b.X;
		float num2 = a.Z - b.Z;
		return MathF.Sqrt(num * num + num2 * num2);
	}
}
