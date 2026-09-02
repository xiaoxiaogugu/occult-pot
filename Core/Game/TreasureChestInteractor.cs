using System;
using System.Collections.Generic;
using System.Numerics;
using OccultPot.Core.Dig;
using OmenTools;
using OmenTools.Dalamud.Services.Game.Object.Abstractions.ObjectKinds;
using OmenTools.Extensions;
using OmenTools.OmenService;

namespace OccultPot.Core.Game;

internal static class TreasureChestInteractor
{
	internal const float DetectRange = 12f;

	internal const float OpenRange = 5f;

	private const float TableMatchRange = 8f;

	private static readonly HashSet<uint> PreexistingEntityIDs = new HashSet<uint>();

	private static uint trackedEntityID;

	internal static uint TrackedEntityID => trackedEntityID;

	internal static void SnapshotExisting()
	{
		PreexistingEntityIDs.Clear();
		trackedEntityID = 0u;
		foreach (IGameObject item in DService.Instance().ObjectTable)
		{
			if (item != null && item.IsValid() && LooksLikeChest(item) && item.EntityID != 0)
			{
				PreexistingEntityIDs.Add(item.EntityID);
			}
		}
	}

	internal static void ClearSnapshot()
	{
		PreexistingEntityIDs.Clear();
		trackedEntityID = 0u;
	}

	internal static bool TryOpenNearby(float maxDistance = 5f)
	{
		if (PlayerReader.IsMoving)
		{
			return false;
		}
		IGameObject gameObject = FindPotChest(maxDistance);
		if (gameObject == null)
		{
			return false;
		}
		if (LocalPlayerState.DistanceToObject3D(gameObject) > 5f)
		{
			return false;
		}
		return gameObject.TargetInteract();
	}

	internal static bool HasPotChest(float maxDistance = 12f)
	{
		return FindPotChest(maxDistance) != null;
	}

	internal static Vector3? GetPotChestPosition(float maxDistance = 12f)
	{
		return FindPotChest(maxDistance)?.Position;
	}

	internal static bool TrackedChestGone()
	{
		if (trackedEntityID == 0)
		{
			return false;
		}
		IGameObject gameObject = DService.Instance().ObjectTable.SearchByEntityID(trackedEntityID);
		if (gameObject != null && gameObject.IsValid())
		{
			return !gameObject.IsTargetable;
		}
		return true;
	}

	private static IGameObject? FindPotChest(float maxDistance)
	{
		if (trackedEntityID != 0)
		{
			IGameObject gameObject = DService.Instance().ObjectTable.SearchByEntityID(trackedEntityID);
			if (gameObject != null && gameObject.IsValid() && gameObject.IsTargetable && LooksLikeChest(gameObject))
			{
				return gameObject;
			}
			return null;
		}
		IPlayerCharacter playerCharacter = LocalPlayerState.Object;
		if (playerCharacter == null)
		{
			return null;
		}
		IGameObject gameObject2 = DService.Instance().ObjectTable.FindNearest(playerCharacter.Position, (IGameObject obj) => IsNewPotChest(obj) && LocalPlayerState.DistanceToObject3D(obj) <= maxDistance);
		if (gameObject2 != null && gameObject2.EntityID != 0)
		{
			trackedEntityID = gameObject2.EntityID;
		}
		return gameObject2;
	}

	private static bool IsNewPotChest(IGameObject obj)
	{
		if (!obj.IsValid() || !obj.IsTargetable || !LooksLikeChest(obj))
		{
			return false;
		}
		uint entityID = obj.EntityID;
		if (entityID == 0 || PreexistingEntityIDs.Contains(entityID))
		{
			return false;
		}
		return IsOnPotChestTable(obj.Position);
	}

	private static bool IsOnPotChestTable(Vector3 position)
	{
		IReadOnlyList<Vector3> all = OccultPotChestTables.GetAll(DService.Instance().ClientState.TerritoryType);
		if (all.Count == 0)
		{
			return false;
		}
		float num = 64f;
		foreach (Vector3 item in all)
		{
			float num2 = item.X - position.X;
			float num3 = item.Z - position.Z;
			if (num2 * num2 + num3 * num3 <= num)
			{
				return true;
			}
		}
		return false;
	}

	private static bool LooksLikeChest(IGameObject obj)
	{
		//IL_0001: Unknown result type (might be due to invalid IL or missing references)
		//IL_0007: Invalid comparison between Unknown and I4
		//IL_000a: Unknown result type (might be due to invalid IL or missing references)
		//IL_0010: Invalid comparison between Unknown and I4
		if ((int)obj.ObjectKind != 4)
		{
			if ((int)obj.ObjectKind == 7)
			{
				return LooksLikeTreasureName(obj.Name.ToString());
			}
			return false;
		}
		return true;
	}

	private static bool LooksLikeTreasureName(string name)
	{
		if (!name.Contains("宝箱", StringComparison.Ordinal) && !name.Contains("Coffer", StringComparison.OrdinalIgnoreCase))
		{
			return name.Contains("Treasure", StringComparison.OrdinalIgnoreCase);
		}
		return true;
	}
}
