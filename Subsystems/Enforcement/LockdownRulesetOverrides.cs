namespace TortalPortalLite.Subsystems.Enforcement
{
    /// <summary>
    /// Small in-memory-only override flags LockdownRulesetEngine (#121) sets on the other primitive
    /// engines, so a named preset can force a sub-engine on/off WITHOUT mutating its BepInEx
    /// ConfigEntry&lt;bool&gt; (which would rewrite the operator's own .cfg file on next save - surprising
    /// and not what an ephemeral "Ruleset = X" selection should do). Never persisted, always resets to
    /// false on plugin reload.
    /// </summary>
    public static class LockdownRulesetOverrides
    {
        public static bool BossLockdownForced;
        public static bool RaidGeofenceForced;

        public static void ClearAll()
        {
            BossLockdownForced = false;
            RaidGeofenceForced = false;
        }
    }
}
