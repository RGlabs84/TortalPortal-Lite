using System;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using TortalPortalLite.Core;

namespace TortalPortalLite.Subsystems.Foundations
{
    /// <summary>
    /// #74 Output surface: atomic JSON/CSV file export. Serialises OpsOutputReportModel's DTO (plus the
    /// current health findings and metrics window) to "&lt;config&gt;/TortalPortalLite.portals.json" and
    /// "...portals.csv", both via OpsOutputAtomicFile's temp-then-replace pattern so an external reader
    /// (a cron rsync, a `tail -f`, an HTTP GET landing mid-write) never observes a torn file.
    ///
    /// Cadence: on a timer (FoundationsConfig.ExportIntervalSeconds, already bound at Wave 0), on demand
    /// (tplite export), and once from ZNet.WorldSaveStarted (:78989, invoked :80524 on the main thread
    /// BEFORE PrepareSave) so the export is always consistent with the world file about to hit disk.
    /// CSV columns match #74's own worked spec exactly: id,tag,x,y,z,biome,zone,prefab,connected,
    /// partnerId,partnerTag,distance,reciprocal,network,ownerPeer,builder,tagAuthor,firstSeen,lastChanged.
    /// </summary>
    public static class OpsOutputExportEngine
    {
        private static float _timer;
        private static bool _worldSaveHookInstalled;

        public static void Initialize()
        {
            try
            {
                ZNet.WorldSaveStarted = (Action)Delegate.Combine(ZNet.WorldSaveStarted, new Action(OnWorldSaveStarted));
                _worldSaveHookInstalled = true;
            }
            catch (Exception ex)
            {
                _worldSaveHookInstalled = false;
                PortalDebug.LogError($"[OpsOutputExportEngine] failed to subscribe to ZNet.WorldSaveStarted: {ex.GetType().Name}: {ex.Message}");
            }
        }

        public static void Shutdown()
        {
            if (_worldSaveHookInstalled)
            {
                try { ZNet.WorldSaveStarted = (Action)Delegate.Remove(ZNet.WorldSaveStarted, new Action(OnWorldSaveStarted)); }
                catch { /* best-effort */ }
            }
        }

        public static void OnUpdate(float dt)
        {
            _timer += dt;
            float interval = FoundationsConfig.ExportIntervalSeconds?.Value ?? 300f;
            if (_timer < interval)
            {
                return;
            }
            _timer = 0f;
            ExportNow();
        }

        private static void OnWorldSaveStarted()
        {
            try { ExportNow(); }
            catch (Exception ex) { PortalDebug.LogError($"[OpsOutputExportEngine] pre-save export failed: {ex.Message}"); }
        }

        private static string JsonPath => Path.Combine(OpsOutputConfig.PluginConfigDir, "TortalPortalLite.portals.json");
        private static string CsvPath => Path.Combine(OpsOutputConfig.PluginConfigDir, "TortalPortalLite.portals.csv");

        public static string ExportNow()
        {
            if (FoundationsConfig.ExportJson?.Value == false)
            {
                return "tpl: export disabled (Foundations 'Export.Json' is off).";
            }

            OpsOutputReportModel.ForceRebuild();
            int written = 0;
            var errors = new StringBuilder();

            try
            {
                var payload = new
                {
                    generatedUtc = DateTime.UtcNow.ToString("o"),
                    portalCount = OpsOutputReportModel.Latest.Count,
                    metrics = new
                    {
                        OpsOutputMetricsEngine.TotalPortals,
                        OpsOutputMetricsEngine.ConnectedCount,
                        OpsOutputMetricsEngine.UnconnectedCount,
                        OpsOutputMetricsEngine.ManagedCount,
                        OpsOutputMetricsEngine.UnmanagedCount,
                        OpsOutputMetricsEngine.TransitsLastWindow,
                        OpsOutputMetricsEngine.TransitsPerHourEstimate,
                        OpsOutputMetricsEngine.VanillaZdoCount,
                    },
                    health = HealthScanEngine.Findings,
                    networkValidation = OpsOutputNetworkValidationEngine.Findings,
                    portals = OpsOutputReportModel.Latest,
                };
                string json = JsonConvert.SerializeObject(payload, Formatting.Indented);
                if (OpsOutputAtomicFile.TryWriteAllText(JsonPath, json, out string? err))
                {
                    written++;
                }
                else
                {
                    errors.Append($"json: {err}; ");
                }
            }
            catch (Exception ex)
            {
                errors.Append($"json: {ex.GetType().Name}: {ex.Message}; ");
            }

            if (OpsOutputConfig.ExportCsv?.Value != false)
            {
                try
                {
                    string csv = OpsOutputReportModel.BuildCsv(OpsOutputReportModel.Latest);
                    if (OpsOutputAtomicFile.TryWriteAllText(CsvPath, csv, out string? err))
                    {
                        written++;
                    }
                    else
                    {
                        errors.Append($"csv: {err}; ");
                    }
                }
                catch (Exception ex)
                {
                    errors.Append($"csv: {ex.GetType().Name}: {ex.Message}; ");
                }
            }

            string result = $"tpl: export wrote {written} file(s) ({OpsOutputReportModel.Latest.Count} portal(s))" + (errors.Length > 0 ? $" - errors: {errors}" : ".");
            PortalDebug.LogInfo($"[OpsOutputExportEngine] {result}");
            return result;
        }

    }
}
