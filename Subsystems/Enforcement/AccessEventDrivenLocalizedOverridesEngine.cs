using System;
using System.Collections.Generic;
using UnityEngine;
using TortalPortalLite.Core;

namespace TortalPortalLite.Subsystems.Enforcement
{
    /// <summary>
    /// #221 Event-Driven Localized Overrides. Because this mod runs ON the server, it can append an
    /// `ActivePersistentEvent` directly instead of going through the vanilla RPC handler - which matters,
    /// because that handler ALSO spawns `objectsToSpawn` via `Object.Instantiate` as a side effect
    /// (`PersistentEvent.objectsToSpawn` / `ObjectSpawnSettings.Create`), and the option's own failure
    /// mode is explicit that bypassing the handler avoids that. Mirrors the vanilla handler's own actual
    /// field usage exactly (it does not set `startTime`, relying on the struct's own field
    /// initialisers - `duration = -1.0` already means "permanent until stopped").
    ///
    /// `m_possibleEvents`/`m_eventIdCounter`/`UpdateClientEventsList` are accessed directly (no
    /// reflection) - the csproj's Publicize=true reference already makes every private member of
    /// assembly_valheim compile-time accessible, the same mechanism ZdoSpatialQuery.PrefabSetSweeper
    /// already relies on for `ZDOMan.m_objectsBySector`.
    ///
    /// `needs-ingame-check` per the catalog's own feasibility rating: whether `m_possibleEvents` is
    /// non-empty and what effects it contains is Inspector data this mod cannot see ahead of time - if
    /// it is empty on this server build, MarkPortal below fails cleanly with a clear reason rather than
    /// throwing.
    /// </summary>
    public static class AccessEventDrivenLocalizedOverridesEngine
    {
        private static readonly Dictionary<Vector3, int> _eventIdByPortalPosition = new Dictionary<Vector3, int>();

        /// <summary>Marks a portal with a chosen (or default, index 0) possible-event's ambient effect + a removable map pin, bypassing the vanilla handler's own object-spawn side effect.</summary>
        public static string MarkPortal(Vector3 portalPos, float radius, int sourceEventId = 0)
        {
            if (PersistentEventSystem.instance == null)
            {
                return "PersistentEventSystem not ready yet.";
            }
            if (PersistentEventSystem.instance.m_possibleEvents == null || sourceEventId < 0 || sourceEventId >= PersistentEventSystem.instance.m_possibleEvents.Count)
            {
                return "no possible event at that index on this server build - nothing to attach (m_possibleEvents is Inspector data this mod cannot populate).";
            }

            try
            {
                var activeEvent = new PersistentEventSystem.ActivePersistentEvent
                {
                    eventId = PersistentEventSystem.instance.m_eventIdCounter,
                    sourceEventId = sourceEventId,
                    position = portalPos,
                    radius = radius
                };
                PersistentEventSystem.instance.m_eventIdCounter++;
                PersistentEventSystem.instance.m_activePersistentEvents.list.Add(activeEvent);
                PersistentEventSystem.instance.UpdateClientEventsList(0L);

                Vector3 key = AccessAclStore.RoundPos(portalPos);
                _eventIdByPortalPosition[key] = activeEvent.eventId;
                return $"tpl: marked portal at {portalPos} with event #{activeEvent.eventId} (radius {radius}).";
            }
            catch (Exception ex)
            {
                PortalDebug.LogWarning($"[AccessEventDrivenLocalizedOverridesEngine] MarkPortal failed: {ex.GetType().Name}: {ex.Message}");
                return $"tpl: failed to mark portal - {ex.Message}";
            }
        }

        /// <summary>The removable half - a server-driven pin/effect the server can also take away (correcting the UX brief's "no way to remove a pushed pin", per this option's own citation).</summary>
        public static string UnmarkPortal(Vector3 portalPos)
        {
            if (PersistentEventSystem.instance == null)
            {
                return "PersistentEventSystem not ready yet.";
            }
            Vector3 key = AccessAclStore.RoundPos(portalPos);
            if (!_eventIdByPortalPosition.TryGetValue(key, out int eventId))
            {
                return "tpl: no tracked event mark at that position.";
            }
            _eventIdByPortalPosition.Remove(key);

            try
            {
                // #210's own citation: vanilla's own RPC_RequestStopEvent has NO -1 guard on FindIndex -
                // guard it here rather than reproduce that crash.
                int index = PersistentEventSystem.instance.m_activePersistentEvents.list.FindIndex(x => x.eventId == eventId);
                if (index < 0)
                {
                    return "tpl: event already gone (expired or removed elsewhere).";
                }
                PersistentEventSystem.instance.m_activePersistentEvents.list.RemoveAt(index);
                PersistentEventSystem.instance.UpdateClientEventsList(0L);
                return $"tpl: cleared event #{eventId}.";
            }
            catch (Exception ex)
            {
                PortalDebug.LogWarning($"[AccessEventDrivenLocalizedOverridesEngine] UnmarkPortal failed: {ex.GetType().Name}: {ex.Message}");
                return $"tpl: failed to clear event - {ex.Message}";
            }
        }
    }
}
