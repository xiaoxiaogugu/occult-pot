using System.Drawing;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using OccultPot.Core;
using OccultPot.Core.Data;
using OccultPot.Core.Dig;
using OccultPot.Localization;
using OmenTools;
using OmenTools.Extensions;
using OmenTools.OmenService;
using static OmenTools.Global.Globals;

namespace OccultPot.Ui;

internal static class StatusSummary
{
    internal static void Draw(OccultPotService service)
    {
        DrawHeaderLine(service);
        DrawSummaryTable(service);
    }

    internal static void DrawCompact(OccultPotService service)
    {
        var spacing = ImGui.GetStyle().ItemSpacing;
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(spacing.X, ScaledVector2(2f).Y));
        DrawHeaderLine(service);
        DrawInfoLine(OccultPotLoc.Get("StatusActivity"), service.ActivityLabel);
        DrawInfoLine(OccultPotLoc.Get("StatusCurrentTarget"), service.CurrentTargetLabel);
        DrawInfoLine(OccultPotLoc.Get("StatusNextTarget"), service.NextTargetLabel);
        ImGui.PopStyleVar();
    }

    private static void DrawInfoLine(string label, string value)
    {
        ImGui.TextDisabled(label);
        ImGui.SameLine();
        ImGui.TextWrapped(value);
    }

    private static void DrawHeaderLine(OccultPotService service)
    {
        ImGui.TextColored(KnownColor.Goldenrod.ToVector4(), OccultPotLoc.Get("WindowTitle"));
        ImGui.SameLine();
        var running = service.IsRunning;
        var runColor = running ? KnownColor.LightGreen : KnownColor.Gray;
        ImGui.TextColored(runColor.ToVector4(), running ? OccultPotLoc.Get("StatusRunning") : OccultPotLoc.Get("StatusStopped"));
    }

    private static void DrawSummaryTable(OccultPotService service)
    {
        if (!ImGui.BeginTable("OccultPotSummary", 2, ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.Resizable | ImGuiTableFlags.BordersInnerV))
            return;

        ImGui.TableSetupColumn("OccultPotSummaryTask", ImGuiTableColumnFlags.WidthStretch, 1.35f);
        ImGui.TableSetupColumn("OccultPotSummaryLocation", ImGuiTableColumnFlags.WidthStretch, 0.65f);
        ImGui.TableNextRow();

        ImGui.TableNextColumn();
        ImGui.TextDisabled(OccultPotLoc.Get("StatusActivity"));
        ImGui.Text(service.ActivityLabel);
        ImGui.TextDisabled(OccultPotLoc.Get("StatusDetail"));
        var status = service.IsDigActive && !service.IsSessionRunning
            ? RuntimeStatusFormatter.FormatDig(service.DigSnapshot)
            : RuntimeStatusFormatter.Format(service.Status);
        ImGui.TextWrapped(status);

        var territory = (ushort)DService.Instance().ClientState.TerritoryType;
        var onIsland = ZoneIds.IsSupportedIsland(territory);
        var locationColor = onIsland ? KnownColor.LightGreen : KnownColor.SandyBrown;

        ImGui.TableNextColumn();
        ImGui.AlignTextToFramePadding();
        ImGui.TextDisabled(OccultPotLoc.Get("StatusLocation"));
        ImGui.TextColored(locationColor.ToVector4(), TerritoryLabel(territory));

        ImGui.EndTable();

        DrawInfoLine(OccultPotLoc.Get("StatusCurrentTarget"), service.CurrentTargetLabel);
        DrawInfoLine(OccultPotLoc.Get("StatusNextTarget"), service.NextTargetLabel);
    }

    private static string TerritoryLabel(ushort territory) =>
        territory switch
        {
            ZoneIds.SouthHorn => OccultPotLoc.Get("TerritorySouth"),
            ZoneIds.NorthHorn => OccultPotLoc.Get("TerritoryNorth"),
            _ => OccultPotLoc.Format("TerritoryOffIsland", territory),
        };
}
