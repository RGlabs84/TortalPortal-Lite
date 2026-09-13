using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using UnityEngine;
using TortalPortalLite.Core;
using TortalPortalLite.Core.Data;

namespace TortalPortalLite.Subsystems.Foundations
{
    public sealed class OpsSnapshotPortalEntry
    {
        /// <summary>Position id ("x:y:z" @0.5m) - snapshots resolve by position, never by ZDOID: ZDO.Load re-mints m_uid every world load (:74552).</summary>
        public string Id = "";
        public float X, Y, Z;
        public float RotY;
        public int Prefab;
        public string Tag = "";
        /// <summary>The connected partner's own position id, or "" if unconnected at snapshot time.</summary>
        public string PartnerId = "";
        public string NetworkId = "";
        public bool Locked;
    }

    public sealed class OpsSnapshotFile
    {
        public int SchemaVersion = 1;
        public string CreatedUtc = "";
        public string WorldName = "";
        public long WorldUid;
        public string SeedName = "";
        public List<OpsSnapshotPortalEntry> Portals = new List<OpsSnapshotPortalEntry>();
    }

    /// <summary>
    /// #78 Network state snapshot, rollback and migration. A snapshot is the census DTO plus a schema
    /// version and world fingerprint (ZNet.instance.GetWorldName() :80876, GetWorldUID() :80871,
    /// ZNet.World.m_seedName :112376), written atomically to
    /// "&lt;config&gt;/snapshots/&lt;name&gt;.json". Taken automatically on every detected networks.json reload
    /// (detected here by the NetworkModel.Networks list's OWN reference changing - NetworkModel replaces
    /// that field wholesale on every successful reload, so a changed reference IS a reload, with no need
    /// to touch that file) and on ZNet.WorldSaveStarted (:78989, invoked at :80524, main-thread, BEFORE
    /// PrepareSave) so the newest snapshot always matches the world file about to hit disk.
    ///
    /// Restore re-resolves every entry by rounded position (never ZDOID) and issues the shared
    /// PortalOwnership.ClaimAndWrite primitive for any tag/connection that differs - the same "reassert
    /// engine's write primitive" catalog #71/#78 both cite. A portal in the snapshot but missing from the
    /// world is reported only, unless --recreate is passed: recreation uses
    /// ZDOMan.instance.CreateNewZDO(pos, portalPrefabHash) (:76674-76692, which internally calls
    /// AddIfPortal with the REAL prefab hash immediately, filing it into m_portalObjects correctly) then
    /// the five ZNetView.Awake fabrication lines a plain CreateNewZDO does NOT set on its own
    /// (:82613-82620): Persistent, Type, Distant, SetPrefab, SetRotation.
    /// </summary>
    public static class OpsOutputSnapshotEngine
    {
        private static IReadOnlyList<NetworkDefinition>? _lastNetworksRef;
        private static bool _worldSaveHookInstalled;

        public static void Initialize()
        {
            try
            {
                ZNet.WorldSaveStarted = (Action)Delegate.Combine(ZNet.WorldSaveStarted, new Action(OnWorldSaveStarted));
                _worldSaveHookInstalled = true;
                PortalDebug.LogAlways("[OpsOutputSnapshotEngine] subscribed to ZNet.WorldSaveStarted for pre-save auto-snapshots.");
            }
            catch (Exception ex)
            {
                _worldSaveHookInstalled = false;
                PortalDebug.LogError($"[OpsOutputSnapshotEngine] failed to subscribe to ZNet.WorldSaveStarted: {ex.GetType().Name}: {ex.Message}");
            }
        }

        public static void Shutdown()
        {
            if (_worldSaveHookInstalled)
            {
                try
                {
                    ZNet.WorldSaveStarted = (Action)Delegate.Remove(ZNet.WorldSaveStarted, new Action(OnWorldSaveStarted));
                }
                catch { /* best-effort unhook on shutdown */ }
            }
        }

        public static void OnUpdate(float dt)
        {
            IReadOnlyList<NetworkDefinition> current = NetworkModel.Networks;
            if (!ReferenceEquals(current, _lastNetworksRef))
            {
                bool firstObservation = _lastNetworksRef == null;
                _lastNetworksRef = current;
                if (!firstObservation)
                {
                    TakeSnapshot("networks-reload-" + FileStampNow());
                }
            }
        }

        private static void OnWorldSaveStarted()
        {
            try
            {
                TakeSnapshot("pre-save-" + FileStampNow());
            }
            catch (Exception ex)
            {
                PortalDebug.LogError($"[OpsOutputSnapshotEngine] pre-save snapshot failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static string FileStampNow() => DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'");
        private static string SnapshotDir => Path.Combine(OpsOutputConfig.PluginConfigDir, OpsOutputConfig.SnapshotDirName?.Value ?? "snapshots");

        public static string TakeSnapshot(string? name = null)
        {
            if (ZDOMan.instance == null)
            {
                return "tpl: ZDOMan not ready, snapshot skipped.";
            }
            try
            {
                string safeName = SanitizeName(name ?? ("manual-" + FileStampNow()));
                var byUid = new Dictionary<ZDOID, PortalRecord>();
                foreach (PortalRecord r in PortalCensus.Latest) byUid[r.Uid] = r;

                var file = new OpsSnapshotFile
                {
                    CreatedUtc = DateTime.UtcNow.ToString("o"),
                    WorldName = ZNet.instance != null ? ZNet.instance.GetWorldName() : "",
                    WorldUid = ZNet.instance != null ? ZNet.instance.GetWorldUID() : 0L,
                    SeedName = ZNet.World?.m_seedName ?? "",
                };

                foreach (PortalRecord record in PortalCensus.Latest)
                {
                    ZDO? zdo = ZDOMan.instance.GetZDO(record.Uid);
                    string partnerId = "";
                    if (record.Connection != ZDOID.None && byUid.TryGetValue(record.Connection, out PortalRecord partner))
                    {
                        partnerId = OpsOutputReportModel.PositionId(partner.Position);
                    }
                    file.Portals.Add(new OpsSnapshotPortalEntry
                    {
                        Id = OpsOutputReportModel.PositionId(record.Position),
                        X = record.Position.x,
                        Y = record.Position.y,
                        Z = record.Position.z,
                        RotY = record.Rotation.eulerAngles.y,
                        Prefab = record.Prefab,
                        Tag = record.Tag,
                        PartnerId = partnerId,
                        NetworkId = zdo != null ? PortalRecordStore.GetNetworkId(zdo) : "",
                        Locked = zdo != null && PortalRecordStore.IsLocked(zdo),
                    });
                }

                string json = JsonConvert.SerializeObject(file, Formatting.Indented);
                string path = Path.Combine(SnapshotDir, safeName + ".json");
                if (!OpsOutputAtomicFile.TryWriteAllText(path, json, out string? error))
                {
                    return $"tpl: snapshot failed: {error}";
                }
                PruneOldAutoSnapshots();
                PortalDebug.LogAlways($"[OpsOutputSnapshotEngine] snapshot '{safeName}' written ({file.Portals.Count} portal(s)).");
                return $"tpl: snapshot '{safeName}' saved ({file.Portals.Count} portal(s)).";
            }
            catch (Exception ex)
            {
                PortalDebug.LogError($"[OpsOutputSnapshotEngine] TakeSnapshot failed: {ex.GetType().Name}: {ex.Message}");
                return $"tpl: snapshot failed: {ex.GetType().Name}: {ex.Message}";
            }
        }

        private static void PruneOldAutoSnapshots()
        {
            try
            {
                int retain = OpsOutputConfig.SnapshotRetain?.Value ?? 30;
                if (!Directory.Exists(SnapshotDir))
                {
                    return;
                }
                var autoFiles = Directory.GetFiles(SnapshotDir, "*.json")
                    .Where(p => Path.GetFileName(p).StartsWith("pre-save-") || Path.GetFileName(p).StartsWith("networks-reload-") || Path.GetFileName(p).StartsWith("pre-repair-"))
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .ToList();
                foreach (string stale in autoFiles.Skip(retain))
                {
                    try { File.Delete(stale); } catch { /* best-effort */ }
                }
            }
            catch (Exception ex)
            {
                PortalDebug.LogWarning($"[OpsOutputSnapshotEngine] prune failed: {ex.Message}");
            }
        }

        public static string[] ListSnapshots()
        {
            try
            {
                if (!Directory.Exists(SnapshotDir))
                {
                    return Array.Empty<string>();
                }
                return Directory.GetFiles(SnapshotDir, "*.json")
                    .Select(Path.GetFileNameWithoutExtension)
                    .OrderByDescending(n => n)
                    .ToArray();
            }
            catch
            {
                return Array.Empty<string>();
            }
        }

        public static string Restore(string name, bool recreate)
        {
            if (ZDOMan.instance == null)
            {
                return "tpl: ZDOMan not ready.";
            }
            string path = Path.Combine(SnapshotDir, SanitizeName(name) + ".json");
            if (!File.Exists(path))
            {
                return $"tpl: no snapshot named '{name}' (looked in '{SnapshotDir}').";
            }

            try
            {
                var file = JsonConvert.DeserializeObject<OpsSnapshotFile>(File.ReadAllText(path));
                if (file == null)
                {
                    return "tpl: snapshot file failed to parse.";
                }

                if (ZNet.instance != null && file.WorldUid != 0 && file.WorldUid != ZNet.instance.GetWorldUID())
                {
                    return $"tpl: snapshot was taken on a different world (uid {file.WorldUid} vs current {ZNet.instance.GetWorldUID()}) - refusing (position keys would resolve to unrelated portals). Not yet supporting --force.";
                }

                int restored = 0, missing = 0, recreated = 0;
                // Two passes: first ensure every entry resolves to a live portal (recreating if asked),
                // then fix up tag/connection now that every id-to-Uid mapping is known.
                var idToUid = new Dictionary<string, ZDOID>();
                foreach (PortalRecord r in PortalCensus.Latest)
                {
                    idToUid[OpsOutputReportModel.PositionId(r.Position)] = r.Uid;
                }

                foreach (OpsSnapshotPortalEntry entry in file.Portals)
                {
                    if (!idToUid.ContainsKey(entry.Id))
                    {
                        if (!recreate)
                        {
                            missing++;
                            continue;
                        }
                        ZDO created = ZDOMan.instance.CreateNewZDO(new Vector3(entry.X, entry.Y, entry.Z), entry.Prefab);
                        created.Persistent = true;
                        created.Type = ZDO.ObjectType.Default;
                        created.Distant = false;
                        created.SetPrefab(entry.Prefab);
                        created.SetRotation(Quaternion.Euler(0f, entry.RotY, 0f));
                        idToUid[entry.Id] = created.m_uid;
                        recreated++;
                    }
                }

                foreach (OpsSnapshotPortalEntry entry in file.Portals)
                {
                    if (!idToUid.TryGetValue(entry.Id, out ZDOID uid))
                    {
                        continue;
                    }
                    ZDO? zdo = ZDOMan.instance.GetZDO(uid);
                    if (zdo == null || !zdo.IsValid())
                    {
                        continue;
                    }

                    ZDOID targetUid = ZDOID.None;
                    if (!string.IsNullOrEmpty(entry.PartnerId) && idToUid.TryGetValue(entry.PartnerId, out ZDOID partnerUid))
                    {
                        targetUid = partnerUid;
                    }

                    string currentTag = zdo.GetString(ZDOVars.s_tag, "");
                    ZDOID currentConn = zdo.GetConnectionZDOID(ZDOExtraData.ConnectionType.Portal);
                    if (currentTag == entry.Tag && currentConn == targetUid)
                    {
                        continue; // already matches - no write needed
                    }

                    PortalOwnership.ClaimAndWrite(zdo, z =>
                    {
                        z.Set(ZDOVars.s_tag, entry.Tag);
                        z.SetConnection(ZDOExtraData.ConnectionType.Portal, targetUid);
                    });
                    if (!string.IsNullOrEmpty(entry.NetworkId))
                    {
                        PortalRecordStore.SetNetworkId(zdo, entry.NetworkId);
                    }
                    if (entry.Locked)
                    {
                        PortalRecordStore.SetLocked(zdo, true);
                    }
                    restored++;
                }

                if (recreated > 0)
                {
                    ZDOMan.instance.SetDirtyPortals();
                }

                return $"tpl: restore '{name}': {restored} portal(s) rewritten, {missing} missing (not recreated), {recreated} recreated.";
            }
            catch (Exception ex)
            {
                PortalDebug.LogError($"[OpsOutputSnapshotEngine] restore failed: {ex.GetType().Name}: {ex.Message}");
                return $"tpl: restore failed: {ex.GetType().Name}: {ex.Message}";
            }
        }

        private static string SanitizeName(string name)
        {
            foreach (char c in Path.GetInvalidFileNameChars())
            {
                name = name.Replace(c, '_');
            }
            return name;
        }
    }
}
