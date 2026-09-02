using System.Drawing;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Windowing;
using OccultPot.Core;
using OccultPot.Localization;
using OmenTools.Extensions;

namespace OccultPot.Ui;

internal sealed class WindowTitleButtons
{
    private readonly OccultPotService service;
    private readonly Action toggleSimplified;
    private readonly Func<bool> isSimplified;

    private readonly TitleBarButton startButton;
    private readonly TitleBarButton stopButton;
    private readonly TitleBarButton skipButton;
    private readonly TitleBarButton digButton;
    private readonly TitleBarButton modeButton;

    internal WindowTitleButtons(OccultPotService service, Action toggleSimplified, Func<bool> isSimplified)
    {
        this.service = service;
        this.toggleSimplified = toggleSimplified;
        this.isSimplified = isSimplified;

        startButton = CreateButton(FontAwesomeIcon.Play, OccultPotLoc.Get("TooltipStart"), 60, OnStart);
        stopButton = CreateButton(FontAwesomeIcon.Stop, OccultPotLoc.Get("TooltipStop"), 70, OnStop);
        skipButton = CreateButton(FontAwesomeIcon.Forward, OccultPotLoc.Get("TooltipSkipIsland"), 80, OnSkip);
        digButton = CreateButton(FontAwesomeIcon.Hammer, OccultPotLoc.Get("TooltipDigOnly"), 90, OnDig);
        modeButton = CreateButton(FontAwesomeIcon.WindowMinimize, OccultPotLoc.Get("TooltipSimpleMode"), 100, toggleSimplified);
    }

    internal IReadOnlyList<TitleBarButton> Buttons =>
    [
        modeButton,
        startButton,
        stopButton,
        skipButton,
        digButton,
    ];

    internal void Sync()
    {
        var cnSupported = OccultPotRuntime.IsSupported;
        var running = service.IsRunning;

        startButton.IconColor = cnSupported && !running ? null : Dimmed();
        stopButton.IconColor = running ? null : Dimmed();
        skipButton.IconColor = service.CanSkipIsland ? null : Dimmed();
        digButton.IconColor = cnSupported && !running ? null : Dimmed();

        modeButton.Icon = isSimplified() ? FontAwesomeIcon.WindowMaximize : FontAwesomeIcon.WindowMinimize;
        modeButton.ShowTooltip = () =>
            ImGui.SetTooltip(isSimplified()
                ? OccultPotLoc.Get("TooltipFullMode")
                : OccultPotLoc.Get("TooltipSimpleMode"));
    }

    private void OnStart()
    {
        if (!OccultPotRuntime.IsSupported || service.IsRunning)
            return;

        service.Start();
    }

    private void OnStop()
    {
        if (!service.IsRunning)
            return;

        service.Stop();
    }

    private void OnSkip()
    {
        if (!service.CanSkipIsland)
            return;

        service.SkipCurrentIsland();
    }

    private void OnDig()
    {
        if (!OccultPotRuntime.IsSupported || service.IsRunning)
            return;

        service.StartDigOnly();
    }

    private static TitleBarButton CreateButton(FontAwesomeIcon icon, string tooltip, int priority, Action click) =>
        new()
        {
            Icon = icon,
            IconOffset = new Vector2(1f, 1f),
            Priority = priority,
            ShowTooltip = () => ImGui.SetTooltip(tooltip),
            Click = _ => click(),
        };

    private static Vector4 Dimmed() =>
        KnownColor.Gray.ToVector4() with { W = 0.45f };
}
