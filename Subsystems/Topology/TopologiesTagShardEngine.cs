using System;
using System.Text.RegularExpressions;
using TortalPortalLite.Core.Data;

namespace TortalPortalLite.Subsystems.Topology
{
    /// <summary>
    /// #14 Invisible Tag Sharding via rich-text-stripped suffix - the shared encode/decode primitive
    /// consumed by #15 (Per-Player Private Networks), #16 (Team/Faction Networks) and #17 (Tag-Scramble
    /// Lockdown). Exploits a genuine, verified asymmetry between what ROUTES and what DISPLAYS:
    ///
    ///  - Routes on the RAW string: Game.ConnectPortals compares `GetString(s_tag)` with bare `!=`
    ///    (SERVER decompile :100601) and FindRandomUnconnectedPortal filters the same way (:100669) - no
    ///    trimming, no normalisation, no stripping anywhere in the routing path.
    ///  - Displays a STRIPPED string: TeleportWorld.GetHoverText runs the tag through
    ///    `text.RemoveRichTextTags()` (:143459), which is `Regex.Replace(text,
    ///    "&lt;[\/a-zA-Z0-9= \"'#;:()$_-]*?&gt;", "")` (assembly_utils_SERVER.decompiled.cs:6700-6703, read
    ///    directly and mirrored below so this engine can predict a vanilla client's hover text without
    ///    calling into TeleportWorld, which never runs server-side).
    ///
    /// So `base&lt;payload&gt;` hovers as exactly `base` on every vanilla client but is a distinct routing
    /// namespace from `base` itself and from `base&lt;otherPayload&gt;`. This is layered ON TOP of
    /// Core/Data/TagCodec.cs's own leading-sigil command-grammar convention, not a replacement for it -
    /// TagCodec owns the FIRST character of a tag; this engine owns a trailing "&lt;...&gt;" group, and the
    /// two compose (sigil, then base text, then shard suffix) as long as the combined length still fits
    /// TagCodec.MaxTagLength. Nothing here is added to TagCodec.cs itself - a shard suffix is not a
    /// sigil in that file's sense (a single leading command character); it is a suffix, always trailing,
    /// implemented entirely in this domain's own files.
    /// </summary>
    public static class TopologiesTagShardEngine
    {
        // Mirror of StringExtensionMethods.RemoveRichTextTags's own pattern (assembly_utils_SERVER.decompiled.cs:6700-6703).
        private static readonly Regex RichTextTag = new Regex("<[\\/a-zA-Z0-9= \"'#;:()$_-]*?>", RegexOptions.Compiled);

        private const string Alphabet = "0123456789abcdefghijklmnopqrstuvwxyz";

        /// <summary>What a vanilla client's hover text will show for <paramref name="rawTag"/> once RemoveRichTextTags runs - i.e. every "&lt;...&gt;" group removed, not just a trailing one.</summary>
        public static string StripForDisplay(string rawTag) => RichTextTag.Replace(rawTag ?? "", "");

        /// <summary>The portion of <paramref name="rawTag"/> before its FIRST "&lt;" - what this engine itself wrote as the visible base the last time it touched this tag. Equal to StripForDisplay for any tag this engine produced (which never embeds more than one suffix group), cheaper than running the regex.</summary>
        public static string BaseOf(string rawTag)
        {
            if (string.IsNullOrEmpty(rawTag))
            {
                return "";
            }
            int idx = rawTag.IndexOf('<');
            return idx < 0 ? rawTag : rawTag.Substring(0, idx);
        }

        /// <summary>True if <paramref name="rawTag"/> already carries the exact shard suffix for <paramref name="payload"/> (ordinal - Game.ConnectPortals' own comparison is bare `string !=`, :100601).</summary>
        public static bool HasShard(string rawTag, string payload) => !string.IsNullOrEmpty(rawTag) && rawTag.EndsWith("<" + payload + ">", StringComparison.Ordinal);

        /// <summary>
        /// Builds "base&lt;payload&gt;", truncating base (never payload - payload is what makes routing
        /// correct; a truncated base only makes the display name a little shorter, which the catalog's
        /// own citation already expects: "keep display names <= 5 chars, or accept the mod must
        /// re-apply the suffix after every player retag") so the total never exceeds
        /// TagCodec.MaxTagLength. Returns the input payload unsharded (no "&lt;&gt;" at all) if payload
        /// itself is empty - the "public/shared namespace" case every consumer below uses for a
        /// creator/faction id of 0 or "no assignment resolved".
        /// </summary>
        public static string FitAndShard(string baseDisplayTag, string payload)
        {
            baseDisplayTag ??= "";
            if (string.IsNullOrEmpty(payload))
            {
                return baseDisplayTag.Length > TagCodec.MaxTagLength ? baseDisplayTag.Substring(0, TagCodec.MaxTagLength) : baseDisplayTag;
            }
            string suffix = "<" + payload + ">";
            int roomForBase = TagCodec.MaxTagLength - suffix.Length;
            if (roomForBase < 0)
            {
                // Payload alone doesn't fit - truncate the payload as a last resort rather than refuse
                // to shard at all (an unsharded tag would fall into the bare/public namespace, which is
                // the one outcome every consumer of this method needs to avoid).
                int keep = Math.Max(0, payload.Length + roomForBase - 2); // -2 for the two bracket chars
                payload = payload.Substring(0, keep);
                suffix = "<" + payload + ">";
                roomForBase = TagCodec.MaxTagLength - suffix.Length;
            }
            string basePart = roomForBase <= 0 ? "" : (baseDisplayTag.Length > roomForBase ? baseDisplayTag.Substring(0, roomForBase) : baseDisplayTag);
            return basePart + suffix;
        }

        /// <summary>
        /// Folds an arbitrary long (a player id, a ZDOID's numeric parts, ...) into a short, budget-safe
        /// base-36 code. Deliberately lossy - the 10-char vanilla tag budget cannot fit a full 64-bit id
        /// AND a readable base name, so two different inputs CAN fold to the same code (documented, not
        /// hidden: every caller below explains what a collision means for it and why it self-heals).
        /// </summary>
        public static string ShortCode(long value, int chars)
        {
            chars = Math.Max(1, chars);
            ulong h = unchecked((ulong)value);
            h = (h ^ (h >> 32)) * 2654435761u; // Knuth-style fold+multiply, non-cryptographic on purpose
            char[] buf = new char[chars];
            for (int i = chars - 1; i >= 0; i--)
            {
                buf[i] = Alphabet[(int)(h % 36)];
                h /= 36;
            }
            return new string(buf);
        }

        /// <summary>Same fold as ShortCode, but over BOTH halves of a ZDOID (session + sequence), for Tag-Scramble Lockdown's "every portal must be globally unique, forever, this lock-cycle" requirement.</summary>
        public static string ShortCode(ZDOID uid, int chars) => ShortCode(unchecked((long)((ulong)(uint)uid.UserID << 32 | (uint)uid.ID)), chars);
    }
}
