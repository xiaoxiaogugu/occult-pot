using OmenTools;

namespace OccultPot.Core.Adapters;

internal static class BmrAi
{
	private static bool? wantOn;
	private static DateTime lastSentUTC;

	internal static void On()
	{
		wantOn = true;
		if (lastSentUTC != default && (DateTime.UtcNow - lastSentUTC).TotalSeconds < 2)
			return;
		Send(true);
	}

	internal static void Off()
	{
		if (wantOn == false)
			return;
		wantOn = false;
		Send(false);
	}

	internal static void ForceOff()
	{
		wantOn = false;
		lastSentUTC = default;
		Send(false);
	}

	private static void Send(bool on)
	{
		lastSentUTC = DateTime.UtcNow;
		var command = on ? "/bmrai on" : "/bmrai off";
		try
		{
			DService.Instance().Command.ProcessCommand(command);
		}
		catch (Exception)
		{
			ExternalCommands.Run(command);
		}
	}
}
