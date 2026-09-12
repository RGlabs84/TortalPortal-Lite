using System.Collections.Generic;
using UnityEngine;
using TortalPortalLite.Core;
using TortalPortalLite.Core.Data;
using TortalPortalLite.Subsystems.Foundations;

namespace TortalPortalLite.Subsystems.Topology
{
    /// <summary>Global settings for Per-Player Private Portal Terminals (routing.json section "privateTerminals" - a one-element array by convention, since terminals themselves are discovered dynamically, not declared by position).</summary>
    public sealed class RoutingPrivateTerminalConfig
    {
        public List<RoutingPosition> Destinations = new List<RoutingPosition>();
        public List<string>? DestinationNames;
        public string CycleEmote = "wave";
        public string GoHomeEmote = "point";
        public float ScanRadius = 8f;
    }

    /// <summary>
    /// #33 Per-Player Private Portal Terminals - the honest answer to "can routing be per-player": it
    /// cannot at the protocol layer (one ZDOID connection field per portal), so this gives every player
    /// their OWN physical portal instead. Identity comes free: `Piece.SetCreator` writes
    /// `ZDOVars.s_creator` (:78393, confirmed only when `GetCreator() == 0 &amp;&amp; m_nview.IsOwner()`,
    /// :136417) to the same PlayerProfile UID as the character ZDO's `s_playerID` (:78539) - so this
    /// engine never needs its own ownership bookkeeping, only a PortalCensus scan for
    /// `Creator == thatPlayerId`, keyed by that id (NEVER by ZDOID - a rebuilt terminal gets a new one,
    /// :74552) rather than by position (unlike every other engine in this domain, a private terminal's
    /// position is whatever the player chose, not admin-declared).
    ///
    /// OUTBOUND (choosing where the terminal leads) reuses the exact emote-cycle mechanic
    /// RoutingPlayerRequestEngine already implements for shared gates - the player stands at their own
    /// terminal and emotes to cycle a shared destination catalog, written directly (no JIT/approach step
    /// needed, they are standing right there).
    ///
    /// INBOUND (the return leg) is the catalog's own named honest limit: a shared arrival portal has
    /// exactly one connection field, so it cannot send different players to different homes. What IS
    /// implemented is the mitigation the catalog itself prefers over N x M anchor fabrication: a second
    /// emote ("go home") server-teleports the player directly to their own terminal via the ALREADY
    /// PUBLIC `Chat.TeleportPlayer(long, Vector3, Quaternion, bool)` (:41957-41959, which itself just
    /// invokes the routed RPC "RPC_TeleportPlayer" - calling the public wrapper directly needs no new
    /// patch). This bypasses the ore/global-key checks exactly as the catalog warns (Player inventories
    /// are never in any ZDO, so the server cannot reproduce vanilla's item gate here) - an accepted,
    /// documented trade-off for a convenience command, not a portal transit.
    ///
    /// ACCESS CONTROL IS NOT IMPLEMENTED - the catalog proves it cannot be:
    /// `PrivateArea.CheckAccess` iterates `m_allAreas`, permanently empty server-side, so it returns true
    /// vacuously. Anyone who physically reaches another player's terminal uses that player's
    /// destination; the only real mitigation is architectural (put the terminal behind a warded door),
    /// which is out of this engine's scope.
    /// </summary>
    public static class RoutingPrivateTerminalEngine
    {
        private static float _timer;
        private static int _lastRegistryVersion = -1;
        private static RoutingPrivateTerminalConfig? _config;
        private static bool _emoteRegistered;

        public static void Initialize()
        {
            if (_emoteRegistered)
            {
                return;
            }
            EmoteSignals.Register(OnEmote);
            _emoteRegistered = true;
        }

        public static void OnUpdate(float dt)
        {
            if (RoutingConfig.TakeoverEnabled?.Value == false || ZDOMan.instance == null)
            {
                return;
            }
            _timer += dt;
            if (_timer < 1.0f)
            {
                return;
            }
            _timer = 0f;

            if (RoutingManagedPortalRegistry.Version != _lastRegistryVersion)
            {
                _lastRegistryVersion = RoutingManagedPortalRegistry.Version;
                List<RoutingPrivateTerminalConfig> parsed = RoutingManagedPortalRegistry.Section<RoutingPrivateTerminalConfig>("privateTerminals");
                _config = parsed.Count > 0 ? parsed[0] : null;
            }
            if (_config == null || _config.Destinations.Count == 0)
            {
                return;
            }

            // Defense-in-depth: re-publish every known terminal's current selection so
            // RoutingPairingAuthorityEngine keeps defending it between emotes, same as every other engine.
            foreach (PortalRecord record in PortalCensus.Latest)
            {
                if (record.Creator == 0L)
                {
                    continue;
                }
                if (!RoutingPlayerSelectionStore.TryGetSelection(TerminalGateKey, record.Creator, out RoutingPosition destPos))
                {
                    continue;
                }
                ZDO? destZdo = RoutingWriteOps.ResolveLive(destPos);
                if (destZdo == null)
                {
                    continue;
                }
                ZDO? terminalZdo = ZDOMan.instance!.GetZDO(record.Uid);
                if (terminalZdo == null || !terminalZdo.IsValid())
                {
                    continue;
                }
                RoutingWriteOps.Reassert(terminalZdo, null, destZdo.m_uid);
                RoutingPairingAuthorityEngine.Publish(terminalZdo.m_uid, null, destZdo.m_uid);
            }
        }

        private static void OnEmote(ConnectedCharacter character, string emote)
        {
            if (RoutingConfig.TakeoverEnabled?.Value == false || _config == null || _config.Destinations.Count == 0)
            {
                return;
            }
            long playerId = character.PlayerId;
            if (playerId == 0L)
            {
                return;
            }

            if (EmoteSignals.Is(emote, _config.GoHomeEmote, "point"))
            {
                HandleGoHome(character, playerId);
                return;
            }
            if (!EmoteSignals.Is(emote, _config.CycleEmote, "wave"))
            {
                return;
            }

            PortalRecord? nearestOwn = FindNearestOwnTerminal(character, playerId, _config.ScanRadius);
            if (nearestOwn == null)
            {
                return;
            }
            ZDO? terminalZdo = ZDOMan.instance?.GetZDO(nearestOwn.Value.Uid);
            if (terminalZdo == null || !terminalZdo.IsValid())
            {
                return;
            }

            int index = RoutingPlayerSelectionStore.AdvanceCycle(TerminalGateKey, playerId, _config.Destinations.Count);
            RoutingPosition destPos = _config.Destinations[index];
            RoutingPlayerSelectionStore.SetSelection(TerminalGateKey, playerId, destPos);

            ZDO? destZdo = RoutingWriteOps.ResolveLive(destPos);
            if (destZdo == null)
            {
                return;
            }

            string? name = _config.DestinationNames != null && index < _config.DestinationNames.Count ? _config.DestinationNames[index] : null;
            string tag = Truncate(string.IsNullOrEmpty(name) ? $"{character.Name} {index + 1}" : name!);

            RoutingWriteOps.PrewarmToPeer(character.Peer.m_uid, destZdo.m_uid);
            RoutingWriteOps.Reassert(terminalZdo, tag, destZdo.m_uid);
            RoutingPairingAuthorityEngine.Publish(terminalZdo.m_uid, tag, destZdo.m_uid);
            PlayerNotify.Toast(character, string.IsNullOrEmpty(name) ? $"Terminal -> {index + 1}/{_config.Destinations.Count}" : $"Terminal -> {name}");
        }

        private static void HandleGoHome(ConnectedCharacter character, long playerId)
        {
            PortalRecord? terminal = FindAnyOwnTerminal(playerId);
            if (terminal == null || Chat.instance == null)
            {
                return;
            }
            Vector3 forward = terminal.Value.Rotation * Vector3.forward;
            Vector3 arrival = terminal.Value.Position + forward * 1f + Vector3.up;
            Chat.instance.TeleportPlayer(character.Peer.m_uid, arrival, terminal.Value.Rotation, true);
        }

        private static PortalRecord? FindNearestOwnTerminal(ConnectedCharacter character, long playerId, float radius)
        {
            float bestSqr = radius * radius;
            PortalRecord? best = null;
            foreach (PortalRecord record in PortalCensus.Latest)
            {
                if (record.Creator != playerId)
                {
                    continue;
                }
                float sqr = (record.Position - character.Position).sqrMagnitude;
                if (sqr <= bestSqr)
                {
                    bestSqr = sqr;
                    best = record;
                }
            }
            return best;
        }

        private static PortalRecord? FindAnyOwnTerminal(long playerId)
        {
            foreach (PortalRecord record in PortalCensus.Latest)
            {
                if (record.Creator == playerId)
                {
                    return record;
                }
            }
            return null;
        }

        private const string TerminalGateKey = "privateterminal";

        private static string Truncate(string s) => s.Length <= 10 ? s : s.Substring(0, 10);
    }
}
