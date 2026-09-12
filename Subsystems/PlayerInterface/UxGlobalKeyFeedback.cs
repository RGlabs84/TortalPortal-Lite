using System.Collections.Generic;
using TortalPortalLite.Core.Data;

namespace TortalPortalLite.Subsystems.PlayerInterface
{
    /// <summary>
    /// #175 Feedback: global keys as a world-wide state signal, and their silence. `NoPortals`/
    /// `NoBossPortals` silently block every portal transit server-wide with ZERO client-side affordance -
    /// `TeleportWorld.GetHoverText`/`UpdatePortal` never consult either key, so a blocked portal still
    /// glows and still reads `[Connected]`. The catalog's own framing: "the clearest case in this
    /// document of the mod's real job being to ADD THE MISSING FEEDBACK to a vanilla mechanism that is
    /// otherwise silently confusing."
    ///
    /// Deliberately toast-only, never a tag rewrite: an earlier draft of this option imagined rewriting
    /// the blocked portal's tag to a `'!'`-prefixed marker, but `'!'` is reserved to the access/lockdown
    /// domain (see TagCodec.cs's own sigil table) - this engine only ever reads global keys and portal
    /// proximity, it never touches `s_tag`.
    /// </summary>
    public static class UxGlobalKeyFeedback
    {
        private static readonly HashSet<long> _warnedThisActivation = new HashSet<long>();
        private static float _timer;

        public static void OnUpdate(float dt)
        {
            if (UxConfig.Enabled?.Value == false || UxConfig.GlobalKeyFeedbackEnabled?.Value == false)
            {
                return;
            }
            _timer += dt;
            float interval = UxConfig.GlobalKeyPollSeconds?.Value ?? 2f;
            if (_timer < interval)
            {
                return;
            }
            _timer = 0f;
            Poll();
        }

        private static void Poll()
        {
            if (ZoneSystem.instance == null)
            {
                return;
            }
            bool noPortals = ZoneSystem.instance.GetGlobalKey(GlobalKeys.NoPortals);
            bool noBossPortals = ZoneSystem.instance.GetGlobalKey(GlobalKeys.NoBossPortals);
            if (!noPortals && !noBossPortals)
            {
                _warnedThisActivation.Clear();
                return;
            }

            float radius = UxConfig.GlobalKeyToastRadius?.Value ?? 8f;
            string reason = noPortals ? "Portals are disabled on this server." : "Portals are blocked while a boss is active.";
            foreach (ConnectedCharacter who in ConnectedCharacters.All())
            {
                if (_warnedThisActivation.Contains(who.PlayerId))
                {
                    continue;
                }
                if (!UxAddressBook.TryNearestAnyPortal(who.Position, radius, out _))
                {
                    continue;
                }
                _warnedThisActivation.Add(who.PlayerId);
                UxFeedback.Toast(who, reason, center: true);
            }
        }
    }
}
