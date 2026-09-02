using OccultPot.Core.Game;
using OmenTools;
using OmenTools.Extensions;
using OmenTools.Interop.Game.ExecuteCommand.Implementations;

namespace OccultPot.Core.Adapters;

internal static class MountActions
{
	internal static bool CanMount()
	{
		if (PlayerReader.IsOnMount() || !PlayerReader.IsAvailable() || PlayerReader.IsBusy())
		{
			return false;
		}
		return IConditionExtension.get_IsAbleToMount(DService.Instance().Condition);
	}

	internal static bool TryMount()
	{
		if (!CanMount())
		{
			return false;
		}
		ExternalCommands.Run("/共通技能 随机坐骑");
		return true;
	}

	internal static void TryDismount()
	{
		if (PlayerReader.IsOnMount())
		{
			MountCommand.Dismount();
		}
	}
}
