using System;
using UnityEngine;
using TortalPortalLite.Core;

namespace TortalPortalLite.Subsystems.WorldOps
{
    /// <summary>
    /// #233 "The Event Beacon - Persistent Events As Removable Portal Markers". `PersistentEventSystem`
    /// is a server-authoritative, world-persisted list (`m_activePersistentEvents.list`, SERVER decompile
    /// :105073) of `ActivePersistentEvent { sourceEventId, eventId, position, radius, startTime,
    /// duration }` (:104922-104939, confirmed field-for-field against the decompile by this build).
    /// Vanilla's own entry point (`TriggerEvent`/`RPC_RequestStartEvent`, :105341-105379) always picks a
    /// RANDOM world position via `GenerateEventLocation` - placement at a chosen point (a managed portal
    /// hub) is only possible by appending the list entry directly, exactly as this engine does, then
    /// calling the (Publicize=true-accessible) private `UpdateClientEventsList(0L)` (:105407-105410) to
    /// broadcast the whole list as JSON over the already-registered routed RPC "UpdateClientEventsList" -
    /// no new Harmony patch needed anywhere, this is calling an existing outbound path with our own data,
    /// the same pattern Core/Data/PlayerNotify.cs already uses for the "Message" RPC.
    ///
    /// On a vanilla client this drives, for free: (1) `Minimap.UpdatePersistentEventPins` adds an
    /// EventArea pin sized `radius*2` plus an animated map icon keyed by eventId, and REMOVES both the
    /// instant the entry disappears from the list - the only server-controlled map marker vanilla can
    /// REMOVE on its own; (2) `EnvMan`'s environment override for anyone standing inside the radius, if
    /// the source PersistentEvent asset defines one; (3) SpawnSystem's `m_requiredPersistentEvent` gate
    /// and (4) WearNTear's `m_eventDamage` gate, both keyed off the SAME `Source.internalName` this
    /// engine only ever resolves from the live `m_possibleEvents` list (never guessed/hard-coded, per
    /// this option's own prerequisite).
    ///
    /// Failure mode #233 itself flags as the dangerous one - `ActivePersistentEvent.Source` indexes
    /// `m_possibleEvents` with NO bounds check, and a bad index throws inside every vanilla client's own
    /// Minimap loop once a second - is guarded here by resolving the source event by NAME through
    /// `m_possibleEvents.FindIndex` and refusing to inject when it does not resolve, rather than ever
    /// accepting a raw index from a caller.
    /// </summary>
    public static class WildcardBEventBeaconEngine
    {
        /// <summary>
        /// Injects a new active persistent event centred at <paramref name="pos"/>. <paramref name="durationSeconds"/>
        /// &lt;= 0 means "no duration" (matches vanilla's own `duration = -1.0` convention, :104934 -
        /// never auto-expires via PersistentEventSystem.Update's own 30s sweep, :105209-105235; the
        /// caller - e.g. WildcardBEventGateEngine - is then responsible for calling <see cref="Remove"/>
        /// itself). Returns false (and logs) if <paramref name="sourceEventInternalName"/> does not
        /// resolve against the live m_possibleEvents list - the one bounds-check failure mode #233 names
        /// explicitly.
        /// </summary>
        public static bool TryInject(Vector3 pos, float radius, double durationSeconds, string sourceEventInternalName, out int eventId)
        {
            eventId = -1;
            if (WildcardBConfig.EventBeaconApiEnabled?.Value == false)
            {
                return false;
            }
            PersistentEventSystem sys = PersistentEventSystem.instance;
            if (sys == null || ZNet.instance == null || !ZNet.instance.IsServer())
            {
                return false;
            }
            if (string.IsNullOrEmpty(sourceEventInternalName))
            {
                return false;
            }

            int sourceEventId = sys.m_possibleEvents.FindIndex(e => e != null && string.Equals(e.internalName, sourceEventInternalName, StringComparison.OrdinalIgnoreCase));
            if (sourceEventId < 0 || sourceEventId >= sys.m_possibleEvents.Count)
            {
                PortalDebug.LogWarning($"[WildcardBEventBeaconEngine] '{sourceEventInternalName}' does not match any PersistentEventSystem.instance.m_possibleEvents internalName - refusing to inject (this is the exact bounds-check hazard #233 itself flags as breaking every client's map).");
                return false;
            }

            try
            {
                var activeEvent = new PersistentEventSystem.ActivePersistentEvent
                {
                    eventId = sys.m_eventIdCounter,
                    sourceEventId = sourceEventId,
                    position = pos,
                    radius = radius > 0f ? radius : 10f,
                    startTime = ZNet.instance.GetTimeSeconds(),
                    duration = durationSeconds > 0.0 ? durationSeconds : -1.0,
                };
                sys.m_eventIdCounter++;
                sys.m_activePersistentEvents.list.Add(activeEvent);
                sys.UpdateClientEventsList(0L);
                eventId = activeEvent.eventId;
                PortalDebug.LogAlways($"[WildcardBEventBeaconEngine] injected event '{sourceEventInternalName}' (id {eventId}) at {pos:F0}, radius {activeEvent.radius:F0}.");
                return true;
            }
            catch (Exception ex)
            {
                PortalDebug.LogError($"[WildcardBEventBeaconEngine] inject failed: {ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }

        /// <summary>Removes a previously-injected event by id (no-op, returns false, if not present - already expired via vanilla's own 30s sweep, or never existed).</summary>
        public static bool Remove(int eventId)
        {
            PersistentEventSystem sys = PersistentEventSystem.instance;
            if (sys == null || eventId < 0)
            {
                return false;
            }
            int index = sys.m_activePersistentEvents.list.FindIndex(e => e.eventId == eventId);
            if (index < 0)
            {
                return false;
            }
            try
            {
                sys.m_activePersistentEvents.list.RemoveAt(index);
                sys.UpdateClientEventsList(0L);
                PortalDebug.LogAlways($"[WildcardBEventBeaconEngine] removed event id {eventId}.");
                return true;
            }
            catch (Exception ex)
            {
                PortalDebug.LogWarning($"[WildcardBEventBeaconEngine] remove failed for id {eventId}: {ex.Message}");
                return false;
            }
        }
    }
}
