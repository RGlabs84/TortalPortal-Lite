using System.Collections.Generic;
using TortalPortalLite.Core;
using TortalPortalLite.Core.Data;
using TortalPortalLite.Core.Hooks;
using TortalPortalLite.Subsystems.Foundations;

namespace TortalPortalLite.Subsystems.Enforcement
{
    /// <summary>
    /// #54 Managed Network Governor (the ACCESS-CONTROL half) + #62 Password Tags, combined - #62's own
    /// conclusion is that a tag can never be a real secret (`TeleportWorld.GetHoverText` has no access
    /// check, so anyone who finds a gate reads its tag) and the only design that actually works is
    /// "the tag is a token you must be told, and the roster - not the string - is the real
    /// authorisation" - which is exactly what this engine already does by preferring
    /// `PortalRecordStore.NetworkId` (a mod-private ZDO key no vanilla UI ever surfaces) over the raw
    /// `s_tag` as the authoritative network identity. That single design choice gives #62's "genuinely
    /// secret variant" for free, using infrastructure #50 already built - no new key needed.
    ///
    /// SCOPE NOTE vs NetworkReassertEngine (Foundations, #69): that engine maintains RING topology for
    /// ADMIN-DECLARED networks (networks.json). This engine only refuses vanilla's own random pairing
    /// for portals it doesn't authorise - dynamically player-declared networks (via #53's claim emotes
    /// or #60's team rosters) get simple pairwise links, the same SHAPE vanilla itself builds, just
    /// restricted to authorised members. Reimplementing ring/hub topology for player-declared networks
    /// was judged out of scope for this wave's time budget; an admin who wants a ring for a
    /// player-built network can still declare it in networks.json and let NetworkReassertEngine own it.
    ///
    /// Registered on FindRandomUnconnectedPortalHook (a Core/Hooks/ broker built for exactly this
    /// decision - see its own doc comment) rather than a prefix on the whole of `Game.ConnectPortals`,
    /// which would disable vanilla pairing for every unmanaged portal on the server too.
    /// </summary>
    public static class AccessManagedNetworkGovernorEngine
    {
        public static void Initialize()
        {
            FindRandomUnconnectedPortalHook.Register(50, OnFindRandomUnconnectedPortal);
        }

        private static bool OnFindRandomUnconnectedPortal(List<ZDO> portals, ZDO skip, string tag, out ZDO? result)
        {
            result = null;
            if (string.IsNullOrEmpty(tag) || ZDOMan.instance == null)
            {
                return false; // empty-tag group is AccessUntaggedAutoPairSuppressionEngine's own registration; let it (or vanilla) decide.
            }

            string skipNetwork = PortalRecordStore.GetNetworkId(skip);
            if (!string.IsNullOrEmpty(skipNetwork))
            {
                // #58 Progression Gate - refuse to wire at all until the declared requirement is met.
                if (!AccessProgressionGateEngine.NetworkMayWire(skipNetwork))
                {
                    result = null;
                    return true;
                }
                result = NearestAuthorized(portals, skip, tag, candidate => PortalRecordStore.GetNetworkId(candidate) == skipNetwork);
                return true;
            }

            // No mod-private network id on the requesting portal - fall back to treating the visible tag
            // itself as a team/guild roster name (#60/#62's "honest" variant: the tag is public, but
            // pairing still requires the OWNER to be on that team's roster, not merely knowing the string).
            if (AccessTeamRoster.TryGetByName(tag, out AccessTeam team))
            {
                result = NearestAuthorized(portals, skip, tag, candidate =>
                {
                    string owner = PortalRecordStore.GetOwnerPlatformId(candidate);
                    return !string.IsNullOrEmpty(owner) && team.Members.Contains(owner);
                });
                return true;
            }

            return false; // an ordinary, unmanaged tag - let vanilla's own random pairing run unmodified.
        }

        private static ZDO? NearestAuthorized(List<ZDO> portals, ZDO skip, string tag, System.Func<ZDO, bool> authorized)
        {
            ZDO? best = null;
            float bestDist = float.MaxValue;
            UnityEngine.Vector3 origin = skip.GetPosition();

            foreach (ZDO candidate in portals)
            {
                if (candidate == null || candidate == skip || !candidate.IsValid())
                {
                    continue;
                }
                if (candidate.GetConnectionZDOID(ZDOExtraData.ConnectionType.Portal) != ZDOID.None)
                {
                    continue; // mirror vanilla's own "still unconnected" filter.
                }
                if (candidate.GetString(ZDOVars.s_tag, "") != tag)
                {
                    continue;
                }
                if (!authorized(candidate))
                {
                    continue; // an unauthorised portal sharing this tag simply never becomes a candidate.
                }
                float d = (candidate.GetPosition() - origin).sqrMagnitude;
                if (d < bestDist)
                {
                    bestDist = d;
                    best = candidate;
                }
            }
            return best;
        }
    }
}
