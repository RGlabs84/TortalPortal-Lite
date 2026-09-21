using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using TortalPortalLite.Core;
using TortalPortalLite.Core.Data;

namespace TortalPortalLite.Subsystems.Foundations
{
    /// <summary>
    /// #71 Automatic repair pass - turns each HealthScanEngine finding into a bounded, logged, reversible
    /// corrective ZDO write. Default is dry-run (FoundationsConfig.RepairDryRunDefault): compute and print
    /// every action, write nothing. Every writing action uses PortalOwnership.ClaimAndWrite - the same
    /// "claim owner -> write -> ForceSendZDO -> SetDirtyPortals" primitive NetworkReassertEngine itself
    /// uses (that engine's own doc comment names it as "the reassert engine's write primitive", not a
    /// method exclusive to that class). NetworkReassertEngine's doc comment asks that tag/connection
    /// writes "ideally" route through it - RepairEngine is a deliberate, narrow, always-audited exception
    /// for CORRECTIVE actions outside that engine's own declarative scope (dangling-clear and self-loop
    /// removal are deletions, not network membership declarations; there is nothing to add to
    /// networks.json for either). An automatic pre-repair snapshot (OpsOutputSnapshotEngine) is taken
    /// before every --apply pass so `tplite restore &lt;name&gt;` can always undo the whole batch.
    /// While VersionMigration.DestructivePassesAllowed is false the automatic --apply pass logs that it is
    /// idle ONCE and then skips silently, resuming by itself the moment the gate opens.
    /// </summary>
    public static class OpsOutputRepairEngine
    {
        private static float _autoTimer;
        private static bool _gateIdleLogged;

        /// <summary>Cumulative count of individually-applied repair actions since this process started - exposed for the /metrics HTTP route (tplite_repairs_total) and Discord summaries.</summary>
        public static long TotalActionsApplied { get; private set; }

        public static void OnUpdate(float dt)
        {
            if (FoundationsConfig.AutoRepair?.Value != true)
            {
                return;
            }
            _autoTimer += dt;
            float interval = OpsOutputConfig.RepairAutoIntervalSeconds?.Value ?? 300f;
            if (_autoTimer < interval)
            {
                return;
            }
            _autoTimer = 0f;
            bool dryRun = FoundationsConfig.RepairDryRunDefault?.Value != false;
            if (!dryRun && !VersionMigration.DestructivePassesAllowed)
            {
                // Run(apply: true) could only come back refused, so say so once and skip instead of
                // re-logging the identical refusal every interval (the VanillaBean box printed it 230
                // times across its 1.0.3/1.0.4 boots after moving to a then-unverified Valheim 1.0.15).
                // Checked at fire time rather than at boot on purpose: VersionMigration's registry half
                // only lands once Game.instance exists, so a boot-time check would flag every build.
                if (!_gateIdleLogged)
                {
                    _gateIdleLogged = true;
                    PortalDebug.LogWarning("[OpsOutputRepairEngine] automatic --apply pass is idle: destructive passes are disallowed on this build (see VersionMigration/PortalPrefabHashSane). It resumes by itself if the gate opens (AcceptUnverifiedBuild hot-reloads); set DryRunDefault=true for report-only passes meanwhile.");
                }
                return;
            }
            if (_gateIdleLogged)
            {
                _gateIdleLogged = false;
                PortalDebug.LogAlways($"[OpsOutputRepairEngine] automatic pass resumed ({(dryRun ? "DryRunDefault=true, report-only" : "destructive passes are allowed again")}).");
            }
            string result = Run(apply: !dryRun);
            PortalDebug.LogAlways($"[OpsOutputRepairEngine] automatic pass: {result}");
        }

        /// <summary>Console/file-queue entry point. `apply=false` (or omitted) is always a dry run regardless of config defaults - an explicit request to actually write requires an explicit --apply.</summary>
        public static string Run(bool apply)
        {
            if (ZDOMan.instance == null)
            {
                return "tpl: ZDOMan not ready.";
            }
            if (apply && !VersionMigration.DestructivePassesAllowed)
            {
                return "tpl: repair --apply refused - destructive passes are disallowed on this build (see VersionMigration/PortalPrefabHashSane).";
            }

            var actions = PlanActions();
            if (actions.Count == 0)
            {
                return "tpl: repair: no actionable findings.";
            }

            int budget = FoundationsConfig.RepairMaxActionsPerRun?.Value ?? 20;
            var chosen = actions.Take(budget).ToList();

            if (!apply)
            {
                var sb = new StringBuilder();
                sb.Append($"DRY RUN - {chosen.Count} action(s): ");
                sb.Append(string.Join("; ", chosen.Select(a => a.Description)));
                sb.Append(". Re-run with --apply.");
                return sb.ToString();
            }

            string? snapshotName = null;
            if (OpsOutputConfig.SnapshotOnReassertApply?.Value != false)
            {
                snapshotName = "pre-repair-" + DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'");
                OpsOutputSnapshotEngine.TakeSnapshot(snapshotName);
            }

            int applied = 0;
            foreach (RepairAction action in chosen)
            {
                try
                {
                    action.Apply();
                    applied++;
                    TotalActionsApplied++;
                    PortalDebug.LogAlways($"[OpsOutputRepairEngine] applied: {action.Description}");
                }
                catch (Exception ex)
                {
                    PortalDebug.LogWarning($"[OpsOutputRepairEngine] action failed ({action.Description}): {ex.GetType().Name}: {ex.Message}");
                }
            }

            return snapshotName != null
                ? $"tpl: repair applied {applied}/{chosen.Count}, snapshot '{snapshotName}' saved."
                : $"tpl: repair applied {applied}/{chosen.Count}.";
        }

        private sealed class RepairAction
        {
            public string Description = "";
            public Action Apply = () => { };
        }

        private static List<RepairAction> PlanActions()
        {
            var actions = new List<RepairAction>();
            float skipWindow = OpsOutputConfig.RepairSkipRecentlyChangedSeconds?.Value ?? 6f;
            bool allowTagRewrite = FoundationsConfig.AllowTagRewrite?.Value != false;
            var byUid = new Dictionary<ZDOID, PortalRecord>();
            foreach (PortalRecord r in PortalCensus.Latest) byUid[r.Uid] = r;

            bool RecentlyChanged(PortalRecord record)
            {
                OpsPortalReport? report = OpsOutputReportModel.Latest.FirstOrDefault(r => r.SessionZdoId == record.Uid.ToString());
                if (report == null)
                {
                    return false;
                }
                return (DateTimeOffset.UtcNow - report.LastChangedUtc).TotalSeconds < skipWindow;
            }

            foreach (HealthFinding finding in HealthScanEngine.Findings)
            {
                switch (finding.Category)
                {
                    case "SelfLoop":
                        foreach (ZDOID uid in finding.Portals)
                        {
                            if (!byUid.TryGetValue(uid, out PortalRecord rec) || RecentlyChanged(rec))
                            {
                                continue;
                            }
                            actions.Add(new RepairAction
                            {
                                Description = $"break self-loop at ({rec.Position.x:F0},{rec.Position.y:F0},{rec.Position.z:F0})",
                                Apply = () => ClearConnectionAndReassert(uid),
                            });
                        }
                        break;

                    case "DanglingConnection":
                        foreach (ZDOID uid in finding.Portals)
                        {
                            if (!byUid.TryGetValue(uid, out PortalRecord rec) || RecentlyChanged(rec))
                            {
                                continue;
                            }
                            actions.Add(new RepairAction
                            {
                                Description = $"clear dangling link at ({rec.Position.x:F0},{rec.Position.y:F0},{rec.Position.z:F0})",
                                Apply = () => ClearConnectionAndReassert(uid),
                            });
                        }
                        break;

                    case "OddCountStrand":
                        if (!allowTagRewrite || finding.Portals.Count == 0)
                        {
                            break;
                        }
                        // Deterministic (not necessarily the same member vanilla's own dictionary-bucket
                        // order would leave stranded - that order isn't queryable) pick: the LAST member
                        // by ZDOID, so re-running this plan against an unchanged census is idempotent.
                        ZDOID orphanUid = finding.Portals.OrderBy(u => u.ToString(), StringComparer.Ordinal).Last();
                        if (byUid.TryGetValue(orphanUid, out PortalRecord orphan) && !RecentlyChanged(orphan))
                        {
                            string newTag = SuffixTag(orphan.Tag);
                            actions.Add(new RepairAction
                            {
                                Description = $"retag ({orphan.Position.x:F0},{orphan.Position.y:F0},{orphan.Position.z:F0}) '{orphan.Tag}' -> '{newTag}' [orphan of {finding.Portals.Count}]",
                                Apply = () => Retag(orphanUid, newTag),
                            });
                        }
                        break;
                }
            }

            // Whitespace-normalisation: trimming a tag that would MERGE this portal into an existing
            // (different) group sharing the trimmed form - never a bare cosmetic rewrite of a tag nobody
            // else uses (catalog #71's own "never silently retag a player's gate" caution).
            if (allowTagRewrite)
            {
                var byTrimmed = new Dictionary<string, int>(StringComparer.Ordinal);
                foreach (PortalRecord r in PortalCensus.Latest)
                {
                    string trimmed = r.Tag?.Trim() ?? "";
                    byTrimmed.TryGetValue(trimmed, out int c);
                    byTrimmed[trimmed] = c + 1;
                }
                foreach (PortalRecord r in PortalCensus.Latest)
                {
                    string trimmed = r.Tag?.Trim() ?? "";
                    if (trimmed == r.Tag || RecentlyChanged(r))
                    {
                        continue;
                    }
                    if (byTrimmed.TryGetValue(trimmed, out int othersCount) && othersCount >= 1)
                    {
                        ZDOID uid = r.Uid;
                        string from = r.Tag, to = trimmed;
                        actions.Add(new RepairAction
                        {
                            Description = $"trim '{from}' -> '{to}' at ({r.Position.x:F0},{r.Position.y:F0},{r.Position.z:F0})",
                            Apply = () => Retag(uid, to),
                        });
                    }
                }
            }

            if (OpsOutputConfig.AllowRegistrySurgery?.Value == true)
            {
                foreach ((ZDOID uid, int count) in DetectDuplicateBucketEntries())
                {
                    actions.Add(new RepairAction
                    {
                        Description = $"remove {count - 1} duplicate registry entr{(count - 1 == 1 ? "y" : "ies")} for {uid}",
                        Apply = () => RemoveDuplicateBucketEntries(uid),
                    });
                }
            }

            return actions;
        }

        private static string SuffixTag(string tag)
        {
            for (int i = 2; i < 100; i++)
            {
                string candidate = $"{tag}-{i}";
                if (candidate.Length <= TagCodec.MaxTagLength)
                {
                    return candidate;
                }
                // Tag budget exhausted with the suffix - fall back to truncating the base before suffixing.
                int room = TagCodec.MaxTagLength - $"-{i}".Length;
                if (room > 0)
                {
                    return tag.Substring(0, Math.Min(tag.Length, room)) + $"-{i}";
                }
            }
            return tag; // exhausted every suffix slot - leave it, HealthScanEngine will keep reporting it
        }

        private static void Retag(ZDOID uid, string newTag)
        {
            ZDO? zdo = ZDOMan.instance?.GetZDO(uid);
            if (zdo == null || !zdo.IsValid())
            {
                return;
            }
            PortalOwnership.ClaimAndWrite(zdo, z => z.Set(ZDOVars.s_tag, newTag));
        }

        private static void ClearConnectionAndReassert(ZDOID uid)
        {
            ZDO? zdo = ZDOMan.instance?.GetZDO(uid);
            if (zdo == null || !zdo.IsValid())
            {
                return;
            }
            PortalOwnership.ClaimAndWrite(zdo, z => z.SetConnection(ZDOExtraData.ConnectionType.Portal, ZDOID.None));
            // Re-pair in the same frame instead of waiting up to 5s for vanilla's own reconciler -
            // Game.ConnectPortals() is public/no-arg (SERVER decompile :100589, see ConnectPortalsHook's
            // own doc comment for the confirmation), catalog #71's own suggestion for this exact action.
            try
            {
                Game.instance?.ConnectPortals();
            }
            catch (Exception ex)
            {
                PortalDebug.LogWarning($"[OpsOutputRepairEngine] immediate ConnectPortals() re-pair failed: {ex.Message}");
            }
        }

        /// <summary>
        /// A "duplicate bucket" entry: the same portal ZDOID present more than once across
        /// ZDOMan.GetPortals()'s live Dictionary&lt;SectorIndex, List&lt;ZDO&gt;&gt; (:77643-77646) - not
        /// repairable through any public API per catalog #71's own note, since AddIfPortal's own
        /// dictionary-add path already de-dupes within a single bucket list (:77732-77752); a genuine
        /// duplicate can only arise from another mod's direct manipulation of that dictionary.
        /// </summary>
        private static List<(ZDOID uid, int count)> DetectDuplicateBucketEntries()
        {
            var result = new List<(ZDOID, int)>();
            if (ZDOMan.instance == null)
            {
                return result;
            }
            try
            {
                var counts = new Dictionary<ZDOID, int>();
                foreach (var kvp in ZDOMan.instance.GetPortals())
                {
                    List<ZDO>? list = kvp.Value;
                    if (list == null) continue;
                    foreach (ZDO zdo in list)
                    {
                        if (zdo == null) continue;
                        counts.TryGetValue(zdo.m_uid, out int c);
                        counts[zdo.m_uid] = c + 1;
                    }
                }
                foreach (var kvp in counts)
                {
                    if (kvp.Value > 1)
                    {
                        result.Add((kvp.Key, kvp.Value));
                    }
                }
            }
            catch (Exception ex)
            {
                PortalDebug.LogWarning($"[OpsOutputRepairEngine] duplicate-bucket scan failed: {ex.Message}");
            }
            return result;
        }

        /// <summary>Gated behind AllowRegistrySurgery - mutates ZDOMan's LIVE m_portalObjects dictionary (GetPortals() returns the live object, not a copy). Deliberately narrow: removes extra occurrences of one already-identified ZDOID, nothing else.</summary>
        private static void RemoveDuplicateBucketEntries(ZDOID uid)
        {
            if (ZDOMan.instance == null)
            {
                return;
            }
            int removed = 0;
            bool kept = false; // tracked ACROSS every bucket - the pathology is the same ZDOID present in more than one bucket's list, not just repeated within one list.
            foreach (var kvp in ZDOMan.instance.GetPortals())
            {
                List<ZDO>? list = kvp.Value;
                if (list == null) continue;
                for (int i = list.Count - 1; i >= 0; i--)
                {
                    if (list[i] != null && list[i].m_uid == uid)
                    {
                        if (!kept)
                        {
                            kept = true;
                            continue;
                        }
                        list.RemoveAt(i);
                        removed++;
                    }
                }
            }
            if (removed > 0)
            {
                PortalDebug.LogAlways($"[OpsOutputRepairEngine] AllowRegistrySurgery: removed {removed} duplicate registry entr{(removed == 1 ? "y" : "ies")} for {uid} from ZDOMan.GetPortals().");
                ZDOMan.instance.SetDirtyPortals();
            }
        }
    }
}
