using System.Collections.Generic;
using TortalPortalLite.Core;
using TortalPortalLite.Core.Data;
using TortalPortalLite.Core.Hooks;
using TortalPortalLite.Subsystems.Foundations;

namespace TortalPortalLite.Subsystems.Enforcement
{
    /// <summary>
    /// #214 Untagged Auto-Pair Suppression. `ZDO.GetString(s_tag)` defaults to "" and every never-named
    /// portal in the world therefore shares one giant tag group, which vanilla's
    /// `FindRandomUnconnectedPortal` will happily pair at random - "why is my new portal connected to a
    /// stranger's basement" is stock behaviour, not an attack, but it is exactly what a fresh, unnamed
    /// portal should NOT do.
    ///
    /// Primary mechanism (server-enforced, writes nothing): registered on
    /// FindRandomUnconnectedPortalHook at a LOW priority number so it runs before
    /// AccessManagedNetworkGovernorEngine's own handler and simply refuses to hand back a partner at all
    /// for the empty/whitespace tag - `Game.ConnectPortals` phase 2 then leaves that portal unconnected
    /// for the tick, exactly like a real "no partner available" outcome vanilla itself can also produce.
    ///
    /// Secondary sweep: because vanilla's phase 1 only tears down a connection whose PARTNER is missing,
    /// mismatched-tag, or itself disconnected (never checking reciprocity), an existing ""&lt;-&gt;""
    /// pair - formed before this mod loaded, or in the gap before the primary mechanism's first tick -
    /// survives indefinitely. This periodic sweep finds and clears both ends; each side converges
    /// independently in the same pass since both record's Tag == "" satisfies the same check.
    /// </summary>
    public static class AccessUntaggedAutoPairSuppressionEngine
    {
        private const float SweepIntervalSeconds = 5f;
        private static float _timer;

        public static void Initialize()
        {
            FindRandomUnconnectedPortalHook.Register(10, OnFindRandomUnconnectedPortal);
        }

        private static bool OnFindRandomUnconnectedPortal(List<ZDO> portals, ZDO skip, string tag, out ZDO? result)
        {
            result = null;
            if (string.IsNullOrWhiteSpace(tag))
            {
                return true; // refuse pairing outright - a real, vanilla-shaped "no partner" outcome.
            }
            return false;
        }

        public static void OnUpdate(float dt)
        {
            _timer += dt;
            if (_timer < SweepIntervalSeconds || ZDOMan.instance == null)
            {
                return;
            }
            _timer = 0f;

            foreach (PortalRecord record in PortalCensus.Latest)
            {
                if (!string.IsNullOrEmpty(record.Tag) || record.Connection == ZDOID.None)
                {
                    continue;
                }
                if (!PortalCensus.TryGet(record.Connection, out PortalRecord partner) || !string.IsNullOrEmpty(partner.Tag))
                {
                    continue; // partner isn't also untagged - not the pathology this engine targets (leave HealthScanEngine's own diagnostics to flag anything odd here).
                }

                ZDO? zdo = ZDOMan.instance.GetZDO(record.Uid);
                if (zdo == null || !zdo.IsValid())
                {
                    continue;
                }
                PortalOwnership.ClaimAndWrite(zdo, z => z.SetConnection(ZDOExtraData.ConnectionType.Portal, ZDOID.None));
                PortalDebug.LogInfo($"[AccessUntaggedAutoPairSuppressionEngine] cleared a pre-existing untagged<->untagged link: {record.Uid} <-> {partner.Uid}.");
            }
        }
    }
}
