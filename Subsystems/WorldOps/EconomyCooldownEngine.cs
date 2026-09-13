using System.Collections.Generic;
using TortalPortalLite.Core;
using TortalPortalLite.Core.Data;
using TortalPortalLite.Subsystems.Topology;

namespace TortalPortalLite.Subsystems.WorldOps
{
    /// <summary>Admin declaration for one rate-limited portal (economy.json section "cooldowns"). Mode selects which of #96's three variants applies.</summary>
    public sealed class EconomyCooldownDeclaration
    {
        public EconomyPosition Portal = new EconomyPosition();
        public EconomyPosition? Destination;
        public string Label = "GATE";

        /// <summary>"perTransit" | "quota" | "schedule".</summary>
        public string Mode = "perTransit";

        /// <summary>perTransit: seconds the gate stays parked after each detected transit.</summary>
        public float CooldownSeconds = 60f;

        /// <summary>quota: portals sharing the same non-empty NetworkKey share one rolling counter.</summary>
        public string NetworkKey = "";
        public int QuotaMax = 8;
        public float QuotaWindowSeconds = 3600f;

        /// <summary>schedule: EnvMan.GetDayFraction() window (0..1) during which the gate is open. Open supports wraparound (From > To means "crosses midnight").</summary>
        public float ScheduleOpenFraction = 0f;
        public float ScheduleCloseFraction = 0.2f;
    }

    /// <summary>
    /// #96 Cooldowns and Throughput Quotas. Three variants, all genuinely server-enforced because each is
    /// a pure function of state the server owns (ZNet.GetTimeSeconds()'s monotonic world clock, or
    /// EnvMan.GetDayFraction() for the scheduled-service variant, catalog's own preferred illustration of
    /// this domain's boundary: "needs no detection at all, cannot be gamed, cannot false-positive").
    /// State (cooldown-until timestamps, quota window start/hits) lives in EconomyStateStore rather than
    /// a new ZDO key, per this wave's "do not invent new PortalKeys.cs entries" rule.
    /// </summary>
    public static class EconomyCooldownEngine
    {
        private static int _lastVersion = -1;
        private static List<EconomyCooldownDeclaration> _cooldowns = new List<EconomyCooldownDeclaration>();
        private static readonly RoutingTransitDetector _detector = new RoutingTransitDetector();
        private static float _timer;

        public static void OnUpdate(float dt)
        {
            RefreshIfNeeded();
            _timer += dt;
            float interval = EconomyConfig.CooldownEvalSeconds?.Value ?? 1f;
            if (_timer < interval || _cooldowns.Count == 0 || ZNet.instance == null)
            {
                return;
            }
            _timer = 0f;

            double now = ZNet.instance.GetTimeSeconds();

            // perTransit: detect transits through gates in perTransit mode and stamp a fresh cooldown.
            var perTransitByUid = new Dictionary<ZDOID, EconomyCooldownDeclaration>();
            foreach (EconomyCooldownDeclaration decl in _cooldowns)
            {
                if (decl.Mode != "perTransit")
                {
                    continue;
                }
                ZDO? gz = EconomyWriteOps.ResolveLivePortal(decl.Portal);
                if (gz != null)
                {
                    perTransitByUid[gz.m_uid] = decl;
                }
            }
            if (perTransitByUid.Count > 0)
            {
                foreach (RoutingTransitDetector.Transit transit in _detector.Poll(EconomyConfig.TransitStraddleRadius?.Value ?? 8f))
                {
                    if (perTransitByUid.TryGetValue(transit.From.Uid, out EconomyCooldownDeclaration decl))
                    {
                        // #99 Physical Tiers feeds "cooldown length" directly, per that option's own
                        // "what tier buys" list - a higher-tier gate (more structure/ward investment)
                        // recharges faster. Tier 0 = full declared cooldown; each tier halves it, floored
                        // at 10% of the base so a maxed-tier gate is fast but never instantaneous.
                        int tier = EconomyTierEngine.TierOf(decl.Portal.ToVector3());
                        float scaledCooldown = decl.CooldownSeconds * System.Math.Max(0.1f, 1f / (1 + tier));
                        string key = EconomyStateStore.PositionKey("cooldown", decl.Portal.ToVector3());
                        EconomyStateStore.SetLong(key, (long)((now + scaledCooldown) * 1000.0));
                    }
                }
            }
            else
            {
                _detector.Poll(EconomyConfig.TransitStraddleRadius?.Value ?? 8f);
            }

            // quota: count perTransit-independent hits per network key within a rolling window.
            var quotaHitsByUid = new Dictionary<ZDOID, string>();
            foreach (EconomyCooldownDeclaration decl in _cooldowns)
            {
                if (decl.Mode != "quota" || string.IsNullOrEmpty(decl.NetworkKey))
                {
                    continue;
                }
                ZDO? gz = EconomyWriteOps.ResolveLivePortal(decl.Portal);
                if (gz != null)
                {
                    quotaHitsByUid[gz.m_uid] = decl.NetworkKey;
                }
            }
            if (quotaHitsByUid.Count > 0)
            {
                foreach (RoutingTransitDetector.Transit transit in _detector.Poll(EconomyConfig.TransitStraddleRadius?.Value ?? 8f))
                {
                    if (quotaHitsByUid.TryGetValue(transit.From.Uid, out string networkKey))
                    {
                        RegisterQuotaHit(networkKey, now);
                    }
                }
            }

            foreach (EconomyCooldownDeclaration decl in _cooldowns)
            {
                Evaluate(decl, now);
            }
        }

        private static void RefreshIfNeeded()
        {
            if (EconomyRegistry.Version != _lastVersion)
            {
                _lastVersion = EconomyRegistry.Version;
                _cooldowns = EconomyRegistry.Section<EconomyCooldownDeclaration>("cooldowns");
            }
        }

        private static void RegisterQuotaHit(string networkKey, double now)
        {
            string startKey = $"quotastart:{networkKey}";
            string hitsKey = $"quotahits:{networkKey}";
            long windowStartMs = EconomyStateStore.GetLong(startKey, 0L);
            if (windowStartMs == 0L)
            {
                EconomyStateStore.SetLong(startKey, (long)(now * 1000.0));
                EconomyStateStore.SetLong(hitsKey, 1L);
                return;
            }
            EconomyStateStore.SetLong(hitsKey, EconomyStateStore.GetLong(hitsKey, 0L) + 1L);
        }

        private static void Evaluate(EconomyCooldownDeclaration decl, double now)
        {
            ZDO? gateZdo = EconomyWriteOps.ResolveLivePortal(decl.Portal);
            if (gateZdo == null)
            {
                return;
            }
            ZDOID gateUid = gateZdo.m_uid;

            if (decl.Destination != null)
            {
                ZDO? destZdo = EconomyWriteOps.ResolveLivePortal(decl.Destination);
                if (destZdo != null)
                {
                    EconomyRoutingKernel.SetDestination(gateUid, destZdo.m_uid);
                }
            }

            switch (decl.Mode)
            {
                case "quota":
                    EvaluateQuota(decl, gateUid, now);
                    break;
                case "schedule":
                    EvaluateSchedule(decl, gateUid);
                    break;
                default:
                    EvaluatePerTransit(decl, gateUid, now);
                    break;
            }
        }

        private static void EvaluatePerTransit(EconomyCooldownDeclaration decl, ZDOID gateUid, double now)
        {
            string key = EconomyStateStore.PositionKey("cooldown", decl.Portal.ToVector3());
            long untilMs = EconomyStateStore.GetLong(key, 0L);
            double until = untilMs / 1000.0;
            bool open = now >= until;
            string tag = open ? decl.Label : $"{decl.Label} cd {System.Math.Max(0, (int)(until - now))}s";
            EconomyRoutingKernel.Publish(gateUid, "cooldown", open, tag, 10);
        }

        private static void EvaluateQuota(EconomyCooldownDeclaration decl, ZDOID gateUid, double now)
        {
            string startKey = $"quotastart:{decl.NetworkKey}";
            string hitsKey = $"quotahits:{decl.NetworkKey}";
            long windowStartMs = EconomyStateStore.GetLong(startKey, 0L);
            double windowStart = windowStartMs / 1000.0;
            if (windowStartMs != 0L && now - windowStart >= decl.QuotaWindowSeconds)
            {
                // Window rolled over - reset.
                EconomyStateStore.RemoveLong(startKey);
                EconomyStateStore.RemoveLong(hitsKey);
            }
            long hits = EconomyStateStore.GetLong(hitsKey, 0L);
            bool open = hits < decl.QuotaMax;
            string tag = $"{decl.Label} {hits}/{decl.QuotaMax}";
            EconomyRoutingKernel.Publish(gateUid, "cooldown", open, tag, 10);
        }

        private static void EvaluateSchedule(EconomyCooldownDeclaration decl, ZDOID gateUid)
        {
            if (EnvMan.instance == null)
            {
                return;
            }
            float frac = EnvMan.instance.GetDayFraction();
            bool inWindow = decl.ScheduleOpenFraction <= decl.ScheduleCloseFraction
                ? frac >= decl.ScheduleOpenFraction && frac < decl.ScheduleCloseFraction
                : frac >= decl.ScheduleOpenFraction || frac < decl.ScheduleCloseFraction;
            string tag = inWindow ? $"{decl.Label}" : $"{decl.Label} sealed";
            EconomyRoutingKernel.Publish(gateUid, "cooldown", inWindow, tag, 10);
        }
    }
}
