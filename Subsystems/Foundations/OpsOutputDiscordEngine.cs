using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using TortalPortalLite.Core;
using TortalPortalLite.Core.Data;

namespace TortalPortalLite.Subsystems.Foundations
{
    /// <summary>
    /// #75 Output surface: Discord webhook reporting. Every post goes through Core.Data.DiscordWebhook
    /// (already shipped, forked from Wonderland verbatim) - fire-and-forget Send() for everything except
    /// the shutdown lifecycle message (SendBlocking, since the process can exit before a fire-and-forget
    /// task runs). Rate-limited/coalesced IN THIS MOD, not relying on Discord's own 429s, per #75's own
    /// warning.
    ///
    /// ZDOMan.m_onZDODestroyed (:76033) is an INSTANCE field on the live ZDOMan, only combinable once
    /// ZDOMan.instance exists (well after this mod's own Initialize()/BepInEx Awake) - subscribed lazily,
    /// retried once per tick from OnUpdate until it succeeds, the same tolerant pattern CapabilityProbe
    /// uses for exactly this "singleton not ready yet" situation. Always Delegate.Combine, never assign -
    /// catalog #82's own explicit warning: ZNetScene.Awake already attaches its own handler to the same
    /// field (:81953), and an assignment there would silently disable ZNetScene's own destruction handling
    /// server-wide.
    /// </summary>
    public static class OpsOutputDiscordEngine
    {
        private static bool _destroyHookInstalled;
        private static List<HealthFinding> _lastHealthSignature = new List<HealthFinding>();
        private static IReadOnlyList<NetworkDefinition>? _lastNetworksRef;
        private static int _lastAuditCount;
        private static float _summaryTimer;
        private static readonly Queue<float> _recentSendTimes = new Queue<float>();

        public static void Initialize()
        {
            TrySubscribeDestroy();
            if (OpsOutputConfig.DiscordServerLifecycle?.Value == true)
            {
                DiscordWebhook.Send(":green_circle: portal engine online.");
            }
        }

        public static void OnUpdate(float dt)
        {
            if (!_destroyHookInstalled)
            {
                TrySubscribeDestroy();
            }

            CheckHealthDigest();
            CheckNetworkReload();
            CheckAuditStream();

            if (OpsOutputConfig.DiscordPeriodicSummary?.Value == true)
            {
                _summaryTimer += dt;
                float interval = (OpsOutputConfig.DiscordSummaryIntervalMinutes?.Value ?? 60f) * 60f;
                if (_summaryTimer >= interval)
                {
                    _summaryTimer = 0f;
                    PostSummary();
                }
            }
        }

        /// <summary>Called from Plugin.OnDestroy's own DiscordWebhook.SendBlocking lifecycle line - the orchestrator can add this call there alongside it. Safe to call even if never wired (simply never fires).</summary>
        public static void NotifyOffline()
        {
            if (OpsOutputConfig.DiscordServerLifecycle?.Value == true)
            {
                DiscordWebhook.SendBlocking(":red_circle: portal engine offline.");
            }
        }

        private static void TrySubscribeDestroy()
        {
            if (ZDOMan.instance == null)
            {
                return;
            }
            try
            {
                ZDOMan.instance.m_onZDODestroyed = (Action<ZDO>)Delegate.Combine(ZDOMan.instance.m_onZDODestroyed, new Action<ZDO>(OnZdoDestroyed));
                _destroyHookInstalled = true;
            }
            catch (Exception ex)
            {
                PortalDebug.LogError($"[OpsOutputDiscordEngine] failed to subscribe to ZDOMan.m_onZDODestroyed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static void OnZdoDestroyed(ZDO zdo)
        {
            try
            {
                if (OpsOutputConfig.DiscordDestructionNotices?.Value != true || Game.instance == null)
                {
                    return;
                }
                if (!Game.instance.PortalPrefabHash.Contains(zdo.GetPrefab()))
                {
                    return;
                }
                string tag = zdo.GetString(ZDOVars.s_tag, "");
                Vector3 pos = zdo.GetPosition();
                PostRateLimited($":boom: portal destroyed - tag '{tag}' at ({pos.x:F0},{pos.y:F0},{pos.z:F0}).");
            }
            catch (Exception ex)
            {
                PortalDebug.LogWarning($"[OpsOutputDiscordEngine] destruction notice failed: {ex.Message}");
            }
        }

        private static void CheckHealthDigest()
        {
            if (OpsOutputConfig.DiscordHealthDigest?.Value != true)
            {
                return;
            }
            IReadOnlyList<HealthFinding> current = HealthScanEngine.Findings;
            if (SameFindings(current, _lastHealthSignature))
            {
                return;
            }
            _lastHealthSignature = current.ToList();
            if (current.Count == 0)
            {
                return; // no "all clear" spam by default - the signature is still updated so a later regression posts again
            }
            var lines = current.Take(10).Select(f => $"{(f.Severity == FindingSeverity.Error ? ":x:" : ":warning:")} {f.Category}: {f.Detail}");
            PostRateLimited("**Portal health**\n" + string.Join("\n", lines));
        }

        private static bool SameFindings(IReadOnlyList<HealthFinding> a, List<HealthFinding> b)
        {
            if (a.Count != b.Count)
            {
                return false;
            }
            for (int i = 0; i < a.Count; i++)
            {
                if (a[i].Category != b[i].Category || a[i].Detail != b[i].Detail)
                {
                    return false;
                }
            }
            return true;
        }

        private static void CheckNetworkReload()
        {
            if (OpsOutputConfig.DiscordNetworkReload?.Value != true)
            {
                return;
            }
            IReadOnlyList<NetworkDefinition> current = NetworkModel.Networks;
            if (ReferenceEquals(current, _lastNetworksRef))
            {
                return;
            }
            bool firstObservation = _lastNetworksRef == null;
            _lastNetworksRef = current;
            if (firstObservation)
            {
                return;
            }
            int errCount = OpsOutputNetworkValidationEngine.Findings.Count(f => f.Severity == NetworkValidationSeverity.Error);
            PostRateLimited($"networks.json reloaded: {current.Count} network(s), {errCount} validation error(s).");
        }

        private static void CheckAuditStream()
        {
            if (OpsOutputConfig.DiscordAuditStream?.Value != true)
            {
                return;
            }
            IReadOnlyCollection<AuditEntry> entries = AuditEngine.Entries;
            if (entries.Count <= _lastAuditCount)
            {
                _lastAuditCount = entries.Count;
                return;
            }
            var newOnes = entries.Skip(_lastAuditCount).Take(10).ToList();
            _lastAuditCount = entries.Count;
            foreach (AuditEntry e in newOnes)
            {
                PostRateLimited($"[{(e.Attributed ? "confirmed" : "probable")}] {e.Portal} {e.Change} (actor: {e.Actor})");
            }
        }

        private static void PostSummary()
        {
            string msg = $":globe_with_meridians: Portal summary - {OpsOutputMetricsEngine.TotalPortals} portals / {OpsOutputMetricsEngine.ByTag.Count} tags, ~{OpsOutputMetricsEngine.TransitsPerHourEstimate:F0} transits/hr (heuristic).";
            var top = OpsOutputMetricsEngine.TopRoutes(3).ToList();
            if (top.Count > 0)
            {
                msg += " Top routes: " + string.Join(", ", top);
            }
            PostRateLimited(msg);
        }

        private static void PostRateLimited(string message)
        {
            if (!DiscordWebhook.IsConfigured())
            {
                return;
            }
            float now = Time.realtimeSinceStartup;
            while (_recentSendTimes.Count > 0 && now - _recentSendTimes.Peek() > 60f)
            {
                _recentSendTimes.Dequeue();
            }
            int cap = Math.Max(1, OpsOutputConfig.DiscordMaxMessagesPerMinute?.Value ?? 6);
            if (_recentSendTimes.Count >= cap)
            {
                PortalDebug.LogWarning("[OpsOutputDiscordEngine] rate limit hit - dropping a Discord post this minute.");
                return;
            }
            _recentSendTimes.Enqueue(now);
            DiscordWebhook.Send(message);
        }
    }
}
