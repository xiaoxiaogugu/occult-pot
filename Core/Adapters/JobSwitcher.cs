using OccultPot.Core;
using OccultPot.Core.Data;
using OccultPot.Core.Game;
using OmenTools.Info.Game;
using OmenTools.Interop.Game.Models;
using OmenTools.OmenService;

namespace OccultPot.Core.Adapters;

internal static class JobSwitcher
{
    private static DateTime lastTryUTC;

    internal static bool NeedsSwitch(PluginConfiguration config) =>
        config.AutoBaseClassJobID != 0 || config.AutoPhantomJobID >= 0;

    internal static bool IsSatisfied(PluginConfiguration config)
    {
        if (!NeedsSwitch(config))
            return true;

        try
        {
            if (config.AutoBaseClassJobID != 0 && LocalPlayerState.ClassJob != config.AutoBaseClassJobID)
                return false;

            if (config.AutoPhantomJobID < 0)
                return true;

            var current = CrescentSupportJob.GetCurrentSupportJob();
            return current is not null && current.DataID == config.AutoPhantomJobID;
        }
        catch
        {
            return false;
        }
    }

    internal static RuntimeStatus Status(PluginConfiguration config)
    {
        try
        {
            if (config.AutoBaseClassJobID != 0 && LocalPlayerState.ClassJob != config.AutoBaseClassJobID)
                return RuntimeStatus.Of(RuntimeStatusCode.Job_SwitchBase, JobCatalog.BaseJobName(config.AutoBaseClassJobID));

            if (config.AutoPhantomJobID >= 0)
            {
                var current = CrescentSupportJob.GetCurrentSupportJob();
                if (current is null || current.DataID != config.AutoPhantomJobID)
                    return RuntimeStatus.Of(RuntimeStatusCode.Job_SwitchPhantom, JobCatalog.PhantomJobName(config.AutoPhantomJobID));
            }

            return RuntimeStatus.Of(RuntimeStatusCode.Job_Ready);
        }
        catch
        {
            return RuntimeStatus.Of(RuntimeStatusCode.Job_Switching);
        }
    }

    internal static void TrySwitchToBaseOnStart(PluginConfiguration config)
    {
        if (config.AutoBaseClassJobID == 0)
            return;
        if (!PlayerReader.CanSwitchJob())
            return;
        if ((DateTime.UtcNow - lastTryUTC).TotalSeconds < 2)
            return;

        try
        {
            if (LocalPlayerState.ClassJob != config.AutoBaseClassJobID)
            {
                LocalPlayerState.SwitchGearset(config.AutoBaseClassJobID);
                lastTryUTC = DateTime.UtcNow;
            }
        }
        catch
        {
        }
    }

    internal static bool IsOnBaseJob(PluginConfiguration config) =>
        config.AutoBaseClassJobID == 0 || LocalPlayerState.ClassJob == config.AutoBaseClassJobID;

    internal static void TrySwitch(PluginConfiguration config)
    {
        if (!NeedsSwitch(config))
            return;
        if (!PlayerReader.CanSwitchJob())
            return;
        if ((DateTime.UtcNow - lastTryUTC).TotalSeconds < 2)
            return;

        try
        {
            if (config.AutoBaseClassJobID != 0 && LocalPlayerState.ClassJob != config.AutoBaseClassJobID)
            {
                LocalPlayerState.SwitchGearset(config.AutoBaseClassJobID);
                lastTryUTC = DateTime.UtcNow;
                return;
            }

            if (config.AutoPhantomJobID < 0)
                return;

            var target = CrescentSupportJob.AllJobs.FirstOrDefault(j => j.DataID == config.AutoPhantomJobID);
            if (target is null || target.IsThisJob())
                return;

            target.ChangeTo();
            lastTryUTC = DateTime.UtcNow;
        }
        catch
        {
        }
    }
}
