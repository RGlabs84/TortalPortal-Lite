using System.Collections.Generic;
using UnityEngine;
using TortalPortalLite.Core;
using TortalPortalLite.Core.Data;
using TortalPortalLite.Subsystems.Foundations;

namespace TortalPortalLite.Subsystems.Topology
{
    /// <summary>
    /// #191 Nearest Player (player-to-player portal) - cycle through connected players and track the
    /// chosen one live via a moving Invisible Anchor (#179/#196), the catalog's own "mechanism B".
    ///
    /// Mechanism A (a DIRECT `src.SetConnection(Portal, characterZdo.m_uid)` link straight onto the
    /// target's own character ZDO) is NOT implemented: the catalog's own howItWorks is explicit that
    /// it "requires a prefix on Game.ConnectPortals... a Harmony prefix that skips reconcile for
    /// TPL-managed sources" - but Core/Hooks/ConnectPortalsHook.cs only offers a POSTFIX broker (by
    /// design: it observes vanilla's reconciler after it runs, it cannot veto the reconcile pass before
    /// it clears an unrecognised link). Writing a competing ad hoc Harmony prefix on Game.ConnectPortals
    /// from this file would be exactly the "one independent Harmony patch per subsystem on the same hot
    /// method" collision Core/Hooks/ exists to prevent.
    /// NEEDS NEW HOOK BROKER on Game.ConnectPortals (a prefix-veto/skip-reconcile capability, not just a
    /// postfix): purpose - let a TPL-managed source portal's direct link to a live (non-portal, frequently
    /// relocating) character ZDO survive vanilla's 5 s reconciler without a phantom/anchor indirection,
    /// for #191 mechanism A. Mechanism B below is a complete, independently-shippable implementation and
    /// does not depend on this.
    ///
    /// Safety filters mirror the catalog's own list: never target a dead, sleeping, underwater, or
    /// interior (y&gt;3000) player - and never write a Portal connection onto a character ZDO directly
    /// (its one connection slot is SyncTransform-parenting; ZSyncTransform.OwnerSync already claims it).
    /// </summary>
    public static class TargetedNearestPlayerEngine
    {
        private const string Kind = "NearestPlayer";
        private static float _timer;
        private static readonly Dictionary<long, float> _lastActiveClock = new Dictionary<long, float>();
        private static float _clock;

        public static void Initialize()
        {
            EmoteSignals.Register(OnEmote);
        }

        public static void OnUpdate(float dt)
        {
            _clock += dt;

            _timer += dt;
            float interval = TargetedMovingTargetEngine.TickSeconds;
            if (_timer < interval)
            {
                return;
            }
            _timer = 0f;
            ReassertAll();
        }

        private static void OnEmote(ConnectedCharacter who, string emote)
        {
            if (!EmoteSignals.Is(emote, TargetedConfig.CyclePlayerEmote?.Value, "wave"))
            {
                return;
            }
            _lastActiveClock[who.PlayerId] = _clock; // performing the cycle emote also opts this player into being a candidate for others

            ZDO hub = FindNearestHub(who.Position, 10f);
            if (hub == null)
            {
                return;
            }

            List<ConnectedCharacter> candidates = BuildCandidates(who);
            if (candidates.Count == 0)
            {
                PlayerNotify.Toast(who, "No eligible players online to target.");
                return;
            }

            long previousTarget = ParseTargetId(TargetedAnchorFactory.ResolveExistingAnchor(hub));
            int startIndex = candidates.FindIndex(c => c.PlayerId == previousTarget);
            int nextIndex = (startIndex + 1) % candidates.Count;
            ConnectedCharacter target = candidates[nextIndex];

            PlaceFor(hub, who, target);
            float dist = Vector3.Distance(who.Position, target.Position);
            PlayerNotify.Toast(who, $"Target: {target.Name} ({dist:F0} m)");
        }

        private static List<ConnectedCharacter> BuildCandidates(ConnectedCharacter claimant)
        {
            bool requireConsent = TargetedConfig.NearestPlayerRequireConsent?.Value == true;
            const float consentWindowSeconds = 300f;

            var list = new List<ConnectedCharacter>();
            foreach (ConnectedCharacter cc in ConnectedCharacters.All())
            {
                if (cc.PlayerId == claimant.PlayerId || cc.PlayerId == 0L)
                {
                    continue;
                }
                if (cc.Zdo.GetBool(ZDOVars.s_dead) || cc.Zdo.GetBool(ZDOVars.s_inBed))
                {
                    continue;
                }
                if (cc.Position.y > 3000f)
                {
                    continue; // dungeon interior - traveller's client would never have it loaded
                }
                if (WorldGenerator.instance != null && WorldGenerator.instance.GetHeight(cc.Position.x, cc.Position.z) < 30f && cc.Position.y < 30f)
                {
                    continue; // in open water
                }
                if (requireConsent && (!_lastActiveClock.TryGetValue(cc.PlayerId, out float lastActive) || _clock - lastActive > consentWindowSeconds))
                {
                    continue;
                }
                list.Add(cc);
            }
            list.Sort((a, b) => Vector3.Distance(a.Position, claimant.Position).CompareTo(Vector3.Distance(b.Position, claimant.Position)));
            return list;
        }

        private static void PlaceFor(ZDO hub, ConnectedCharacter claimant, ConnectedCharacter target)
        {
            Vector3 pos = target.Position + Vector3.up * 0.1f;
            string tag = TargetedTagFormat.Named(target.Name);
            ZDO anchor = TargetedAnchorFactory.CreateOrRetarget(hub, pos, Quaternion.identity, tag, $"nearestplayer:{claimant.PlayerId}:{target.PlayerId}");
            if (anchor != null)
            {
                TargetedMovingTargetEngine.ForceSendToNearbySourcePeers(hub, anchor);
            }
        }

        private static void ReassertAll()
        {
            if (ZDOMan.instance == null)
            {
                return;
            }
            foreach (TargetedRoute route in TargetedRouteStore.RoutesOfKind(Kind))
            {
                if (!PortalCensus.TryGetByPosition(route.SourcePosition, out PortalRecord sourceRec))
                {
                    continue;
                }
                ZDO source = ZDOMan.instance.GetZDO(sourceRec.Uid);
                if (source == null || !source.IsValid())
                {
                    continue;
                }

                ZDO anchor = TargetedAnchorFactory.ResolveExistingAnchor(source);
                if (anchor == null)
                {
                    continue;
                }
                long targetId = ParseTargetId(anchor);
                if (targetId == 0L)
                {
                    continue;
                }

                ConnectedCharacter? target = FindConnected(targetId);
                if (target == null)
                {
                    continue; // target logged out/disconnected - freeze the anchor at its last position (#196's own "lost target" rule) rather than reaping a live claim
                }
                ConnectedCharacter t = target.Value;
                if (t.Zdo.GetBool(ZDOVars.s_dead) || t.Position.y > 3000f)
                {
                    continue;
                }

                Vector3 pos = t.Position + Vector3.up * 0.1f;
                if (TargetedMovingTargetEngine.MovedEnough(anchor.GetPosition(), pos) && TargetedAnchorFactory.MoveTo(anchor, pos, Quaternion.identity))
                {
                    TargetedMovingTargetEngine.ForceSendToNearbySourcePeers(source, anchor);
                }
            }
        }

        private static long ParseTargetId(ZDO anchor)
        {
            if (anchor == null)
            {
                return 0L;
            }
            string[] parts = TargetedAnchorFactory.GetKind(anchor).Split(':');
            return parts.Length == 3 && parts[0] == "nearestplayer" && long.TryParse(parts[2], out long id) ? id : 0L;
        }

        private static ConnectedCharacter? FindConnected(long playerId)
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

        private static ZDO FindNearestHub(Vector3 pos, float radius)
        {
            if (ZDOMan.instance == null)
            {
                return null;
            }
            ZDO best = null;
            float bestDistSqr = radius * radius;
            foreach (TargetedRoute route in TargetedRouteStore.RoutesOfKind(Kind))
            {
                float d = (route.SourcePosition - pos).sqrMagnitude;
                if (d > bestDistSqr)
                {
                    continue;
                }
                if (!PortalCensus.TryGetByPosition(route.SourcePosition, out PortalRecord rec))
                {
                    continue;
                }
                ZDO z = ZDOMan.instance.GetZDO(rec.Uid);
                if (z == null || !z.IsValid())
                {
                    continue;
                }
                bestDistSqr = d;
                best = z;
            }
            return best;
        }
    }
}
