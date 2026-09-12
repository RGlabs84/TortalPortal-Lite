using System.Collections.Generic;
using TortalPortalLite.Core;

namespace TortalPortalLite.Subsystems.Topology
{
    /// <summary>Admin declaration for one Progression-Gated Sealed Gate (routing.json section "sealedGates").</summary>
    public sealed class RoutingSealedGateDefinition
    {
        public string Name = "";
        public RoutingPosition Position = new RoutingPosition();

        /// <summary>Passed straight to ZoneSystem.GetGlobalKey(string) - matches a real GlobalKeys enum name (e.g. "defeated_bonemass") or a custom NonServerOption mod flag uniformly, since that overload classifies either kind by string.</summary>
        public string GlobalKey = "";

        public RoutingPosition? Destination;
        public string? SealedTag;
        public string? OpenTag;
    }

    /// <summary>
    /// #29 Progression-Gated Sealed Gate. `ZoneSystem.instance.GetGlobalKey(string)` (:115968-115973) is
    /// a plain dictionary read against state `ZoneSystem.GlobalKeyAdd` (:113483) already maintains for
    /// every boss-defeat/quest-flag key, server or custom. Evaluated at 1Hz and written through
    /// RoutingWriteOps.Reassert, which is a no-op when nothing changed - so "blindly re-check every
    /// tick" is not just simplest but literally free in the steady state
    /// (ZDOExtraData.SetConnection's own early-return, :74954-74957), and as a side effect self-heals
    /// against `Game.RPC_SetConnection` (:100653-100662), which the catalog notes is an UNAUTHENTICATED
    /// global routed RPC any connected client can invoke to rewire any portal - a 1Hz re-assert reverts
    /// that within a second without this engine needing to patch or validate the RPC itself.
    ///
    /// Deliberately polling GetGlobalKey rather than patching `ZoneSystem.GlobalKeyAdd` (which the
    /// catalog itself warns re-fires for every historical key at every boot via
    /// `SetStartingGlobalKeys`, :115904-115931) - reading current state fresh every tick has no boot-replay
    /// hazard to guard against in the first place, and no new Harmony patch is needed.
    /// </summary>
    public static class RoutingSealedGateEngine
    {
        private static float _timer;
        private static int _lastRegistryVersion = -1;
        private static List<RoutingSealedGateDefinition> _gates = new List<RoutingSealedGateDefinition>();

        public static void OnUpdate(float dt)
        {
            if (RoutingConfig.TakeoverEnabled?.Value == false || ZDOMan.instance == null || ZoneSystem.instance == null)
            {
                return;
            }
            _timer += dt;
            float interval = RoutingConfig.SealedGateEvalSeconds?.Value ?? 1.0f;
            if (_timer < interval)
            {
                return;
            }
            _timer = 0f;

            if (RoutingManagedPortalRegistry.Version != _lastRegistryVersion)
            {
                _lastRegistryVersion = RoutingManagedPortalRegistry.Version;
                _gates = RoutingManagedPortalRegistry.Section<RoutingSealedGateDefinition>("sealedGates");
            }

            foreach (RoutingSealedGateDefinition gate in _gates)
            {
                if (string.IsNullOrEmpty(gate.GlobalKey) || !RoutingPairingAuthorityEngine.TryClaim(gate.Position, $"sealedgate:{gate.Name}"))
                {
                    continue;
                }
                ZDO? zdo = RoutingWriteOps.ResolveLive(gate.Position);
                if (zdo == null)
                {
                    continue;
                }

                bool open = ZoneSystem.instance.GetGlobalKey(gate.GlobalKey);
                ZDOID desiredConnection = ZDOID.None;
                if (open && gate.Destination != null)
                {
                    ZDO? destZdo = RoutingWriteOps.ResolveLive(gate.Destination);
                    if (destZdo != null)
                    {
                        desiredConnection = destZdo.m_uid;
                    }
                }
                string? desiredTag = open ? gate.OpenTag : gate.SealedTag;

                RoutingWriteOps.Reassert(zdo, desiredTag, desiredConnection);
                RoutingPairingAuthorityEngine.Publish(zdo.m_uid, desiredTag, desiredConnection);
            }
        }
    }
}
