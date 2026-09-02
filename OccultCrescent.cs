using Dalamud.Game.Chat;
using Dalamud.Game.Command;
using Dalamud.Game.Gui.Toast;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using OccultPot.Core;
using OccultPot.Core.Game;
using OccultPot.Core.Dig;
using OccultPot.Ui;
using OmenTools;
using OmenTools.Dalamud;
using OmenTools.OmenService;

namespace OccultPot;

internal sealed class OccultCrescent : IDisposable
{
    internal const string CommandName = "/ocpot";
    internal const string Title = "新月岛撒娇罐";
    internal const string Description = "多区循环找罐、打罐、挖箱";

    internal static PluginConfiguration Config { get; private set; } = new();

    private readonly IDalamudPluginInterface pluginInterface;
    private readonly IPluginLog log;
    private readonly ICommandManager commands;
    private readonly OccultPotService service;
    private readonly MainWindow mainWindow;
    private readonly PotTalkHooks talkHooks;

    internal OccultCrescent(
        IDalamudPluginInterface pluginInterface,
        ICommandManager commands,
        IPluginLog log)
    {
        this.pluginInterface = pluginInterface;
        this.log = log;
        this.commands = commands;

        Config = pluginInterface.GetPluginConfig() as PluginConfiguration ?? new PluginConfiguration();
        if (MigrateConfig(Config))
            pluginInterface.SavePluginConfig(Config);

        service = new OccultPotService(() => Config, SaveConfig);
        mainWindow = new MainWindow(Config, SaveConfig, service);
        WindowManager.Instance().AddWindow(mainWindow);

        pluginInterface.UiBuilder.OpenMainUi += OpenMainUI;
        pluginInterface.UiBuilder.OpenConfigUi += OpenMainUI;

        commands.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "打开撒娇罐。/ocpot start | stop | skip | dig",
        });

        FrameworkManager.Instance().Reg(OnTargetTick, 100);
        FrameworkManager.Instance().Reg(OnServiceTick, 200);

        DService.Instance().Chat.ChatMessage += OnChatMessage;
        DService.Instance().Toast.Toast += OnToast;

        talkHooks = new PotTalkHooks(text => service.OnChatText(text));
        talkHooks.Enable();

        log.Information("[OccultPot] Loaded. Use {Command}.", CommandName);
    }

    private void OnChatMessage(IHandleableChatMessage message) =>
        service.OnChatMessage(message);

    private void OnToast(ref SeString message, ref ToastOptions options, ref bool isHandled) =>
        service.OnChatText(message.TextValue);

    private void OnTargetTick(IFramework framework)
    {
        if (Config.Enabled)
            PotFateTargeter.Tick(service.ShouldKeepPotFateTarget());
        else
            PotFateTargeter.Idle();
    }

    private void OnServiceTick(IFramework framework)
    {
        if (Config.Enabled)
            service.Tick();
    }

    private void OnCommand(string command, string args)
    {
        var trimmed = args.Trim();
        if (trimmed.Equals("start", StringComparison.OrdinalIgnoreCase))
        {
            service.Start();
            return;
        }

        if (trimmed.Equals("stop", StringComparison.OrdinalIgnoreCase))
        {
            service.Stop();
            return;
        }

        if (trimmed.Equals("skip", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("next", StringComparison.OrdinalIgnoreCase))
        {
            service.SkipCurrentIsland();
            return;
        }

        if (trimmed.Equals("dig", StringComparison.OrdinalIgnoreCase))
        {
            service.StartDigOnly();
            return;
        }

        OpenMainUI();
    }

    private void OpenMainUI() => mainWindow.IsOpen = true;

    private void SaveConfig() => pluginInterface.SavePluginConfig(Config);

    private static bool MigrateConfig(PluginConfiguration config)
    {
        var migrated = false;

        if (config.Version < 17)
        {
            if (config.WindowWidth >= 600f || config.WindowHeight >= 620f)
            {
                config.WindowWidth = 520f;
                config.WindowHeight = 460f;
            }

            config.Version = 17;
            migrated = true;
        }

        if (config.Version < 18)
        {
            if (config.WindowWidth > 0f && config.WindowWidth <= 520f)
                config.WindowWidth = 560f;

            config.Version = 18;
            migrated = true;
        }

        return migrated;
    }

    public void Dispose()
    {
        talkHooks.Dispose();

        try
        {
            DService.Instance().Toast.Toast -= OnToast;
        }
        catch (Exception ex)
        {
            DLog.Error("[OccultPot] Toast 取消订阅失败", ex);
        }

        try
        {
            DService.Instance().Chat.ChatMessage -= OnChatMessage;
        }
        catch (Exception ex)
        {
            DLog.Error("[OccultPot] Chat 取消订阅失败", ex);
        }

        FrameworkManager.Instance().Unreg(OnTargetTick, OnServiceTick);

        commands.RemoveHandler(CommandName);

        pluginInterface.UiBuilder.OpenMainUi -= OpenMainUI;
        pluginInterface.UiBuilder.OpenConfigUi -= OpenMainUI;

        WindowManager.Instance().RemoveWindow(mainWindow);

        service.Uninit();
    }
}
