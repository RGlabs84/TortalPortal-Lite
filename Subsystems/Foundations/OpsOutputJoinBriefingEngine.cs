using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using TortalPortalLite.Core;
using TortalPortalLite.Core.Data;

namespace TortalPortalLite.Subsystems.Foundations
{
    /// <summary>
    /// #201 OnJoin Briefing &amp; login-time state push. Three join stages, exactly as the catalog lays
    /// them out:
    ///
    /// (1) PEER READY - ZRoutedRpc.m_onNewPeer (:83500, fired from ZDOMan.AddPeer's tail via
    ///     ZNet.RPC_PeerInfo, :79905-79944) is an INSTANCE delegate field, only combinable once
    ///     ZRoutedRpc.instance exists - subscribed lazily here (retried each OnUpdate tick, same tolerant
    ///     pattern CapabilityProbe/OpsOutputDiscordEngine use). At this stage force-send every MANAGED
    ///     portal ZDO via ZDOMan.instance.ForceSendZDO(peer.m_uid, id) (:77714-77719) - a fresh ZDOPeer's
    ///     m_zdos is empty so ShouldSend is guaranteed true (:76003-76015), making this the one moment a
    ///     force-send is guaranteed to actually transmit, eliminating the "first transit fails while
    ///     RequestZDO fetches the destination" cold-start warm-up.
    /// (2)/(3) IDENTITY KNOWN - polled from ConnectedCharacters.All() every tick (no separate hook exists
    ///     for RPC_CharacterID/s_playerID becoming non-zero) until a character's s_playerID is set, then
    ///     a settle delay (default 8s, config JoinBriefingSettleSeconds) covers the post-spawn window
    ///     before Player.Start has registered the owner-only "Message" RPC. After the delay (and only
    ///     while the character is alive), toast the player's own portal summary and re-push idempotent map
    ///     pins for each (RPC_DiscoverLocationResponse, :100068/:100823 - the client's own HaveSimilarPin
    ///     dedups by name+type+&lt;1m so a repeat push is a no-op). Admins additionally get a health summary
    ///     via the same confirmed "Message" toast channel (this mod does not independently verify a
    ///     separate "RemotePrint" RPC beyond that, so it reuses PlayerNotify.Toast rather than inventing
    ///     an unconfirmed call).
    /// </summary>
    public static class OpsOutputJoinBriefingEngine
    {
        private static bool _peerHookInstalled;
        public static bool PeerHookInstalled => _peerHookInstalled;

        private static readonly Dictionary<long, float> _settleStart = new Dictionary<long, float>();
        private static readonly HashSet<long> _briefedPlayerIds = new HashSet<long>();
        private static readonly HashSet<string> _pinnedThisSession = new HashSet<string>(StringComparer.Ordinal);

        public static void OnUpdate(float dt)
        {
            if (!_peerHookInstalled)
            {
                TrySubscribePeerHook();
            }
            if (OpsOutputConfig.JoinBriefingEnabled?.Value != true)
            {
                return;
            }

            List<ConnectedCharacter> connected = ConnectedCharacters.All();
            var stillConnected = new HashSet<long>();

            foreach (ConnectedCharacter character in connected)
            {
                long playerId = character.PlayerId;
                if (playerId == 0L)
                {
                    continue; // identity not resolved yet (stage 2/3 not reached)
                }
                stillConnected.Add(playerId);
                if (_briefedPlayerIds.Contains(playerId))
                {
                    continue;
                }

                if (!_settleStart.TryGetValue(playerId, out float startTime))
                {
                    _settleStart[playerId] = Time.realtimeSinceStartup;
                    continue;
                }
                float settleSeconds = OpsOutputConfig.JoinBriefingSettleSeconds?.Value ?? 8f;
                if (Time.realtimeSinceStartup - startTime < settleSeconds)
                {
                    continue;
                }
                if (character.Zdo.GetBool(ZDOVars.s_dead))
                {
                    continue; // wait until actually alive in the world
                }

                try
                {
                    SendBriefing(character, playerId);
                }
                catch (Exception ex)
                {
                    PortalDebug.LogWarning($"[OpsOutputJoinBriefingEngine] briefing failed for player {playerId}: {ex.GetType().Name}: {ex.Message}");
                }
                _briefedPlayerIds.Add(playerId);
            }

            // Forget settle-timer bookkeeping for anyone no longer connected - a later re-join starts fresh.
            if (_settleStart.Count > 0)
            {
                var toForget = _settleStart.Keys.Where(id => !stillConnected.Contains(id)).ToList();
                foreach (long id in toForget) _settleStart.Remove(id);
            }
        }

        private static void TrySubscribePeerHook()
        {
            if (ZRoutedRpc.instance == null)
            {
                return;
            }
            try
            {
                ZRoutedRpc.instance.m_onNewPeer = (Action<long>)Delegate.Combine(ZRoutedRpc.instance.m_onNewPeer, new Action<long>(OnNewPeer));
                _peerHookInstalled = true;
                PortalDebug.LogAlways("[OpsOutputJoinBriefingEngine] subscribed to ZRoutedRpc.m_onNewPeer for join-time managed-anchor force-send.");
            }
            catch (Exception ex)
            {
                PortalDebug.LogError($"[OpsOutputJoinBriefingEngine] failed to subscribe to ZRoutedRpc.m_onNewPeer: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static void OnNewPeer(long peerUid)
        {
            try
            {
                if (OpsOutputConfig.JoinBriefingEnabled?.Value != true || ZDOMan.instance == null)
                {
                    return;
                }
                int cap = Math.Max(1, OpsOutputConfig.JoinBriefingMaxForceSendPerJoin?.Value ?? 64);
                int sent = 0;
                foreach (PortalRecord record in PortalCensus.Latest)
                {
                    ZDO? zdo = ZDOMan.instance.GetZDO(record.Uid);
                    if (zdo == null || !zdo.IsValid() || string.IsNullOrEmpty(PortalRecordStore.GetNetworkId(zdo)))
                    {
                        continue; // managed anchors only - StructureUpkeep's own per-sweep cap precedent, catalog #201's own warning against inflating the first sync batch
                    }
                    ZDOMan.instance.ForceSendZDO(peerUid, record.Uid);
                    sent++;
                    if (sent >= cap)
                    {
                        break;
                    }
                }
                if (sent > 0)
                {
                    PortalDebug.LogInfo($"[OpsOutputJoinBriefingEngine] force-sent {sent} managed anchor(s) to newly-joined peer {peerUid}.");
                }
            }
            catch (Exception ex)
            {
                PortalDebug.LogWarning($"[OpsOutputJoinBriefingEngine] force-send on join failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static void SendBriefing(ConnectedCharacter character, long playerId)
        {
            var ownPortals = PortalCensus.Latest.Where(r => r.Creator == playerId).ToList();
            if (ownPortals.Count > 0)
            {
                var stranded = ownPortals.Where(r => r.Connection == ZDOID.None).ToList();
                string msg = $"Portals: you own {ownPortals.Count}";
                if (stranded.Count > 0)
                {
                    string strandedTag = string.IsNullOrEmpty(stranded[0].Tag) ? "(untagged)" : stranded[0].Tag;
                    msg += $" ({stranded.Count} has no partner: '{strandedTag}')";
                }
                msg += ".";
                PlayerNotify.Toast(character, msg);

                foreach (PortalRecord portal in ownPortals)
                {
                    string tag = portal.Tag ?? "";
                    if (tag.Length == 0)
                    {
                        continue; // nothing meaningful to name a pin after
                    }
                    string pinKey = $"{playerId}:{tag}";
                    if (!_pinnedThisSession.Add(pinKey))
                    {
                        continue; // already pushed this tag to this player this session - catalog #201's own warning against duplicate pins on a renamed-then-reverted tag
                    }
                    PushPin(character, tag, portal.Position);
                }
            }

            if (OpsOutputConfig.JoinBriefingAdminHealthSummary?.Value == true)
            {
                string? host = SenderContext.HostNameOf(character.Peer);
                if (!string.IsNullOrEmpty(host) && ZNet.instance != null && ZNet.instance.IsAdmin(host))
                {
                    int stranded = HealthScanEngine.Findings.Count(f => f.Category == "OddCountStrand");
                    int dangling = HealthScanEngine.Findings.Count(f => f.Category == "DanglingConnection");
                    PlayerNotify.Toast(character, $"tpl health: {PortalCensus.Latest.Count} portals, {stranded} stranded group(s), {dangling} dangling, {HealthScanEngine.Findings.Count} total finding(s).");
                }
            }
        }

        private static void PushPin(ConnectedCharacter character, string tag, Vector3 pos)
        {
            try
            {
                ZRoutedRpc.instance?.InvokeRoutedRPC(character.Peer.m_uid, "RPC_DiscoverLocationResponse", tag, (int)Minimap.PinType.Icon3, pos, false);
            }
            catch (Exception ex)
            {
                PortalDebug.LogWarning($"[OpsOutputJoinBriefingEngine] pin push failed for '{tag}': {ex.Message}");
            }
        }
    }
}
