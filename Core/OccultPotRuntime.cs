using OccultPot.Core.Adapters;
using OccultPot.Localization;
using OmenTools.OmenService;

namespace OccultPot.Core;

internal static class OccultPotRuntime
{
	internal static bool IsSupported => GameState.IsCN;

	internal static RuntimeStatus UnsupportedStatus => RuntimeStatus.Of(RuntimeStatusCode.ErrorCnOnly);

	internal static string UnsupportedMessage => OccultPotLoc.Get("ErrorCnOnly");

	internal static bool RequireSupported()
	{
		if (IsSupported)
		{
			return true;
		}
		ExternalCommands.Echo(UnsupportedMessage);
		return false;
	}
}
