using System.Collections.Generic;

namespace TortalPortalLite.Core.Hooks
{
    /// <summary>
    /// An insertion-ordered-by-priority handler list, shared by every broker in this folder. Lower
    /// priority runs first. Stable for equal priorities (later Add() at the same priority runs after
    /// earlier ones), so registration order is deterministic given the same wave build order.
    /// </summary>
    public sealed class PriorityList<T>
    {
        private readonly List<(int priority, T handler)> _entries = new List<(int, T)>();

        public void Add(int priority, T handler)
        {
            int i = 0;
            while (i < _entries.Count && _entries[i].priority <= priority)
            {
                i++;
            }
            _entries.Insert(i, (priority, handler));
        }

        public IEnumerable<T> InOrder()
        {
            foreach (var entry in _entries)
            {
                yield return entry.handler;
            }
        }

        public int Count => _entries.Count;
    }
}
