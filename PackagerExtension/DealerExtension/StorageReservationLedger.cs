using Il2CppScheduleOne.Storage;
using MelonLoader;
using UnityEngine;
using System;
using System.Collections.Generic;
using System.Linq;

namespace DealerSelfSupplySystem.DealerExtension
{
    // =========================================================================
    // StorageReservationLedger
    //
    // Manages stock reservations for a single StorageEntity.
    // Each dealer that needs restocking reserves their weighted share BEFORE
    // starting their travel. This prevents any bottleneck where the first dealer
    // to arrive drains the storage leaving nothing for others.
    //
    // How it works:
    //   1. A dealer calls RequestReservation() when they decide to restock.
    //      The ledger calculates their weighted share based on their deficit
    //      relative to all other active/pending reservations.
    //
    //   2. All existing reservations are REBALANCED every time a new dealer
    //      joins, so the total reserved never exceeds available stock.
    //
    //   3. When a dealer arrives at the storage, they call ConsumeReservation()
    //      to get the exact quantity they are allowed to take.
    //
    //   4. If a dealer cancels (contract received during travel), they call
    //      ReleaseReservation(). The freed stock is redistributed proportionally
    //      to all remaining active reservations.
    //
    // Deficit definition:
    //   deficit = targetFillLevel - currentFillLevel  (clamped to [0, 1])
    //   This represents how much of their capacity each dealer actually needs.
    //   A dealer at 5% with target 80% has deficit 75%.
    //   A dealer at 60% with target 80% has deficit 20%.
    //   Only dealers BELOW the threshold are eligible to request a reservation.
    // =========================================================================
    public class StorageReservationLedger
    {
        // One entry per dealer that has an active reservation.
        public class ReservationEntry
        {
            public DealerExtendedBrain Dealer    { get; set; }
            public int                 Reserved  { get; set; } // items reserved for this dealer
            public float               Deficit   { get; set; } // 0-1, used for weighted rebalancing
            public ReservationStatus   Status    { get; set; }
            public float               CreatedAt { get; set; } // Time.time when reserved
        }

        public enum ReservationStatus
        {
            Active,    // dealer is traveling to storage
            Consumed,  // dealer collected their items
            Released   // dealer cancelled (contract, etc.)
        }

        private readonly StorageEntity _storage;

        // All reservations — active, consumed and released — for logging/debug.
        private readonly List<ReservationEntry> _reservations = new List<ReservationEntry>();

        public StorageReservationLedger(StorageEntity storage)
        {
            _storage = storage;
        }

        // -------------------------------------------------------------------------
        // RequestReservation
        //
        // Called by a dealer that just decided they need to restock.
        // Returns the number of items reserved for them (may be 0 if storage empty).
        //
        // Steps:
        //   1. Calculate available stock = total valid items - already reserved items
        //   2. Calculate this dealer's weighted share based on deficit vs all active dealers
        //   3. Rebalance all existing active reservations so nothing exceeds total stock
        // -------------------------------------------------------------------------
        public int RequestReservation(DealerExtendedBrain dealer, float deficit)
        {
            // Clean up stale entries first (consumed/released dealers)
            PruneInactiveReservations();

            // Don't allow a dealer to have two active reservations
            var existing = _reservations.FirstOrDefault(r =>
                r.Dealer == dealer && r.Status == ReservationStatus.Active);
            if (existing != null)
            {
                Core.MelonLogger.Msg(
                    $"[Ledger:{_storage.name}] {dealer.Dealer.fullName} already has " +
                    $"an active reservation of {existing.Reserved} items, skipping.");
                return existing.Reserved;
            }

            int totalStock = GetTotalValidItems();
            if (totalStock <= 0)
            {
                Core.MelonLogger.Msg($"[Ledger:{_storage.name}] No stock available for reservation.");
                return 0;
            }

            // Add new entry with deficit weight (reserved=0 for now, rebalance will set it)
            var entry = new ReservationEntry
            {
                Dealer    = dealer,
                Reserved  = 0,
                Deficit   = Mathf.Clamp01(deficit),
                Status    = ReservationStatus.Active,
                CreatedAt = Time.time
            };
            _reservations.Add(entry);

            // Rebalance all active reservations proportionally to their deficit
            Rebalance(totalStock);

            Core.MelonLogger.Msg(
                $"[Ledger:{_storage.name}] {dealer.Dealer.fullName} reserved {entry.Reserved} items " +
                $"(deficit={deficit:P1}, stock={totalStock})");

            LogCurrentState();
            return entry.Reserved;
        }

        // -------------------------------------------------------------------------
        // ConsumeReservation
        //
        // Called when a dealer physically arrives at the storage and collects items.
        // Returns the exact quantity they are allowed to take.
        // Also validates against actual current stock (in case it changed externally).
        // -------------------------------------------------------------------------
        public int ConsumeReservation(DealerExtendedBrain dealer)
        {
            var entry = _reservations.FirstOrDefault(r =>
                r.Dealer == dealer && r.Status == ReservationStatus.Active);

            if (entry == null)
            {
                Core.MelonLogger.Warning(
                    $"[Ledger:{_storage.name}] {dealer.Dealer.fullName} tried to consume " +
                    $"but has no active reservation. Falling back to fair share.");
                return FallbackFairShare();
            }

            // Clamp to actual current stock in case another process modified the storage
            int currentStock = GetTotalValidItems();
            int toTake = Mathf.Min(entry.Reserved, currentStock);

            entry.Status = ReservationStatus.Consumed;

            Core.MelonLogger.Msg(
                $"[Ledger:{_storage.name}] {dealer.Dealer.fullName} consuming {toTake} items " +
                $"(reserved={entry.Reserved}, actual stock={currentStock})");

            return toTake;
        }

        // -------------------------------------------------------------------------
        // ReleaseReservation
        //
        // Called when a dealer cancels their trip (received a contract, etc.).
        // Freed items are redistributed proportionally to remaining active dealers.
        // -------------------------------------------------------------------------
        public void ReleaseReservation(DealerExtendedBrain dealer)
        {
            var entry = _reservations.FirstOrDefault(r =>
                r.Dealer == dealer && r.Status == ReservationStatus.Active);

            if (entry == null) return;

            int freed = entry.Reserved;
            entry.Status   = ReservationStatus.Released;
            entry.Reserved = 0;

            Core.MelonLogger.Msg(
                $"[Ledger:{_storage.name}] {dealer.Dealer.fullName} released {freed} items. " +
                $"Redistributing to remaining active dealers.");

            // Rebalance remaining dealers with the now-freed stock
            int totalStock = GetTotalValidItems();
            Rebalance(totalStock);

            LogCurrentState();
        }

        // -------------------------------------------------------------------------
        // Rebalance
        //
        // Core algorithm. Distributes totalStock across all active reservations
        // proportionally to each dealer's deficit weight.
        //
        // Formula per dealer:
        //   share = floor( (deficit / sumOfAllDeficits) * totalStock )
        //
        // Remainder (from floor) is given to the dealer with the highest deficit
        // to avoid wasting stock.
        // -------------------------------------------------------------------------
        private void Rebalance(int totalStock)
        {
            var active = _reservations
                .Where(r => r.Status == ReservationStatus.Active)
                .ToList();

            if (active.Count == 0) return;

            float totalDeficit = active.Sum(r => r.Deficit);

            if (totalDeficit <= 0f)
            {
                // Edge case: all deficits are 0, distribute equally
                int equalShare = totalStock / active.Count;
                foreach (var r in active)
                    r.Reserved = equalShare;
                return;
            }

            int distributed = 0;

            // Assign weighted floor shares
            foreach (var r in active)
            {
                float weight  = r.Deficit / totalDeficit;
                r.Reserved    = Mathf.FloorToInt(weight * totalStock);
                distributed  += r.Reserved;
            }

            // Give remainder to the dealer with the highest deficit
            int remainder = totalStock - distributed;
            if (remainder > 0)
            {
                var topDealer = active.OrderByDescending(r => r.Deficit).First();
                topDealer.Reserved += remainder;
            }
        }

        // -------------------------------------------------------------------------
        // Helpers
        // -------------------------------------------------------------------------

        public bool HasActiveReservation(DealerExtendedBrain dealer)
        {
            return _reservations.Any(r =>
                r.Dealer == dealer && r.Status == ReservationStatus.Active);
        }

        public int GetReservedFor(DealerExtendedBrain dealer)
        {
            var entry = _reservations.FirstOrDefault(r =>
                r.Dealer == dealer && r.Status == ReservationStatus.Active);
            return entry?.Reserved ?? 0;
        }

        // Total items currently locked by active reservations
        public int GetTotalReserved()
        {
            return _reservations
                .Where(r => r.Status == ReservationStatus.Active)
                .Sum(r => r.Reserved);
        }

        public int GetActiveReservationCount()
        {
            return _reservations.Count(r => r.Status == ReservationStatus.Active);
        }

        // Remove consumed/released entries older than 5 minutes to keep list clean
        private void PruneInactiveReservations()
        {
            float cutoff = Time.time - 300f;
            _reservations.RemoveAll(r =>
                r.Status != ReservationStatus.Active && r.CreatedAt < cutoff);
        }

        // Counts valid packaged product items in the storage
        private int GetTotalValidItems()
        {
            return DealerStorageManager.Instance?.GetTotalValidItemsInStorage(_storage) ?? 0;
        }

        // Fallback if somehow a dealer arrives without a reservation
        private int FallbackFairShare()
        {
            int stock   = GetTotalValidItems();
            int dealers = Mathf.Max(1, GetActiveReservationCount() + 1);
            return stock / dealers;
        }

        public void Clear()
        {
            _reservations.Clear();
        }

        private void LogCurrentState()
        {
            var active = _reservations.Where(r => r.Status == ReservationStatus.Active).ToList();
            if (active.Count == 0) return;

            Core.MelonLogger.Msg($"[Ledger:{_storage.name}] Current reservations:");
            foreach (var r in active)
            {
                Core.MelonLogger.Msg(
                    $"  {r.Dealer.Dealer.fullName}: {r.Reserved} items (deficit={r.Deficit:P1})");
            }
        }
    }
}