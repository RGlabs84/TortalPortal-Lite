using System;
using System.Collections.Generic;
using System.Linq;
using TortalPortalLite.Core;

namespace TortalPortalLite.Subsystems.Enforcement
{
    /// <summary>
    /// #57 Ungated RPC Hardening + #210 Extended RPC Veto Table, combined - #210 is literally "add more
    /// entries to #57's own veto list", so one engine carries both.
    ///
    /// NONE of the seven RPC handlers these two options name have a Core/Hooks/ broker, and this wave's
    /// rules are explicit: never install a new Harmony patch directly, flag the need instead. So this
    /// engine is honest about being a PARTIAL, reactive mitigation for two of the seven, and a pure flag
    /// for the rest - it does not claim the "server-enforced, no window" tier the catalog describes for
    /// the full prefix-veto design.
    ///
    /// What IS implemented without a new patch:
    ///  1. A reactive guard on a configured set of world global keys (default: nobossportals, noportals)
    ///     - `ZoneSystem.SetGlobalKey`/`GetGlobalKey`/`RemoveGlobalKey` are all public, ordinary API calls
    ///     (no patching needed to READ or WRITE a key, only to intercept who else writes one), so this
    ///     engine polls the guarded keys and reverts any change that was not explicitly flagged as
    ///     intentional by another engine in this mod (see NotifyIntentionalChange, used by
    ///     AccessBossLockdownCorrectionEngine). This is Tag-Watchdog-shaped detect-and-revert, not
    ///     prevention - a hostile SetGlobalKey/RemoveGlobalKey is live for up to one guard tick.
    ///  2. `Game.RPC_SetConnection` hijacks are NOT vetoed directly, but any connection it writes to a
    ///     managed portal is caught and reverted by AccessTagWatchdogEngine on its own very next tick
    ///     (it diffs `record.Connection` the same way it diffs `record.Tag`) - cross-referenced here so
    ///     the coverage is not silently assumed to not exist.
    ///
    /// NEEDS NEW HOOK BROKER on ZoneSystem.RPC_SetGlobalKey(long,string) / RPC_RemoveGlobalKey(long,string):
    /// purpose - a true prefix veto (allow only when the resolved Authentic Sender Context is the server
    /// itself or an admin host name) would close the guard-tick window in (1) entirely.
    ///
    /// NEEDS NEW HOOK BROKER on Game.RPC_SetConnection(long,ZDOID,ZDOID): purpose - a true prefix veto
    /// would stop a hijacked connection from ever being written at all, instead of relying on the next
    /// Tag Watchdog tick to notice and revert it.
    ///
    /// NEEDS NEW HOOK BROKER on ZDOMan.RPC_RequestZDO(long,ZDOID): purpose - the payload `sender` this
    /// handler forwards to `ZDOPeer.ForceSendZDO` is spoofable and enables a reconnaissance/amplification
    /// primitive (ask the server to blast any ZDO at any other peer); a prefix replacing the payload
    /// sender with the authenticated peer's own uid closes it while leaving the legitimate
    /// TeleportWorld.TargetFound -> RequestZDO path untouched. No reactive mitigation is possible here
    /// (there is nothing after the fact to observe or revert) - this one is a pure flag.
    ///
    /// NEEDS NEW HOOK BROKER on ZNetScene.RPC_SpawnObject(long,Vector3,Quaternion,int): purpose - bare
    /// unauthenticated server-side Instantiate of any named prefab; a prefix admin-gating it (or scoping
    /// it to what a legitimate caller would ever request) closes it. Not portal-specific, lowest
    /// priority of the seven, pure flag.
    ///
    /// NEEDS NEW HOOK BROKER on PersistentEventSystem.RPC_RequestStartEvent(long,int) /
    /// RPC_RequestStopEvent(long,int) / RPC_UpdateClientEventsList(long,string): purpose (#210) - the
    /// first two run entirely unauthenticated server-side spawn/removal logic with no bounds checking
    /// (an out-of-range source event id throws inside the RPC dispatch); the third lets any client
    /// forge the world's entire active-event list on every other client's screen. A prefix admin-gating
    /// start/stop and dropping UpdateClientEventsList from any non-server sender closes all three. Pure
    /// flag - nothing to react to after the fact without the hook (the spawn/broadcast has already
    /// happened by the time any census-style diff would notice).
    ///
    /// NEEDS NEW HOOK BROKER on Ship.RPC_Forward/RPC_Backward/RPC_Stop/RPC_Rudder (all take no sender
    /// parameter beyond the routed envelope): purpose (#210) - these set helm state unconditionally with
    /// no owner/proximity check; a prefix requiring the true sender's character to be within a
    /// configured radius of the ship (or to hold `ZDOVars.s_user`) stops remote-steering a ship a client
    /// is nowhere near. Not portal-related; lowest priority. Pure flag.
    /// </summary>
    public static class AccessRpcHardeningEngine
    {
        private static readonly Dictionary<string, bool> _lastKnown = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        private const float GuardIntervalSeconds = 2f;
        private static float _timer;

        public static void OnUpdate(float dt)
        {
            if (AccessConfig.GlobalKeyGuardEnabled?.Value == false || ZoneSystem.instance == null)
            {
                return;
            }
            _timer += dt;
            if (_timer < GuardIntervalSeconds)
            {
                return;
            }
            _timer = 0f;
            Guard();
        }

        /// <summary>Called by an engine (e.g. AccessBossLockdownCorrectionEngine) right after it deliberately changes a guarded key, so the very next guard tick does not treat its own write as an attack.</summary>
        public static void NotifyIntentionalChange(string key)
        {
            if (ZoneSystem.instance != null && !string.IsNullOrEmpty(key))
            {
                _lastKnown[key] = ZoneSystem.instance.GetGlobalKey(key);
            }
        }

        private static void Guard()
        {
            foreach (string key in GuardedKeys())
            {
                bool present = ZoneSystem.instance.GetGlobalKey(key);
                if (_lastKnown.TryGetValue(key, out bool prior) && prior != present)
                {
                    // Drift nobody in this mod flagged as intentional - revert it.
                    if (prior)
                    {
                        ZoneSystem.instance.SetGlobalKey(key);
                    }
                    else
                    {
                        ZoneSystem.instance.RemoveGlobalKey(key);
                    }
                    PortalDebug.LogAlways($"[AccessRpcHardeningEngine] guarded global key '{key}' changed without this mod's authorisation - reverted to {prior}.");
                    present = prior;
                }
                _lastKnown[key] = present;
            }
        }

        private static IEnumerable<string> GuardedKeys()
        {
            string csv = AccessConfig.GuardedGlobalKeys?.Value ?? "";
            return csv.Split(',').Select(s => s.Trim()).Where(s => s.Length > 0);
        }
    }
}
