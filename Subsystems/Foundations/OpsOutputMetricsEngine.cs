using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using TortalPortalLite.Core;
using TortalPortalLite.Core.Data;

namespace TortalPortalLite.Subsystems.Foundations
{
    /// <summary>
    /// #73 MetricsEngine. Two very different confidence tiers, kept clearly separate everywhere they're
    /// rendered:
    ///
    /// EXACT (census-derived, free): total/connected/unconnected/managed/unmanaged counts, per-tag,
    /// per-biome, per-builder tallies, new/destroyed-per-hour (from OpsOutputReportModel's own
    /// first-seen timestamps and AuditEngine's "destroyed" entries), plus vanilla's own server-wide
    /// counters (ZDOMan.NrOfObjects/GetSentZDOs/GetRecvZDOs, :77658/:77663/:77668).
    ///
    /// HEURISTIC ONLY (transit counts / "most-used routes"): vanilla's own PlayerStatType.PortalsUsed
    /// (:106516) is written into a PURELY CLIENT-SIDE PlayerProfile.IncrementStat (:106287-106300) and
    /// never reaches the server in any form - ZNet.SaveOtherPlayerProfiles (:79653-79670) only tells each
    /// client to save ITS OWN file. The only usable signal is Wonderland's own already-tuned
    /// position-straddle heuristic (Subsystems/Security/PositionWatch.cs:148): sample connected
    /// characters' positions every MetricsSampleSeconds, and flag a transit when both ends of a sample sit
    /// within StraddleRadius of some portal (with an extended 60m check through the near portal's own
    /// connection target) - ported here and INVERTED (PositionWatch suppresses a speed alarm on a
    /// detected transit; this counts it and records the (from,to) portal pair for a route tally).
    /// </summary>
    public static class OpsOutputMetricsEngine
    {
        // --- exact, census-derived ---
        public static int TotalPortals { get; private set; }
        public static int ConnectedCount { get; private set; }
        public static int UnconnectedCount { get; private set; }
        public static int ManagedCount { get; private set; }
        public static int UnmanagedCount { get; private set; }
        public static int NewLastHour { get; private set; }
        public static int DestroyedLastHour { get; private set; }
        public static IReadOnlyDictionary<string, int> ByTag => _byTag;
        public static IReadOnlyDictionary<string, int> ByBiome => _byBiome;
        public static IReadOnlyDictionary<string, int> ByBuilder => _byBuilder;

        // --- vanilla-wide, exact ---
        public static int VanillaZdoCount { get; private set; }
        public static int VanillaSentZdos { get; private set; }
        public static int VanillaRecvZdos { get; private set; }

        // --- heuristic transit tracking ---
        public static int TransitsLastWindow { get; private set; }
        public static float TransitsPerHourEstimate { get; private set; }

        private static Dictionary<string, int> _byTag = new Dictionary<string, int>();
        private static Dictionary<string, int> _byBiome = new Dictionary<string, int>();
        private static Dictionary<string, int> _byBuilder = new Dictionary<string, int>();
        private static readonly Dictionary<(string from, string to), int> _routeCounts = new Dictionary<(string, string), int>();

        private static readonly Dictionary<ZDOID, (Vector3 pos, float time)> _lastSample = new Dictionary<ZDOID, (Vector3, float)>();
        private static readonly HashSet<ZDOID> _seenThisPass = new HashSet<ZDOID>();
        private static readonly List<ZDOID> _stale = new List<ZDOID>();
        private static int _transitsSinceLastAggregate;

        private static float _sampleTimer;
        private static float _aggregateTimer;

        public static void OnUpdate(float dt)
        {
            _sampleTimer += dt;
            float sampleInterval = FoundationsConfig.MetricsSampleSeconds?.Value ?? 3f;
            if (_sampleTimer >= sampleInterval)
            {
                _sampleTimer = 0f;
                SampleTransits();
            }

            _aggregateTimer += dt;
            float aggInterval = FoundationsConfig.MetricsAggregateSeconds?.Value ?? 60f;
            if (_aggregateTimer >= aggInterval)
            {
                TransitsLastWindow = _transitsSinceLastAggregate;
                TransitsPerHourEstimate = _transitsSinceLastAggregate * (3600f / Math.Max(1f, _aggregateTimer));
                _transitsSinceLastAggregate = 0;
                _aggregateTimer = 0f;
                Aggregate();
            }
        }

        public static IEnumerable<string> TopRoutes(int n) =>
            _routeCounts.OrderByDescending(kvp => kvp.Value).Take(n).Select(kvp => $"{kvp.Key.from}->{kvp.Key.to} ({kvp.Value})");

        private static void Aggregate()
        {
            try
            {
                var byTag = new Dictionary<string, int>();
                var byBiome = new Dictionary<string, int>();
                var byBuilder = new Dictionary<string, int>();
                int connected = 0, managed = 0;

                foreach (PortalRecord r in PortalCensus.Latest)
                {
                    string tagKey = string.IsNullOrEmpty(r.Tag) ? "(none)" : r.Tag;
                    byTag.TryGetValue(tagKey, out int tc); byTag[tagKey] = tc + 1;

                    string biomeKey = PortalCensus.BiomeAt(r.Position).ToString();
                    byBiome.TryGetValue(biomeKey, out int bc); byBiome[biomeKey] = bc + 1;

                    if (r.Connection != ZDOID.None) connected++;

                    ZDO? zdo = ZDOMan.instance?.GetZDO(r.Uid);
                    if (zdo != null && zdo.IsValid())
                    {
                        if (!string.IsNullOrEmpty(PortalRecordStore.GetNetworkId(zdo))) managed++;
                    }
                }
                foreach (OpsPortalReport report in OpsOutputReportModel.Latest)
                {
                    byBuilder.TryGetValue(report.Builder, out int uc); byBuilder[report.Builder] = uc + 1;
                }

                _byTag = byTag;
                _byBiome = byBiome;
                _byBuilder = byBuilder;

                TotalPortals = PortalCensus.Latest.Count;
                ConnectedCount = connected;
                UnconnectedCount = TotalPortals - connected;
                ManagedCount = managed;
                UnmanagedCount = TotalPortals - managed;

                DateTimeOffset hourAgo = DateTimeOffset.UtcNow.AddHours(-1);
                NewLastHour = OpsOutputReportModel.Latest.Count(r => r.FirstSeenUtc >= hourAgo);
                DestroyedLastHour = AuditEngine.Entries.Count(e => e.Change == "destroyed" && e.WhenUtc >= hourAgo.UtcDateTime);

                if (ZDOMan.instance != null)
                {
                    VanillaZdoCount = ZDOMan.instance.NrOfObjects();
                    VanillaSentZdos = ZDOMan.instance.GetSentZDOs();
                    VanillaRecvZdos = ZDOMan.instance.GetRecvZDOs();
                }
            }
            catch (Exception ex)
            {
                PortalDebug.LogError($"[OpsOutputMetricsEngine] aggregate failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static void SampleTransits()
        {
            try
            {
                float radius = FoundationsConfig.MetricsTransitStraddleRadius?.Value ?? 40f;
                float now = Time.time;
                _seenThisPass.Clear();

                foreach (ConnectedCharacter character in ConnectedCharacters.All())
                {
                    ZDOID uid = character.Zdo.m_uid;
                    Vector3 pos = character.Position;
                    _seenThisPass.Add(uid);

                    bool isDead = character.Zdo.GetBool(ZDOVars.s_dead);
                    bool isAdmin = false;
                    string? host = SenderContext.HostNameOf(character.Peer);
                    if (!string.IsNullOrEmpty(host) && ZNet.instance != null)
                    {
                        isAdmin = ZNet.instance.IsAdmin(host);
                    }

                    if (_lastSample.TryGetValue(uid, out (Vector3 pos, float time) last) && !isDead && !isAdmin)
                    {
                        // Exclusions: dungeon-door transits (class Teleport has no ZDO at all, :143316-143390) and
                        // respawns both produce a huge position jump with no portal anywhere near either end -
                        // the interior/altitude checks below filter those without needing to touch Teleport itself.
                        bool interiorFlip = Character.InInterior(last.pos) != Character.InInterior(pos);
                        bool bigYJump = Mathf.Abs(pos.y - last.pos.y) > 1000f;
                        if (!interiorFlip && !bigYJump && TryDetectPortalTransit(last.pos, pos, radius, out string fromId, out string toId))
                        {
                            _transitsSinceLastAggregate++;
                            var key = (fromId, toId);
                            _routeCounts.TryGetValue(key, out int c);
                            _routeCounts[key] = c + 1;
                        }
                    }
                    _lastSample[uid] = (pos, now);
                }

                _stale.Clear();
                foreach (ZDOID uid in _lastSample.Keys)
                {
                    if (!_seenThisPass.Contains(uid)) _stale.Add(uid);
                }
                foreach (ZDOID uid in _stale) _lastSample.Remove(uid);
            }
            catch (Exception ex)
            {
                PortalDebug.LogError($"[OpsOutputMetricsEngine] transit sample failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>Direct port of Wonderland's PositionWatch.IsPortalTransit (PositionWatch.cs:148), inverted (count, don't suppress) and extended to also return the two nearest portal ids for the route tally.</summary>
        private static bool TryDetectPortalTransit(Vector3 fromPos, Vector3 toPos, float radius, out string fromId, out string toId)
        {
            fromId = "";
            toId = "";
            float rSqr = radius * radius;
            PortalRecord? nearFrom = null;
            PortalRecord? nearTo = null;

            foreach (PortalRecord r in PortalCensus.Latest)
            {
                if (nearFrom == null && Vector3.SqrMagnitude(r.Position - fromPos) <= rSqr) nearFrom = r;
                if (nearTo == null && Vector3.SqrMagnitude(r.Position - toPos) <= rSqr) nearTo = r;
                if (nearFrom != null && nearTo != null) break;
            }

            if (nearFrom != null && nearTo != null)
            {
                fromId = OpsOutputReportModel.PositionId(nearFrom.Value.Position);
                toId = OpsOutputReportModel.PositionId(nearTo.Value.Position);
                return true;
            }

            const float extendedRSqr = 60f * 60f;
            if (nearFrom != null && nearFrom.Value.Connection != ZDOID.None && PortalCensus.TryGet(nearFrom.Value.Connection, out PortalRecord target))
            {
                if (Vector3.SqrMagnitude(target.Position - toPos) <= extendedRSqr)
                {
                    fromId = OpsOutputReportModel.PositionId(nearFrom.Value.Position);
                    toId = OpsOutputReportModel.PositionId(target.Position);
                    return true;
                }
            }
            if (nearTo != null && nearTo.Value.Connection != ZDOID.None && PortalCensus.TryGet(nearTo.Value.Connection, out PortalRecord source))
            {
                if (Vector3.SqrMagnitude(source.Position - fromPos) <= extendedRSqr)
                {
                    fromId = OpsOutputReportModel.PositionId(source.Position);
                    toId = OpsOutputReportModel.PositionId(nearTo.Value.Position);
                    return true;
                }
            }
            return false;
        }
    }
}
