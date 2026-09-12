using System;
using System.Collections.Generic;
using UnityEngine;
using TortalPortalLite.Core;
using TortalPortalLite.Core.Data;
using TortalPortalLite.Subsystems.Foundations;

namespace TortalPortalLite.Subsystems.Topology
{
    /// <summary>
    /// #12 Switchboard - a single hub portal whose destination the player selects live; the server
    /// rewrites one ZDOID and the very next transit uses it (TeleportWorld.Teleport re-reads the
    /// connection at the instant of transit, never cached, :143539).
    ///
    /// Inbound legs (every destination -> hub) are a plain Fan-In Star (#2), reasserted every tick via
    /// TopologiesShapeEngine.ReassertShape exactly like any other declared shape - flipping the hub's
    /// OUTBOUND edge never disturbs those, because pass 1 evaluates each destination's own edge against
    /// the HUB (non-null, tag-matched, non-None), never against what the hub itself currently points at
    /// (verified directly against the pass-1 body, :100594-100606).
    ///
    /// The hub's outbound edge is genuinely runtime-mutable state driven by player input, not a static
    /// declaration - this is the "(b) genuinely needs its own write logic outside the [declared] model"
    /// case Core/Data/PortalOwnership's own guidance calls out, so this engine writes it directly via
    /// PortalOwnership.ClaimAndWrite whenever a selection changes, in addition to the ordinary per-tick
    /// reassert (which just keeps re-confirming whatever was last selected).
    ///
    /// Input channel, in priority order:
    ///  1. Map ping (Chat.SendPing -> the routed "ChatMessage" RPC at targetPeerID 0, always reaches the
    ///     server even with one player online, SERVER decompile :42058-42068/:41626). Registered via
    ///     ZRoutedRpc.instance.Register - vanilla's own public multi-consumer RPC-name extension point,
    ///     the same mechanism Chat itself uses to claim that name and PlayerNotify already uses to SEND
    ///     ("Message") - NOT a Harmony patch, so it needs no Core/Hooks/ broker. Skipped defensively if
    ///     Chat.instance already exists server-side (CapabilityProbe's own "singleton.Chat" probe - if
    ///     Chat.Awake() ran, it already registered "ChatMessage" itself, and ZRoutedRpc.Register uses a
    ///     plain Dictionary.Add that throws on a duplicate key, :83680-83699) or if registration throws
    ///     for any other reason.
    ///  2. Emote cycling (TopologiesConfig.SwitchboardSelectEmote, default "point"), via EmoteSignals -
    ///     this mod's other always-available vanilla-client input channel - cycles the nearest hub to the
    ///     player's own position to its next declared destination. Always active, independent of whether
    ///     the ping channel installed.
    /// </summary>
    public static class TopologiesSwitchboardEngine
    {
        private const float HubProximityRadius = 10f;
        private const float PingDebounceSeconds = 2f;

        private sealed class HubState
        {
            public int SelectedIndex;
            public float LockedUntilRealtime;
            public long LockedByPlayerId;
        }

        private static readonly Dictionary<string, HubState> _state = new Dictionary<string, HubState>();
        private static readonly Dictionary<long, float> _lastPingRealtime = new Dictionary<long, float>();

        private static float _timer;
        private static bool _pingChannelAttempted;
        private static bool _pingChannelInstalled;

        /// <summary>Diagnostic: true once the map-ping ("ChatMessage") input channel is confirmed registered. False forever on a build where Chat.instance already claimed that RPC name, or where registration otherwise failed - emote-cycling still works either way.</summary>
        public static bool PingChannelInstalled => _pingChannelInstalled;

        public static void Initialize()
        {
            EmoteSignals.Register(OnEmote);
        }

        public static void OnUpdate(float dt)
        {
            if (!_pingChannelAttempted)
            {
                TryInstallPingChannel();
            }

            _timer += dt;
            float interval = TopologiesConfig.ShapeReassertSeconds?.Value ?? 2.0f;
            if (_timer < interval)
            {
                return;
            }
            _timer = 0f;
            Reassert();
        }

        private static void Reassert()
        {
            if (ZDOMan.instance == null || !VersionMigration.DestructivePassesAllowed)
            {
                return;
            }
            int budget = TopologiesConfig.MaxWritesPerTick?.Value ?? 50;
            int written = 0;
            foreach (TopologySwitchboardDefinition sb in TopologiesDefinitions.Current.Switchboards)
            {
                if (written >= budget)
                {
                    break;
                }
                written += ReassertOne(sb, budget - written);
            }
        }

        private static int ReassertOne(TopologySwitchboardDefinition sb, int budget)
        {
            if (sb == null || string.IsNullOrEmpty(sb.Tag) || sb.Destinations.Count == 0)
            {
                return 0;
            }
            HubState state = GetState(sb);
            state.SelectedIndex = ((state.SelectedIndex % sb.Destinations.Count) + sb.Destinations.Count) % sb.Destinations.Count;

            var shape = new TopologyShapeDefinition { Name = "switchboard:" + sb.Name, Tag = sb.Tag, Nodes = new List<TopologyNode>() };
            foreach (TopologyDestination d in sb.Destinations)
            {
                shape.Nodes.Add(new TopologyNode { Position = d.Position, Target = sb.Hub });
            }
            shape.Nodes.Add(new TopologyNode { Position = sb.Hub, Target = sb.Destinations[state.SelectedIndex].Position });

            return TopologiesShapeEngine.ReassertShape(shape, budget, autoProvisionGlobal: false);
        }

        /// <summary>
        /// Seeds a first-seen hub's selection from whatever it is ALREADY connected to (real, persisted
        /// ZDO data that survives a restart) rather than always defaulting to destination 0 - a hub that
        /// was last set to "mountain" before a server restart stays pointed at "mountain" instead of
        /// silently reverting, since this in-memory selection index is the only thing that does NOT
        /// survive a restart on its own.
        /// </summary>
        private static HubState GetState(TopologySwitchboardDefinition sb)
        {
            if (!_state.TryGetValue(sb.Name, out HubState s))
            {
                s = new HubState { SelectedIndex = ResolveInitialIndex(sb.Hub, sb.Destinations) };
                _state[sb.Name] = s;
            }
            return s;
        }

        private static int ResolveInitialIndex(TopologyVec3 hub, List<TopologyDestination> destinations)
        {
            if (ZDOMan.instance != null && PortalCensus.TryGetByPosition(hub.ToVector3(), out PortalRecord hubRecord) && hubRecord.Connection != ZDOID.None)
            {
                for (int i = 0; i < destinations.Count; i++)
                {
                    if (PortalCensus.TryGetByPosition(destinations[i].Position.ToVector3(), out PortalRecord destRecord) && destRecord.Uid == hubRecord.Connection)
                    {
                        return i;
                    }
                }
            }
            return 0;
        }

        // ---- emote-cycling fallback (always active) ----

        private static void OnEmote(ConnectedCharacter who, string emote)
        {
            if (!EmoteSignals.Is(emote, TopologiesConfig.SwitchboardSelectEmote?.Value, "point"))
            {
                return;
            }
            TopologySwitchboardDefinition sb = FindNearestHub(who.Position, out float dist);
            if (sb == null || dist > HubProximityRadius || sb.Destinations.Count == 0)
            {
                return;
            }
            HubState state = GetState(sb);
            if (IsLocked(state, who.PlayerId))
            {
                PlayerNotify.Toast(who, "Gate busy - try again shortly.");
                return;
            }
            state.SelectedIndex = (state.SelectedIndex + 1) % sb.Destinations.Count;
            Lock(state, who.PlayerId);
            PushAndToast(sb, state, who);
        }

        // ---- map-ping selection (best-effort) ----

        private static void TryInstallPingChannel()
        {
            if (TopologiesConfig.SwitchboardUseMapPing?.Value != true)
            {
                _pingChannelAttempted = true;
                return;
            }
            // Wait for CapabilityProbe's own verdict on "did Chat.Awake() run server-side" before racing
            // it for the "ChatMessage" RPC name - both resolve within the first ~60s of boot (see
            // CapabilityProbe's own MaxAttempts), and registering first only to have Chat's own later
            // Awake() throw on the duplicate key (Dictionary.Add, :83680-83699) would be worse than
            // simply waiting.
            if (!CapabilityProbe.IsDone || ZRoutedRpc.instance == null)
            {
                return; // not attempted yet - retry next tick.
            }
            _pingChannelAttempted = true;
            if (CapabilityProbe.Get("singleton.Chat"))
            {
                PortalDebug.LogInfo("[TopologiesSwitchboardEngine] Chat.instance exists server-side - assuming it already owns the \"ChatMessage\" RPC name and skipping map-ping registration (emote-cycling remains available).");
                return;
            }
            try
            {
                ZRoutedRpc.instance.Register<Vector3, int, UserInfo, string>("ChatMessage", OnChatMessageRpc);
                _pingChannelInstalled = true;
                PortalDebug.LogAlways("[TopologiesSwitchboardEngine] registered a ChatMessage/map-ping listener for Switchboard destination selection.");
            }
            catch (Exception ex)
            {
                _pingChannelInstalled = false;
                PortalDebug.LogWarning($"[TopologiesSwitchboardEngine] could not register a ChatMessage listener ({ex.GetType().Name}: {ex.Message}) - falling back to emote-only selection.");
            }
        }

        private static void OnChatMessageRpc(long sender, Vector3 position, int type, UserInfo userInfo, string text)
        {
            try
            {
                ConnectedCharacter? whoNullable = FindByPeerUid(sender);
                if (whoNullable == null)
                {
                    return;
                }
                ConnectedCharacter who = whoNullable.Value;

                float now = Time.realtimeSinceStartup;
                if (_lastPingRealtime.TryGetValue(who.PlayerId, out float last) && now - last < PingDebounceSeconds)
                {
                    return; // catalog's own "trivially spammable; debounce per player" mitigation
                }
                _lastPingRealtime[who.PlayerId] = now;

                TopologySwitchboardDefinition sb = FindNearestHub(who.Position, out float dist);
                if (sb == null || dist > HubProximityRadius || sb.Destinations.Count == 0)
                {
                    return; // this ping wasn't meant for a switchboard - most map pings aren't.
                }
                HubState state = GetState(sb);
                if (IsLocked(state, who.PlayerId))
                {
                    PlayerNotify.Toast(who, "Gate busy - try again shortly.");
                    return;
                }
                state.SelectedIndex = NearestDestinationIndex(sb, position);
                Lock(state, who.PlayerId);
                PushAndToast(sb, state, who);
            }
            catch (Exception ex)
            {
                PortalDebug.LogError($"[TopologiesSwitchboardEngine] ChatMessage handler failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static ConnectedCharacter? FindByPeerUid(long peerUid)
        {
            foreach (ConnectedCharacter who in ConnectedCharacters.All())
            {
                if (who.Peer != null && who.Peer.m_uid == peerUid)
                {
                    return who;
                }
            }
            return null;
        }

        private static TopologySwitchboardDefinition FindNearestHub(Vector3 pos, out float bestDist)
        {
            TopologySwitchboardDefinition best = null;
            bestDist = float.MaxValue;
            foreach (TopologySwitchboardDefinition sb in TopologiesDefinitions.Current.Switchboards)
            {
                float d = Vector3.Distance(pos, sb.Hub.ToVector3());
                if (d < bestDist)
                {
                    bestDist = d;
                    best = sb;
                }
            }
            return best;
        }

        private static int NearestDestinationIndex(TopologySwitchboardDefinition sb, Vector3 clickedPos)
        {
            int best = 0;
            float bestDist = float.MaxValue;
            for (int i = 0; i < sb.Destinations.Count; i++)
            {
                float d = Vector3.Distance(clickedPos, sb.Destinations[i].Position.ToVector3());
                if (d < bestDist)
                {
                    bestDist = d;
                    best = i;
                }
            }
            return best;
        }

        private static bool IsLocked(HubState state, long requesterPlayerId)
        {
            return state.LockedUntilRealtime > Time.realtimeSinceStartup && state.LockedByPlayerId != requesterPlayerId;
        }

        private static void Lock(HubState state, long playerId)
        {
            state.LockedByPlayerId = playerId;
            state.LockedUntilRealtime = Time.realtimeSinceStartup + (TopologiesConfig.SwitchboardLockSeconds?.Value ?? 15f);
        }

        /// <summary>
        /// Gate-1 pre-push: TargetFound() requires the CLIENT to already hold the destination ZDO
        /// (:143640-143654); without this, the very first walk-in after a repoint silently no-ops
        /// (TeleportWorld.Teleport returns without calling player.Message, :143519-143522).
        /// </summary>
        private static void PushAndToast(TopologySwitchboardDefinition sb, HubState state, ConnectedCharacter who)
        {
            TopologyDestination dest = sb.Destinations[state.SelectedIndex];
            if (ZDOMan.instance != null && who.Peer != null && PortalCensus.TryGetByPosition(dest.Position.ToVector3(), out PortalRecord destRecord))
            {
                ZDOMan.instance.ForceSendZDO(who.Peer.m_uid, destRecord.Uid);
            }
            PlayerNotify.Toast(who, $"Gate set: {dest.Name}");
        }
    }
}
