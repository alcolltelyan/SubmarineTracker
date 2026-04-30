using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using FFXIVClientStructs.FFXIV.Client.Network;
using SubmarineTracker.Data;

namespace SubmarineTracker.Manager;

public unsafe class HookManager
{
    private static readonly HashSet<string> SentLootProcessed = [];
    private Hook<PacketDispatcher.Delegates.HandleEventYieldPacket> PacketHandlerHook { get; init; }

    public HookManager()
    {
        PacketHandlerHook = Plugin.Hook.HookFromAddress<PacketDispatcher.Delegates.HandleEventYieldPacket>(PacketDispatcher.MemberFunctionPointers.HandleEventYieldPacket, PacketReceiver);
        PacketHandlerHook.Enable();
    }

    public void Dispose()
    {
        PacketHandlerHook.Dispose();
    }

    private void PacketReceiver(EventId id, short scene, byte responseId, int* intParams, byte argCount)
    {
        PacketHandlerHook.Original(id, scene, responseId, intParams, argCount);

        // We only care about voyage results
        if (id != 721343)
            return;

        try
        {
            var instance = HousingManager.Instance();
            if (instance == null || instance->WorkshopTerritory == null)
                return;

            var current = instance->WorkshopTerritory->Submersible.DataPointers[4];
            if (current.Value == null)
                return;

            var sub = current.Value;

            var fcId = Plugin.GetFCId;
            var register = sub->RegisterTime;
            var returnTime = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds(); // sub->ReturnTime is 0 at this point

            var data = sub->GatheredData;
            if (data[0].ItemIdPrimary == 0)
                return;

            var validSectors = data.Filter(val => val.Point > 0);
            var expGathered = (uint) validSectors.Sum(val => val.ExpGained);
            var buildRank = Sectors.CalculateOriginalRank(sub->RankId, sub->CurrentExp, expGathered);
            var build = new Build.SubmarineBuild(buildRank, sub->HullId, sub->SternId, sub->BowId, sub->BridgeId);

            var lootList = new List<Loot>();
            foreach (var val in validSectors)
                lootList.Add(new Loot(build, val) {FreeCompanyId = fcId, Register = register, Return = returnTime});

            if (Plugin.Configuration.WebhookLootProcessed)
                Task.Run(() => SendLootProcessedWebhook(lootList, fcId, register, returnTime));

            Task.Run(() =>
            {
                try
                {
                    foreach (var loot in lootList)
                        Plugin.DatabaseCache.Database.InsertLootEntry(loot);
                }
                catch (Exception ex)
                {
                    Plugin.Log.Error(ex, "Error while upsert of loot entry");
                }
            });
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Error in packet receiver");
        }
    }

    private static void SendLootProcessedWebhook(List<Loot> lootList, ulong fcId, uint register, uint returnTime)
    {
        try
        {
            if (!Plugin.ClientState.IsLoggedIn)
                return;

            if (Plugin.Configuration.WebhookOfflineMode)
                return;

            if (!Plugin.Configuration.WebhookUrl.StartsWith("https://"))
                return;

            // Prevent that multibox user send multiple webhook triggers
            using var mutex = new Mutex(false, "Global\\SubmarineTrackerMutex");
            if (!mutex.WaitOne(0, false))
                return;

            if (lootList.Count == 0)
                return;

            if (!SentLootProcessed.Add($"LootProcessed{fcId}{register}{returnTime}"))
                return;

            var profileName = Plugin.Configuration.WebhookLootProcessedProfile;
            if (string.IsNullOrEmpty(profileName) || !Plugin.Configuration.CustomLootProfiles.TryGetValue(profileName, out var profile))
            {
                profileName = "Default";
                Plugin.Configuration.CustomLootProfiles.TryGetValue(profileName, out profile);
            }

            profile ??= new Dictionary<uint, int>();

            long totalValue = 0;
            foreach (var loot in lootList)
            {
                if (profile.TryGetValue(loot.Primary, out var primaryValue))
                    totalValue += (long)loot.PrimaryCount * primaryValue;

                if (loot.ValidAdditional && profile.TryGetValue(loot.Additional, out var additionalValue))
                    totalValue += (long)loot.AdditionalCount * additionalValue;
            }

            var sub = Plugin.DatabaseCache.GetSubmarines(fcId).FirstOrDefault(s => s.Register == register);
            if (sub == null)
                return;

            if (!Plugin.DatabaseCache.TryGetFC(fcId, out var fc))
                return;

            var nameConverter = new NameConverter();

            var content = new Webhook.WebhookContent();
            content.Embeds.Add(new
            {
                title = nameConverter.GetSub(sub, fc),
                description = $"Loot processed. Total value: {totalValue:N0} gil (Profile: {profileName}).",
                color = 11027200
            });

            Webhook.PostMessage(content);

            // Ensure that the other process had time to catch up
            Thread.Sleep(500);
            mutex.ReleaseMutex();
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Unable to send loot processed webhook");
        }
    }
}
