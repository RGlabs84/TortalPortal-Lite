using UnityEngine;
using TortalPortalLite.Core.Data;
using TortalPortalLite.Subsystems.Topology;

namespace TortalPortalLite.Subsystems.PlayerInterface
{
    /// <summary>
    /// #269 Warp Verbs - /home, /back, /spawn without a portal. Three emote-triggered destinations
    /// resolved entirely server-side, sharing `UxForcedTeleport` (#174) for delivery/cooldown/guards:
    ///
    ///   HOME  - reuses Topology's own `TargetedBedEngine.TryGetMostRecentBed`, rather than
    ///           re-sweeping bed ZDOs a second time - that engine already tracks "which bed a player
    ///           most recently claimed" (vanilla itself never records an active bed, only ownership),
    ///           and its own doc comment names this exact kind of reuse (#207 Corpse-Run Gate already
    ///           does the same). Falls back to a polite "no claimed bed yet" toast if the Targeted
    ///           subsystem or its bed sweep hasn't found one.
    ///   BACK  - the one verb that can ONLY exist server-side: `UxForcedTeleport`'s own pre-warp history
    ///           ring, since a client's death/logout/home points never leave its local save file.
    ///   SPAWN - `ZoneSystem.GetLocationIcon(Game.instance.m_StartLocation, out pos)`, the same lookup
    ///           vanilla's own spawn logic uses.
    /// </summary>
    public static class UxWarpVerbsEngine
    {
        public static void Initialize()
        {
            EmoteSignals.Register(OnEmote);
        }

        private static void OnEmote(ConnectedCharacter who, string emote)
        {
            if (UxConfig.Enabled?.Value == false || UxConfig.WarpVerbsEnabled?.Value == false)
            {
                return;
            }

            if (EmoteSignals.Is(emote, UxConfig.HomeEmote?.Value, "bow"))
            {
                Home(who);
            }
            else if (EmoteSignals.Is(emote, UxConfig.BackEmote?.Value, "shrug"))
            {
                Back(who);
            }
            else if (EmoteSignals.Is(emote, UxConfig.SpawnEmote?.Value, "cower"))
            {
                Spawn(who);
            }
        }

        private static void Home(ConnectedCharacter who)
        {
            if (!UxForcedTeleport.CanTeleport(who, out string reason))
            {
                UxFeedback.Toast(who, $"Can't warp home ({reason}).");
                return;
            }
            if (!TargetedBedEngine.TryGetMostRecentBed(who.PlayerId, out ZDOID bedId))
            {
                UxFeedback.Toast(who, "No claimed bed found yet.");
                return;
            }
            ZDO bed = ZDOMan.instance?.GetZDO(bedId);
            if (bed == null || !bed.IsValid())
            {
                UxFeedback.Toast(who, "Your bed is gone.");
                return;
            }
            if (UxForcedTeleport.TryTeleport(who, bed.GetPosition() + Vector3.up, bed.GetRotation()))
            {
                UxFeedback.Toast(who, "Warping home.");
            }
        }

        private static void Back(ConnectedCharacter who)
        {
            if (!UxForcedTeleport.CanTeleport(who, out string reason))
            {
                UxFeedback.Toast(who, $"Can't warp back ({reason}).");
                return;
            }
            if (!UxForcedTeleport.TryPopHistory(who.PlayerId, out Vector3 pos, out Quaternion rot))
            {
                UxFeedback.Toast(who, "Nowhere to go back to.");
                return;
            }
            if (UxForcedTeleport.TryTeleport(who, pos, rot, recordForBack: false))
            {
                UxFeedback.Toast(who, "Warping back.");
            }
        }

        private static void Spawn(ConnectedCharacter who)
        {
            if (!UxForcedTeleport.CanTeleport(who, out string reason))
            {
                UxFeedback.Toast(who, $"Can't warp to spawn ({reason}).");
                return;
            }
            if (ZoneSystem.instance == null || Game.instance == null || !ZoneSystem.instance.GetLocationIcon(Game.instance.m_StartLocation, out Vector3 pos))
            {
                UxFeedback.Toast(who, "World spawn not found.");
                return;
            }
            if (UxForcedTeleport.TryTeleport(who, pos + Vector3.up * 2f, Quaternion.identity))
            {
                UxFeedback.Toast(who, "Warping to spawn.");
            }
        }
    }
}
