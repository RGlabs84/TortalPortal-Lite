using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using UnityEngine;
using TortalPortalLite.Core;
using TortalPortalLite.Core.Data;
using TortalPortalLite.Subsystems.Foundations;
using TortalPortalLite.Subsystems.Topology;

namespace TortalPortalLite.Subsystems.Enforcement
{
    public sealed class LockdownRelayDestination
    {
        public string Name = "";
        public float X, Y, Z;
        [JsonIgnore] public Vector3 Position => new Vector3(X, Y, Z);
    }

    public sealed class LockdownRelayHub
    {
        public float SourceX, SourceY, SourceZ;
        public string Tag = ">Anywhere";
        public List<LockdownRelayDestination> Destinations = new List<LockdownRelayDestination>();
        [JsonIgnore] public Vector3 SourcePos => new Vector3(SourceX, SourceY, SourceZ);
    }

    public sealed class LockdownRelayFile
    {
        public int SchemaVersion = 1;
        public List<LockdownRelayHub> Hubs = new List<LockdownRelayHub>();
    }

    /// <summary>
    /// #197 Two-Hop Relay: Phantom Landing Pad + RPC_TeleportPlayer, timed per #208's Cooldown-Correct
    /// Timing Spec. Built LAST among this engine's own two-option pair, after #123 Destination Safety
    /// Validator and #250 Placement Gate Audit (its explicit dependencies) already existed.
    ///
    /// Stage 1: each declared hub's SOURCE portal is paired to one phantom "lobby" pad via
    /// Subsystems/Topology/TargetedPhantomPortalFactory.CreateOrRetarget - THE shared phantom-portal
    /// primitive (Wave 1), reused rather than re-derived per this wave's own explicit instruction.
    /// Stage 2: arrival at the lobby is sampled (RelaySamplePeriodSeconds), and - per #208's own timing
    /// analysis of the vanilla Player teleport state machine - the server WAITS at least
    /// RelaySecondHopHoldSeconds (default 8.5s: the 8s distant-teleport floor the first hop's own
    /// TargetFound()->distantTeleport:true path guarantees, plus margin) after observing the character
    /// ZDO land in the lobby before sending the second hop, because `Player.TeleportTo` silently refuses
    /// while `IsTeleporting()` or `m_teleportCooldown &lt; 2f`, and the cooldown only starts accruing once
    /// the FIRST teleport's own `m_teleporting` flag clears at t=8s. Sending any earlier is silently eaten
    /// with no acknowledgement, hence the fixed hold rather than a "wait and check" loop with no signal to
    /// check.
    ///
    /// Hop 2 is sent as `InvokeRoutedRPC(peer.m_uid, characterZdoId, "RPC_TeleportTo", pos, rot,
    /// distantTeleport:false)` (Character.RPC_TeleportTo's own registration, confirmed
    /// `m_nview.Register&lt;Vector3,Quaternion,bool&gt;("RPC_TeleportTo", ...)`) rather than the
    /// unauthenticated global Chat.RPC_TeleportPlayer the catalog names as an alternative carrier - the
    /// ZDOID-scoped RPC only executes `if (m_nview.IsOwner())` on the addressed character's own client,
    /// which is exactly the intended target and needs no extra authentication of its own.
    ///
    /// distantTeleport:false for hop 2 (per #208): the traveller is already inside the lobby, not
    /// crossing the map, so no 8s floor is needed for this leg - but a FindFloor miss at the non-distant
    /// path immediately rubber-bands to the lobby with a misleading vanilla message, so the destination is
    /// ALWAYS run through LockdownDestinationSafetyValidator first and the target zone is pre-generated via
    /// Topology's own TargetedMaterialiserEngine (shared reuse, not re-derived) before sending.
    /// </summary>
    public static class LockdownTwoHopRelayEngine
    {
        private const float FilePollInterval = 5f;
        private const float SelectScanRadius = 8f;

        private sealed class LobbyState
        {
            public int HubIndex;
            public float EnteredAtClock;
            public float? Hop2SentAtClock;
            public bool ResentOnce;
        }

        private static float _fileTimer;
        private static DateTime _fileStamp = DateTime.MinValue;
        private static List<LockdownRelayHub> _hubs = new List<LockdownRelayHub>();

        private static float _sampleTimer;
        private static float _maintenanceTimer;
        private static float _clock;

        private static readonly Dictionary<long, int> _selectedDestinationIndex = new Dictionary<long, int>(); // playerId -> chosen index into that hub's Destinations
        private static readonly Dictionary<long, LobbyState> _lobbyState = new Dictionary<long, LobbyState>(); // playerId -> current lobby dwell state
        private static readonly Dictionary<long, float> _lastRelayAtClock = new Dictionary<long, float>(); // per-player cooldown

        private static bool _emoteRegistered;

        public static void Initialize()
        {
            if (_emoteRegistered)
            {
                return;
            }
            _emoteRegistered = true;
            EmoteSignals.Register(OnEmote);
        }

        public static void OnUpdate(float dt)
        {
            _clock += dt;

            _fileTimer += dt;
            if (_fileTimer >= FilePollInterval)
            {
                _fileTimer = 0f;
                TryReloadFile();
            }
            if (_hubs.Count == 0)
            {
                return;
            }

            _maintenanceTimer += dt;
            if (_maintenanceTimer >= 2f)
            {
                _maintenanceTimer = 0f;
                MaintainLobbyPads();
            }

            _sampleTimer += dt;
            float period = LockdownConfig.RelaySamplePeriodSeconds?.Value ?? 0.5f;
            if (_sampleTimer < period)
            {
                return;
            }
            _sampleTimer = 0f;
            SampleArrivals();
        }

        private static string FilePath()
        {
            string name = LockdownConfig.RelayFile?.Value ?? "lockdown_relay.json";
            return Path.Combine(BepInEx.Paths.ConfigPath, name);
        }

        private static void TryReloadFile()
        {
            try
            {
                string path = FilePath();
                if (!File.Exists(path))
                {
                    return;
                }
                DateTime stamp = File.GetLastWriteTimeUtc(path);
                if (stamp == _fileStamp)
                {
                    return;
                }
                var parsed = JsonConvert.DeserializeObject<LockdownRelayFile>(File.ReadAllText(path));
                if (parsed?.Hubs == null)
                {
                    return;
                }
                _hubs = parsed.Hubs;
                _fileStamp = stamp;
                PortalDebug.LogAlways($"[LockdownTwoHopRelayEngine] loaded {_hubs.Count} relay hub(s) from '{path}'.");
            }
            catch (Exception ex)
            {
                PortalDebug.LogError($"[LockdownTwoHopRelayEngine] failed to load relay file: {ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>Ensures each declared hub's source portal is paired to its phantom lobby pad - reuses Topology's own shared factory rather than re-deriving phantom creation.</summary>
        private static void MaintainLobbyPads()
        {
            foreach (LockdownRelayHub hub in _hubs)
            {
                if (!PortalCensus.TryGetByPosition(hub.SourcePos, out PortalRecord source))
                {
                    continue; // Declared hub has no live portal at that position (not built yet, or demolished).
                }
                ZDO sourceZdo = ZDOMan.instance?.GetZDO(source.Uid);
                if (sourceZdo == null || !sourceZdo.IsValid())
                {
                    continue;
                }
                LockdownDestinationSafetyValidator.Result lobbySite = LockdownDestinationSafetyValidator.BestCompassOffset(hub.SourcePos, 30f);
                Vector3 lobbyPos = lobbySite.Ok ? lobbySite.Position : hub.SourcePos + Vector3.forward * 30f;
                TargetedPhantomPortalFactory.CreateOrRetarget(sourceZdo, lobbyPos, lobbySite.FacingHint, hub.Tag, kind: "lockdown:relay-lobby");
            }
        }

        private static void OnEmote(ConnectedCharacter who, string emote)
        {
            if (!EmoteSignals.Is(emote, LockdownConfig.RelaySelectEmote?.Value, "point"))
            {
                return;
            }
            for (int i = 0; i < _hubs.Count; i++)
            {
                LockdownRelayHub hub = _hubs[i];
                if (hub.Destinations.Count == 0)
                {
                    continue;
                }
                if ((who.Position - hub.SourcePos).sqrMagnitude > SelectScanRadius * SelectScanRadius)
                {
                    continue;
                }
                long playerId = who.PlayerId;
                _selectedDestinationIndex.TryGetValue(playerId, out int current);
                int next = (current + 1) % hub.Destinations.Count;
                _selectedDestinationIndex[playerId] = next;
                LockdownAnnouncementEngine.ToastPlayer(who, $"Destination: {hub.Destinations[next].Name}", center: false);
            }
        }

        private static void SampleArrivals()
        {
            if (_hubs.Count == 0)
            {
                return;
            }
            float lobbyRadius = LockdownConfig.RelayLobbyScanRadius?.Value ?? 3f;
            float lobbyRadiusSqr = lobbyRadius * lobbyRadius;
            var stillInLobby = new HashSet<long>();

            foreach (ConnectedCharacter who in ConnectedCharacters.All())
            {
                long playerId = who.PlayerId;
                if (playerId == 0L)
                {
                    continue;
                }

                int hubIndex = FindHubPlayerIsAtLobbyOf(who.Position, lobbyRadiusSqr);
                if (hubIndex < 0)
                {
                    continue;
                }
                stillInLobby.Add(playerId);

                if (!_lobbyState.TryGetValue(playerId, out LobbyState state) || state.HubIndex != hubIndex)
                {
                    state = new LobbyState { HubIndex = hubIndex, EnteredAtClock = _clock };
                    _lobbyState[playerId] = state;

                    LockdownRelayHub hub = _hubs[hubIndex];
                    string destName = ResolveDestination(hub, playerId, out _)?.Name ?? "(no destination selected)";
                    LockdownAnnouncementEngine.ToastPlayer(who, $"Relaying to {destName}...", center: false);
                    continue;
                }

                TryAdvanceHop2(who, state);
            }

            // Anyone no longer sampled in a lobby (successfully relayed onward, or walked off) stops being tracked.
            var departed = new List<long>();
            foreach (long id in _lobbyState.Keys)
            {
                if (!stillInLobby.Contains(id))
                {
                    departed.Add(id);
                }
            }
            foreach (long id in departed)
            {
                _lobbyState.Remove(id);
            }
        }

        private static int FindHubPlayerIsAtLobbyOf(Vector3 pos, float radiusSqr)
        {
            for (int i = 0; i < _hubs.Count; i++)
            {
                if (!PortalCensus.TryGetByPosition(_hubs[i].SourcePos, out PortalRecord source))
                {
                    continue;
                }
                ZDO phantom = TargetedPhantomPortalFactory.ResolveExistingPhantom(ZDOMan.instance?.GetZDO(source.Uid));
                if (phantom == null)
                {
                    continue;
                }
                if ((phantom.GetPosition() - pos).sqrMagnitude <= radiusSqr)
                {
                    return i;
                }
            }
            return -1;
        }

        private static LockdownRelayDestination ResolveDestination(LockdownRelayHub hub, long playerId, out int index)
        {
            index = _selectedDestinationIndex.TryGetValue(playerId, out int i) ? i : 0;
            if (hub.Destinations.Count == 0)
            {
                return null;
            }
            index = Mathf.Clamp(index, 0, hub.Destinations.Count - 1);
            return hub.Destinations[index];
        }

        private static void TryAdvanceHop2(ConnectedCharacter who, LobbyState state)
        {
            long playerId = who.PlayerId;
            float hold = LockdownConfig.RelaySecondHopHoldSeconds?.Value ?? 8.5f;
            float retryAfter = LockdownConfig.RelayRetryAfterSeconds?.Value ?? 4f;
            float cooldown = LockdownConfig.RelayPerPlayerCooldownSeconds?.Value ?? 12f;

            if (_lastRelayAtClock.TryGetValue(playerId, out float lastAt) && _clock - lastAt < cooldown && state.Hop2SentAtClock == null)
            {
                return; // Per-player cooldown - do not even attempt a fresh relay yet.
            }

            if (state.Hop2SentAtClock == null)
            {
                if (_clock - state.EnteredAtClock < hold)
                {
                    return; // #208's own fixed hold - never send before this, there is no earlier signal worth checking.
                }
                SendHop2(who, state);
                return;
            }

            // Already sent - if they're STILL in the lobby after the retry window, resend once, then give up.
            if (!state.ResentOnce && _clock - state.Hop2SentAtClock.Value >= retryAfter)
            {
                state.ResentOnce = true;
                SendHop2(who, state);
            }
        }

        private static void SendHop2(ConnectedCharacter who, LobbyState state)
        {
            if (ZDOMan.instance == null || ZRoutedRpc.instance == null)
            {
                return;
            }
            ZDO characterZdo = who.Zdo;
            if (characterZdo == null || !characterZdo.IsValid())
            {
                return;
            }
            if (characterZdo.GetBool(ZDOVars.s_dead))
            {
                LockdownAnnouncementEngine.ToastPlayer(who, "You cannot be relayed while dead.", center: false);
                return;
            }
            if (characterZdo.GetBool(ZDOVars.s_inBed))
            {
                LockdownAnnouncementEngine.ToastPlayer(who, "You cannot be relayed while sleeping.", center: false);
                return;
            }

            LockdownRelayHub hub = _hubs[state.HubIndex];
            LockdownRelayDestination dest = ResolveDestination(hub, who.PlayerId, out _);
            if (dest == null)
            {
                LockdownAnnouncementEngine.ToastPlayer(who, "No destination selected - pick one with the select gesture before entering.", center: false);
                return;
            }

            LockdownDestinationSafetyValidator.Result validated = LockdownDestinationSafetyValidator.ValidatePoint(dest.Position);
            if (!validated.Ok)
            {
                PortalDebug.LogWarning($"[LockdownTwoHopRelayEngine] destination '{dest.Name}' refused: {validated.Reason}");
                LockdownAnnouncementEngine.ToastPlayer(who, $"Destination '{dest.Name}' is not safe to relay to right now.", center: false);
                return;
            }

            TargetedMaterialiserEngine.EnsureMaterialized(validated.Position);

            try
            {
                ZRoutedRpc.instance.InvokeRoutedRPC(who.Peer.m_uid, characterZdo.m_uid, "RPC_TeleportTo", validated.Position, Quaternion.identity, false);
                state.Hop2SentAtClock = _clock;
                _lastRelayAtClock[who.PlayerId] = _clock;
                PortalDebug.LogInfo($"[LockdownTwoHopRelayEngine] hop 2 sent for {who.Name} -> '{dest.Name}' at {validated.Position:F0}.");
            }
            catch (Exception ex)
            {
                PortalDebug.LogWarning($"[LockdownTwoHopRelayEngine] hop 2 send failed for {who.Name}: {ex.Message}");
            }
        }
    }
}
