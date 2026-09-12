using System.Collections.Generic;
using UnityEngine;
using TortalPortalLite.Core;
using TortalPortalLite.Core.Data;

namespace TortalPortalLite.Subsystems.Topology
{
    /// <summary>Admin declaration for one Parked Terminal (routing.json section "parkedTerminals"). Covers both #227 (Distant=true, the vanilla-feel base mechanism) and #225 (Distant=false, its fast-transit timing variant - #225's own prerequisite list names #227 as "the base mechanism").</summary>
    public sealed class RoutingParkedTerminalDefinition
    {
        public string Name = "";
        public RoutingPosition Position = new RoutingPosition();

        /// <summary>Everyone gets the same destination when set; otherwise each player's own RoutingPlayerSelectionStore pick under this terminal's Name is used (fed by #36's emote/item-stand channels at this same position, or elsewhere under the same gate Name).</summary>
        public RoutingPosition? FixedDestination;

        /// <summary>true (#227): vanilla-feel swirl, ~8s hold, every vanilla portal gate still applies (NoPortals/NoBossPortals/ore check all live inside TeleportWorld.Teleport, which this path never calls, so none of them run - documented limitation, not a bug). false (#225): fast ~2-4s fade, gated on the destination zone already being server-generated and the exit point resolving above ground, falling back to distant:true for that one transit otherwise.</summary>
        public bool Distant = true;

        /// <summary>Kept unique per terminal by convention so Game.ConnectPortals' pass 2 (FindRandomUnconnectedPortal) never finds a same-tag partner for it - the terminal's Portal connection is intentionally always None; RPC_TeleportTo is this engine's own, separate delivery path.</summary>
        public string ReservedTag = "parked";
    }

    /// <summary>
    /// #227 Parked Terminal (Server-Teleport Portal) + #225 Fast-Transit Mode. A managed portal is kept
    /// permanently disconnected (Portal connection held at ZDOID.None, so `TeleportWorld.Teleport`'s own
    /// gate 1 silently no-ops for anyone who still walks in the ordinary way, :143519-143522) and a tight
    /// position-based watcher answers the ACTUAL transit itself with a direct
    /// `ZRoutedRpc.InvokeRoutedRPC(peer.m_uid, characterZdoId, "RPC_TeleportTo", exitPos, exitRot,
    /// distant)` call (the routed RPC name `Character` registers on every instance, :886; handler
    /// `Character.RPC_TeleportTo` is owner-gated so only the traveller's own client acts on it,
    /// :4126-4132) - never a Harmony patch, this is calling an RPC vanilla already listens for, exactly
    /// like Core/Data/PlayerNotify.cs already does for "Message".
    ///
    /// This is the ONE transit design in the whole domain with NO shared-connection contention: two
    /// players stepping onto the same terminal in the same tick each get their OWN RPC with their OWN
    /// destination, because the destination never has to live in the portal's single connection slot.
    /// The cost, honestly documented: NoPortals/NoBossPortals/the ore-carrying check are all evaluated
    /// INSIDE `TeleportWorld.Teleport` (:143523-143537), which this path never calls - the global-key
    /// gates could be re-implemented server-side but the item gate cannot (player inventories are never
    /// in any ZDO, per Core/Data/ZdoInventoryIO's own documented limit).
    ///
    /// Distant=false (#225) additionally gates on: the destination zone already being server-generated
    /// (`ZoneSystem.instance.IsZoneGenerated(ZoneSystem.GetZone(pos))`, :115869/:115741 - otherwise the
    /// traveller's own client-side PokeLocalZone runs in SpawnMode.Client and never places vegetation/
    /// locations, :114021-114037) and the exit point resolving above `WorldGenerator.GetHeight` (a
    /// distant:false FindFloor miss reverts the player with "$msg_portal_blocked" instead of force-landing,
    /// :15360-15374) - failing either check falls back to distant:true for that specific transit rather
    /// than risking a stuck player.
    /// </summary>
    public static class RoutingParkedTerminalEngine
    {
        private static float _timer;
        private static int _lastRegistryVersion = -1;
        private static List<RoutingParkedTerminalDefinition> _terminals = new List<RoutingParkedTerminalDefinition>();
        private static readonly RoutingApproachWatcher _watcher = new RoutingApproachWatcher();
        private static readonly Dictionary<string, RoutingParkedTerminalDefinition> _byName = new Dictionary<string, RoutingParkedTerminalDefinition>();

        public static void OnUpdate(float dt)
        {
            if (RoutingConfig.TakeoverEnabled?.Value == false || ZDOMan.instance == null || ZRoutedRpc.instance == null)
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
                _terminals = RoutingManagedPortalRegistry.Section<RoutingParkedTerminalDefinition>("parkedTerminals");
                _byName.Clear();
                foreach (RoutingParkedTerminalDefinition terminal in _terminals)
                {
                    _byName[terminal.Name] = terminal;
                }
            }
            if (_terminals.Count == 0)
            {
                return;
            }

            var positions = new List<(string, Vector3)>(_terminals.Count);
            foreach (RoutingParkedTerminalDefinition terminal in _terminals)
            {
                if (!RoutingPairingAuthorityEngine.TryClaim(terminal.Position, $"parkedterminal:{terminal.Name}"))
                {
                    continue;
                }
                // Keep the terminal permanently unconnected under its own reserved tag - defended every
                // tick exactly like every other managed portal in this domain.
                ZDO? zdo = RoutingWriteOps.ResolveLive(terminal.Position);
                if (zdo != null)
                {
                    RoutingWriteOps.Reassert(zdo, terminal.ReservedTag, ZDOID.None);
                    RoutingPairingAuthorityEngine.Publish(zdo.m_uid, terminal.ReservedTag, ZDOID.None);
                }
                positions.Add((terminal.Name, terminal.Position.ToVector3()));
            }

            float triggerRadius = RoutingConfig.FastTransitTriggerRadius?.Value ?? 1.2f;
            _watcher.Poll(positions, triggerRadius, 0f, onArm: HandleArm);
        }

        private static void HandleArm(string terminalName, ConnectedCharacter character)
        {
            if (!_byName.TryGetValue(terminalName, out RoutingParkedTerminalDefinition? terminal) || ZRoutedRpc.instance == null)
            {
                return;
            }
            if (character.Zdo.GetBool(ZDOVars.s_dead) || character.Zdo.GetBool(ZDOVars.s_inBed))
            {
                return;
            }

            RoutingPosition? destPos = terminal.FixedDestination;
            if (destPos == null && !RoutingPlayerSelectionStore.TryGetSelection(terminal.Name, character.PlayerId, out destPos))
            {
                return;
            }
            ZDO? destZdo = RoutingWriteOps.ResolveLive(destPos!);
            if (destZdo == null)
            {
                return;
            }

            Vector3 destinationPos = destZdo.GetPosition();
            Quaternion destinationRot = destZdo.GetRotation();
            Vector3 exitPos = destinationPos + destinationRot * Vector3.forward * 1f + Vector3.up;

            bool useDistant = terminal.Distant || !FastPathIsSafe(destinationPos);

            ZRoutedRpc.instance.InvokeRoutedRPC(character.Peer.m_uid, character.Zdo.m_uid, "RPC_TeleportTo", exitPos, destinationRot, useDistant);
        }

        private static bool FastPathIsSafe(Vector3 destinationPos)
        {
            if (ZoneSystem.instance == null || WorldGenerator.instance == null)
            {
                return false;
            }
            if (!ZoneSystem.instance.IsZoneGenerated(ZoneSystem.GetZone(destinationPos)))
            {
                // Not ready for a fast (distant:false) hop this time - queue it so a LATER attempt at
                // this same terminal can succeed (#228).
                RoutingZoneGhostPreGenEngine.Enqueue(destinationPos, 1);
                return false;
            }
            float ground = WorldGenerator.instance.GetHeight(destinationPos.x, destinationPos.z);
            return destinationPos.y >= ground - 0.5f;
        }
    }
}
