using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using TortalPortalLite.Core;

namespace TortalPortalLite.Subsystems.WorldOps
{
    internal sealed class EconomyStateFile
    {
        public int SchemaVersion = 1;
        public Dictionary<string, long> Longs = new Dictionary<string, long>();
        public Dictionary<string, float> Floats = new Dictionary<string, float>();
    }

    /// <summary>
    /// A small, generic, position/name-keyed persisted key-value store for per-portal economy state that
    /// would otherwise need a brand-new ZDO key (cooldown-until timestamps, quota window starts, lease
    /// expiry, auction cycle state, ...). Mirrors Subsystems/Enforcement/AccessAclStore.cs's own precedent
    /// exactly, generalised to arbitrary string keys rather than one fixed record shape - that file's own
    /// doc comment states the reasoning this class exists to reuse: "NEEDS NEW KEY... left as a
    /// file-backed store instead, per this wave's explicit instruction not to invent new keys mid-wave."
    /// Several economy engines need exactly this shape of state (a numeric value that must survive a
    /// restart, keyed by something more stable than a ZDOID), so one shared store is used instead of
    /// each engine hand-rolling its own JSON file.
    ///
    /// // NEEDS NEW KEY: tpl_worldops_cooldownuntil (long), tpl_worldops_leaseuntil (long),
    /// // tpl_worldops_quotawindowstart (long)/tpl_worldops_quotahits (int) - purpose: catalog #96's own
    /// // recommended design stores these directly on the portal ZDO (it saves wholesale via ChunkPortal
    /// // with no Persistent filter, so a ZDO field survives automatically with no separate flush). This
    /// // file-backed store is the safe substitute for this wave; if these keys are added to
    /// // Core/Data/PortalKeys.cs in a future wave, this store should be retired in favour of them.
    ///
    /// Callers key entries by a caller-chosen string (convention: "{mechanism}:{roundedX},{roundedY},{roundedZ}"
    /// or "{mechanism}:{networkKey}") - never a raw ZDOID, for the same reasons AccessAclStore keys by
    /// rounded position rather than ZDOID (ZDO.Load re-mints m_uid at every world load).
    /// </summary>
    public static class EconomyStateStore
    {
        private static readonly Dictionary<string, long> _longs = new Dictionary<string, long>();
        private static readonly Dictionary<string, float> _floats = new Dictionary<string, float>();
        private static bool _loaded;
        private static bool _dirty;
        private static float _flushTimer;

        public static void Initialize() => Load();

        public static void OnUpdate(float dt)
        {
            _flushTimer += dt;
            if (_flushTimer < 15f)
            {
                return;
            }
            _flushTimer = 0f;
            if (_dirty)
            {
                Save();
            }
        }

        public static long GetLong(string key, long def = 0L) => _longs.TryGetValue(key, out long v) ? v : def;
        public static void SetLong(string key, long value) { _longs[key] = value; _dirty = true; }
        public static void RemoveLong(string key) { if (_longs.Remove(key)) _dirty = true; }

        public static float GetFloat(string key, float def = 0f) => _floats.TryGetValue(key, out float v) ? v : def;
        public static void SetFloat(string key, float value) { _floats[key] = value; _dirty = true; }
        public static void RemoveFloat(string key) { if (_floats.Remove(key)) _dirty = true; }

        public static string PositionKey(string mechanism, UnityEngine.Vector3 pos) =>
            $"{mechanism}:{UnityEngine.Mathf.Round(pos.x * 2f) / 2f},{UnityEngine.Mathf.Round(pos.y * 2f) / 2f},{UnityEngine.Mathf.Round(pos.z * 2f) / 2f}";

        private static string FilePath()
        {
            string configPath = EconomyConfig.RegistryFile?.ConfigFile?.ConfigFilePath ?? "";
            string dir = Path.GetDirectoryName(configPath) ?? ".";
            return Path.Combine(dir, "economy_state.json");
        }

        private static void Load()
        {
            if (_loaded)
            {
                return;
            }
            _loaded = true;
            try
            {
                string path = FilePath();
                if (!File.Exists(path))
                {
                    return;
                }
                string json = File.ReadAllText(path);
                EconomyStateFile? parsed = JsonConvert.DeserializeObject<EconomyStateFile>(json);
                if (parsed == null)
                {
                    return;
                }
                if (parsed.Longs != null)
                {
                    foreach (KeyValuePair<string, long> kvp in parsed.Longs) _longs[kvp.Key] = kvp.Value;
                }
                if (parsed.Floats != null)
                {
                    foreach (KeyValuePair<string, float> kvp in parsed.Floats) _floats[kvp.Key] = kvp.Value;
                }
                PortalDebug.LogAlways($"[EconomyStateStore] loaded {_longs.Count} long/{_floats.Count} float record(s).");
            }
            catch (Exception ex)
            {
                PortalDebug.LogError($"[EconomyStateStore] failed to load economy_state.json: {ex.GetType().Name}: {ex.Message}");
            }
        }

        public static void Save()
        {
            try
            {
                var file = new EconomyStateFile { Longs = new Dictionary<string, long>(_longs), Floats = new Dictionary<string, float>(_floats) };
                File.WriteAllText(FilePath(), JsonConvert.SerializeObject(file, Formatting.Indented));
                _dirty = false;
            }
            catch (Exception ex)
            {
                PortalDebug.LogError($"[EconomyStateStore] failed to save economy_state.json: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }
}
