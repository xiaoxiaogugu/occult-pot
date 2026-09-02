using Dalamud.Bindings.ImGui;
using OccultPot.Core.Adapters;
using OccultPot.Core.Data;
using OccultPot.Localization;
using static OmenTools.Global.Globals;

namespace OccultPot.Ui;

internal static class DutySection
{
    private enum DigTravelMode
    {
        GreenWalk,
        DiveTp
    }

    public static void Draw(PluginConfiguration config, Action saveConfig, bool running)
    {
        if (!ImGui.BeginTable("OccultPotDuty", 2, ImGuiTableFlags.Resizable | ImGuiTableFlags.BordersInnerV))
            return;

        ImGui.TableSetupColumn(OccultPotLoc.Get("DutyColOption"), ImGuiTableColumnFlags.WidthFixed, ScaledVector2(148f).X);
        ImGui.TableSetupColumn(OccultPotLoc.Get("DutyColValue"), ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableHeadersRow();

        DrawJobRow(config, saveConfig);
        DrawDigModeRow(config, saveConfig, running);
        DrawToggleRow
        (
            OccultPotLoc.Get("DutyParty"),
            "duty-party",
            ref config.AutoAcceptPartyAtFate,
            OccultPotLoc.Get("NoSwitch"),
            OccultPotLoc.Get("DutyPartyOn"),
            saveConfig
        );

        ImGui.EndTable();
    }

    private static void DrawJobRow(PluginConfiguration config, Action saveConfig)
    {
        DrawComboRow(OccultPotLoc.Get("BaseJob"), "duty-base-job", () => DrawBaseJobCombo(config, saveConfig));
        DrawComboRow(OccultPotLoc.Get("PhantomJob"), "duty-phantom-job", () => DrawPhantomJobCombo(config, saveConfig));
    }

    private static void DrawDigModeRow(PluginConfiguration config, Action saveConfig, bool running)
    {
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        ImGui.AlignTextToFramePadding();
        ImGui.Text(OccultPotLoc.Get("DutyDigMode"));
        ImGui.TableNextColumn();
        ImGui.PushID("duty-dig-mode");
        ImGui.SetNextItemWidth(-1);
        if (running)
            ImGui.BeginDisabled();

        var mode = GetDigMode(config);
        var preview = mode == DigTravelMode.GreenWalk
            ? OccultPotLoc.Get("DutyDigGreen")
            : OccultPotLoc.Get("DutyDigDiveTp");
        if (ImGui.BeginCombo("##dig-mode", preview))
        {
            if (ImGui.Selectable(OccultPotLoc.Get("DutyDigGreen"), mode == DigTravelMode.GreenWalk))
            {
                SetDigMode(config, DigTravelMode.GreenWalk);
                saveConfig();
            }

            if (ImGui.Selectable(OccultPotLoc.Get("DutyDigDiveTp"), mode == DigTravelMode.DiveTp))
            {
                SetDigMode(config, DigTravelMode.DiveTp);
                saveConfig();
            }

            ImGui.EndCombo();
        }

        if (running)
            ImGui.EndDisabled();
        ImGui.PopID();
    }

    private static DigTravelMode GetDigMode(PluginConfiguration config) =>
        !config.PreferTp ? DigTravelMode.GreenWalk : DigTravelMode.DiveTp;

    private static void SetDigMode(PluginConfiguration config, DigTravelMode mode)
    {
        switch (mode)
        {
            case DigTravelMode.GreenWalk:
                config.PreferTp = false;
                break;
            case DigTravelMode.DiveTp:
                config.PreferTp = true;
                config.UseDiveTp = true;
                break;
        }
    }

    private static void DrawComboRow(string label, string id, Action drawCombo)
    {
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        ImGui.AlignTextToFramePadding();
        ImGui.Text(label);
        ImGui.TableNextColumn();
        ImGui.PushID(id);
        ImGui.SetNextItemWidth(-1);
        drawCombo();
        ImGui.PopID();
    }

    private static void DrawToggleRow(
        string label,
        string id,
        ref bool enabled,
        string offLabel,
        string onLabel,
        Action saveConfig)
    {
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        ImGui.AlignTextToFramePadding();
        ImGui.Text(label);
        ImGui.TableNextColumn();
        ImGui.PushID(id);
        ImGui.SetNextItemWidth(-1);
        var preview = enabled ? onLabel : offLabel;
        if (ImGui.BeginCombo("##toggle", preview))
        {
            if (ImGui.Selectable(offLabel, !enabled))
            {
                enabled = false;
                saveConfig();
            }

            if (ImGui.Selectable(onLabel, enabled))
            {
                enabled = true;
                saveConfig();
            }

            ImGui.EndCombo();
        }

        ImGui.PopID();
    }

    private static void DrawBaseJobCombo(PluginConfiguration config, Action saveConfig)
    {
        var preview = config.AutoBaseClassJobID == 0
            ? OccultPotLoc.Get("NoSwitch")
            : JobCatalog.CombatJobs.FirstOrDefault(j => j.ID == config.AutoBaseClassJobID).Name is { Length: > 0 } baseName
                ? baseName
                : OccultPotLoc.Get("NoSwitch");
        if (!ImGui.BeginCombo("##base", preview))
            return;

        if (ImGui.Selectable(OccultPotLoc.Get("NoSwitch"), config.AutoBaseClassJobID == 0))
        {
            config.AutoBaseClassJobID = 0;
            saveConfig();
        }

        if (config.AutoBaseClassJobID == 0)
            ImGui.SetItemDefaultFocus();

        foreach (var (jobID, name) in JobCatalog.CombatJobs)
        {
            var selected = jobID == config.AutoBaseClassJobID;
            if (ImGui.Selectable(name, selected))
            {
                config.AutoBaseClassJobID = jobID;
                saveConfig();
            }

            if (selected)
                ImGui.SetItemDefaultFocus();
        }

        ImGui.EndCombo();
    }

    private static void DrawPhantomJobCombo(PluginConfiguration config, Action saveConfig)
    {
        var preview = config.AutoPhantomJobID < 0
            ? OccultPotLoc.Get("NoSwitch")
            : JobCatalog.PhantomJobs.FirstOrDefault(j => j.ID == config.AutoPhantomJobID).Name is { Length: > 0 } phantomName
                ? phantomName
                : OccultPotLoc.Get("NoSwitch");
        if (!ImGui.BeginCombo("##phantom", preview))
            return;

        if (ImGui.Selectable(OccultPotLoc.Get("NoSwitch"), config.AutoPhantomJobID < 0))
        {
            config.AutoPhantomJobID = -1;
            saveConfig();
        }

        if (config.AutoPhantomJobID < 0)
            ImGui.SetItemDefaultFocus();

        foreach (var (jobID, name) in JobCatalog.PhantomJobs)
        {
            var selected = jobID == config.AutoPhantomJobID;
            if (ImGui.Selectable(name, selected))
            {
                config.AutoPhantomJobID = jobID;
                saveConfig();
            }

            if (selected)
                ImGui.SetItemDefaultFocus();
        }

        ImGui.EndCombo();
    }
}
