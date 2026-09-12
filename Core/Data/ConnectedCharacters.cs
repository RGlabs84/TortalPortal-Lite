using System.Collections.Generic;
using UnityEngine;

namespace TortalPortalLite.Core.Data
{
    /// <summary>
    /// One connected player as the dedicated server actually sees them: the peer plus their character
    /// ZDO. Position/rotation are kept fresh every physics tick by the owning client's ZSyncTransform;
    /// identity (s_playerID / s_playerName) is written once by Player.SetPlayerID.
    /// </summary>
    public readonly struct ConnectedCharacter
    {
        public readonly ZNetPeer Peer;
        public readonly ZDO Zdo;

        public ConnectedCharacter(ZNetPeer peer, ZDO zdo)
        {
            Peer = peer;
            Zdo = zdo;
        }

        /// <summary>
        /// The stable identity - PlayerProfile.m_playerID, copied into the world as s_playerID. Persist
        /// this: never Peer.m_uid (a connection, new every session) and never Zdo.m_uid.UserID (the
        /// ZDOMan session that minted the ZDO). 0 until the owning client's Player.SetPlayerID has run.
        /// </summary>
        public long PlayerId => Zdo.GetLong(ZDOVars.s_playerID, 0L);

        public string Name
        {
            get
            {
                string fromZdo = Zdo.GetString(ZDOVars.s_playerName, "");
                return string.IsNullOrEmpty(fromZdo) ? (Peer.m_playerName ?? "") : fromZdo;
            }
        }

        public Vector3 Position => Zdo.GetPosition();
    }

    /// <summary>
    /// Replaces Player.GetAllPlayers() everywhere in this mod. That method walks the LOCAL instance
    /// list, and a dedicated server never instantiates a Player: Game.FixedUpdate pins ZNet's reference
    /// position at (1e6, 0, 1e6) every physics tick, so on the one machine this mod runs on,
    /// GetAllPlayers() is always empty. What the server does hold for every connected player is the
    /// character ZDO behind ZNetPeer.m_characterID, and that is what this yields. Forked verbatim from
    /// Wonderland's Core/Data/ConnectedCharacters.cs (namespace renamed only) - the non-negotiable
    /// server-only primitive every subsystem in this mod builds on.
    /// </summary>
    public static class ConnectedCharacters
    {
        public static List<ConnectedCharacter> All()
        {
            var result = new List<ConnectedCharacter>();
            if (ZNet.instance == null || ZDOMan.instance == null)
            {
                return result;
            }

            foreach (ZNetPeer peer in ZNet.instance.GetPeers())
            {
                // m_characterID arrives via RPC_CharacterID after the peer is otherwise ready, so a
                // ready peer with no character yet is normal for the first moments of a connection.
                if (peer == null || !peer.IsReady() || peer.m_characterID.IsNone())
                {
                    continue;
                }
                ZDO zdo = ZDOMan.instance.GetZDO(peer.m_characterID);
                if (zdo == null || !zdo.IsValid())
                {
                    continue;
                }
                result.Add(new ConnectedCharacter(peer, zdo));
            }
            return result;
        }
    }
}
