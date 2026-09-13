using System;
using TortalPortalLite.Core;
using TortalPortalLite.Core.Data;

namespace TortalPortalLite.Subsystems.WorldOps
{
    /// <summary>
    /// #133 The Tag As A Public Broadcast Surface. ZDOVars.s_tag is an uncapped, unfiltered,
    /// server-writable string (ZDO.Set(int,string) performs no ownership check, :73721) that a vanilla
    /// client renders live: TeleportWorld.GetHoverText (:143453-143463) concatenates the tag directly
    /// INTO the Localization.Localize() input string BEFORE localizing, which is what makes three things
    /// possible from a pure ZDO write:
    ///  (1) a localization token embedded in the tag (e.g. "$piece_portal_connected") resolves in the
    ///      reading player's own language;
    ///  (2) a newline character produces a genuine multi-line hover panel - the tag is a small text box,
    ///      not a single line;
    ///  (3) StringExtensionMethods.RemoveRichTextTags' regex (assembly_utils_SERVER.decompiled.cs
    ///      :6700-6703, `Regex.Replace(text, "&lt;[/a-zA-Z0-9= \"'#;:()$_-]*?&gt;", "")`) makes one
    ///      forward, non-re-scanning pass, so the nested-tag input "&lt;&lt;color=red&gt;color=red&gt;"
    ///      survives as exactly "&lt;color=red&gt;" - real TextMeshPro markup smuggled past the strip.
    ///
    /// The 10-character cap is CLIENT UI ONLY (TextInput.instance.RequestText(..., 10) at :143482) -
    /// there is no server-side limit, but this class still enforces vanilla's TagCodec 10-char budget by
    /// default for anything NOT explicitly requesting the signage/multi-line path, since a
    /// vanilla-client-side retag of a long server tag truncates and saves back a mutilated string
    /// (TextInput.Show sets characterLimit=10 BEFORE assigning the existing text, :61515-61522) -
    /// #133's own failure mode 3.
    ///
    /// Persistence trap (#133's own citation): a tag-only write does NOT dirty the portal chunk -
    /// DirtyPortalObjects is set only by SetConnection/UpdateConnection, AddIfPortal and
    /// HandleDestroyedZDO, never by a bare tag Set - so every write here explicitly calls
    /// ZDOMan.instance.SetDirtyPortals() afterward, never relying on PortalOwnership.ClaimAndWrite's own
    /// revision-tie write to imply it (that bumps SchemaVersion, an ordinary ZDO int field, not the
    /// portal-chunk dirty flag).
    /// </summary>
    public static class WildcardATagBroadcastEngine
    {
        /// <summary>
        /// Writes arbitrary signage text (multi-line, up to <paramref name="maxLength"/> characters, no
        /// 10-char clamp) as this portal's s_tag. Refuses on a NetworkReassertEngine-managed portal
        /// (task rule #5) since the tag IS the pairing key there. Intended for standalone/unmanaged
        /// display portals (hub labels, countdowns, status lines), not networked pairs.
        /// </summary>
        public static bool TrySetSignage(ZDO portal, string text, int maxLength = 200)
        {
            if (portal == null || !portal.IsValid() || ZDOMan.instance == null)
            {
                return false;
            }
            if (WildcardAWriteOps.IsNetworkManaged(portal))
            {
                PortalDebug.LogWarning($"[WildcardATagBroadcastEngine] refusing direct tag write on {portal.m_uid} - it is NetworkReassertEngine-managed; route display text through a network's declared Tag instead.");
                return false;
            }
            text ??= "";
            if (text.Length > maxLength)
            {
                text = text.Substring(0, maxLength);
            }
            if (portal.GetString(ZDOVars.s_tag, "") == text)
            {
                return true; // already correct - idempotent, no wasted write
            }

            PortalOwnership.ClaimAndWrite(portal, z => z.Set(ZDOVars.s_tag, text));
            ZDOMan.instance.SetDirtyPortals(); // #133's own persistence trap - a tag-only write never dirties the portal chunk on its own.
            return true;
        }

        /// <summary>Vanilla-safe variant: clamps to TagCodec's own 10-character budget so a player who opens the retag dialog on this portal never truncates/corrupts it further.</summary>
        public static bool TrySetShortTag(ZDO portal, string text) => TrySetSignage(portal, text, TagCodec.MaxTagLength);

        /// <summary>
        /// #133's own nested-tag-smuggling recipe. RemoveRichTextTags' regex
        /// (`&lt;[/a-zA-Z0-9= \"'#;:()$_-]*?&gt;`, assembly_utils_SERVER.decompiled.cs :6700-6703)
        /// excludes '&lt;' from its own character class and makes one forward, non-re-scanning pass, so
        /// for input "&lt;&lt;color=red&gt;color=red&gt;" no match starts at index 0 (the lazy class
        /// can't consume the second '&lt;'), the match "&lt;color=red&gt;" starting at index 1 gets
        /// removed, and the untouched leftovers either side of that match - the orphaned leading '&lt;'
        /// plus the untouched trailing "color=red&gt;" - concatenate back into exactly
        /// "&lt;color=red&gt;", which TextMeshPro then renders as real markup. This method returns the
        /// FULL pre-strip sequence ready to embed verbatim in a tag - do not truncate or otherwise alter
        /// its return value, or the reconstruction breaks.
        /// </summary>
        public static string SmuggleRichText(string tagBody) => $"<<{tagBody}>{tagBody}>";

        /// <summary>Builds a genuine multi-line hover panel body - a plain newline is all TeleportWorld.GetHoverText needs (#133's own citation 2).</summary>
        public static string BuildMultiLine(params string[] lines) => string.Join("\n", lines ?? Array.Empty<string>());
    }
}
