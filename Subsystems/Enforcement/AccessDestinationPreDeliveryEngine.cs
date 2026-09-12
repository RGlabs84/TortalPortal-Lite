using TortalPortalLite.Core;
using TortalPortalLite.Core.Data;
using TortalPortalLite.Subsystems.Foundations;

namespace TortalPortalLite.Subsystems.Enforcement
{
    /// <summary>
    /// #213 Destination Pre-Delivery. `TeleportWorld.TargetFound` returns false and fires `RequestZDO`
    /// when the client lacks the destination ZDO, and `UpdatePortal` only evaluates it once a player is
    /// already inside the portal's activation range - so vanilla's own warm-up only starts once the
    /// player is already standing at the gate, and the first walk-through to any destination the client
    /// has not yet seen fails silently at gate 1 with no message.
    ///
    /// Fully achievable WITHOUT a new hook: `ZDOMan.ForceSendZDO(long peerID, ZDOID id)` (a distinct,
    /// public, PEER-TARGETED overload from the broadcast-to-everyone `ForceSendZDO(ZDOID id)` this mod
    /// already uses elsewhere) lets this engine push a specific destination ZDO to a specific approaching
    /// player directly, on an ordinary poll loop - no patch on `ZDOMan.CreateSyncList` required, unlike
    /// #211/#212's withholding mechanism (the opposite operation - removing an already-delivered ZDO -
    /// which genuinely has no such direct primitive and is flagged separately).
    ///
    /// Scoped to MANAGED portals only (has a Portal Record Store network id, or an AccessAclStore
    /// record) - pre-pushing every vanilla portal's destination to every nearby player regardless of
    /// this mod's involvement would be needless traffic for structures this mod has no stake in.
    /// </summary>
    public static class AccessDestinationPreDeliveryEngine
    {
        private static float _timer;

        public static void OnUpdate(float dt)
        {
            if (AccessConfig.PreDeliveryEnabled?.Value == false || ZDOMan.instance == null)
            {
                return;
            }
            _timer += dt;
            float interval = AccessConfig.PreDeliveryIntervalSeconds?.Value ?? 2f;
            if (_timer < interval)
            {
                return;
            }
            _timer = 0f;

            float radius = AccessConfig.PreDeliveryRadius?.Value ?? 25f;
            float radiusSqr = radius * radius;

            foreach (ConnectedCharacter who in ConnectedCharacters.All())
            {
                foreach (PortalRecord portal in PortalCensus.Latest)
                {
                    if (portal.Connection == ZDOID.None || !IsManaged(portal))
                    {
                        continue;
                    }
                    if ((portal.Position - who.Position).sqrMagnitude > radiusSqr)
                    {
                        continue;
                    }
                    ZDOMan.instance.ForceSendZDO(who.Peer.m_uid, portal.Connection);
                }
            }
        }

        private static bool IsManaged(PortalRecord portal)
        {
            ZDO? zdo = ZDOMan.instance.GetZDO(portal.Uid);
            if (zdo == null || !zdo.IsValid())
            {
                return false;
            }
            return !string.IsNullOrEmpty(PortalRecordStore.GetNetworkId(zdo)) || AccessAclStore.TryGet(portal.Position, out _);
        }
    }
}
