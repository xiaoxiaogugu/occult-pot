using OccultPot.Models;

namespace OccultPot.Core.Dig;

internal sealed class PotDigController
{
    private readonly OccultPotHooks hooks;
    private readonly OccultCrescentPotRunner runner;
    private readonly Queue<string> pendingChat = [];

    internal bool IsActive =>
        runner.Status is not OccultPotStatus.Idle
            and not OccultPotStatus.Completed
            and not OccultPotStatus.Failed;

    internal PotDigController(Action<StopReason>? onStopped, Func<bool>? preferTp, Func<bool>? useDiveTp, Func<float>? tpIntervalSeconds)
    {
        hooks  = new OccultPotHooks(onStopped, preferTp, useDiveTp, tpIntervalSeconds);
        runner = new OccultCrescentPotRunner(hooks);
    }

    internal OccultPotSnapshot? GetSnapshot() =>
        runner.GetSnapshot();

    internal StartResult Start(bool medicineAlreadyUsed = false, PotKind? digKind = null)
    {
        hooks.ConfigureChestTable(digKind);
        var result = runner.Start(medicineAlreadyUsed);
        if (result.Success)
            FlushPendingChat();
        return result;
    }

    internal void Stop(StopReason reason = StopReason.UserRequested) =>
        runner.Stop(reason);

    internal void Tick()
    {
        if (IsActive)
            runner.Tick(hooks.NowSeconds);
    }

    internal void OnChatText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;

        if (IsActive)
            runner.EnqueueChat(text);
        else if (PotHintParser.TryParseChat(text, out _))
            pendingChat.Enqueue(text);
    }

    private void FlushPendingChat()
    {
        while (pendingChat.Count > 0)
            runner.EnqueueChat(pendingChat.Dequeue());
    }
}
