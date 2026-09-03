using System.Globalization;
using System.Numerics;
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
			if (IsRunning())
				vnavmeshIPC.StopPathfind();
			vnavmeshIPC.CancelAllQueries();
		}
		catch
		{
		}
		LastDetail = "已停止寻路";
	}

	internal static string MoveToCommand(Vector3 destination)
	{
		return $"/vnav moveto {destination.X.ToString(CultureInfo.InvariantCulture)} {destination.Y.ToString(CultureInfo.InvariantCulture)} {destination.Z.ToString(CultureInfo.InvariantCulture)}";
	}

	internal bool MoveTo(Vector3 destination)
	{
		if (!IsReady())
		{
			return false;
		}
		if (vnavmeshIPC.PathfindAndMoveTo(destination, fly: false))
		{
			LastDetail = "vnav 寻路中";
			return true;
		}
		ExternalCommands.Run(MoveToCommand(destination));
		LastDetail = "vnav 寻路中";
		return true;
	}

	internal bool PathfindTo(Vector3 destination)
	{
		if (!IsReady())
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
			return vnavmeshIPC.GetIsPathfindRunning() || vnavmeshIPC.GetIsPathfindInProgress() || vnavmeshIPC.GetIsNavPathfindInProgress();
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
