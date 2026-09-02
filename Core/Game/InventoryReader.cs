using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using OccultPot.Core.Data;
using OmenTools;
using OmenTools.Extensions;
using OmenTools.OmenService;

namespace OccultPot.Core.Game;

internal static class InventoryReader
{
    internal static unsafe int GetElixirCount(uint itemID = PotConstants.ElixirItemID)
    {
        var inventory = InventoryManager.Instance();
        if (inventory == null)
            return 0;

        var nq = inventory->GetInventoryItemCount(itemID, false, true, true, 0);
        var hq = inventory->GetInventoryItemCount(itemID, true, true, true, 0);
        var total = nq + hq;
        total = Math.Max(total, CountInContainer(inventory, InventoryType.KeyItems, itemID));
        total = Math.Max(total, CountInContainer(inventory, InventoryType.HandIn, itemID));
        total = Math.Max(total, CountInContainer(inventory, InventoryType.Inventory1, itemID));
        total = Math.Max(total, CountInContainer(inventory, InventoryType.Inventory2, itemID));
        total = Math.Max(total, CountInContainer(inventory, InventoryType.Inventory3, itemID));
        total = Math.Max(total, CountInContainer(inventory, InventoryType.Inventory4, itemID));
        return total;
    }

    internal static bool HasElixir(uint itemID = PotConstants.ElixirItemID) =>
        GetElixirCount(itemID) > 0;

    internal static unsafe float GetElixirRecastRemaining(uint itemID = PotConstants.ElixirItemID)
    {
        var manager = ActionManager.Instance();
        if (manager == null)
            return 0f;

        static float Remaining(ActionManager* am, uint id)
        {
            var total = am->GetRecastTime(ActionType.Item, id);
            var elapsed = am->GetRecastTimeElapsed(ActionType.Item, id);
            return Math.Max(0f, total - elapsed);
        }

        return Math.Max(Remaining(manager, itemID), Remaining(manager, itemID + 1_000_000));
    }

    internal static unsafe bool TryUseElixir(uint itemID = PotConstants.ElixirItemID)
    {
        if (!PlayerReader.IsAvailable() || PlayerReader.IsBetweenAreas())
            return false;

        var condition = DService.Instance().Condition;
        if (condition.IsCasting)
            return false;

        if (GetElixirRecastRemaining(itemID) > 0.15f)
            return false;

        var agent = AgentInventoryContext.Instance();
        if (agent != null)
        {
            agent->UseItem(itemID, (InventoryType)9999, 0u, 0);
            return true;
        }

        var inventory = InventoryManager.Instance();
        var hq = inventory != null &&
                 inventory->GetInventoryItemCount(itemID, true, true, true, 0) > 0;
        var actionID = hq ? itemID + 1_000_000u : itemID;
        var am = ActionManager.Instance();
        var target = LocalPlayerState.EntityID != 0
            ? LocalPlayerState.EntityID
            : 0xE000_0000ul;
        return am != null &&
               am->UseAction(ActionType.Item, actionID, target, ushort.MaxValue, 0, 0, null);
    }

    private static unsafe int CountInContainer(InventoryManager* inventory, InventoryType type, uint itemID)
    {
        var container = inventory->GetInventoryContainer(type);
        if (container == null || !container->IsLoaded)
            return 0;

        var total = 0;
        for (var i = 0; i < container->Size; i++)
        {
            var slot = container->GetInventorySlot(i);
            if (slot == null || slot->ItemId != itemID)
                continue;

            var qty = slot->Quantity;
            total += qty > 0 ? qty : 1;
        }

        return total;
    }
}
