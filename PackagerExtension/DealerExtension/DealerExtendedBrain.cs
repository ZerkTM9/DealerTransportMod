using Il2CppScheduleOne.Economy;
using Il2CppScheduleOne.ItemFramework;
using Il2CppScheduleOne.Product;
using Il2CppScheduleOne.Storage;
using MelonLoader;
using DealerSelfSupplySystem.Utils;
using Il2CppScheduleOne.Money;
using UnityEngine;
using System;
using System.Collections.Generic;
using System.Linq;

namespace DealerSelfSupplySystem.DealerExtension
{
    public class DealerExtendedBrain
    {
        public Dealer Dealer             { get; private set; }
        public bool   NeedsItems         { get; private set; } = false;
        public bool   IsProcessingItems  { get; private set; } = false;

        private float lastInventoryCheckTime  = 0f;
        private const float INVENTORY_CHECK_INTERVAL = 30f;
        private float INVENTORY_THRESHOLD;
        private bool  _canBeInterrupted = false;

        // Stats
        public int      TotalItemsCollected      { get; private set; } = 0;
        public DateTime LastCollectionTime        { get; private set; }
        public string   LastProcessedStorageName  { get; private set; } = "None";

        public DealerExtendedBrain(Dealer dealer)
        {
            Dealer            = dealer;
            INVENTORY_THRESHOLD = Config.dealerInventoryThreshold.Value;
        }

        // -------------------------------------------------------------------------
        // Inventory state helpers
        // -------------------------------------------------------------------------

        // Fraction of slots currently occupied (0 = empty, 1 = full)
        public float GetInventoryFillPercentage()
        {
            try
            {
                var slotsArray = Dealer.GetAllSlots();
                if (slotsArray == null) return 0f;

                int total = 0, filled = 0;
                foreach (var slot in slotsArray)
                {
                    if (slot == null) continue;
                    total++;
                    if (slot.Quantity > 0) filled++;
                }
                return total == 0 ? 0f : (float)filled / total;
            }
            catch { return 0f; }
        }

        // How much inventory this dealer still needs to reach their target fill.
        // This is the weight used by the reservation ledger for proportional distribution.
        // Returns a value in [0, 1].
        //   deficit = clamp(targetFill - currentFill, 0, 1)
        // A completely empty dealer targeting 80% has deficit = 0.80.
        // A dealer at 60% targeting 80% has deficit = 0.20.
        public float GetDeficit()
        {
            float current = GetInventoryFillPercentage();
            float target  = Mathf.Clamp01(Config.dealerTargetFillLevel.Value);
            return Mathf.Clamp01(target - current);
        }

        public bool CalculateNeedsItems()
        {
            if (Time.time - lastInventoryCheckTime < INVENTORY_CHECK_INTERVAL)
                return NeedsItems;

            lastInventoryCheckTime = Time.time;

            try
            {
                var slotsArray = Dealer.GetAllSlots();
                if (slotsArray == null)
                {
                    NeedsItems = false;
                    return false;
                }

                List<ItemSlot> slots = new List<ItemSlot>();
                foreach (var slot in slotsArray)
                    if (slot != null) slots.Add(slot);

                if (slots.Count == 0)
                {
                    NeedsItems = false;
                    return false;
                }

                int   totalSlots  = slots.Count;
                int   filledSlots = slots.Count(s => s.Quantity > 0);
                float fill        = (float)filledSlots / totalSlots;

                // Triggered only when below the threshold — NOT based on target.
                // Target is only used to know when to STOP collecting.
                bool needsItems = fill < INVENTORY_THRESHOLD;

                if (needsItems != NeedsItems)
                {
                    Core.MelonLogger.Msg(needsItems
                        ? $"Dealer {Dealer.fullName} inventory low ({fill:P1}), needs restocking"
                        : $"Dealer {Dealer.fullName} inventory sufficient ({fill:P1})");
                }

                NeedsItems = needsItems;
                return needsItems;
            }
            catch (Exception ex)
            {
                Core.MelonLogger.Error($"Error checking dealer inventory: {ex.Message}");
                NeedsItems = false;
                return false;
            }
        }

        // -------------------------------------------------------------------------
        // Collection trigger
        // -------------------------------------------------------------------------

        public bool TryCollectItemsFromStorage(StorageEntity storageEntity)
        {
            try
            {
                if (storageEntity == null)
                {
                    Core.MelonLogger.Error("Cannot collect: storage entity is null");
                    return false;
                }

                if (Dealer.currentContract != null)
                {
                    Core.MelonLogger.Msg($"Dealer {Dealer.fullName} is on a contract, releasing reservation.");
                    DealerStorageManager.Instance?.GetOrCreateLedger(storageEntity)
                                                  .ReleaseReservation(this);
                    return false;
                }

                var ledger = DealerStorageManager.Instance?.GetOrCreateLedger(storageEntity);

                if (!CalculateNeedsItems())
                {
                    // Inventory recovered between reservation and collection — free the slot
                    ledger?.ReleaseReservation(this);
                    return false;
                }

                if (IsProcessingItems)
                {
                    Core.MelonLogger.Msg($"Dealer {Dealer.fullName} is already traveling");
                    return false;
                }

                // Verify the ledger actually has a reservation for us before traveling
                if (ledger != null && !ledger.HasActiveReservation(this))
                {
                    Core.MelonLogger.Warning(
                        $"Dealer {Dealer.fullName} tried to collect without a reservation. " +
                        $"This should not happen — CheckDealerStorage always reserves first.");
                    return false;
                }

                MelonCoroutines.Start(SimulateItemCollection(storageEntity));
                return true;
            }
            catch (Exception ex)
            {
                Core.MelonLogger.Error($"Error in TryCollectItemsFromStorage: {ex.Message}");
                IsProcessingItems = false;
                return false;
            }
        }

        // -------------------------------------------------------------------------
        // Travel + collection coroutine
        // -------------------------------------------------------------------------

        private float CalculateTravelTime(StorageEntity storageEntity)
        {
            try
            {
                if (Dealer.transform != null && storageEntity.transform != null)
                {
                    float distance = Vector3.Distance(Dealer.transform.position, storageEntity.transform.position);
                    float baseTime = Mathf.Max(5f, distance * 0.5f);
                    return baseTime * UnityEngine.Random.Range(0.8f, 1.2f);
                }
            }
            catch (Exception ex)
            {
                Core.MelonLogger.Warning($"Error calculating travel time: {ex.Message}");
            }
            return UnityEngine.Random.Range(10f, 20f);
        }

        private System.Collections.IEnumerator SimulateItemCollection(StorageEntity storageEntity)
        {
            // Flag set INSIDE coroutine so the finally always pairs with it
            IsProcessingItems        = true;
            LastProcessedStorageName = storageEntity.name;
            float travelTime         = CalculateTravelTime(storageEntity);

            Core.MelonLogger.Msg(
                $"Dealer {Dealer.fullName} traveling to {storageEntity.name} " +
                $"(ETA {travelTime:F1}s, reserved: " +
                $"{DealerStorageManager.Instance?.GetOrCreateLedger(storageEntity).GetReservedFor(this)} items)");

            try
            {
                yield return new WaitForSeconds(travelTime);

                // If dealer received a contract during travel, release reservation and abort
                if (Dealer.currentContract != null && _canBeInterrupted)
                {
                    Core.MelonLogger.Msg(
                        $"Dealer {Dealer.fullName} received a contract during travel, releasing reservation.");
                    DealerStorageManager.Instance?.GetOrCreateLedger(storageEntity)
                                                  .ReleaseReservation(this);
                    yield break;
                }

                Core.MelonLogger.Msg($"Dealer {Dealer.fullName} arrived at {storageEntity.name}");

                // Deposit cash before collecting stock — dealer drops earnings on the way in
                DepositCash();

                int itemsCollected = AddItemsFromStorage(storageEntity);

                if (itemsCollected > 0)
                {
                    TotalItemsCollected += itemsCollected;
                    LastCollectionTime   = DateTime.Now;
                    Core.MelonLogger.Msg($"Dealer {Dealer.fullName} collected {itemsCollected} items");
                    Dealer.SendTextMessage(Messages.GetRandomItemCollectionMessage(true));
                }
                else
                {
                    Core.MelonLogger.Msg($"Dealer {Dealer.fullName} found nothing to collect");
                    Dealer.SendTextMessage(Messages.GetRandomItemCollectionMessage(false));
                }

                yield return new WaitForSeconds(travelTime);
                Core.MelonLogger.Msg($"Dealer {Dealer.fullName} returned from {storageEntity.name}");
            }
            finally
            {
                IsProcessingItems = false;
            }
        }

        // -------------------------------------------------------------------------
        // Item transfer
        // -------------------------------------------------------------------------

        public int AddItemsFromStorage(StorageEntity storageEntity)
        {
            try
            {
                if (storageEntity == null) return 0;

                var itemsArray = storageEntity.GetAllItems();
                if (itemsArray == null) return 0;

                // Ask the ledger how many items we are allowed to take.
                // ConsumeReservation() also clamps to actual current stock.
                var ledger       = DealerStorageManager.Instance?.GetOrCreateLedger(storageEntity);
                int maxToTake    = ledger?.ConsumeReservation(this) ?? FallbackMaxItems(storageEntity);

                if (maxToTake <= 0)
                {
                    Core.MelonLogger.Msg($"Dealer {Dealer.fullName}: reservation resolved to 0, nothing to take");
                    return 0;
                }

                List<ItemInstance> storageItems = new List<ItemInstance>();
                foreach (var item in itemsArray)
                    if (item != null) storageItems.Add(item);

                List<ItemInstance> validItems = storageItems
                    .Where(item => item.TryCast<ProductItemInstance>() != null &&
                                   !item.Name.Contains("Unpackaged"))
                    .ToList();

                Core.MelonLogger.Msg(
                    $"Dealer {Dealer.fullName}: taking up to {maxToTake} from " +
                    $"{validItems.Count} valid stacks in {storageEntity.name}");

                int totalTransferred = 0;
                int stacksTransferred = 0;

                foreach (var item in validItems)
                {
                    if (totalTransferred >= maxToTake) break;

                    // Cap by reservation quota
                    int quantityToTake = Mathf.Min(item.Quantity, maxToTake - totalTransferred);

                    // Also hard-cap by available inventory space so we never overfill
                    int availableSpace = GetDealerAvailableSpace();
                    quantityToTake = Mathf.Min(quantityToTake, availableSpace);

                    if (quantityToTake <= 0)
                    {
                        Core.MelonLogger.Msg($"Dealer {Dealer.fullName} inventory full, stopping.");
                        break;
                    }

                    ItemInstance copy = item.GetCopy(quantityToTake);
                    if (copy == null)
                    {
                        Core.MelonLogger.Warning($"Failed to copy item {item.Name}");
                        continue;
                    }

                    Dealer.AddItemToInventory(copy);
                    item.ChangeQuantity(-quantityToTake);
                    totalTransferred  += quantityToTake;
                    stacksTransferred++;

                    Core.MelonLogger.Msg(
                        $"  Transferred {quantityToTake}x {item.Name} to {Dealer.fullName}");
                }

                Core.MelonLogger.Msg(
                    $"Dealer {Dealer.fullName}: collected {totalTransferred} items " +
                    $"({stacksTransferred} stacks) from {storageEntity.name}");

                return totalTransferred;
            }
            catch (Exception ex)
            {
                Core.MelonLogger.Error($"Error adding items from storage: {ex.Message}\n{ex.StackTrace}");
                return 0;
            }
        }

        // How many item slots are currently free in this dealer's inventory.
        // Hard cap so we never collect more than the dealer can physically carry.
        private int GetDealerAvailableSpace()
        {
            try
            {
                var slotsArray = Dealer.GetAllSlots();
                if (slotsArray == null) return 0;

                int free = 0;
                foreach (var slot in slotsArray)
                {
                    if (slot == null) continue;
                    if (slot.ItemInstance != null)
                        free += slot.ItemInstance.StackLimit - slot.ItemInstance.Quantity;
                    else
                        free += 20; // default empty slot capacity
                }
                return free;
            }
            catch { return 0; }
        }

        // Transfers all cash the dealer currently holds to the player.
        // Called when the dealer arrives at the storage on their restock trip.
        // Mirrors the vanilla CollectCash() logic used when the player manually collects.
        private void DepositCash()
        {
            try
            {
                float cash = Dealer.Cash;
                if (cash <= 0f)
                {
                    Core.MelonLogger.Msg($"Dealer {Dealer.fullName} has no cash to deposit.");
                    return;
                }

                // Give the money to the player — same call the game uses in CollectCash()
                NetworkSingleton<MoneyManager>.Instance.ChangeCashBalance(cash, true, true);

                // Zero out the dealer cash
                Dealer.SetCash(0f);

                Core.MelonLogger.Msg($"Dealer {Dealer.fullName} deposited ${cash:F2} on their storage run.");
                Dealer.SendTextMessage(Messages.GetRandomCashDepositMessage(cash));
            }
            catch (Exception ex)
            {
                Core.MelonLogger.Error($"Error depositing dealer cash: {ex.Message}");
            }
        }

        // Used only if somehow AddItemsFromStorage is called without a ledger reservation.
        private int FallbackMaxItems(StorageEntity storageEntity)
        {
            Core.MelonLogger.Warning(
                $"Dealer {Dealer.fullName}: using fallback max items calculation (no ledger)");

            int stock   = DealerStorageManager.Instance?.GetTotalValidItemsInStorage(storageEntity) ?? 0;
            int dealers = Mathf.Max(1,
                DealerStorageManager.Instance?.GetDealersFromStorage(storageEntity).Count ?? 1);
            return stock / dealers;
        }
    }
}