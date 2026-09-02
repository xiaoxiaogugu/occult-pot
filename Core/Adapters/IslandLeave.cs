using System;
using OmenTools.Interop.Game.ExecuteCommand.Implementations;

namespace OccultPot.Core.Adapters;

internal static class IslandLeave
{
	private static DateTime lastAttemptUTC;

	private static int attempt;

	internal static void Reset()
	{
		lastAttemptUTC = default(DateTime);
		attempt = 0;
	}

	internal static void TickLeave()
	{
		DateTime utcNow = DateTime.UtcNow;
		if (!((utcNow - lastAttemptUTC).TotalSeconds < 1.5))
		{
			lastAttemptUTC = utcNow;
			attempt++;
			try
			{
				DutyCommand.Leave();
			}
			catch
			{
			}
			if (attempt == 1 || attempt % 3 == 0)
			{
				ExternalCommands.Run("/pdr leaveduty");
			}
			if (attempt == 2 || attempt % 4 == 0)
			{
				ExternalCommands.Run("/callback _ToDoList true 23");
				ExternalCommands.Run("/callback ContentsFinderMenu true 0");
				ExternalCommands.Run("/callback SelectYesno true 0");
			}
		}
	}
}
