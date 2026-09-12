using TortalPortalLite.Core;

namespace TortalPortalLite.Subsystems.Enforcement
{
    /// <summary>
    /// #109 Global-Key RPC Hardening and #255 Authenticated Global-Key Relay Filter (#255 explicitly
    /// "Replaces: Global-Key RPC Hardening" in the catalog - same problem, better chokepoint). Both need
    /// a prefix that can VETO before the world-modifier key write lands:
    ///
    ///  - #109's target is `ZoneSystem.RPC_SetGlobalKey`/`RPC_RemoveGlobalKey` - private instance RPC
    ///    handlers with zero sender validation, reached only via ZRoutedRpc's routed-RPC dispatch.
    ///  - #255's target is the earlier, more general `ZRoutedRpc.RPC_RoutedRPC(ZRpc, ZPackage)` choke
    ///    point, authenticating by the real socket rather than the spoofable long sender id, and
    ///    classifying by parsed key ordinal so legitimate client-originated keys (defeated_*, activeBosses,
    ///    killed_*) still pass.
    ///
    /// Core/Hooks/ has no broker for either target: nothing wraps ZoneSystem.RPC_SetGlobalKey/
    /// RPC_RemoveGlobalKey at all, and RPC_RoutedRPC already has exactly one consumer -
    /// Core/Data/SenderContext.cs - which that file's own doc comment describes as "its own installer,
    /// not a Core/Hooks/ broker with registrable handlers: nothing competes to patch RPC_RoutedRPC... there
    /// is nothing to order" - i.e. it is observe-only (Current) with no veto capability, and adding a
    /// second Harmony patch on the same method from here would be exactly the ad hoc patch collision this
    /// mod's architecture is built to prevent.
    ///
    /// NEEDS NEW HOOK BROKER on ZoneSystem.RPC_SetGlobalKey(long,string) / RPC_RemoveGlobalKey(long,string):
    /// purpose - true first-veto-wins prefix so a non-server sender can never lift/set a world-modifier
    /// key, mirroring HandleDestroyedZdoHook's shape (Register(priority, (sender, key) => bool), false blocks).
    /// NEEDS NEW HOOK BROKER on ZRoutedRpc.RPC_RoutedRPC(ZRpc,ZPackage): purpose - veto-capable variant
    /// (or a SenderContext extension) so #255's classification can actually drop a packet instead of only
    /// observing it, superseding the narrower #109 target once it exists.
    ///
    /// What IS implemented here, fully working today with zero new patches: the pure classification
    /// logic both options describe (ShouldDropKeyWrite), plus a passive detector that watches for a
    /// world-modifier key flipping to a state this engine did not itself request (via
    /// LockdownGlobalKeyEngine's own wanted-flags) and logs a SECURITY line and silently re-corrects it on
    /// the next LockdownGlobalKeyEngine.OnUpdate tick - a "detect and revert" fallback in the same spirit
    /// RpcZdoDataHook's own doc comment names as this mod's accepted tier when a true veto isn't wired.
    /// </summary>
    public static class LockdownGlobalKeyGuard
    {
        /// <summary>
        /// #255's own classification rule: drop iff the parsed key is a world-modifier ordinal
        /// (&lt; GlobalKeys.NonServerOption) - NoPortals/NoBossPortals/DungeonBuild/TeleportAll and every
        /// rate/difficulty slider live here - or the raw name carries this mod's own reserved prefix.
        /// Boss/progression keys (defeated_*, activeBosses, killed_*, StoneCircle, AshlandsOcean) are all
        /// &gt;= NonServerOption and correctly fall through as legitimate client-originated traffic.
        /// </summary>
        public static bool ShouldDropKeyWrite(string rawKeyName)
        {
            if (string.IsNullOrEmpty(rawKeyName) || ZoneSystem.instance == null || LockdownConfig.GlobalKeyGuardEnabled?.Value == false)
            {
                return false;
            }
            try
            {
                string parsedName = ZoneSystem.GetKeyValue(rawKeyName, out _, out GlobalKeys gk);
                if (gk < GlobalKeys.NonServerOption)
                {
                    return true;
                }
                if (parsedName.StartsWith("tpl_"))
                {
                    return true;
                }
            }
            catch (System.Exception ex)
            {
                PortalDebug.LogWarning($"[LockdownGlobalKeyGuard] classification failed for '{rawKeyName}': {ex.Message}");
            }
            return false;
        }

        /// <summary>
        /// Passive fallback while no veto broker exists: if a world-modifier key this engine wants ON is
        /// observed OFF (or vice versa) outside of this engine's own write, that is exactly the tamper
        /// signature #109/#255 exist to stop. LockdownGlobalKeyEngine's own next tick already reasserts
        /// its wanted state unconditionally, so the "revert" half is already covered - this method only
        /// adds the missing "detect and log" half so an admin sees a SECURITY line instead of a silent,
        /// invisible flap.
        /// </summary>
        public static void OnUpdate(float dt)
        {
            if (ZoneSystem.instance == null || LockdownConfig.GlobalKeyGuardEnabled?.Value == false)
            {
                return;
            }
            CheckOne(GlobalKeys.NoPortals, LockdownGlobalKeyEngine.NoPortalsWanted);
            CheckOne(GlobalKeys.NoBossPortals, LockdownGlobalKeyEngine.NoBossPortalsWanted);
        }

        private static void CheckOne(GlobalKeys key, bool wanted)
        {
            bool have = ZoneSystem.instance.GetGlobalKey(key);
            if (have != wanted)
            {
                PortalDebug.LogWarning($"[SECURITY:GlobalKeyTamper] '{key}' observed {(have ? "SET" : "CLEARED")} but this mod wants it {(wanted ? "SET" : "CLEARED")} - reverting on next tick (no veto broker wired yet, see this file's NEEDS NEW HOOK BROKER note).");
            }
        }
    }
}
