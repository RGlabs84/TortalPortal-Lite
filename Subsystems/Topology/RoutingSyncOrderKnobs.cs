using TortalPortalLite.Core;

namespace TortalPortalLite.Subsystems.Topology
{
    /// <summary>
    /// #230 Sync-Order Knobs (ObjectType.Prioritized / Distant) - Marginal. The catalog's own verdict:
    /// "read this pass: Type=Prioritized only reorders ZDOs inside the peer's ordinary sector list and
    /// Distant=true only helps non-portal anchors in the far ring - neither substitutes for force-send or
    /// pre-streaming". This class exists purely to apply the ONE case the catalog confirms actually
    /// sticks: a server-owned, NEVER-client-instantiated void anchor (#24's non-portal fabrication,
    /// prefab 0) can have `ObjectType.Prioritized`/`Distant` set once at creation and they will never be
    /// reset, because `ZNetView.Awake`'s Type/Distant-from-Inspector reset (:82583-82590) only runs when
    /// an owning CLIENT instantiates a GameObject for the ZDO - which never happens for a prefab-0 ZDO
    /// (`ZNetScene.CreateObject` returns null immediately for prefab 0, :81999-82002). On a REAL portal
    /// prefab this would be silently undone the moment a nearby player's client becomes its owner
    /// (`ReleaseNearbyZDOS`, :76901-76927) - so this helper deliberately refuses to touch anything but a
    /// void (prefab-0) anchor, to avoid shipping a knob that looks like it does something on a real portal
    /// and does not.
    ///
    /// Practical effect, honestly scoped: `ObjectType.Prioritized` only changes ordering INSIDE
    /// `ServerSortSendZDOS`'s pass over a peer's own nearby-object sweep (:77288-77327) - it does nothing
    /// for anything force-sent or pre-streamed (those are inserted after sorting, :77239/:77275, or
    /// appended by a later pass entirely). `Distant=true` only matters once a peer's own near list has
    /// drained below 10 entries (:77229-77238) and only for a NON-portal anchor (portals never enter the
    /// distant ring at all, :77384-77404, merged separately by FindObjects, :77376). Used here as a
    /// small, one-time nudge for a void anchor placed inside a busy hub's own zone, nothing more.
    /// </summary>
    public static class RoutingSyncOrderKnobs
    {
        /// <summary>Marks a server-fabricated VOID anchor (prefab 0 - never call this on a real portal prefab) as high sync priority. No-ops (and logs) if the ZDO's prefab is non-zero.</summary>
        public static void MarkVoidAnchorPrioritized(ZDO zdo)
        {
            if (zdo == null || !zdo.IsValid())
            {
                return;
            }
            if (zdo.GetPrefab() != 0)
            {
                PortalDebug.LogWarning($"[RoutingSyncOrderKnobs] refused to mark {zdo.m_uid} Prioritized - its prefab is non-zero, so a client owner would silently reset this on next instantiation (ZNetView.Awake).");
                return;
            }
            zdo.SetType(ZDO.ObjectType.Prioritized);
        }

        /// <summary>Marks a server-fabricated VOID anchor as eligible for the distant sync ring - only meaningful once a peer's own near list has fewer than 10 entries left (ZDOMan.CreateSyncList, :77229-77238).</summary>
        public static void MarkVoidAnchorDistant(ZDO zdo)
        {
            if (zdo == null || !zdo.IsValid())
            {
                return;
            }
            if (zdo.GetPrefab() != 0)
            {
                PortalDebug.LogWarning($"[RoutingSyncOrderKnobs] refused to mark {zdo.m_uid} Distant - its prefab is non-zero, so a client owner would silently reset this on next instantiation (ZNetView.Awake).");
                return;
            }
            zdo.SetDistant(true);
        }
    }
}
