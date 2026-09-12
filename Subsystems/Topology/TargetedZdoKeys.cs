namespace TortalPortalLite.Subsystems.Topology
{
    /// <summary>
    /// Single source of truth for the `targeted` domain's OWN mod-private ZDO keys - deliberately a
    /// domain-local file, NOT an addition to Core/Data/PortalKeys.cs. PortalKeys.cs's own header says
    /// adding a key there is an orchestrator action between build waves (three agents are editing this
    /// codebase concurrently right now), and every key below is already named verbatim, by string
    /// literal, in the wave1_targeted.json catalog text this file implements (options #178/#179's own
    /// howItWorks: `z.Set("TPL_phantom", 1)`, `z.Set("TPL_kind", kind)`, `z.Set("TPL_anchor",1)`) - so
    /// there is nothing to coordinate: the literal strings are already frozen by the catalog itself.
    /// They use a distinct "TPL_" (uppercase) prefix specifically so they are visually and hash-wise
    /// disjoint from PortalKeys.cs's own "tpl_&lt;group&gt;_&lt;field&gt;" convention, and from anything
    /// the topologies/routing agents might independently pick for their own domains.
    ///
    /// These are written with the plain string overload of ZDO.Set/Get (never routed through
    /// PortalKeys.cs's int-hash fields) because that is what the catalog's own howItWorks text
    /// specifies; PortalOwnership.ClaimAndWrite is still the only path that ever writes them.
    /// </summary>
    public static class TargetedZdoKeys
    {
        // --- #178 Phantom Destination Portal ---

        /// <summary>1 on every phantom ZDO this domain minted - the marker every maintenance/reap/purge pass filters on.</summary>
        public const string Phantom = "TPL_phantom";

        /// <summary>
        /// Free-form small descriptor of WHAT a phantom (or anchor) is for, e.g. "boss:Bonemass",
        /// "bed:70000123", "biome:Plains". Doubles as this domain's position-keyed-registry substitute:
        /// since ZDOIDs renumber every world load but a phantom's own ZDO (and its reciprocal Connection
        /// back to the real source portal) persists across save/load, re-deriving "what is this phantom
        /// for and which source does it belong to" from the phantom's own Kind + Connection field at
        /// OnWorldReady avoids needing a second, parallel position-keyed file for anything the mod
        /// itself computes (as opposed to what an admin declares - see TargetedRouteStore.cs).
        /// </summary>
        public const string Kind = "TPL_kind";

        // --- #179 Invisible Anchor ZDO ---

        /// <summary>1 on every non-portal anchor ZDO this domain minted (prefab 0 or "TPL_Anchor").</summary>
        public const string Anchor = "TPL_anchor";

        /// <summary>Stable name for the mod-private prefab hash used when an anchor is NOT created with prefab 0 (see TargetedAnchorFactory's prefab-0-vs-named tradeoff).</summary>
        public const string AnchorPrefabName = "TPL_Anchor";

        // --- LoadFields overrides (vanilla's OWN client-honoured "&lt;TypeName&gt;.&lt;PublicField&gt;" key format,
        //     ZNetView.LoadFields SERVER decompile :82669-82720 - NOT mod-private data, just named here so
        //     every engine that wants "this phantom can't be hammered down" spells the same literal keys). ---

        public const string HasFields = "HasFields";
        public const string HasFieldsPiece = "HasFieldsPiece";
        public const string PieceCanBeRemoved = "Piece.m_canBeRemoved";
        public const string HasFieldsTeleportWorld = "HasFieldsTeleportWorld";
        public const string TeleportWorldExitDistance = "TeleportWorld.m_exitDistance";
        public const string TeleportWorldAllowAllItems = "TeleportWorld.m_allowAllItems";
    }
}
