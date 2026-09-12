using System;
using System.Collections.Generic;
using UnityEngine;
using TortalPortalLite.Core;

namespace TortalPortalLite.Subsystems.Topology
{
    /// <summary>
    /// #205 Destination Zone Governor - controls what the world-gen pipeline does to a managed
    /// destination's zone: (1) PRE-GENERATE - ghost-generate it at route-write time so arrival never
    /// pays for first-visit generation; (2) CLEAR-AREA INJECTION - vegetation never grows through a
    /// phantom portal; (3) BARREN PAD - mark a zone generated-without-generating for a spawn-free
    /// clearing. Owns the shared "call ZoneSystem.SpawnZone(zone, Ghost, out _), budgeted, with retry"
    /// driver #206 Target Materialiser also uses (its own explicit prerequisite: "shares the SpawnZone
    /// driver and per-tick budget").
    ///
    /// Publicize=true (already active for this project - TortalPortalLite.csproj's own comment) makes
    /// every private member this needs directly callable, no reflection: `ZoneSystem.SpawnZone`,
    /// `IsZoneGenerated`/`SetZoneGenerated`/`m_generatedZones` are all private in vanilla source (SERVER
    /// decompile :114071, :115864-115871, :113276).
    ///
    /// (2) CLEAR-AREA INJECTION is NOT implemented: the catalog's own mechanism is "a Harmony prefix on
    /// ZoneSystem.PlaceVegetation" appending a ClearArea for every managed destination - Core/Hooks/ has
    /// no broker for that method (every existing broker there is portal/ZDO-destroy specific:
    /// ConnectPortalsHook, SetConnectionHook, FindRandomUnconnectedPortalHook, RpcZdoDataHook,
    /// HandleDestroyedZdoHook). Writing a private ad hoc patch here would be exactly the Harmony
    /// collision hazard Core/Hooks/ exists to prevent.
    /// NEEDS NEW HOOK BROKER on ZoneSystem.PlaceVegetation: purpose - append a
    /// `new ZoneSystem.ClearArea(portalPos, radius)` to that method's own clearAreas list for every
    /// managed destination in the zone being placed, so vegetation never grows through a phantom portal
    /// (#205's own mechanism 2). Mechanisms 1 and 3 below are complete and do not depend on this.
    /// </summary>
    public static class TargetedZoneGovernorEngine
    {
        private const int MaxAttemptsPerZone = 20;
        private static readonly Queue<Vector2s> _pending = new Queue<Vector2s>();
        private static readonly HashSet<Vector2s> _queued = new HashSet<Vector2s>();
        private static readonly Dictionary<Vector2s, int> _attempts = new Dictionary<Vector2s, int>();

        public static void OnUpdate(float dt)
        {
            if (ZoneSystem.instance == null || _pending.Count == 0)
            {
                return;
            }
            int budget = Mathf.Max(1, TargetedConfig.ZoneGovernorZonesPerTick?.Value ?? 1);
            for (int i = 0; i < budget && _pending.Count > 0; i++)
            {
                Vector2s zone = _pending.Dequeue();
                _queued.Remove(zone);
                if (ZoneSystem.instance.IsZoneGenerated(zone))
                {
                    _attempts.Remove(zone);
                    continue;
                }
                int attempt = (_attempts.TryGetValue(zone, out int a) ? a : 0) + 1;
                _attempts[zone] = attempt;

                bool ok;
                try
                {
                    ok = ZoneSystem.instance.SpawnZone(zone, ZoneSystem.SpawnMode.Ghost, out GameObject _);
                }
                catch (Exception ex)
                {
                    PortalDebug.LogWarning($"[TargetedZoneGovernorEngine] SpawnZone threw for zone {zone}: {ex.Message}");
                    ok = false;
                }

                if (!ok && attempt < MaxAttemptsPerZone)
                {
                    // Terrain not built yet / location prefab still async-loading are both legitimate
                    // "not done", not "failed" (#205/#206's own citation: IsTerrainReady queues a build
                    // and returns false; PokeCanSpawnLocation starts an async load) - requeue for later.
                    _pending.Enqueue(zone);
                    _queued.Add(zone);
                }
                else
                {
                    _attempts.Remove(zone);
                }
            }
        }

        /// <summary>Enqueues a zone (by world position) for ghost-generation if it isn't already generated. Cheap and idempotent to call repeatedly.</summary>
        public static void RequestGhostGenerate(Vector3 worldPos)
        {
            if (TargetedConfig.ZoneGovernorPreGenerate?.Value == false || ZoneSystem.instance == null)
            {
                return;
            }
            EnqueueIfNeeded(ZoneSystem.GetZone(worldPos));
        }

        /// <summary>Also enqueues the 8 neighbouring zones - #206's own "generate the ring too" rule, so a traveller's IsAreaReady/view distance are covered on arrival.</summary>
        public static void RequestGhostGenerateWithRing(Vector3 worldPos)
        {
            RequestGhostGenerate(worldPos);
            if (ZoneSystem.instance == null)
            {
                return;
            }
            Vector2s centre = ZoneSystem.GetZone(worldPos);
            for (int dx = -1; dx <= 1; dx++)
            {
                for (int dy = -1; dy <= 1; dy++)
                {
                    if (dx == 0 && dy == 0)
                    {
                        continue;
                    }
                    EnqueueIfNeeded(new Vector2s((short)(centre.x + dx), (short)(centre.y + dy)));
                }
            }
        }

        private static void EnqueueIfNeeded(Vector2s zone)
        {
            if (IsWithinSectorClamp(zone) && !_queued.Contains(zone) && !ZoneSystem.instance.IsZoneGenerated(zone))
            {
                _pending.Enqueue(zone);
                _queued.Add(zone);
            }
        }

        /// <summary>True if this zone is fully generated (has real objects, not just a world-gen-rolled placeholder).</summary>
        public static bool IsGenerated(Vector3 worldPos)
        {
            return ZoneSystem.instance != null && ZoneSystem.instance.IsZoneGenerated(ZoneSystem.GetZone(worldPos));
        }

        /// <summary>#205's own clamp citation - coordinates beyond roughly +-16320 m collide with sector 0 (ZoneSystem.SectorToIndex, SERVER decompile :115755-115773).</summary>
        private static bool IsWithinSectorClamp(Vector2s zone)
        {
            return Mathf.Abs(zone.x) < 255 && Mathf.Abs(zone.y) < 255;
        }

        /// <summary>
        /// Mechanism 3 - BARREN PAD. Marks a zone generated WITHOUT generating it (skips
        /// PlaceLocations/PlaceVegetation/PlaceZoneCtrl forever): a direct `m_generatedZones.Add` via the
        /// publicized private field. Deliberately conservative per the catalog's own warning - refuses if
        /// a world-gen location is already queued for this zone (m_locationInstances), which a barren
        /// marking would otherwise silently erase.
        /// </summary>
        public static bool TryMarkBarren(Vector3 worldPos)
        {
            if (TargetedConfig.ZoneGovernorBarrenPad?.Value != true || ZoneSystem.instance == null)
            {
                return false;
            }
            Vector2s zone = ZoneSystem.GetZone(worldPos);
            if (ZoneSystem.instance.IsZoneGenerated(zone))
            {
                return false;
            }
            if (ZoneSystem.instance.m_locationInstances.ContainsKey(zone))
            {
                PortalDebug.LogWarning($"[TargetedZoneGovernorEngine] refused to mark zone {zone} barren - a world-gen location is queued there.");
                return false;
            }
            ZoneSystem.instance.m_generatedZones.Add(zone);
            return true;
        }
    }
}
