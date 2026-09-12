using System;
using System.Collections.Generic;
using UnityEngine;
using TortalPortalLite.Core;

namespace TortalPortalLite.Subsystems.Topology
{
    /// <summary>Admin declaration for one Roguelike Re-Roll gate (routing.json section "roguelikeGates").</summary>
    public sealed class RoutingRoguelikeGateDefinition
    {
        public string Name = "";
        public RoutingPosition Position = new RoutingPosition();

        /// <summary>0 = RoutingConfig.RerollIntervalSeconds.</summary>
        public float RerollSeconds = 0f;

        /// <summary>Optional Heightmap.Biome name (e.g. "Plains") restricting candidates - parsed with Enum.TryParse, ignored if unrecognised.</summary>
        public string? BiomeFilter;

        public string UnknownTag = "Unknown";
    }

    /// <summary>
    /// #37 Randomised Roguelike Re-Roll. Every ZoneSystem raycast-based helper is dead on a dedicated
    /// server - `Game.FixedUpdate` pins `ZNet.SetReferencePosition` to (1e6,0,1e6) every physics tick in
    /// the SERVER build (:100458-100466), so GetGroundHeight/IsBlocked/FindFloor silently return their
    /// no-hit fallback rather than throwing. This engine only uses the validation stack that is genuinely
    /// pure math and confirmed safe headless: `WorldGenerator.instance.GetHeight(x,z)` (:151944, sea
    /// level 30 via `ZoneSystem.m_waterLevel`, :113218), `WorldGenerator.instance.GetBiome(x,z)`
    /// (:151725), `WorldGenerator.instance.GetTerrainDelta` (:152364-152390, vanilla's own location
    /// placement accepts delta &lt;= 2f) and the STATIC `ZoneSystem.IsLavaPreHeightmap` (:115637-115646,
    /// the only lava test that is biome-gated and safe headless). Deliberately skips the catalog's
    /// "fast pre-filter" via `AltBiomeWorldData`'s internal biome grid (its exact field/return shapes
    /// were not confirmed against this build's decompile with the same certainty as the primitives
    /// above) - candidate sampling is uniform-random within RerollMaxWorldRadius instead, budgeted at
    /// RerollMaxAttemptsPerTick pure-math attempts per tick, cheap enough that the optimisation is not
    /// load-bearing for correctness.
    ///
    /// A re-roll fabricates the NEW anchor (#24, void - "arrived at empty ground" is the intended read)
    /// BEFORE destroying the old one, so the gate's connection is never left dangling for even one tick.
    /// </summary>
    public static class RoutingRoguelikeRerollEngine
    {
        private static float _timer;
        private static int _lastRegistryVersion = -1;
        private static List<RoutingRoguelikeGateDefinition> _gates = new List<RoutingRoguelikeGateDefinition>();

        // NEEDS NEW KEY: tpl_routing_nextrerollat (server-clock seconds), purpose: GateState.NextRerollAt
        // is RAM-only - a restart re-arms every gate's full reroll interval from scratch rather than
        // resuming a countdown that was already partway elapsed. Cosmetic only (worst case a gate re-rolls
        // later than an admin expects immediately after a restart), left as a documented gap rather than
        // silently accepted.
        private sealed class GateState
        {
            public double NextRerollAt;
            public bool Initialized;
            public ZDOID CurrentAnchor = ZDOID.None;
            public bool HasAnchor;
        }

        private static readonly Dictionary<string, GateState> _state = new Dictionary<string, GateState>();

        public static void OnUpdate(float dt)
        {
            if (RoutingConfig.TakeoverEnabled?.Value == false || ZDOMan.instance == null || WorldGenerator.instance == null || ZNet.instance == null)
            {
                return;
            }
            _timer += dt;
            if (_timer < 1.0f)
            {
                return;
            }
            _timer = 0f;

            if (RoutingManagedPortalRegistry.Version != _lastRegistryVersion)
            {
                _lastRegistryVersion = RoutingManagedPortalRegistry.Version;
                _gates = RoutingManagedPortalRegistry.Section<RoutingRoguelikeGateDefinition>("roguelikeGates");
            }
            if (_gates.Count == 0)
            {
                return;
            }

            double now = ZNet.instance.GetTimeSeconds();
            foreach (RoutingRoguelikeGateDefinition gate in _gates)
            {
                if (!RoutingPairingAuthorityEngine.TryClaim(gate.Position, $"roguelike:{gate.Name}"))
                {
                    continue;
                }
                ZDO? gateZdo = RoutingWriteOps.ResolveLive(gate.Position);
                if (gateZdo == null)
                {
                    continue;
                }
                if (!_state.TryGetValue(gate.Name, out GateState? state))
                {
                    state = new GateState();
                    _state[gate.Name] = state;
                }
                float rerollSeconds = gate.RerollSeconds > 0f ? gate.RerollSeconds : (RoutingConfig.RerollIntervalSeconds?.Value ?? 86400f);
                if (!state.Initialized)
                {
                    state.NextRerollAt = now + rerollSeconds;
                    state.Initialized = true;
                    // Keep whatever the gate currently reads until the first reroll fires.
                    continue;
                }
                if (now < state.NextRerollAt)
                {
                    continue;
                }

                if (TryFindCandidate(gate, out Vector3 candidate, out Heightmap.Biome biome))
                {
                    // #228: a never-visited destination arrives on bald terrain otherwise - queue its
                    // surrounding ring for background ghost generation now, ahead of the first walk-through.
                    RoutingZoneGhostPreGenEngine.Enqueue(candidate);
                    ZDO? newAnchor = RoutingPhantomAnchorEngine.FabricateVoidAnchor(candidate, Quaternion.identity);
                    if (newAnchor != null)
                    {
                        if (state.HasAnchor)
                        {
                            ZDO? old = ZDOMan.instance.GetZDO(state.CurrentAnchor);
                            if (old != null)
                            {
                                RoutingPhantomAnchorEngine.Destroy(old);
                            }
                        }
                        state.CurrentAnchor = newAnchor.m_uid;
                        state.HasAnchor = true;

                        string tag = Truncate($"{gate.UnknownTag} {biome}");
                        RoutingWriteOps.Reassert(gateZdo, tag, newAnchor.m_uid);
                        RoutingPairingAuthorityEngine.Publish(gateZdo.m_uid, tag, newAnchor.m_uid);
                        RoutingWriteOps.PrewarmToPeersNear(gateZdo.GetPosition(), RoutingConfig.HubPrewarmRadius?.Value ?? 200f, newAnchor.m_uid);

                        state.NextRerollAt = now + rerollSeconds;
                    }
                }
                // If no candidate validated this tick, state.NextRerollAt is left in the past, so the
                // next OnUpdate tick (1s later) simply tries again - "try every tick until it succeeds".
            }
        }

        private static bool TryFindCandidate(RoutingRoguelikeGateDefinition gate, out Vector3 candidate, out Heightmap.Biome biome)
        {
            candidate = Vector3.zero;
            biome = Heightmap.Biome.None;
            float maxRadius = RoutingConfig.RerollMaxWorldRadius?.Value ?? 8000f;
            float margin = RoutingConfig.RerollHeightMargin?.Value ?? 2f;
            int attempts = RoutingConfig.RerollMaxAttemptsPerTick?.Value ?? 40;
            const float seaLevel = 30f;

            Heightmap.Biome? wantBiome = null;
            if (!string.IsNullOrEmpty(gate.BiomeFilter) && Enum.TryParse(gate.BiomeFilter, true, out Heightmap.Biome parsed))
            {
                wantBiome = parsed;
            }

            for (int i = 0; i < attempts; i++)
            {
                float angle = UnityEngine.Random.Range(0f, Mathf.PI * 2f);
                float radius = Mathf.Sqrt(UnityEngine.Random.Range(0f, 1f)) * maxRadius;
                float x = Mathf.Cos(angle) * radius;
                float z = Mathf.Sin(angle) * radius;

                float height = WorldGenerator.instance!.GetHeight(x, z);
                if (height < seaLevel + margin)
                {
                    continue;
                }
                var pos = new Vector3(x, height, z);
                if (ZoneSystem.IsLavaPreHeightmap(pos))
                {
                    continue;
                }
                Heightmap.Biome actualBiome = WorldGenerator.instance.GetBiome(x, z);
                if (wantBiome.HasValue && actualBiome != wantBiome.Value)
                {
                    continue;
                }
                WorldGenerator.instance.GetTerrainDelta(pos, 2f, out float delta, out _);
                if (delta > 2f)
                {
                    continue;
                }

                candidate = pos;
                biome = actualBiome;
                return true;
            }
            return false;
        }

        private static string Truncate(string s) => s.Length <= 10 ? s : s.Substring(0, 10);
    }
}
