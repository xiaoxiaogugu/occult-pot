using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using OccultPot.Core.Game;
using OmenTools.Interop.Game.AddonEvent;
using OmenTools.OmenService;

namespace OccultPot.Core.Adapters;

internal static unsafe class PartyInviteActions
{
    private static uint inviteTime;
    private static string inviterName = string.Empty;
    private static bool acceptSent;
    private static long acceptAt;
    private static bool leaveSent;

    internal static void Reset()
    {
        inviteTime = 0;
        inviterName = string.Empty;
        acceptSent = false;
        acceptAt = 0;
        leaveSent = false;
    }

    internal static void TickAccept()
    {
        if (PlayerReader.IsBetweenAreas() || LocalPlayerState.IsInAnyParty)
        {
            ClearPendingAccept();
            return;
        }

        var proxy = InfoProxyPartyInvite.Instance();
        var name = proxy == null ? string.Empty : proxy->InviterName.ToString();
        if (proxy == null || string.IsNullOrWhiteSpace(name))
        {
            ClearPendingAccept();
            TryClickInviteYesno();
            return;
        }

        if (inviteTime != proxy->InviteTime ||
            !string.Equals(inviterName, name, StringComparison.Ordinal))
        {
            inviteTime = proxy->InviteTime;
            inviterName = name;
            acceptSent = false;
            acceptAt = Environment.TickCount64 + 400;
            return;
        }

        if (acceptSent || Environment.TickCount64 < acceptAt)
            return;

        if (proxy->RespondToInvitation(name, true))
        {
            acceptSent = true;
            acceptAt = 0;
            var agent = AgentPartyInvite.Instance();
            if (agent != null)
                agent->Hide();
            return;
        }

        if (TryClickInviteYesno())
        {
            acceptSent = true;
            acceptAt = 0;
            return;
        }

        acceptAt = Environment.TickCount64 + 400;
    }

    internal static void LeaveOnce()
    {
        if (leaveSent)
            return;

        if (!LocalPlayerState.IsInAnyParty)
        {
            leaveSent = true;
            return;
        }

        if (PlayerReader.IsBusy() || PlayerReader.IsBetweenAreas())
            return;

        ExternalCommands.Run("/partyleave");
        leaveSent = true;
    }

    private static void ClearPendingAccept()
    {
        inviteTime = 0;
        inviterName = string.Empty;
        acceptSent = false;
        acceptAt = 0;
    }

    private static bool TryClickInviteYesno() =>
        AddonSelectYesnoEvent.ClickYes("加入队伍") ||
        AddonSelectYesnoEvent.ClickYes("加入小队");
}
