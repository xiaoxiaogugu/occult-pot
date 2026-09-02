using System;
using OmenTools;
using OmenTools.Dalamud;
using OmenTools.OmenService;
using OmenTools.OmenService.Abstractions;

namespace OccultPot.Core.Adapters;

internal static class ExternalCommands
{
	internal static bool Run(string command)
	{
		if (RequiresCnClient(command) && !OccultPotRuntime.IsSupported)
		{
			return false;
		}
		try
		{
			OmenServiceBase<ChatManager>.Instance().SendMessage(command);
			return true;
		}
		catch (Exception ex)
		{
			try
			{
				DService.Instance().Command.ProcessCommand(command);
				return true;
			}
			catch (Exception ex2)
			{
				DLog.Error("[OccultPot] 命令执行失败: " + command, ex2);
				DLog.Error("[OccultPot] ChatManager 发送失败", ex);
				return false;
			}
		}
	}

	internal static void Echo(string message)
	{
		Run("/e " + message);
	}

	private static bool RequiresCnClient(string command)
	{
		if (string.IsNullOrWhiteSpace(command))
		{
			return false;
		}
		string text = command.TrimStart();
		if (!text.StartsWith("/pdr", StringComparison.OrdinalIgnoreCase) && !text.StartsWith("/pdrfe", StringComparison.OrdinalIgnoreCase) && !text.StartsWith("/bmrai", StringComparison.OrdinalIgnoreCase))
		{
			return text.StartsWith("/xsz", StringComparison.OrdinalIgnoreCase);
		}
		return true;
	}
}
