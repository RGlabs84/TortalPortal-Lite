using System;
using UnityEngine;
using TortalPortalLite.Core;
using TortalPortalLite.Core.Data;
using TortalPortalLite.Subsystems.Foundations;

namespace TortalPortalLite.Subsystems.Topology
{
    /// <summary>
    /// #8 Anchor-Terminated One-Way / Dead Drop - "the strongest primitive in the whole catalogue,
    /// because the anchor is never iterated by Game.ConnectPortals". Shared fabrication primitive used
    /// directly by #8 itself and as a documented prerequisite by #9 (tree root terminator), #10
    /// (vestibule arrival anchor), #18 (Black-Hole Sink) and #21 (asymmetric round-trip terminator).
    ///
    /// Recipe, exactly ZNetView.Awake's fabrication block (SERVER decompile :82613-82620), replicated
    /// here because there is no live GameObject/ZNetView for a server-fabricated ZDO to run that Awake
    /// on: `ZDOMan.instance.CreateNewZDO(pos, hash)` (:76674-76692, which already routes through
    /// AddIfPortal :77732-77752 - harmless no-op for a non-portal hash) then, directly on the returned
    /// ZDO, `Persistent = true; Type = ZDO.ObjectType.Default; Distant = false; SetPrefab(hash);
    /// SetRotation(rot);` - omitting SetPrefab leaves m_prefab at its -1/0 default and the requesting
    /// client's CreateObject fails to resolve a prefab for it; omitting Persistent means
    /// AddObjectsPerChunk's persistence filter (:77572) drops it from the portal-chunk save file and it
    /// is gone on restart.
    ///
    /// The anchor is stamped with TopologiesKeys.AnchorMarker so a position-based re-scan (mandatory:
    /// ZDOIDs renumber on every world load, ZDO.Load :74552, so nothing here ever caches a ZDOID across
    /// a tick boundary) can tell "this is my anchor" apart from an unrelated object a player happens to
    /// have placed nearby, and reuses an already-fabricated anchor instead of leaking a duplicate.
    /// Never destroyed proactively - if one goes missing (player-side griefing is not possible since
    /// anchors carry no interact/destroy path a vanilla client exposes, but a future engine bug or manual
    /// console `devcommands` cleanup could still remove one) the next EnsureAnchor call simply
    /// re-fabricates it, and every consumer re-resolves the anchor's current ZDOID fresh every reassert
    /// tick rather than caching the old one - see TopologiesShapeEngine.
    /// </summary>
    public static class TopologiesFabricationEngine
    {
        private const float ReuseSearchRadius = 1.5f;

        /// <summary>
        /// Idempotent: returns the ZDOID of an existing marked anchor near <paramref name="spec"/>'s
        /// position, or fabricates one. <paramref name="tag"/> is written (ZDOVars.s_tag) so the anchor
        /// satisfies whichever shape's pass-1 test needs to see it non-null/tag-matched/non-None; the
        /// anchor's own connection is set to itself (a self-loop the anchor's own tag group never has to
        /// answer for, because the anchor is never enumerated by GetPortalList()).
        /// </summary>
        public static bool TryEnsureAnchor(TopologyAnchorSpec spec, string tag, out ZDOID anchorUid)
        {
            anchorUid = ZDOID.None;
            if (ZDOMan.instance == null || spec == null)
            {
                return false;
            }

            Vector3 pos = spec.Position.ToVector3();
            ZDO? existing = FindMarkedAnchorNear(pos);
            ZDO zdo;

            if (existing != null)
            {
                zdo = existing;
            }
            else
            {
                string prefabName = string.IsNullOrEmpty(spec.PrefabName) ? (TopologiesConfig.DefaultAnchorPrefab?.Value ?? "guard_stone") : spec.PrefabName;
                if (!TryResolvePrefabHash(prefabName, out int prefabHash))
                {
                    PortalDebug.LogError($"[TopologiesFabricationEngine] anchor prefab '{prefabName}' does not resolve via ZNetScene - refusing to fabricate a broken anchor at {pos}. Configure DefaultAnchorPrefab (or this shape's own Anchor.PrefabName) to a real, currently-loaded prefab.");
                    return false;
                }

                zdo = ZDOMan.instance.CreateNewZDO(pos, prefabHash);
                if (zdo == null)
                {
                    return false;
                }
                zdo.Persistent = true;
                zdo.Type = ZDO.ObjectType.Default;
                zdo.Distant = false;
                zdo.SetPrefab(prefabHash);
                zdo.SetRotation(Quaternion.Euler(0f, spec.FacingYaw, 0f));
                PortalOwnership.ClaimAndWrite(zdo, z => z.Set(TopologiesKeys.AnchorMarker, 1));
                PortalDebug.LogAlways($"[TopologiesFabricationEngine] fabricated anchor '{prefabName}' at {pos}.");
            }

            anchorUid = zdo.m_uid;

            bool tagWrong = zdo.GetString(ZDOVars.s_tag, "") != tag;
            bool connWrong = zdo.GetConnectionZDOID(ZDOExtraData.ConnectionType.Portal) != zdo.m_uid;
            if (tagWrong || connWrong)
            {
                PortalOwnership.ClaimAndWrite(zdo, z =>
                {
                    z.Set(ZDOVars.s_tag, tag);
                    z.SetConnection(ZDOExtraData.ConnectionType.Portal, z.m_uid);
                });
            }
            return true;
        }

        /// <summary>
        /// Portal Bank / Departures Hall (#11) "provisioning": fabricates a REAL portal-prefab ZDO (so it
        /// joins ZDOMan.GetPortalList() normally, unlike an Anchor) at <paramref name="pos"/> if
        /// PortalCensus has nothing there yet. Uses whichever prefab hash Core/Data/PortalRegistry
        /// resolved from vanilla's own Game.instance.PortalPrefabHash - never a hardcoded prefab name -
        /// so this works whether the server runs the base game's "portal_wood" or a DLC/reskinned
        /// equivalent. Gated by TopologiesConfig.AutoProvisionPortals globally, in addition to the
        /// per-shape opt-in flag callers must already check before calling this.
        /// </summary>
        public static bool TryProvisionPortal(Vector3 pos, float facingYaw)
        {
            if (ZDOMan.instance == null)
            {
                return false;
            }
            if (PortalCensus.TryGetByPosition(pos, out _))
            {
                return false; // already there - nothing to provision
            }
            if (PortalRegistry.PrefabHashes.Count == 0)
            {
                PortalDebug.LogWarning("[TopologiesFabricationEngine] cannot provision a bank portal - PortalRegistry has not resolved any portal prefab hash yet (Game.instance not ready?).");
                return false;
            }

            int prefabHash = PortalRegistry.PrefabHashes[0];
            ZDO zdo = ZDOMan.instance.CreateNewZDO(pos, prefabHash);
            if (zdo == null)
            {
                return false;
            }
            zdo.Persistent = true;
            zdo.Type = ZDO.ObjectType.Default;
            zdo.Distant = false;
            zdo.SetPrefab(prefabHash);
            zdo.SetRotation(Quaternion.Euler(0f, facingYaw, 0f));
            PortalDebug.LogAlways($"[TopologiesFabricationEngine] provisioned a bank portal ('{(PortalRegistry.PrefabNames.Count > 0 ? PortalRegistry.PrefabNames[0] : prefabHash.ToString())}') at {pos}. PortalCensus will pick it up on its next scan.");
            return true;
        }

        private static ZDO? FindMarkedAnchorNear(Vector3 pos)
        {
            var nearby = ZdoSpatialQuery.FindNear(pos, ReuseSearchRadius);
            foreach (ZDO zdo in nearby)
            {
                if (zdo.IsValid() && zdo.GetInt(TopologiesKeys.AnchorMarker, 0) == 1)
                {
                    return zdo;
                }
            }
            return null;
        }

        private static bool TryResolvePrefabHash(string prefabName, out int hash)
        {
            hash = 0;
            if (string.IsNullOrEmpty(prefabName) || ZNetScene.instance == null)
            {
                return false;
            }
            GameObject prefab = ZNetScene.instance.GetPrefab(prefabName);
            if (prefab == null)
            {
                return false;
            }
            // Hash the RESOLVED prefab's own reported name, not the input string - matches exactly how
            // vanilla itself builds every prefab-hash table it owns (e.g. Game.PortalPrefabHash's own
            // population, "portalPrefab.name.GetStableHashCode()", :100031), so this can never diverge
            // from GetPrefab's own lookup even if prefabName differs from prefab.name only in case.
            hash = prefab.name.GetStableHashCode();
            return true;
        }
    }
}
