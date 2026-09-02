using Dalamud.Bindings.ImGui;
using OccultPot.Core;
using OccultPot.Localization;

namespace OccultPot.Ui;

internal static class ActionFooter
{
    internal static void Draw(MainWindow window, OccultPotService service)
    {
        ImGui.Separator();
        ImGui.TextDisabled(service.IsRunning ? OccultPotLoc.Get("FooterRunning") : OccultPotLoc.Get("FooterStopped"));
        ImGui.SameLine();

        var cnSupported = OccultPotRuntime.IsSupported;
        var running = service.IsRunning;

        if (running)
        {
            if (ImGui.Button(OccultPotLoc.Get("FooterStop")))
                service.Stop();
        }
        else
        {
            ImGui.BeginDisabled(!cnSupported);
            if (ImGui.Button(OccultPotLoc.Get("FooterStart")))
                service.Start();
            ImGui.EndDisabled();
        }

        ImGui.SameLine();
        if (ImGui.Button(OccultPotLoc.Get("CloseWindow")))
            window.IsOpen = false;
    }

    internal static float ReservedHeight() =>
        ImGui.GetFrameHeightWithSpacing() + ImGui.GetStyle().ItemSpacing.Y;
}
