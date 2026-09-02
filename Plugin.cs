using System;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using OmenTools;

namespace OccultPot;

public sealed class Plugin : IDalamudPlugin, IDisposable
{
	private readonly OccultCrescent core;

	public Plugin(IDalamudPluginInterface pluginInterface, ICommandManager commands, IPluginLog log)
	{
		DService.Init(pluginInterface);
		core = new OccultCrescent(pluginInterface, commands, log);
	}

	public void Dispose()
	{
		core.Dispose();
		DService.Uninit();
	}
}
