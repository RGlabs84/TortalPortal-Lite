namespace TortalPortalLite.Subsystems.WorldOps
{
    /// <summary>
    /// Mod-private ZDO key hashes for the wildcard cluster B domain only (motion/companion/world-event/
    /// Sector-Zero mechanisms) - NOT added to the shared Core/Data/PortalKeys.cs, which is an
    /// orchestrator-only edit point between waves (four agents are writing new engines concurrently this
    /// wave). Same convention Subsystems/Topology/TargetedZdoKeys.cs and
    /// Subsystems/WorldOps/WildcardAZdoKeys.cs already use for their own domains. Every string is
    /// prefixed "tplwc_b_" - distinct from every other domain's own prefix ("tpl_", "TPL_", "tplwc_a_")
    /// so a stable-hash collision would require an actual string collision, not just a shared prefix.
    /// </summary>
    public static class WildcardBZdoKeys
    {
        // --- #136 Portal As Trigger, RPC As Transport ---

        /// <summary>1 on a portal ZDO this domain has been told to treat as a "waygate": the vanilla
        /// Connection field is ignored for it and arrival is instead driven by WildcardBTriggerTransportEngine's
        /// own position poll + RPC_TeleportTo. Never touches vanilla's own Connection/s_tag semantics.</summary>
        public static readonly int Waygate = "tplwc_b_waygate".GetStableHashCode();

        // --- #131 Ephemeral Event Gates / #240 Ambush Gates shared bookkeeping ---

        /// <summary>Free-form label for why this domain's maintenance pass should not touch a given
        /// ZDO's Connection field this tick (e.g. "ambush-locked") - read by nothing outside this
        /// domain's own engines, purely a same-file coordination flag between #131/#240.</summary>
        public static readonly int GateHoldReason = "tplwc_b_gateholdreason".GetStableHashCode();

        // --- LoadFields overrides (vanilla's own client-honoured "<TypeName>.<PublicField>" key format,
        //     ZNetView.LoadFields SERVER decompile :82669-82720 - NOT mod-private data, just named here so
        //     #235 Phantom Survival spells the same literal keys every time it hardens a fabricated ZDO
        //     this domain minted). ---

        public const string HasFields = "HasFields";
        public const string HasFieldsWearNTear = "HasFieldsWearNTear";
        public const string WearNTearNoSupportWear = "WearNTear.m_noSupportWear";
        public const string WearNTearNoRoofWear = "WearNTear.m_noRoofWear";
    }
}
