using System.Collections.Generic;
using UnityEngine;
using TortalPortalLite.Core;
using TortalPortalLite.Core.Data;

namespace TortalPortalLite.Subsystems.Topology
{
    /// <summary>Admin opt-in for one Pre-Stream gate (routing.json section "preStreamGates") - a managed portal whose CURRENT destination (whatever any other engine last wrote into it) is pre-streamed to an approaching player.</summary>
    public sealed class RoutingPreStreamGateDefinition
    {
        public string Name = "";
        public RoutingPosition Position = new RoutingPosition();
    }

    /// <summary>
    /// #222 Pre-Stream Engine (Server-Initiated Destination Streaming). The catalog's own "elegant"
    /// design is a postfix on the private `ZDOMan.CreateSyncList(ZDOPeer, List&lt;ZDO&gt;)` (:77212)
    /// appending destination-area ZDOs directly into a peer's own sync list ahead of vanilla's own
    /// FindSectorObjects sweep.
    ///
    /// // NEEDS NEW HOOK BROKER on ZDOMan.CreateSyncList: none of Core/Hooks/'s existing brokers cover
    /// // this private method, and three agents are editing Core/Hooks/ concurrently this wave.
    ///
    /// What IS implemented achieves the SAME OUTCOME (an approaching player's client holds the
    /// destination area's ZDOs before they arrive) through an already-public primitive instead of a new
    /// patch: `ZDOMan.instance.ForceSendZDO(peerUid, zdoId)` adds directly into that peer's own
    /// `ZDOPeer.m_forceSend` set (:75998-76001), which vanilla's OWN existing `AddForceSendZdos` already
    /// drains into that peer's very next sync list on its normal send cycle (:77263-77286) - there is no
    /// need to intercept CreateSyncList to get ZDOs delivered ahead of physical arrival, only to get
    /// vanilla's own priority/budget heuristics applied to them the way the catalog's fuller design
    /// intends. On arming (RoutingConfig.PreStreamRadius, default 20m), this engine reads the gate's
    /// CURRENT live connection (whatever RoutingPairingAuthorityEngine's owning mechanism last wrote,
    /// generic across hub/schedule/event/etc.), gathers nearby ZDOs around that destination via
    /// ZdoSpatialQuery.FindNear, and force-sends up to RoutingConfig.PreStreamMaxZdosPerCycle of them to
    /// the approaching peer - budgeted per RoutingConfig.PreStreamMaxZdosPerCycle exactly as the catalog
    /// itself calls for, so a large destination base cannot flood one peer's send queue in one pass.
    /// </summary>
    public static class RoutingPreStreamEngine
    {
        private static float _timer;
        private static int _lastRegistryVersion = -1;
        private static List<RoutingPreStreamGateDefinition> _gates = new List<RoutingPreStreamGateDefinition>();
        private static readonly RoutingApproachWatcher _watcher = new RoutingApproachWatcher();
        private static readonly List<ZDO> _scanBuffer = new List<ZDO>();

        public static void OnUpdate(float dt)
        {
            if (RoutingConfig.TakeoverEnabled?.Value == false || ZDOMan.instance == null)
            {
                return;
            }
            _timer += dt;
            float interval = RoutingConfig.ApproachPollSeconds?.Value ?? 0.1f;
            if (_timer < interval)
            {
                return;
            }
            _timer = 0f;

            if (RoutingManagedPortalRegistry.Version != _lastRegistryVersion)
            {
                _lastRegistryVersion = RoutingManagedPortalRegistry.Version;
                _gates = RoutingManagedPortalRegistry.Section<RoutingPreStreamGateDefinition>("preStreamGates");
            }
            if (_gates.Count == 0)
            {
                return;
            }

            var positions = new List<(string, Vector3)>(_gates.Count);
            foreach (RoutingPreStreamGateDefinition gate in _gates)
            {
                positions.Add((gate.Name, gate.Position.ToVector3()));
            }

            float radius = RoutingConfig.PreStreamRadius?.Value ?? 20f;
            _watcher.Poll(positions, radius, 5f, onArm: HandleArm);
        }

        private static void HandleArm(string gateName, ConnectedCharacter character)
        {
            foreach (RoutingPreStreamGateDefinition gate in _gates)
            {
                if (gate.Name != gateName)
                {
                    continue;
                }
                ZDO? gateZdo = RoutingWriteOps.ResolveLive(gate.Position);
                if (gateZdo == null)
                {
                    return;
                }
                ZDOID destId = gateZdo.GetConnectionZDOID(ZDOExtraData.ConnectionType.Portal);
                if (destId == ZDOID.None)
                {
                    return;
                }
                ZDO? destZdo = ZDOMan.instance!.GetZDO(destId);
                if (destZdo == null)
                {
                    return;
                }

                RoutingWriteOps.PrewarmToPeer(character.Peer.m_uid, destId);

                _scanBuffer.Clear();
                ZdoSpatialQuery.FindNear(destZdo.GetPosition(), 64f, _scanBuffer);
                int budget = RoutingConfig.PreStreamMaxZdosPerCycle?.Value ?? 400;
                foreach (ZDO nearby in _scanBuffer)
                {
                    if (budget-- <= 0)
                    {
                        break;
                    }
                    RoutingWriteOps.PrewarmToPeer(character.Peer.m_uid, nearby.m_uid);
                }
                return;
            }
        }
    }
}
