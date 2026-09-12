using System;
using System.Collections.Generic;
using UnityEngine;
using TortalPortalLite.Core;

namespace TortalPortalLite.Subsystems.PlayerInterface
{
    /// <summary>
    /// #266 Live Portal Markers - removable, labelled map markers via persistent events. The catalog's
    /// own case for this option: every other map-pin surface in this mod (#170) is permanent and
    /// unremovable by construction (`save: true` hard-coded, no removal RPC anywhere in the assembly).
    /// `PersistentEventSystem`'s own `ActivePersistentEvent` list is different - fully server-authoritative,
    /// world-saved, and REMOVABLE: this engine injects one synthetic entry per named portal (address
    /// book) pointing at the blandest available event type (no environment override, no graphical
    /// effect, nothing to spawn), broadcasts the whole list via the same private-but-publicized
    /// `UpdateClientEventsList` vanilla itself uses, and removes an entry the moment its portal is no
    /// longer named.
    ///
    /// Off by default (`UxConfig.LiveMarkersEnabled`) and radius-capped small - the catalog's own
    /// documented side effects (weather override, snow shader, spawner/WearNTear pieces that react to
    /// `m_requiredPersistentEvent`) all scale with the marker radius, so this stays deliberately tiny.
    /// Re-broadcasts periodically rather than reacting to `RequestStopEvent`/`RequestStartEvent` (both
    /// have NO sender check in vanilla - any client could delete or spawn a real event) - the same
    /// "re-assert beats prefix-veto" mitigation this codebase already uses for vanilla's own portal
    /// pairing pass, needing no new Harmony patch.
    /// </summary>
    public static class UxLivePortalMarkersEngine
    {
        private const int EventIdBase = 1_700_000;

        private static readonly Dictionary<ZDOID, int> _markerIdByPortal = new Dictionary<ZDOID, int>();
        private static int _blandSourceIndex = -1;
        private static int _nextEventId = EventIdBase;
        private static float _timer;

        public static void OnUpdate(float dt)
        {
            if (UxConfig.Enabled?.Value == false || UxConfig.LiveMarkersEnabled?.Value != true)
            {
                return;
            }
            ResolveBlandSourceIfNeeded();
            _timer += dt;
            float interval = UxConfig.LiveMarkersReassertSeconds?.Value ?? 30f;
            if (_timer < interval)
            {
                return;
            }
            _timer = 0f;
            Reassert();
        }

        private static void ResolveBlandSourceIfNeeded()
        {
            if (_blandSourceIndex >= 0 || PersistentEventSystem.instance == null)
            {
                return;
            }
            List<PersistentEventSystem.PersistentEvent> events = PersistentEventSystem.instance.m_possibleEvents;
            for (int i = 0; i < events.Count; i++)
            {
                PersistentEventSystem.PersistentEvent e = events[i];
                if (string.IsNullOrEmpty(e.environmentOverride) && e.graphicalEffects == 0 && (e.objectsToSpawn == null || e.objectsToSpawn.Count == 0))
                {
                    _blandSourceIndex = i;
                    return;
                }
            }
        }

        private static void Reassert()
        {
            if (PersistentEventSystem.instance == null || _blandSourceIndex < 0 || ZNet.instance == null)
            {
                return;
            }

            try
            {
                List<PersistentEventSystem.ActivePersistentEvent> list = PersistentEventSystem.instance.m_activePersistentEvents.list;
                float radius = UxConfig.LiveMarkersRadius?.Value ?? 6f;
                var wanted = new HashSet<ZDOID>();

                foreach (UxAddressBook.AddressEntry entry in UxAddressBook.Ordered())
                {
                    wanted.Add(entry.Record.Uid);
                    EnsureMarker(list, entry.Record.Uid, entry.Record.Position, radius);
                }

                var stale = new List<ZDOID>();
                foreach (ZDOID uid in _markerIdByPortal.Keys)
                {
                    if (!wanted.Contains(uid))
                    {
                        stale.Add(uid);
                    }
                }
                foreach (ZDOID uid in stale)
                {
                    RemoveMarker(list, uid);
                }

                PersistentEventSystem.instance.UpdateClientEventsList(0L); // originally private, publicized - broadcasts + re-asserts against any spoofed client-side removal
            }
            catch (Exception ex)
            {
                PortalDebug.LogError($"[UxLivePortalMarkersEngine] reassert failed: {ex.Message}");
            }
        }

        private static void EnsureMarker(List<PersistentEventSystem.ActivePersistentEvent> list, ZDOID portalUid, Vector3 pos, float radius)
        {
            if (_markerIdByPortal.TryGetValue(portalUid, out int eventId))
            {
                PersistentEventSystem.ActivePersistentEvent existing = list.Find(e => e.eventId == eventId);
                if (existing != null)
                {
                    existing.position = pos;
                    existing.radius = radius;
                    return;
                }
                // Fell out of the list (e.g. a spoofed RequestStopEvent) - fall through and recreate.
            }

            int newId = _nextEventId++;
            list.Add(new PersistentEventSystem.ActivePersistentEvent
            {
                eventId = newId,
                sourceEventId = _blandSourceIndex,
                position = pos,
                radius = radius,
                startTime = ZNet.instance.GetTimeSeconds(),
                duration = -1.0
            });
            _markerIdByPortal[portalUid] = newId;
        }

        private static void RemoveMarker(List<PersistentEventSystem.ActivePersistentEvent> list, ZDOID portalUid)
        {
            if (!_markerIdByPortal.TryGetValue(portalUid, out int eventId))
            {
                return;
            }
            _markerIdByPortal.Remove(portalUid);
            list.RemoveAll(e => e.eventId == eventId);
        }
    }
}
