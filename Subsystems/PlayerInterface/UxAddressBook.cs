using System;
using System.Collections.Generic;
using UnityEngine;
using TortalPortalLite.Core.Data;
using TortalPortalLite.Subsystems.Foundations;

namespace TortalPortalLite.Subsystems.PlayerInterface
{
    /// <summary>
    /// #152/#177 L1 STATE - "position-keyed portal address book". Deliberately NOT a separately
    /// persisted file: a portal's player-given name already lives in the one place that survives a
    /// restart for free - its own `s_tag` field, prefixed with TagCodec's '#' sigil ("a player-authored
    /// destination alias... targeted/UX domain"). So this class is a STATELESS query layer over
    /// PortalCensus (Foundations' own 1Hz snapshot), never a second source of truth: every call re-reads
    /// PortalCensus.Latest fresh, which is exactly the catalog's own prescription ("re-resolve positions
    /// to live ZDOs... each tick", "NEVER key by ZDOID - ZDO.Load renumbers every uid").
    ///
    /// Numeric indexing ("&gt;7") uses a stable ordinal-name sort computed on demand - it does not need
    /// to be identical across restarts, only self-consistent within one player's "?" listing and their
    /// next "&gt;N" a few seconds later.
    /// </summary>
    public static class UxAddressBook
    {
        public readonly struct AddressEntry
        {
            public readonly string Name;
            public readonly PortalRecord Record;
            public AddressEntry(string name, PortalRecord record) { Name = name; Record = record; }
        }

        /// <summary>Every currently-named portal (tag starts with '#'), sorted case-insensitively by name for stable indexing.</summary>
        public static List<AddressEntry> Ordered()
        {
            var list = new List<AddressEntry>();
            foreach (PortalRecord rec in PortalCensus.Latest)
            {
                if (!TagCodec.HasSigil(rec.Tag, '#'))
                {
                    continue;
                }
                string name = TagCodec.PayloadAfter(rec.Tag, '#');
                if (string.IsNullOrEmpty(name))
                {
                    continue;
                }
                list.Add(new AddressEntry(name, rec));
            }
            list.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            return list;
        }

        public static bool TryGet(string name, out PortalRecord record)
        {
            record = default;
            if (string.IsNullOrWhiteSpace(name))
            {
                return false;
            }
            string want = name.Trim();
            foreach (PortalRecord rec in PortalCensus.Latest)
            {
                if (TagCodec.HasSigil(rec.Tag, '#') && string.Equals(TagCodec.PayloadAfter(rec.Tag, '#'), want, StringComparison.OrdinalIgnoreCase))
                {
                    record = rec;
                    return true;
                }
            }
            return false;
        }

        /// <summary>1-based index into the current Ordered() listing - what a player just saw via '?' / the sign directory.</summary>
        public static bool TryGetByIndex(int oneBasedIndex, out AddressEntry entry)
        {
            entry = default;
            List<AddressEntry> ordered = Ordered();
            if (oneBasedIndex < 1 || oneBasedIndex > ordered.Count)
            {
                return false;
            }
            entry = ordered[oneBasedIndex - 1];
            return true;
        }

        /// <summary>"Did you mean X?" - cheapest-useful typo help: shortest Levenshtein distance among named portals, capped so a wildly wrong guess suggests nothing rather than something misleading.</summary>
        public static bool TryFindClosest(string typed, out string suggestion)
        {
            suggestion = "";
            if (string.IsNullOrWhiteSpace(typed))
            {
                return false;
            }
            int best = int.MaxValue;
            foreach (AddressEntry e in Ordered())
            {
                int d = Levenshtein(typed, e.Name);
                if (d < best)
                {
                    best = d;
                    suggestion = e.Name;
                }
            }
            return best <= 3 && !string.IsNullOrEmpty(suggestion);
        }

        private static int Levenshtein(string a, string b)
        {
            a = a.ToLowerInvariant();
            b = b.ToLowerInvariant();
            int[,] d = new int[a.Length + 1, b.Length + 1];
            for (int i = 0; i <= a.Length; i++) d[i, 0] = i;
            for (int j = 0; j <= b.Length; j++) d[0, j] = j;
            for (int i = 1; i <= a.Length; i++)
            {
                for (int j = 1; j <= b.Length; j++)
                {
                    int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                    d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
                }
            }
            return d[a.Length, b.Length];
        }

        /// <summary>Any portal (named or not) within radius of pos - used by input channels that bind to "the portal I'm standing at" rather than an address-book alias.</summary>
        public static bool TryNearestAnyPortal(Vector3 pos, float radius, out PortalRecord record)
        {
            record = default;
            bool found = false;
            float bestSqr = radius * radius;
            foreach (PortalRecord rec in PortalCensus.Latest)
            {
                float d = (rec.Position - pos).sqrMagnitude;
                if (d <= bestSqr)
                {
                    bestSqr = d;
                    record = rec;
                    found = true;
                }
            }
            return found;
        }
    }
}
