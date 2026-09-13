using UnityEngine;
using TortalPortalLite.Core.Data;

namespace TortalPortalLite.Subsystems.Topology
{
    /// <summary>
    /// Trimmed to just the map-pin feedback primitive #207 Corpse-Run Gate reuses. #193 Map Ping ->
    /// Destination's own input side (a player pinging the map to retarget a hub) is not part of this
    /// build's wanted set and has been cut - it needed a routed-RPC content hook this codebase doesn't
    /// have anyway (see the option's own catalog notes).
    /// </summary>
    public static class TargetedMapPingEngine
    {
        /// <summary>Saved, named, deduplicated pin on the requesting client - InvokeRoutedRPC(peer, "RPC_DiscoverLocationResponse", label, PinType.Icon3, pos, showMap:false). SERVER decompile citation: RPC_DiscoverLocationResponse :100068/:100823-100831.</summary>
        public static void PushSavedPin(ConnectedCharacter who, string label, Vector3 pos)
        {
            if (ZRoutedRpc.instance == null || who.Peer == null)
            {
                return;
            }
            ZRoutedRpc.instance.InvokeRoutedRPC(who.Peer.m_uid, "RPC_DiscoverLocationResponse", label, (int)Minimap.PinType.Icon3, pos, false);
        }
    }
}
