using System;
using System.Linq;
using UnityEngine;
using TortalPortalLite.Core;

namespace TortalPortalLite.Subsystems.Foundations
{
    /// <summary>
    /// Wave 3 "ops" domain admin verbs (catalog #71/#73/#74/#75/#76/#77/#78/#82/#203). Same situation Wave
    /// 2's access agent documented on AccessAdminCommands: CommandEngine.cs (Wave 0) owns the ONLY patch on
    /// Terminal.TryRunCommand and this wave's rules forbid a second, competing patch on the same vanilla
    /// method AND forbid editing existing Foundations files. This class implements the real verb logic and
    /// is ready to be called, but needs one line added to CommandEngine.Dispatch's `default:` case to
    /// actually be reachable - see OpsOutputConfig.cs's own "NEEDS DISPATCH WIRING" note for the exact
    /// chaining suggestion (try AccessAdminCommands first, then this class, matching the precedent already
    /// wired for Wave 2).
    ///
    /// Verbs: export, map, repair [--apply], snapshot [name], snapshots, restore &lt;name&gt; [--recreate],
    /// networks, compat, metrics, http-status, report &lt;x&gt; &lt;y&gt; &lt;z&gt;.
    /// </summary>
    public static class OpsOutputAdminCommands
    {
        public static bool TryDispatch(string verb, string[] args, out string response)
        {
            switch (verb)
            {
                case "export": response = OpsOutputExportEngine.ExportNow(); return true;
                case "map": response = OpsOutputMapEngine.RenderNow(); return true;
                case "repair": response = OpsOutputRepairEngine.Run(apply: args.Any(a => a.Equals("--apply", StringComparison.OrdinalIgnoreCase))); return true;
                case "snapshot": response = OpsOutputSnapshotEngine.TakeSnapshot(args.Length > 0 ? args[0] : null); return true;
                case "snapshots": response = ListSnapshots(); return true;
                case "restore": response = Restore(args); return true;
                case "networks": response = NetworksStatus(); return true;
                case "compat": response = OpsOutputCompatEngine.BuildReport(); return true;
                case "metrics": response = MetricsSummary(); return true;
                case "http-status": response = HttpStatus(); return true;
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

        private static string NetworksStatus()
        {
            int errors = OpsOutputNetworkValidationEngine.Findings.Count(f => f.Severity == NetworkValidationSeverity.Error);
            int warnings = OpsOutputNetworkValidationEngine.Findings.Count(f => f.Severity == NetworkValidationSeverity.Warning);
            string result = $"tpl: {NetworkModel.Networks.Count} declared network(s), {errors} error(s), {warnings} warning(s).";
            if (errors + warnings > 0)
            {
                var lines = OpsOutputNetworkValidationEngine.Findings.Take(10).Select(f => $"[{f.Severity}] {f.NetworkName}: {f.Detail}");
                result += " " + string.Join(" | ", lines);
            }
            return result;
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

        private static string HttpStatus()
        {
            return OpsOutputHttpEngine.IsRunning
                ? $"tpl: http endpoint running on port {OpsOutputConfig.HttpPort?.Value}."
                : "tpl: http endpoint not running (disabled by config, or the boot probe failed - see log; portals.json/.csv are the fallback).";
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
