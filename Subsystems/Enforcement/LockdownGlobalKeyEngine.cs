using TortalPortalLite.Core;

namespace TortalPortalLite.Subsystems.Enforcement
{
    /// <summary>
    /// #108 NoPortals Global Key, #110 Session-Only Key Writes, #120 TeleportAll/DungeonBuild Policy
    /// Levers - all three are the same one-line-body world-modifier key write vanilla itself uses, so
    /// one engine owns all of them.
    ///
    /// #108: `ZoneSystem.instance.SetGlobalKey("noportals")` (public, SERVER decompile :115943-115946) is
    /// the whole write - it round-trips through ZRoutedRpc's self-dispatch (server IS its own target
    /// peer) synchronously, so the key is readable immediately, and ZoneSystem.OnNewPeer (already-existing
    /// vanilla machinery) covers late joiners on its own. CRITICAL: the lowercase string literal
    /// "noportals" must be used, never SetGlobalKey(GlobalKeys.NoPortals) - the enum overload stringifies
    /// PascalCase ("NoPortals") and the store only ever holds the lowercased form, so the dedupe guard in
    /// RPC_SetGlobalKey never trips on the enum spelling and every call re-adds and re-broadcasts. Always
    /// pre-check with GetGlobalKey(GlobalKeys.X) and only write on an actual transition.
    ///
    /// #110: bypasses SetGlobalKey's RPC entirely and calls the (Publicize=true, already project-wide)
    /// private `GlobalKeyAdd(name, canSaveToServerOptionKeys:false)` + `SendGlobalKeys(0L)` pair directly -
    /// the exact recipe the catalog specifies - so a lockdown key never gets mirrored into
    /// ZNet.World.m_startingGlobalKeys and therefore never survives into the .fwl. The matching clear is
    /// GlobalKeyRemove(name, false) + SendGlobalKeys(0L). Because the key is then absent from both the
    /// .fwl and (being &lt; NonServerOption) the .db, a restart returns the world to its pre-mod state; this
    /// engine's own re-application (below) reasserts the DESIRED state every tick from its own in-memory
    /// intent, independent of anything vanilla persisted.
    ///
    /// #120: identical write path for DungeonBuild/TeleportAll, with the important corrected semantics the
    /// catalog documents: TeleportAll does NOT override Inventory.IsTeleportable's hard m_toolTier>=1000
    /// rejection (a tier-1000 item stays non-teleportable regardless), and DungeonBuild is global (every
    /// piece, every player), not portal-specific.
    /// </summary>
    public static class LockdownGlobalKeyEngine
    {
        // Desired in-memory intent for each world-modifier key this engine owns. Re-asserted every tick
        // so a client-side clear (until #109/#255's guard is wired) or a missed restart-replay is
        // self-healing rather than a one-shot fire-and-forget.
        private static bool _wantNoPortals;
        private static bool _wantNoBossPortals;
        private static bool _wantDungeonBuild;
        private static bool _wantTeleportAll;

        public static bool NoPortalsWanted => _wantNoPortals;
        public static bool NoBossPortalsWanted => _wantNoBossPortals;

        public static void SetNoPortals(bool on) => _wantNoPortals = on;
        public static void SetNoBossPortals(bool on) => _wantNoBossPortals = on;
        public static void SetDungeonBuild(bool on) => _wantDungeonBuild = on;
        public static void SetTeleportAll(bool on) => _wantTeleportAll = on;

        public static void OnUpdate(float dt)
        {
            if (ZoneSystem.instance == null)
            {
                return;
            }
            Reassert("noportals", GlobalKeys.NoPortals, _wantNoPortals);
            Reassert("nobossportals", GlobalKeys.NoBossPortals, _wantNoBossPortals);
            Reassert("dungeonbuild", GlobalKeys.DungeonBuild, _wantDungeonBuild);
            Reassert("teleportall", GlobalKeys.TeleportAll, _wantTeleportAll);
        }

        private static void Reassert(string lowercaseLiteral, GlobalKeys enumKey, bool want)
        {
            bool have = ZoneSystem.instance.GetGlobalKey(enumKey);
            if (want == have)
            {
                return;
            }

            bool sessionOnly = LockdownConfig.SessionOnlyKeyWrites?.Value != false;
            try
            {
                if (want)
                {
                    if (sessionOnly)
                    {
                        // #110: publicized private write, never mirrored into m_startingGlobalKeys.
                        ZoneSystem.instance.GlobalKeyAdd(lowercaseLiteral, canSaveToServerOptionKeys: false);
                        ZoneSystem.instance.SendGlobalKeys(0L);
                    }
                    else
                    {
                        // #108: vanilla's own public one-liner - lowercase literal, never the enum overload.
                        ZoneSystem.instance.SetGlobalKey(lowercaseLiteral);
                    }
                    PortalDebug.LogAlways($"[LockdownGlobalKeyEngine] engaged '{lowercaseLiteral}' (sessionOnly={sessionOnly}).");
                }
                else
                {
                    if (sessionOnly)
                    {
                        ZoneSystem.instance.GlobalKeyRemove(lowercaseLiteral, canSaveToServerOptionKeys: false);
                        ZoneSystem.instance.SendGlobalKeys(0L);
                    }
                    else
                    {
                        ZoneSystem.instance.RemoveGlobalKey(lowercaseLiteral);
                    }
                    PortalDebug.LogAlways($"[LockdownGlobalKeyEngine] lifted '{lowercaseLiteral}'.");
                }
            }
            catch (System.Exception ex)
            {
                PortalDebug.LogError($"[LockdownGlobalKeyEngine] failed to reassert '{lowercaseLiteral}': {ex.GetType().Name}: {ex.Message}");
            }
        }
    }
}
