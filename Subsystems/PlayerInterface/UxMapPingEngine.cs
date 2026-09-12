using UnityEngine;
using TortalPortalLite.Core.Data;
using System.Collections.Generic;
using TortalPortalLite.Subsystems.Foundations;

namespace TortalPortalLite.Subsystems.PlayerInterface
{
    /// <summary>
    /// #156 Map ping as a freehand destination picker, superseded in this implementation by #270's own
    /// corrected version (socket identity, y re-derivation, arming, debounce) - one engine serves both
    /// catalog entries since #270 IS #156 with its failure modes fixed, not a separate mechanism.
    ///
    /// CAPTURE IS NOT WIRED UP. `Chat.SendPing` invokes the ZDO-less routed RPC `"ChatMessage"(Vector3,
    /// int type=3 Ping, UserInfo, string)` to `targetPeerID 0`, which reaches the server (unlike chat
    /// TEXT, which only routes when `targetPeerID != m_id`). Reading it needs the authentic connection
    /// AND the payload in the same place, which per #270's own analysis is only
    /// `ZRoutedRpc.RPC_RoutedRPC(ZRpc rpc, ZPackage pkg)` (:83632) - `HandleRoutedRPC(RoutedRPCData)`
    /// has no socket in scope, and `SenderContext`'s existing prefix on `RPC_RoutedRPC` exposes only the
    /// verified SENDER peer, not the arbitrary method-by-name payload this needs. No `Core/Hooks/`
    /// broker covers "peek a routed RPC's parameters by method name" today, and writing a second,
    /// competing Harmony patch on `RPC_RoutedRPC` here would be exactly the collision `Core/Hooks/`
    /// exists to prevent (the same reasoning Topology's own `TargetedMapPingEngine` already documents for
    /// its own, separate map-ping feature).
    ///
    /// // NEEDS NEW HOOK BROKER on ZRoutedRpc.RPC_RoutedRPC(ZRpc,ZPackage): purpose - observe (never
    /// veto) a routed RPC's deserialized parameters by method-hash match, with the authentic ZRpc socket
    /// in scope, WITHOUT consuming the shared ZPackage's read position (deserialize a copy, exactly as
    /// RpcZdoDataHook's own prefix already does for the leading ZDOID). Once it exists, call HandlePing
    /// below with the socket-resolved sender peer and the ping's Vector3 whenever
    /// `d.m_methodHash == "ChatMessage".GetStableHashCode() &amp;&amp; type == 3`, cross-checking
    /// `d.m_senderPeerID == peer.m_uid` first (the catalog's own spoofing warning - never trust the
    /// payload's own sender field).
    ///
    /// Everything downstream of capture is real and ready: height re-derivation via
    /// `WorldGenerator.GetHeight` (never `ZoneSystem.GetGroundHeight`, a dead raycast server-side),
    /// nearest-managed-portal resolution, the arming+debounce gate #270 adds specifically because a ping
    /// is free, public and spammable, and the shared UxDialAction for the actual effect.
    /// </summary>
    public static class UxMapPingEngine
    {
        private static readonly Dictionary<long, float> _lastAcceptedClock = new Dictionary<long, float>();
        private static float _clock;

        public static void OnUpdate(float dt) => _clock += dt;

        /// <summary>Ready-to-call receiver for the capture broker described above. `senderPeer` must already be authenticated against the RPC's own ZRpc socket by the caller.</summary>
        public static void HandlePing(ZNetPeer senderPeer, Vector3 pingWorldXZY)
        {
            if (UxConfig.Enabled?.Value == false || UxConfig.MapPingEnabled?.Value == false)
            {
                return;
            }
            ConnectedCharacter? whoOpt = ResolveByPeer(senderPeer);
            if (!whoOpt.HasValue)
            {
                return;
            }
            ConnectedCharacter who = whoOpt.Value;

            if (UxConfig.MapPingRequireArmed?.Value != false && !UxArmingGate.IsArmed(who.PlayerId))
            {
                return; // #270's own correction: an unarmed ping must never act, or a casual ping during a hunt re-routes a base portal
            }

            float debounce = UxConfig.MapPingDebounceSeconds?.Value ?? 5f;
            if (_lastAcceptedClock.TryGetValue(who.PlayerId, out float last) && _clock - last < debounce)
            {
                return;
            }

            // #270's own correction: Chat.SendPing overwrites the ping's y with the SENDER's own y, not
            // the terrain's - re-derive from world height, and only WorldGenerator.GetHeight, since
            // ZoneSystem.GetGroundHeight is a Physics.Raycast that always misses server-side.
            float y = WorldGenerator.instance != null ? WorldGenerator.instance.GetHeight(pingWorldXZY.x, pingWorldXZY.z) : pingWorldXZY.y;
            if (y < 30f)
            {
                UxFeedback.Toast(who, "Nothing there (water).");
                return;
            }
            Vector3 groundPos = new Vector3(pingWorldXZY.x, y, pingWorldXZY.z);

            float radius = UxConfig.MapPingClaimRadius?.Value ?? 64f;
            if (!UxAddressBook.TryNearestAnyPortal(groundPos, radius, out PortalRecord dest))
            {
                UxFeedback.Toast(who, $"Nothing within {radius:0}m of that ping.");
                return;
            }

            if (!UxArmingGate.TryGetArmedPortal(who.PlayerId, out PortalRecord source) &&
                !UxAddressBook.TryNearestAnyPortal(who.Position, UxConfig.PortalProximityRadius?.Value ?? 6f, out source))
            {
                return;
            }
            ZDO sourceZdo = ZDOMan.instance?.GetZDO(source.Uid);
            if (sourceZdo == null || !sourceZdo.IsValid())
            {
                return;
            }

            _lastAcceptedClock[who.PlayerId] = _clock;
            UxArmingGate.Touch(who.PlayerId);
            UxArmingGate.Disarm(who.PlayerId);
            UxDialAction.TryDialToRecord(sourceZdo, dest, who, out _);
        }

        private static ConnectedCharacter? ResolveByPeer(ZNetPeer peer)
        {
            foreach (ConnectedCharacter cc in ConnectedCharacters.All())
            {
                if (cc.Peer == peer)
                {
                    return cc;
                }
            }
            return null;
        }
    }
}
