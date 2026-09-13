using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using TortalPortalLite.Core;

namespace TortalPortalLite.Subsystems.Foundations
{
    /// <summary>
    /// #76/#203 in-process HTTP endpoint - downgraded to needs-ingame-check per #203: HttpListener is a
    /// System.dll facility no game assembly references anywhere (a full grep of every decompile turns up
    /// zero hits), so unlike outbound HttpClient (vanilla's own ZNet.GetPublicIP proves that works
    /// headless, SERVER decompile :79390-79420) this mod cannot cite proof INBOUND listening works on
    /// Unity's Mono. Ships behind a boot self-probe with the atomic JSON/CSV export as the guaranteed
    /// fallback if the probe fails - nothing else in this mod depends on HTTP.
    ///
    /// Threading contract (non-negotiable): ZDOMan has no locking anywhere, and vanilla's only background
    /// thread (ZNet.SaveWorldThread, :80583) deliberately operates on a clone built on the main thread by
    /// ZDOMan.PrepareSave (:76197-76205). So the listener callback - which runs on a thread-pool thread via
    /// BeginGetContext, never the main thread - touches ONLY the pre-serialised `volatile byte[]`
    /// snapshots below, swapped by the main-thread OnUpdate tick. It must never dereference ZDOMan/ZNet/
    /// ZoneSystem/WorldGenerator/any Unity API directly.
    ///
    /// Bind loopback (127.0.0.1) by default. Binding anything else REQUIRES a non-empty BearerToken or the
    /// listener refuses to start at all - catalog #76's own security model: no write routes exist in v1,
    /// writes go through the file-queue or the admin console, both of which already have an authorization
    /// story.
    /// </summary>
    public static class OpsOutputHttpEngine
    {
        private static HttpListener? _listener;
        private static volatile byte[] _jsonSnapshot = Encoding.UTF8.GetBytes("{}");
        private static volatile byte[] _csvSnapshot = Encoding.UTF8.GetBytes("");
        private static volatile byte[] _metricsSnapshot = Encoding.UTF8.GetBytes("");
        private static volatile bool _healthy = true;
        private static bool _started;
        private static float _timer;

        public static bool IsRunning => _started;

        public static void Initialize()
        {
            if (OpsOutputConfig.HttpEnabled?.Value != true)
            {
                PortalDebug.LogInfo("[OpsOutputHttpEngine] disabled by config (Ops: HTTP Endpoint / Enabled=false) - use the file export (portals.json) instead.");
                return;
            }

            string bind = OpsOutputConfig.HttpBindAddress?.Value ?? "127.0.0.1";
            int port = OpsOutputConfig.HttpPort?.Value ?? 7770;
            string token = OpsOutputConfig.HttpBearerToken?.Value ?? "";
            bool isLoopback = bind is "127.0.0.1" or "localhost" or "::1";

            if (!isLoopback && string.IsNullOrEmpty(token))
            {
                PortalDebug.LogError($"[OpsOutputHttpEngine] refusing to bind non-loopback address '{bind}' with no BearerToken configured - catalog #76's security model. HTTP endpoint disabled; use the file export instead.");
                return;
            }

            // Mono's HttpListener prefix parsing can refuse forms a plain "http://+:port/" or a literal
            // IP handles differently - catalog #203's own guidance: try 127.0.0.1 first, then localhost,
            // reporting which worked rather than assuming one form is universally supported.
            string[] candidates = isLoopback
                ? new[] { $"http://{bind}:{port}/", $"http://localhost:{port}/" }
                : new[] { $"http://{bind}:{port}/" };

            foreach (string prefix in candidates.Distinct())
            {
                if (TryStart(prefix))
                {
                    _started = true;
                    PortalDebug.LogAlways($"[OpsOutputHttpEngine] listening on '{prefix}' (routes: /health, /portals.json, /portals.csv, /metrics).");
                    SelfProbe(prefix);
                    return;
                }
            }

            PortalDebug.LogError("[OpsOutputHttpEngine] every HttpListener prefix failed to bind - catalog #203's predicted needs-ingame-check outcome. Falling back to the file export only (TortalPortalLite.portals.json/.csv).");
        }

        private static bool TryStart(string prefix)
        {
            try
            {
                var listener = new HttpListener();
                listener.Prefixes.Add(prefix);
                listener.Start();
                _listener = listener;
                // BeginGetContext, never a blocking foreground GetContext() - catalog #203's own warning:
                // a blocking accept loop on a foreground thread keeps the process alive after Unity quits.
                _listener.BeginGetContext(OnGetContext, null);
                return true;
            }
            catch (Exception ex)
            {
                PortalDebug.LogWarning($"[OpsOutputHttpEngine] prefix '{prefix}' failed: {ex.GetType().Name}: {ex.Message}");
                try { _listener?.Close(); } catch { /* ignore */ }
                _listener = null;
                return false;
            }
        }

        /// <summary>Outbound self-check on a thread-pool task, exactly mirroring vanilla's own proven-safe outbound HttpClient usage (ZNet.GetPublicIP) - never touches game state, just confirms the loopback round-trip actually works.</summary>
        private static void SelfProbe(string prefix)
        {
            string url = prefix.TrimEnd('/') + "/health";
            _ = Task.Run(async () =>
            {
                try
                {
                    using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
                    HttpResponseMessage resp = await client.GetAsync(url).ConfigureAwait(false);
                    PortalDebug.LogAlways($"[OpsOutputHttpEngine] self-probe GET {url} -> {(int)resp.StatusCode} (httpListener=ok).");
                }
                catch (Exception ex)
                {
                    PortalDebug.LogWarning($"[OpsOutputHttpEngine] self-probe GET {url} failed: {ex.GetType().Name}: {ex.Message} (httpListener may not actually be reachable - check firewall/host).");
                }
            });
        }

        public static void Shutdown()
        {
            try
            {
                _listener?.Stop();
                _listener?.Close();
            }
            catch (Exception ex)
            {
                PortalDebug.LogWarning($"[OpsOutputHttpEngine] shutdown close failed: {ex.Message}");
            }
            _started = false;
            _listener = null;
        }

        public static void OnUpdate(float dt)
        {
            if (!_started)
            {
                return;
            }
            _timer += dt;
            if (_timer < 2f)
            {
                return;
            }
            _timer = 0f;
            RebuildSnapshots();
        }

        private static void RebuildSnapshots()
        {
            try
            {
                var payload = new
                {
                    generatedUtc = DateTime.UtcNow.ToString("o"),
                    portalCount = OpsOutputReportModel.Latest.Count,
                    portals = OpsOutputReportModel.Latest,
                };
                _jsonSnapshot = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(payload));
                _csvSnapshot = Encoding.UTF8.GetBytes(OpsOutputReportModel.BuildCsv(OpsOutputReportModel.Latest));
                _metricsSnapshot = Encoding.UTF8.GetBytes(BuildPrometheusText());
                _healthy = !HealthScanEngine.Findings.Any(f => f.Severity == FindingSeverity.Error);
            }
            catch (Exception ex)
            {
                PortalDebug.LogError($"[OpsOutputHttpEngine] snapshot rebuild failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static string BuildPrometheusText()
        {
            var sb = new StringBuilder();
            sb.Append("# TYPE tplite_portals_total gauge\n");
            sb.Append($"tplite_portals_total {OpsOutputMetricsEngine.TotalPortals}\n");
            sb.Append($"tplite_portals_unconnected {OpsOutputMetricsEngine.UnconnectedCount}\n");
            sb.Append($"tplite_tag_groups {OpsOutputMetricsEngine.ByTag.Count}\n");
            sb.Append($"tplite_transits_total {OpsOutputMetricsEngine.TransitsLastWindow}\n");
            sb.Append($"tplite_repairs_total {OpsOutputRepairEngine.TotalActionsApplied}\n");
            sb.Append($"valheim_zdos_total {OpsOutputMetricsEngine.VanillaZdoCount}\n");
            return sb.ToString();
        }

        /// <summary>
        /// Runs on a THREAD-POOL thread (BeginGetContext's callback), never the main thread. Reads only
        /// the volatile byte[] fields above - see this class's own threading-contract doc comment.
        /// </summary>
        private static void OnGetContext(IAsyncResult ar)
        {
            HttpListener? listener = _listener;
            if (listener == null)
            {
                return;
            }

            HttpListenerContext? ctx = null;
            try
            {
                if (listener.IsListening)
                {
                    ctx = listener.EndGetContext(ar);
                }
            }
            catch (Exception ex)
            {
                PortalDebug.LogWarning($"[OpsOutputHttpEngine] EndGetContext failed: {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                try
                {
                    if (listener.IsListening)
                    {
                        listener.BeginGetContext(OnGetContext, null);
                    }
                }
                catch (Exception ex)
                {
                    PortalDebug.LogWarning($"[OpsOutputHttpEngine] re-arming BeginGetContext failed: {ex.Message}");
                }
            }

            if (ctx == null)
            {
                return;
            }
            try
            {
                HandleRequest(ctx);
            }
            catch (Exception ex)
            {
                PortalDebug.LogWarning($"[OpsOutputHttpEngine] request handling failed: {ex.GetType().Name}: {ex.Message}");
                try { ctx.Response.StatusCode = 500; ctx.Response.Close(); } catch { /* best-effort */ }
            }
        }

        private static void HandleRequest(HttpListenerContext ctx)
        {
            string token = OpsOutputConfig.HttpBearerToken?.Value ?? "";
            if (!string.IsNullOrEmpty(token))
            {
                string? auth = ctx.Request.Headers["Authorization"];
                if (auth != $"Bearer {token}")
                {
                    ctx.Response.StatusCode = 401;
                    ctx.Response.Close();
                    return;
                }
            }

            string path = ctx.Request.Url?.AbsolutePath ?? "/";
            byte[] body;
            string contentType;
            int status = 200;

            switch (path)
            {
                case "/health":
                    body = Encoding.UTF8.GetBytes(_healthy ? "{\"ok\":true}" : "{\"ok\":false}");
                    contentType = "application/json";
                    status = _healthy ? 200 : 503;
                    break;
                case "/portals.json":
                    body = _jsonSnapshot;
                    contentType = "application/json";
                    break;
                case "/portals.csv":
                    body = _csvSnapshot;
                    contentType = "text/csv";
                    break;
                case "/metrics":
                    body = _metricsSnapshot;
                    contentType = "text/plain; version=0.0.4";
                    break;
                default:
                    body = Encoding.UTF8.GetBytes("not found\n");
                    contentType = "text/plain";
                    status = 404;
                    break;
            }

            ctx.Response.StatusCode = status;
            ctx.Response.ContentType = contentType;
            ctx.Response.ContentLength64 = body.Length;
            using System.IO.Stream output = ctx.Response.OutputStream;
            output.Write(body, 0, body.Length);
        }
    }
}
