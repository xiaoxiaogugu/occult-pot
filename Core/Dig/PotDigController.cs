using System;
using System.Collections.Generic;
using OccultPot.Models;

namespace OccultPot.Core.Dig;

internal sealed class PotDigController
{
	private readonly OccultPotHooks hooks;

	private readonly OccultCrescentPotRunner runner;

	private readonly Queue<string> pendingChat = new Queue<string>();

	internal bool IsActive
	{
		get
		{
			OccultPotStatus status = runner.Status;
			bool flag = ((status == OccultPotStatus.Idle || (uint)(status - 5) <= 1u) ? true : false);
			return !flag;
		}
	}

	internal PotDigController(Action<StopReason>? onStopped, Func<bool>? preferTp, Func<bool>? useDiveTp, Func<float>? tpIntervalSeconds)
	{
		hooks = new OccultPotHooks(onStopped, preferTp, useDiveTp, tpIntervalSeconds);
		runner = new OccultCrescentPotRunner(hooks);
	}

	internal OccultPotSnapshot? GetSnapshot()
	{
		return runner.GetSnapshot();
	}

	internal StartResult Start(bool medicineAlreadyUsed = false, PotKind? digKind = null)
	{
		hooks.ConfigureChestTable(digKind);
		StartResult result = runner.Start(medicineAlreadyUsed);
		if (result.Success)
		{
			FlushPendingChat();
		}
		return result;
	}

	internal void Stop(StopReason reason = StopReason.UserRequested)
	{
		runner.Stop(reason);
	}

	internal void Tick()
	{
		if (IsActive)
		{
			runner.Tick(hooks.NowSeconds);
		}
	}

	internal void OnChatText(string text)
	{
		if (!string.IsNullOrWhiteSpace(text))
		{
			PotChatEvent evt;
			if (IsActive)
			{
				runner.EnqueueChat(text);
			}
			else if (PotHintParser.TryParseChat(text, out evt))
			{
				pendingChat.Enqueue(text);
			}
		}
	}

	private void FlushPendingChat()
	{
		while (pendingChat.Count > 0)
		{
			runner.EnqueueChat(pendingChat.Dequeue());
		}
	}
}
