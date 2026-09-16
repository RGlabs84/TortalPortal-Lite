using System;
using System.Linq;
using UnityEngine;
using TortalPortalLite.Core;

namespace TortalPortalLite.Subsystems.Foundations
{
    /// <summary>
    /// Ops domain admin verbs, trimmed to this build's surviving engines. Reached via
    /// CommandEngine.Dispatch's `default:` case (Subsystems/Foundations/CommandEngine.cs).
    ///
    /// Verbs: repair [--apply], snapshot [name], snapshots, restore &lt;name&gt; [--recreate], compat,
    /// metrics, barrkbot, report &lt;x&gt; &lt;y&gt; &lt;z&gt;.
    /// </summary>
    public static class OpsOutputAdminCommands
    {
        public static bool TryDispatch(string verb, string[] args, out string response)
        {
            switch (verb)
            {
                case "repair": response = OpsOutputRepairEngine.Run(apply: args.Any(a => a.Equals("--apply", StringComparison.OrdinalIgnoreCase))); return true;
                case "snapshot": response = OpsOutputSnapshotEngine.TakeSnapshot(args.Length > 0 ? args[0] : null); return true;
                case "snapshots": response = ListSnapshots(); return true;
                case "restore": response = Restore(args); return true;
                case "compat": response = OpsOutputCompatEngine.BuildReport(); return true;
                case "metrics": response = MetricsSummary(); return true;
                case "barrkbot": response = BarrkBotExportEngine.ExportNow(); return true;
                case "report": response = ReportAt(args); return true;
                default:
                    response = "";
                    return false;
            }
        }

        private static string ListSnapshots()
        {
            string[] names = OpsOutputSnapshotEngine.ListSnapshots();
            return names.Length == 0 ? "tpl: no snapshots yet." : $"tpl: {names.Length} snapshot(s): {string.Join(", ", names.Take(20))}";
        }

        private static string Restore(string[] args)
        {
            if (args.Length < 1)
            {
                return "tpl: syntax: restore <name> [--recreate]";
            }
            bool recreate = args.Skip(1).Any(a => a.Equals("--recreate", StringComparison.OrdinalIgnoreCase));
            return OpsOutputSnapshotEngine.Restore(args[0], recreate);
        }

        private static string MetricsSummary()
        {
            string top = string.Join(", ", OpsOutputMetricsEngine.TopRoutes(3));
            return "tpl: " +
                $"{OpsOutputMetricsEngine.TotalPortals} portals ({OpsOutputMetricsEngine.ConnectedCount} connected, {OpsOutputMetricsEngine.ManagedCount} managed) | " +
                $"~{OpsOutputMetricsEngine.TransitsPerHourEstimate:F0} transits/hr (heuristic) | " +
                $"{OpsOutputMetricsEngine.NewLastHour} new / {OpsOutputMetricsEngine.DestroyedLastHour} destroyed in the last hour | " +
                $"valheim zdos: {OpsOutputMetricsEngine.VanillaZdoCount}" +
                (top.Length > 0 ? $" | top routes: {top}" : "");
        }

        private static string ReportAt(string[] args)
        {
            if (args.Length < 3 || !float.TryParse(args[0], out float x) || !float.TryParse(args[1], out float y) || !float.TryParse(args[2], out float z))
            {
                return "tpl: syntax: report <x> <y> <z>";
            }
            string id = OpsOutputReportModel.PositionId(new Vector3(x, y, z));
            OpsPortalReport? found = OpsOutputReportModel.Latest.FirstOrDefault(r => r.Id == id);
            if (found == null)
            {
                return $"tpl: no portal at ({x:F1},{y:F1},{z:F1}).";
            }
            return "tpl: " +
                $"tag='{found.Tag}' prefab={found.Prefab} biome={found.Biome} zone=({found.ZoneX},{found.ZoneY}) interior={found.Interior} " +
                $"connected={found.Connected} partner='{found.PartnerTag}' distance={found.Distance:F0}m reciprocal={found.Reciprocal} " +
                $"network='{found.NetworkId}' locked={found.Locked} owner={found.OwnerKind} builder='{found.Builder}' " +
                $"tagAuthor='{found.TagAuthor}' (untrusted) firstSeen={found.FirstSeenUtc:u} lastChanged={found.LastChangedUtc:u}";
        }
    }
}
