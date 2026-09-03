using System;
using System.Collections.Generic;
using System.Numerics;
using OccultPot.Core.Adapters;
using OccultPot.Core.Game;
using OccultPot.Core.Nav;
using OccultPot.Models;
using OmenTools;
using OmenTools.OmenService;

namespace OccultPot.Core.Dig;

internal sealed class OccultPotHooks
{
	private readonly TpNavigator tp = new TpNavigator();

	private readonly VNavController vnav = new VNavController();

	private readonly IslandTravel travel;

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
		travel = new IslandTravel(vnav);
	}

	internal void Tick()
	{
		if (!preferTp())
			travel.Tick();
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
			travel.Stop();
			if (!allowMount && PlayerReader.IsOnMount())
			{
				MountActions.TryDismount();
				return false;
			}
			return true;
		}
		if (!allowMount)
		{
			travel.Stop();
			if (PlayerReader.IsOnMount())
			{
				MountActions.TryDismount();
				return false;
			}
			return vnav.MoveTo(position);
		}

		// 挖箱绿玩：远点走水晶 hop，禁止返回营地。
		travel.Begin((ushort)TerritoryID, position, "箱点", allowReturn: false, destArrive: arriveRadius);
		return travel.IsDone || LocalPlayerState.DistanceTo3D(position) <= arriveRadius;
	}

	public bool StopMove()
	{
		tp.Stop();
		travel.Stop();
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
			return LocalPlayerState.DistanceTo3D(target) <= radius || vnav.HasArrived(target, radius);
		}
		return tp.HasArrived(target, radius);
	}

	internal bool IsNavigating()
	{
		if (!PreferTp)
			return travel.IsRunning || vnav.IsRunning();
		if (activeTarget is { } target)
			return tp.IsTeleporting(target, 6f);
		return tp.IsPending;
	}
}
