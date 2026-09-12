using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using UnityEngine;
using TortalPortalLite.Core;

namespace TortalPortalLite.Subsystems.Enforcement
{
    public enum AccessLockLevel { None = 0, Retag = 1, Relink = 2, Full = 3 }

    /// <summary>
    /// One portal's ACL record (#53 Portal ACL). Keyed by rounded position (same 0.5m grid
    /// PortalCensus.Round uses, duplicated here since Foundations does not expose it) - never by
    /// ZDOID, which regenerates every world load (see PortalRecordStore's own doc comment on RecordId
    /// for the same reasoning, repeated throughout the catalog's own citations).
    ///
    /// Fields that already have a real ZDO key in PortalKeys.cs (owner, network, the boolean
    /// locked/unlocked flag) are NOT duplicated here - PortalRecordStore is authoritative for those and
    /// this store only mirrors what it does not have room for: co-owners, LOCK GRANULARITY (vanilla-
    /// facing PortalRecordStore.RecordLocked is a bare bool; this store's finer none/retag/relink/full
    /// still keeps PortalRecordStore.SetLocked(zdo, level != None) mirrored so anything reading the
    /// coarse flag - e.g. NetworkReassertEngine's own "RecordLocked... any client-authored change is
    /// reverted" contract - still sees the correct coarse state), the last approved tag/partner (the
    /// Tag Watchdog's reference state), and abuse counters.
    ///
    /// NEEDS NEW KEY: per-portal co-owner list and lock-level as real ZDO fields, purpose: survive even
    /// if access_acl.json is lost/corrupted, and be visible to the same "one ZDO key namespace" schema
    /// PortalKeys.cs centralises for every other engine. Left as a file-backed store instead, per this
    /// wave's explicit instruction not to invent new keys mid-wave.
    /// </summary>
    public sealed class AccessAclEntry
    {
        public float X;
        public float Y;
        public float Z;

        public List<string> CoOwners = new List<string>();
        public int LockLevel; // AccessLockLevel, stored as int for JsonUtility/Newtonsoft simplicity
        public string ApprovedTag = "";
        public bool HasApprovedPartner;
        public float PartnerX;
        public float PartnerY;
        public float PartnerZ;
        public long ClaimedAtTicks;
        public long LastRetagAtTicks;
        public int RetagCount;

        [JsonIgnore]
        public Vector3 Position => new Vector3(X, Y, Z);

        [JsonIgnore]
        public Vector3 ApprovedPartnerPosition => new Vector3(PartnerX, PartnerY, PartnerZ);

        public AccessLockLevel Lock
        {
            get => (AccessLockLevel)LockLevel;
            set => LockLevel = (int)value;
        }
    }

    internal sealed class AccessAclFile
    {
        public int SchemaVersion = 1;
        public List<AccessAclEntry> Entries = new List<AccessAclEntry>();
    }

    /// <summary>
    /// #53 Portal ACL's storage half. In-memory dictionary keyed by rounded position, periodically
    /// flushed to disk (access_acl.json, alongside Foundations' own networks.json) rather than on every
    /// write - most mutations here (retag counters, drift checks) happen on the Tag Watchdog's own fast
    /// tick and would otherwise thrash disk under an active attack.
    /// </summary>
    public static class AccessAclStore
    {
        private static readonly Dictionary<Vector3, AccessAclEntry> _byPosition = new Dictionary<Vector3, AccessAclEntry>();
        private static bool _dirty;
        private static float _flushTimer;
        private static bool _loaded;

        public static Vector3 RoundPos(Vector3 v) =>
            new Vector3(Mathf.Round(v.x * 2f) / 2f, Mathf.Round(v.y * 2f) / 2f, Mathf.Round(v.z * 2f) / 2f);

        public static void Initialize()
        {
            Load();
        }

        public static void OnUpdate(float dt)
        {
            _flushTimer += dt;
            float interval = AccessConfig.StoreFlushSeconds?.Value ?? 10f;
            if (_flushTimer < interval)
            {
                return;
            }
            _flushTimer = 0f;
            if (_dirty)
            {
                Save();
            }
        }

        public static bool TryGet(Vector3 pos, out AccessAclEntry entry) => _byPosition.TryGetValue(RoundPos(pos), out entry);

        public static AccessAclEntry GetOrCreate(Vector3 pos)
        {
            Vector3 key = RoundPos(pos);
            if (!_byPosition.TryGetValue(key, out AccessAclEntry entry))
            {
                entry = new AccessAclEntry { X = key.x, Y = key.y, Z = key.z };
                _byPosition[key] = entry;
                MarkDirty();
            }
            return entry;
        }

        public static IEnumerable<AccessAclEntry> All() => _byPosition.Values;

        public static void MarkDirty() => _dirty = true;

        private static string FilePath()
        {
            string configPath = AccessConfig.AclFile?.ConfigFile?.ConfigFilePath ?? "";
            string dir = Path.GetDirectoryName(configPath) ?? ".";
            return Path.Combine(dir, AccessConfig.AclFile?.Value ?? "access_acl.json");
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
                var parsed = JsonConvert.DeserializeObject<AccessAclFile>(json);
                if (parsed?.Entries == null)
                {
                    return;
                }
                foreach (AccessAclEntry entry in parsed.Entries)
                {
                    _byPosition[new Vector3(entry.X, entry.Y, entry.Z)] = entry;
                }
                PortalDebug.LogAlways($"[AccessAclStore] loaded {_byPosition.Count} ACL record(s) from '{path}'.");
            }
            catch (Exception ex)
            {
                PortalDebug.LogError($"[AccessAclStore] failed to load: {ex.GetType().Name}: {ex.Message}");
            }
        }

        public static void Save()
        {
            try
            {
                string path = FilePath();
                var file = new AccessAclFile { Entries = new List<AccessAclEntry>(_byPosition.Values) };
                File.WriteAllText(path, JsonConvert.SerializeObject(file, Formatting.Indented));
                _dirty = false;
            }
            catch (Exception ex)
            {
                PortalDebug.LogError($"[AccessAclStore] failed to save: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }
}
