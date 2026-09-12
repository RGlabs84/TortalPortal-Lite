namespace TortalPortalLite.Subsystems.Topology
{
    /// <summary>
    /// Domain-local placeholder ZDO keys for the Topologies engines (Subsystems/Topology/*). These are
    /// NOT added to Core/Data/PortalKeys.cs - that file's own rule ("adding a key here is an orchestrator
    /// action between build waves, not something a subagent does mid-wave") applies exactly here: three
    /// agents (topologies/routing/targeted) are editing this codebase concurrently right now, and
    /// PortalKeys.cs is a shared file none of us should write to mid-wave.
    ///
    /// Every key below is fully functional TODAY (computed the same way PortalKeys.cs computes its own -
    /// ZDOVars.GetStableHashCode() on a "tpl_&lt;group&gt;_&lt;field&gt;" string, so a collision is a
    /// collision of readable strings) - nothing here is a stub. Each is marked with a `NEEDS NEW KEY`
    /// comment so the orchestrator can move it into PortalKeys.cs (checking for a collision with whatever
    /// routing/targeted picked) before the next wave, at which point every reference in this folder
    /// should be repointed at the promoted constant and this file's copy retired.
    /// </summary>
    internal static class TopologiesKeys
    {
        /// <summary>
        /// NEEDS NEW KEY: TopologyTagStash, purpose: Tag-Scramble Lockdown (#17) stashes each portal's
        /// pre-scramble s_tag here before overwriting it with a per-portal-unique suffixed tag, so the
        /// original can be restored byte-for-byte on unlock. Written to the ZDO (round-trips through
        /// save automatically) rather than a mod-side file keyed by ZDOID, because ZDOIDs are renumbered
        /// on every world load (ZDO.Load :74552) - a ZDOID-keyed file would be worthless after a restart,
        /// exactly the catalog's own stated failure mode for this option.
        /// </summary>
        public static readonly int LockdownTagStash = "tpl_topology_lockdowntagstash".GetStableHashCode();

        /// <summary>
        /// NEEDS NEW KEY: TopologyRoute, purpose: Displaced Routing (#20) - the routing edge, expressed
        /// as "x;y;z" world position of the intended target (or the literal tokens "self"/"anchor"),
        /// stored ON the portal ZDO rather than only in this mod's own topologies.json, exactly as the
        /// catalog's howItWorks specifies ("arbitrary int-keyed ZDO strings persist through save and
        /// network automatically... invisible to the player and to any other mod"). A position string,
        /// not a ZDOID, for the same renumbering reason as LockdownTagStash above.
        /// </summary>
        public static readonly int DisplacedRoute = "tpl_topology_displacedroute".GetStableHashCode();

        /// <summary>
        /// NEEDS NEW KEY: TopologyFaction, purpose: Team/Faction Networks (#16) priority-1 override - an
        /// admin- or command-assigned faction id stamped directly on a portal, read before falling back
        /// to a roster lookup by s_creator or to the ward's permitted-player list. Optional: absence just
        /// means "fall through to the next resolution rule", so leaving this unset never breaks anything.
        /// </summary>
        public static readonly int FactionOverride = "tpl_topology_factionoverride".GetStableHashCode();

        /// <summary>
        /// NEEDS NEW KEY: TopologyAnchorMarker, purpose: stamped (value 1) on every ZDO this mod fabricates
        /// via TopologiesFabricationEngine (the Anchor-Terminated One-Way / Dead Drop primitive, #8, and
        /// its dependents #9/#10/#18/#21). Lets a re-scan-by-position (after a restart, when any locally
        /// cached ZDOID is worthless per ZDO.Load's renumbering) distinguish "this is my anchor" from "a
        /// player happens to have placed the same prefab at a nearby position" before adopting it.
        /// </summary>
        public static readonly int AnchorMarker = "tpl_topology_anchormarker".GetStableHashCode();

        /// <summary>
        /// NEEDS NEW KEY: TopologySinkStash, purpose: Black-Hole Sink (#18) reversible mode - stashes the
        /// pre-sink target's WORLD POSITION (never its ZDOID - renumbers on load) as "x;y;z" so a portal
        /// swallowed into a sink can be restored to its original destination without needing the tag-
        /// scramble stash mechanism.
        /// </summary>
        public static readonly int SinkPreRepointPos = "tpl_topology_sinkprerepointpos".GetStableHashCode();
    }
}
