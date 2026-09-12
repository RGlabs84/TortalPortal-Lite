using BepInEx.Configuration;
using HarmonyLib;
using ServerSync;
using TortalPortalLite.Core;

namespace TortalPortalLite.Subsystems.PlayerInterface
{
    /// <summary>
    /// The `ux` domain subsystem - Wave 2's "how a 100%-vanilla-client player actually drives this mod"
    /// half (34 catalog options, #152-177/#260-272). Thin dispatcher over many small, mostly-independent
    /// engines, following PortalOpsSubsystem's (#84) own established shape: one subsystem, real logic in
    /// sibling static engine classes, a fixed intra-tick order only where two engines actually share
    /// state.
    ///
    /// THE CENTRAL DESIGN, restated: every input channel here (tag CLI, sign text, map ping, jump/dodge
    /// gestures, emotes, equipped items, item stands, chest tokens, floor plates, build-shape sockets,
    /// warp-verb emotes) resolves to exactly one of the three channels that actually reach a dedicated
    /// server (a global routed RPC, `RPC_ZDOData` -&gt; ZDO field writes, or plain ZDO polling), and every
    /// one of them that actually CHANGES a portal's routing funnels through the single shared
    /// `UxDialAction`, which is the only place in this domain that writes a portal's tag/connection.
    /// Feedback (tag echo, toasts, world text, VFX/SFX, map pins, status effects, voice, sleep fade,
    /// puppet-string body triggers) is all output-only and centralised in a handful of small helper
    /// classes for the same reason.
    ///
    /// Tick order: ArmingGate (the shared crouch-arm/commit primitive several input channels consume)
    /// before anything that reads it; discovery/reassert-style engines next; the scheme integration
    /// wrapper last, since it only ever reads what everything else already produced.
    /// </summary>
    public class UxSubsystem : IPortalSubsystem
    {
        public string Name => "PlayerInterface";
        public bool IsEnabled => GlobalConfig.Enabled?.Value != false && UxConfig.Enabled?.Value != false;

        public void Initialize(ConfigFile config, ConfigSync configSync, Harmony harmony)
        {
            UxConfig.Bind(config, configSync);

            UxWardIndex.Initialize();

            // Event-driven input engines - each registers against an existing Core/Hooks/ broker or
            // Core/Data poller and needs no per-tick work of its own.
            UxDialEngine.Initialize();
            UxSignInputEngine.Initialize();
            UxItemStandBoardEngine.Initialize();
            UxChestTokenEngine.Initialize();
            UxBuildShapeSwitchEngine.Initialize();
            UxEmoteInputEngine.Initialize();
            UxGestureTriggerEngine.Initialize();
            UxStatusAndPuppetEngine.Initialize();
            UxSummoningGazeEngine.Initialize();
            UxWarpVerbsEngine.Initialize();
        }

        public void OnWorldReady()
        {
            UxCartographersTableEngine.OnWorldReady();
        }

        public void OnUpdate()
        {
            float dt = UnityEngine.Time.deltaTime;

            // Shared primitives first.
            UxArmingGate.OnUpdate(dt);
            UxWardIndex.OnUpdate(dt);
            UxForcedTeleport.OnUpdate(dt);
            UxFreeBurst.OnUpdate(dt);

            // Ticked input engines (polling-based rather than event-driven).
            UxEquipItemSelectorEngine.OnUpdate(dt);
            UxChestTokenEngine.OnUpdate(dt);
            UxBuildShapeSwitchEngine.OnUpdate(dt);
            UxPositionalInputEngine.OnUpdate(dt);
            UxGestureTriggerEngine.OnUpdate(dt);
            UxMapPingEngine.OnUpdate(dt);
            UxSummoningGazeEngine.OnUpdate(dt);

            // Output / discovery / reassertion engines.
            UxLedgerEngine.OnUpdate(dt);
            UxMapPinFeedback.OnUpdate(dt);
            UxGlobalKeyFeedback.OnUpdate(dt);
            UxStatusAndPuppetEngine.OnUpdate(dt);
            UxLivePortalMarkersEngine.OnUpdate(dt);
            UxCartographersTableEngine.OnUpdate(dt);

            // Integration wrapper last - only ever reads what everything above already produced.
            UxDialScheme.OnUpdate(dt);
        }

        public void Shutdown()
        {
        }
    }
}
