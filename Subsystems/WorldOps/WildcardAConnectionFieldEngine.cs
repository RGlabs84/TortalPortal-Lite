using TortalPortalLite.Core;

namespace TortalPortalLite.Subsystems.WorldOps
{
    /// <summary>
    /// #139 The Connection Field Beyond Portals - a pure utility engine (no ticking of its own) mapping
    /// out what the OTHER ZDOExtraData.ConnectionType values buy and cost, for every other wildcard-A
    /// engine to use safely rather than each re-deriving these facts.
    ///
    /// ConnectionType is a [Flags] byte enum: None=0, Portal=1, SyncTransform=2, Spawned=3, Target=0x10
    /// (:74747-74754). Portal/SyncTransform/Spawned OVERLAP (Spawned is bitwise Portal|SyncTransform) -
    /// only Target is a real independent flag - and a ZDO holds exactly ONE connection entry
    /// (ZDOExtraData.s_connections is Dictionary&lt;ZDOID, ZDOConnection&gt;, one readonly type/target pair
    /// per ZDO, :74794/:75524-75534). Writing ANY connection type onto a ZDO silently destroys whatever
    /// connection it already had (ZDOExtraData.SetConnection just replaces, :74951-74960) - this is the
    /// single most important guardrail this class provides: never write a Portal connection onto a ZDO
    /// this domain did not itself create/claim, since it may already be someone else's SyncTransform or
    /// Spawned link.
    /// </summary>
    public static class WildcardAConnectionFieldEngine
    {
        /// <summary>
        /// A rider's client writes SyncTransform (+ s_relPosHash/s_relRotHash/s_attachJointHash) onto
        /// their OWN character ZDO only when the parent changes (ZSyncTransform.OwnerSync,
        /// :87542-87571) - reading it here is the documented way to know a player is on a moving
        /// platform (ship, cart) without any client install. Not written every tick - it is a latch, so
        /// a false negative on a player who boarded before this engine started watching is expected
        /// until the next parent change.
        /// </summary>
        public static bool IsRidingSomething(ZDO characterZdo)
        {
            return characterZdo != null && characterZdo.IsValid()
                && characterZdo.GetConnectionZDOID(ZDOExtraData.ConnectionType.SyncTransform) != ZDOID.None;
        }

        /// <summary>
        /// Off-label but real world-governance primitive #139 documents: a CreatureSpawner whose Spawned
        /// connection points at ANY still-resident ZDO (`SpawnedCreatureStillExists` true) with
        /// `m_respawnTimeMinuts &lt;= 0f` never spawns again. Pointing it at a permanent, always-resident
        /// anchor (e.g. a WildcardAVoidAnchorEngine anchor) therefore permanently silences a fixed
        /// creature spawner from the server. Refuses if the target spawner is a portal (Portal/Spawned
        /// share bit 1, so a portal ZDO could otherwise be mistaken for a valid spawner-shaped target).
        /// </summary>
        public static bool TryPermanentlySuppressSpawner(ZDO spawnerZdo, ZDOID permanentAnchorId)
        {
            if (spawnerZdo == null || !spawnerZdo.IsValid() || permanentAnchorId == ZDOID.None)
            {
                return false;
            }
            if (Core.Data.PortalRegistry.IsPortalPrefabHash(spawnerZdo.GetPrefab()))
            {
                PortalDebug.LogWarning($"[WildcardAConnectionFieldEngine] refusing to write a Spawned connection onto {spawnerZdo.m_uid} - it is itself a portal prefab.");
                return false;
            }
            Core.Data.PortalOwnership.ClaimAndWrite(spawnerZdo, z => z.SetConnection(ZDOExtraData.ConnectionType.Spawned, permanentAnchorId));
            PortalDebug.LogAlways($"[WildcardAConnectionFieldEngine] wrote a permanent Spawned connection onto {spawnerZdo.m_uid} - this spawner will never respawn again while that target ZDO stays resident.");
            return true;
        }

        /// <summary>
        /// #139's own central safety rule, exposed so every other wildcard-A engine can check it before
        /// writing a Portal connection onto a ZDO it did not itself fabricate: true if the ZDO already
        /// carries a DIFFERENT connection type than the one about to be written (a write would silently
        /// clobber it).
        /// </summary>
        public static bool WouldClobberExistingConnection(ZDO zdo, ZDOExtraData.ConnectionType intended)
        {
            if (zdo == null || !zdo.IsValid())
            {
                return false;
            }
            // GetConnectionZDOID requires EXACT type equality (:75094-75102), so probe every other kind
            // explicitly rather than relying on a single "has any connection" test.
            foreach (ZDOExtraData.ConnectionType candidate in new[]
                     {
                         ZDOExtraData.ConnectionType.Portal,
                         ZDOExtraData.ConnectionType.SyncTransform,
                         ZDOExtraData.ConnectionType.Spawned,
                         ZDOExtraData.ConnectionType.Target
                     })
            {
                if (candidate == intended)
                {
                    continue;
                }
                if (zdo.GetConnectionZDOID(candidate) != ZDOID.None)
                {
                    return true;
                }
            }
            return false;
        }
    }
}
