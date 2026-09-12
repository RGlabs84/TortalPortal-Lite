using System.Collections.Generic;
using UnityEngine;
using TortalPortalLite.Core;
using TortalPortalLite.Core.Data;
using TortalPortalLite.Subsystems.Foundations;

namespace TortalPortalLite.Subsystems.Topology
{
    /// <summary>
    /// #193 Map Ping -&gt; Destination, and Map-Pin Feedback.
    ///
    /// Capture side (INPUT) is NOT wired up: Chat.SendPing invokes routed RPC "ChatMessage" to
    /// targetPeerID 0 with (Vector3 pos, int type=3 Ping, UserInfo, "") (SERVER decompile :42058-42068).
    /// Reading that argument server-side needs a Harmony patch on ZRoutedRpc.HandleRoutedRPC comparing
    /// `data.m_methodHash == "ChatMessage".GetStableHashCode()` and peeking its parameters - no
    /// Core/Hooks/ broker in this codebase covers that method (SenderContext patches the DIFFERENT
    /// RPC_RoutedRPC(ZRpc,ZPackage) entry point purely to expose the socket-verified sender peer, not to
    /// let handlers inspect arbitrary routed-RPC payloads by method name; RpcZdoDataHook is specific to
    /// ZDOMan.RPC_ZDOData). Writing a competing ad hoc patch here would be exactly the Harmony-collision
    /// hazard Core/Hooks/ exists to prevent.
    /// NEEDS NEW HOOK BROKER on ZRoutedRpc.HandleRoutedRPC(RoutedRPCData): purpose - observe (never veto)
    /// the Vector3 position argument of a client's "ChatMessage" routed RPC whose int argument is 3
    /// (Ping), for #193's own primary input mechanism. HandlePing below is the complete, ready-to-call
    /// receiver for exactly that broker once it exists - every other part of this option is implemented.
    ///
    /// Feedback side (OUTPUT) needs no new hook and IS fully implemented: a saved, named pin per managed
    /// destination via `RPC_DiscoverLocationResponse` (SERVER decompile :100068/:100823-100831), pushed
    /// to a newly-seen connected player on a slow poll of ConnectedCharacters (no dedicated
    /// peer-connect broker exists in this codebase, and none is needed for a poll-based "greet once"
    /// pattern), plus an ephemeral server-authored "Shout" pin at the moment a ping is actually applied.
    /// </summary>
    public static class TargetedMapPingEngine
    {
        private static readonly Dictionary<long, float> _lastAcceptedPingClock = new Dictionary<long, float>();
        private static readonly HashSet<long> _greetedPlayerIds = new HashSet<long>();
        private static float _clock;
        private static float _greetTimer;

        public static void OnUpdate(float dt)
        {
            _clock += dt;
            _greetTimer += dt;
            if (_greetTimer >= 5f)
            {
                _greetTimer = 0f;
                GreetNewArrivals();
            }
        }

        /// <summary>
        /// Ready to be called once a ZRoutedRpc.HandleRoutedRPC broker exists and a handler here
        /// recognises a "ChatMessage" ping (methodHash match + int param == 3) and passes its Vector3
        /// argument through. Implements the rest of #193's own mechanism unmodified: recompute height
        /// from the pinger's own Y (the ping's Y is the PINGER's Y, not the terrain - #193's own note),
        /// validate, debounce per player, then re-target whichever managed hub the pinging player is
        /// standing nearest.
        /// </summary>
        public static void HandlePing(ConnectedCharacter pinger, Vector3 pingedXZY)
        {
            float debounce = TargetedConfig.MapPingDebounceSeconds?.Value ?? 10f;
            if (_lastAcceptedPingClock.TryGetValue(pinger.PlayerId, out float last) && _clock - last < debounce)
            {
                return;
            }

            TargetedWorldGenValidation.Result result = TargetedWorldGenValidation.ValidateGroundPoint(pingedXZY.x, pingedXZY.z);
            if (!result.Ok)
            {
                PlayerNotify.Toast(pinger, $"Ping rejected: {result.Reason}.");
                return;
            }

            ZDO hub = FindNearestManagedHub(pinger.Position, TargetedConfig.MapPingClaimRadius?.Value ?? 10f);
            if (hub == null)
            {
                return; // pinger isn't standing near any managed hub - nothing to retarget
            }

            _lastAcceptedPingClock[pinger.PlayerId] = _clock;
            string tag = TargetedTagFormat.Named($"Ping: {pinger.Name}");
            TargetedPhantomPortalFactory.CreateOrRetarget(hub, result.Position, Quaternion.identity, tag, $"ping:{pinger.PlayerId}");
            PlayerNotify.Toast(pinger, $"Hub now points to your ping.");
            PushSavedPin(pinger, $"Portal: {pinger.Name}'s ping", result.Position);
        }

        /// <summary>Any managed source portal - a ping can retarget a hub of ANY kind currently declared in targeted_routes.json, not just a dedicated "Ping" kind, matching the catalog's own "re-targets the hub the pinger last stood near" wording.</summary>
        private static ZDO FindNearestManagedHub(Vector3 pos, float radius)
        {
            if (ZDOMan.instance == null)
            {
                return null;
            }
            ZDO best = null;
            float bestDistSqr = radius * radius;
            foreach (TargetedRoute route in TargetedRouteStore.Routes)
            {
                float d = (route.SourcePosition - pos).sqrMagnitude;
                if (d > bestDistSqr)
                {
                    continue;
                }
                if (!PortalCensus.TryGetByPosition(route.SourcePosition, out PortalRecord rec))
                {
                    continue;
                }
                ZDO z = ZDOMan.instance.GetZDO(rec.Uid);
                if (z == null || !z.IsValid())
                {
                    continue;
                }
                bestDistSqr = d;
                best = z;
            }
            return best;
        }

        /// <summary>Saved, named, deduplicated pin on the requesting client - InvokeRoutedRPC(peer, "RPC_DiscoverLocationResponse", label, PinType.Icon3, pos, showMap:false), SERVER decompile citation in class remarks.</summary>
        public static void PushSavedPin(ConnectedCharacter who, string label, Vector3 pos)
        {
            if (ZRoutedRpc.instance == null || who.Peer == null)
            {
                return;
            }
            ZRoutedRpc.instance.InvokeRoutedRPC(who.Peer.m_uid, "RPC_DiscoverLocationResponse", label, (int)Minimap.PinType.Icon3, pos, false);
        }

        /// <summary>Greets any connected player not yet greeted this session with a saved pin for every currently-declared static destination - the login-discovery behaviour, achieved by polling ConnectedCharacters rather than a dedicated peer-connect hook.</summary>
        private static void GreetNewArrivals()
        {
            if (ZDOMan.instance == null)
            {
                return;
            }
            foreach (ConnectedCharacter cc in ConnectedCharacters.All())
            {
                if (cc.PlayerId == 0L || !_greetedPlayerIds.Add(cc.PlayerId))
                {
                    continue;
                }
                foreach (TargetedRoute route in TargetedRouteStore.Routes)
                {
                    if (string.IsNullOrEmpty(route.Label))
                    {
                        continue;
                    }
                    if (!PortalCensus.TryGetByPosition(route.SourcePosition, out PortalRecord rec))
                    {
                        continue;
                    }
                    PushSavedPin(cc, $"Portal: {route.Label}", rec.Position);
                }
            }
        }
    }
}
