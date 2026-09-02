using Lumina.Excel.Sheets;
using OmenTools.Info.Game;
using OmenTools.Interop.Game.Lumina;

namespace OccultPot.Core.Data;

internal static class JobCatalog
{
    private static readonly HashSet<uint> baseClasses =
    [
        1, 2, 3, 4, 5, 6, 7, 25, 26,
    ];

    private static IReadOnlyList<(uint ID, string Name)>? combatJobs;
    private static IReadOnlyList<(int ID, string Name)>? phantomJobs;

    internal static IReadOnlyList<(uint ID, string Name)> CombatJobs =>
        combatJobs ??= LuminaGetter.Get<ClassJob>()
            .Where(job => job.RowId != 0 && job.Role != 0 && !baseClasses.Contains(job.RowId))
            .OrderBy(job => job.Role)
            .ThenBy(job => job.UIPriority)
            .Select(job => (job.RowId, job.Name.ToString()))
            .ToList();

    internal static IReadOnlyList<(int ID, string Name)> PhantomJobs =>
        phantomJobs ??= CrescentSupportJob.AllJobs
            .Select(job => ((int)job.DataID, job.Name))
            .ToList();

    internal static string BaseJobName(uint id) =>
        CombatJobs.FirstOrDefault(j => j.ID == id).Name is { Length: > 0 } name ? name : "不切换";

    internal static string PhantomJobName(int id) =>
        id < 0
            ? "不切换"
            : PhantomJobs.FirstOrDefault(j => j.ID == id).Name is { Length: > 0 } name
                ? name
                : "不切换";
}
