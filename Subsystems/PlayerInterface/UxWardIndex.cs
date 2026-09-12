using System.Collections.Generic;
using UnityEngine;
using TortalPortalLite.Core;
using TortalPortalLite.Core.Data;
using TortalPortalLite.Core.Hooks;
using TortalPortalLite.Subsystems.Topology;

namespace TortalPortalLite.Subsystems.PlayerInterface
{
    /// <summary>
    /// #167 Ward toggle as a switch, and ward data as the permission source. The RPC path is dead server
    /// side (`PrivateArea.RPC_ToggleEnabled`/`RPC_TogglePermitted` are handled locally by the owning
    /// client - same `targetPeerID == owner` / no `ZNetScene.FindInstance` story as every other portal
    /// RPC), so this reads the ZDO fields directly: `ZDOVars.s_enabled` for the on/off bit,
    /// `ZDOVars.s_permitted` (a COUNT) plus the raw runtime-concatenated keys `"pu_id"+i`/`"pu_name"+i`
    /// (no ZDOVars constant exists for these - PrivateArea.SetPermittedPlayers/GetPermittedPlayers build
    /// them by string concatenation) for the permitted list, and `ZDOVars.s_creator` for the builder.
    ///
    /// Never calls `PrivateArea.CheckAccess` - its backing `m_allAreas` static list is populated only in
    /// `PrivateArea.Awake`, which never runs server-side (no live component), so it is permanently empty
    /// and the method is a silent allow-everything, not a working permission check (the catalog's own
    /// explicit warning).
    ///
    /// Ward discovery reuses Topology's `TargetedPrefabDiscovery.PrivateAreaHashes` (component-based
    /// prefab classification, already built in Wave 1) rather than re-scanning `ZNetScene.m_prefabs` a
    /// second time.
    /// </summary>
    public static class UxWardIndex
    {
        private static ZdoSpatialQuery.PrefabSetSweeper? _sweeper;
        private static readonly HashSet<ZDOID> _wardIds = new HashSet<ZDOID>();
        private static readonly Dictionary<ZDOID, bool> _lastEnabled = new Dictionary<ZDOID, bool>();
        private static readonly Dictionary<ZDOID, (bool state, float clock)> _lastToggle = new Dictionary<ZDOID, (bool, float)>();
        private static readonly Dictionary<ZDOID, float> _tempUnlockUntilClock = new Dictionary<ZDOID, float>();
        private static readonly Dictionary<ZDOID, (ZNetPeer sender, float clock)> _recentWrite = new Dictionary<ZDOID, (ZNetPeer, float)>();

        private static float _clock;
        private static float _timer;

        public static void Initialize()
        {
            RpcZdoDataHook.RegisterPostfix(50, OnZdoDataFromClient);
        }

        public static void OnUpdate(float dt)
        {
            _clock += dt;
            if (UxConfig.Enabled?.Value == false || UxConfig.WardIndexEnabled?.Value == false)
            {
                return;
            }
            SweepDiscovery();

            _timer += dt;
            float interval = UxConfig.WardPollSeconds?.Value ?? 0.5f;
            if (_timer < interval)
            {
                return;
            }
            _timer = 0f;
            PollWards();
        }

        private static void SweepDiscovery()
        {
            if (TargetedPrefabDiscovery.PrivateAreaHashes.Count == 0 || ZDOMan.instance == null)
            {
                return;
            }
            _sweeper ??= new ZdoSpatialQuery.PrefabSetSweeper(TargetedPrefabDiscovery.PrivateAreaHashes);
            var results = new List<ZDO>();
            _sweeper.Advance(20, results);
            foreach (ZDO z in results)
            {
                if (z != null && z.IsValid())
                {
                    _wardIds.Add(z.m_uid);
                }
            }
        }

        private static void OnZdoDataFromClient(ZNetPeer? sender, ZDOID zdoid)
        {
            if (sender != null && _wardIds.Contains(zdoid))
            {
                _recentWrite[zdoid] = (sender, _clock);
            }
        }

        private static void PollWards()
        {
            if (ZDOMan.instance == null)
            {
                return;
            }
            float window = UxConfig.WardDoubleToggleWindowSeconds?.Value ?? 5f;
            float unlockSeconds = UxConfig.WardUnlockSeconds?.Value ?? 60f;

            foreach (ZDOID id in _wardIds)
            {
                ZDO zdo = ZDOMan.instance.GetZDO(id);
                if (zdo == null || !zdo.IsValid())
                {
                    continue;
                }
                bool enabled = zdo.GetBool(ZDOVars.s_enabled, false);
                bool changed = _lastEnabled.TryGetValue(id, out bool last) && last != enabled;
                _lastEnabled[id] = enabled;
                if (!changed)
                {
                    continue;
                }

                bool isDoubleToggle = _lastToggle.TryGetValue(id, out var prev) && (_clock - prev.clock) <= window && prev.state != enabled;
                _lastToggle[id] = (enabled, _clock);
                if (!isDoubleToggle)
                {
                    continue;
                }

                _tempUnlockUntilClock[id] = _clock + unlockSeconds;
                if (_recentWrite.TryGetValue(id, out var w) && (_clock - w.clock) <= 3f)
                {
                    ConnectedCharacter? who = ResolveByPeer(w.sender);
                    if (who.HasValue)
                    {
                        UxFeedback.Toast(who.Value, $"Portal network unlocked for {unlockSeconds:0}s.");
                    }
                }
            }
        }

        private static ConnectedCharacter? ResolveByPeer(ZNetPeer peer)
        {
            foreach (ConnectedCharacter cc in ConnectedCharacters.All())
            {
                if (cc.Peer == peer)
                {
                    return cc;
                }
            }
            return null;
        }

        public static bool IsTemporarilyUnlocked(ZDOID wardId) => _tempUnlockUntilClock.TryGetValue(wardId, out float until) && _clock < until;

        public static bool TryFindWardNear(Vector3 pos, out ZDO ward)
        {
            ward = null;
            if (ZDOMan.instance == null)
            {
                return false;
            }
            float radius = UxConfig.WardDefaultRadius?.Value ?? 10f;
            float bestSqr = radius * radius;
            foreach (ZDOID id in _wardIds)
            {
                ZDO z = ZDOMan.instance.GetZDO(id);
                if (z == null || !z.IsValid())
                {
                    continue;
                }
                float d = (z.GetPosition() - pos).sqrMagnitude;
                if (d <= bestSqr)
                {
                    bestSqr = d;
                    ward = z;
                }
            }
            return ward != null;
        }

        public static bool IsPermitted(ZDO ward, long playerId)
        {
            int count = ward.GetInt(ZDOVars.s_permitted, 0);
            for (int i = 0; i < count; i++)
            {
                if (ward.GetLong("pu_id" + i, 0L) == playerId)
                {
                    return true;
                }
            }
            return false;
        }

        public static long GetCreator(ZDO ward) => ward.GetLong(ZDOVars.s_creator, 0L);

        /// <summary>Consulted by UxDialAction before letting a player's Dial command change a warded portal. Not warded at all, temporarily unlocked, the ward's own creator, or on its permitted list all pass.</summary>
        public static bool CanDial(Vector3 portalPos, long requesterPlayerId, out string denyReason)
        {
            denyReason = "";
            if (UxConfig.WardIndexEnabled?.Value == false)
            {
                return true;
            }
            if (!TryFindWardNear(portalPos, out ZDO ward))
            {
                return true;
            }
            if (IsTemporarilyUnlocked(ward.m_uid))
            {
                return true;
            }
            long creator = GetCreator(ward);
            if (creator != 0L && creator == requesterPlayerId)
            {
                return true;
            }
            if (IsPermitted(ward, requesterPlayerId))
            {
                return true;
            }
            denyReason = "warded";
            return false;
        }
    }
}
