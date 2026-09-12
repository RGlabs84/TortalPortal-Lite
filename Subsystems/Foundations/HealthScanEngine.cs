using System;
using System.Collections.Generic;
using TortalPortalLite.Core;

namespace TortalPortalLite.Subsystems.Foundations
{
    public enum FindingSeverity { Info, Warning, Error }

    public readonly struct HealthFinding
    {
        public readonly FindingSeverity Severity;
        public readonly string Category;
        public readonly string Detail;
        public readonly IReadOnlyList<ZDOID> Portals;

        public HealthFinding(FindingSeverity severity, string category, string detail, IReadOnlyList<ZDOID> portals)
        {
            Severity = severity;
            Category = category;
            Detail = detail;
            Portals = portals;
        }
    }

    /// <summary>
    /// #70 HealthScanEngine - a pure-read (30s default) pass over PortalCensus's own snapshot,
    /// classifying every pathology vanilla's pairing algorithm can produce. Writes nothing - findings
    /// only, consumed by RepairEngine/the command dispatcher/the export engine.
    /// </summary>
    public static class HealthScanEngine
    {
        private static float _timer;
        private static List<HealthFinding> _findings = new List<HealthFinding>();

        /// <summary>Persists across scans - used to require a dangling connection to be seen twice before reporting (vanilla clears most within 5s itself).</summary>
        private static readonly HashSet<ZDOID> _danglingLastScan = new HashSet<ZDOID>();

        public static IReadOnlyList<HealthFinding> Findings => _findings;

        public static void OnUpdate(float dt)
        {
            _timer += dt;
            float interval = FoundationsConfig.HealthIntervalSeconds?.Value ?? 30f;
            if (_timer < interval)
            {
                return;
            }
            _timer = 0f;
            Scan();
        }

        private static void Scan()
        {
            var findings = new List<HealthFinding>();
            var byTag = new Dictionary<string, List<PortalRecord>>();
            var byUid = new Dictionary<ZDOID, PortalRecord>();

            foreach (PortalRecord record in PortalCensus.Latest)
            {
                byUid[record.Uid] = record;
                if (!byTag.TryGetValue(record.Tag, out var list))
                {
                    list = new List<PortalRecord>();
                    byTag[record.Tag] = list;
                }
                list.Add(record);
            }

            // 1. Odd-count strand: any tag group of size >= 3 leaves floor(N/2) pairs and N mod 2 permanently orphaned.
            bool reportUnmanaged = FoundationsConfig.ReportUnmanagedOddGroups?.Value != false;
            foreach (var kvp in byTag)
            {
                if (string.IsNullOrEmpty(kvp.Key) || kvp.Value.Count < 3)
                {
                    continue;
                }
                bool isManaged = false;
                foreach (PortalRecord r in kvp.Value)
                {
                    if (!string.IsNullOrEmpty(PortalRecordStore_NetworkIdOf(byUid, r.Uid)))
                    {
                        isManaged = true;
                        break;
                    }
                }
                if (!isManaged && !reportUnmanaged)
                {
                    continue;
                }
                var ids = new List<ZDOID>();
                foreach (PortalRecord r in kvp.Value) ids.Add(r.Uid);
                findings.Add(new HealthFinding(FindingSeverity.Warning, "OddCountStrand",
                    $"tag '{kvp.Key}' has {kvp.Value.Count} members - {kvp.Value.Count % 2} will always be orphaned by vanilla's strictly-pairwise pass (which member is stranded is dictionary-bucket order and changes across restarts).", ids));
            }

            // 2. Dangling connection: connection non-None but target ZDO not in the census, persisting across two scans.
            var danglingThisScan = new HashSet<ZDOID>();
            foreach (PortalRecord record in PortalCensus.Latest)
            {
                if (record.Connection == ZDOID.None || byUid.ContainsKey(record.Connection))
                {
                    continue;
                }
                danglingThisScan.Add(record.Uid);
                if (_danglingLastScan.Contains(record.Uid))
                {
                    findings.Add(new HealthFinding(FindingSeverity.Warning, "DanglingConnection",
                        "connection target is not a live portal, across two consecutive scans - something is re-writing it, since vanilla itself clears this within 5s.", new List<ZDOID> { record.Uid }));
                }
            }
            _danglingLastScan.Clear();
            foreach (ZDOID uid in danglingThisScan) _danglingLastScan.Add(uid);

            // 3. Non-reciprocal link: A -> B while B -> C or B -> None. Informational for unmanaged portals.
            foreach (PortalRecord record in PortalCensus.Latest)
            {
                if (record.Connection == ZDOID.None || !byUid.TryGetValue(record.Connection, out PortalRecord partner))
                {
                    continue;
                }
                if (partner.Connection != record.Uid)
                {
                    string network = PortalRecordStore_NetworkIdOf(byUid, record.Uid);
                    FindingSeverity sev = !string.IsNullOrEmpty(network) ? FindingSeverity.Error : FindingSeverity.Info;
                    findings.Add(new HealthFinding(sev, "NonReciprocalLink",
                        $"{record.Uid} -> {record.Connection}, but that portal does not point back (legal and runtime-stable in vanilla; only a problem for a declared 'pairs'-style network).", new List<ZDOID> { record.Uid, record.Connection }));
                }
            }

            // 4. Self-loop: A -> A. Stable at runtime, erased at save.
            foreach (PortalRecord record in PortalCensus.Latest)
            {
                if (record.Connection == record.Uid)
                {
                    findings.Add(new HealthFinding(FindingSeverity.Info, "SelfLoop",
                        "connected to itself - stable at runtime but will not survive a world restart (the on-disk connection-hash slot collapses self-loops).", new List<ZDOID> { record.Uid }));
                }
            }

            // 5. Declared-but-not-found: a network's declared member position resolved to nothing.
            foreach (NetworkDefinition network in NetworkModel.Networks)
            {
                foreach (NetworkMemberPosition pos in network.Members)
                {
                    if (!PortalCensus.TryGetByPosition(pos.ToVector3(), out _))
                    {
                        findings.Add(new HealthFinding(FindingSeverity.Warning, "DeclaredNotFound",
                            $"network '{network.Name}' declares a member at ({pos.X:F1},{pos.Y:F1},{pos.Z:F1}) with no portal ZDO there.", Array.Empty<ZDOID>()));
                    }
                }
            }

            _findings = findings;
        }

        private static string PortalRecordStore_NetworkIdOf(Dictionary<ZDOID, PortalRecord> byUid, ZDOID uid)
        {
            // HealthScanEngine only has the census (position/tag/connection), not a live ZDO handle -
            // NetworkId is a mod-private ZDO key PortalRecordStore reads from an actual ZDO, so this
            // resolves it the only way available here: through ZDOMan, on demand, scan-time only (never
            // cached - this is a diagnostic read, not a hot path).
            ZDO zdo = ZDOMan.instance?.GetZDO(uid);
            return zdo != null ? PortalRecordStore.GetNetworkId(zdo) : "";
        }
    }
}
