using System;
using System.Collections.Generic;
using TortalPortalLite.Core;

namespace TortalPortalLite.Subsystems.Foundations
{
    public enum NetworkValidationSeverity { Warning, Error }

    public readonly struct NetworkValidationFinding
    {
        public readonly NetworkValidationSeverity Severity;
        public readonly string NetworkName;
        public readonly string Detail;

        public NetworkValidationFinding(NetworkValidationSeverity severity, string networkName, string detail)
        {
            Severity = severity;
            NetworkName = networkName;
            Detail = detail;
        }
    }

    /// <summary>
    /// #68 Network definition validation - the hazard checks the catalog's own howItWorks specifies for
    /// the hot-reloaded networks.json, layered ENTIRELY as a read-only observer on top of the existing
    /// NetworkModel (Subsystems/Foundations/NetworkModel.cs, Wave 0) rather than a second parser/poller:
    /// NetworkModel already does the hot-reload (5s poll, same pattern as WonderlandPlugin.PollConfigFile)
    /// and the position-radius resolution against PortalCensus, and NetworkReassertEngine already applies
    /// a declared network as a ring (member i -> member i+1 mod N, which covers pair/hub/ring uniformly -
    /// a 2-member ring IS a pair, per that engine's own doc comment). What Wave 0 did NOT add is
    /// validation: catalog #68 explicitly calls for rejecting a duplicate id/tag, a network with too few
    /// members, an out-of-range or interior-only coordinate, and - the one check no other engine performs -
    /// two declared members (in the same or different networks) resolving to the SAME live portal ZDO,
    /// which silently makes NetworkReassertEngine fight itself every tick. This engine finds those and
    /// reports them; it never writes a ZDO and never touches the live NetworkModel.Networks list.
    ///
    /// Tag-collision-with-an-unmanaged-portal (the other hazard #68 names: "vanilla's random same-tag
    /// pairing will cross-link them the instant the reassert loop is late") is checked against the
    /// CURRENT census tag distribution, not just other declared networks - a network can collide with a
    /// player's own hand-typed tag just as easily as with another declared network.
    /// </summary>
    public static class OpsOutputNetworkValidationEngine
    {
        private const float Interval = 5.5f; // just after NetworkModel's own 5s poll, so a reload is validated the same pass it lands
        private static float _timer;
        private static List<NetworkValidationFinding> _findings = new List<NetworkValidationFinding>();

        public static IReadOnlyList<NetworkValidationFinding> Findings => _findings;

        public static void OnUpdate(float dt)
        {
            if (OpsOutputConfig.NetworkValidationEnabled?.Value == false)
            {
                return;
            }
            _timer += dt;
            if (_timer < Interval)
            {
                return;
            }
            _timer = 0f;
            Validate();
        }

        private static void Validate()
        {
            var findings = new List<NetworkValidationFinding>();
            var networks = NetworkModel.Networks;

            // 1. Unique network name / unique tag among DECLARED networks.
            var namesSeen = new Dictionary<string, int>(StringComparer.Ordinal);
            var tagOwners = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (NetworkDefinition net in networks)
            {
                namesSeen.TryGetValue(net.Name, out int count);
                namesSeen[net.Name] = count + 1;

                if (tagOwners.TryGetValue(net.Tag, out string owner) && owner != net.Name)
                {
                    findings.Add(new NetworkValidationFinding(NetworkValidationSeverity.Error, net.Name,
                        $"tag '{net.Tag}' is shared with network '{owner}' - vanilla's ordinal tag-equality teardown (Game.ConnectPortals phase 1, SERVER decompile :100601) cannot tell these two declared networks apart; the random same-tag pairing (:100664-100679) will cross-link them the instant the reassert loop is late."));
                }
                else
                {
                    tagOwners[net.Tag] = net.Name;
                }

                // 2. Too few members for anything to actually pair.
                if (net.Members.Count < 2)
                {
                    findings.Add(new NetworkValidationFinding(NetworkValidationSeverity.Warning, net.Name,
                        $"only {net.Members.Count} member(s) declared - a network needs at least 2 members to form any connection."));
                }
            }
            foreach (var kvp in namesSeen)
            {
                if (kvp.Value > 1)
                {
                    findings.Add(new NetworkValidationFinding(NetworkValidationSeverity.Error, kvp.Key,
                        $"declared {kvp.Value} times in networks.json - names must be unique."));
                }
            }

            // 3. Declared tag collides with an UNMANAGED portal's own hand-typed tag.
            var unmanagedTags = new HashSet<string>(StringComparer.Ordinal);
            foreach (PortalRecord record in PortalCensus.Latest)
            {
                ZDO? zdo = ZDOMan.instance?.GetZDO(record.Uid);
                string networkId = zdo != null ? PortalRecordStore.GetNetworkId(zdo) : "";
                if (string.IsNullOrEmpty(networkId) && !string.IsNullOrEmpty(record.Tag))
                {
                    unmanagedTags.Add(record.Tag);
                }
            }
            foreach (NetworkDefinition net in networks)
            {
                if (unmanagedTags.Contains(net.Tag))
                {
                    findings.Add(new NetworkValidationFinding(NetworkValidationSeverity.Warning, net.Name,
                        $"tag '{net.Tag}' matches at least one UNMANAGED portal's own tag - an unrelated player-built gate can be cross-linked into this network."));
                }
            }

            // 4. Coordinate sanity: out-of-range clamp and interior-without-opt-in.
            bool allowInterior = OpsOutputConfig.NetworkAllowInteriorMembers?.Value == true;
            const float maxCoord = 16320f; // ZoneSystem.SectorToIndex clamps out-of-range zones to sector 0 (:115755-115774)
            foreach (NetworkDefinition net in networks)
            {
                foreach (NetworkMemberPosition pos in net.Members)
                {
                    if (Math.Abs(pos.X) > maxCoord || Math.Abs(pos.Z) > maxCoord)
                    {
                        findings.Add(new NetworkValidationFinding(NetworkValidationSeverity.Error, net.Name,
                            $"member at ({pos.X:F0},{pos.Y:F0},{pos.Z:F0}) is beyond +/-{maxCoord:F0}m - ZoneSystem.SectorToIndex clamps out-of-range zones to sector 0 (:115755-115774), so this would silently alias onto an unrelated part of the map."));
                    }
                    else if (!allowInterior && pos.Y > 3000f)
                    {
                        findings.Add(new NetworkValidationFinding(NetworkValidationSeverity.Warning, net.Name,
                            $"member at ({pos.X:F0},{pos.Y:F0},{pos.Z:F0}) has y>3000 (Character.InInterior, :4538-4541) - likely a dungeon-interior anchor; set NetworkAllowInteriorMembers=true if this is intentional."));
                    }
                }
            }

            // 5. Ambiguous membership: two declared member positions (same or different network)
            // resolving to the SAME live portal ZDO - NetworkReassertEngine would then try to make that
            // one portal simultaneously satisfy two different ring positions every tick.
            var claimedBy = new Dictionary<ZDOID, string>();
            foreach (var (network, record) in NetworkModel.ResolveAll())
            {
                if (claimedBy.TryGetValue(record.Uid, out string firstNetwork) && firstNetwork != network.Name)
                {
                    findings.Add(new NetworkValidationFinding(NetworkValidationSeverity.Error, network.Name,
                        $"the portal at ({record.Position.x:F0},{record.Position.y:F0},{record.Position.z:F0}) also resolves as a member of network '{firstNetwork}' - one portal cannot serve two declared networks at once."));
                }
                else
                {
                    claimedBy[record.Uid] = network.Name;
                }
            }

            _findings = findings;
        }
    }
}
