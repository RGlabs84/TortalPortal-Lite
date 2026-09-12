using System;
using System.Collections.Generic;
using TortalPortalLite.Core;
using TortalPortalLite.Core.Data;
using TortalPortalLite.Core.Hooks;
using TortalPortalLite.Subsystems.Foundations;

namespace TortalPortalLite.Subsystems.Enforcement
{
    /// <summary>
    /// #49 Tag Watchdog, combined with #47 Tag Author Rewrite and #218 Client-Edit Dirty Guard - all
    /// three are naturally one tick loop over the same census diff. Detect-and-revert, not prevention:
    /// for a window of up to one tick, an unauthorised tag/connection change is live and visible to
    /// other players before this engine reverts it - stated plainly per this option's own honesty
    /// requirement.
    ///
    /// Diffing is tick-to-tick against the PREVIOUS census snapshot (not against AccessAclStore's
    /// longer-lived "approved" record, which exists for cross-restart bookkeeping and other engines to
    /// consult, not as the hot diff baseline) - the same shape AuditEngine already uses for its own,
    /// separate "confirmed vs probable" log, kept independent here because AuditEngine's own state is
    /// private and its job is strictly observational ("AuditEngine never vetoes - it only watches").
    /// This engine needs to actually decide ALLOW/DENY and revert, so it keeps its own recent-sender
    /// cache fed by the same RpcZdoDataHook postfix broker, piggybacked rather than double-patched.
    ///
    /// Sole use of NetworkReassertEngine's reserved key contract: PortalKeys.RecordLocked's own doc
    /// comment already promises "any client-authored change is reverted" for a locked portal - this
    /// engine is the consumer that fulfils that promise for arbitrary player-owned portals (as opposed
    /// to NetworkReassertEngine's job, which is re-asserting ADMIN-DECLARED network topology from
    /// networks.json). Both ultimately call PortalOwnership.ClaimAndWrite, which is idempotent and
    /// therefore safe if the two ever address the same portal in the same tick.
    /// </summary>
    public static class AccessTagWatchdogEngine
    {
        private static readonly Dictionary<ZDOID, (string tag, ZDOID connection)> _lastSeen = new Dictionary<ZDOID, (string, ZDOID)>();
        private static readonly Dictionary<ZDOID, (string hostName, long profileId, float t)> _recentSender = new Dictionary<ZDOID, (string, long, float)>();
        private const float AttributionWindowSeconds = 3f;

        private static float _clock;
        private static float _timer;

        public static void Initialize()
        {
            RpcZdoDataHook.RegisterPostfix(60, OnZdoData);
        }

        private static void OnZdoData(ZNetPeer? sender, ZDOID zdoid)
        {
            if (sender == null || ZDOMan.instance == null)
            {
                return;
            }
            ZDO zdo = ZDOMan.instance.GetZDO(zdoid);
            if (zdo == null || !zdo.IsValid())
            {
                return;
            }

            // Feed the shadow ward index's incremental-update path too - one hook registration, two consumers.
            if (AccessShadowWardIndexEngine.IsWardPrefab(zdo.GetPrefab()))
            {
                AccessShadowWardIndexEngine.OnWardZdoChanged(zdo);
            }

            if (!PortalRegistry.IsPortalPrefabHash(zdo.GetPrefab()))
            {
                return;
            }

            string host = SenderContext.HostNameOf(sender) ?? "";
            long profileId = 0L;
            if (!sender.m_characterID.IsNone())
            {
                ZDO charZdo = ZDOMan.instance.GetZDO(sender.m_characterID);
                if (charZdo != null && charZdo.IsValid())
                {
                    profileId = charZdo.GetLong(ZDOVars.s_playerID, 0L);
                }
            }
            _recentSender[zdoid] = (host, profileId, _clock);
        }

        public static void OnUpdate(float dt)
        {
            _clock += dt;
            _timer += dt;
            float interval = AccessConfig.WatchdogIntervalSeconds?.Value ?? 1.5f;
            if (_timer < interval || ZDOMan.instance == null)
            {
                return;
            }
            _timer = 0f;
            Tick();
        }

        private static void Tick()
        {
            var current = new Dictionary<ZDOID, (string, ZDOID)>();
            bool anyRevert = false;

            foreach (PortalRecord record in PortalCensus.Latest)
            {
                current[record.Uid] = (record.Tag, record.Connection);

                if (_lastSeen.TryGetValue(record.Uid, out var prior))
                {
                    bool tagChanged = prior.Item1 != record.Tag;
                    bool connChanged = prior.Item2 != record.Connection;
                    if (tagChanged || connChanged)
                    {
                        anyRevert |= HandleDrift(record, prior.Item1, prior.Item2, tagChanged, connChanged);
                    }
                }
            }

            _lastSeen.Clear();
            foreach (var kvp in current)
            {
                _lastSeen[kvp.Key] = kvp.Value;
            }

            if (anyRevert)
            {
                // Re-pair immediately rather than waiting up to vanilla's own 5s ConnectPortals cadence.
                Game.instance?.ConnectPortals();
            }
        }

        private static bool HandleDrift(PortalRecord record, string priorTag, ZDOID priorConnection, bool tagChanged, bool connChanged)
        {
            ZDO zdo = ZDOMan.instance.GetZDO(record.Uid);
            if (zdo == null || !zdo.IsValid())
            {
                return false;
            }

            string actorHost = "";
            long actorProfileId = 0L;
            if (_recentSender.TryGetValue(record.Uid, out var rs) && (_clock - rs.t) <= AttributionWindowSeconds)
            {
                actorHost = rs.hostName;
                actorProfileId = rs.profileId;
            }

            bool isAdmin = !string.IsNullOrEmpty(actorHost) && ZNet.instance != null && ZNet.instance.IsAdmin(actorHost);

            // #51 Reserved Tag Namespace - checked before the ACL chain even runs.
            if (tagChanged && AccessReservedNamespaceEngine.IsReserved(record.Tag) && !AccessReservedNamespaceEngine.ActorMayWriteReserved(actorHost))
            {
                Revert(zdo, priorTag, priorConnection, tagChanged, connChanged, actorHost, "reserved tag namespace");
                return true;
            }

            // #61 Retag Rate Limit - before the ACL chain, so a fast attacker can't out-write reverts.
            if (!isAdmin)
            {
                bool ownerOrAdmin = IsOwnerOrAdmin(zdo, actorHost, isAdmin);
                AccessRetagRateLimitEngine.RateDecision rate = AccessRetagRateLimitEngine.Check(actorHost, record.Uid, ownerOrAdmin, out string rateReason);
                if (rate != AccessRetagRateLimitEngine.RateDecision.Allow)
                {
                    Revert(zdo, priorTag, priorConnection, tagChanged, connChanged, actorHost, $"rate limit ({rateReason})");
                    if (rate == AccessRetagRateLimitEngine.RateDecision.DenyEscalate)
                    {
                        AccessOwnershipPinEngine.Pin(zdo, "rate-limit abuse escalation");
                        AccessRetagRateLimitEngine.Escalate(actorHost, rateReason);
                    }
                    return true;
                }
            }

            // #55 Anti-Grief Tag Integrity - tag-squatting collision check.
            if (tagChanged && AccessConfig.SquattingEnabled?.Value != false
                && !AccessAntiGriefTagIntegrityEngine.CheckClaim(record.Tag, actorHost, record.Uid, out string squatReason))
            {
                Revert(zdo, priorTag, priorConnection, tagChanged, connChanged, actorHost, squatReason);
                return true;
            }

            // #53 Portal ACL - the core ownership/lock/network/ward chain.
            AccessPortalAclEngine.Decision decision = AccessPortalAclEngine.Evaluate(zdo, actorHost, actorProfileId, tagChanged, connChanged, out string reason);
            if (decision == AccessPortalAclEngine.Decision.Deny)
            {
                Revert(zdo, priorTag, priorConnection, tagChanged, connChanged, actorHost, reason);
                return true;
            }

            Approve(zdo, record, tagChanged, connChanged, actorHost);
            return false;
        }

        private static bool IsOwnerOrAdmin(ZDO zdo, string actorHost, bool isAdmin)
        {
            if (isAdmin)
            {
                return true;
            }
            string owner = PortalRecordStore.GetOwnerPlatformId(zdo);
            return !string.IsNullOrEmpty(owner) && string.Equals(owner, actorHost, StringComparison.OrdinalIgnoreCase);
        }

        private static void Approve(ZDO zdo, PortalRecord record, bool tagChanged, bool connChanged, string actorHost)
        {
            AccessAclEntry entry = AccessAclStore.GetOrCreate(record.Position);

            if (tagChanged)
            {
                entry.ApprovedTag = record.Tag;
                entry.LastRetagAtTicks = DateTime.UtcNow.Ticks;
                entry.RetagCount++;
                AccessRetagRateLimitEngine.RecordPortalCooldown(record.Uid);

                // #47 Tag Author Rewrite - overwrite s_tagauthor with the verified account, never the
                // client-supplied value TeleportWorld.RPC_SetTag writes verbatim.
                if (AccessConfig.WatchdogRewriteTagAuthor?.Value != false && !string.IsNullOrEmpty(actorHost))
                {
                    PortalOwnership.ClaimAndWrite(zdo, z => z.Set(ZDOVars.s_tagauthor, actorHost));
                }
            }

            if (connChanged)
            {
                entry.HasApprovedPartner = record.Connection != ZDOID.None;
                if (entry.HasApprovedPartner && PortalCensus.TryGet(record.Connection, out PortalRecord partner))
                {
                    entry.PartnerX = partner.Position.x;
                    entry.PartnerY = partner.Position.y;
                    entry.PartnerZ = partner.Position.z;
                }
            }

            AccessAclStore.MarkDirty();

            // #218 Client-Edit Dirty Guard - AddIfPortal's early-return means a tag-only change on an
            // already-registered portal never sets DirtyPortalObjects on its own; an unconnected
            // portal's rename in particular has no other path to get dirtied at all.
            ZDOMan.instance.SetDirtyPortals();
        }

        private static void Revert(ZDO zdo, string priorTag, ZDOID priorConnection, bool tagChanged, bool connChanged, string actorHost, string reason)
        {
            if (tagChanged)
            {
                AccessOwnerDelegatedRetagEngine.RevertTag(zdo, priorTag, actorHost);
            }
            if (connChanged)
            {
                AccessOwnerDelegatedRetagEngine.RevertConnection(zdo, priorConnection);
            }
            ZDOMan.instance.SetDirtyPortals();

            if (!string.IsNullOrEmpty(actorHost) && AccessConfig.WatchdogRewriteTagAuthor?.Value != false)
            {
                NotifyActor(actorHost, $"Reverted: {reason}");
            }
            PortalDebug.LogAlways($"[AccessTagWatchdogEngine] REVERTED {zdo.m_uid}: {reason} (actor: {(string.IsNullOrEmpty(actorHost) ? "unknown" : actorHost)})");
        }

        private static void NotifyActor(string hostName, string message)
        {
            foreach (ConnectedCharacter who in ConnectedCharacters.All())
            {
                if (SenderContext.HostNameOf(who.Peer) == hostName)
                {
                    PlayerNotify.Toast(who, message);
                    return;
                }
            }
        }
    }
}
