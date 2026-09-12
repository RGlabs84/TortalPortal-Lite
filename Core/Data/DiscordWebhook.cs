using System;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using TortalPortalLite.Core;

namespace TortalPortalLite.Core.Data
{
    /// <summary>
    /// A minimal Discord webhook poster - catalog #84's [Discord] config section (WebhookUrl, Health,
    /// Audit, Destructions, Heartbeat) names this as a shared ops output surface, used by whichever
    /// Foundations engine (AuditEngine, HealthScanEngine, Core.Heartbeat) has something to announce.
    /// Kept in Core/Data rather than under Subsystems/Foundations so Core/Heartbeat.cs (a mod-wide
    /// concern ticked directly from Plugin.Update, independent of the subsystem registry) can call it
    /// without depending on a Subsystems/ folder.
    /// </summary>
    public static class DiscordWebhook
    {
        private static readonly HttpClient Client = new HttpClient();

        public static bool IsConfigured() => !string.IsNullOrWhiteSpace(GlobalConfig.DiscordWebhookUrl?.Value);

        /// <summary>Fire-and-forget - never awaited, never blocks the caller's tick.</summary>
        public static void Send(string content)
        {
            if (!IsConfigured()) return;
            _ = SendAsync(content);
        }

        /// <summary>Blocking with a timeout - used for a shutdown announcement, where fire-and-forget would never actually fire.</summary>
        public static void SendBlocking(string content, int timeoutMs = 3000)
        {
            if (!IsConfigured()) return;
            try
            {
                SendAsync(content).Wait(timeoutMs);
            }
            catch (Exception ex)
            {
                PortalDebug.LogWarning($"[DiscordWebhook] blocking send failed: {ex.Message}");
            }
        }

        private static async Task SendAsync(string content)
        {
            try
            {
                string url = GlobalConfig.DiscordWebhookUrl!.Value;
                string escaped = content.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n");
                var payload = new StringContent($"{{\"content\":\"{escaped}\"}}", Encoding.UTF8, "application/json");
                using HttpResponseMessage response = await Client.PostAsync(url, payload).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    PortalDebug.LogWarning($"[DiscordWebhook] POST returned {(int)response.StatusCode}.");
                }
            }
            catch (Exception ex)
            {
                PortalDebug.LogWarning($"[DiscordWebhook] send failed: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }
}
