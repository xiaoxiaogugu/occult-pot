using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using OccultPot.Core.Adapters;
using OccultPot.Core.Game;
using OccultPot.Models;
using OmenTools;
using OmenTools.OmenService;

namespace OccultPot.Core.Dig;

internal sealed class OccultPotHooks
{
	private readonly TpNavigator tp = new TpNavigator();

	private readonly VNavController vnav = new VNavController();

	private readonly Action<StopReason>? onStopped;

	private readonly Func<bool> preferTp;

	private readonly Func<bool> useDiveTp;

	private readonly Func<float> tpIntervalSeconds;

	private Vector3? activeTarget;

	private PotKind? digKind;

	private bool rerollOnly;

	public uint TerritoryID => DService.Instance().ClientState.TerritoryType;

	public Vector3 PlayerPosition => LocalPlayerState.Object?.Position ?? Vector3.Zero;

	public double NowSeconds => DateTime.UtcNow.Subtract(DateTime.UnixEpoch).TotalSeconds;

	internal bool PreferTp => preferTp();

	internal string NavDetail
	{
		get
		{
			if (!PreferTp)
			{
				return vnav.LastDetail;
			}
			return tp.LastDetail;
		}
	}

	public OccultPotHooks(Action<StopReason>? onStopped = null, Func<bool>? preferTp = null, Func<bool>? useDiveTp = null, Func<float>? tpIntervalSeconds = null)
	{
		this.onStopped = onStopped;
		this.preferTp = preferTp ?? ((Func<bool>)(() => true));
		this.useDiveTp = useDiveTp ?? ((Func<bool>)(() => true));
		this.tpIntervalSeconds = tpIntervalSeconds ?? ((Func<float>)(() => 5f));
	}

	public IReadOnlyList<Vector3> GetChestTable(uint territoryID)
	{
		return OccultPotChestTables.GetPositions(territoryID, digKind, rerollOnly);
	}

	public void ConfigureChestTable(PotKind? kind)
	{
		digKind = kind;
		rerollOnly = false;
	}

	public void PreferRerollChests()
	{
		rerollOnly = true;
	}

	public int GetElixirCount()
	{
		return InventoryReader.GetElixirCount();
	}

	public bool MoveTo(Vector3 position)
	{
		activeTarget = position;
		if (!preferTp())
		{
			return MoveGreen(position, 6f, allowMount: true);
		}
		vnav.Stop();
		return tp.MoveTo(position, NowSeconds, tpIntervalSeconds(), useDiveTp());
	}

	public bool MoveToInteract(Vector3 position)
	{
		activeTarget = position;
		tp.Stop();
		return MoveGreen(position, 5f, allowMount: false);
	}

	private bool MoveGreen(Vector3 position, float arriveRadius, bool allowMount)
	{
		tp.Stop();
		if (LocalPlayerState.DistanceTo3D(position) <= arriveRadius)
		{
			if (!allowMount && PlayerReader.IsOnMount())
			{
				MountActions.TryDismount();
				return false;
			}
			return true;
		}
		if (!allowMount)
		{
			if (PlayerReader.IsOnMount())
			{
				MountActions.TryDismount();
				return false;
			}
			return vnav.MoveTo(position);
		}
		bool flag = DService.Instance().Condition[(ConditionFlag)26];
		if (!PlayerReader.IsOnMount() && !flag && MountActions.CanMount())
		{
			MountActions.TryMount();
			return false;
		}
		return vnav.MoveTo(position);
	}

	public bool StopMove()
	{
		tp.Stop();
		vnav.Stop();
		activeTarget = null;
		return true;
	}

	public bool TryUseElixir()
	{
		return InventoryReader.TryUseElixir();
	}

	public void SnapshotExistingChests()
	{
		TreasureChestInteractor.SnapshotExisting();
	}

	public bool HasPotChest(float maxDistance = 80f)
	{
		return TreasureChestInteractor.HasPotChest(maxDistance);
	}

	public bool TryOpenPotChest(float maxDistance = 80f)
	{
		return TreasureChestInteractor.TryOpenNearby(maxDistance);
	}

	public Vector3? NearbyPotChestPosition(float maxDistance = 80f)
	{
		return TreasureChestInteractor.GetPotChestPosition(maxDistance);
	}

	public bool TrackedChestGone()
	{
		return TreasureChestInteractor.TrackedChestGone();
	}

	public void OnStopped(StopReason reason)
	{
		onStopped?.Invoke(reason);
	}

	internal bool HasArrived(Vector3 target, float radius)
	{
		if (!PreferTp)
		{
			return vnav.HasArrived(target, radius);
		}
		return tp.HasArrived(target, radius);
	}

	internal bool IsNavigating()
	{
		if (!PreferTp)
		{
			return vnav.IsRunning();
		}
		Vector3? vector = activeTarget;
		if (vector.HasValue)
		{
			Vector3 valueOrDefault = vector.GetValueOrDefault();
			return tp.IsTeleporting(valueOrDefault, 6f);
		}
		return tp.IsPending;
	}
}
