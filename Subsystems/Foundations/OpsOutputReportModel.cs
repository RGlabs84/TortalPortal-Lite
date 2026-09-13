using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using UnityEngine;
using TortalPortalLite.Core;
using TortalPortalLite.Core.Data;

namespace TortalPortalLite.Subsystems.Foundations
{
    /// <summary>
    /// #65 Full-realm portal enumeration report - one DTO, rendered N ways by the export/HTTP/Discord/map
    /// engines below. Every field's source is exactly what #65's howItWorks specifies; nothing here is
    /// re-derived independently by a consumer engine, so a fix here fixes every output surface at once.
    /// </summary>
    public sealed class OpsPortalReport
    {
        /// <summary>Rounded-position key "x:y:z" at 0.5m - durable across restarts. NEVER the session ZDOID.</summary>
        public string Id = "";
        /// <summary>zdo.m_uid.ToString() - "userID:ID". Session-scoped only; never persist and key off this.</summary>
        public string SessionZdoId = "";
        public string Prefab = "";
        public string Tag = "";
        public string TagAuthor = "";
        /// <summary>Always false - TeleportWorld.RPC_SetTag (SERVER decompile :143587-143602) writes the client-supplied authorId verbatim with no cross-check.</summary>
        public bool TagAuthorTrusted = false;
        public float X, Y, Z;
        public float RotY;
        public string Biome = "";
        public int ZoneX, ZoneY;
        /// <summary>Character.InInterior(pos) i.e. pos.y > 3000f (:4538-4541) - a portal built inside a dungeon.</summary>
        public bool Interior;
        public bool Connected;
        public string PartnerId = "";
        public string PartnerTag = "";
        public float PartnerX, PartnerY, PartnerZ;
        /// <summary>Does the partner point back at us.</summary>
        public bool Reciprocal;
        public float Distance;
        /// <summary>0 = unowned, matches ZDOMan.GetSessionID() = server-owned, else a live peer's m_uid (may be stale for an offline player - see OwnerKind).</summary>
        public long OwnerPeer;
        /// <summary>"unowned" | "server" | a connected player's display name | "peer:&lt;uid&gt;" (owner peer currently offline).</summary>
        public string OwnerKind = "";
        /// <summary>s_creator joined through World.m_playerHistory via s_creatorIndex - "unknown builder" (never "nobody") when unresolvable, per #65's own failure-mode note.</summary>
        public string Builder = "";
        public string NetworkId = "";
        public bool Locked;
        public DateTimeOffset FirstSeenUtc;
        public DateTimeOffset LastChangedUtc;
    }

    /// <summary>Persisted alongside the report - vanilla stores no wall-clock on a ZDO (#65's own citation: the whole ZDO field set is m_uid/m_rotation/m_position/m_tempSortValue/m_prefab/m_dataFlags/m_tempRemoveEarmark, :73309-73321), so first-seen/last-changed are mod-maintained and must survive a restart.</summary>
    internal sealed class OpsReportTimestampEntry
    {
        public string FirstSeenUtc = "";
        public string LastChangedUtc = "";
        public string LastTag = "";
        public string LastConnection = "";
    }

    public static class OpsOutputReportModel
    {
        private const float DefaultRebuildInterval = 2f;
        private static float _timer;
        private static List<OpsPortalReport> _latest = new List<OpsPortalReport>();
        private static Dictionary<string, OpsReportTimestampEntry> _timestamps = new Dictionary<string, OpsReportTimestampEntry>();
        private static bool _timestampsLoaded;
        private static bool _timestampsDirty;
        private static float _timestampSaveTimer;

        public static IReadOnlyList<OpsPortalReport> Latest => _latest;
        public static DateTime LastBuiltUtc { get; private set; }

        private static string TimestampFilePath => Path.Combine(OpsOutputConfig.PluginConfigDir, "TortalPortalLite.portals.timestamps.json");

        public static void OnUpdate(float dt)
        {
            EnsureTimestampsLoaded();

            _timer += dt;
            if (_timer >= DefaultRebuildInterval)
            {
                _timer = 0f;
                Rebuild();
            }

            if (_timestampsDirty)
            {
                _timestampSaveTimer += dt;
                if (_timestampSaveTimer >= 5f)
                {
                    _timestampSaveTimer = 0f;
                    SaveTimestamps();
                }
            }
        }

        /// <summary>Forces an immediate rebuild - used by on-demand consumers (tplite export/map/http-probe) that shouldn't wait up to 2s for the next scheduled pass.</summary>
        public static void ForceRebuild() => Rebuild();

        private static void EnsureTimestampsLoaded()
        {
            if (_timestampsLoaded)
            {
                return;
            }
            _timestampsLoaded = true;
            try
            {
                string path = TimestampFilePath;
                if (File.Exists(path))
                {
                    string json = File.ReadAllText(path);
                    var loaded = JsonConvert.DeserializeObject<Dictionary<string, OpsReportTimestampEntry>>(json);
                    if (loaded != null)
                    {
                        _timestamps = loaded;
                    }
                }
            }
            catch (Exception ex)
            {
                PortalDebug.LogWarning($"[OpsOutputReportModel] failed to load '{TimestampFilePath}': {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static void SaveTimestamps()
        {
            try
            {
                string json = JsonConvert.SerializeObject(_timestamps, Formatting.None);
                OpsOutputAtomicFile.TryWriteAllText(TimestampFilePath, json, out _);
                _timestampsDirty = false;
            }
            catch (Exception ex)
            {
                PortalDebug.LogWarning($"[OpsOutputReportModel] failed to save timestamps: {ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>
        /// #74's own worked CSV column list, shared by the file export and the HTTP endpoint's
        /// /portals.csv route so the two output surfaces can never drift out of sync with each other.
        /// </summary>
        public static string BuildCsv(IReadOnlyList<OpsPortalReport> reports)
        {
            var sb = new StringBuilder();
            sb.Append("id,tag,x,y,z,biome,zone,prefab,connected,partnerId,partnerTag,distance,reciprocal,network,ownerPeer,builder,tagAuthor,firstSeen,lastChanged\n");
            foreach (OpsPortalReport r in reports)
            {
                sb.Append(CsvEscape(r.Id)).Append(',')
                  .Append(CsvEscape(r.Tag)).Append(',')
                  .Append(r.X.ToString(CultureInfo.InvariantCulture)).Append(',')
                  .Append(r.Y.ToString(CultureInfo.InvariantCulture)).Append(',')
                  .Append(r.Z.ToString(CultureInfo.InvariantCulture)).Append(',')
                  .Append(CsvEscape(r.Biome)).Append(',')
                  .Append(CsvEscape($"{r.ZoneX},{r.ZoneY}")).Append(',')
                  .Append(CsvEscape(r.Prefab)).Append(',')
                  .Append(r.Connected ? "true" : "false").Append(',')
                  .Append(CsvEscape(r.PartnerId)).Append(',')
                  .Append(CsvEscape(r.PartnerTag)).Append(',')
                  .Append(r.Distance.ToString("F1", CultureInfo.InvariantCulture)).Append(',')
                  .Append(r.Reciprocal ? "true" : "false").Append(',')
                  .Append(CsvEscape(r.NetworkId)).Append(',')
                  .Append(CsvEscape(r.OwnerKind)).Append(',')
                  .Append(CsvEscape(r.Builder)).Append(',')
                  .Append(CsvEscape(r.TagAuthor)).Append(',')
                  .Append(r.FirstSeenUtc.ToString("o")).Append(',')
                  .Append(r.LastChangedUtc.ToString("o"))
                  .Append('\n');
            }
            return sb.ToString();
        }

        private static string CsvEscape(string? value)
        {
            value ??= "";
            if (value.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0)
            {
                return "\"" + value.Replace("\"", "\"\"") + "\"";
            }
            return value;
        }

        /// <summary>Same 0.5m rounding PortalCensus itself uses internally - reproduced here (not exposed by that class) so the id in this report matches what an admin sees at "the portal at my base".</summary>
        public static string PositionId(Vector3 pos)
        {
            float x = Mathf.Round(pos.x * 2f) / 2f;
            float y = Mathf.Round(pos.y * 2f) / 2f;
            float z = Mathf.Round(pos.z * 2f) / 2f;
            return $"{x:F1}:{y:F1}:{z:F1}";
        }

        private static void Rebuild()
        {
            try
            {
                var byUid = new Dictionary<ZDOID, PortalRecord>();
                foreach (PortalRecord r in PortalCensus.Latest)
                {
                    byUid[r.Uid] = r;
                }

                long sessionId = ZDOMan.instance != null ? ZDOMan.GetSessionID() : 0L;
                DateTimeOffset now = DateTimeOffset.UtcNow;
                var next = new List<OpsPortalReport>(byUid.Count);

                foreach (PortalRecord record in PortalCensus.Latest)
                {
                    string id = PositionId(record.Position);
                    OpsReportTimestampEntry ts = TouchTimestamp(id, record, now);

                    var report = new OpsPortalReport
                    {
                        Id = id,
                        SessionZdoId = record.Uid.ToString(),
                        Prefab = ResolvePrefabName(record.Prefab),
                        Tag = record.Tag,
                        TagAuthor = record.TagAuthor,
                        TagAuthorTrusted = false,
                        X = record.Position.x,
                        Y = record.Position.y,
                        Z = record.Position.z,
                        RotY = record.Rotation.eulerAngles.y,
                        Biome = PortalCensus.BiomeAt(record.Position).ToString(),
                        Interior = Character.InInterior(record.Position),
                    };

                    Vector2s zone = ZoneSystem.GetZone(record.Position);
                    report.ZoneX = zone.x;
                    report.ZoneY = zone.y;

                    if (record.Connection != ZDOID.None && byUid.TryGetValue(record.Connection, out PortalRecord partner))
                    {
                        report.Connected = true;
                        report.PartnerId = PositionId(partner.Position);
                        report.PartnerTag = partner.Tag;
                        report.PartnerX = partner.Position.x;
                        report.PartnerY = partner.Position.y;
                        report.PartnerZ = partner.Position.z;
                        report.Reciprocal = partner.Connection == record.Uid;
                        report.Distance = Vector3.Distance(record.Position, partner.Position);
                    }

                    report.OwnerPeer = record.Owner;
                    report.OwnerKind = ResolveOwnerKind(record.Owner, sessionId);
                    report.Builder = ResolveBuilder(record.Creator, record.CreatorIndex);

                    ZDO? liveZdo = ZDOMan.instance?.GetZDO(record.Uid);
                    if (liveZdo != null && liveZdo.IsValid())
                    {
                        report.NetworkId = PortalRecordStore.GetNetworkId(liveZdo);
                        report.Locked = PortalRecordStore.IsLocked(liveZdo);
                    }

                    report.FirstSeenUtc = DateTimeOffset.Parse(ts.FirstSeenUtc);
                    report.LastChangedUtc = DateTimeOffset.Parse(ts.LastChangedUtc);

                    next.Add(report);
                }

                _latest = next;
                LastBuiltUtc = DateTime.UtcNow;
            }
            catch (Exception ex)
            {
                PortalDebug.LogError($"[OpsOutputReportModel] rebuild failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static OpsReportTimestampEntry TouchTimestamp(string id, PortalRecord record, DateTimeOffset now)
        {
            string nowStr = now.ToString("o");
            string connStr = record.Connection.ToString();
            if (!_timestamps.TryGetValue(id, out OpsReportTimestampEntry entry))
            {
                entry = new OpsReportTimestampEntry { FirstSeenUtc = nowStr, LastChangedUtc = nowStr, LastTag = record.Tag, LastConnection = connStr };
                _timestamps[id] = entry;
                _timestampsDirty = true;
                return entry;
            }

            if (entry.LastTag != record.Tag || entry.LastConnection != connStr)
            {
                entry.LastChangedUtc = nowStr;
                entry.LastTag = record.Tag;
                entry.LastConnection = connStr;
                _timestampsDirty = true;
            }
            return entry;
        }

        private static string ResolvePrefabName(int prefabHash)
        {
            try
            {
                GameObject? prefab = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(prefabHash) : null;
                return prefab != null ? prefab.name : $"hash:{prefabHash}";
            }
            catch
            {
                return $"hash:{prefabHash}";
            }
        }

        private static string ResolveOwnerKind(long owner, long sessionId)
        {
            if (owner == 0L)
            {
                return "unowned";
            }
            if (owner == sessionId)
            {
                return "server";
            }
            try
            {
                if (ZNet.instance != null)
                {
                    foreach (ZNetPeer peer in ZNet.instance.GetPeers())
                    {
                        if (peer != null && peer.m_uid == owner)
                        {
                            return string.IsNullOrEmpty(peer.m_playerName) ? $"peer:{owner}" : peer.m_playerName;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                PortalDebug.LogWarning($"[OpsOutputReportModel] owner-peer lookup failed: {ex.Message}");
            }
            return $"peer:{owner}";
        }

        /// <summary>
        /// s_creator == 0 on world-generated or very old portals - Piece.SetCreator only writes when
        /// GetCreator() == 0 && m_nview.IsOwner() (:136417). s_creatorIndex == -1 when the placer wasn't
        /// yet in m_playerHistory at placement time. Both render as "unknown builder", never "nobody" -
        /// #65's own explicit failure-mode correction.
        /// </summary>
        private static string ResolveBuilder(long creator, int creatorIndex)
        {
            if (creator == 0L)
            {
                return "unknown builder";
            }
            try
            {
                var history = ZNet.World?.m_playerHistory;
                if (history != null && creatorIndex >= 0 && creatorIndex < history.Count)
                {
                    string name = history[creatorIndex].m_displayName;
                    if (!string.IsNullOrEmpty(name))
                    {
                        return name;
                    }
                }
            }
            catch (Exception ex)
            {
                PortalDebug.LogWarning($"[OpsOutputReportModel] builder lookup failed: {ex.Message}");
            }
            return "unknown builder";
        }
    }
}
