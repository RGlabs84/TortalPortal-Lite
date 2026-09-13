using System.Collections.Generic;
using UnityEngine;
using TortalPortalLite.Core;
using TortalPortalLite.Core.Data;
using TortalPortalLite.Subsystems.Foundations;

namespace TortalPortalLite.Subsystems.WorldOps
{
    /// <summary>
    /// #136 "Portal As Trigger, RPC As Transport". A ZDO holds at most ONE connection
    /// (`ZDOExtraData.s_connections`/`ZDOConnection`, SERVER decompile :74794/:75524-75534) so a
    /// simultaneous multi-destination hub is structurally impossible through vanilla's own Connection
    /// field. A portal marked as a "waygate" (this engine's own <see cref="WildcardBZdoKeys.Waygate"/>
    /// marker) is left with its Connection alone entirely (`TeleportWorld.Teleport` gate 1 just returns
    /// silently when it is None, :143519) - instead this engine polls connected character positions
    /// against every waygate portal's own position and, on proximity, force-teleports with
    /// `Character.RPC_TeleportTo` - registered OWNER-GATED on every character in `Character.Awake`
    /// (:886, handler :4126-4132) - which the catalog itself notes is the SAFER of the two available
    /// teleport RPCs (the alternative, `Chat`'s "RPC_TeleportPlayer", has no sender/owner check at all,
    /// :41962-41968); since this mod is the server calling it, not an untrusted peer, using the
    /// owner-gated channel is simply the more defensible of the two, not a functional requirement.
    ///
    /// Mandatory pre-checks, both read straight off the character ZDO exactly as the catalog specifies:
    /// skip a dead player (`ZDOVars.s_dead` - `Player.UpdateTeleport` only runs inside the `!IsDead()`
    /// branch of FixedUpdate, so a teleport sent to a dead player latches `m_teleporting` true and never
    /// executes, :10213/:10226) and skip an attached/sleeping player (`ZDOVars.s_inBed` -
    /// `Player.UpdateAttach` runs BEFORE `UpdateTeleport` every tick and re-snaps to the attach point,
    /// :10217/:15818-15836). Per-player cooldown (default 12s, comfortably past vanilla's own
    /// `m_teleportCooldown` window which stays live for the WHOLE 8+s teleport plus 2s, :15331-15337)
    /// prevents a double-fire against a still-teleporting player.
    ///
    /// Destination selection is pluggable (`SetPlayerDestination`) rather than hard-wired to one input
    /// channel - this engine wires ONE concrete default matching the catalog's own "the emote they last
    /// performed" idea: Core/Data/EmoteSignals.cs (Wave 0/1, shared, frozen) already gives this mod its
    /// only vanilla-client input channel besides the tag string; a configurable "bookmark" emote
    /// (default "wave") sets the performing player's OWN current position/rotation as their personal
    /// waygate destination, so walking to a waygate after bookmarking somewhere sends them back there.
    /// </summary>
    public static class WildcardBTriggerTransportEngine
    {
        private static readonly Dictionary<long, (Vector3 pos, Quaternion rot)> _destinations = new Dictionary<long, (Vector3, Quaternion)>();
        private static readonly Dictionary<long, float> _cooldownRemaining = new Dictionary<long, float>();
        private static bool _emoteRegistered;
        private static float _pollTimer;

        public static void Initialize()
        {
            if (!_emoteRegistered)
            {
                _emoteRegistered = true;
                EmoteSignals.Register(OnEmote);
            }
        }

        // ------------------------------------------------------------------------------ public API

        /// <summary>Marks (or unmarks) a portal ZDO as a waygate - its own Connection field is then ignored by this engine (and left however vanilla/other engines maintain it).</summary>
        public static void SetWaygate(ZDO portal, bool enabled)
        {
            if (portal == null || !portal.IsValid())
            {
                return;
            }
            PortalOwnership.ClaimAndWrite(portal, z => z.Set(WildcardBZdoKeys.Waygate, enabled ? 1 : 0));
        }

        public static bool IsWaygate(ZDO portal) => portal != null && portal.IsValid() && portal.GetInt(WildcardBZdoKeys.Waygate) == 1;

        public static void SetPlayerDestination(long playerId, Vector3 pos, Quaternion rot) => _destinations[playerId] = (pos, rot);

        // ------------------------------------------------------------------------------------ tick

        public static void OnUpdate(float dt)
        {
            if (WildcardBConfig.TriggerTransportEnabled?.Value != true)
            {
                return;
            }

            TickCooldowns(dt);

            _pollTimer += dt;
            float interval = WildcardBConfig.TriggerTransportPollSeconds?.Value ?? 0.3f;
            if (_pollTimer < interval)
            {
                return;
            }
            _pollTimer = 0f;
            PollAndTeleport();
        }

        private static void TickCooldowns(float dt)
        {
            if (_cooldownRemaining.Count == 0)
            {
                return;
            }
            List<long> expired = null;
            foreach (KeyValuePair<long, float> kv in _cooldownRemaining)
            {
                float remaining = kv.Value - dt;
                if (remaining <= 0f)
                {
                    (expired ??= new List<long>()).Add(kv.Key);
                }
                else
                {
                    _cooldownRemaining[kv.Key] = remaining;
                }
            }
            if (expired != null)
            {
                foreach (long id in expired) _cooldownRemaining.Remove(id);
            }
        }

        private static void PollAndTeleport()
        {
            if (ZDOMan.instance == null || ZRoutedRpc.instance == null)
            {
                return;
            }
            float radius = WildcardBConfig.TriggerTransportRadius?.Value ?? 3.5f;
            float radiusSqr = radius * radius;

            List<ZDO> waygates = null;
            foreach (PortalRecord rec in PortalCensus.Latest)
            {
                ZDO zdo = ZDOMan.instance.GetZDO(rec.Uid);
                if (zdo != null && zdo.IsValid() && zdo.GetInt(WildcardBZdoKeys.Waygate) == 1)
                {
                    (waygates ??= new List<ZDO>()).Add(zdo);
                }
            }
            if (waygates == null)
            {
                return;
            }

            foreach (ConnectedCharacter who in ConnectedCharacters.All())
            {
                if (_cooldownRemaining.ContainsKey(who.PlayerId))
                {
                    continue;
                }
                if (!_destinations.TryGetValue(who.PlayerId, out (Vector3 pos, Quaternion rot) dest))
                {
                    continue;
                }
                if (who.Zdo.GetBool(ZDOVars.s_dead) || who.Zdo.GetBool(ZDOVars.s_inBed))
                {
                    continue;
                }

                Vector3 pos = who.Position;
                foreach (ZDO gate in waygates)
                {
                    if ((gate.GetPosition() - pos).sqrMagnitude > radiusSqr)
                    {
                        continue;
                    }

                    try
                    {
                        ZRoutedRpc.instance.InvokeRoutedRPC(who.Peer.m_uid, who.Zdo.m_uid, "RPC_TeleportTo", dest.pos, dest.rot, true);
                        float cooldown = WildcardBConfig.TriggerTransportCooldownSeconds?.Value ?? 12f;
                        _cooldownRemaining[who.PlayerId] = cooldown;
                        PortalDebug.LogInfo($"[WildcardBTriggerTransportEngine] triggered waygate for {who.Name} -> {dest.pos:F0}.");
                    }
                    catch (System.Exception ex)
                    {
                        PortalDebug.LogWarning($"[WildcardBTriggerTransportEngine] RPC_TeleportTo failed for {who.Name}: {ex.Message}");
                    }
                    break;
                }
            }
        }

        private static void OnEmote(ConnectedCharacter who, string emote)
        {
            if (WildcardBConfig.TriggerTransportEnabled?.Value != true)
            {
                return;
            }
            if (!EmoteSignals.Is(emote, WildcardBConfig.TriggerTransportBookmarkEmote?.Value, "wave"))
            {
                return;
            }
            SetPlayerDestination(who.PlayerId, who.Position, who.Zdo.GetRotation());
            PlayerNotify.Toast(who, "Waygate destination bookmarked here.");
        }
    }
}
