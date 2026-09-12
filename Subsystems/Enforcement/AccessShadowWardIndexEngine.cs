using System;
using System.Collections.Generic;
using UnityEngine;
using TortalPortalLite.Core;
using TortalPortalLite.Core.Data;

namespace TortalPortalLite.Subsystems.Enforcement
{
    /// <summary>One indexed ward, rebuilt purely from ZDO data - never from PrivateArea.CheckAccess (see class doc).</summary>
    public readonly struct WardCoverage
    {
        public readonly Vector3 Centre;
        public readonly float Radius;
        public readonly long CreatorPlayerId;
        public readonly bool Enabled;
        public readonly HashSet<long> Permitted;

        public WardCoverage(Vector3 centre, float radius, long creatorPlayerId, bool enabled, HashSet<long> permitted)
        {
            Centre = centre;
            Radius = radius;
            CreatorPlayerId = creatorPlayerId;
            Enabled = enabled;
            Permitted = permitted;
        }
    }

    /// <summary>
    /// #52 Shadow Ward Index. `PrivateArea.CheckAccess` is a silent allow-all on a dedicated server: it
    /// iterates the static `m_allAreas` list, which only ever gets appended to from `PrivateArea.Awake`
    /// on a live GameObject - a headless server instantiates none, so the list is permanently empty and
    /// the method returns true (vacuously) for every point in the world. Any server-side code that calls
    /// it as an authorization check silently permits everything - the single most dangerous trap this
    /// option names. This engine rebuilds the same coverage purely from ward ZDOs instead.
    ///
    /// Radius is Inspector data (`PrivateArea.m_radius`, a genuinely public field, default 10f) with no
    /// networked value - resolved once per distinct prefab hash from ZNetScene's own prefab GameObject,
    /// cached, falling back to AccessConfig.WardDefaultRadius if the prefab can't be resolved (e.g. a
    /// ward whose prefab entry never loaded on this server for some reason).
    ///
    /// Discovery: every prefab in ZNetScene.instance.m_prefabs carrying a PrivateArea component becomes
    /// a tracked prefab hash; ZdoSpatialQuery.PrefabSetSweeper walks the whole sector array once
    /// (budgeted per tick) rather than re-scanning per prefab name the way
    /// GetAllZDOsWithPrefabIterative does. Incremental updates between resweeps arrive from
    /// RpcZdoDataHook's postfix (any client write to a tracked ward ZDO immediately updates that one
    /// entry) - the full resweep only needs to run occasionally to pick up wards outside anyone's
    /// current sector residency (see the option's own "not world-resident like portals" failure mode).
    /// </summary>
    public static class AccessShadowWardIndexEngine
    {
        private static readonly HashSet<int> _wardPrefabHashes = new HashSet<int>();
        private static readonly Dictionary<int, float> _radiusByPrefab = new Dictionary<int, float>();
        private static ZdoSpatialQuery.PrefabSetSweeper? _sweeper;

        private static readonly Dictionary<ZDOID, WardCoverage> _wards = new Dictionary<ZDOID, WardCoverage>();
        private static float _timer;
        private static bool _discovered;

        public static void OnWorldReady()
        {
            Discover();
        }

        public static void OnUpdate(float dt)
        {
            if (!_discovered)
            {
                Discover();
            }
            if (_sweeper == null || ZDOMan.instance == null)
            {
                return;
            }

            _timer += dt;
            float interval = AccessConfig.WardResweepSeconds?.Value ?? 45f;
            if (_timer < interval)
            {
                return;
            }

            var buffer = new List<ZDO>();
            int budget = AccessConfig.WardSectorBudgetPerTick?.Value ?? 200;
            bool completedPass = _sweeper.Advance(budget, buffer);
            foreach (ZDO zdo in buffer)
            {
                Index(zdo);
            }
            if (completedPass)
            {
                _timer = 0f;
            }
        }

        /// <summary>Called from RpcZdoDataHook (see AccessTagWatchdogEngine's shared registration) for any ZDO whose prefab is a tracked ward prefab.</summary>
        public static void OnWardZdoChanged(ZDO zdo)
        {
            if (zdo == null || !zdo.IsValid())
            {
                return;
            }
            Index(zdo);
        }

        public static bool IsWardPrefab(int prefabHash) => _wardPrefabHashes.Contains(prefabHash);

        /// <summary>True if any enabled, indexed ward covering <paramref name="pos"/> permits <paramref name="playerId"/> (creator or on its permitted list).</summary>
        public static bool WardCovers(Vector3 pos, long playerId, out bool anyWardPresent)
        {
            anyWardPresent = false;
            foreach (WardCoverage ward in _wards.Values)
            {
                if (!ward.Enabled)
                {
                    continue;
                }
                float dx = ward.Centre.x - pos.x;
                float dz = ward.Centre.z - pos.z;
                if ((dx * dx + dz * dz) > ward.Radius * ward.Radius)
                {
                    continue;
                }
                anyWardPresent = true;
                if (ward.CreatorPlayerId == playerId || ward.Permitted.Contains(playerId))
                {
                    return true;
                }
            }
            return false;
        }

        private static void Discover()
        {
            if (ZNetScene.instance == null)
            {
                return;
            }
            _discovered = true;
            _wardPrefabHashes.Clear();
            _radiusByPrefab.Clear();

            foreach (GameObject prefab in ZNetScene.instance.m_prefabs)
            {
                if (prefab == null)
                {
                    continue;
                }
                PrivateArea area = prefab.GetComponent<PrivateArea>();
                if (area == null)
                {
                    continue;
                }
                int hash = prefab.name.GetStableHashCode();
                _wardPrefabHashes.Add(hash);
                _radiusByPrefab[hash] = area.m_radius > 0f ? area.m_radius : (AccessConfig.WardDefaultRadius?.Value ?? 10f);
            }

            _sweeper = new ZdoSpatialQuery.PrefabSetSweeper(_wardPrefabHashes);
            PortalDebug.LogAlways($"[AccessShadowWardIndexEngine] tracking {_wardPrefabHashes.Count} ward prefab(s).");
        }

        private static void Index(ZDO zdo)
        {
            try
            {
                int prefab = zdo.GetPrefab();
                if (!_wardPrefabHashes.Contains(prefab))
                {
                    return;
                }
                // #52's own failure mode: a faction-owned ward (m_ownerFaction != 0) is a different code
                // path (PrivateArea.Interact early-returns) and must be excluded from the index. The
                // exact persisted key name for m_ownerFaction was not confirmed against the decompile
                // (PrivateArea's own Awake/Save were not traced this deeply) - "faction" is a best-effort
                // guess and a wrong key here just means this guard never trips (GetInt returns its
                // default), not a compile or crash risk. Re-verify against the decompile before relying
                // on faction exclusion in production.
                if (zdo.GetInt("faction", 0) != 0)
                {
                    _wards.Remove(zdo.m_uid);
                    return;
                }

                bool enabled = zdo.GetBool(ZDOVars.s_enabled, false);
                long creator = zdo.GetLong(ZDOVars.s_creator, 0L);
                float radius = _radiusByPrefab.TryGetValue(prefab, out float r) ? r : (AccessConfig.WardDefaultRadius?.Value ?? 10f);

                var permitted = new HashSet<long>();
                int count = zdo.GetInt(ZDOVars.s_permitted, 0);
                for (int i = 0; i < count; i++)
                {
                    long id = zdo.GetLong("pu_id" + i, 0L);
                    if (id != 0L)
                    {
                        permitted.Add(id);
                    }
                }

                _wards[zdo.m_uid] = new WardCoverage(zdo.GetPosition(), radius, creator, enabled, permitted);
            }
            catch (Exception ex)
            {
                PortalDebug.LogWarning($"[AccessShadowWardIndexEngine] failed to index {zdo?.m_uid}: {ex.Message}");
            }
        }
    }
}
