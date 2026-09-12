using System.Collections.Generic;
using UnityEngine;
using TortalPortalLite.Core;
using TortalPortalLite.Subsystems.Foundations;

namespace TortalPortalLite.Subsystems.Enforcement
{
    /// <summary>
    /// #117 Raid-Time Geofenced Restriction. Locks only the portals inside an active RandEventSystem
    /// event's radius, so a base under attack cannot be escaped or reinforced through its own gate -
    /// genuinely server-authoritative (unlike the boss gate): RandEventSystem.instance.GetCurrentRandomEvent()
    /// is public and its `m_time`/`m_duration` only advance while the SERVER's own
    /// IsAnyPlayerInEventArea (over ZNet's own character ZDOs) is true, so the server already reasons
    /// about raid geometry with exactly the data a server-only mod has.
    ///
    /// Level-triggered reconciler (never edge-triggered - a raid that ends via SetRandomEvent(null,...)
    /// for ANY reason, including a client `RPC_ConsoleStartRandomEvent`, must be observed and unlocked
    /// just as readily as one that ends by timing out). Reads m_eventRange/m_duration off the LIVE
    /// RandomEvent every tick rather than hard-coding the 96f/60f defaults, since those are Inspector
    /// values the shipped raids may override.
    /// </summary>
    public static class LockdownRaidGeofenceEngine
    {
        private const string Reason = "raidgeofence";
        private static float _timer;
        private static string _engagedEventKey = ""; // event name + rounded pos, so a NEW raid starting before the old one's unlock finished re-derives scope correctly

        public static void OnUpdate(float dt)
        {
            bool enabled = LockdownConfig.RaidGeofenceEnabled?.Value == true || LockdownRulesetOverrides.RaidGeofenceForced;
            if (!enabled || RandEventSystem.instance == null)
            {
                if (_engagedEventKey.Length > 0)
                {
                    Disengage();
                }
                return;
            }

            _timer += dt;
            float interval = LockdownConfig.RaidGeofencePollSeconds?.Value ?? 1f;
            if (_timer < interval)
            {
                return;
            }
            _timer = 0f;
            Reconcile();
        }

        private static void Reconcile()
        {
            RandomEvent ev = RandEventSystem.instance.GetCurrentRandomEvent();
            if (ev == null)
            {
                if (_engagedEventKey.Length > 0)
                {
                    Disengage();
                }
                return;
            }

            string key = $"{ev.m_name}@{ev.m_pos.x:F0},{ev.m_pos.z:F0}";
            if (key == _engagedEventKey)
            {
                return; // Already locked for this exact raid instance.
            }
            if (_engagedEventKey.Length > 0)
            {
                Disengage(); // A different raid replaced the tracked one without us observing a null in between.
            }
            Engage(ev, key);
        }

        private static void Engage(RandomEvent ev, string key)
        {
            bool lockWholeTagGroup = LockdownConfig.RaidGeofenceLockWholeTagGroup?.Value == true;
            var scope = new List<PortalRecord>();
            var affectedTags = new HashSet<string>();

            foreach (PortalRecord rec in PortalCensus.Latest)
            {
                float dxz = Vector2.Distance(new Vector2(rec.Position.x, rec.Position.z), new Vector2(ev.m_pos.x, ev.m_pos.z));
                if (rec.Position.y <= 3000f && dxz < ev.m_eventRange)
                {
                    scope.Add(rec);
                    if (!string.IsNullOrEmpty(rec.Tag))
                    {
                        affectedTags.Add(rec.Tag);
                    }
                }
            }

            if (lockWholeTagGroup && affectedTags.Count > 0)
            {
                foreach (PortalRecord rec in PortalCensus.Latest)
                {
                    if (affectedTags.Contains(rec.Tag) && !scope.Contains(rec))
                    {
                        scope.Add(rec);
                    }
                }
            }

            if (scope.Count == 0)
            {
                _engagedEventKey = key; // Nothing in range - still remember the raid so we don't re-evaluate every tick, and unlock cleanly (no-op) when it ends.
                return;
            }

            _engagedEventKey = key;
            LockdownForceDisconnectEngine.EngageScope(scope, Reason);
            LockdownAnnouncementEngine.FloatingTextNear(ev.m_pos, ev.m_eventRange, "SEALED - RAID");
            PortalDebug.LogAlways($"[LockdownRaidGeofenceEngine] sealed {scope.Count} portal(s) for raid '{ev.m_name}' at {ev.m_pos:F0} (range {ev.m_eventRange:F0}m, tagGroup={lockWholeTagGroup}).");
        }

        private static void Disengage()
        {
            _engagedEventKey = "";
            LockdownForceDisconnectEngine.DisengageScope(Reason);
            PortalDebug.LogAlways("[LockdownRaidGeofenceEngine] raid ended - geofenced portals restored.");
        }
    }
}
