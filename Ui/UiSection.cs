using Dalamud.Bindings.ImGui;

namespace OccultPot.Ui;

internal static class UiSection
{
    private static bool firstGroup;

    internal static void BeginScope() =>
        firstGroup = true;

    internal static bool Begin(string id, string title, string subtitle, ref bool expanded, Action saveConfig)
    {
        if (!firstGroup)
            ImGui.Spacing();
        else
            firstGroup = false;

        ImGui.SetNextItemOpen(expanded, ImGuiCond.Once);
        var open = ImGui.CollapsingHeader($"◇ {title}  —  {subtitle}##{id}");
        if (open != expanded)
        {
            expanded = open;
            saveConfig();
        }

        if (!open)
            return false;

        ImGui.Indent(8f);
        return true;
    }

    internal static void End() =>
        ImGui.Unindent(8f);
}
