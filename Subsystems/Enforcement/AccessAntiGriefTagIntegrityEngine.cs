using System.Collections.Generic;
using System.Text;

namespace TortalPortalLite.Subsystems.Enforcement
{
    /// <summary>
    /// #55 Anti-Grief Tag Integrity. Three vectors named by this option; two are already covered
    /// elsewhere in this build, so this class carries only the third:
    ///  - Stranger-linking into a private network: fully solved by AccessManagedNetworkGovernorEngine
    ///    (#54), which simply never gives an unauthorised same-tag portal a partner.
    ///  - The odd-count "3 portals, 1 orphan" denial attack: already detected and reported by
    ///    Subsystems/Foundations/HealthScanEngine's own "OddCountStrand" finding (a pure read, no
    ///    write, over the same census) - re-implementing it here would just be a second copy of the
    ///    same scan. Left to that engine, per this wave's "don't touch Foundations, do use it" rule.
    ///  - Tag squatting via a CONFUSABLE variant (case/whitespace/zero-width - vanilla's own comparison
    ///    is bare ordinal `string !=`, so `farm`/`Farm`/`farm ` are three different networks): THIS is
    ///    what remains, and what this class implements as a first-claim registry keyed on a normalised
    ///    form used for collision detection only - the stored/compared value handed back to
    ///    AccessTagWatchdogEngine is always the untouched raw tag, never the normalised one (writing a
    ///    normalised value would itself desync vanilla's own pairing, per this option's own failure mode).
    ///
    /// The registry is in-memory only (not persisted) - a restart resets "who claimed this tag first".
    /// Acceptable for a wave-2 scope: the exact-match case (two portals legitimately sharing one network
    /// name) is unaffected either way, and the confusable-variant case this engine actually blocks is
    /// re-learned within one watchdog cycle of the legitimate owner's tag being seen again.
    ///
    /// Homoglyph folding is NOT implemented (no table shipped) - only whitespace/case/zero-width
    /// normalisation. The option's own failure mode admits homoglyph tables are "never complete" and
    /// this reduces, not eliminates, the attack regardless; a table can be added later without changing
    /// this class's shape (see Normalize).
    /// </summary>
    public static class AccessAntiGriefTagIntegrityEngine
    {
        private sealed class Bucket
        {
            public string CanonicalTag = "";
            public string FirstClaimant = "";
            public readonly HashSet<ZDOID> Members = new HashSet<ZDOID>();
        }

        private static readonly Dictionary<string, Bucket> _registry = new Dictionary<string, Bucket>();

        /// <summary>
        /// True if <paramref name="portalUid"/> may adopt <paramref name="rawTag"/>. False (with a
        /// reason) only for the confusable-variant case; an exact re-use of an already-registered tag
        /// is always allowed here (whether that portal may actually PAIR under it is the Managed
        /// Network Governor's separate decision).
        /// </summary>
        public static bool CheckClaim(string rawTag, string actorHost, ZDOID portalUid, out string reason)
        {
            reason = "";
            if (string.IsNullOrEmpty(rawTag))
            {
                return true; // the empty-tag group is Untagged Auto-Pair Suppression's concern, not squatting.
            }

            if (rawTag != rawTag.Trim())
            {
                reason = "leading/trailing whitespace in tag (a bare confusion vector with no legitimate use)";
                return false;
            }

            string normalized = Normalize(rawTag);
            if (!_registry.TryGetValue(normalized, out Bucket bucket))
            {
                bucket = new Bucket { CanonicalTag = rawTag, FirstClaimant = actorHost ?? "" };
                bucket.Members.Add(portalUid);
                _registry[normalized] = bucket;
                return true;
            }

            bucket.Members.Add(portalUid);

            if (bucket.CanonicalTag == rawTag)
            {
                return true; // identical spelling to what's already registered - not a confusable variant.
            }

            if (string.IsNullOrEmpty(actorHost) || actorHost == bucket.FirstClaimant)
            {
                return true; // the same account (or an unattributable change we can't penalise) re-spelling their own tag.
            }

            reason = $"tag is confusingly close to the existing '{bucket.CanonicalTag}' network (first claimed by a different account)";
            return false;
        }

        private static string Normalize(string tag)
        {
            var sb = new StringBuilder(tag.Length);
            foreach (char c in tag)
            {
                if (c == '​' || c == '‌' || c == '‍' || c == '﻿')
                {
                    continue; // zero-width space/joiners/BOM - invisible confusion characters, never meaningful payload.
                }
                sb.Append(c);
            }
            return sb.ToString().Trim().ToLowerInvariant();
        }
    }
}
