using System;
using UnityEngine;
using TortalPortalLite.Core;
using TortalPortalLite.Core.Data;

namespace TortalPortalLite.Subsystems.WorldOps
{
    /// <summary>
    /// Server-to-player broadcast helpers used by several economy engines (#100 Treasury unlocks, #104
    /// Naming Rights, #105 Event Topologies, #106 Route Auctions all want a "the whole server just heard
    /// about this" moment). Deliberately self-contained rather than calling into
    /// Subsystems/PlayerInterface's own UxMapPinFeedback - that domain is being edited concurrently by
    /// another Wave 3 agent this wave, and this mod's own architecture note (Core/Hooks/ doc comments)
    /// treats a second, independent caller of the same underlying vanilla RPC as safe (it is a plain
    /// ordinary RPC invocation, not a Harmony patch), so duplicating this thin wrapper here avoids a
    /// cross-domain file dependency without duplicating any actual patch or shared mutable state.
    ///
    /// Announcement is a broadcast toast (PlayerNotify.Toast to every connected character), not a
    /// fabricated Chat "ChatMessage" shout - the catalog's own flavour text imagines a world-space shout
    /// bubble, but that requires constructing a synthetic UserInfo/PlatformUserID whose validity this mod
    /// has not verified end-to-end; a broadcast toast is the same "everyone finds out" effect through the
    /// primitive this mod already trusts (Core/Data/PlayerNotify.cs), so that trade-off is made
    /// deliberately here rather than risking a malformed identity object reaching client code.
    /// </summary>
    public static class EconomyAnnounce
    {
        public static void Broadcast(string message, bool center = false)
        {
            foreach (ConnectedCharacter who in ConnectedCharacters.All())
            {
                PlayerNotify.Toast(who, message, center);
            }
        }

        /// <summary>
        /// Pushes a saved, one-shot map pin (Game.RPC_DiscoverLocationResponse, registered on every peer
        /// unconditionally in Game.Start :100068) to one player. `showMap: false` always - a true re-push
        /// spams `$msg_pin_exist` and re-centres the recipient's map (catalog #104's own citation).
        /// </summary>
        public static void PushPin(ConnectedCharacter who, string label, Vector3 pos)
        {
            if (ZRoutedRpc.instance == null || who.Peer == null)
            {
                return;
            }
            try
            {
                ZRoutedRpc.instance.InvokeRoutedRPC(who.Peer.m_uid, "RPC_DiscoverLocationResponse", label, (int)Minimap.PinType.Icon3, pos, false);
            }
            catch (Exception ex)
            {
                PortalDebug.LogWarning($"[EconomyAnnounce] pin push failed for {who.Name}: {ex.Message}");
            }
        }

        public static void PushPinToEveryone(string label, Vector3 pos)
        {
            foreach (ConnectedCharacter who in ConnectedCharacters.All())
            {
                PushPin(who, label, pos);
            }
        }
    }
}
