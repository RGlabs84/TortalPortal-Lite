using System;
using System.Linq;
using TortalPortalLite.Core.Data;

namespace TortalPortalLite.Subsystems.Enforcement
{
    /// <summary>
    /// #51 Reserved Tag Namespace. A configured set of prefixes/exact names only the server/admins may
    /// write - enforced by the Tag Watchdog's policy chain, which asks this class first (before the
    /// Portal ACL chain even runs) whenever a proposed tag falls in the reserved space.
    ///
    /// Matching is deliberately ordinal/case-sensitive/whitespace-significant, mirroring vanilla's own
    /// `Game.ConnectPortals` tag comparison (bare `string !=`, :100601) - `Hub`/`hub`/`Hub ` are three
    /// different networks under vanilla's own rules, so a reserved-namespace check that normalised case
    /// would both under- and over-match relative to what actually drives pairing.
    ///
    /// Note this deliberately does NOT special-case TagCodec's own '!' admin sigil - that table already
    /// documents '!' as "never player-writable by convention... NetworkReassertEngine treats a
    /// client-authored '!' tag as unauthorized and reverts it" - this engine is what actually performs
    /// that reversion for the access domain's own reserved-namespace policy, and the default
    /// AccessConfig.ReservedPrefixes ships with '!' included precisely so the convention has a real
    /// enforcer rather than just a comment.
    /// </summary>
    public static class AccessReservedNamespaceEngine
    {
        public static bool IsReserved(string tag)
        {
            if (string.IsNullOrEmpty(tag))
            {
                return false;
            }

            foreach (string prefix in SplitConfig(AccessConfig.ReservedPrefixes?.Value))
            {
                if (prefix.Length > 0 && tag.StartsWith(prefix, StringComparison.Ordinal))
                {
                    return true;
                }
            }
            foreach (string exact in SplitConfig(AccessConfig.ReservedExactNames?.Value))
            {
                if (string.Equals(tag, exact, StringComparison.Ordinal))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>True if <paramref name="actorPlatformId"/> may write a reserved tag - admins and the server's own engines only.</summary>
        public static bool ActorMayWriteReserved(string actorPlatformId)
        {
            return !string.IsNullOrEmpty(actorPlatformId) && ZNet.instance != null && ZNet.instance.IsAdmin(actorPlatformId);
        }

        private static string[] SplitConfig(string? csv)
        {
            return string.IsNullOrWhiteSpace(csv)
                ? Array.Empty<string>()
                : csv.Split(',').Select(s => s.Trim()).Where(s => s.Length > 0).ToArray();
        }
    }
}
