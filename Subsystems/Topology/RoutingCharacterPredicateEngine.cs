using System.Collections.Generic;
using UnityEngine;
using TortalPortalLite.Core;
using TortalPortalLite.Core.Data;

namespace TortalPortalLite.Subsystems.Topology
{
    /// <summary>Admin declaration for one Character-State Predicate gate (routing.json section "predicateGates").</summary>
    public sealed class RoutingPredicateGateDefinition
    {
        public string Name = "";
        public RoutingPosition Position = new RoutingPosition();

        /// <summary>"allpvp" | "allunarmored" | "allcrowned" - ALL characters currently within RoutingConfig.ArmRadius must satisfy it.</summary>
        public string Predicate = "allpvp";

        public RoutingPosition? Destination;
        public string ClosedTag = "Closed";
        public string? OpenTag;
        public string FailReason = "requirement not met";
    }

    /// <summary>
    /// #226 Character-State Predicates for JIT Routing. Extends approach-triggered routing from "who is
    /// approaching" to "what state are they in", reading the same handful of server-visible character
    /// ZDO bits every connected character's owning client already pushes: `ZDOVars.s_pvp`
    /// (Player.IsPVPEnabled reads `GetBool(ZDOVars.s_pvp)`, :15555), and the VisEquipment slot hashes
    /// `s_chestItem`/`s_helmetItem` (:78389, :78445 - 0 when empty, exactly like ItemStand's s_item).
    ///
    /// Deliberately an ALL-IN-RING rule, never "the nearest wins": the catalog's own failure-mode list is
    /// explicit that a shared portal's single connection slot makes any per-player divergence a race, and
    /// "all must currently satisfy it, or the gate stays dark for everyone nearby" is the only version
    /// that is legible rather than flaky. A closed gate reads `$piece_portal_unconnected` with no
    /// glow - the honest vanilla signal for "you may not enter", which is why ClosedTag defaults to
    /// something explanatory rather than blank.
    /// </summary>
    public static class RoutingCharacterPredicateEngine
    {
        private static float _timer;
        private static int _lastRegistryVersion = -1;
        private static List<RoutingPredicateGateDefinition> _gates = new List<RoutingPredicateGateDefinition>();

        public static void OnUpdate(float dt)
        {
            if (RoutingConfig.TakeoverEnabled?.Value == false || ZDOMan.instance == null)
            {
                return;
            }
            _timer += dt;
            float interval = RoutingConfig.ApproachPollSeconds?.Value ?? 0.1f;
            if (_timer < interval)
            {
                return;
            }
            _timer = 0f;

            if (RoutingManagedPortalRegistry.Version != _lastRegistryVersion)
            {
                _lastRegistryVersion = RoutingManagedPortalRegistry.Version;
                _gates = RoutingManagedPortalRegistry.Section<RoutingPredicateGateDefinition>("predicateGates");
            }
            if (_gates.Count == 0)
            {
                return;
            }

            List<ConnectedCharacter> characters = ConnectedCharacters.All();
            float armRadius = RoutingConfig.ArmRadius?.Value ?? 12f;
            float armSqr = armRadius * armRadius;

            foreach (RoutingPredicateGateDefinition gate in _gates)
            {
                if (!RoutingPairingAuthorityEngine.TryClaim(gate.Position, $"predicate:{gate.Name}"))
                {
                    continue;
                }
                ZDO? zdo = RoutingWriteOps.ResolveLive(gate.Position);
                if (zdo == null)
                {
                    continue;
                }

                Vector3 pos = gate.Position.ToVector3();
                var nearby = new List<ConnectedCharacter>();
                foreach (ConnectedCharacter character in characters)
                {
                    if ((character.Position - pos).sqrMagnitude <= armSqr)
                    {
                        nearby.Add(character);
                    }
                }

                if (nearby.Count == 0)
                {
                    RoutingWriteOps.Reassert(zdo, gate.ClosedTag, ZDOID.None);
                    RoutingPairingAuthorityEngine.Publish(zdo.m_uid, gate.ClosedTag, ZDOID.None);
                    continue;
                }

                bool allSatisfy = true;
                foreach (ConnectedCharacter character in nearby)
                {
                    if (!Satisfies(gate.Predicate, character))
                    {
                        allSatisfy = false;
                        break;
                    }
                }

                if (!allSatisfy)
                {
                    RoutingWriteOps.Reassert(zdo, gate.ClosedTag, ZDOID.None);
                    RoutingPairingAuthorityEngine.Publish(zdo.m_uid, gate.ClosedTag, ZDOID.None);
                    foreach (ConnectedCharacter character in nearby)
                    {
                        if (!Satisfies(gate.Predicate, character))
                        {
                            PlayerNotify.Toast(character, $"Gate: {gate.FailReason}");
                        }
                    }
                    continue;
                }

                ZDOID desiredConnection = ZDOID.None;
                if (gate.Destination != null)
                {
                    ZDO? destZdo = RoutingWriteOps.ResolveLive(gate.Destination);
                    if (destZdo != null)
                    {
                        desiredConnection = destZdo.m_uid;
                        foreach (ConnectedCharacter character in nearby)
                        {
                            RoutingWriteOps.PrewarmToPeer(character.Peer.m_uid, destZdo.m_uid);
                        }
                    }
                }
                string? openTag = gate.OpenTag ?? gate.ClosedTag;
                RoutingWriteOps.Reassert(zdo, openTag, desiredConnection);
                RoutingPairingAuthorityEngine.Publish(zdo.m_uid, openTag, desiredConnection);
            }
        }

        private static bool Satisfies(string predicate, ConnectedCharacter character)
        {
            switch (predicate)
            {
                case "allunarmored":
                    return character.Zdo.GetInt(ZDOVars.s_chestItem, 0) == 0 && character.Zdo.GetInt(ZDOVars.s_helmetItem, 0) == 0;
                case "allcrowned":
                    return character.Zdo.GetBool(ZDOVars.s_crowned);
                default: // "allpvp"
                    return character.Zdo.GetBool(ZDOVars.s_pvp);
            }
        }
    }
}
