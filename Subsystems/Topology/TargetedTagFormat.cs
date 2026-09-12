namespace TortalPortalLite.Subsystems.Topology
{
    /// <summary>
    /// Builds this domain's own server-authored destination tags. Every sigil used here is already
    /// reserved by Core/Data/TagCodec.cs's own table ('&gt;' named-NPC/trader/location shorthand, '@'
    /// biome/region shorthand, both explicitly assigned to the "targeted domain") - this file adds no
    /// new sigil, it only builds the payload text that follows one.
    ///
    /// Deliberately does NOT route these through TagCodec.TryFormat's 10-character cap: that cap
    /// protects the PLAYER-TYPABLE budget (a vanilla client's own in-world retag widget), but #178's own
    /// verified howItWorks is explicit that a server-authored descriptive tag is not subject to it ("no
    /// client-side 10-char cap applies to server writes; rich text is stripped"), and several catalog
    /// options' own worked examples exceed 10 characters (">Home: Rohan", ">Back to Rohan"). A player
    /// can still retype something shorter over it at will; TargetedPhantomPortalFactory's maintenance
    /// tick then treats that retag as an opt-out rather than fighting it.
    /// </summary>
    public static class TargetedTagFormat
    {
        public const char NamedLocationSigil = '>';
        public const char BiomeSigil = '@';

        public static string Named(string label) => NamedLocationSigil + Truncate(label);
        public static string Biome(string label) => BiomeSigil + Truncate(label);

        /// <summary>A generous but finite ceiling - Localization/UI rendering has practical limits even though the ZDO field itself does not.</summary>
        public static string Truncate(string label, int maxLen = 40)
        {
            if (string.IsNullOrEmpty(label))
            {
                return "";
            }
            return label.Length <= maxLen ? label : label.Substring(0, maxLen);
        }
    }
}
