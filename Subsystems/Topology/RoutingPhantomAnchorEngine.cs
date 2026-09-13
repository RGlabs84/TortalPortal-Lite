using System;
using System.Collections.Generic;
using UnityEngine;
using TortalPortalLite.Core;
using TortalPortalLite.Core.Data;

namespace TortalPortalLite.Subsystems.Topology
{
    /// <summary>Admin declaration for one standing phantom anchor (routing.json section "phantomAnchors") - a named destination point with no player-built portal behind it, for #29 Sealed Gates / #225/#227 Parked Terminals to point at by position.</summary>
    public sealed class RoutingPhantomAnchorDefinition
    {
        public string Name = "";
        public RoutingPosition Position = new RoutingPosition();
        public float RotationY = 0f;

        /// <summary>false (default): a real, visible portal-prefab anchor, enumerable by PortalCensus like any other portal. true: an invisible prefab-0 anchor - a legal teleport target with no client-instantiated GameObject.</summary>
        public bool Void = false;
    }

    /// <summary>
    /// #24 Phantom Anchor Fabrication. `TeleportWorld.Teleport` performs zero prefab validation on its
    /// destination (:143517-143549) - it only reads GetPosition()/GetRotation() off whatever ZDO the
    /// portal's Portal connection points at. So a server-fabricated ZDO with no player-built structure
    /// behind it is a legal arrival point for a 100% vanilla client, and every other dynamic-routing
    /// engine in this domain that needs a destination nobody physically built (Rotating Hub's stops
    /// that aren't real player portals, Randomised Roguelike Re-Roll, Moving Destination Anchor,
    /// World-State muster points, ...) fabricates one through here rather than re-deriving the recipe.
    ///
    /// `ZDOMan.CreateNewZDO(Vector3, int)` (:76674) does two things and NEITHER is what a first read
    /// suggests: the prefabHash argument is used ONLY to decide, once, whether `AddIfPortal` (:77732)
    /// registers the new ZDO as a portal - it is never written to the ZDO's own prefab field. And the
    /// ZDO it hands back may be a RECYCLED instance from ZDOPool (ZDO.Reset, :73536-73549, clears
    /// revisions/position/rotation/Valid but deliberately - confirmed by reading it - does NOT clear
    /// m_prefab), so trusting CreateNewZDO's own "prefabHashIn==0 -&gt; fall back to the object's
    /// existing GetPrefab()" branch on a pooled object can silently resurrect a stale portal hash from
    /// whatever this exact ZDO instance was last used for. Both anchor flavours below therefore ALWAYS
    /// pass a definite, non-zero, non-portal registration hash into CreateNewZDO (so AddIfPortal's
    /// membership test never depends on recycled state) and then call ZDO.SetPrefab explicitly
    /// afterward with the value that should actually be served to clients - replicating
    /// `ZNetView.Awake`'s fabrication block (:82613-82620) field-for-field: Persistent, Type, Distant,
    /// SetPrefab, SetRotation.
    ///
    /// Two flavours:
    ///  - PORTAL anchor: final prefab is a real entry from `Game.instance.PortalPrefabHash`
    ///    (PortalRegistry) - always resident, saved in the ChunkPortal file, enumerated by
    ///    PortalCensus/GetPortalList, and instantiates a real visible portal mesh for any client within
    ///    ~96m (ZNetScene.PointInsideActiveArea). Use where players should see and reuse the arrival
    ///    point.
    ///  - VOID anchor: final prefab is forced to 0. `ZNetScene.CreateObject` early-returns null for
    ///    prefab 0 with NO warning (:81999-82002, verified directly) - the ZDO exists, is saved (once
    ///    Persistent is set here) and is a legal teleport target, but no client ever instantiates a
    ///    GameObject for it. Reads as "arrived at empty ground". The registration hash used to satisfy
    ///    AddIfPortal's non-portal test is a fixed, private, stable-hashed string - astronomically
    ///    unlikely to collide with any real prefab hash, same trust model PortalKeys.cs already uses
    ///    for its own key hashes.
    /// </summary>
    public static class RoutingPhantomAnchorEngine
    {
        private static readonly int VoidRegistrationHash = "tortalportallite_routing_void_anchor_v1".GetStableHashCode();
        private static int _lastRegistryVersion = -1;
        private static List<RoutingPhantomAnchorDefinition> _declared = new List<RoutingPhantomAnchorDefinition>();

        /// <summary>
        /// Provisions every routing.json "phantomAnchors" declaration: a named destination position with
        /// no player-built portal behind it. Idempotent and one-shot per declared name - once a real
        /// portal (fabricated or otherwise) resolves at that position via PortalCensus, this leaves it
        /// alone; the fabricated anchor is Persistent, so it survives restarts without needing to be
        /// re-created. This is what makes #24 usable on its own: an admin names a point in routing.json
        /// and it becomes a real destination other declarations (#29 sealed gates, #225/#227 parked
        /// terminals) can resolve by position, exactly as this class's own header describes.
        /// </summary>
        public static void OnUpdate(float dt)
        {
            if (ZDOMan.instance == null)
            {
                return;
            }
            if (RoutingManagedPortalRegistry.Version == _lastRegistryVersion)
            {
                return;
            }
            _lastRegistryVersion = RoutingManagedPortalRegistry.Version;
            _declared = RoutingManagedPortalRegistry.Section<RoutingPhantomAnchorDefinition>("phantomAnchors");

            foreach (RoutingPhantomAnchorDefinition def in _declared)
            {
                if (string.IsNullOrEmpty(def.Name) || !RoutingPairingAuthorityEngine.TryClaim(def.Position, $"phantomanchor:{def.Name}"))
                {
                    continue;
                }
                if (RoutingWriteOps.ResolveLive(def.Position) != null)
                {
                    continue; // already provisioned (or a player built a real portal there) - nothing to do
                }

                Quaternion rot = Quaternion.Euler(0f, def.RotationY, 0f);
                ZDO? anchor = def.Void ? FabricateVoidAnchor(def.Position.ToVector3(), rot) : FabricatePortalAnchor(def.Position.ToVector3(), rot);
                if (anchor == null)
                {
                    PortalDebug.LogWarning($"[RoutingPhantomAnchorEngine] failed to provision declared anchor '{def.Name}'.");
                }
            }
        }

        /// <summary>Fabricates a real, visible, always-resident portal-prefab anchor at <paramref name="pos"/>/<paramref name="rot"/>. Returns null if no portal prefab hash is known yet (PortalRegistry not populated - call after OnWorldReady).</summary>
        public static ZDO? FabricatePortalAnchor(Vector3 pos, Quaternion rot)
        {
            if (PortalRegistry.PrefabHashes.Count == 0)
            {
                PortalDebug.LogWarning("[RoutingPhantomAnchorEngine] cannot fabricate a portal anchor - PortalRegistry has no known portal prefab hash yet.");
                return null;
            }
            int prefabHash = PortalRegistry.PrefabHashes[0];
            return Fabricate(pos, rot, registrationHash: prefabHash, finalPrefabHash: prefabHash);
        }

        /// <summary>Fabricates an invisible (prefab-0), non-portal anchor ZDO - a legal teleport target that no client ever instantiates a GameObject for.</summary>
        public static ZDO? FabricateVoidAnchor(Vector3 pos, Quaternion rot)
        {
            return Fabricate(pos, rot, registrationHash: VoidRegistrationHash, finalPrefabHash: 0);
        }

        private static ZDO? Fabricate(Vector3 pos, Quaternion rot, int registrationHash, int finalPrefabHash)
        {
            if (ZDOMan.instance == null)
            {
                return null;
            }
            try
            {
                ZDO zdo = ZDOMan.instance.CreateNewZDO(pos, registrationHash);
                zdo.Persistent = true;
                zdo.Type = ZDO.ObjectType.Default;
                zdo.Distant = false;
                zdo.SetPrefab(finalPrefabHash);
                zdo.SetRotation(rot);
                return zdo;
            }
            catch (Exception ex)
            {
                PortalDebug.LogError($"[RoutingPhantomAnchorEngine] fabrication failed at {pos:F0}: {ex.GetType().Name}: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Destroys a previously-fabricated anchor. `ZDOMan.DestroyZDO` is a silent no-op unless the
        /// server owns the ZDO (:76929-76935, verified: `if (zdo.IsOwner()) m_destroySendList.Add(...)`
        /// else nothing happens at all) - SetOwner first, always.
        /// </summary>
        public static void Destroy(ZDO zdo)
        {
            if (zdo == null || !zdo.IsValid() || ZDOMan.instance == null)
            {
                return;
            }
            try
            {
                zdo.SetOwner(ZDOMan.GetSessionID());
                ZDOMan.instance.DestroyZDO(zdo);
            }
            catch (Exception ex)
            {
                PortalDebug.LogError($"[RoutingPhantomAnchorEngine] destroy failed for {zdo.m_uid}: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }
}
