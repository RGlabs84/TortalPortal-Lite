using System;
using Splatform;
using UnityEngine;
using TortalPortalLite.Core;
using TortalPortalLite.Core.Data;

namespace TortalPortalLite.Subsystems.PlayerInterface
{
    /// <summary>
    /// Three small, independently-cataloged, output-only presentation channels - grouped because each is
    /// "forge one specific routed RPC vanilla already handles unconditionally", with no capture needed:
    ///
    ///   #171 Voice   - a forged `"ChatMessage"` RPC yields a real chat line, floating world text (via
    ///                  `Chat.OnNewChatMessage`/`AddInworldText`) AND a labelled animated map pin, all
    ///                  from one packet. `&lt;`/`&gt;` are replaced with spaces by vanilla's own
    ///                  sanitiser, so a raw tag/sigil must never be spoken verbatim. Off by default
    ///                  (`UxConfig.VoiceEnabled`) - the catalog's own tone warning ("a server that
    ///                  speaks... reads as a bot") makes this an admin's deliberate choice, not a default.
    ///   #260 Lights Out - `Game.Start` registers the ZDO-less `"SleepStart"`/`"SleepStop"` RPCs on every
    ///                  peer; firing them directly produces the same full-screen sleep fade and input
    ///                  lockout vanilla itself only ever uses from its own `Game.UpdateSleeping`. Exposed
    ///                  as an optional "swap cover" for #268/#269's forced teleports - covering the
    ///                  teleport under a fade is strictly optional polish, not required for correctness
    ///                  (`UxForcedTeleport`'s own `distantTeleport: true` swirl already covers it).
    ///   #261 Dream Reel - queues a named vanilla cinematic via `"RPC_SetDreamCinematic"`, which only
    ///                  plays on that player's NEXT sleep. Genuinely inert unless paired with Lights Out
    ///                  AND a real, verified vanilla video name - `DreamReelVideoName` defaults to empty
    ///                  precisely because no such name has been confirmed against this build's
    ///                  `CinematicsManager.m_videos` (Inspector data this mod cannot enumerate remotely).
    /// </summary>
    public static class UxVoiceAndSleepFeedback
    {
        private enum TalkerType { Whisper, Normal, Shout, Ping }

        /// <summary>#171. Reserve for consequential events only, per the catalog's own tone guidance - never a routine confirmation.</summary>
        public static void Speak(ConnectedCharacter who, Vector3 worldPos, string text)
        {
            if (UxConfig.VoiceEnabled?.Value != true || ZRoutedRpc.instance == null || who.Peer == null)
            {
                return;
            }
            try
            {
                var speaker = new UserInfo { Name = UxConfig.VoiceSpeakerName?.Value ?? "Portal Network", UserId = PlatformUserID.None };
                ZRoutedRpc.instance.InvokeRoutedRPC(who.Peer.m_uid, "ChatMessage", worldPos, (int)TalkerType.Shout, speaker, text);
            }
            catch (Exception ex)
            {
                PortalDebug.LogWarning($"[UxVoiceAndSleepFeedback] Speak failed for {who.Name}: {ex.Message}");
            }
        }

        /// <summary>#171 Ping-type variant - no chat line, world text forced to "PING", just a transient labelled map beacon. Cheaper and less noisy than Speak for "here's where you're about to go".</summary>
        public static void Beacon(ConnectedCharacter who, Vector3 worldPos)
        {
            if (UxConfig.VoiceEnabled?.Value != true || ZRoutedRpc.instance == null || who.Peer == null)
            {
                return;
            }
            try
            {
                var speaker = new UserInfo { Name = UxConfig.VoiceSpeakerName?.Value ?? "Portal Network", UserId = PlatformUserID.None };
                ZRoutedRpc.instance.InvokeRoutedRPC(who.Peer.m_uid, "ChatMessage", worldPos, (int)TalkerType.Ping, speaker, "");
            }
            catch (Exception ex)
            {
                PortalDebug.LogWarning($"[UxVoiceAndSleepFeedback] Beacon failed for {who.Name}: {ex.Message}");
            }
        }

        /// <summary>#260. Caller MUST eventually call SleepStop for this player - vanilla will not (Game.m_sleeping stays false server-side for a mod-initiated fade), so a crashed caller leaves the player black-screened until they relog.</summary>
        public static void SleepStart(ConnectedCharacter who)
        {
            if (UxConfig.LightsOutEnabled?.Value != true || ZRoutedRpc.instance == null || who.Peer == null)
            {
                return;
            }
            ZRoutedRpc.instance.InvokeRoutedRPC(who.Peer.m_uid, "SleepStart");
        }

        public static void SleepStop(ConnectedCharacter who)
        {
            if (ZRoutedRpc.instance == null || who.Peer == null)
            {
                return;
            }
            ZRoutedRpc.instance.InvokeRoutedRPC(who.Peer.m_uid, "SleepStop");
        }

        /// <summary>#261. Only plays on the player's NEXT sleep (real or Lights-Out-forced) - inert without one.</summary>
        public static void QueueDreamCinematic(ConnectedCharacter who, string? videoNameOverride = null)
        {
            string name = videoNameOverride ?? UxConfig.DreamReelVideoName?.Value ?? "";
            if (string.IsNullOrEmpty(name) || ZRoutedRpc.instance == null || who.Peer == null)
            {
                return;
            }
            ZRoutedRpc.instance.InvokeRoutedRPC(who.Peer.m_uid, "RPC_SetDreamCinematic", name);
        }
    }
}
