using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using TortalPortalLite.Core;
using TortalPortalLite.Core.Data;
using TortalPortalLite.Subsystems.Topology;

namespace TortalPortalLite.Subsystems.WorldOps
{
    /// <summary>
    /// #131 "Ephemeral Event Gates" - portals the server conjures for the duration of an event and
    /// destroys afterwards, with no player ever placing a piece. This engine reuses, rather than
    /// re-derives, the verified five-line ZDO-fabrication recipe (`ZDOMan.CreateNewZDO` +
    /// `Persistent=true` + `Type=Default` + `Distant=false` + `SetPrefab` + `SetRotation`, catalog's own
    /// citation set: :76674-76692, :77732-77752, :82612-82620) via
    /// Subsystems.Topology.TargetedPhantomPortalFactory.CreateStandalonePair - the exact "two brand-new
    /// phantom ZDOs paired reciprocally to EACH OTHER" shape #131 itself asks for (neither end is an
    /// existing player-placed portal), already built and verified in Wave 1 for #207 Corpse-Run Gate.
    /// Reusing it also means the pair automatically carries the SAME literal tag on both ends -
    /// `Game.ConnectPortals`'s own reconcile predicate (SERVER decompile :100589-100606) - so vanilla's
    /// 5s reconciler leaves a freshly-opened gate alone from the first tick, with no manual
    /// SetConnection race to manage.
    ///
    /// Lifecycle: `OpenGate`/`CloseGate` are a public API any other engine in this mod's process can call
    /// (e.g. #240 Ambush Gates deliberately does NOT call these - a raid locks an EXISTING portal's own
    /// Connection rather than fabricating a new one - but a future engine that wants a timed portal has
    /// this ready). This file also wires ONE concrete, config-driven demo trigger of the kind the
    /// catalog's own howItWorks names ("a boss key appearing... Harmony postfix on
    /// ZoneSystem.GlobalKeyAdd, the BossDefeatWatch pattern"): rather than adding a second Harmony patch
    /// on GlobalKeyAdd (Core/Hooks/ has no broker for it and this mod's rule is to flag, not add, a new
    /// one), this engine polls `ZoneSystem.instance.GetGlobalKey(name)` (already public, already used
    /// read-only elsewhere in this mod - Subsystems.Topology.TargetedLocationRegistry.IsGlobalKeySet)
    /// once per tick and reacts on the false-&gt;true edge, which is observationally identical to a
    /// postfix hook for a key that (per vanilla's own GlobalKeyAdd, :113483-113511) is only ever set once
    /// and never cleared.
    ///
    /// Every fabricated ZDO is validated against #143/#244's own guard
    /// (WildcardBSectorZeroEngine.IsSafeCoordinate) before creation, and hardened against the
    /// structural-integrity pass (#235, WildcardBPhantomSurvivalEngine) immediately after - an ephemeral
    /// gate that gets torn down by WearNTear before its own timer would be indistinguishable from a bug.
    /// </summary>
    public static class WildcardBEventGateEngine
    {
        private sealed class GateRecord
        {
            public ZDO A;
            public ZDO B;
            public float RemainingSeconds;
            public int BeaconEventId = -1;
        }

        private static readonly Dictionary<string, GateRecord> _gates = new Dictionary<string, GateRecord>();
        private static string? _demoGateId;
        private static bool _lastDemoKeyState;

        // ------------------------------------------------------------------------------ public API

        /// <summary>
        /// Opens a brand-new reciprocal ephemeral portal pair at <paramref name="gatePos"/> (what a
        /// player walks into) leading to <paramref name="destPos"/>. Returns null on failure (unsafe
        /// coordinate, or the phantom factory itself failed - e.g. no portal prefab hash resolved yet).
        /// <paramref name="durationSeconds"/> &lt;= 0 means "open until CloseGate is called explicitly".
        /// </summary>
        public static string? OpenGate(Vector3 gatePos, Quaternion gateRot, Vector3 destPos, Quaternion destRot, string label, float durationSeconds,
            bool spawnBeacon = false, string? beaconEventName = null, float beaconRadius = 20f)
        {
            if (!WildcardBSectorZeroEngine.IsSafeCoordinate(gatePos) || !WildcardBSectorZeroEngine.IsSafeCoordinate(destPos))
            {
                PortalDebug.LogWarning($"[WildcardBEventGateEngine] refused to open gate at {gatePos:F0} -> {destPos:F0}: one end fails the #143/#244 Sector Zero safety guard.");
                return null;
            }

            string tag = string.IsNullOrEmpty(label) ? "Event" : label;
            if (tag.Length > TagCodec.MaxTagLength)
            {
                tag = tag.Substring(0, TagCodec.MaxTagLength);
            }

            (ZDO? a, ZDO? b) = TargetedPhantomPortalFactory.CreateStandalonePair(gatePos, gateRot, "EventGate", destPos, destRot, "EventGateExit", tag);
            if (a == null || b == null)
            {
                PortalDebug.LogWarning("[WildcardBEventGateEngine] CreateStandalonePair failed - no ephemeral gate opened.");
                return null;
            }

            WildcardBPhantomSurvivalEngine.ApplyIfEnabled(a);
            WildcardBPhantomSurvivalEngine.ApplyIfEnabled(b);

            var record = new GateRecord
            {
                A = a,
                B = b,
                RemainingSeconds = durationSeconds > 0f ? durationSeconds : float.MaxValue,
            };

            if (spawnBeacon && !string.IsNullOrEmpty(beaconEventName) && WildcardBConfig.EventBeaconApiEnabled?.Value != false)
            {
                if (WildcardBEventBeaconEngine.TryInject(gatePos, beaconRadius, durationSeconds > 0f ? durationSeconds : -1.0, beaconEventName!, out int eventId))
                {
                    record.BeaconEventId = eventId;
                }
            }

            string id = Guid.NewGuid().ToString("N");
            _gates[id] = record;
            PortalDebug.LogAlways($"[WildcardBEventGateEngine] opened ephemeral gate '{id}' ({tag}) at {gatePos:F0} -> {destPos:F0}, duration={(durationSeconds > 0f ? durationSeconds.ToString("F0") + "s" : "until closed")}.");
            return id;
        }

        public static bool IsOpen(string id) => id != null && _gates.ContainsKey(id);

        public static void CloseGate(string? id)
        {
            if (id == null || !_gates.TryGetValue(id, out GateRecord record))
            {
                return;
            }
            TargetedPhantomPortalFactory.DestroyStandalone(record.A);
            TargetedPhantomPortalFactory.DestroyStandalone(record.B);
            if (record.BeaconEventId >= 0)
            {
                WildcardBEventBeaconEngine.Remove(record.BeaconEventId);
            }
            _gates.Remove(id);
            PortalDebug.LogAlways($"[WildcardBEventGateEngine] closed ephemeral gate '{id}'.");
        }

        // ------------------------------------------------------------------------------------ tick

        public static void OnUpdate(float dt)
        {
            TickExpiry(dt);
            TickDemoTrigger();
        }

        private static void TickExpiry(float dt)
        {
            if (_gates.Count == 0)
            {
                return;
            }
            List<string>? expired = null;
            foreach (KeyValuePair<string, GateRecord> kv in _gates)
            {
                kv.Value.RemainingSeconds -= dt;
                if (kv.Value.RemainingSeconds <= 0f)
                {
                    (expired ??= new List<string>()).Add(kv.Key);
                }
            }
            if (expired == null)
            {
                return;
            }
            foreach (string id in expired)
            {
                CloseGate(id);
                if (id == _demoGateId)
                {
                    _demoGateId = null;
                }
            }
        }

        private static void TickDemoTrigger()
        {
            if (WildcardBConfig.EventGateEnabled?.Value != true || ZoneSystem.instance == null)
            {
                return;
            }
            string keyName = WildcardBConfig.EventGateTriggerGlobalKey?.Value ?? "";
            if (string.IsNullOrEmpty(keyName))
            {
                return;
            }

            bool keySet = TargetedLocationRegistry.IsGlobalKeySet(keyName);
            bool edge = keySet && !_lastDemoKeyState;
            _lastDemoKeyState = keySet;
            if (!edge || _demoGateId != null)
            {
                return;
            }

            if (!TryParseVector3(WildcardBConfig.EventGateAtPosition?.Value, out Vector3 gatePos) ||
                !TryParseVector3(WildcardBConfig.EventGateDestPosition?.Value, out Vector3 destPos))
            {
                PortalDebug.LogWarning("[WildcardBEventGateEngine] demo trigger fired but GatePosition/DestPosition are not valid 'x,y,z' strings - skipping.");
                return;
            }

            Quaternion gateRot = SafeLookRotation(destPos - gatePos);
            Quaternion destRot = SafeLookRotation(gatePos - destPos);
            string label = WildcardBConfig.EventGateLabel?.Value ?? "Event";
            float duration = WildcardBConfig.EventGateDurationSeconds?.Value ?? 1200f;
            bool spawnBeacon = WildcardBConfig.EventGateSpawnBeacon?.Value == true;
            string beaconEventName = WildcardBConfig.EventGateBeaconEventName?.Value ?? "";
            float beaconRadius = WildcardBConfig.EventGateBeaconRadius?.Value ?? 20f;

            _demoGateId = OpenGate(gatePos, gateRot, destPos, destRot, label, duration, spawnBeacon, beaconEventName, beaconRadius);
        }

        private static Quaternion SafeLookRotation(Vector3 dir)
        {
            dir.y = 0f;
            return dir.sqrMagnitude > 0.001f ? Quaternion.LookRotation(dir.normalized, Vector3.up) : Quaternion.identity;
        }

        private static bool TryParseVector3(string? s, out Vector3 result)
        {
            result = Vector3.zero;
            if (string.IsNullOrWhiteSpace(s))
            {
                return false;
            }
            string[] parts = s.Split(',');
            if (parts.Length != 3) return false;
            if (!float.TryParse(parts[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float x)) return false;
            if (!float.TryParse(parts[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float y)) return false;
            if (!float.TryParse(parts[2].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float z)) return false;
            result = new Vector3(x, y, z);
            return true;
        }
    }
}
