using System;

namespace TortalPortalLite.Core.Data
{
    /// <summary>
    /// Sole owner of vanilla's 10-character `s_tag` budget. `s_tag` is the ONE field a vanilla client
    /// can both write (via the in-world retag UI) and always see (TeleportWorld.GetHoverText renders it
    /// live) - catalog option #177 ("The Dial") and 23 others encode a whole command grammar into it.
    /// Nine independent authors each inventing their own tag format would collide silently (a tag is
    /// just a string vanilla's own reconciler also reads for pairing - see NetworkReassertEngine, #69).
    /// This class is the only place that decides what a leading character in a tag MEANS.
    ///
    /// RULE: adding a new sigil is an orchestrator action between build waves, same as PortalKeys.cs -
    /// see the implementation plan's "shared ZDO key / tag-budget namespace" hazard.
    ///
    /// Sigil table (first character; 9 remain for the payload after it):
    ///   (none)   - a bare tag: vanilla's own pairing key, untouched user text, or a managed network's
    ///              plain name (NetworkReassertEngine writes these with no sigil, matching what vanilla
    ///              itself has always accepted, so an admin's existing world is unaffected).
    ///   '#'      - a player-authored destination alias, e.g. "#home" (targeted/UX domain).
    ///   '>'      - a named-NPC/trader/location shorthand, e.g. ">Haldor" (targeted domain).
    ///   '@'      - a biome/region shorthand, e.g. "@meadows" (targeted domain).
    ///   ':'      - reserved for a namespaced index form, e.g. "bank:3" (topology domain - portal banks).
    ///   '!'      - reserved for an admin/lockdown-only marker (access/lockdown domains) - never
    ///              player-writable by convention (nothing stops a client writing it; NetworkReassertEngine
    ///              treats a client-authored '!' tag as unauthorized and reverts it, same as any other
    ///              unmanaged retag - see #69's ownership rule).
    /// </summary>
    public static class TagCodec
    {
        public const int MaxTagLength = 10;

        public static bool TryFormat(char sigil, string payload, out string tag)
        {
            tag = sigil == default ? payload : sigil + payload;
            return tag.Length <= MaxTagLength;
        }

        /// <summary>True if the tag starts with the given sigil and still has room for at least one payload character.</summary>
        public static bool HasSigil(string tag, char sigil)
        {
            return !string.IsNullOrEmpty(tag) && tag[0] == sigil && tag.Length > 1;
        }

        /// <summary>The payload after a one-character sigil (empty if the tag doesn't start with it).</summary>
        public static string PayloadAfter(string tag, char sigil)
        {
            return HasSigil(tag, sigil) ? tag.Substring(1) : string.Empty;
        }

        /// <summary>
        /// A namespaced index form like "bank:3" - name up to the LAST ':' (so a name may itself
        /// contain earlier characters freely), index after it. Returns false if there is no ':' or the
        /// suffix isn't a non-negative integer.
        /// </summary>
        public static bool TryParseIndexed(string tag, out string name, out int index)
        {
            name = string.Empty;
            index = -1;
            if (string.IsNullOrEmpty(tag)) return false;
            int sep = tag.LastIndexOf(':');
            if (sep < 0 || sep == tag.Length - 1) return false;
            string suffix = tag.Substring(sep + 1);
            if (!int.TryParse(suffix, out index) || index < 0) return false;
            name = tag.Substring(0, sep);
            return true;
        }

        public static string FormatIndexed(string name, int index)
        {
            string tag = $"{name}:{index}";
            if (tag.Length > MaxTagLength)
            {
                throw new ArgumentException($"[TagCodec] '{tag}' ({tag.Length} chars) exceeds the {MaxTagLength}-char vanilla tag budget.");
            }
            return tag;
        }
    }
}
