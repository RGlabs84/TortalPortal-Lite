using System.Collections.Generic;
using UnityEngine;
using TortalPortalLite.Core.Data;

namespace TortalPortalLite.Subsystems.Topology
{
    /// <summary>
    /// #188 Player's Custom Spawn Point - a direct read is ruled out (PlayerProfile.WorldPlayerData /
    /// SetCustomSpawnPoint are written client-locally with no RPC and no ZDO, SERVER decompile
    /// :105526-105545/:106224-106238); this engine infers it instead from three signals the client
    /// leaks unintentionally, in the catalog's own priority order, and hands the result to #187's Bed
    /// engine as a preferred override over the raw claimed-bed position (TryGetInferredHome).
    ///
    /// INFERENCE 1 (respawn leak, highest priority): while a peer's character is dead/absent
    /// (ZDOVars.s_dead, or characterID.IsNone() mid-respawn), Game.UpdateRespawn on the CLIENT calls
    /// ZNet.instance.SetReferencePosition(customSpawnPoint) every frame, and the client's own periodic
    /// ServerSyncedPlayerData RPC (~2 s cadence, SERVER decompile :80395-80410) delivers that value into
    /// peer.m_refPos - sampled here directly from ZNet.instance.GetPeers(), bypassing
    /// ConnectedCharacters (which deliberately excludes exactly this "no live character" window, per its
    /// own header comment).
    /// INFERENCE 2 (sleep): s_inBed on the character ZDO plus the nearest bed ZDO within 2 m
    /// (Game.EverybodyIsTryingToSleep reads the same field server-side, :100735-100748).
    /// INFERENCE 3 (login leak, lowest priority): the first non-zero ref-pos sample ever seen for a
    /// connection this session - an approximation of "first RPC_ServerSyncedPlayerData after PeerInfo"
    /// that needs no dedicated peer-connect hook (this codebase's Core/Hooks/ has none).
    ///
    /// Identity is tracked by PEER CONNECTION (m_uid), not by player ID: peer.m_playerID is always 0 in
    /// this codebase's own verified finding (Core/Data/ConnectedCharacters.cs's own remarks - the
    /// PlayerID RPC is never invoked by a vanilla client), and the character ZDO that carries the real
    /// s_playerID does not exist during exactly the dead/respawning window INFERENCE 1 needs to watch -
    /// so this engine remembers the peer-uid -&gt; playerId mapping from whenever a live character WAS
    /// available and correlates samples taken while it wasn't.
    ///
    /// Enforcement is "reactive-detection-only" (the catalog's own classification): this can only ever
    /// OBSERVE a spawn point after the mod has seen at least one death/sleep/login of that player on
    /// this server - it cannot read or set one directly.
    /// </summary>
    public static class TargetedSpawnPointEngine
    {
        private sealed class PeerState
        {
            public long PlayerId;
            public Vector3? Inference1;
            public Vector3? Inference2;
            public Vector3? Inference3;
            public bool DeadWindowActive;
            public float DeadWindowElapsed;
        }

        private static readonly Dictionary<long, PeerState> _statesByPeerUid = new Dictionary<long, PeerState>();
        private static readonly Dictionary<long, long> _peerUidByPlayerId = new Dictionary<long, long>();

        public static void OnUpdate(float dt)
        {
            if (ZNet.instance == null || ZDOMan.instance == null)
            {
                return;
            }
            float window = TargetedConfig.SpawnPointSampleWindowSeconds?.Value ?? 6f;

            foreach (ZNetPeer peer in ZNet.instance.GetPeers())
            {
                if (peer == null || !peer.IsReady())
                {
                    continue;
                }
                if (!_statesByPeerUid.TryGetValue(peer.m_uid, out PeerState st))
                {
                    st = new PeerState();
                    _statesByPeerUid[peer.m_uid] = st;
                }

                bool haveCharacter = !peer.m_characterID.IsNone();
                ZDO charZdo = haveCharacter ? ZDOMan.instance.GetZDO(peer.m_characterID) : null;
                bool valid = charZdo != null && charZdo.IsValid();

                if (valid)
                {
                    long pid = charZdo.GetLong(ZDOVars.s_playerID, 0L);
                    if (pid != 0L)
                    {
                        st.PlayerId = pid;
                        _peerUidByPlayerId[pid] = peer.m_uid;
                    }
                }

                if (st.Inference3 == null && peer.m_refPos != Vector3.zero)
                {
                    st.Inference3 = peer.m_refPos;
                }

                bool dead = !valid || charZdo.GetBool(ZDOVars.s_dead);
                if (dead)
                {
                    st.DeadWindowActive = true;
                    st.DeadWindowElapsed = 0f;
                    if (peer.m_refPos != Vector3.zero)
                    {
                        st.Inference1 = peer.m_refPos; // keep overwriting - the stable value right before the new character ZDO appears wins
                    }
                    continue;
                }

                if (st.DeadWindowActive)
                {
                    st.DeadWindowElapsed += dt;
                    if (st.DeadWindowElapsed >= window)
                    {
                        st.DeadWindowActive = false;
                    }
                }

                if (charZdo.GetBool(ZDOVars.s_inBed) && TryFindNearestBed(charZdo.GetPosition(), 2f, out Vector3 bedPos))
                {
                    st.Inference2 = bedPos;
                }
            }
        }

        /// <summary>The best available inferred "home" for this player, in the catalog's own priority order. False if nothing has ever been observed (caller should fall back to the raw claimed-bed position).</summary>
        public static bool TryGetInferredHome(long playerId, out Vector3 pos)
        {
            pos = default;
            if (playerId == 0L || !_peerUidByPlayerId.TryGetValue(playerId, out long peerUid) || !_statesByPeerUid.TryGetValue(peerUid, out PeerState st))
            {
                return false;
            }
            if (st.Inference1.HasValue)
            {
                pos = st.Inference1.Value;
                return true;
            }
            if (st.Inference2.HasValue)
            {
                pos = st.Inference2.Value;
                return true;
            }
            if (st.Inference3.HasValue)
            {
                pos = st.Inference3.Value;
                return true;
            }
            return false;
        }

        private static bool TryFindNearestBed(Vector3 pos, float radius, out Vector3 bedPos)
        {
            bedPos = default;
            if (TargetedPrefabDiscovery.BedHashes.Count == 0)
            {
                return false;
            }
            List<ZDO> nearby = ZdoSpatialQuery.FindNear(pos, radius);
            foreach (ZDO z in nearby)
            {
                if (TargetedPrefabDiscovery.IsBed(z.GetPrefab()))
                {
                    bedPos = z.GetPosition();
                    return true;
                }
            }
            return false;
        }
    }
}
