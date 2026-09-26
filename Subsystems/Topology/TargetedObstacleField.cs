using System;
using System.Collections.Generic;
using UnityEngine;
using TortalPortalLite.Core;
using TortalPortalLite.Core.Data;

namespace TortalPortalLite.Subsystems.Topology
{
    /// <summary>
    /// Everything solid around a placement anchor, as (centre, footprint radius) discs, so a candidate
    /// spot can be scored by how far its edge is from the nearest obstacle's EDGE - not its pivot.
    /// That distinction is the whole reason this class exists: a ZDO's position is the prefab pivot,
    /// and a black-forest boulder or a Mistlands rock spans 6-12 m around a pivot that may itself be
    /// several metres from where a portal would be dropped. The 1.0.3 scanner measured 1.3 m from the
    /// pivot and happily planted gates inside rock walls.
    ///
    /// Sources, gathered once per search:
    ///  - ZDOs within reach (ZdoSpatialQuery.FindNear - sector data, never Physics.*). Each prefab's
    ///    footprint is the horizontal reach of its non-trigger colliders in prefab-root space (read off
    ///    the prefab asset ZNetScene holds; collider shapes and mesh bounds are plain asset data that
    ///    need no physics scene), cached per prefab hash and scaled by the ZDO's own stored scale
    ///    (ZNetView.Awake :82635-82646). Things that move or are the reason we are here - creatures,
    ///    items, the tombstone, projectiles, fish, ragdolls - are not obstacles.
    ///  - Generated locations (ZoneSystem.m_locationInstances, populated for the whole map at world
    ///    load). A location's static geometry - ruin walls, crypt facades, the start temple's stones -
    ///    never has ZDOs (only its ZNetView children do), so it is represented by the location's own
    ///    exterior radius instead.
    ///
    /// A vertical window around the anchor keeps a dungeon interior (stacked 5000 m above its
    /// entrance at the same x,z) from shadowing the ground outside it, and vice versa.
    /// </summary>
    public sealed class TargetedObstacleField
    {
        /// <summary>Half-width of a portal frame - the margin is measured from the portal's own edge, so even a 1 m clearance keeps the frame itself out of everything.</summary>
        public const float PortalFootprintRadius = 1.25f;
        /// <summary>Pickables and other sub-metre props (mushrooms, flowers, berry bushes, small debris) only ever need this much room, whatever the configured clearance - they never dominate the "most open spot" fallback.</summary>
        public const float SmallObstacleClearance = 2f;
        /// <summary>Obstacles further than this above/below the anchor are a different floor (dungeon interior vs entrance, cliff top vs base) and are ignored.</summary>
        public const float VerticalWindow = 40f;
        /// <summary>Largest single-prefab footprint we bother reaching for when sizing the ZDO query (the biggest MineRock5 boulders and Ashlands spires).</summary>
        private const float MaxFootprintReach = 20f;
        /// <summary>Extra distance location instances are gathered from, on top of the ZDO reach - covers the largest location exterior radii.</summary>
        private const float LocationExtraReach = 60f;
        /// <summary>Rings of genuine choice scanned beyond the first ring that could escape an obstacle covering the anchor.</summary>
        private const float EscapeRings = 6f;
        /// <summary>Footprint assumed for a solid prefab whose collider mesh is not readable headless.</summary>
        private const float UnreadableMeshFallbackRadius = 3f;

        private readonly struct Obstacle
        {
            public readonly float X;
            public readonly float Z;
            public readonly float Radius;
            public readonly bool Small;
            public readonly string Name;
            public readonly bool FromZdo;

            public Obstacle(float x, float z, float radius, bool small, string name, bool fromZdo = false)
            {
                X = x;
                Z = z;
                Radius = radius;
                Small = small;
                Name = name;
                FromZdo = fromZdo;
            }
        }

        private readonly struct Footprint
        {
            public readonly bool Solid;
            public readonly float Radius;
            public readonly bool Small;
            public readonly float PrefabScale;
            public readonly string Name;
            public readonly bool Unreadable;

            public Footprint(bool solid, float radius, bool small, float prefabScale, string name, bool unreadable = false)
            {
                Solid = solid;
                Radius = radius;
                Small = small;
                PrefabScale = prefabScale;
                Name = name;
                Unreadable = unreadable;
            }

            public static readonly Footprint NonSolid = new Footprint(false, 0f, false, 1f, "");
        }

        private static readonly Dictionary<int, Footprint> _footprintCache = new Dictionary<int, Footprint>();
        private static HashSet<int>? _ignoredLayers;

        private readonly List<Obstacle> _obstacles = new List<Obstacle>();

        public int Count => _obstacles.Count;
        public int ZdoObstacles { get; private set; }
        public int LocationObstacles { get; private set; }
        /// <summary>Solid prefabs seen this search whose collider mesh could not be read headless (footprint assumed) - non-zero means the dedicated server stripped mesh data and the fallback radii are doing the work.</summary>
        public int UnreadablePrefabs { get; private set; }

        /// <summary>
        /// Gathers every obstacle that could matter to a candidate within <paramref name="searchRadius"/>
        /// of <paramref name="anchor"/> at the required <paramref name="clearance"/>. <paramref name="avoid"/>
        /// are portal-sized spots already spoken for (the other end of the same gate, placed a moment
        /// earlier and not yet in sector data).
        /// </summary>
        public static TargetedObstacleField Build(Vector3 anchor, float searchRadius, float clearance, ZDOID ignoreZdo, IReadOnlyList<Vector3>? avoid = null)
        {
            var field = new TargetedObstacleField();
            float reach = ReachFor(searchRadius, clearance);
            // Locations are few per map and their radii are the largest single discs here, so they are
            // gathered from well beyond the nominal reach: one that contains the anchor (a death, or a
            // bed, inside a ruin or the start temple) widens the search below and must never be missed.
            field.AddLocations(anchor, reach + LocationExtraReach);
            field.AddZdos(anchor, reach, ignoreZdo);
            if (avoid != null)
            {
                foreach (Vector3 p in avoid)
                {
                    field._obstacles.Add(new Obstacle(p.x, p.z, PortalFootprintRadius, false, "portal (other end of this gate)"));
                }
            }

            // An obstacle whose keep-out disc covers the anchor itself pushes the first possibly-clear
            // ring outward; make sure the scan reaches past it plus a few rings of choice.
            field.EffectiveSearchRadius = Mathf.Max(searchRadius, field.RadiusToEscape(anchor, clearance) + EscapeRings);
            float widerReach = ReachFor(field.EffectiveSearchRadius, clearance);
            if (widerReach > reach + 0.01f)
            {
                field._obstacles.RemoveAll(o => o.FromZdo);
                field.ZdoObstacles = 0;
                field.UnreadablePrefabs = 0;
                field.AddZdos(anchor, widerReach, ignoreZdo);
            }
            return field;
        }

        /// <summary>The search radius actually worth scanning: the configured one, or further when something big sits on top of the anchor.</summary>
        public float EffectiveSearchRadius { get; private set; }

        private static float ReachFor(float searchRadius, float clearance) => searchRadius + clearance + PortalFootprintRadius + MaxFootprintReach;

        /// <summary>Smallest ring radius at which a candidate could possibly clear every obstacle whose keep-out disc covers the anchor (0 when none does).</summary>
        private float RadiusToEscape(Vector3 anchor, float clearance)
        {
            float needed = 0f;
            for (int i = 0; i < _obstacles.Count; i++)
            {
                Obstacle o = _obstacles[i];
                float dx = o.X - anchor.x;
                float dz = o.Z - anchor.z;
                float dist = Mathf.Sqrt((dx * dx) + (dz * dz));
                float required = o.Small ? Mathf.Min(clearance, SmallObstacleClearance) : clearance;
                float escape = o.Radius + PortalFootprintRadius + required - dist;
                if (escape > needed)
                {
                    needed = escape;
                }
            }
            return needed;
        }

        /// <summary>
        /// How much room a portal centred at (x,z) has to spare: min over obstacles of
        /// (edge-to-edge distance - required clearance). &gt;= 0 means the spot honours the configured
        /// clearance against everything; negative is how far short it falls against the worst offender.
        /// </summary>
        public float Margin(float x, float z, float clearance, out string nearest)
        {
            Probe(x, z, out float normalEdge, out float smallEdge, out string normalName, out string smallName);
            return MarginFrom(normalEdge, smallEdge, normalName, smallName, clearance, out nearest);
        }

        /// <summary>
        /// The raw edge-to-edge room at this spot, measured from the portal frame's own edge, split into
        /// the nearest ordinary obstacle and the nearest sub-metre prop. Split because the two answer to
        /// different clearance requirements (a mushroom never needs more than SmallObstacleClearance), and
        /// separating them here lets a caller test the same spot against many clearance requirements
        /// without walking the obstacle list again - which is what makes the graded search in
        /// TargetedWorldGenValidation affordable. float.MaxValue for "nothing of that kind in reach".
        /// </summary>
        public void Probe(float x, float z, out float normalEdge, out float smallEdge, out string normalName, out string smallName)
        {
            normalEdge = float.MaxValue;
            smallEdge = float.MaxValue;
            normalName = "";
            smallName = "";
            for (int i = 0; i < _obstacles.Count; i++)
            {
                Obstacle o = _obstacles[i];
                float dx = o.X - x;
                float dz = o.Z - z;
                float edge = Mathf.Sqrt((dx * dx) + (dz * dz)) - o.Radius - PortalFootprintRadius;
                if (o.Small)
                {
                    if (edge < smallEdge)
                    {
                        smallEdge = edge;
                        smallName = o.Name;
                    }
                }
                else if (edge < normalEdge)
                {
                    normalEdge = edge;
                    normalName = o.Name;
                }
            }
        }

        /// <summary>Room to spare beyond <paramref name="clearance"/> for a spot already probed (negative = short by that much). 1000 when nothing was in reach at all.</summary>
        public static float MarginFrom(float normalEdge, float smallEdge, string normalName, string smallName, float clearance, out string nearest)
        {
            float best = float.MaxValue;
            nearest = "";
            if (normalEdge != float.MaxValue)
            {
                best = normalEdge - clearance;
                nearest = normalName;
            }
            if (smallEdge != float.MaxValue)
            {
                float smallMargin = smallEdge - Mathf.Min(clearance, SmallObstacleClearance);
                if (smallMargin < best)
                {
                    best = smallMargin;
                    nearest = smallName;
                }
            }
            return best == float.MaxValue ? 1000f : best;
        }

        // ------------------------------------------------------------------------------------- sources

        private void AddZdos(Vector3 anchor, float reach, ZDOID ignoreZdo)
        {
            if (ZDOMan.instance == null)
            {
                return;
            }
            List<ZDO> near = ZdoSpatialQuery.FindNear(anchor, reach);
            for (int i = 0; i < near.Count; i++)
            {
                ZDO zdo = near[i];
                if (zdo == null || !zdo.IsValid() || zdo.m_uid == ignoreZdo)
                {
                    continue;
                }
                Vector3 p = zdo.GetPosition();
                if (Mathf.Abs(p.y - anchor.y) > VerticalWindow)
                {
                    continue;
                }
                Footprint fp = FootprintOf(zdo.GetPrefab());
                if (!fp.Solid)
                {
                    continue;
                }
                if (fp.Unreadable)
                {
                    UnreadablePrefabs++;
                }
                float scale = InstanceScale(zdo, fp.PrefabScale);
                _obstacles.Add(new Obstacle(p.x, p.z, fp.Radius * scale, fp.Small, fp.Name, fromZdo: true));
                ZdoObstacles++;
            }
        }

        private void AddLocations(Vector3 anchor, float reach)
        {
            if (ZoneSystem.instance == null)
            {
                return;
            }
            try
            {
                foreach (ZoneSystem.LocationInstance inst in ZoneSystem.instance.m_locationInstances.Values)
                {
                    float r = TargetedLocationRegistry.ExteriorRadiusOf(inst);
                    float dx = inst.m_position.x - anchor.x;
                    float dz = inst.m_position.z - anchor.z;
                    float limit = reach + r;
                    if ((dx * dx) + (dz * dz) > limit * limit)
                    {
                        continue;
                    }
                    string name = "location:" + (inst.m_location != null ? inst.m_location.m_prefabName : "?");
                    _obstacles.Add(new Obstacle(inst.m_position.x, inst.m_position.z, r, false, name));
                    LocationObstacles++;
                }
            }
            catch (Exception ex)
            {
                PortalDebug.LogWarning($"[TargetedObstacleField] location scan unavailable on this build: {ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>The exterior location instance sharing a zone with an interior point (a dungeon at y &gt; 1000 sits in the same 64 m zone as its entrance - ZoneSystem.SpawnLocation :115193-115209), if any.</summary>
        public static bool TryFindLocationInZoneOf(Vector3 point, out Vector3 exteriorPos, out string name)
        {
            exteriorPos = Vector3.zero;
            name = "";
            if (ZoneSystem.instance == null)
            {
                return false;
            }
            int zx = Mathf.FloorToInt((point.x + 32f) / 64f);
            int zz = Mathf.FloorToInt((point.z + 32f) / 64f);
            try
            {
                foreach (ZoneSystem.LocationInstance inst in ZoneSystem.instance.m_locationInstances.Values)
                {
                    if (Mathf.FloorToInt((inst.m_position.x + 32f) / 64f) != zx || Mathf.FloorToInt((inst.m_position.z + 32f) / 64f) != zz)
                    {
                        continue;
                    }
                    exteriorPos = inst.m_position;
                    name = inst.m_location != null ? inst.m_location.m_prefabName : "?";
                    return true;
                }
            }
            catch (Exception ex)
            {
                PortalDebug.LogWarning($"[TargetedObstacleField] location lookup unavailable on this build: {ex.GetType().Name}: {ex.Message}");
            }
            return false;
        }

        // ---------------------------------------------------------------------------------- footprints

        private static float InstanceScale(ZDO zdo, float prefabScale)
        {
            Vector3 vec = zdo.GetVec3(ZDOVars.s_scaleHash, Vector3.zero);
            if (vec != Vector3.zero)
            {
                return Mathf.Max(Mathf.Abs(vec.x), Mathf.Abs(vec.z));
            }
            return zdo.GetFloat(ZDOVars.s_scaleScalarHash, prefabScale);
        }

        private static Footprint FootprintOf(int prefabHash)
        {
            if (_footprintCache.TryGetValue(prefabHash, out Footprint cached))
            {
                return cached;
            }
            Footprint fp;
            try
            {
                fp = ComputeFootprint(prefabHash);
            }
            catch (Exception ex)
            {
                PortalDebug.LogWarning($"[TargetedObstacleField] footprint for prefab {prefabHash} failed, assuming {UnreadableMeshFallbackRadius} m: {ex.GetType().Name}: {ex.Message}");
                fp = new Footprint(true, UnreadableMeshFallbackRadius, false, 1f, prefabHash.ToString(), unreadable: true);
            }
            _footprintCache[prefabHash] = fp;
            return fp;
        }

        private static Footprint ComputeFootprint(int prefabHash)
        {
            GameObject prefab = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(prefabHash) : null;
            if (prefab == null)
            {
                // Unknown to ZNetScene (a prefab from a mod that registered late, or a stale hash): a
                // modest solid disc is the conservative reading.
                return new Footprint(true, 1f, false, 1f, "prefab#" + prefabHash);
            }

            if (prefab.GetComponent<ItemDrop>() != null
                || prefab.GetComponent<Character>() != null
                || prefab.GetComponent<TombStone>() != null
                || prefab.GetComponent<Projectile>() != null
                || prefab.GetComponent<Fish>() != null
                || prefab.GetComponent<Ragdoll>() != null
                || prefab.GetComponent<RandomFlyingBird>() != null)
            {
                return Footprint.NonSolid;
            }

            HashSet<int> ignored = IgnoredLayers();
            Matrix4x4 rootInv = prefab.transform.worldToLocalMatrix;
            Collider[] colliders = prefab.GetComponentsInChildren<Collider>(true);
            float maxRadius = -1f;
            bool sawSolidCollider = false;
            var corners = new Vector3[8];
            foreach (Collider c in colliders)
            {
                if (c == null || c.isTrigger || ignored.Contains(c.gameObject.layer))
                {
                    continue;
                }
                sawSolidCollider = true;
                if (!TryLocalBounds(c, out Bounds b))
                {
                    continue;
                }
                Matrix4x4 toRoot = rootInv * c.transform.localToWorldMatrix;
                Vector3 min = b.min;
                Vector3 max = b.max;
                corners[0] = new Vector3(min.x, min.y, min.z);
                corners[1] = new Vector3(max.x, min.y, min.z);
                corners[2] = new Vector3(min.x, max.y, min.z);
                corners[3] = new Vector3(max.x, max.y, min.z);
                corners[4] = new Vector3(min.x, min.y, max.z);
                corners[5] = new Vector3(max.x, min.y, max.z);
                corners[6] = new Vector3(min.x, max.y, max.z);
                corners[7] = new Vector3(max.x, max.y, max.z);
                for (int i = 0; i < 8; i++)
                {
                    Vector3 p = toRoot.MultiplyPoint3x4(corners[i]);
                    float r = Mathf.Sqrt((p.x * p.x) + (p.z * p.z));
                    if (r > maxRadius)
                    {
                        maxRadius = r;
                    }
                }
            }

            if (!sawSolidCollider)
            {
                return Footprint.NonSolid;
            }

            Vector3 rootScale = prefab.transform.localScale;
            float prefabScale = Mathf.Max(Mathf.Abs(rootScale.x), Mathf.Abs(rootScale.z));
            if (prefabScale <= 0f)
            {
                prefabScale = 1f;
            }

            bool unreadable = maxRadius < 0f;
            if (unreadable)
            {
                // Solid colliders exist but none had readable shape data (mesh-only prefab whose mesh
                // the headless build did not load): size it by what the prefab is.
                maxRadius = prefab.GetComponent<MineRock5>() != null ? 8f
                    : prefab.GetComponent<MineRock>() != null ? 4f
                    : UnreadableMeshFallbackRadius;
            }
            // "Small" = a prop nobody would call an obstruction: anything pickable, or a sub-metre
            // destructible/decoration that is not a tree, rock or building piece. These only ever
            // need SmallObstacleClearance, so a meadow full of bushes and mushrooms still yields a
            // spot that is genuinely clear of the things that matter.
            bool structural = prefab.GetComponent<TreeBase>() != null
                || prefab.GetComponent<Piece>() != null
                || prefab.GetComponent<WearNTear>() != null
                || prefab.GetComponent<MineRock>() != null
                || prefab.GetComponent<MineRock5>() != null;
            bool small = prefab.GetComponent<Pickable>() != null || (!structural && maxRadius * prefabScale < 1f);
            return new Footprint(true, maxRadius, small, prefabScale, prefab.name, unreadable);
        }

        /// <summary>Collider-local AABB from shape data alone - Collider.bounds needs a live physics scene and is empty on an inactive prefab asset.</summary>
        private static bool TryLocalBounds(Collider c, out Bounds bounds)
        {
            switch (c)
            {
                case BoxCollider box:
                    bounds = new Bounds(box.center, box.size);
                    return true;
                case SphereCollider sphere:
                    bounds = new Bounds(sphere.center, Vector3.one * (sphere.radius * 2f));
                    return true;
                case CapsuleCollider capsule:
                {
                    Vector3 size = Vector3.one * (capsule.radius * 2f);
                    float length = Mathf.Max(capsule.height, capsule.radius * 2f);
                    if (capsule.direction == 0) size.x = length;
                    else if (capsule.direction == 1) size.y = length;
                    else size.z = length;
                    bounds = new Bounds(capsule.center, size);
                    return true;
                }
                case MeshCollider mesh:
                    if (mesh.sharedMesh != null)
                    {
                        bounds = mesh.sharedMesh.bounds;
                        return true;
                    }
                    break;
            }
            bounds = default;
            return false;
        }

        /// <summary>Collider layers that never block a player or a placed piece - camera/view volumes, creature hit volumes, water, effects - resolved by name once so nothing here hard-codes a layer index.</summary>
        private static HashSet<int> IgnoredLayers()
        {
            if (_ignoredLayers != null)
            {
                return _ignoredLayers;
            }
            var set = new HashSet<int>();
            foreach (string name in new[]
            {
                "character", "character_net", "character_noenv", "character_ghost", "character_trigger",
                "viewblock", "ghost", "hitbox", "weapon", "projectile", "smoke", "WaterVolume", "Water",
                "UI", "effect", "item", "pathblocker", "blocker",
            })
            {
                int layer = LayerMask.NameToLayer(name);
                if (layer >= 0)
                {
                    set.Add(layer);
                }
            }
            _ignoredLayers = set;
            return set;
        }
    }
}
