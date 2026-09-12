using UnityEngine;
using TortalPortalLite.Core;

namespace TortalPortalLite.Subsystems.Topology
{
    /// <summary>
    /// #206 Target Materialiser - before finalising a route to a boss altar/trader/dungeon/location,
    /// force that target's zone (and its 8-neighbour ring) to ghost-generate NOW rather than waiting for
    /// a player to discover it organically, turning "portal to X" from "if someone has been there" into
    /// "always" - a thin front door onto #205's shared SpawnZone driver/budget (this option's own
    /// explicit prerequisite: "shares the SpawnZone driver and per-tick budget").
    ///
    /// Called from TargetedBossAltarEngine/TargetedDungeonEngine/TargetedLocationEngine right after each
    /// resolves a ZoneSystem.LocationInstance, and from TargetedTraderEngine only in "always" mode
    /// (DiscoveredOnly=false) - forcing generation in "discovered only" mode would defeat that mode's own
    /// purpose (PlaceLocations' own SendLocationIcons call reveals the map pin the instant the zone
    /// generates, SERVER decompile :114998-115001).
    /// </summary>
    public static class TargetedMaterialiserEngine
    {
        /// <summary>No-op if TargetedConfig.MaterialiserEnabled is off.</summary>
        public static void EnsureMaterialized(Vector3 targetPos)
        {
            if (TargetedConfig.MaterialiserEnabled?.Value == false)
            {
                return;
            }
            TargetedZoneGovernorEngine.RequestGhostGenerateWithRing(targetPos);
        }
    }
}
