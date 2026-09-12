using System.Collections.Generic;
using UnityEngine;
using TortalPortalLite.Core;
using TortalPortalLite.Core.Data;
using TortalPortalLite.Subsystems.Foundations;

namespace TortalPortalLite.Subsystems.Enforcement
{
    /// <summary>
    /// #247 Raid Beacon, #248 Event Slot Governor, #259 Beacon Cleanup Sweep - a tightly-coupled trio
    /// sharing vanilla's single random-event slot, so they live in one file.
    ///
    /// #247: `RandEventSystem.SetRandomEventByName(name, pos)` is public and genuinely server-authoritative
    /// (ZNet.instance.IsServer() gate inside SetRandomEvent) - starting it at a locked gate turns vanilla's
    /// OWN centre-screen message, forced music/weather, animated map pin and client-run spawners into the
    /// lockdown's own announcement and hazard, for free.
    ///
    /// #248: vanilla has exactly ONE `m_randomEvent` slot, so an organic raid (or another SetRandomEvent
    /// call) can evict a beacon at any time, and a beacon squats the slot so no organic raid can start
    /// near a managed gate either. The catalog's own prescribed defence is a prefix on the private
    /// `SetRandomEvent(RandomEvent, Vector3)`/public `StartRandomEvent()` - Core/Hooks/ has no broker for
    /// RandEventSystem at all:
    /// NEEDS NEW HOOK BROKER on RandEventSystem.SetRandomEvent(RandomEvent,Vector3) / StartRandomEvent():
    /// purpose - true zero-flicker veto of an organic raid landing on a managed hub or evicting an armed
    /// beacon. Until that broker exists, this engine runs a REACTIVE corrector instead: every OnUpdate
    /// tick (i.e. every frame, via LockdownSubsystem's own dispatch) it re-reads the live
    /// GetCurrentRandomEvent() and re-claims the slot the instant it observes drift - in practice a
    /// same-tick correction rather than a true prevention, which the catalog itself treats as an
    /// acceptable "detect and revert" tier when no veto broker is available (see RpcZdoDataHook's own
    /// doc comment on the same tradeoff).
    ///
    /// #259: on beacon end (or an observed raid-at-a-locked-gate ending), sweeps s_eventCreature ZDOs near
    /// the gate and destroys them server-side rather than waiting for BaseAI's own slow client-side
    /// despawn walk (which only fires for an unaggroed, untargeted creature).
    /// </summary>
    public static class LockdownRaidBeaconEngine
    {
        private static bool _armed;
        private static Vector3 _gatePos;
        private static string _eventName = "";

        public static bool IsArmed => _armed;

        public static void StartBeacon(Vector3 gatePos)
        {
            if (RandEventSystem.instance == null || LockdownConfig.RaidBeaconEnabled?.Value != true)
            {
                return;
            }
            _armed = true;
            _gatePos = gatePos;
            _eventName = LockdownConfig.RaidBeaconEventName?.Value ?? "army_eikthyr";
            TryClaimSlot();
            PortalDebug.LogAlways($"[LockdownRaidBeaconEngine] beacon '{_eventName}' armed at {gatePos:F0}.");
        }

        public static void StopBeacon()
        {
            if (!_armed)
            {
                return;
            }
            _armed = false;
            try
            {
                RandEventSystem.instance?.ResetRandomEvent();
            }
            catch (System.Exception ex)
            {
                PortalDebug.LogWarning($"[LockdownRaidBeaconEngine] ResetRandomEvent failed: {ex.Message}");
            }
            BeaconCleanupSweep(_gatePos, LockdownConfig.RaidBeaconEventName?.Value != null ? 96f : 96f);
            PortalDebug.LogAlways("[LockdownRaidBeaconEngine] beacon disarmed.");
        }

        public static void OnUpdate(float dt)
        {
            if (RandEventSystem.instance == null)
            {
                return;
            }

            if (_armed)
            {
                if (LockdownConfig.RaidBeaconEnabled?.Value != true)
                {
                    StopBeacon();
                    return;
                }
                TryClaimSlot();
                return;
            }

            if (LockdownConfig.EventSlotGuardOrganicNearHubs?.Value == true)
            {
                GuardOrganicRaidsNearHubs();
            }
        }

        private static void TryClaimSlot()
        {
            RandomEvent current = RandEventSystem.instance.GetCurrentRandomEvent();
            if (current != null && current.m_name == _eventName)
            {
                return; // Already holding the slot with our own beacon.
            }
            try
            {
                RandEventSystem.instance.SetRandomEventByName(_eventName, _gatePos);
            }
            catch (System.Exception ex)
            {
                PortalDebug.LogWarning($"[LockdownRaidBeaconEngine] SetRandomEventByName('{_eventName}') failed: {ex.Message}");
            }
        }

        private static void GuardOrganicRaidsNearHubs()
        {
            RandomEvent current = RandEventSystem.instance.GetCurrentRandomEvent();
            if (current == null)
            {
                return;
            }
            float hubRadius = LockdownConfig.EventSlotHubRadius?.Value ?? 150f;
            // "Managed hub" proxy: any position this domain currently has vaulted (force-disconnected or
            // quarantined) counts as a hub worth protecting - the cheapest available signal without a
            // dedicated hub-declaration file of its own.
            foreach (PortalRecord rec in PortalCensus.Latest)
            {
                if (!LockdownVault.IsLocked(rec.Position))
                {
                    continue;
                }
                float dxz = Vector2.Distance(new Vector2(rec.Position.x, rec.Position.z), new Vector2(current.m_pos.x, current.m_pos.z));
                if (dxz < hubRadius)
                {
                    try
                    {
                        RandEventSystem.instance.ResetRandomEvent();
                        PortalDebug.LogAlways($"[LockdownRaidBeaconEngine] cleared organic event '{current.m_name}' rolled within {hubRadius:F0}m of managed hub at {rec.Position:F0}.");
                    }
                    catch (System.Exception ex)
                    {
                        PortalDebug.LogWarning($"[LockdownRaidBeaconEngine] ResetRandomEvent (guard) failed: {ex.Message}");
                    }
                    return;
                }
            }
        }

        /// <summary>#259: destroys leftover event-creature ZDOs near <paramref name="center"/> - shares the destroy primitive with #254 Portal Sanctuary rather than re-deriving it.</summary>
        public static void BeaconCleanupSweep(Vector3 center, float radius)
        {
            List<ZDO> nearby = ZdoSpatialQuery.FindNear(center, radius + 32f);
            int budget = LockdownConfig.BeaconCleanupBudgetPerTick?.Value ?? 32;
            int destroyed = 0;
            foreach (ZDO zdo in nearby)
            {
                if (destroyed >= budget)
                {
                    break;
                }
                if (zdo == null || !zdo.IsValid() || !zdo.GetBool(ZDOVars.s_eventCreature))
                {
                    continue;
                }
                if (LockdownPortalSanctuaryEngine.TryDestroyHostile(zdo, skipIfAlertNearPlayer: true))
                {
                    destroyed++;
                }
            }
            if (destroyed > 0)
            {
                PortalDebug.LogInfo($"[LockdownRaidBeaconEngine] cleanup swept {destroyed} leftover event creature(s) near {center:F0}.");
            }
        }
    }
}
