using System;
using TortalPortalLite.Core;
using TortalPortalLite.Core.Data;

namespace TortalPortalLite.Subsystems.Foundations
{
    /// <summary>
    /// #69 NetworkReassertEngine - THE ONLY ENGINE IN THIS MOD THAT WRITES `s_tag` OR
    /// `ConnectionType.Portal`. Every other engine (topology/routing/access/etc., in later waves) that
    /// wants a portal's tag or connection changed must route the request through here, never write
    /// those two fields itself - see the implementation plan's "ops is the architecture, not a domain"
    /// finding and catalog #69's own explicit rule.
    ///
    /// A 2 Hz (configurable, must stay under vanilla's 5s pass) loop that compares each network's
    /// declared members (NetworkModel, resolved against PortalCensus) against their CURRENT tag/
    /// connection, and re-writes only what differs - vanilla's own immediate-write recipe
    /// (Game.SetConnection :100637-100646), reproduced verbatim via PortalOwnership.ClaimAndWrite:
    /// SetOwner, Set(s_tag), SetConnection(Portal, target), ForceSendZDO, SetDirtyPortals. Uses
    /// ZDO.SetConnection directly, never UpdateConnection (that early-returns and writes nothing when
    /// no connection entry exists yet - wrong for a brand-new pairing).
    ///
    /// Survives vanilla's own reconciler because Game.ConnectPortals phase 1 only clears a connection
    /// when the partner is missing, the partner's tag differs, or the partner's own connection is None
    /// - it never checks reciprocity, so a same-tagged ring/hub declared here is a stable fixed point
    /// once every member's tag+connection match what this engine intends.
    /// </summary>
    public static class NetworkReassertEngine
    {
        private static float _timer;

        public static void OnUpdate(float dt)
        {
            _timer += dt;
            float interval = FoundationsConfig.ReassertSeconds?.Value ?? 2.0f;
            if (_timer < interval)
            {
                return;
            }
            _timer = 0f;
            Reassert();
        }

        private static void Reassert()
        {
            if (ZDOMan.instance == null || !VersionMigration.DestructivePassesAllowed)
            {
                return;
            }

            int budget = FoundationsConfig.MaxWritesPerTick?.Value ?? 50;
            int written = 0;
            bool allowTagRewrite = FoundationsConfig.AllowTagRewrite?.Value != false;

            // Pair up members of the SAME network sharing the SAME declared tag, ring-style: member i
            // connects to member (i+1) mod N. A 2-member network is therefore a simple mutual pair, the
            // same shape vanilla itself builds - this engine does not invent new topology semantics,
            // only makes a declared one durable. Later waves' topology-specific engines may declare
            // richer shapes by writing their own NetworkDefinition entries with more members; this loop
            // is topology-agnostic (a ring covers pair/hub/ring uniformly - a 2-member ring IS a pair).
            var byNetwork = new System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<(NetworkDefinition net, PortalRecord rec)>>();
            foreach (var (network, record) in NetworkModel.ResolveAll())
            {
                if (!byNetwork.TryGetValue(network.Name, out var list))
                {
                    list = new System.Collections.Generic.List<(NetworkDefinition, PortalRecord)>();
                    byNetwork[network.Name] = list;
                }
                list.Add((network, record));
            }

            foreach (var kvp in byNetwork)
            {
                var members = kvp.Value;
                if (members.Count == 0)
                {
                    continue;
                }
                string intendedTag = members[0].net.Tag;

                for (int i = 0; i < members.Count && written < budget; i++)
                {
                    PortalRecord member = members[i].rec;
                    PortalRecord target = members[members.Count == 1 ? i : (i + 1) % members.Count].rec;
                    bool selfLoopOfOne = members.Count == 1;

                    bool tagWrong = allowTagRewrite && member.Tag != intendedTag;
                    bool connectionWrong = !selfLoopOfOne && member.Connection != target.Uid;

                    if (!tagWrong && !connectionWrong)
                    {
                        continue;
                    }

                    ZDO zdo = ZDOMan.instance.GetZDO(member.Uid);
                    if (zdo == null || !zdo.IsValid())
                    {
                        continue;
                    }

                    try
                    {
                        PortalOwnership.ClaimAndWrite(zdo, z =>
                        {
                            if (tagWrong)
                            {
                                z.Set(ZDOVars.s_tag, intendedTag);
                            }
                            if (connectionWrong)
                            {
                                z.SetConnection(ZDOExtraData.ConnectionType.Portal, target.Uid);
                            }
                        });
                        PortalRecordStore.EnsureRecordId(zdo);
                        PortalRecordStore.SetNetworkId(zdo, kvp.Key);
                        written++;
                    }
                    catch (Exception ex)
                    {
                        PortalDebug.LogError($"[NetworkReassertEngine] failed to reassert network '{kvp.Key}' member {member.Uid}: {ex.GetType().Name}: {ex.Message}");
                    }
                }
            }
        }
    }
}
