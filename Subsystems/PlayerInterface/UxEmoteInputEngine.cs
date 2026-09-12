using System.Collections.Generic;
using UnityEngine;
using TortalPortalLite.Core.Data;
using TortalPortalLite.Subsystems.Foundations;

namespace TortalPortalLite.Subsystems.PlayerInterface
{
    /// <summary>
    /// #159 Emote signalling near a portal. `Player.StartEmote` writes `s_emote`/`s_emoteID` onto the
    /// player's own character ZDO with no RPC at all - reused verbatim through the shared
    /// `Core/Data/EmoteSignals` poller (Wave 0 infrastructure, the one input channel this whole domain
    /// leans on hardest). Second-choice next to the tag CLI (a real UI to open, a beat of poll latency,
    /// only 4-6 of 24 emotes practically memorable per the catalog) but completely reliable and
    /// independent of player count.
    ///
    /// Self-contained arming: unlike the jump/crouch/equip channels (which share `UxArmingGate`), a
    /// cycling session here is bounded by its OWN confirm/cancel emotes and by proximity to a portal at
    /// every step - the catalog's own "proximity gating plus an armed state is mandatory" mitigation,
    /// satisfied without demanding the player also crouch through an emote-wheel interaction they
    /// already had to deliberately open.
    /// </summary>
    public static class UxEmoteInputEngine
    {
        private static readonly Dictionary<long, int> _cursor = new Dictionary<long, int>();
        private static readonly Dictionary<long, PortalRecord> _sessionPortal = new Dictionary<long, PortalRecord>();

        public static void Initialize()
        {
            EmoteSignals.Register(OnEmote);
        }

        private static void OnEmote(ConnectedCharacter who, string emote)
        {
            if (UxConfig.Enabled?.Value == false || UxConfig.EmoteInputEnabled?.Value == false)
            {
                return;
            }
            float radius = UxConfig.PortalProximityRadius?.Value ?? 6f;
            if (!UxAddressBook.TryNearestAnyPortal(who.Position, radius, out PortalRecord portal))
            {
                return;
            }

            if (EmoteSignals.Is(emote, UxConfig.EmoteList?.Value, "wave"))
            {
                List<UxAddressBook.AddressEntry> ordered = UxAddressBook.Ordered();
                var lines = new List<string> { $"-- PORTALS -- ({ordered.Count})" };
                for (int i = 0; i < ordered.Count; i++)
                {
                    float d = Vector3.Distance(portal.Position, ordered[i].Record.Position);
                    lines.Add($"{i + 1} {ordered[i].Name} {d:0}m");
                }
                UxFeedback.Toasts(who, lines);
                return;
            }

            if (EmoteSignals.Is(emote, UxConfig.EmoteCancel?.Value, "nonono"))
            {
                _cursor.Remove(who.PlayerId);
                _sessionPortal.Remove(who.PlayerId);
                UxFeedback.Toast(who, "Cancelled.");
                return;
            }

            if (EmoteSignals.Is(emote, UxConfig.EmoteAdvance?.Value, "point"))
            {
                List<UxAddressBook.AddressEntry> ordered = UxAddressBook.Ordered();
                if (ordered.Count == 0)
                {
                    UxFeedback.Toast(who, "No named portals yet.");
                    return;
                }
                _sessionPortal[who.PlayerId] = portal;
                int idx = _cursor.TryGetValue(who.PlayerId, out int c) ? (c + 1) % ordered.Count : 0;
                _cursor[who.PlayerId] = idx;
                float d = Vector3.Distance(portal.Position, ordered[idx].Record.Position);
                UxFeedback.Toast(who, $"> {idx + 1}/{ordered.Count} {ordered[idx].Name} ({d:0}m)");
                return;
            }

            if (EmoteSignals.Is(emote, UxConfig.EmoteConfirm?.Value, "thumbsup"))
            {
                if (!_cursor.TryGetValue(who.PlayerId, out int idx))
                {
                    return;
                }
                List<UxAddressBook.AddressEntry> ordered = UxAddressBook.Ordered();
                _cursor.Remove(who.PlayerId);
                PortalRecord source = _sessionPortal.TryGetValue(who.PlayerId, out PortalRecord sp) ? sp : portal;
                _sessionPortal.Remove(who.PlayerId);
                if (idx < 0 || idx >= ordered.Count)
                {
                    return;
                }
                ZDO sourceZdo = ZDOMan.instance?.GetZDO(source.Uid);
                if (sourceZdo == null || !sourceZdo.IsValid())
                {
                    return;
                }
                UxDialAction.TryDialToRecord(sourceZdo, ordered[idx].Record, who, out _);
            }
        }
    }
}
