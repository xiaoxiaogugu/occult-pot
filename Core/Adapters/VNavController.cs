using System.Numerics;
using OccultPot.Core.Game;
using OmenTools;
using OmenTools.Dalamud;
using OmenTools.Extensions;
using OmenTools.OmenService;

namespace OccultPot.Core.Adapters;

internal sealed class VNavController
{
	internal string LastDetail { get; private set; } = string.Empty;

	internal bool IsReady()
	{
		if (!DService.Instance().PI.IsPluginEnabled("vnavmesh"))
		{
			LastDetail = "未安装或未加载 vnavmesh";
			return false;
		}
		if (!vnavmeshIPC.GetIsNavReady())
		{
			LastDetail = "等待 vnavmesh 网格";
			return false;
		}
		return true;
	}

	internal void Stop()
	{
		try
		{
			if (PlayerReader.IsTransitionLocked())
			{
				LastDetail = "过图中，跳过停路";
				return;
			}

			if (!IsFollowing())
				return;

			vnavmeshIPC.StopPathfind();
		}
		catch
		{
		}
		LastDetail = "已停止寻路";
	}

	internal bool MoveTo(Vector3 destination)
	{
		if (!IsReady() || PlayerReader.IsTransitionLocked())
		{
			return false;
		}
		if (vnavmeshIPC.PathfindAndMoveTo(destination, fly: false))
		{
			LastDetail = "vnav 寻路中";
			return true;
		}
		LastDetail = "vnav 寻路失败";
		return false;
	}

	internal bool PathfindTo(Vector3 destination)
	{
		if (!IsReady() || PlayerReader.IsTransitionLocked())
		{
			return false;
		}
		if (!vnavmeshIPC.PathfindAndMoveTo(destination, fly: false))
		{
			LastDetail = "vnav 寻路失败";
			return false;
		}
		LastDetail = "vnav 寻路中";
		return true;
	}

	internal bool IsRunning()
	{
		try
		{
			return IsFollowing()
				|| vnavmeshIPC.GetIsPathfindInProgress()
				|| vnavmeshIPC.GetIsNavPathfindInProgress();
		}
		catch
		{
			return false;
		}
	}

	private static bool IsFollowing()
	{
		try
		{
			return vnavmeshIPC.GetIsPathfindRunning();
		}
		catch
		{
			return false;
		}
	}

	internal bool HasArrived(Vector3 target, float radius)
	{
		if (LocalPlayerState.DistanceTo3D(target) <= radius)
		{
			return !IsRunning();
		}
		return false;
	}
}
