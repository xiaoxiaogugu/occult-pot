namespace OccultPot.Core.Adapters;

internal static class BmrAi
{
	private static bool? wantOn;

	internal static void On()
	{
		if (wantOn != true)
		{
			ExternalCommands.Run("/bmrai on");
			wantOn = true;
		}
	}

	internal static void Off()
	{
		if (wantOn != false)
		{
			ExternalCommands.Run("/bmrai off");
			wantOn = false;
		}
	}

	internal static void ForceOff()
	{
		ExternalCommands.Run("/bmrai off");
		wantOn = false;
	}
}
