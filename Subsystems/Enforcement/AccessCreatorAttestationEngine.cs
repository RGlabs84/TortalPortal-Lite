using System;
using System.Collections.Generic;
using System.Linq;
using Splatform;
using TortalPortalLite.Core;
using TortalPortalLite.Core.Data;
using TortalPortalLite.Core.Hooks;
using TortalPortalLite.Subsystems.Foundations;

namespace TortalPortalLite.Subsystems.Enforcement
{
    /// <summary>
    /// #46 Creator Attestation. `Piece.SetCreator` writes `ZDOVars.s_creator` (the placing client's own
    /// `PlayerProfile.m_playerID`) and `ZDOVars.s_creatorIndex` (an index into the server-maintained
    /// `ZNet.World.m_playerHistory`) on the PLACING CLIENT - both forgeable in isolation by a modified
    /// client. This engine piggybacks on RpcZdoDataHook's existing postfix (the "network-receive path" -
    /// every client-authored ZDO write, portal or not, passes through it) rather than installing its own
    /// second patch on ZDOMan.RPC_ZDOData/ZDO.Deserialize (that hook already exists precisely to avoid a
    /// second competing patch on the same vanilla method - see RpcZdoDataHook's own doc comment).
    ///
    /// Verification is against the SENDER'S OWN character ZDO, not just their socket identity: a
    /// connected peer's true `PlayerProfile.m_playerID` is sitting right there in
    /// `ZDOMan.GetZDO(peer.m_characterID).GetLong(ZDOVars.s_playerID)` (the same field Player.SetPlayerID
    /// stamps on login - ConnectedCharacters.PlayerId reads the identical value for a player's own
    /// character). That is directly comparable to the portal's claimed `s_creator`, which is exactly what
    /// this option needs and is more precise than resolving through player-history alone.
    /// `s_creatorIndex` is separately re-derived from `ZNet.World.m_playerHistory.FindIndex(...)` against
    /// the socket-verified PlatformUserID (SenderContext.HostNameOf) - the server-maintained,
    /// append-only table the catalog cites.
    /// </summary>
    public static class AccessCreatorAttestationEngine
    {
        private static readonly Dictionary<ZDOID, long> _lastKnownCreator = new Dictionary<ZDOID, long>();

        public static void Initialize()
        {
            RpcZdoDataHook.RegisterPostfix(50, OnZdoData);
        }

        private static void OnZdoData(ZNetPeer? sender, ZDOID zdoid)
        {
            if (sender == null || ZDOMan.instance == null)
            {
                return;
            }

            ZDO zdo = ZDOMan.instance.GetZDO(zdoid);
            if (zdo == null || !zdo.IsValid())
            {
                return;
            }

            if (!PortalRegistry.IsPortalPrefabHash(zdo.GetPrefab()))
            {
                return;
            }

            try
            {
                long claimedCreator = zdo.GetLong(ZDOVars.s_creator, 0L);

                // #46's own failure mode: s_creator == 0 means "unowned" (world-gen, admin-spawned, or
                // pre-dates this field) - never treat it as "owned by player 0", and never force an
                // attribution onto whichever client happened to be the first to sync an unowned portal.
                if (claimedCreator == 0L)
                {
                    _lastKnownCreator.Remove(zdoid);
                    return;
                }

                if (_lastKnownCreator.TryGetValue(zdoid, out long last) && last == claimedCreator)
                {
                    return; // unchanged since we last verified it - nothing to re-check.
                }

                ZDO? senderCharZdo = sender.m_characterID.IsNone() ? null : ZDOMan.instance.GetZDO(sender.m_characterID);
                if (senderCharZdo == null || !senderCharZdo.IsValid())
                {
                    return; // can't verify yet (character not resolvable this tick) - retry on the next write.
                }
                long trueProfileId = senderCharZdo.GetLong(ZDOVars.s_playerID, 0L);
                if (trueProfileId == 0L)
                {
                    return; // sender's own s_playerID hasn't landed yet (fresh connection race) - retry later.
                }

                string? hostName = SenderContext.HostNameOf(sender);
                int trueIdx = -1;
                if (hostName != null && ZNet.instance != null)
                {
                    string wantId = new PlatformUserID(hostName).ToString();
                    List<ZNet.CrossNetworkUserInfo> history = ZNet.World?.m_playerHistory ?? new List<ZNet.CrossNetworkUserInfo>();
                    trueIdx = history.FindIndex(h => h.m_id.ToString() == wantId);
                }

                int claimedIdx = zdo.GetInt(ZDOVars.s_creatorIndex, -1);
                bool creatorForged = trueProfileId != claimedCreator;
                bool indexWrong = claimedIdx != trueIdx && trueIdx >= 0; // only correct the index when we could actually resolve one

                if (creatorForged || indexWrong)
                {
                    PortalOwnership.ClaimAndWrite(zdo, z =>
                    {
                        if (creatorForged)
                        {
                            z.Set(ZDOVars.s_creator, trueProfileId);
                        }
                        if (trueIdx >= 0)
                        {
                            z.Set(ZDOVars.s_creatorIndex, trueIdx);
                        }
                    });
                    PortalDebug.LogAlways($"[AccessCreatorAttestationEngine] {(creatorForged ? "FORGERY corrected" : "index corrected")} on {zdoid}: claimed s_creator={claimedCreator} idx={claimedIdx}, true profileId={trueProfileId} idx={trueIdx} (sender {hostName ?? "unknown"}).");
                }

                _lastKnownCreator[zdoid] = trueProfileId;

                // Bridge into Portal Record Store's ownership only when nothing has claimed it yet and
                // attribution is now trustworthy - this is what lets every later access-control option
                // "say the owner of this portal is this Steam account and mean it" (the option's own
                // playerExperience text).
                if (hostName != null && string.IsNullOrEmpty(PortalRecordStore.GetOwnerPlatformId(zdo)))
                {
                    PortalRecordStore.SetOwnerPlatformId(zdo, hostName);
                }
            }
            catch (Exception ex)
            {
                PortalDebug.LogWarning($"[AccessCreatorAttestationEngine] failed for {zdoid}: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }
}
