using System;
using System.Collections.Generic;
using TortalPortalLite.Core;

namespace TortalPortalLite.Subsystems.Enforcement
{
    /// <summary>
    /// #61 Retag Rate Limit. A per-account token bucket (rolling window of recent change timestamps)
    /// plus a per-portal cooldown, consulted by the Tag Watchdog's policy chain BEFORE the Portal ACL
    /// chain even runs a DENY through it - a bucket exhaustion is reactive-detection-only by itself
    /// (stage 1: revert), and only becomes server-enforced at stage 2 (escalate to the Ownership Pin,
    /// which AccessOwnershipPinEngine.Pin(...) performs) or stage 3 (kick, ZNet.instance.Kick - both
    /// public, server-side, resolve by the socket-verified host name exactly like vanilla's own
    /// adminlist check).
    ///
    /// The per-portal cooldown deliberately exempts the portal's own owner/admins (#61's own failure
    /// mode: "a griefer retags your portal once, and your own corrective retag is refused for T
    /// seconds" - callers must pass isOwnerOrAdmin=true for the cooldown to be bypassed).
    /// </summary>
    public static class AccessRetagRateLimitEngine
    {
        public enum RateDecision { Allow, DenySoft, DenyEscalate }

        private sealed class AccountBucket
        {
            public readonly Queue<DateTime> Changes = new Queue<DateTime>();
            public int AbuseStrikes;
            public DateTime LastStrikeUtc;
        }

        private static readonly Dictionary<string, AccountBucket> _byAccount = new Dictionary<string, AccountBucket>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<ZDOID, DateTime> _portalCooldownUntil = new Dictionary<ZDOID, DateTime>();

        /// <summary>Call before allowing a tag/connection write to actually land. Records the attempt regardless of the verdict.</summary>
        public static RateDecision Check(string actorPlatformId, ZDOID portalUid, bool isOwnerOrAdmin, out string reason)
        {
            DateTime now = DateTime.UtcNow;
            float cooldown = AccessConfig.RateLimitPortalCooldownSeconds?.Value ?? 20f;

            if (!isOwnerOrAdmin && cooldown > 0f && _portalCooldownUntil.TryGetValue(portalUid, out DateTime until) && now < until)
            {
                reason = $"portal cooldown active for another {(until - now).TotalSeconds:F0}s";
                return RateDecision.DenySoft;
            }

            if (string.IsNullOrEmpty(actorPlatformId))
            {
                reason = "unattributed change (no per-account bucket to charge)";
                return RateDecision.Allow; // per-portal cooldown is the only defence available for an unattributable change - see #61's own failure mode.
            }

            if (!_byAccount.TryGetValue(actorPlatformId, out AccountBucket bucket))
            {
                bucket = new AccountBucket();
                _byAccount[actorPlatformId] = bucket;
            }

            float windowSeconds = AccessConfig.RateLimitWindowSeconds?.Value ?? 60f;
            while (bucket.Changes.Count > 0 && (now - bucket.Changes.Peek()).TotalSeconds > windowSeconds)
            {
                bucket.Changes.Dequeue();
            }

            int limit = AccessConfig.RateLimitTagChangesPerWindow?.Value ?? 5;
            if (bucket.Changes.Count >= limit)
            {
                bucket.AbuseStrikes++;
                bucket.LastStrikeUtc = now;
                int kickAt = AccessConfig.RateLimitAbuseCountBeforeKick?.Value ?? 30;
                reason = $"exceeded {limit} changes / {windowSeconds:F0}s (strike {bucket.AbuseStrikes}/{kickAt})";
                return bucket.AbuseStrikes >= kickAt ? RateDecision.DenyEscalate : RateDecision.DenySoft;
            }

            bucket.Changes.Enqueue(now);
            // Long quiet periods forgive past strikes - this is abuse detection, not a permanent record.
            if (bucket.AbuseStrikes > 0 && (now - bucket.LastStrikeUtc).TotalSeconds > windowSeconds * 20)
            {
                bucket.AbuseStrikes = 0;
            }
            reason = "within budget";
            return RateDecision.Allow;
        }

        public static void RecordPortalCooldown(ZDOID portalUid)
        {
            float cooldown = AccessConfig.RateLimitPortalCooldownSeconds?.Value ?? 20f;
            if (cooldown > 0f)
            {
                _portalCooldownUntil[portalUid] = DateTime.UtcNow.AddSeconds(cooldown);
            }
        }

        /// <summary>Stage 3 escalation - kick by the socket-verified host name, exactly like the adminlist check.</summary>
        public static void Escalate(string actorPlatformId, string reasonForLog)
        {
            try
            {
                PortalDebug.LogAlways($"[AccessRetagRateLimitEngine] escalating - kicking {actorPlatformId}: {reasonForLog}");
                ZNet.instance?.Kick(actorPlatformId);
            }
            catch (Exception ex)
            {
                PortalDebug.LogWarning($"[AccessRetagRateLimitEngine] kick failed for {actorPlatformId}: {ex.Message}");
            }
        }
    }
}
