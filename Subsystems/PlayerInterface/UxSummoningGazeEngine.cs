using System.Collections.Generic;
using UnityEngine;
using TortalPortalLite.Core.Data;
using TortalPortalLite.Subsystems.Foundations;

namespace TortalPortalLite.Subsystems.PlayerInterface
{
    /// <summary>
    /// #268 Summoning Gaze - consent-handshake player-to-player teleport (TPA). Two Wave 0 primitives
    /// combine into a mechanism absent from vanilla entirely: `EmoteSignals` for the request/accept
    /// gesture, and `ZDOVars.s_lookTarget` (written by `CharacterAnimEvent.UpdateHeadRotation` on the
    /// owning client, at most every 0.2s) to resolve WHO the requester means, via an angular cone from
    /// their eye position toward every other connected character.
    ///
    /// `s_lookTarget` is Inspector-dependent (only written if the Player prefab's `CharacterAnimEvent`
    /// has head rotation wired up) - if it's unset or the cone finds nobody, this falls back to "nearest
    /// other connected player within a small radius", a degraded but still-useful resolution.
    ///
    /// Bypasses every vanilla teleport rule by design (shares `UxForcedTeleport` with #269) - it is
    /// framed as a deliberate, consented convenience, not a replacement for walking through a portal.
    /// </summary>
    public static class UxSummoningGazeEngine
    {
        private sealed class PendingRequest
        {
            public long RequesterPlayerId;
            public float ExpiresClock;
        }

        private static readonly Dictionary<long, PendingRequest> _pendingByTargetPlayerId = new Dictionary<long, PendingRequest>();
        private static float _clock;

        public static void Initialize()
        {
            EmoteSignals.Register(OnEmote);
        }

        public static void OnUpdate(float dt)
        {
            _clock += dt;
            if (_pendingByTargetPlayerId.Count == 0)
            {
                return;
            }
            var expired = new List<long>();
            foreach (KeyValuePair<long, PendingRequest> kv in _pendingByTargetPlayerId)
            {
                if (_clock > kv.Value.ExpiresClock)
                {
                    expired.Add(kv.Key);
                }
            }
            foreach (long k in expired)
            {
                _pendingByTargetPlayerId.Remove(k);
            }
        }

        private static void OnEmote(ConnectedCharacter who, string emote)
        {
            if (UxConfig.Enabled?.Value == false || UxConfig.SummoningGazeEnabled?.Value == false)
            {
                return;
            }

            if (EmoteSignals.Is(emote, UxConfig.SummonRequestEmote?.Value, "wave"))
            {
                if (!TryResolveLookedAtPlayer(who, out ConnectedCharacter target) || target.PlayerId == who.PlayerId)
                {
                    return;
                }
                float expiry = UxConfig.SummonExpirySeconds?.Value ?? 30f;
                _pendingByTargetPlayerId[target.PlayerId] = new PendingRequest { RequesterPlayerId = who.PlayerId, ExpiresClock = _clock + expiry };
                UxFeedback.Toast(target, $"{who.Name} wants to teleport to you - accept within {expiry:0}s.");
                UxFeedback.Toast(who, $"Request sent to {target.Name}.");
                return;
            }

            if (EmoteSignals.Is(emote, UxConfig.SummonAcceptEmote?.Value, "thumbsup"))
            {
                if (!_pendingByTargetPlayerId.TryGetValue(who.PlayerId, out PendingRequest req))
                {
                    return;
                }
                _pendingByTargetPlayerId.Remove(who.PlayerId);
                if (_clock > req.ExpiresClock)
                {
                    return;
                }
                ConnectedCharacter? requesterOpt = ResolveByPlayerId(req.RequesterPlayerId);
                if (!requesterOpt.HasValue)
                {
                    return;
                }
                ConnectedCharacter requester = requesterOpt.Value;
                if (!UxForcedTeleport.CanTeleport(requester, out string reason))
                {
                    UxFeedback.Toast(requester, $"Can't teleport right now ({reason}).");
                    return;
                }

                Vector3 pos = who.Position + who.Zdo.GetRotation() * Vector3.back * 1.5f + Vector3.up;
                if (UxForcedTeleport.TryTeleport(requester, pos, who.Zdo.GetRotation()))
                {
                    UxFeedback.Toast(who, $"Teleporting {requester.Name} to you.");
                    UxFeedback.Toast(requester, $"Teleporting to {who.Name}. Carried items travel with you.");
                }
            }
        }

        private static bool TryResolveLookedAtPlayer(ConnectedCharacter requester, out ConnectedCharacter target)
        {
            target = default;
            Vector3 lookTarget = requester.Zdo.GetVec3(ZDOVars.s_lookTarget, Vector3.zero);
            float maxRange = UxConfig.SummonMaxLookRangeMeters?.Value ?? 40f;

            if (lookTarget != Vector3.zero)
            {
                Vector3 eyePos = requester.Position + Vector3.up * 1.6f;
                Vector3 rayDir = lookTarget - eyePos;
                if (rayDir.sqrMagnitude > 0.01f)
                {
                    rayDir.Normalize();
                    float coneCos = Mathf.Cos((UxConfig.SummonConeDegrees?.Value ?? 8f) * Mathf.Deg2Rad);
                    bool found = false;
                    float bestDot = -1f;
                    ConnectedCharacter best = default;
                    foreach (ConnectedCharacter cc in ConnectedCharacters.All())
                    {
                        if (cc.PlayerId == requester.PlayerId)
                        {
                            continue;
                        }
                        Vector3 toOther = cc.Position - eyePos;
                        float dist = toOther.magnitude;
                        if (dist < 0.01f || dist > maxRange)
                        {
                            continue;
                        }
                        float dot = Vector3.Dot(rayDir, toOther / dist);
                        if (dot >= coneCos && dot > bestDot)
                        {
                            bestDot = dot;
                            best = cc;
                            found = true;
                        }
                    }
                    if (found)
                    {
                        target = best;
                        return true;
                    }
                }
            }

            // Degraded fallback (s_lookTarget unset on this build's Player prefab, or nothing in the cone): nearest other connected player within a small radius.
            bool anyNear = false;
            float bestSqr = 10f * 10f;
            ConnectedCharacter nearest = default;
            foreach (ConnectedCharacter cc in ConnectedCharacters.All())
            {
                if (cc.PlayerId == requester.PlayerId)
                {
                    continue;
                }
                float d = (cc.Position - requester.Position).sqrMagnitude;
                if (d <= bestSqr)
                {
                    bestSqr = d;
                    nearest = cc;
                    anyNear = true;
                }
            }
            target = nearest;
            return anyNear;
        }

        private static ConnectedCharacter? ResolveByPlayerId(long playerId)
        {
            foreach (ConnectedCharacter cc in ConnectedCharacters.All())
            {
                if (cc.PlayerId == playerId)
                {
                    return cc;
                }
            }
            return null;
        }
    }
}
