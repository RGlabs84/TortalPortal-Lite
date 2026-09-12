using System.Collections.Generic;
using TortalPortalLite.Core;
using TortalPortalLite.Core.Data;

namespace TortalPortalLite.Subsystems.Enforcement
{
    /// <summary>
    /// #122 Modifier Badge Honesty. The server-browser "modifiers" badge is a boot-time snapshot of
    /// `ZNet.World.m_startingGlobalKeys`, taken once at ZSteamMatchmaking registration - there is no
    /// supported API to re-register with fresh modifiers at runtime, so a runtime lockdown (session-only
    /// key, force-disconnect, quarantine - none of which are even global keys in the connection/quarantine
    /// cases) can never make that badge honest. This engine implements the catalog's own out-of-band
    /// mitigation: push the TRUE current state through channels that ARE live - a join-time toast (the
    /// one channel guaranteed to reach a joining player) and the mod-wide Heartbeat/Discord line already
    /// carries connected-player context, so this engine only adds the lockdown-state sentence to it via
    /// its own status string.
    /// </summary>
    public static class LockdownModifierBadgeEngine
    {
        private static readonly HashSet<ZDOID> _announcedTo = new HashSet<ZDOID>();

        public static void OnUpdate(float dt)
        {
            if (LockdownConfig.ModifierBadgeAnnounceOnJoin?.Value != true)
            {
                return;
            }
            List<ConnectedCharacter> chars = ConnectedCharacters.All();
            var stillHere = new HashSet<ZDOID>();
            foreach (ConnectedCharacter c in chars)
            {
                stillHere.Add(c.Zdo.m_uid);
                if (_announcedTo.Add(c.Zdo.m_uid))
                {
                    string state = CurrentStateSummary();
                    if (!string.IsNullOrEmpty(state))
                    {
                        LockdownAnnouncementEngine.AnnounceOnJoin(c, state);
                    }
                }
            }
            _announcedTo.RemoveWhere(id => !stillHere.Contains(id));
        }

        /// <summary>Human-readable one-liner of the CURRENT lockdown state - empty if nothing is engaged, since the badge is only dishonest when there's something to hide.</summary>
        public static string CurrentStateSummary()
        {
            var parts = new List<string>();
            if (ZoneSystem.instance != null)
            {
                if (ZoneSystem.instance.GetGlobalKey(GlobalKeys.NoPortals))
                {
                    parts.Add("portals disabled (noportals)");
                }
                if (ZoneSystem.instance.GetGlobalKey(GlobalKeys.NoBossPortals))
                {
                    parts.Add("portals disabled during boss fights");
                }
            }
            if (LockdownVault.Count > 0)
            {
                parts.Add($"{LockdownVault.Count} portal link(s) currently locked");
            }
            string ruleset = LockdownConfig.ActiveRuleset?.Value;
            if (!string.IsNullOrEmpty(ruleset) && ruleset != "None")
            {
                parts.Add($"ruleset '{ruleset}' active");
            }
            return parts.Count == 0 ? "" : "Portal network status: " + string.Join("; ", parts) + ". (Note: the server-browser modifier badge does not reflect this - it is a boot-time snapshot only.)";
        }
    }
}
