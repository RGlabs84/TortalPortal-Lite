using System;
using System.Collections.Generic;
using TortalPortalLite.Core;

namespace TortalPortalLite.Subsystems.Enforcement
{
    /// <summary>
    /// #58 Progression Gate - the SERVER-ENFORCED half specifically (gate the CONNECTION, not the
    /// transit). The catalog's own comparison is blunt: `GlobalKeys.NoPortals`/`NoBossPortals` are
    /// client-honoured (`TeleportWorld.Teleport` reads them live, but a modified client ignores them
    /// entirely, and they give no visual warning - the portal still looks connected). Gating the
    /// connection itself is strictly stronger: an unconnected portal cannot teleport anyone regardless
    /// of what the client does, because `TeleportWorld.Teleport` gate 1 is `!TargetFound()` and the
    /// destination is read from the server-owned ZDO connection at the instant of transit.
    ///
    /// This engine only answers "may network X be wired right now" - AccessManagedNetworkGovernorEngine
    /// (#54) is what actually withholds the connection when the answer is no, in its
    /// FindRandomUnconnectedPortalHook handler.
    ///
    /// Deliberately scoped to progression keys the catalog itself says are SAFE to poll at runtime
    /// (ordinals >= 41 - defeated_eikthyr, defeated_bonemass, etc. - and any custom NonServerOption
    /// string key) rather than world-modifier ordinals (0-40, e.g. a hypothetical mod-owned "permanent"
    /// key), which the catalog notes are wiped and only selectively restored by
    /// `ZoneSystem.SetStartingGlobalKeys` on every world load. Reasserting a mod-owned ordinal-&lt;41 key
    /// after that wipe needs a postfix on that method, which has no Core/Hooks/ broker:
    ///
    /// NEEDS NEW HOOK BROKER on ZoneSystem.SetStartingGlobalKeys(): purpose - only needed if an admin
    /// wants a PERMANENT (world-modifier-style) progression requirement that must survive being wiped on
    /// every load; the common case (a vanilla boss-defeated key, or any custom string key - both persist
    /// in the .db and are unaffected by this wipe) does not need it and is fully supported without one.
    ///
    /// Per-network requirement mapping is in-memory only (admin-settable via AccessAdminCommands, once
    /// wired) - not yet persisted to its own file. Given the wave's time budget this was judged lower
    /// priority than the enforcement mechanism itself; a future pass can persist it the same way
    /// AccessAclStore persists its own records.
    /// </summary>
    public static class AccessProgressionGateEngine
    {
        private static readonly Dictionary<string, string> _requiredKeyByNetwork = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public static void SetRequirement(string network, string globalKey)
        {
            if (!string.IsNullOrEmpty(network) && !string.IsNullOrEmpty(globalKey))
            {
                _requiredKeyByNetwork[network] = globalKey;
            }
        }

        public static void ClearRequirement(string network)
        {
            _requiredKeyByNetwork.Remove(network ?? "");
        }

        public static IEnumerable<KeyValuePair<string, string>> All() => _requiredKeyByNetwork;

        /// <summary>True if network <paramref name="network"/> has no declared requirement, or its required global key is currently set.</summary>
        public static bool NetworkMayWire(string network)
        {
            if (AccessConfig.ProgressionGateEnabled?.Value == false)
            {
                return true;
            }
            if (string.IsNullOrEmpty(network) || !_requiredKeyByNetwork.TryGetValue(network, out string key))
            {
                return true;
            }
            return ZoneSystem.instance != null && ZoneSystem.instance.GetGlobalKey(key);
        }
    }
}
