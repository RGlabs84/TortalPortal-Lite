using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using UnityEngine;
using TortalPortalLite.Core;
using TortalPortalLite.Core.Data;

namespace TortalPortalLite.Subsystems.Enforcement
{
    /// <summary>One admin-declared fabricated location pad.</summary>
    public sealed class LockdownLocationPadEntry
    {
        public float X, Y, Z;
        public float RotY;
        public string LocationName = "";
        public int Seed;
        [JsonIgnore] public Vector3 Position => new Vector3(X, Y, Z);
    }

    public sealed class LockdownLocationPadFile
    {
        public int SchemaVersion = 1;
        public List<LockdownLocationPadEntry> Pads = new List<LockdownLocationPadEntry>();
    }

    /// <summary>
    /// #251 Fabricated Location Pad. Mints a `ZoneSystem.m_locationProxyPrefab` ZDO server-side so every
    /// vanilla client materialises a location's non-networked parts (EffectAreas: NoMonsters, PlayerBase,
    /// WarmCozyArea, Teleport) around a hub - a spawn-free, base-flagged, comfort-bearing, and possibly
    /// build-unlocking pad with zero client mod.
    ///
    /// Recipe follows `LocationProxy.SetLocation`/`ZoneSystem.CreateLocationProxy`'s own field set exactly
    /// (SERVER decompile citations in the catalog entry): Persistent=true, SetPrefab(proxyHash),
    /// SetRotation(rot), ZDOVars.s_location = locationName hash, ZDOVars.s_seed = seed. Written through
    /// PortalOwnership.ClaimAndWrite for this mod's own ownership/force-send discipline, same as every
    /// other ZDO-minting engine (TargetedPhantomPortalFactory's own creation recipe, which this mirrors
    /// for a location-proxy prefab instead of a portal prefab).
    ///
    /// Site safety is validated through this domain's own #123 Destination Safety Validator before a pad
    /// is ever created (its own listed prerequisite), and #250's audit output is consulted only for an
    /// informational log line about which declared pads happen to carry a Teleport-flagged area.
    /// </summary>
    public static class LockdownLocationPad
    {
        private const float PollInterval = 5f;
        private static float _timer;
        private static DateTime _fileStamp = DateTime.MinValue;
        private static List<LockdownLocationPadEntry> _declared = new List<LockdownLocationPadEntry>();
        private static readonly Dictionary<Vector3, ZDOID> _created = new Dictionary<Vector3, ZDOID>();

        public static void OnUpdate(float dt)
        {
            _timer += dt;
            if (_timer < PollInterval)
            {
                return;
            }
            _timer = 0f;
            TryReloadFile();
            Reconcile();
        }

        private static string FilePath()
        {
            string name = LockdownConfig.LocationPadFile?.Value ?? "lockdown_location_pads.json";
            return Path.Combine(BepInEx.Paths.ConfigPath, name);
        }

        private static void TryReloadFile()
        {
            try
            {
                string path = FilePath();
                if (!File.Exists(path))
                {
                    return;
                }
                DateTime stamp = File.GetLastWriteTimeUtc(path);
                if (stamp == _fileStamp)
                {
                    return;
                }
                var parsed = JsonConvert.DeserializeObject<LockdownLocationPadFile>(File.ReadAllText(path));
                if (parsed?.Pads == null)
                {
                    return;
                }
                _declared = parsed.Pads;
                _fileStamp = stamp;
                PortalDebug.LogAlways($"[LockdownLocationPad] loaded {_declared.Count} pad declaration(s) from '{path}'.");
            }
            catch (Exception ex)
            {
                PortalDebug.LogError($"[LockdownLocationPad] failed to load pad file: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static void Reconcile()
        {
            if (ZoneSystem.instance == null || ZDOMan.instance == null || ZoneSystem.instance.m_locationProxyPrefab == null)
            {
                return;
            }

            var declaredPositions = new HashSet<Vector3>();
            foreach (LockdownLocationPadEntry pad in _declared)
            {
                Vector3 key = Round(pad.Position);
                declaredPositions.Add(key);
                if (_created.ContainsKey(key))
                {
                    continue; // Already minted.
                }
                EnsurePad(pad, key);
            }

            // Remove pads whose declaration was deleted from the file.
            var toRemove = new List<Vector3>();
            foreach (var kvp in _created)
            {
                if (!declaredPositions.Contains(kvp.Key))
                {
                    toRemove.Add(kvp.Key);
                }
            }
            foreach (Vector3 key in toRemove)
            {
                DestroyPad(_created[key]);
                _created.Remove(key);
            }
        }

        private static void EnsurePad(LockdownLocationPadEntry pad, Vector3 roundedKey)
        {
            LockdownDestinationSafetyValidator.Result validation = LockdownDestinationSafetyValidator.ValidatePoint(pad.Position);
            if (!validation.Ok)
            {
                PortalDebug.LogWarning($"[LockdownLocationPad] refused pad '{pad.LocationName}' at {pad.Position:F0}: {validation.Reason}");
                return;
            }

            int proxyHash = ZoneSystem.instance.m_locationProxyPrefab.name.GetStableHashCode();
            try
            {
                ZDO zdo = ZDOMan.instance.CreateNewZDO(pad.Position, proxyHash);
                if (zdo == null)
                {
                    return;
                }
                int seed = pad.Seed != 0 ? pad.Seed : UnityEngine.Random.Range(0, int.MaxValue);
                Quaternion rot = Quaternion.Euler(0f, pad.RotY, 0f);
                PortalOwnership.ClaimAndWrite(zdo, z =>
                {
                    z.Persistent = true;
                    z.SetPrefab(proxyHash);
                    z.SetRotation(rot);
                    z.Set(ZDOVars.s_location, pad.LocationName.GetStableHashCode());
                    z.Set(ZDOVars.s_seed, seed);
                });
                _created[roundedKey] = zdo.m_uid;

                bool carriesTeleport = LockdownPlacementGateAudit.AnyCarriesTeleportArea(out string teleportPrefab);
                PortalDebug.LogAlways($"[LockdownLocationPad] minted '{pad.LocationName}' pad at {pad.Position:F0}." + (carriesTeleport ? $" (a Teleport-flagged prefab '{teleportPrefab}' exists on this server, per the placement gate audit.)" : ""));
            }
            catch (Exception ex)
            {
                PortalDebug.LogError($"[LockdownLocationPad] failed to mint pad '{pad.LocationName}' at {pad.Position:F0}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static void DestroyPad(ZDOID id)
        {
            if (ZDOMan.instance == null)
            {
                return;
            }
            ZDO zdo = ZDOMan.instance.GetZDO(id);
            if (zdo == null || !zdo.IsValid())
            {
                return;
            }
            try
            {
                zdo.SetOwner(ZDOMan.GetSessionID());
                ZDOMan.instance.DestroyZDO(zdo);
                PortalDebug.LogAlways($"[LockdownLocationPad] removed pad {id} (declaration deleted).");
            }
            catch (Exception ex)
            {
                PortalDebug.LogWarning($"[LockdownLocationPad] failed to remove pad {id}: {ex.Message}");
            }
        }

        private static Vector3 Round(Vector3 v) => new Vector3(Mathf.Round(v.x * 2f) / 2f, Mathf.Round(v.y * 2f) / 2f, Mathf.Round(v.z * 2f) / 2f);
    }
}
