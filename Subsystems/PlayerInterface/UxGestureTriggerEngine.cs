using System.Collections.Generic;
using UnityEngine;
using TortalPortalLite.Core.Data;
using TortalPortalLite.Subsystems.Foundations;

namespace TortalPortalLite.Subsystems.PlayerInterface
{
    /// <summary>
    /// #157 Animator SetTrigger as a real-time gesture channel (jump/dodge cycling). NEW FINDING the
    /// catalog verifies: `ZSyncAnimation.SetTrigger` invokes `ZNetView.Everybody` (0L), which the client
    /// handles locally AND routes to the server via `ZRoutedRpc.RouteRPC` - a zero-latency, zero-polling
    /// gesture push, one hop, for every jump/dodge/forsaken-power/etc. any player performs anywhere.
    ///
    /// CAPTURE IS NOT WIRED UP, for the same reason as #156/#270's map ping: the only place that carries
    /// both the routed RPC's deserialized parameters (here: the target character ZDOID and the trigger
    /// name string) and the authenticated sender is `ZRoutedRpc.RPC_RoutedRPC(ZRpc, ZPackage)`, which no
    /// existing `Core/Hooks/` broker exposes to handlers by method name (see UxMapPingEngine's identical
    /// note - this is the same missing broker, reusable by both features once it exists).
    ///
    /// // NEEDS NEW HOOK BROKER on ZRoutedRpc.RPC_RoutedRPC(ZRpc,ZPackage): purpose - observe (never
    /// veto) a routed RPC by method-hash match without consuming the shared ZPackage's read position.
    /// Once it exists, call HandleTrigger below whenever
    /// `d.m_methodHash == "SetTrigger".GetStableHashCode()`, resolving identity from the payload's
    /// `m_targetZDO` (the real character ZDOID - the catalog's own warning that `m_senderPeerID` is
    /// spoofable, `m_targetZDO` is not) rather than the sender field.
    ///
    /// The FSM below is real and ready: while ARMED (UxArmingGate, shared with every other gesture-style
    /// channel in this domain), consecutive "jump" triggers within a time+movement window advance a
    /// destination cursor through the current address book, "dodge" (or the shared crouch-release
    /// commit, via UxArmingGate.RegisterCommitRequested) confirms it.
    /// </summary>
    public static class UxGestureTriggerEngine
    {
        private sealed class JumpState
        {
            public int Count;
            public float LastClock;
            public Vector3 LastPos;
        }

        private static readonly Dictionary<long, JumpState> _jumpState = new Dictionary<long, JumpState>();
        private static readonly Dictionary<long, int> _cursor = new Dictionary<long, int>();
        private static float _clock;

        public static void Initialize()
        {
            UxArmingGate.RegisterCommitRequested(who => Commit(who));
        }

        public static void OnUpdate(float dt) => _clock += dt;

        /// <summary>Ready-to-call receiver for the capture broker described above. `who` must already be resolved from the payload's own character ZDOID, not the sender field.</summary>
        public static void HandleTrigger(ConnectedCharacter who, string triggerName)
        {
            if (UxConfig.Enabled?.Value == false || UxConfig.GestureTriggerEnabled?.Value == false)
            {
                return;
            }
            if (!UxArmingGate.IsArmed(who.PlayerId))
            {
                return; // gesture cycling only counts while armed - #157's own "bunny-hopping trips it constantly" mitigation
            }

            if (triggerName == "jump")
            {
                Advance(who);
            }
            else if (triggerName == "dodge")
            {
                Commit(who);
            }
        }

        private static void Advance(ConnectedCharacter who)
        {
            float window = UxConfig.GestureWindowSeconds?.Value ?? 4f;
            float maxMove = UxConfig.GestureMaxMoveMeters?.Value ?? 2f;

            if (!_jumpState.TryGetValue(who.PlayerId, out JumpState st) ||
                _clock - st.LastClock > window ||
                Vector3.Distance(st.LastPos, who.Position) > maxMove)
            {
                st = new JumpState();
                _jumpState[who.PlayerId] = st;
            }
            st.Count++;
            st.LastClock = _clock;
            st.LastPos = who.Position;
            UxArmingGate.Touch(who.PlayerId);

            List<UxAddressBook.AddressEntry> ordered = UxAddressBook.Ordered();
            if (ordered.Count == 0)
            {
                UxFeedback.Toast(who, "No named portals yet.");
                return;
            }
            int idx = (st.Count - 1) % ordered.Count;
            _cursor[who.PlayerId] = idx;
            UxFeedback.Toast(who, $"{idx + 1}/{ordered.Count} {ordered[idx].Name}");
        }

        private static void Commit(ConnectedCharacter who)
        {
            if (!_cursor.TryGetValue(who.PlayerId, out int idx))
            {
                return;
            }
            _cursor.Remove(who.PlayerId);
            _jumpState.Remove(who.PlayerId);

            List<UxAddressBook.AddressEntry> ordered = UxAddressBook.Ordered();
            if (idx < 0 || idx >= ordered.Count)
            {
                return;
            }
            if (!UxArmingGate.TryGetArmedPortal(who.PlayerId, out PortalRecord source))
            {
                return;
            }
            ZDO sourceZdo = ZDOMan.instance?.GetZDO(source.Uid);
            if (sourceZdo == null || !sourceZdo.IsValid())
            {
                return;
            }
            UxDialAction.TryDialToRecord(sourceZdo, ordered[idx].Record, who, out _);
        }
    }
}
