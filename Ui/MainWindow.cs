using System.Drawing;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using OccultPot.Core;
using OccultPot.Core.Data;
using OccultPot.Core.Session;
using OccultPot.Localization;
using OccultPot.Models;
using OmenTools;
using OmenTools.Extensions;
using OmenTools.Global;
using OmenTools.OmenService;
using static OmenTools.Global.Globals;

namespace OccultPot.Ui;

internal sealed class MainWindow : Window
{
    private const string WindowID = "OccultPotMainWindow";

    private static readonly Vector2 DefaultFullWindowSize = new(560f, 460f);
    private static readonly Vector2 MinFullWindowSize = new(480f, 380f);
    private static readonly Vector2 DefaultSimplifiedWindowSize = new(360f, 0f);

    private readonly PluginConfiguration config;
    private readonly Action saveConfig;
    private readonly OccultPotService service;
    private readonly WindowTitleButtons titleButtons;
    private int sizeRestoreFrames;

    public MainWindow(PluginConfiguration config, Action saveConfig, OccultPotService service)
        : base(OccultPotLoc.Get("WindowTitle"))
    {
        this.config = config;
        this.saveConfig = saveConfig;
        this.service = service;
        Flags |= ImGuiWindowFlags.NoCollapse;

        titleButtons = new WindowTitleButtons(service, ToggleSimplifiedMode, () => config.SimplifiedUI);
        TitleBarButtons.AddRange(titleButtons.Buttons);

        SyncWindowTitle();
        ApplySavedSize();
    }

    public override void PreDraw()
    {
        titleButtons.Sync();
        SyncWindowFlags();
        SyncWindowTitle();
        if (config.SimplifiedUI)
        {
            var minWidth = config.SimplifiedWindowWidth > 0f
                ? config.SimplifiedWindowWidth
                : DefaultSimplifiedWindowSize.X;
            ImGui.SetNextWindowSizeConstraints(
                new Vector2(minWidth, 0f),
                new Vector2(float.MaxValue, float.MaxValue));
        }
        else
        {
            ImGui.SetNextWindowSizeConstraints(
                MinFullWindowSize,
                new Vector2(float.MaxValue, float.MaxValue));
        }

        base.PreDraw();
    }

    public override void Draw()
    {
        config.SyncHomeWorldLock();

        if (!OccultPotRuntime.IsSupported)
        {
            ImGui.TextColored(KnownColor.Orange.ToVector4(), OccultPotLoc.Get("CnOnlyBanner"));
            ImGui.Spacing();
        }

        if (config.SimplifiedUI)
        {
            StatusSummary.DrawCompact(service);
            return;
        }

        DrawFull();
    }

    public override void PostDraw()
    {
        base.PostDraw();

        var size = ImGui.GetWindowSize();
        if (size.X <= 0f || size.Y <= 0f)
            return;

        if (sizeRestoreFrames > 0)
        {
            sizeRestoreFrames--;
            if (sizeRestoreFrames == 0)
                SizeCondition = ImGuiCond.FirstUseEver;
            return;
        }

        if (config.SimplifiedUI)
        {
            if (MathF.Abs(config.SimplifiedWindowWidth - size.X) < 0.5f)
                return;

            config.SimplifiedWindowWidth = size.X;
            saveConfig();
            return;
        }

        var savedSize = GetSavedSize();
        if (MathF.Abs(savedSize.X - size.X) < 0.5f && MathF.Abs(savedSize.Y - size.Y) < 0.5f)
            return;

        StoreSize(size);
        saveConfig();
    }

    private void DrawFull()
    {
        StatusSummary.Draw(service);
        ImGui.Separator();

        var avail = ImGui.GetContentRegionAvail();
        var footerHeight = ActionFooter.ReservedHeight();
        var childHeight = MathF.Max(
            avail.Y - footerHeight,
            ScaledVector2(96f).Y);

        if (ImGui.BeginChild("OccultPotSettingsScroll", new Vector2(0f, childHeight)))
        {
            DrawSettingsTab();
            ImGui.EndChild();
        }

        ActionFooter.Draw(this, service);
    }

    private void DrawSettingsTab()
    {
        UiSection.BeginScope();

        if (UiSection.Begin("requirements", OccultPotLoc.Get("RequirementsHeader"), OccultPotLoc.Get("RequirementsSubtitle"), ref config.SectionRequirementsExpanded, saveConfig))
        {
            ImGui.TextWrapped(OccultPotLoc.Get("RequirementsBody"));
            UiSection.End();
        }

        if (UiSection.Begin("duty", OccultPotLoc.Get("SettingsSectionDuty"), OccultPotLoc.Get("SettingsSubtitleDuty"), ref config.SectionDutyExpanded, saveConfig))
        {
            DutySection.Draw(config, saveConfig, service.IsRunning);
            UiSection.End();
        }

        if (UiSection.Begin("route", OccultPotLoc.Get("SettingsSectionRoute"), OccultPotLoc.Get("SettingsSubtitleRoute"), ref config.SectionRouteExpanded, saveConfig))
        {
            RouteSection.Draw(config, saveConfig, service.IsRunning);
            UiSection.End();
        }
    }

    private void ToggleSimplifiedMode()
    {
        var currentSize = ImGui.GetWindowSize();
        if (currentSize.X > 0f && currentSize.Y > 0f)
            StoreSize(currentSize);

        config.SimplifiedUI = !config.SimplifiedUI;
        SyncWindowFlags();
        if (config.SimplifiedUI)
        {
            var width = config.SimplifiedWindowWidth > 0f
                ? config.SimplifiedWindowWidth
                : DefaultSimplifiedWindowSize.X;
            Size = new Vector2(width, 0f);
        }
        else
        {
            var targetSize = GetSavedSize();
            if (targetSize.X <= 0f || targetSize.Y <= 0f)
                targetSize = currentSize.X > 0f && currentSize.Y > 0f ? currentSize : DefaultFullWindowSize;
            Size = targetSize;
        }

        SizeCondition = ImGuiCond.Always;
        sizeRestoreFrames = 2;
        saveConfig();
    }

    private void SyncWindowFlags() =>
        Flags = ImGuiWindowFlags.NoCollapse
            | (config.SimplifiedUI ? ImGuiWindowFlags.AlwaysAutoResize : ImGuiWindowFlags.None);

    private void SyncWindowTitle() =>
        WindowName = config.SimplifiedUI
            ? $"###{WindowID}"
            : $"{OccultPotLoc.Get("WindowTitle")}###{WindowID}";

    private void ApplySavedSize()
    {
        SyncWindowFlags();
        if (config.SimplifiedUI)
        {
            var width = config.SimplifiedWindowWidth > 0f
                ? config.SimplifiedWindowWidth
                : DefaultSimplifiedWindowSize.X;
            Size = new Vector2(width, 0f);
            SizeCondition = ImGuiCond.FirstUseEver;
            return;
        }

        var savedSize = GetSavedSize();
        if (savedSize.X > 0f && savedSize.Y > 0f)
        {
            if (savedSize.X < MinFullWindowSize.X || savedSize.Y < MinFullWindowSize.Y)
                savedSize = DefaultFullWindowSize;
            Size = savedSize;
            SizeCondition = ImGuiCond.Always;
            sizeRestoreFrames = 1;
            return;
        }

        Size = config.SimplifiedUI ? DefaultSimplifiedWindowSize : DefaultFullWindowSize;
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    private Vector2 GetSavedSize() =>
        config.SimplifiedUI
            ? new Vector2(config.SimplifiedWindowWidth, 0f)
            : new Vector2(config.WindowWidth, config.WindowHeight);

    private void StoreSize(Vector2 size)
    {
        if (config.SimplifiedUI)
        {
            config.SimplifiedWindowWidth = size.X;
            return;
        }

        config.WindowWidth = size.X;
        config.WindowHeight = size.Y;
    }
}

internal static class RouteSection
{
    public static void Draw(PluginConfiguration config, Action saveConfig, bool running)
    {
        var homeDC = CnWorldCatalog.HomeDCKind;
        ImGui.TextDisabled(OccultPotLoc.Format
        (
            "RouteHomeWorld",
            CnWorldCatalog.WorldName(CnWorldCatalog.HomeWorldID),
            homeDC?.Display() ?? OccultPotLoc.Get("UnknownDC")
        ));
        ImGui.TextDisabled(OccultPotLoc.Format("RouteCurrentWorld", CnWorldCatalog.WorldName(CnWorldCatalog.CurrentWorldID)));
        ImGui.Spacing();

        if (!ImGui.BeginTable("OccultPotRoute", 3, ImGuiTableFlags.Resizable | ImGuiTableFlags.BordersInnerV))
            return;

        ImGui.TableSetupColumn(OccultPotLoc.Get("RouteColEnable"), ImGuiTableColumnFlags.WidthFixed, ScaledVector2(36f).X);
        ImGui.TableSetupColumn(OccultPotLoc.Get("RouteColDC"), ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn(OccultPotLoc.Get("RouteColWorld"), ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableHeadersRow();

        DrawRow(CnDataCenterKind.Chocobo, config, saveConfig, running, homeDC);
        DrawRow(CnDataCenterKind.Moogle, config, saveConfig, running, homeDC);
        DrawRow(CnDataCenterKind.Cat, config, saveConfig, running, homeDC);
        DrawRow(CnDataCenterKind.Atomos, config, saveConfig, running, homeDC);

        ImGui.EndTable();
    }

    private static void DrawRow(
        CnDataCenterKind kind,
        PluginConfiguration config,
        Action saveConfig,
        bool running,
        CnDataCenterKind? homeDC)
    {
        var route = config.GetRoute(kind);
        var label = CnWorldCatalog.DCDisplayName(kind);
        var locked = homeDC == kind;
        var worldID = locked
            ? CnWorldCatalog.HomeWorldID
            : CnWorldCatalog.ResolveWorldID(kind, route.DestinationWorldID);
        var worldName = CnWorldCatalog.WorldName(worldID);

        ImGui.TableNextRow();

        ImGui.TableNextColumn();
        if (running)
            ImGui.BeginDisabled();
        if (ImGui.Checkbox($"##en_{kind}", ref route.Enabled))
            saveConfig();
        if (running)
            ImGui.EndDisabled();

        ImGui.TableNextColumn();
        ImGui.Text(label);

        ImGui.TableNextColumn();
        if (running)
            ImGui.BeginDisabled();

        if (locked)
        {
            ImGui.BeginDisabled();
            if (ImGui.BeginCombo($"##world_{kind}", worldName))
                ImGui.EndCombo();
            ImGui.EndDisabled();
            ImGui.SameLine();
            ImGui.TextColored(KnownColor.Goldenrod.ToVector4(), OccultPotLoc.Get("RouteHomeLocked"));
        }
        else
        {
            ImGui.SetNextItemWidth(-1);
            if (ImGui.BeginCombo($"##world_{kind}", worldName))
            {
                foreach (var world in CnWorldCatalog.WorldsFor(kind))
                {
                    var selected = world.RowId == worldID;
                    if (ImGui.Selectable(world.Name.ToString(), selected))
                    {
                        route.DestinationWorldID = world.RowId;
                        saveConfig();
                    }

                    if (selected)
                        ImGui.SetItemDefaultFocus();
                }

                ImGui.EndCombo();
            }
        }

        if (running)
            ImGui.EndDisabled();
    }
}
