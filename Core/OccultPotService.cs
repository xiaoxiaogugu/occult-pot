using System;
using Dalamud.Game.Chat;
using OccultPot.Core.Adapters;
using OccultPot.Core.Data;
using OccultPot.Core.Session;
using OccultPot.Core.Dig;
using OccultPot.Models;
using OmenTools;

namespace OccultPot.Core;

internal sealed class OccultPotService
{
	private readonly PotSessionOrchestrator session;

	private readonly PotDigController dig;

	private readonly Func<PluginConfiguration> getConfig;

	private readonly Action saveConfig;

	private bool digOnlyMode;

	internal SessionPhase Phase => session.Phase;

	internal RuntimeStatus Status => session.Status;

	internal bool IsDigOnlyMode => digOnlyMode;

	internal string ActivityLabel => SessionBriefFormatter.Activity(this);

	internal string NextTargetLabel => SessionBriefFormatter.NextTarget(this);

	internal bool TryGetNextTargetLabel(out string label) => session.TryGetNextTargetLabel(out label);

	internal string RouteSummary => session.RouteSummary;

	internal RuntimeStatus TrackerStatus => session.TrackerStatus;

	internal RuntimeStatus TrackerCatalog => session.TrackerCatalog;

	internal PotKind? ActiveKind => session.ActiveKind;

	internal bool IsSessionRunning => session.IsRunning;

	internal bool IsDigActive => dig.IsActive;

	internal bool IsRunning
	{
		get
		{
			if (getConfig().Enabled)
			{
				if (!session.IsRunning && !digOnlyMode)
				{
					return dig.IsActive;
				}
				return true;
			}
			return false;
		}
	}

	internal bool CanSkipIsland =>
		getConfig().Enabled && session.CanSkipCurrentIsland && !digOnlyMode;

	internal bool IsPotFateCombat => session.IsPotFateCombat;

	internal OccultPotSnapshot? DigSnapshot => dig.GetSnapshot();

	internal OccultPotService(Func<PluginConfiguration> getConfig, Action saveConfig)
	{
		this.getConfig = getConfig;
		this.saveConfig = saveConfig;
		dig = new PotDigController(OnDigStopped, () => getConfig().PreferTp, () => getConfig().UseDiveTp, () => getConfig().TpIntervalSeconds);
		session = new PotSessionOrchestrator(getConfig, saveConfig, dig);
	}

	private void OnDigStopped(StopReason reason)
	{
		if (digOnlyMode)
		{
			digOnlyMode = false;
			getConfig().Enabled = false;
			saveConfig();
		}
		else
		{
			session.OnDigStopped(reason);
		}
	}

	internal void Tick()
	{
		PluginConfiguration pluginConfiguration = getConfig();
		if (!pluginConfiguration.Enabled)
		{
			return;
		}
		if (digOnlyMode)
		{
			dig.Tick();
			if (!dig.IsActive)
			{
				digOnlyMode = false;
				pluginConfiguration.Enabled = false;
				saveConfig();
			}
		}
		else
		{
			session.Tick();
		}
	}

	internal void OnChatMessage(IHandleableChatMessage message)
	{
		OnChatText(((IMutableChatMessage)message).Message.TextValue);
	}

	internal void OnChatText(string text)
	{
		session.OnChatMessage(text);
	}

	internal void Start()
	{
		if (OccultPotRuntime.RequireSupported())
		{
			PluginConfiguration pluginConfiguration = getConfig();
			pluginConfiguration.SyncHomeWorldLock();
			digOnlyMode = false;
			pluginConfiguration.Enabled = true;
			saveConfig();
			session.Start();
		}
	}

	internal void StartDigOnly()
	{
		if (OccultPotRuntime.RequireSupported() && ZoneIds.IsSupportedIsland((ushort)DService.Instance().ClientState.TerritoryType))
		{
			session.Stop();
			PluginConfiguration pluginConfiguration = getConfig();
			digOnlyMode = true;
			pluginConfiguration.Enabled = true;
			saveConfig();
			if (!dig.Start().Success)
			{
				digOnlyMode = false;
				pluginConfiguration.Enabled = false;
				saveConfig();
			}
			else
			{
				ExternalCommands.Echo("[挖箱] 挖箱已启动");
			}
		}
	}

	internal void Stop()
	{
		digOnlyMode = false;
		session.Stop();
		if (dig.IsActive)
		{
			dig.Stop();
		}
		getConfig().Enabled = false;
		saveConfig();
	}

	internal void Uninit()
	{
		session.Uninit();
	}

	internal void SkipCurrentIsland()
	{
		if (!digOnlyMode && session.CanSkipCurrentIsland)
		{
			session.RequestSkipIsland();
		}
	}

	internal bool ShouldKeepPotFateTarget()
	{
		if (!getConfig().Enabled)
			return false;
		if (digOnlyMode || dig.IsActive)
		{
			return false;
		}
		return session.IsPotFateCombat;
	}
}
