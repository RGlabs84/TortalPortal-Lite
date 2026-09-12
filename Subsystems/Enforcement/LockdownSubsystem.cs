using BepInEx.Configuration;
using HarmonyLib;
using ServerSync;
using TortalPortalLite.Core;

namespace TortalPortalLite.Subsystems.Enforcement
{
    /// <summary>
    /// #84-style architecture: one thin dispatcher owning every `lockdown`-domain engine (wave2_lockdown.json,
    /// options #108-125/#247-259) plus the two targeted-domain options #197/#208 that live here because
    /// their dependency (#123/#250) does. Follows Subsystems/Foundations/PortalOpsSubsystem.cs's own
    /// established pattern exactly: real logic lives in sibling static engine classes with
    /// Initialize()/OnUpdate(float)/Shutdown(); this class only sequences them.
    ///
    /// Tick order: global keys and their guard first (cheapest, and other engines read their state);
    /// then the scope-determining engines (schedule/raid-geofence/raid-beacon/boss-watchdog/region/ruleset/
    /// one-way/curfew) that decide WHAT should be locked; then legibility/modifier-badge (which only
    /// OBSERVE and dress up whatever the scope engines just decided); then the invariant harness LAST so
    /// it asserts against the state everything above just produced, not a stale one from before this
    /// tick's writes landed. Hook registrations (Initialize) run once at plugin Awake, independent of
    /// this order - what matters here is only the OnUpdate polling sequence.
    /// </summary>
    public class LockdownSubsystem : IPortalSubsystem
    {
        public string Name => "Lockdown";
        public bool IsEnabled => GlobalConfig.Enabled?.Value != false;

        public void Initialize(ConfigFile config, ConfigSync configSync, Harmony harmony)
        {
            LockdownConfig.Bind(config, configSync);

            LockdownForceDisconnectEngine.Initialize();
            LockdownRelayShieldEngine.Initialize();
            LockdownPortalSanctuaryEngine.Initialize();
            LockdownPlacementPolicyEngine.Initialize();
            LockdownInvariantHarness.Initialize();
            LockdownTwoHopRelayEngine.Initialize();
        }

        public void OnWorldReady()
        {
            // Cheapest possible restore window (catalog #112's own citation): every ZDO is freshly
            // loaded with Owned=false/OwnerRevision=0/DataRevision=0 and no peer is connected yet.
            LockdownVault.RestoreAll();
            LockdownPlacementGateAudit.Run();
        }

        public void OnUpdate()
        {
            float dt = UnityEngine.Time.deltaTime;

            LockdownGlobalKeyEngine.OnUpdate(dt);
            LockdownGlobalKeyGuard.OnUpdate(dt);

            LockdownScheduleEngine.OnUpdate(dt);
            LockdownRaidGeofenceEngine.OnUpdate(dt);
            LockdownRaidBeaconEngine.OnUpdate(dt);
            LockdownBossWatchdogEngine.OnUpdate(dt);
            LockdownRegionEngine.OnUpdate(dt);
            LockdownRulesetEngine.OnUpdate(dt);
            LockdownOneWayEngine.OnUpdate(dt);
            LockdownNightCurfewEngine.OnUpdate(dt);

            LockdownLocationPad.OnUpdate(dt);
            LockdownTwoHopRelayEngine.OnUpdate(dt);

            LockdownModifierBadgeEngine.OnUpdate(dt);
            LockdownLegibilityEngine.OnUpdate(dt);

            LockdownGroundDropRefund.OnUpdate(dt);
            LockdownPortalSanctuaryEngine.OnUpdate(dt);

            LockdownInvariantHarness.OnUpdate(dt);
        }

        public void Shutdown()
        {
        }
    }
}
