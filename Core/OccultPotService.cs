using Dalamud.Game.Chat;
using OccultPot.Core.Adapters;
using OccultPot.Core.Data;
using OccultPot.Core.Dig;
using OccultPot.Core.Session;
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

    internal string CurrentTargetLabel => SessionBriefFormatter.CurrentTarget(this);

    internal string NextTargetLabel => SessionBriefFormatter.NextTarget(this);

    internal bool TryGetCurrentTargetLabel(out string label) => session.TryGetCurrentTargetLabel(out label);

    internal bool TryGetNextTargetLabel(out string label) => session.TryGetNextTargetLabel(out label);

    internal string RouteSummary => session.RouteSummary;

    internal RuntimeStatus TrackerStatus => session.TrackerStatus;

    internal RuntimeStatus TrackerCatalog => session.TrackerCatalog;

    internal PotKind? ActiveKind => session.ActiveKind;

    internal bool IsSessionRunning => session.IsRunning;

    internal bool IsDigActive => dig.IsActive;

    internal bool IsRunning =>
        getConfig().Enabled && (session.IsRunning || digOnlyMode || dig.IsActive);

    internal bool CanSkipIsland =>
        getConfig().Enabled && session.CanSkipCurrentIsland && !digOnlyMode;

    internal bool IsPotFateCombat => session.IsPotFateCombat;

    internal OccultPotSnapshot? DigSnapshot => dig.GetSnapshot();

    internal OccultPotService(Func<PluginConfiguration> getConfig, Action saveConfig)
    {
        this.getConfig  = getConfig;
        this.saveConfig = saveConfig;
        dig     = new PotDigController(OnDigStopped, () => getConfig().PreferTp, () => getConfig().UseDiveTp, () => getConfig().TpIntervalSeconds);
        session = new PotSessionOrchestrator(getConfig, dig);
    }

    private void OnDigStopped(StopReason reason)
    {
        if (!digOnlyMode)
        {
            session.OnDigStopped(reason);
            return;
        }

        digOnlyMode           = false;
        getConfig().Enabled   = false;
        saveConfig();
    }

    internal void Tick()
    {
        var config = getConfig();
        if (!config.Enabled)
            return;

        if (!digOnlyMode)
        {
            session.Tick();
            if (session.Phase == SessionPhase.Failed)
            {
                config.Enabled = false;
                saveConfig();
            }
            return;
        }

        dig.Tick();
        if (dig.IsActive)
            return;

        digOnlyMode     = false;
        config.Enabled  = false;
        saveConfig();
    }

    internal void OnChatMessage(IHandleableChatMessage message) =>
        OnChatText(((IMutableChatMessage)message).Message.TextValue);

    internal void OnChatText(string text) =>
        session.OnChatMessage(text);

    internal void Start()
    {
        if (!OccultPotRuntime.RequireSupported())
            return;

        var config = getConfig();
        config.SyncHomeWorldLock();
        digOnlyMode    = false;
        config.Enabled = true;
        session.Start();
        if (session.Phase == SessionPhase.Failed)
            config.Enabled = false;
        saveConfig();
    }

    internal void StartDigOnly()
    {
        if (!OccultPotRuntime.RequireSupported() ||
            !ZoneIds.IsSupportedIsland((ushort)DService.Instance().ClientState.TerritoryType))
            return;

        session.Stop();
        var config = getConfig();
        digOnlyMode    = true;
        config.Enabled = true;
        saveConfig();
        if (dig.Start().Success)
        {
            ExternalCommands.Echo("[挖箱] 挖箱已启动");
            return;
        }

        digOnlyMode    = false;
        config.Enabled = false;
        saveConfig();
    }

    internal void Stop()
    {
        digOnlyMode = false;
        session.Stop();
        if (dig.IsActive)
            dig.Stop();
        getConfig().Enabled = false;
        saveConfig();
    }

    internal void Uninit() =>
        session.Uninit();

    internal void SkipCurrentIsland()
    {
        if (!digOnlyMode && session.CanSkipCurrentIsland)
            session.RequestSkipIsland();
    }

    internal bool ShouldKeepPotFateTarget()
    {
        if (!getConfig().Enabled || digOnlyMode || dig.IsActive)
            return false;
        return session.IsPotFateCombat;
    }
}
