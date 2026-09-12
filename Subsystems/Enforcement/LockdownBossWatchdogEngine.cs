using System.Collections.Generic;
using TortalPortalLite.Core;
using TortalPortalLite.Core.Data;

namespace TortalPortalLite.Subsystems.Enforcement
{
    /// <summary>
    /// #116 Boss Auto-Lockdown with activeBosses Watchdog, corrected by #249 NoBossPortals Gate Model
    /// Correction. The two client gates that read NoBossPortals are NOT the same condition the original
    /// catalog entry assumed:
    ///
    ///  - World portals (TeleportWorld.Teleport gate 3): blocked when NoBossPortals is set AND
    ///    (RandEventSystem.instance.GetBossEvent() != null OR the activeBosses float key is &gt; 0).
    ///    GetBossEvent() is a LOCAL EnemyHud lookup (100m, alerted-boss-on-THIS-player's-screen) that the
    ///    server can never observe or influence - #249's correction is explicit that this mod's only
    ///    server-observable lever is the activeBosses key, never RandEventSystem's boss event.
    ///  - Dungeon/crypt doors (Teleport.Interact): a narrower, HUD-dependent, same-zone check with no
    ///    server presence whatsoever (class Teleport has no ZNetView/ZDO/RPC) - unreachable from the server
    ///    by any means, documented rather than attempted.
    ///
    /// So this engine's actual job is two-fold: (1) hold NoBossPortals set/cleared per
    /// LockdownConfig.BossLockdownEnabled - once set, vanilla's OWN gate logic already does the "only
    /// during a fight" part correctly on both the key half (world-wide) and the HUD half (per-player,
    /// server-invisible); (2) the REAL work - `BaseAI.SetAlerted` increments activeBosses once per boss
    /// and latches `s_bossCount` unconditionally, so the intended decrement on de-alert can never run; the
    /// only decrement is `Character.OnDeath`. A boss that alerts and then has its owning client disconnect
    /// (zone unloads) leaks the counter forever, permanently blocking every portal on the server. The
    /// watchdog scans for a live ALERTED boss ZDO (s_alert bool, corrected per #249 to be the actual live
    /// signal, never GetBossEvent) and only zeroes the counter after several consecutive clean samples -
    /// never on a single sample, since a boss that is alive but momentarily unowned would otherwise be
    /// falsely cleared mid-fight.
    /// </summary>
    public static class LockdownBossWatchdogEngine
    {
        private static readonly HashSet<int> _bossPrefabHashes = new HashSet<int>();
        private static bool _prefabsDiscovered;

        private static float _pollTimer;
        private static int _cleanSamples;
        private static ZdoSpatialQuery.PrefabSetSweeper _sweeper;

        public static void OnUpdate(float dt)
        {
            if (ZoneSystem.instance == null)
            {
                return;
            }

            bool wantLockdown = LockdownConfig.BossLockdownEnabled?.Value == true || LockdownRulesetOverrides.BossLockdownForced;
            LockdownGlobalKeyEngine.SetNoBossPortals(wantLockdown);

            if (!wantLockdown)
            {
                return;
            }

            DiscoverBossPrefabsOnce();

            _pollTimer += dt;
            float interval = LockdownConfig.BossWatchdogPollSeconds?.Value ?? 30f;
            if (_pollTimer < interval)
            {
                return;
            }
            _pollTimer = 0f;
            RunWatchdog();
        }

        private static void DiscoverBossPrefabsOnce()
        {
            if (_prefabsDiscovered || ZNetScene.instance == null)
            {
                return;
            }
            _prefabsDiscovered = true;
            int found = 0;
            foreach (string name in ZNetScene.instance.GetPrefabNames())
            {
                try
                {
                    UnityEngine.GameObject prefab = ZNetScene.instance.GetPrefab(name);
                    Character character = prefab != null ? prefab.GetComponent<Character>() : null;
                    if (character != null && character.m_boss)
                    {
                        _bossPrefabHashes.Add(name.GetStableHashCode());
                        found++;
                    }
                }
                catch (System.Exception ex)
                {
                    PortalDebug.LogWarning($"[LockdownBossWatchdogEngine] prefab '{name}' inspection failed: {ex.Message}");
                }
            }
            _sweeper = new ZdoSpatialQuery.PrefabSetSweeper(_bossPrefabHashes);
            PortalDebug.LogAlways($"[LockdownBossWatchdogEngine] discovered {found} boss prefab(s).");
        }

        private static void RunWatchdog()
        {
            if (!ZoneSystem.instance.GetGlobalKey(GlobalKeys.activeBosses, out float value) || value <= 0f)
            {
                _cleanSamples = 0;
                return; // Counter already at/below zero - nothing leaked.
            }

            bool anyLiveAlerted = FindLiveAlertedBoss();
            if (anyLiveAlerted)
            {
                _cleanSamples = 0;
                return;
            }

            _cleanSamples++;
            int required = LockdownConfig.BossWatchdogRequiredSamples?.Value ?? 3;
            if (_cleanSamples < required)
            {
                return;
            }
            _cleanSamples = 0;

            try
            {
                ZoneSystem.instance.SetGlobalKey(GlobalKeys.activeBosses, 0f);
                PortalDebug.LogAlways($"[LockdownBossWatchdogEngine] activeBosses was {value:F0} with no live alerted boss found after {required} consecutive checks - reset to 0.");
            }
            catch (System.Exception ex)
            {
                PortalDebug.LogError($"[LockdownBossWatchdogEngine] failed to reset activeBosses: {ex.Message}");
            }
        }

        private static bool FindLiveAlertedBoss()
        {
            if (_bossPrefabHashes.Count == 0 || ZDOMan.instance == null)
            {
                return false;
            }
            var results = new List<ZDO>();
            // A generous per-call sector budget - this only runs on the (rare) poll tick where the
            // counter is already suspected leaked, never every frame, so a slightly heavier one-shot scan
            // here is an acceptable trade for not needing a persistent multi-tick accumulator.
            _sweeper.Advance(4096, results);
            foreach (ZDO zdo in results)
            {
                if (zdo != null && zdo.IsValid() && zdo.GetBool(ZDOVars.s_alert))
                {
                    return true;
                }
            }
            return false;
        }
    }
}
