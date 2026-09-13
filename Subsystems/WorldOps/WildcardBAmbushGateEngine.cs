using System.Collections.Generic;
using UnityEngine;
using TortalPortalLite.Core;
using TortalPortalLite.Core.Data;
using TortalPortalLite.Subsystems.Foundations;

namespace TortalPortalLite.Subsystems.WorldOps
{
    /// <summary>
    /// #240 "Ambush Gates - Arrival-Triggered World Events". `RandEventSystem.instance.SetRandomEventByName(name, pos)`
    /// (SERVER decompile :107067-107071, confirmed directly by this build) resolves the named asset and
    /// calls the server-authoritative `SetRandomEvent` (:107083-107109 - the `ZNet.instance.IsServer()`
    /// gate inside it, plus the routed broadcast, is what makes this genuinely server-driven rather than
    /// a request the client could ignore), bypassing `m_requiredGlobalKeys` and the valid-point search
    /// entirely - any event can be started anywhere.
    ///
    /// Detection reuses the SAME poll-based transit heuristic as #238 (a player's previous sample was
    /// near a portal with a live Connection, their current sample is near that portal's own connected
    /// partner) - a coarse, but genuinely server-observable, stand-in for the catalog's own preferred
    /// "Portal-As-Trigger already knows the exact moment" path (#136 is a DIFFERENT transport mechanism,
    /// used only for waygates this domain explicitly marks - ordinary vanilla portal travel has no
    /// server-side arrival event to hook, Core/Hooks/ has none for it). After
    /// `AmbushGateArriveGraceSeconds` (covering the tail of the vanilla teleport black screen), this
    /// engine fires the event at the arrival portal's own position and locks that portal by writing its
    /// Connection to `ZDOID.None` (`Game.SetConnection`'s own write shape, reproduced via
    /// Core/Data/PortalOwnership.ClaimAndWrite exactly as every other write in this mod is required to
    /// go through it) - remembering the ORIGINAL connection so it can be restored, not assuming it was
    /// always None. The lock is lifted the instant `RandEventSystem.instance.GetCurrentRandomEvent()`
    /// next reads null - a level-triggered reconciler (the same style
    /// Subsystems/Enforcement/LockdownRaidGeofenceEngine.cs already uses for the analogous "raid ended"
    /// signal), so a raid that ends for ANY reason (timeout, being cleared, an admin console command)
    /// unlocks the gate just as readily as one that ran its full course.
    ///
    /// Known, documented limitation (not worked around): `m_requiredGlobalKeys`/boss-flag detection on
    /// the RandomEvent asset is Inspector data this mod cannot introspect from the decompile alone, so
    /// this engine has no automatic "do not ambush a Meadows newcomer with a Fuling raid" guard - the
    /// operator's own choice of `AmbushGateEventName` IS the policy.
    /// </summary>
    public static class WildcardBAmbushGateEngine
    {
        private sealed class Pending
        {
            public Vector3 Position;
            public ZDOID GateUid;
            public float Remaining;
        }

        private sealed class LockedGate
        {
            public ZDOID OriginalConnection;
        }

        private static readonly Dictionary<long, Vector3> _lastPos = new Dictionary<long, Vector3>();
        private static readonly List<Pending> _pending = new List<Pending>();
        private static readonly Dictionary<ZDOID, LockedGate> _locked = new Dictionary<ZDOID, LockedGate>();

        public static void OnUpdate(float dt)
        {
            if (WildcardBConfig.AmbushGatesEnabled?.Value != true || ZDOMan.instance == null)
            {
                _lastPos.Clear();
                return;
            }

            DetectArrivals();
            TickPending(dt);
            TickLocks();
        }

        private static void DetectArrivals()
        {
            float radius = 4f;
            float radiusSqr = radius * radius;

            foreach (ConnectedCharacter who in ConnectedCharacters.All())
            {
                Vector3 current = who.Position;
                if (_lastPos.TryGetValue(who.PlayerId, out Vector3 prev))
                {
                    foreach (PortalRecord rec in PortalCensus.Latest)
                    {
                        if (rec.Connection == ZDOID.None || (rec.Position - prev).sqrMagnitude > radiusSqr)
                        {
                            continue;
                        }
                        if (!PortalCensus.TryGet(rec.Connection, out PortalRecord destRec) || (destRec.Position - current).sqrMagnitude > radiusSqr)
                        {
                            continue;
                        }
                        if (_locked.ContainsKey(destRec.Uid) || AlreadyPending(destRec.Uid))
                        {
                            break;
                        }
                        _pending.Add(new Pending
                        {
                            Position = destRec.Position,
                            GateUid = destRec.Uid,
                            Remaining = WildcardBConfig.AmbushGateArriveGraceSeconds?.Value ?? 8f,
                        });
                        break;
                    }
                }
                _lastPos[who.PlayerId] = current;
            }
        }

        private static bool AlreadyPending(ZDOID gateUid)
        {
            foreach (Pending p in _pending)
            {
                if (p.GateUid == gateUid) return true;
            }
            return false;
        }

        private static void TickPending(float dt)
        {
            if (_pending.Count == 0)
            {
                return;
            }
            for (int i = _pending.Count - 1; i >= 0; i--)
            {
                Pending p = _pending[i];
                p.Remaining -= dt;
                if (p.Remaining > 0f)
                {
                    continue;
                }
                _pending.RemoveAt(i);
                FireAmbush(p);
            }
        }

        private static void FireAmbush(Pending p)
        {
            string eventName = WildcardBConfig.AmbushGateEventName?.Value ?? "";
            if (string.IsNullOrEmpty(eventName) || RandEventSystem.instance == null || ZNet.instance == null || !ZNet.instance.IsServer())
            {
                return;
            }
            ZDO gate = ZDOMan.instance?.GetZDO(p.GateUid);
            if (gate == null || !gate.IsValid())
            {
                return;
            }

            try
            {
                RandEventSystem.instance.SetRandomEventByName(eventName, p.Position);
                ZDOID original = gate.GetConnectionZDOID(ZDOExtraData.ConnectionType.Portal);
                PortalOwnership.ClaimAndWrite(gate, z => z.SetConnection(ZDOExtraData.ConnectionType.Portal, ZDOID.None));
                _locked[p.GateUid] = new LockedGate { OriginalConnection = original };
                PortalDebug.LogAlways($"[WildcardBAmbushGateEngine] fired '{eventName}' at {p.Position:F0} and locked gate {p.GateUid}.");
            }
            catch (System.Exception ex)
            {
                PortalDebug.LogWarning($"[WildcardBAmbushGateEngine] failed to fire ambush at {p.Position:F0}: {ex.Message}");
            }
        }

        private static void TickLocks()
        {
            if (_locked.Count == 0 || RandEventSystem.instance == null || ZDOMan.instance == null)
            {
                return;
            }
            if (RandEventSystem.instance.GetCurrentRandomEvent() != null)
            {
                return; // still running (or a new one replaced it) - stay locked
            }

            List<ZDOID> toRestore = new List<ZDOID>(_locked.Keys);
            foreach (ZDOID gateUid in toRestore)
            {
                ZDO gate = ZDOMan.instance.GetZDO(gateUid);
                if (gate != null && gate.IsValid())
                {
                    ZDOID original = _locked[gateUid].OriginalConnection;
                    PortalOwnership.ClaimAndWrite(gate, z => z.SetConnection(ZDOExtraData.ConnectionType.Portal, original));
                    PortalDebug.LogAlways($"[WildcardBAmbushGateEngine] event ended - restored gate {gateUid}.");
                }
                _locked.Remove(gateUid);
            }
        }
    }
}
