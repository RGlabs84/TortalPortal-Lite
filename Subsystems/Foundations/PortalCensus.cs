using System;
using System.Collections.Generic;
using UnityEngine;
using TortalPortalLite.Core;
using TortalPortalLite.Core.Data;

namespace TortalPortalLite.Subsystems.Foundations
{
    /// <summary>One immutable snapshot record - catalog #64's per-portal fields.</summary>
    public readonly struct PortalRecord
    {
        public readonly ZDOID Uid;
        public readonly int Prefab;
        public readonly Vector3 Position;
        public readonly Quaternion Rotation;
        public readonly string Tag;
        public readonly string TagAuthor;
        public readonly ZDOID Connection;
        public readonly long Owner;
        public readonly long Creator;
        public readonly int CreatorIndex;
        public readonly uint DataRevision;

        public PortalRecord(ZDO zdo)
        {
            Uid = zdo.m_uid;
            Prefab = zdo.GetPrefab();
            Position = zdo.GetPosition();
            Rotation = zdo.GetRotation();
            Tag = zdo.GetString(ZDOVars.s_tag, "");
            TagAuthor = zdo.GetString(ZDOVars.s_tagauthor, "");
            Connection = zdo.GetConnectionZDOID(ZDOExtraData.ConnectionType.Portal);
            Owner = zdo.GetOwner();
            Creator = zdo.GetLong(ZDOVars.s_creator, 0L);
            CreatorIndex = zdo.GetInt(ZDOVars.s_creatorIndex, -1);
            DataRevision = zdo.DataRevision;
        }
    }

    /// <summary>
    /// #64 PortalCensus - the single-source-of-truth index. A 1 Hz (configurable) main-thread snapshot
    /// of every portal ZDO, built from ZDOMan's own always-resident portal registry
    /// (ZDOMan.GetPortalList(), SERVER decompile :77648, which flattens the private m_portalObjects
    /// dictionary :76039 - this is why the csproj needs Publicize=true). Every other Foundations engine
    /// reads Latest; NOTHING but this class may write to it, and this class itself never writes to any
    /// portal ZDO (catalog #69's ownership rule: "the census may never write at all").
    ///
    /// Biome comes from WorldGenerator.instance.GetBiome(Vector3) - pure procedural noise, safe headless
    /// (no Physics.Raycast, unlike ZoneSystem.GetGroundHeight/IsBlocked which always return the no-hit
    /// fallback server-side because Game.FixedUpdate pins the reference position every tick).
    /// </summary>
    public static class PortalCensus
    {
        private static float _timer;
        private static List<PortalRecord> _latest = new List<PortalRecord>();
        private static Dictionary<ZDOID, PortalRecord> _byUid = new Dictionary<ZDOID, PortalRecord>();
        private static Dictionary<Vector3, PortalRecord> _byRoundedPosition = new Dictionary<Vector3, PortalRecord>();

        public static IReadOnlyList<PortalRecord> Latest => _latest;
        public static DateTime LastScanUtc { get; private set; }

        public static bool TryGet(ZDOID uid, out PortalRecord record) => _byUid.TryGetValue(uid, out record);

        /// <summary>
        /// Position-keyed lookup on a 0.5m grid - catalog #64's own citation: ZDOIDs are NOT stable
        /// identity across a world reload (ZDO.Load re-mints m_uid via ZDOID.m_loadID), but a portal's
        /// physical position is what an admin actually names ("the portal at my base"). Never used for
        /// PERSISTED network membership - see PortalRecordStore.RecordId for that.
        /// </summary>
        public static bool TryGetByPosition(Vector3 pos, out PortalRecord record) => _byRoundedPosition.TryGetValue(Round(pos), out record);

        private static Vector3 Round(Vector3 v) => new Vector3(Mathf.Round(v.x * 2f) / 2f, Mathf.Round(v.y * 2f) / 2f, Mathf.Round(v.z * 2f) / 2f);

        public static void OnUpdate(float dt)
        {
            _timer += dt;
            float interval = FoundationsConfig.CensusIntervalSeconds?.Value ?? 1.0f;
            if (_timer < interval)
            {
                return;
            }
            _timer = 0f;
            Rescan();
        }

        private static void Rescan()
        {
            if (ZDOMan.instance == null)
            {
                return;
            }

            try
            {
                List<ZDO> portals = ZDOMan.instance.GetPortalList();
                var next = new List<PortalRecord>(portals.Count);
                var byUid = new Dictionary<ZDOID, PortalRecord>(portals.Count);
                var byPos = new Dictionary<Vector3, PortalRecord>(portals.Count);
                foreach (ZDO zdo in portals)
                {
                    if (zdo == null || !zdo.IsValid())
                    {
                        continue;
                    }
                    var record = new PortalRecord(zdo);
                    next.Add(record);
                    byUid[record.Uid] = record;
                    byPos[Round(record.Position)] = record;
                }
                _latest = next;
                _byUid = byUid;
                _byRoundedPosition = byPos;
                LastScanUtc = DateTime.UtcNow;
            }
            catch (Exception ex)
            {
                PortalDebug.LogError($"[PortalCensus] rescan failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>Pure procedural noise - safe to call with zero terrain loaded, unlike anything collider-based.</summary>
        public static Heightmap.Biome BiomeAt(Vector3 pos)
        {
            return WorldGenerator.instance != null ? WorldGenerator.instance.GetBiome(pos) : Heightmap.Biome.None;
        }
    }
}
