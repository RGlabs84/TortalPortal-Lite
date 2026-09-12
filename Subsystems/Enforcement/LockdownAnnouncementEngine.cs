using System.Collections.Generic;
using UnityEngine;
using TortalPortalLite.Core;
using TortalPortalLite.Core.Data;

namespace TortalPortalLite.Subsystems.Enforcement
{
    /// <summary>
    /// #115 Graceful Lockdown and Announcement. Force-disconnect is silent by construction -
    /// TeleportWorld.Teleport returns at gate 1 (`if (!TargetFound()) return;`) with no player.Message
    /// call at all, unlike the key-gated blocks which each emit a vanilla $msg string - so this engine
    /// supplies the words through the vanilla-client-honoured channels the catalog verifies:
    ///
    ///  (1) Per-character toast - Core/Data/PlayerNotify.Toast (already-existing shared infra, itself the
    ///      routed "Message" RPC registered client-side in Player.Start, owner-only so it can never be
    ///      delivered to the wrong player).
    ///  (2) Broadcast HUD text - the global routed RPC "ShowMessage" (int type, string text), registered
    ///      client-side in MessageHud.Start with zero server-side registration needed to SEND it (only
    ///      RECEIVING requires a live MessageHud - and no server ever needs to receive its own broadcast).
    ///  (3) Floating world text - the global routed RPC "RPC_DamageText" (one ZPackage arg), registered in
    ///      DamageText.Awake. Exposed here as SendDamageText because #258 Locked-Gate Overlay reuses the
    ///      exact same primitive for its "Portal locked" floating text.
    ///
    /// Deliberately does NOT register any RPC handler for these names - only INVOKES them. Core/Hooks/'s
    /// own CapabilityProbe already confirms Chat.instance/MessageHud.instance existing or not is
    /// irrelevant here: the SEND side needs nothing but ZRoutedRpc.instance, which always exists.
    /// </summary>
    public static class LockdownAnnouncementEngine
    {
        public static void BroadcastMessage(string text, bool center)
        {
            if (ZRoutedRpc.instance == null || string.IsNullOrEmpty(text))
            {
                return;
            }
            try
            {
                int type = (int)(center ? MessageHud.MessageType.Center : MessageHud.MessageType.TopLeft);
                ZRoutedRpc.instance.InvokeRoutedRPC(0L, "ShowMessage", type, text);
            }
            catch (System.Exception ex)
            {
                PortalDebug.LogWarning($"[LockdownAnnouncementEngine] broadcast failed: {ex.Message}");
            }
        }

        /// <summary>Per-player toast to every connected character within radius of pos - the "you specifically are near a portal that's about to close" warning.</summary>
        public static void ToastNear(Vector3 pos, float radius, string text, bool center)
        {
            float radiusSqr = radius * radius;
            foreach (ConnectedCharacter c in ConnectedCharacters.All())
            {
                if ((c.Position - pos).sqrMagnitude <= radiusSqr)
                {
                    PlayerNotify.Toast(c, text, center);
                }
            }
        }

        public static void ToastPlayer(ConnectedCharacter who, string text, bool center = false)
        {
            PlayerNotify.Toast(who, text, center);
        }

        /// <summary>
        /// Floating world text via the global "RPC_DamageText" routed RPC (DamageText.Awake registration,
        /// one ZPackage argument: int type, Vector3 pos, string text, bool mySelf). Peer-targeted, never
        /// broadcast - #258's own citation that a broadcast at targetPeerID 0 also attempts a local
        /// (server-side) dispatch that silently misses, which is harmless but wasteful.
        /// </summary>
        public static void SendDamageText(long peerUid, Vector3 pos, string text, DamageText.TextType type = DamageText.TextType.Bonus)
        {
            if (ZRoutedRpc.instance == null)
            {
                return;
            }
            try
            {
                var pkg = new ZPackage();
                pkg.Write((int)type);
                pkg.Write(pos);
                pkg.Write(text);
                pkg.Write(false);
                ZRoutedRpc.instance.InvokeRoutedRPC(peerUid, "RPC_DamageText", pkg);
            }
            catch (System.Exception ex)
            {
                PortalDebug.LogWarning($"[LockdownAnnouncementEngine] SendDamageText failed: {ex.Message}");
            }
        }

        /// <summary>Sends floating text to every connected character within DamageText.m_maxTextDistance-scale radius of pos.</summary>
        public static void FloatingTextNear(Vector3 pos, float radius, string text, DamageText.TextType type = DamageText.TextType.Bonus)
        {
            float radiusSqr = radius * radius;
            foreach (ConnectedCharacter c in ConnectedCharacters.All())
            {
                if ((c.Position - pos).sqrMagnitude <= radiusSqr)
                {
                    SendDamageText(c.Peer.m_uid, pos + Vector3.up * 2.5f, text, type);
                }
            }
        }

        /// <summary>#122's own join-time channel - a newly-seen connected character gets told the CURRENT lockdown state once, since the server-browser modifier badge can never be live.</summary>
        public static void AnnounceOnJoin(ConnectedCharacter who, string stateText)
        {
            if (string.IsNullOrEmpty(stateText))
            {
                return;
            }
            PlayerNotify.Toast(who, stateText, center: false);
        }
    }
}
