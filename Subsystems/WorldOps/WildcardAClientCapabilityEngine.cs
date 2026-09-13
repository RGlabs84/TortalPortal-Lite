using System;
using System.Collections.Generic;
using TortalPortalLite.Core;
using TortalPortalLite.Core.Data;

namespace TortalPortalLite.Subsystems.WorldOps
{
    /// <summary>
    /// #151 The Minimal Optional Client Mod - Where The Ceiling Actually Sits. This mod is strictly
    /// server-side, so no companion client plugin ships today - but #151's own buildable half is the
    /// SERVER-SIDE half of the handshake that lets a FUTURE optional client add-on announce itself and
    /// gate an enhanced path on that, without requiring the client mod to exist for this engine to be a
    /// real, working piece of infrastructure rather than a stub.
    ///
    /// Root cause 3 from #151's own analysis ("no UI... structurally unreachable server-side") is solved
    /// the same way vanilla itself solves "does this peer support X": a plain ZRoutedRpc.Register call,
    /// the identical mechanism Chat.Awake/Talker.Awake/PlayerNotify's "Message" already use throughout
    /// this codebase - NOT a Harmony patch, so it needs no Core/Hooks/ broker and is not covered by this
    /// task's "never write your own [HarmonyPatch]" rule. An optional client plugin would call
    /// `ZRoutedRpc.instance.InvokeRoutedRPC("TPL_ClientAnnounce", clientVersionString)`; this engine
    /// registers the server-side receiver and remembers which peer announced which version, exposing
    /// `IsEnhancedPeer` for every other engine (and #141/#140's own toll/UI-shaped ideas, should a client
    /// mod ever exist) to gate an enhanced path on - #151's own design shape: "vanilla clients keep the
    /// degraded ZDO-only experience, modded clients get the full one".
    /// </summary>
    public static class WildcardAClientCapabilityEngine
    {
        private const string AnnounceRpcName = "TPL_ClientAnnounce";

        private static readonly Dictionary<long, string> _announcedVersionByPeerUid = new Dictionary<long, string>();
        private static bool _registered;

        public static void Initialize()
        {
            if (_registered || WildcardAConfig.ClientCapabilityHandshakeEnabled?.Value == false || ZRoutedRpc.instance == null)
            {
                return;
            }
            try
            {
                ZRoutedRpc.instance.Register<string>(AnnounceRpcName, OnClientAnnounce);
                _registered = true;
                PortalDebug.LogInfo($"[WildcardAClientCapabilityEngine] registered '{AnnounceRpcName}' - a real optional client mod may now announce itself; nothing happens if none ever does.");
            }
            catch (Exception ex)
            {
                PortalDebug.LogError($"[WildcardAClientCapabilityEngine] failed to register '{AnnounceRpcName}': {ex.Message}");
            }
        }

        private static void OnClientAnnounce(long sender, string version)
        {
            _announcedVersionByPeerUid[sender] = version ?? "";
            PortalDebug.LogInfo($"[WildcardAClientCapabilityEngine] peer-uid {sender} announced optional client version '{version}'.");
        }

        public static bool IsEnhancedPeer(long peerUid) => _announcedVersionByPeerUid.ContainsKey(peerUid);

        public static string? AnnouncedVersion(long peerUid) => _announcedVersionByPeerUid.TryGetValue(peerUid, out string v) ? v : null;

        /// <summary>Drops bookkeeping for peers no longer connected - call periodically, not required for correctness (a stale entry only makes IsEnhancedPeer return a stale true for a disconnected uid, harmless since peer uids are not reused within a session).</summary>
        public static void PruneDisconnected()
        {
            if (_announcedVersionByPeerUid.Count == 0 || ZNet.instance == null)
            {
                return;
            }
            var connected = new HashSet<long>();
            foreach (ZNetPeer peer in ZNet.instance.GetPeers())
            {
                if (peer != null)
                {
                    connected.Add(peer.m_uid);
                }
            }
            List<long> stale = null;
            foreach (long uid in _announcedVersionByPeerUid.Keys)
            {
                if (!connected.Contains(uid))
                {
                    (stale ??= new List<long>()).Add(uid);
                }
            }
            if (stale != null)
            {
                foreach (long uid in stale)
                {
                    _announcedVersionByPeerUid.Remove(uid);
                }
            }
        }
    }
}
