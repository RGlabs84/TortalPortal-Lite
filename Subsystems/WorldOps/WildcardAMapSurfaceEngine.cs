using System;
using System.Collections.Generic;
using UnityEngine;
using Splatform;
using TortalPortalLite.Core;
using TortalPortalLite.Core.Data;

namespace TortalPortalLite.Subsystems.WorldOps
{
    /// <summary>
    /// #141 Portal-Driven Map Surfaces - an honest inventory of the three real server-to-client map
    /// channels a portal event can react through, none of them portal-specific, plus one hard
    /// limitation this class deliberately does NOT try to work around.
    ///
    /// Channel 1 - a NAMED, SAVED pin: Game.Start registers "RPC_DiscoverLocationResponse" on every peer
    /// outside the IsServer block (:100068); the client body (:100823-100831) calls
    /// Minimap.DiscoverLocation, which AddPin(..., save:true) - PERMANENTLY, with NO removal RPC anywhere
    /// in the assembly. Push exactly once per (player, destination); re-pushing after a rename creates a
    /// SECOND pin (HaveSimilarPin only dedups identical name+type within 1m).
    ///
    /// Channel 2 - the icon-only overlay: ZoneSystem.SendLocationIcons/RPC_LocationIcons
    /// (:113581-113607) is a full-replace broadcast, but GetLocationIcons (:115450-115463) feeds it
    /// EXCLUSIVELY from `m_locationInstances` - real, procedurally-placed vanilla locations - not an
    /// arbitrary list this mod can inject portal-specific pins into. Exposed here only as a passthrough
    /// trigger (RefreshLocationIcons) to force a peer's overlay to re-sync on demand; deliberately NOT
    /// implemented as an arbitrary-content channel, since fabricating `m_locationInstances` entries risks
    /// corrupting real location/quest bookkeeping this mod has no business touching - the documented
    /// ceiling #141 itself calls out ("an invented name is silently dropped" undersells the risk of
    /// injecting fake instances at all).
    ///
    /// Channel 3 - a TEMPORARY labelled pin plus in-world text plus a chat line: a fabricated
    /// "ChatMessage" routed RPC (Chat.Awake registration, Talker.Type.Shout=2) broadcast to peer 0. Per
    /// this codebase's own prior-art finding (Wonderland CHANGELOG: "a dedicated server only dispatches
    /// a routed RPC locally when it is the target or the message is a broadcast"), targeting peer 0 IS a
    /// broadcast, so every connected client's own Chat.RPC_ChatMessage fires normally (rendering the
    /// chat line / floating world text / animated map pin, UserInfo.Name entirely server-chosen) while
    /// the server's own local copy no-ops safely (Chat.OnNewChatMessage's rendering branch is gated on
    /// `Player.m_localPlayer != null`, always false on a dedicated server).
    /// </summary>
    public static class WildcardAMapSurfaceEngine
    {
        private static readonly HashSet<(long playerId, string label)> _alreadyPushed = new HashSet<(long, string)>();

        /// <summary>Channel 1: one-shot named discovery pin. Always showMap:false (per #141's own citation - true re-spams "$msg_pin_exist" and re-centres the map). Idempotent per (player, label) for this session.</summary>
        public static void PushDiscoveryPin(ConnectedCharacter who, string label, Vector3 pos)
        {
            if (ZRoutedRpc.instance == null || who.Peer == null || who.PlayerId == 0L)
            {
                return;
            }
            var key = (who.PlayerId, label);
            if (!_alreadyPushed.Add(key))
            {
                return;
            }
            try
            {
                ZRoutedRpc.instance.InvokeRoutedRPC(who.Peer.m_uid, "RPC_DiscoverLocationResponse", label, (int)Minimap.PinType.Icon3, pos, false);
            }
            catch (Exception ex)
            {
                PortalDebug.LogWarning($"[WildcardAMapSurfaceEngine] discovery pin push failed for {who.Name}: {ex.Message}");
            }
        }

        /// <summary>Channel 2: forces ZoneSystem to re-broadcast ITS OWN current location-icon set to one peer - a refresh trigger, never an arbitrary-content channel. See class remarks for why.</summary>
        public static void RefreshLocationIcons(long peerUid)
        {
            if (ZoneSystem.instance == null)
            {
                return;
            }
            try
            {
                ZoneSystem.instance.SendLocationIcons(peerUid);
            }
            catch (Exception ex)
            {
                PortalDebug.LogWarning($"[WildcardAMapSurfaceEngine] location-icon refresh failed for peer {peerUid}: {ex.Message}");
            }
        }

        /// <summary>
        /// Channel 3: a fabricated Shout - broadcast chat line, yellow world text, and a 5s animated map
        /// pin labelled "&lt;speakerName&gt;: &lt;text&gt;" on every connected client. speakerName is
        /// entirely server-chosen (UserInfo.Name has no validation on the receiving side beyond
        /// OnNewChatMessage's '&lt;'/'&gt;' strip, which this method also applies defensively).
        /// </summary>
        public static void PushShoutAnnouncement(Vector3 pos, string speakerName, string text)
        {
            if (ZRoutedRpc.instance == null)
            {
                return;
            }
            try
            {
                var userInfo = new UserInfo
                {
                    Name = (speakerName ?? "").Replace('<', ' ').Replace('>', ' '),
                    UserId = PlatformUserID.None
                };
                ZRoutedRpc.instance.InvokeRoutedRPC(0L, "ChatMessage", pos, (int)Talker.Type.Shout, userInfo, text ?? "");
            }
            catch (Exception ex)
            {
                PortalDebug.LogWarning($"[WildcardAMapSurfaceEngine] shout announcement failed: {ex.Message}");
            }
        }
    }
}
