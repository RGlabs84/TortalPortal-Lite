using System.Collections.Generic;
using UnityEngine;
using TortalPortalLite.Core;

namespace TortalPortalLite.Subsystems.Topology
{
    /// <summary>
    /// #228 Destination Zone Ghost Pre-Generation. A CLIENT on a dedicated server only ever runs
    /// `ZoneSystem.PokeLocalZone` in `SpawnMode.Client` (:114021-114037), which instantiates terrain but
    /// deliberately skips `PlaceLocations`/`PlaceVegetation` (gated on Ghost||Full &amp;&amp;
    /// !IsZoneGenerated, :114086-114105) - so a portal that routes someone into never-visited wilderness
    /// delivers them onto bald terrain that fills in over the following seconds as the server's own
    /// ZoneSystem.Update catches up. This engine lets any OTHER routing engine that is about to point a
    /// portal at a brand-new coordinate (Randomised Roguelike Re-Roll #37 being the clearest case, but
    /// any moving/fabricated-anchor engine may call it too) pre-empt that by calling
    /// <see cref="Enqueue"/> ahead of time.
    ///
    /// Generation itself is the private `ZoneSystem.SpawnZone(Vector2s, SpawnMode, out GameObject)`
    /// (:114071-114107), called directly (not patched - reachable via this csproj's Publicize=true, same
    /// as every other "private but just called, not intercepted" API in this domain) with
    /// SpawnMode.Ghost - which places locations/vegetation as real persistent ZDOs and then destroys
    /// every instantiated GameObject in the same call (:114094-114103), never SpawnMode.Full, which would
    /// leave live GameObjects sitting in the server's own scene forever. `IsZoneGenerated` is checked
    /// first so an already-generated zone is never redundantly touched, and a zone whose HeightmapBuilder
    /// terrain job is not ready yet (`SpawnZone` returns false, :114075) is simply re-queued for the next
    /// tick rather than dropped.
    ///
    /// Budgeted at RoutingConfig.ZoneGhostBudgetPerTick zones actually spawned per tick (SpawnZone is
    /// itself heavier than a single ZDO write - it runs full location/vegetation placement) so a large
    /// ring never spikes one frame.
    /// </summary>
    public static class RoutingZoneGhostPreGenEngine
    {
        private static float _timer;
        private static readonly Queue<Vector2s> _pending = new Queue<Vector2s>();
        private static readonly HashSet<Vector2s> _queued = new HashSet<Vector2s>();

        /// <summary>Queues every zone within <paramref name="ringRadius"/> zones of <paramref name="worldPos"/> for background ghost generation, skipping any already queued or already generated.</summary>
        public static void Enqueue(Vector3 worldPos, int ringRadius = 2)
        {
            if (ZoneSystem.instance == null)
            {
                return;
            }
            Vector2s center = ZoneSystem.GetZone(worldPos);
            for (int dx = -ringRadius; dx <= ringRadius; dx++)
            {
                for (int dz = -ringRadius; dz <= ringRadius; dz++)
                {
                    var zone = new Vector2s((short)(center.x + dx), (short)(center.y + dz));
                    if (_queued.Contains(zone) || ZoneSystem.instance.IsZoneGenerated(zone))
                    {
                        continue;
                    }
                    _queued.Add(zone);
                    _pending.Enqueue(zone);
                }
            }
        }

        public static void OnUpdate(float dt)
        {
            if (RoutingConfig.TakeoverEnabled?.Value == false || ZoneSystem.instance == null || _pending.Count == 0)
            {
                return;
            }
            _timer += dt;
            if (_timer < 1.0f)
            {
                return;
            }
            _timer = 0f;

            int budget = RoutingConfig.ZoneGhostBudgetPerTick?.Value ?? 1;
            for (int i = 0; i < budget && _pending.Count > 0; i++)
            {
                Vector2s zone = _pending.Dequeue();
                _queued.Remove(zone);
                if (ZoneSystem.instance.IsZoneGenerated(zone))
                {
                    continue;
                }
                bool ok = ZoneSystem.instance.SpawnZone(zone, ZoneSystem.SpawnMode.Ghost, out _);
                if (!ok)
                {
                    // Terrain job not ready yet (HeightmapBuilder busy) - retry next tick rather than drop it.
                    _queued.Add(zone);
                    _pending.Enqueue(zone);
                }
            }
        }
    }
}
