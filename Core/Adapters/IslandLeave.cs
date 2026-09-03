using System;
using OmenTools.Dalamud;
using OmenTools.Interop.Game.AddonEvent;
using OmenTools.Interop.Game.ExecuteCommand.Implementations;

namespace OccultPot.Core.Adapters;

internal static class IslandLeave
{
	private static DateTime lastAttemptUTC;

	private static int attempt;

	internal static void Reset()
	{
		lastAttemptUTC = default;
		attempt = 0;
	}

	internal static void TickLeave()
	{
		var utcNow = DateTime.UtcNow;
		if ((utcNow - lastAttemptUTC).TotalSeconds >= 1.5)
		{
			lastAttemptUTC = utcNow;
			attempt++;
			try
			{
				DutyCommand.Leave();
			}
			catch (Exception ex)
			{
				DLog.Error("[离岛] DutyCommand.Leave 失败", ex);
			}

			if (attempt == 1 || attempt % 3 == 0)
				ExternalCommands.Run("/pdr leaveduty");

			if (attempt == 2 || attempt % 4 == 0)
			{
				ExternalCommands.Run("/callback _ToDoList true 23");
				ExternalCommands.Run("/callback ContentsFinderMenu true 0");
			}
		}

		AddonSelectYesnoEvent.ClickYes("离开");
		AddonSelectYesnoEvent.ClickYes("退出");
	}
}
