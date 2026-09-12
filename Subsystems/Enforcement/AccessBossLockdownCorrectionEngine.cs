using TortalPortalLite.Core;

namespace TortalPortalLite.Subsystems.Enforcement
{
    /// <summary>
    /// #219 Boss Lockdown Correction. `TeleportWorld.Teleport`'s boss gate is
    /// `NoBossPortals && (RandEventSystem.instance.GetBossEvent() != null || (GetGlobalKey(activeBosses,
    /// out v) && v > 0))`. The FIRST disjunct is a per-CLIENT HUD read
    /// (`EnemyHud.instance.GetActiveBoss()` - whichever boss THAT client's health bar is currently
    /// showing) and blocks only travellers who personally have a boss bar on screen; only the SECOND
    /// disjunct, `activeBosses`, is genuinely world-wide, and it is maintained by whichever client
    /// happens to have the boss aggroed/killed (`BaseAI.SetAlerted` increments it, `Character.OnDeath`
    /// decrements it) - it can leak non-zero with no boss actually alive.
    ///
    /// A deterministic server-driven lockdown therefore holds BOTH `nobossportals` and
    /// `activebosses &gt;= 1` itself, via the ordinary public `ZoneSystem.SetGlobalKey`/`RemoveGlobalKey`
    /// API (no patching needed to call these - only to stop an unauthenticated CLIENT calling them,
    /// which is AccessRpcHardeningEngine's job; this engine notifies that guard of its own intentional
    /// change so the very next guard tick doesn't "correct" it back).
    ///
    /// Client-honoured by nature (a modified client ignores the key) - genuine enforcement for a
    /// boss-gated network still needs the Managed Network Governor / Progression Gate's connection-level
    /// refusal; this engine only fixes the specific "world-wide" claim's accuracy.
    /// </summary>
    public static class AccessBossLockdownCorrectionEngine
    {
        private static bool _lastApplied;
        private static bool _initialized;

        public static void OnUpdate(float dt)
        {
            if (ZoneSystem.instance == null)
            {
                return;
            }
            bool want = AccessConfig.BossLockdownActive?.Value == true;
            if (_initialized && want == _lastApplied)
            {
                return;
            }
            _initialized = true;
            _lastApplied = want;

            if (want)
            {
                ZoneSystem.instance.SetGlobalKey(GlobalKeys.NoBossPortals);
                ZoneSystem.instance.SetGlobalKey(GlobalKeys.activeBosses, 1f);
                PortalDebug.LogAlways("[AccessBossLockdownCorrectionEngine] boss lockdown ENGAGED world-wide (nobossportals + activebosses>=1).");
            }
            else
            {
                ZoneSystem.instance.RemoveGlobalKey(GlobalKeys.NoBossPortals);
                ZoneSystem.instance.SetGlobalKey(GlobalKeys.activeBosses, 0f);
                PortalDebug.LogAlways("[AccessBossLockdownCorrectionEngine] boss lockdown released.");
            }
            AccessRpcHardeningEngine.NotifyIntentionalChange("nobossportals");
        }
    }
}
