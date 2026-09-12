using System.Collections.Generic;

namespace TortalPortalLite.Core.Data
{
    /// <summary>
    /// Every transfer any subsystem makes through ZdoInventoryIO.MoveMatchingItem is recorded here -
    /// "never lose an item" as a concrete, checkable trail. Bounded ring buffer, not a database: a
    /// diagnostic trail for grepping logs, not a live world-wide item-count auditor.
    /// Forked verbatim from Wonderland's Core/Data/ItemLedger.cs (namespace renamed only).
    /// </summary>
    public static class ItemLedger
    {
        private const int MaxEntries = 2000;

        public readonly struct Entry
        {
            public readonly string Subsystem;
            public readonly string ItemName;
            public readonly int Amount;
            public readonly bool Credited;

            public Entry(string subsystem, string itemName, int amount, bool credited)
            {
                Subsystem = subsystem;
                ItemName = itemName;
                Amount = amount;
                Credited = credited;
            }
        }

        private static readonly LinkedList<Entry> Entries = new LinkedList<Entry>();
        private static long _totalMoved;
        private static long _totalRejected;

        public static void RecordTransfer(string subsystem, string itemName, int amount)
        {
            _totalMoved += amount;
            Append(new Entry(subsystem, itemName, amount, credited: true));
            PortalDebug.LogInfo($"[ItemLedger] {subsystem} moved {amount}x {itemName}");
        }

        /// <summary>A transfer that was attempted but refused - full stack didn't fit, guard rejected it, etc.</summary>
        public static void RecordRejection(string subsystem, string itemName, int amount, string reason)
        {
            _totalRejected += amount;
            Append(new Entry(subsystem, itemName, amount, credited: false));
            PortalDebug.LogAlways($"[ItemLedger] {subsystem} rejected {amount}x {itemName}: {reason}");
        }

        private static void Append(Entry entry)
        {
            Entries.AddLast(entry);
            if (Entries.Count > MaxEntries)
            {
                Entries.RemoveFirst();
            }
        }

        public static (long moved, long rejected, int entriesHeld) GetSummary()
        {
            return (_totalMoved, _totalRejected, Entries.Count);
        }
    }
}
