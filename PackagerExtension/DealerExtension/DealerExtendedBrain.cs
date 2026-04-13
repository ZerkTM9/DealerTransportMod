using Il2CppScheduleOne.Economy;
using Il2CppScheduleOne.ItemFramework;
using Il2CppScheduleOne.Product;
using Il2CppScheduleOne.Storage;
using MelonLoader;
using DealerSelfSupplySystem.Utils;
using UnityEngine;
using System;
using System.Collections.Generic;
using System.Linq;

namespace DealerSelfSupplySystem.DealerExtension
{
    public class DealerExtendedBrain
    {
        public Dealer Dealer { get; private set; }
        public bool NeedsItems { get; private set; } = false;
        public bool IsProcessingItems { get; private set; } = false;
        private float lastInventoryCheckTime = 0f;
        private const float INVENTORY_CHECK_INTERVAL = 30f;
        private float INVENTORY_THRESHOLD;
        private bool _canBeInturrupted = false;

        // Stats tracking
        public int TotalItemsCollected { get; private set; } = 0;
        public DateTime LastCollectionTime { get; private set; }
        public string LastProcessedStorageName { get; private set; } = "None";

        public DealerExtendedBrain(Dealer dealer)
        {
            Dealer = dealer;
            INVENTORY_THRESHOLD = Config.dealerInventoryThreshold.Value;
        }

        // Returns the fraction of inventory slots that are occupied (0.0 = empty, 1.0 = full).
        // Used by DealerStorageManager to sort dealers by priority.
        public float GetInventoryFillPercentage()
        {
            try
            {
                var slotsArray = Dealer.GetAllSlots();
                if (slotsArray == null) return 0f;

                int totalSlots = 0;
                int filledSlots = 0;
                foreach (var slot in slotsArray)
                {
                    if (slot == null) continue;
                    totalSlots++;
                    if (slot.Quantity > 0) filledSlots++;
                }

                return totalSlots == 0 ? 0f : (float)filledSlots / totalSlots;
            }
            catch
            {
                return 0f;
            }
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
                    Core.MelonLogger.Msg($"Dealer {Dealer.fullName} has no inventory slots array");
                    NeedsItems = false;
                    return false;
                }

                List<ItemSlot> slots = new List<ItemSlot>();
                foreach (var slot in slotsArray)
                {
                    if (slot != null) slots.Add(slot);
                }

                if (slots.Count == 0)
                {
                    Core.MelonLogger.Msg($"Dealer {Dealer.fullName} has no inventory slots");
                    NeedsItems = false;
                    return false;
                }

                int totalSlots = slots.Count;
                int filledSlots = slots.Count(s => s.Quantity > 0);
                float fillPercentage = (float)filledSlots / totalSlots;

                // Dealer needs items if below threshold AND hasn't yet reached target fill
                float targetFill = Config.dealerTargetFillLevel.Value;
                bool needsItems = fillPercentage < INVENTORY_THRESHOLD && fillPercentage < targetFill;

                if (needsItems != NeedsItems)
                {
                    if (needsItems)
                        Core.MelonLogger.Msg($"Dealer {Dealer.fullName} inventory low ({fillPercentage:P1}), needs restocking");
                    else
                        Core.MelonLogger.Msg($"Dealer {Dealer.fullName} inventory sufficient ({fillPercentage:P1})");
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

        public bool TryCollectItemsFromStorage(StorageEntity storageEntity)
        {
            try
            {
                if (storageEntity == null)
                {
                    Core.MelonLogger.Error("Cannot collect items: storage entity is null");
                    return false;
                }

                if (Dealer.currentContract != null)
                {
                    Core.MelonLogger.Msg($"Dealer {Dealer.fullName} is busy with a contract, cannot collect items");
                    return false;
                }

                if (!CalculateNeedsItems())
                    return false;

                if (IsProcessingItems)
                {
                    Core.MelonLogger.Msg($"Dealer {Dealer.fullName} is already processing items, cannot collect again");
                    return false;
                }

                if (!HasValidItemsForDealer(storageEntity))
                {
                    Core.MelonLogger.Msg($"Storage {storageEntity.name} has no valid items for {Dealer.fullName}");
                    return false;
                }

                IsProcessingItems = true;
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

        private bool HasValidItemsForDealer(StorageEntity storageEntity)
        {
            try
            {
                int total = GetTotalValidItemsInStorage(storageEntity);
                if (total <= 0)
                {
                    Core.MelonLogger.Msg($"Storage {storageEntity.name} has no valid items for {Dealer.fullName}");
                    return false;
                }
                Core.MelonLogger.Msg($"Storage {storageEntity.name} has {total} valid items available");
                return true;
            }
            catch (Exception ex)
            {
                Core.MelonLogger.Error($"Error checking valid items: {ex.Message}");
                return false;
            }
        }

        // Counts the total quantity of all valid (packaged product) items in a storage.
        private int GetTotalValidItemsInStorage(StorageEntity storageEntity)
        {
            try
            {
                var itemsArray = storageEntity.GetAllItems();
                if (itemsArray == null) return 0;

                int total = 0;
                foreach (var item in itemsArray)
                {
                    if (item != null &&
                        item.TryCast<ProductItemInstance>() != null &&
                        !item.Name.Contains("Unpackaged"))
                    {
                        total += item.Quantity;
                    }
                }
                return total;
            }
            catch (Exception ex)
            {
                Core.MelonLogger.Error($"Error counting storage items: {ex.Message}");
                return 0;
            }
        }

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
                Core.MelonLogger.Warning($"Error calculating travel distance: {ex.Message}. Using default time.");
            }
            return UnityEngine.Random.Range(10f, 20f);
        }

        private System.Collections.IEnumerator SimulateItemCollection(StorageEntity storageEntity)
        {
            LastProcessedStorageName = storageEntity.name;
            float travelTime = CalculateTravelTime(storageEntity);

            Core.MelonLogger.Msg($"Dealer {Dealer.fullName} is traveling to storage {storageEntity.name}, ETA: {travelTime:F1}s");
            try
            {
                yield return new WaitForSeconds(travelTime);

                if (Dealer.currentContract != null && _canBeInturrupted)
                {
                    Core.MelonLogger.Msg($"Dealer {Dealer.fullName} received a contract during travel, aborting item collection");
                    IsProcessingItems = false;
                    yield break;
                }

                Core.MelonLogger.Msg($"Dealer {Dealer.fullName} reached storage {storageEntity.name}, collecting items");
                int itemsCollected = AddItemsFromStorage(storageEntity);

                if (itemsCollected > 0)
                {
                    Core.MelonLogger.Msg($"Dealer {Dealer.fullName} collected {itemsCollected} items from {storageEntity.name}");
                    TotalItemsCollected += itemsCollected;
                    LastCollectionTime = DateTime.Now;
                    Dealer.SendTextMessage(Messages.GetRandomItemCollectionMessage(true));
                }
                else
                {
                    Core.MelonLogger.Msg($"Dealer {Dealer.fullName} found no suitable items in storage {storageEntity.name}");
                    Dealer.SendTextMessage(Messages.GetRandomItemCollectionMessage(false));
                }

                yield return new WaitForSeconds(travelTime);
                Core.MelonLogger.Msg($"Dealer {Dealer.fullName} has returned from storage {storageEntity.name}");
            }
            finally
            {
                IsProcessingItems = false;
            }
        }

        public int AddItemsFromStorage(StorageEntity storageEntity)
        {
            try
            {
                if (storageEntity == null)
                {
                    Core.MelonLogger.Error("Cannot add items: storage entity is null");
                    return 0;
                }

                var itemsArray = storageEntity.GetAllItems();
                if (itemsArray == null)
                {
                    Core.MelonLogger.Error("Storage returned null items array");
                    return 0;
                }

                List<ItemInstance> storageItems = new List<ItemInstance>();
                foreach (var item in itemsArray)
                {
                    if (item != null) storageItems.Add(item);
                }

                int maxItemsToTake = CalculateMaxItemsToTake(storageEntity);
                if (maxItemsToTake <= 0)
                {
                    Core.MelonLogger.Msg($"Dealer {Dealer.fullName} does not need any more items (already at target fill)");
                    return 0;
                }

                Core.MelonLogger.Msg($"Dealer {Dealer.fullName} will collect up to {maxItemsToTake} items from {storageItems.Count} stacks in storage");

                // Filter for valid packaged products only
                List<ItemInstance> validItems = storageItems
                    .Where(item => item != null &&
                                   item.TryCast<ProductItemInstance>() != null &&
                                   !item.Name.Contains("Unpackaged"))
                    .ToList();

                Core.MelonLogger.Msg($"Found {validItems.Count} valid item stacks in storage {storageEntity.name}");

                int itemsTransferred = 0;
                int totalQuantityTransferred = 0;

                foreach (var item in validItems)
                {
                    if (totalQuantityTransferred >= maxItemsToTake) break;

                    int quantityToTake = Mathf.Min(item.Quantity, maxItemsToTake - totalQuantityTransferred);
                    if (quantityToTake <= 0) continue;

                    ItemInstance itemCopy = item.GetCopy(quantityToTake);
                    if (itemCopy == null)
                    {
                        Core.MelonLogger.Warning($"Failed to create copy of item {item.Name}");
                        continue;
                    }

                    Dealer.AddItemToInventory(itemCopy);
                    item.ChangeQuantity(-quantityToTake);
                    itemsTransferred++;
                    totalQuantityTransferred += quantityToTake;
                    Core.MelonLogger.Msg($"Transferred {quantityToTake}x {item.Name} to dealer {Dealer.fullName}");
                }

                Core.MelonLogger.Msg($"Dealer {Dealer.fullName} collected {totalQuantityTransferred} items ({itemsTransferred} stacks) from {storageEntity.name}");
                return totalQuantityTransferred;
            }
            catch (Exception ex)
            {
                Core.MelonLogger.Error($"Error adding items from storage: {ex.Message}\n{ex.StackTrace}");
                return 0;
            }
        }

        // How many items this dealer needs to reach its target fill level.
        private int CalculateNeededToReachTarget()
        {
            try
            {
                float targetFill = Mathf.Clamp(Config.dealerTargetFillLevel.Value, 0f, 1f);

                var slotsArray = Dealer.GetAllSlots();
                if (slotsArray == null) return 0;

                int totalCapacity = 0;
                int currentItems = 0;

                foreach (var slot in slotsArray)
                {
                    if (slot == null) continue;
                    if (slot.ItemInstance != null)
                    {
                        totalCapacity += slot.ItemInstance.StackLimit;
                        currentItems += slot.ItemInstance.Quantity;
                    }
                    else
                    {
                        totalCapacity += 20; // default stack size for an empty slot
                    }
                }

                if (totalCapacity <= 0) return 0;

                int targetItems = Mathf.FloorToInt(totalCapacity * targetFill);
                return Mathf.Max(0, targetItems - currentItems);
            }
            catch (Exception ex)
            {
                Core.MelonLogger.Error($"Error calculating needed items: {ex.Message}");
                return 0;
            }
        }

        // Calculates how many items this dealer may take from the given storage.
        //
        // The logic uses two limits and takes the smaller of the two:
        //   1. Fair share  — storage total divided equally among all dealers assigned to it.
        //                    Prevents any single dealer from draining the storage before
        //                    the others have had a chance to collect.
        //   2. Target need — how many items the dealer needs to reach its target fill level.
        //                    Prevents over-collection when the dealer is already well-stocked.
        private int CalculateMaxItemsToTake(StorageEntity storageEntity)
        {
            try
            {
                // 1. How many items does this dealer actually need?
                int neededToFill = CalculateNeededToReachTarget();
                if (neededToFill <= 0)
                {
                    Core.MelonLogger.Msg($"Dealer {Dealer.fullName} is already at or above target fill level");
                    return 0;
                }

                // 2. How many valid items are currently in the storage?
                int storageTotal = GetTotalValidItemsInStorage(storageEntity);
                if (storageTotal <= 0)
                    return 0;

                // 3. How many dealers share this storage?
                int numDealers = 1;
                if (DealerStorageManager.Instance != null)
                {
                    var assignedDealers = DealerStorageManager.Instance.GetDealersFromStorage(storageEntity);
                    numDealers = Mathf.Max(1, assignedDealers.Count);
                }

                // 4. Fair share: each dealer is entitled to at most 1/N of the available stock.
                //    Integer division is intentional — remainders stay in storage.
                int fairShare = storageTotal / numDealers;

                // 5. Take the smaller of the two limits.
                int maxItems = Mathf.Min(neededToFill, fairShare);

                Core.MelonLogger.Msg(
                    $"Dealer {Dealer.fullName}: needs {neededToFill} items to reach target, " +
                    $"fair share {fairShare} ({storageTotal} in storage / {numDealers} dealers), " +
                    $"will take up to {maxItems}");

                return Mathf.Max(1, maxItems);
            }
            catch (Exception ex)
            {
                Core.MelonLogger.Error($"Error calculating max items to take: {ex.Message}");
                return 1;
            }
        }
    }
}
