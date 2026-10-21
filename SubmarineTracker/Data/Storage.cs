using FFXIVClientStructs.FFXIV.Client.Game;
using Lumina.Excel.Sheets;

namespace SubmarineTracker.Data;

public static class Storage
{
    public static int InventorySlotsFree = -1;

    public static bool HasStorageData()
    {
        if (Plugin.AllaganToolsConsumer.IsAvailable)
            return true;

        return Plugin.DatabaseCache.HasStorage();
    }

    public static bool TryGetStorageCount(uint item, FreeCompany fc, out uint storageCount)
    {
        storageCount = 0;

        if (Plugin.AllaganToolsConsumer.IsAvailable)
        {
            storageCount = Plugin.AllaganToolsConsumer.GetCount(item, fc.ContentId);
            return storageCount != 0 && storageCount != uint.MaxValue;
        }

        if (!Plugin.DatabaseCache.TryGetStorage(fc.FreeCompanyId, out var storageData))
            return false;

        return storageData.Items.TryGetValue(item, out storageCount);
    }

    public static unsafe void GetFreeSlotCount()
    {
        var manager = InventoryManager.Instance();
        InventorySlotsFree = manager == null ? -1 : (int)manager->GetEmptySlotsInBag();
    }

    public static int InventoryCount(Items item)
        => InventoryCount((uint)item);

    public static unsafe int InventoryCount(uint item)
    {
        var manager = InventoryManager.Instance();
        return manager == null ? -1 : manager->GetInventoryItemCount(item, false, false);
    }

    public static Dictionary<uint, uint> GenerateStorageData()
    {
        var tanks = InventoryCount(Items.Tanks);
        var kits = InventoryCount(Items.Kits);

        return new Dictionary<uint, uint>
        {
            { (uint)Items.Tanks, (uint)tanks },
            { (uint)Items.Kits, (uint)kits },
        };
    }

    public static (int Voyages, int Repairs) CheckLeftovers(IEnumerable<Submarine> subs)
    {
        var tanks = InventoryCount(Items.Tanks);
        var kits = InventoryCount(Items.Kits);

        if (tanks == -1 || kits == -1)
        {
            Plugin.Log.Warning("InventoryManager was null");
            return (-1, -1);
        }

        var requiredKits = 0;
        var requiredTanks = 0;
        foreach (var sub in subs)
        {
            requiredKits += sub.Build.RepairCosts;
            requiredTanks += Voyage.ToExplorationArray(sub.Points).Sum(p => p.CeruleumTankReq);
        }

        if (requiredTanks == 0 || requiredKits == 0)
            return (-1, -1);

        return (tanks / requiredTanks, kits / requiredKits);
    }

    public static (long Voyages, long Repairs) CheckLeftoversFromStorage(IEnumerable<Submarine> subs, uint tanks, uint kits)
    {
        var requiredKits = 0;
        var requiredTanks = 0;
        foreach (var sub in subs)
        {
            requiredKits += sub.Build.RepairCosts;
            requiredTanks += Voyage.ToExplorationArray(sub.Points).Sum(p => p.CeruleumTankReq);
        }

        if (requiredTanks == 0 || requiredKits == 0)
            return (-1, -1);

        return (tanks / requiredTanks, kits / requiredKits);
    }
}
