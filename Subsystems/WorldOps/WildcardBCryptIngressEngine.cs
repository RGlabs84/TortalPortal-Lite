using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using TortalPortalLite.Core;
using TortalPortalLite.Core.Data;
using TortalPortalLite.Subsystems.Foundations;
using TortalPortalLite.Subsystems.Topology;

namespace TortalPortalLite.Subsystems.WorldOps
{
    /// <summary>
    /// #130 "Crypt Ingress - Portals Into Dungeon Interiors". `Location.Awake` instantiates a dungeon's
    /// interior at `new Vector3(zoneCenter.x, surfaceY + 5000f, zoneCenter.z)` (SERVER decompile
    /// :133505-133515) and `Character.InInterior(Vector3)` is literally `position.y > 3000f`
    /// (:4538-4541) - zones/sectors are XZ-only (`ZoneSystem.GetZone`/`GetSectorIndex` ignore y entirely,
    /// :115741-115753), so the interior occupies the SAME sector as the surface above it and a ZDO
    /// placed there syncs/saves/pairs exactly like any surface ZDO.
    ///
    /// Room-accurate targeting: `DungeonGenerator.Save` (:152970-152999, confirmed directly against the
    /// decompile by this build) serialises every placed room's hash/position/rotation into the
    /// generator's OWN ZDO as a plain (uncompressed) `ZDOVars.s_roomData` byte array via a
    /// `BinaryWriter`/`BinaryReader` pair - `int count; { int roomHash; Vector3 pos; Quaternion rot; } *
    /// count` - readable with the SAME `BinaryReader.ReadVector3()/ReadQuaternion()` extension methods
    /// (assembly_utils.dll's global-namespace `Utils` class, already used by vanilla's own Load at
    /// :153014-153021) with no reflection needed.
    ///
    /// Locating the DungeonGenerator ZDO itself: this engine does NOT attempt option (a) from the
    /// catalog's own text (enumerating every LocationProxy ZDO's `s_location` field via
    /// `ZDOExtraData.GetAllZDOIDsWithHash` - workable but redundant here) because
    /// Subsystems.Topology.TargetedLocationRegistry (Wave 1, frozen, read-only reuse) already exposes
    /// `ZoneSystem.instance.FindClosestLocation(name, near, out instance)`, which resolves the location
    /// INSTANCE (surface position) by admin-declared name with no guessed prefab hash. From there this
    /// engine computes the interior probe point (zone centre, surface Y + 5000) and spatially searches
    /// for a ZDO whose resolved prefab carries a `DungeonGenerator` component (matching this codebase's
    /// own established idiom for "does this prefab have component X" - see
    /// Subsystems/Enforcement/LockdownBossWatchdogEngine.cs's boss-prefab discovery).
    ///
    /// Room selection: with no DungeonDB room-name data available (asset data outside the decompile, per
    /// the catalog's own note), this engine picks the room FARTHEST (by straight-line distance) from the
    /// generator's own transform position as an approximation of "the deepest room" - a documented
    /// heuristic, not a guarantee.
    ///
    /// Every candidate interior coordinate is validated by #143/#244's own guard
    /// (WildcardBSectorZeroEngine.IsSafeCoordinate - dungeon interiors sit at y ~5000, comfortably inside
    /// the legal XZ range so this should always pass, but the call site never assumes it) and, per #129,
    /// classified through the arrival ladder before use (an interior room is always &lt;=1000m of "clear
    /// air" below itself in the sense that matters - Character.InInterior's own y&gt;3000 test means
    /// FindFloor's 1000m downward ray from ANY interior room always finds the room's own floor mesh, so
    /// this is always a #129 FallOnArrival-classified, harmless landing - logged, never rejected). The
    /// resulting anchor is created via Subsystems.Topology.TargetedAnchorFactory (a non-portal,
    /// SAFELY-MOVABLE destination ZDO - #179's own citation on why a portal prefab is the wrong primitive
    /// for a coordinate this engine might need to re-resolve after a world reload) and hardened via #235
    /// (WildcardBPhantomSurvivalEngine).
    /// </summary>
    public static class WildcardBCryptIngressEngine
    {
        /// <summary>Admin-declared route: a source portal position paired with the dungeon location's admin-known name.</summary>
        private sealed class Route
        {
            public Vector3 SourcePosition;
            public string LocationName = "";
            public string Label = "";
        }

        private static readonly List<Route> _routes = new List<Route>();
        private static float _timer;

        /// <summary>Declares (or updates) a Crypt Ingress route. Call once per desired source portal, e.g. from an admin command or a future JSON loader - this engine does not itself read a routes file (out of scope for this domain; see Subsystems/Topology/TargetedRouteStore.cs for the established JSON pattern if that is ever wanted here).</summary>
        public static void DeclareRoute(Vector3 sourcePosition, string locationName, string label)
        {
            _routes.RemoveAll(r => (r.SourcePosition - sourcePosition).sqrMagnitude < 0.01f);
            _routes.Add(new Route { SourcePosition = sourcePosition, LocationName = locationName ?? "", Label = label ?? locationName ?? "Crypt" });
        }

        public static void OnUpdate(float dt)
        {
            if (WildcardBConfig.Enabled?.Value == false || _routes.Count == 0)
            {
                return;
            }
            _timer += dt;
            float interval = WildcardBConfig.CryptIngressPollSeconds?.Value ?? 5f;
            if (_timer < interval)
            {
                return;
            }
            _timer = 0f;
            Tick();
        }

        private static void Tick()
        {
            if (ZDOMan.instance == null || ZoneSystem.instance == null || ZNetScene.instance == null)
            {
                return;
            }

            foreach (Route route in _routes)
            {
                if (!PortalCensus.TryGetByPosition(route.SourcePosition, out PortalRecord sourceRec))
                {
                    continue;
                }
                ZDO source = ZDOMan.instance.GetZDO(sourceRec.Uid);
                if (source == null || !source.IsValid())
                {
                    continue;
                }

                if (!TargetedLocationRegistry.TryFindClosest(route.LocationName, source.GetPosition(), out ZoneSystem.LocationInstance inst))
                {
                    continue; // location not yet generated for any peer - nothing to aim at yet
                }

                Vector3 interiorProbe = InteriorProbePoint(inst.m_position);
                if (!WildcardBSectorZeroEngine.IsSafeCoordinate(interiorProbe))
                {
                    PortalDebug.LogWarning($"[WildcardBCryptIngressEngine] interior probe for '{route.LocationName}' at {interiorProbe:F0} fails the Sector Zero guard - skipping (should not happen for a normal-world dungeon).");
                    continue;
                }

                Vector3 targetPos = interiorProbe;
                Quaternion targetRot = Quaternion.identity;
                if (TryResolveDeepestRoom(interiorProbe, out Vector3 roomPos, out Quaternion roomRot))
                {
                    targetPos = roomPos;
                    targetRot = roomRot;
                }

                WildcardBArrivalLadderEngine.ClassifyAndWarn(targetPos, targetPos.y, $"CryptIngress:{route.Label}");

                ZDO anchor = TargetedAnchorFactory.CreateOrRetarget(source, targetPos, targetRot, TargetedTagFormat.Named(route.Label), "CryptIngress");
                if (anchor != null)
                {
                    WildcardBPhantomSurvivalEngine.ApplyIfEnabled(anchor);
                }
            }
        }

        private static Vector3 InteriorProbePoint(Vector3 surfacePos)
        {
            Vector2s zone = ZoneSystem.GetZone(surfacePos);
            Vector3 zoneCenter = ZoneSystem.GetZonePos(zone);
            return new Vector3(zoneCenter.x, surfacePos.y + 5000f, zoneCenter.z);
        }

        private static bool TryResolveDeepestRoom(Vector3 interiorProbe, out Vector3 roomPos, out Quaternion roomRot)
        {
            roomPos = interiorProbe;
            roomRot = Quaternion.identity;

            float radius = WildcardBConfig.CryptIngressSearchRadius?.Value ?? 80f;
            List<ZDO> nearby = ZdoSpatialQuery.FindNear(interiorProbe, radius);
            ZDO generatorZdo = null;
            foreach (ZDO candidate in nearby)
            {
                GameObject prefab = ZNetScene.instance.GetPrefab(candidate.GetPrefab());
                if (prefab != null && prefab.GetComponent<DungeonGenerator>() != null)
                {
                    generatorZdo = candidate;
                    break;
                }
            }
            if (generatorZdo == null)
            {
                return false; // zone never ghost-generated for any peer yet - fall back to the interior probe point
            }

            byte[] blob = generatorZdo.GetByteArray(ZDOVars.s_roomData);
            if (blob == null || blob.Length == 0)
            {
                return false;
            }

            try
            {
                using var stream = new MemoryStream(blob);
                using var reader = new BinaryReader(stream);
                int count = reader.ReadInt32();
                Vector3 generatorPos = generatorZdo.GetPosition();
                float bestSqr = -1f;
                bool found = false;
                for (int i = 0; i < count; i++)
                {
                    reader.ReadInt32(); // room hash - unresolvable to a name without DungeonDB asset data, only used to detect corruption below
                    Vector3 pos = reader.ReadVector3();
                    Quaternion rot = reader.ReadQuaternion();
                    float sqr = (pos - generatorPos).sqrMagnitude;
                    if (sqr > bestSqr)
                    {
                        bestSqr = sqr;
                        roomPos = pos;
                        roomRot = rot;
                        found = true;
                    }
                }
                return found;
            }
            catch (Exception ex)
            {
                PortalDebug.LogWarning($"[WildcardBCryptIngressEngine] s_roomData parse failed for generator {generatorZdo.m_uid}: {ex.Message}");
                return false;
            }
        }
    }
}
