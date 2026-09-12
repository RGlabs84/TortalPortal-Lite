using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using TortalPortalLite.Core;
using TortalPortalLite.Core.Data;
using TortalPortalLite.Subsystems.Foundations;

namespace TortalPortalLite.Subsystems.Enforcement
{
    /// <summary>
    /// #53 Portal ACL. Two responsibilities: (1) the policy chain the Tag Watchdog (#49) consults for
    /// every proposed tag/connection change, decoupled from wards so it works anywhere; (2) the emote-
    /// driven mutation interface, since chat is not a channel a solo player ever exercises
    /// (Chat.SendText targets each OTHER listed player individually - a lone player generates zero
    /// packets) and slash commands never leave the client (Chat.InputText runs them as a local console
    /// command with silentFail) - emotes are the one interface a stock client always delivers
    /// (EmoteSignals), exactly as the option's own howItWorks specifies.
    ///
    /// Storage split: PortalRecordStore (#50, Foundations) is authoritative for owner/network/the coarse
    /// locked flag - this class is a CONSUMER of it, never a second writer of those same fields. Co-
    /// owners, lock granularity and the approved-tag/partner reference state live in AccessAclStore (see
    /// its own doc comment for why - no ZDO key exists for them and this wave does not mint new ones).
    /// </summary>
    public static class AccessPortalAclEngine
    {
        public enum Decision { Allow, Deny }

        public static void Initialize()
        {
            EmoteSignals.Register(OnEmote);
        }

        // ------------------------------------------------------------------
        // Policy chain (consumed by AccessTagWatchdogEngine and friends)
        // ------------------------------------------------------------------

        /// <summary>
        /// The 7-step chain from #53's own howItWorks. <paramref name="actorProfileId"/> (the acting
        /// account's own PlayerProfile.m_playerID, resolved from their character ZDO where available) is
        /// used only for the ward check, which is keyed on that long id, not the platform string - pass
        /// 0 if unresolvable (the ward step is simply skipped, never a false ALLOW).
        ///
        /// <paramref name="allowMutation"/> defaults to true for the normal "a change is actually being
        /// applied" callers (the Tag Watchdog, the Destroy Veto). Pass false for a pure VISIBILITY check
        /// (AccessPerPeerWithholdingEngine's #211 decision) - step 6 (unclaimed -> auto-adopt) must not
        /// silently claim a portal for an account just because something asked "would they be allowed to
        /// see/change this", which merely walking near a portal should never trigger.
        /// </summary>
        public static Decision Evaluate(ZDO portalZdo, string actorPlatformId, long actorProfileId, bool isTagChange, bool isConnectionChange, out string reason, bool allowMutation = true)
        {
            // 1. Admins bypass everything.
            if (!string.IsNullOrEmpty(actorPlatformId) && ZNet.instance != null && ZNet.instance.IsAdmin(actorPlatformId))
            {
                reason = "admin";
                return Decision.Allow;
            }

            AccessAclStore.TryGet(portalZdo.GetPosition(), out AccessAclEntry entry);
            AccessLockLevel lockLevel = entry?.Lock ?? (PortalRecordStore.IsLocked(portalZdo) ? AccessLockLevel.Full : AccessLockLevel.None);

            // 2. Lock level.
            bool lockBlocks = lockLevel == AccessLockLevel.Full
                || (lockLevel == AccessLockLevel.Retag && isTagChange)
                || (lockLevel == AccessLockLevel.Relink && isConnectionChange);

            string owner = PortalRecordStore.GetOwnerPlatformId(portalZdo);
            bool isOwner = !string.IsNullOrEmpty(owner) && string.Equals(owner, actorPlatformId, StringComparison.OrdinalIgnoreCase);
            bool isCoOwner = entry != null && entry.CoOwners.Contains(actorPlatformId ?? "");

            if (lockBlocks && !isOwner)
            {
                reason = $"locked ({lockLevel})";
                return Decision.Deny;
            }

            // 3. Owner or co-owner.
            if (isOwner || isCoOwner)
            {
                reason = isOwner ? "owner" : "co-owner";
                return Decision.Allow;
            }

            // 4. Network roster membership.
            string network = PortalRecordStore.GetNetworkId(portalZdo);
            if (!string.IsNullOrEmpty(network) && AccessTeamRoster.IsMember(network, actorPlatformId))
            {
                reason = $"network member ({network})";
                return Decision.Allow;
            }

            // 5. Shadow ward coverage.
            if (actorProfileId != 0L && AccessShadowWardIndexEngine.WardCovers(portalZdo.GetPosition(), actorProfileId, out _))
            {
                reason = "ward-permitted";
                return Decision.Allow;
            }

            // 6. Never claimed - adopt it for the actor (unless this is a non-mutating visibility peek).
            if (string.IsNullOrEmpty(owner))
            {
                if (allowMutation && !string.IsNullOrEmpty(actorPlatformId))
                {
                    PortalRecordStore.SetOwnerPlatformId(portalZdo, actorPlatformId);
                    AccessAclEntry claim = AccessAclStore.GetOrCreate(portalZdo.GetPosition());
                    claim.ClaimedAtTicks = DateTime.UtcNow.Ticks;
                    AccessAclStore.MarkDirty();
                }
                reason = allowMutation ? "unclaimed - auto-adopted" : "unclaimed";
                return Decision.Allow;
            }

            // 7. Otherwise refused.
            reason = $"owned by another account ({owner})";
            return Decision.Deny;
        }

        // ------------------------------------------------------------------
        // Emote-driven mutation
        // ------------------------------------------------------------------

        private static void OnEmote(ConnectedCharacter who, string emote)
        {
            try
            {
                if (EmoteSignals.Is(emote, AccessConfig.EmoteClaim?.Value, "point"))
                {
                    HandleClaim(who);
                }
                else if (EmoteSignals.Is(emote, AccessConfig.EmoteToggleLock?.Value, "wave"))
                {
                    HandleToggleLock(who);
                }
                else if (EmoteSignals.Is(emote, AccessConfig.EmoteCoOwnerAdd?.Value, "challenge"))
                {
                    HandleAddCoOwner(who);
                }
            }
            catch (Exception ex)
            {
                PortalDebug.LogError($"[AccessPortalAclEngine] emote handling failed for '{emote}' from {who.Name}: {ex.Message}");
            }
        }

        private static PortalRecord? NearestPortal(Vector3 pos, float range)
        {
            PortalRecord? best = null;
            float bestDist = range * range;
            foreach (PortalRecord record in PortalCensus.Latest)
            {
                float d = (record.Position - pos).sqrMagnitude;
                if (d <= bestDist)
                {
                    bestDist = d;
                    best = record;
                }
            }
            return best;
        }

        private static ConnectedCharacter? NearestOtherCharacter(ConnectedCharacter self, float range)
        {
            ConnectedCharacter? best = null;
            float bestDist = range * range;
            foreach (ConnectedCharacter other in ConnectedCharacters.All())
            {
                if (other.Zdo.m_uid == self.Zdo.m_uid)
                {
                    continue;
                }
                float d = (other.Position - self.Position).sqrMagnitude;
                if (d <= bestDist)
                {
                    bestDist = d;
                    best = other;
                }
            }
            return best;
        }

        private static void HandleClaim(ConnectedCharacter who)
        {
            float range = AccessConfig.EmoteCoOwnerRange?.Value ?? 5f;
            PortalRecord? nearest = NearestPortal(who.Position, range);
            if (nearest == null)
            {
                // #60 Team / Guild Networks - no portal nearby, so this /point instead answers a
                // pending team invite (the "candidate /point to accept" half of the emote-driven
                // invite flow the option's own playerExperience describes). Silent no-op if there is
                // no pending invite - an emote has many innocent uses.
                string candidateId = SenderContext.HostNameOf(who.Peer) ?? "";
                if (!string.IsNullOrEmpty(candidateId) && AccessTeamRoster.AcceptInvite(candidateId, out AccessTeam? joined) && joined != null)
                {
                    AccessTeamRoster.Touch(joined);
                    PlayerNotify.Toast(who, $"You joined {joined.Name}.");
                }
                return;
            }

            string actor = SenderContext.HostNameOf(who.Peer) ?? "";
            if (string.IsNullOrEmpty(actor))
            {
                return;
            }

            ZDO? zdo = ZDOMan.instance?.GetZDO(nearest.Value.Uid);
            if (zdo == null || !zdo.IsValid())
            {
                return;
            }

            string owner = PortalRecordStore.GetOwnerPlatformId(zdo);
            if (string.IsNullOrEmpty(owner))
            {
                PortalRecordStore.SetOwnerPlatformId(zdo, actor);
                AccessAclEntry entry = AccessAclStore.GetOrCreate(zdo.GetPosition());
                entry.ClaimedAtTicks = DateTime.UtcNow.Ticks;
                AccessAclStore.MarkDirty();
                string tag = string.IsNullOrEmpty(nearest.Value.Tag) ? "this gate" : $"\"{nearest.Value.Tag}\"";
                PlayerNotify.Toast(who, $"Gate claimed - {tag}. Only you (and any co-owners) can rename it.");
            }
            else if (string.Equals(owner, actor, StringComparison.OrdinalIgnoreCase))
            {
                PlayerNotify.Toast(who, "You already own this gate.");
            }
            else
            {
                PlayerNotify.Toast(who, "That gate belongs to someone else.");
            }
        }

        private static void HandleToggleLock(ConnectedCharacter who)
        {
            float range = AccessConfig.EmoteCoOwnerRange?.Value ?? 5f;
            PortalRecord? nearest = NearestPortal(who.Position, range);
            if (nearest == null)
            {
                return;
            }
            string actor = SenderContext.HostNameOf(who.Peer) ?? "";
            ZDO? zdo = ZDOMan.instance?.GetZDO(nearest.Value.Uid);
            if (zdo == null || !zdo.IsValid())
            {
                return;
            }
            string owner = PortalRecordStore.GetOwnerPlatformId(zdo);
            bool isAdmin = ZNet.instance != null && ZNet.instance.IsAdmin(actor);
            if (!isAdmin && !string.Equals(owner, actor, StringComparison.OrdinalIgnoreCase))
            {
                return; // silently ignore - not this player's gate, and an emote has innocent uses.
            }

            AccessAclEntry entry = AccessAclStore.GetOrCreate(zdo.GetPosition());
            bool willLock = entry.Lock == AccessLockLevel.None;
            entry.Lock = willLock ? AccessLockLevel.Full : AccessLockLevel.None;
            AccessAclStore.MarkDirty();
            PortalRecordStore.SetLocked(zdo, willLock);

            PlayerNotify.Toast(who, willLock ? "Gate locked - only you may rename or relink it." : "Gate unlocked - anyone may rename it.");
        }

        private static void HandleAddCoOwner(ConnectedCharacter who)
        {
            float range = AccessConfig.EmoteCoOwnerRange?.Value ?? 5f;
            PortalRecord? nearest = NearestPortal(who.Position, range);
            if (nearest == null)
            {
                // #60 Team / Guild Networks - no portal nearby, so this /challenge instead invites the
                // nearest OTHER player to the acting player's own team (an existing member of exactly
                // one team may invite; teams themselves are still admin-created via AccessAdminCommands
                // team-create). Silent no-op otherwise - an emote has many innocent uses.
                string leaderId = SenderContext.HostNameOf(who.Peer) ?? "";
                AccessTeam? myTeam = string.IsNullOrEmpty(leaderId) ? null : AccessTeamRoster.TeamOf(leaderId);
                if (myTeam == null)
                {
                    return;
                }
                ConnectedCharacter? teamCandidate = NearestOtherCharacter(who, range);
                if (teamCandidate == null)
                {
                    return;
                }
                string teamCandidateId = SenderContext.HostNameOf(teamCandidate.Value.Peer) ?? "";
                if (string.IsNullOrEmpty(teamCandidateId))
                {
                    return;
                }
                AccessTeamRoster.Invite(myTeam, teamCandidateId);
                PlayerNotify.Toast(who, $"Invited {teamCandidate.Value.Name} to {myTeam.Name} - they can /point to accept.");
                PlayerNotify.Toast(teamCandidate.Value, $"{who.Name} invites you to network {myTeam.Name} - /point to accept.");
                return;
            }
            string actor = SenderContext.HostNameOf(who.Peer) ?? "";
            ZDO? zdo = ZDOMan.instance?.GetZDO(nearest.Value.Uid);
            if (zdo == null || !zdo.IsValid())
            {
                return;
            }
            string owner = PortalRecordStore.GetOwnerPlatformId(zdo);
            bool isAdmin = ZNet.instance != null && ZNet.instance.IsAdmin(actor);
            if (!isAdmin && !string.Equals(owner, actor, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            ConnectedCharacter? candidate = null;
            float bestDist = range * range;
            foreach (ConnectedCharacter other in ConnectedCharacters.All())
            {
                if (other.Zdo.m_uid == who.Zdo.m_uid)
                {
                    continue;
                }
                float d = (other.Position - who.Position).sqrMagnitude;
                if (d <= bestDist)
                {
                    bestDist = d;
                    candidate = other;
                }
            }
            if (candidate == null)
            {
                PlayerNotify.Toast(who, "No one nearby to add as co-owner.");
                return;
            }

            string candidateId = SenderContext.HostNameOf(candidate.Value.Peer) ?? "";
            if (string.IsNullOrEmpty(candidateId))
            {
                return;
            }

            AccessAclEntry entry = AccessAclStore.GetOrCreate(zdo.GetPosition());
            if (!entry.CoOwners.Contains(candidateId))
            {
                entry.CoOwners.Add(candidateId);
                AccessAclStore.MarkDirty();
            }
            PlayerNotify.Toast(who, $"Added {candidate.Value.Name} as co-owner of this gate.");
            PlayerNotify.Toast(candidate.Value, $"{who.Name} added you as co-owner of their gate.");
        }
    }
}
