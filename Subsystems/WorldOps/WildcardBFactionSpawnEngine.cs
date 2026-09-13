using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using TortalPortalLite.Core;
using TortalPortalLite.Core.Data;

namespace TortalPortalLite.Subsystems.WorldOps
{
    /// <summary>
    /// #239 "Per-Peer Location Icons - Faction Respawn And Private Landmarks". `ZoneSystem.SendLocationIcons(long peer)`
    /// (SERVER decompile :113581-113593, confirmed directly by this build) packs `GetLocationIcons()`
    /// (public, :115450) as (Vector3, name) pairs into the routed RPC "LocationIcons" for ONE peer; the
    /// client's `RPC_LocationIcons` (:113595-113607) CLEARS its own `m_locationIcons` dictionary and
    /// rebuilds it wholesale on every receipt - a full per-peer replace, not a merge. `Game.FindSpawnPoint`
    /// (client, :100774-100804) falls back to `ZoneSystem.GetLocationIcon(m_StartLocation)`, which returns
    /// the FIRST dictionary entry whose value equals "StartTemple" - so a per-peer payload that swaps out
    /// that one entry's key steers where THAT player's bed-less/new characters respawn, without touching
    /// any established player's own logout point or bed (both checked first, unconditionally).
    ///
    /// This engine does NOT patch `SendLocationIcons` (Core/Hooks/ has no broker for it and this mod's
    /// rule is to flag, not add, a competing Harmony patch):
    /// NEEDS NEW HOOK BROKER on ZoneSystem.SendLocationIcons(long): purpose - true zero-flicker per-peer
    /// icon override instead of the reactive re-assert this engine actually ships. Instead it calls the
    /// SAME already-registered outbound RPC channel directly with our own corrected payload - exactly
    /// the "LocationIcons" RPC vanilla itself sends, built with the identical wire format confirmed
    /// against `SendLocationIcons`'s own body (`int count; count * {Vector3 pos; string name}`) - no
    /// Harmony patch anywhere, the same "call an existing outbound RPC with our own data" pattern
    /// Core/Data/PlayerNotify.cs already uses for "Message". Because the client fully replaces its
    /// dictionary on every receipt, sending our own corrected copy AFTER vanilla's own (whether from
    /// `OnNewPeer` or a placement re-broadcast to peer 0, :114999-115003) simply wins until the next
    /// vanilla broadcast - so this engine re-asserts on a short timer (default 8s,
    /// `FactionSpawnReassertSeconds`) for every currently-hubbed connected peer, and immediately on
    /// first detecting a peer as connected, to keep the exposure window short rather than needing to
    /// prevent vanilla's own send at all.
    /// </summary>
    public static class WildcardBFactionSpawnEngine
    {
        private const string StartTempleIconName = "StartTemple";

        private static readonly Dictionary<long, Vector3> _playerHubs = new Dictionary<long, Vector3>();
        private static readonly HashSet<long> _knownPeers = new HashSet<long>();
        private static float _timer;

        /// <summary>Assigns (or replaces) player <paramref name="playerId"/>'s spawn hub. Pushes an immediate correction if they are currently connected.</summary>
        public static void SetPlayerHub(long playerId, Vector3 hubPosition)
        {
            _playerHubs[playerId] = hubPosition;
            PushToPlayerIfConnected(playerId);
        }

        public static void ClearPlayerHub(long playerId) => _playerHubs.Remove(playerId);

        public static void OnUpdate(float dt)
        {
            if (WildcardBConfig.FactionSpawnEnabled?.Value != true || ZoneSystem.instance == null || ZRoutedRpc.instance == null)
            {
                return;
            }

            DetectNewPeers();

            _timer += dt;
            float interval = WildcardBConfig.FactionSpawnReassertSeconds?.Value ?? 8f;
            if (_timer < interval)
            {
                return;
            }
            _timer = 0f;
            ReassertAll();
        }

        private static void DetectNewPeers()
        {
            var seenNow = new HashSet<long>();
            foreach (ConnectedCharacter who in ConnectedCharacters.All())
            {
                long peerUid = who.Peer.m_uid;
                seenNow.Add(peerUid);
                if (_knownPeers.Add(peerUid))
                {
                    // Freshly observed this session - push immediately rather than waiting out the reassert timer.
                    PushToPeer(who);
                }
            }
            _knownPeers.RemoveWhere(id => !seenNow.Contains(id));
        }

        private static void ReassertAll()
        {
            foreach (ConnectedCharacter who in ConnectedCharacters.All())
            {
                PushToPeer(who);
            }
        }

        private static void PushToPlayerIfConnected(long playerId)
        {
            foreach (ConnectedCharacter who in ConnectedCharacters.All())
            {
                if (who.PlayerId == playerId)
                {
                    PushToPeer(who);
                    return;
                }
            }
        }

        private static bool TryResolveHub(long playerId, out Vector3 hub)
        {
            if (_playerHubs.TryGetValue(playerId, out hub))
            {
                return true;
            }
            string defaultHub = WildcardBConfig.FactionSpawnDefaultHub?.Value ?? "";
            return TryParseVector3(defaultHub, out hub);
        }

        private static void PushToPeer(ConnectedCharacter who)
        {
            if (!TryResolveHub(who.PlayerId, out Vector3 hub))
            {
                return; // no hub assigned and no default configured - leave vanilla's own send alone entirely
            }
            if (ZoneSystem.instance == null || ZRoutedRpc.instance == null)
            {
                return;
            }

            try
            {
                var icons = new Dictionary<Vector3, string>();
                ZoneSystem.instance.GetLocationIcons(icons);

                List<Vector3> toRemove = null;
                foreach (KeyValuePair<Vector3, string> kv in icons)
                {
                    if (kv.Value == StartTempleIconName)
                    {
                        (toRemove ??= new List<Vector3>()).Add(kv.Key);
                    }
                }
                if (toRemove != null)
                {
                    foreach (Vector3 key in toRemove)
                    {
                        icons.Remove(key);
                    }
                }
                icons[hub] = StartTempleIconName;

                var pkg = new ZPackage();
                pkg.Write(icons.Count);
                foreach (KeyValuePair<Vector3, string> kv in icons)
                {
                    pkg.Write(kv.Key);
                    pkg.Write(kv.Value);
                }
                ZRoutedRpc.instance.InvokeRoutedRPC(who.Peer.m_uid, "LocationIcons", pkg);
            }
            catch (System.Exception ex)
            {
                PortalDebug.LogWarning($"[WildcardBFactionSpawnEngine] push to {who.Name} failed: {ex.Message}");
            }
        }

        private static bool TryParseVector3(string s, out Vector3 result)
        {
            result = Vector3.zero;
            if (string.IsNullOrWhiteSpace(s))
            {
                return false;
            }
            string[] parts = s.Split(',');
            if (parts.Length != 3) return false;
            if (!float.TryParse(parts[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float x)) return false;
            if (!float.TryParse(parts[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float y)) return false;
            if (!float.TryParse(parts[2].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float z)) return false;
            result = new Vector3(x, y, z);
            return true;
        }
    }
}
