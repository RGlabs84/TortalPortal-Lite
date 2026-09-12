using System.Collections.Generic;
using TortalPortalLite.Core;

namespace TortalPortalLite.Subsystems.Topology
{
    public sealed class RoutingSeasonalMember
    {
        public RoutingPosition Position = new RoutingPosition();
        public string Tag = "";
        public RoutingPosition? Destination;
    }

    /// <summary>Admin declaration for one seasonal/event network layout (routing.json section "seasonalNetworks"). An admin flips Active to swap layouts - the previous layout's own JSON entry is the "keep the before-state" record the catalog calls for, no separate snapshot file needed.</summary>
    public sealed class RoutingSeasonalNetworkDefinition
    {
        public string Name = "";
        public bool Active = false;
        public List<RoutingSeasonalMember> Members = new List<RoutingSeasonalMember>();

        /// <summary>Broadcast once (edge-triggered on Active turning true) via MessageHud.MessageAll - the catalog's own "The ways have shifted." cue.</summary>
        public string? AnnounceMessage;
    }

    /// <summary>
    /// #38 Seasonal / Event Network Swap - a whole named topology's tags/destinations, applied or
    /// withdrawn as one admin toggle in routing.json rather than 40+ individual hub/schedule edits.
    ///
    /// BATCHING IS MANDATORY, not a nicety: `ZDOMan.SendZDOs` performs no delta encoding (re-serializes
    /// the WHOLE ZDO per peer per send, :77056-77064) and `AddForceSendZdos` inserts every force-sent id
    /// at the HEAD of a peer's sync list (:77275) - a hundred simultaneous force-sends would monopolise
    /// every peer's next several send slots under the 10240-byte queue cap (:77011-77019), the same
    /// lesson Wonderland's StructureUpkeep already paid for (its own 256-per-sweep cap, cited directly in
    /// the catalog). RoutingConfig.SweepWriteBudgetPerTick caps actual writes per tick here exactly the
    /// same way #30's event retargeter does.
    ///
    /// CHEAPEST PATH: also applied once from RoutingSubsystem.OnWorldReady, before any peer connects -
    /// `ZDOMan.LoadChunks` (:76474-76531) has already loaded the portal chunk and `m_peers` is empty, so
    /// every write in that window takes the immediate local path with zero network cost or contention,
    /// exactly the pre-peer opportunity catalog #27 (One-Way Ring) also calls out.
    /// </summary>
    public static class RoutingSeasonalSwapEngine
    {
        private static float _timer;
        private static int _lastRegistryVersion = -1;
        private static List<RoutingSeasonalNetworkDefinition> _networks = new List<RoutingSeasonalNetworkDefinition>();
        private static readonly HashSet<string> _announcedActive = new HashSet<string>();

        public static void OnWorldReady()
        {
            RefreshDeclarationsIfNeeded();
            ApplyActiveNetworks(new RoutingWriteBudget(int.MaxValue));
        }

        public static void OnUpdate(float dt)
        {
            if (RoutingConfig.TakeoverEnabled?.Value == false || ZDOMan.instance == null)
            {
                return;
            }
            _timer += dt;
            if (_timer < 1.0f)
            {
                return;
            }
            _timer = 0f;

            RefreshDeclarationsIfNeeded();
            if (_networks.Count == 0)
            {
                return;
            }
            ApplyActiveNetworks(new RoutingWriteBudget(RoutingConfig.SweepWriteBudgetPerTick?.Value ?? 16));
        }

        private static void RefreshDeclarationsIfNeeded()
        {
            if (RoutingManagedPortalRegistry.Version == _lastRegistryVersion)
            {
                return;
            }
            _lastRegistryVersion = RoutingManagedPortalRegistry.Version;
            _networks = RoutingManagedPortalRegistry.Section<RoutingSeasonalNetworkDefinition>("seasonalNetworks");

            var stillActive = new HashSet<string>();
            foreach (RoutingSeasonalNetworkDefinition network in _networks)
            {
                if (network.Active)
                {
                    stillActive.Add(network.Name);
                    if (_announcedActive.Add(network.Name) && !string.IsNullOrEmpty(network.AnnounceMessage) && MessageHud.instance != null)
                    {
                        MessageHud.instance.MessageAll(MessageHud.MessageType.Center, network.AnnounceMessage);
                    }
                }
            }
            _announcedActive.IntersectWith(stillActive);
        }

        private static void ApplyActiveNetworks(RoutingWriteBudget budget)
        {
            foreach (RoutingSeasonalNetworkDefinition network in _networks)
            {
                if (!network.Active)
                {
                    continue;
                }
                foreach (RoutingSeasonalMember member in network.Members)
                {
                    if (!RoutingPairingAuthorityEngine.TryClaim(member.Position, $"seasonal:{network.Name}"))
                    {
                        continue;
                    }
                    ZDO? zdo = RoutingWriteOps.ResolveLive(member.Position);
                    if (zdo == null)
                    {
                        continue;
                    }

                    ZDOID desiredConnection = ZDOID.None;
                    if (member.Destination != null)
                    {
                        ZDO? destZdo = RoutingWriteOps.ResolveLive(member.Destination);
                        if (destZdo != null)
                        {
                            desiredConnection = destZdo.m_uid;
                        }
                    }
                    string? desiredTag = string.IsNullOrEmpty(member.Tag) ? null : member.Tag;

                    RoutingPairingAuthorityEngine.Publish(zdo.m_uid, desiredTag, desiredConnection);
                    if (budget.TryConsume())
                    {
                        RoutingWriteOps.Reassert(zdo, desiredTag, desiredConnection);
                    }
                }
            }
        }
    }
}
