using System;
using System.Collections.Generic;
using UnityEngine;
using TortalPortalLite.Core;
using TortalPortalLite.Core.Data;

namespace TortalPortalLite.Subsystems.WorldOps
{
    /// <summary>Admin declaration for one rotating dungeon gate (economy.json section "rotatingGates").</summary>
    public sealed class EconomyRotatingGateDeclaration
    {
        public string Key = "";
        public EconomyPosition Hub = new EconomyPosition();
        public string Label = "WANDERING DOOR";
        public float RotateSeconds = 604800f;

        /// <summary>Optional Heightmap.Biome bitmask filter (e.g. only Swamp/Mountain locations); 0 = no filter.</summary>
        public int BiomeFilterMask = 0;
    }

    /// <summary>Admin declaration for one treasure-chain leg (economy.json section "treasureChains" - a flat list of legs sharing a ChainKey, ordered by Index).</summary>
    public sealed class EconomyTreasureChainLegDeclaration
    {
        public string ChainKey = "";
        public int Index = 0;
        public EconomyPosition Portal = new EconomyPosition();
        public EconomyPosition? Destination;
        public string Label = "THE LONG ROAD";
    }

    /// <summary>Admin declaration for one world tour ring (economy.json section "worldTourRings").</summary>
    public sealed class EconomyWorldTourRingDeclaration
    {
        public string Key = "";
        public List<EconomyPosition> Stops = new List<EconomyPosition>();
        public string Label = "TOUR";
        public float RotateSeconds = 86400f;
    }

    /// <summary>
    /// #105 Event Topologies - Rotating Gates, Treasure Chains and the World Tour. Three campaign shapes
    /// built entirely out of this domain's own primitives, all "which routes exist right now" and
    /// therefore genuinely enforced:
    ///
    /// Rotating Dungeon Gate: a public hub portal whose far end is a Customs House anchor placed at a
    /// randomly (re)chosen `ZoneSystem.m_locationInstances` entry - anchored to `m_position` (present from
    /// world creation) never a scanned object ZDO (catalog's own citation: the location's interior objects
    /// do not exist until that zone has been ghost-generated for some peer).
    ///
    /// Treasure Chain: N portals, one leg open at a time - leg k+1 exists only once leg k's OWN
    /// EconomyRoutingKernel-composed state reads open (i.e. whatever mechanism the admin also declared
    /// for leg k - a turnstile, a toll - has been satisfied). Progress is a plain int per chain key in
    /// EconomyStateStore, so it survives a restart without a new ZDO key.
    ///
    /// World Tour Ring: a ring topology (i -> i+1 mod N) re-asserted every tick - vanilla's reconciler
    /// tolerates rings indefinitely (pass 1 never checks reciprocity) - whose stop order rotates by one
    /// position every RotateSeconds, persisted as an int offset.
    /// </summary>
    public static class EconomyEventTopologyEngine
    {
        private static int _lastVersion = -1;
        private static List<EconomyRotatingGateDeclaration> _rotatingGates = new List<EconomyRotatingGateDeclaration>();
        private static List<EconomyTreasureChainLegDeclaration> _chainLegs = new List<EconomyTreasureChainLegDeclaration>();
        private static List<EconomyWorldTourRingDeclaration> _rings = new List<EconomyWorldTourRingDeclaration>();
        private static readonly Dictionary<string, string> _currentLocationName = new Dictionary<string, string>();
        private static float _timer;

        public static void OnUpdate(float dt)
        {
            RefreshIfNeeded();
            _timer += dt;
            float interval = EconomyConfig.EventTopologyEvalSeconds?.Value ?? 5f;
            if (_timer < interval)
            {
                return;
            }
            _timer = 0f;

            foreach (EconomyRotatingGateDeclaration decl in _rotatingGates)
            {
                EvaluateRotatingGate(decl);
            }
            EvaluateTreasureChains();
            foreach (EconomyWorldTourRingDeclaration decl in _rings)
            {
                EvaluateRing(decl);
            }
        }

        private static void RefreshIfNeeded()
        {
            if (EconomyRegistry.Version != _lastVersion)
            {
                _lastVersion = EconomyRegistry.Version;
                _rotatingGates = EconomyRegistry.Section<EconomyRotatingGateDeclaration>("rotatingGates");
                _chainLegs = EconomyRegistry.Section<EconomyTreasureChainLegDeclaration>("treasureChains");
                _rings = EconomyRegistry.Section<EconomyWorldTourRingDeclaration>("worldTourRings");
            }
        }

        // ------------------------------------------------------------------
        // Rotating Dungeon Gate
        // ------------------------------------------------------------------

        private static void EvaluateRotatingGate(EconomyRotatingGateDeclaration decl)
        {
            if (string.IsNullOrEmpty(decl.Key) || ZoneSystem.instance == null || ZNet.instance == null)
            {
                return;
            }
            ZDO? hubZdo = EconomyWriteOps.ResolveLivePortal(decl.Hub);
            if (hubZdo == null)
            {
                return;
            }

            string dueKey = $"rotgate_due:{decl.Key}";
            double now = ZNet.instance.GetTimeSeconds();
            long dueMs = EconomyStateStore.GetLong(dueKey, 0L);
            bool needsPick = dueMs == 0L || now >= dueMs / 1000.0;

            string posKey = $"rotgate_pos:{decl.Key}";

            if (needsPick && TryPickLocation(decl.BiomeFilterMask, out Vector3 pos, out string locationName))
            {
                EconomyStateStore.SetLong(dueKey, (long)((now + decl.RotateSeconds) * 1000.0));
                EconomyStateStore.SetFloat($"{posKey}.x", pos.x);
                EconomyStateStore.SetFloat($"{posKey}.y", pos.y);
                EconomyStateStore.SetFloat($"{posKey}.z", pos.z);
                // The friendly location name is cosmetic only (feeds the announce/pin/tag label) and is
                // deliberately NOT round-tripped through EconomyStateStore (long/float only) - kept in a
                // plain in-memory map instead, so a restart simply shows the base label until the next
                // rotation picks a fresh name, rather than needing a third persistence primitive for one
                // display string.
                _currentLocationName[decl.Key] = locationName;
                EconomyCustomsHouseEngine.Release($"rotgate:{decl.Key}");
                string announceLabel = $"{decl.Label} - {locationName}";
                EconomyAnnounce.Broadcast(announceLabel, center: true);
                EconomyAnnounce.PushPinToEveryone(announceLabel, pos);
            }

            Vector3 anchorPos = new Vector3(
                EconomyStateStore.GetFloat($"{posKey}.x", hubZdo.GetPosition().x),
                EconomyStateStore.GetFloat($"{posKey}.y", hubZdo.GetPosition().y),
                EconomyStateStore.GetFloat($"{posKey}.z", hubZdo.GetPosition().z));

            // The anchor sits AT the chosen location, not "in front of" the hub - EnsureAnchorAt (unlike
            // EnsureAnchorFacingGate) takes an absolute world position and only borrows the hub's own tag
            // to mirror, never its transform.
            ZDOID? anchor = EconomyCustomsHouseEngine.EnsureAnchorAt($"rotgate:{decl.Key}", anchorPos, Quaternion.identity, hubZdo);
            if (anchor.HasValue)
            {
                EconomyRoutingKernel.SetDestination(hubZdo.m_uid, anchor.Value);
                EconomyWriteOps.PrewarmToPeersNear(hubZdo.GetPosition(), EconomyConfig.PrewarmRadius?.Value ?? 40f, anchor.Value);
            }
            string gateTag = _currentLocationName.TryGetValue(decl.Key, out string name) ? $"{decl.Label} - {name}" : decl.Label;
            EconomyRoutingKernel.Publish(hubZdo.m_uid, "eventtopology", true, gateTag, 5);
        }

        private static bool TryPickLocation(int biomeMask, out Vector3 pos, out string name)
        {
            pos = Vector3.zero;
            name = "";
            if (ZoneSystem.instance?.m_locationInstances == null || ZoneSystem.instance.m_locationInstances.Count == 0)
            {
                return false;
            }
            var candidates = new List<ZoneSystem.LocationInstance>();
            foreach (ZoneSystem.LocationInstance inst in ZoneSystem.instance.m_locationInstances.Values)
            {
                if (inst.m_location == null)
                {
                    continue;
                }
                if (biomeMask != 0 && ((int)inst.m_location.m_biome & biomeMask) == 0)
                {
                    continue;
                }
                candidates.Add(inst);
            }
            if (candidates.Count == 0)
            {
                return false;
            }
            ZoneSystem.LocationInstance chosen = candidates[UnityEngine.Random.Range(0, candidates.Count)];
            pos = chosen.m_position + Vector3.up * 2f;
            name = chosen.m_location.m_name;
            return true;
        }

        // ------------------------------------------------------------------
        // Treasure Chain
        // ------------------------------------------------------------------

        private static void EvaluateTreasureChains()
        {
            if (_chainLegs.Count == 0)
            {
                return;
            }
            var byChain = new Dictionary<string, List<EconomyTreasureChainLegDeclaration>>();
            foreach (EconomyTreasureChainLegDeclaration leg in _chainLegs)
            {
                if (string.IsNullOrEmpty(leg.ChainKey))
                {
                    continue;
                }
                if (!byChain.TryGetValue(leg.ChainKey, out List<EconomyTreasureChainLegDeclaration> list))
                {
                    list = new List<EconomyTreasureChainLegDeclaration>();
                    byChain[leg.ChainKey] = list;
                }
                list.Add(leg);
            }

            foreach (KeyValuePair<string, List<EconomyTreasureChainLegDeclaration>> kvp in byChain)
            {
                kvp.Value.Sort((a, b) => a.Index.CompareTo(b.Index));
                string progressKey = $"chainleg:{kvp.Key}";
                int unlockedLeg = (int)EconomyStateStore.GetLong(progressKey, 0L);

                for (int i = 0; i < kvp.Value.Count; i++)
                {
                    EconomyTreasureChainLegDeclaration leg = kvp.Value[i];
                    ZDO? legZdo = EconomyWriteOps.ResolveLivePortal(leg.Portal);
                    if (legZdo == null)
                    {
                        continue;
                    }
                    ZDOID legUid = legZdo.m_uid;

                    if (leg.Destination != null)
                    {
                        ZDO? destZdo = EconomyWriteOps.ResolveLivePortal(leg.Destination);
                        if (destZdo != null)
                        {
                            EconomyRoutingKernel.SetDestination(legUid, destZdo.m_uid);
                        }
                    }

                    bool unlocked = leg.Index <= unlockedLeg;
                    string tag = unlocked ? $"{leg.Label} {leg.Index + 1}/{kvp.Value.Count}" : null;
                    EconomyRoutingKernel.Publish(legUid, "chain", unlocked, tag, 10);

                    // Advance progress once this (already-unlocked) leg's OWN other conditions (a
                    // turnstile/toll the admin also declared on it) report satisfied.
                    if (unlocked && leg.Index == unlockedLeg && leg.Index + 1 < kvp.Value.Count
                        && EconomyRoutingKernel.IsCurrentlyOpen(legUid))
                    {
                        EconomyStateStore.SetLong(progressKey, unlockedLeg + 1);
                        EconomyAnnounce.Broadcast($"{leg.Label}: leg {leg.Index + 2} is open.", center: true);
                    }
                }
            }
        }

        // ------------------------------------------------------------------
        // World Tour Ring
        // ------------------------------------------------------------------

        private static void EvaluateRing(EconomyWorldTourRingDeclaration decl)
        {
            if (string.IsNullOrEmpty(decl.Key) || decl.Stops.Count < 2 || ZNet.instance == null)
            {
                return;
            }

            string dueKey = $"ringdue:{decl.Key}";
            string offsetKey = $"ringoffset:{decl.Key}";
            double now = ZNet.instance.GetTimeSeconds();
            long dueMs = EconomyStateStore.GetLong(dueKey, 0L);
            int offset = (int)EconomyStateStore.GetLong(offsetKey, 0L);

            if (dueMs == 0L)
            {
                EconomyStateStore.SetLong(dueKey, (long)((now + decl.RotateSeconds) * 1000.0));
            }
            else if (now >= dueMs / 1000.0)
            {
                offset = (offset + 1) % decl.Stops.Count;
                EconomyStateStore.SetLong(offsetKey, offset);
                EconomyStateStore.SetLong(dueKey, (long)((now + decl.RotateSeconds) * 1000.0));
            }

            int n = decl.Stops.Count;
            for (int i = 0; i < n; i++)
            {
                int shifted = (i + offset) % n;
                ZDO? fromZdo = EconomyWriteOps.ResolveLivePortal(decl.Stops[shifted]);
                if (fromZdo == null)
                {
                    continue;
                }
                int nextIndex = (shifted + 1) % n;
                ZDO? toZdo = EconomyWriteOps.ResolveLivePortal(decl.Stops[nextIndex]);
                if (toZdo == null)
                {
                    continue;
                }
                EconomyRoutingKernel.SetDestination(fromZdo.m_uid, toZdo.m_uid);
                EconomyRoutingKernel.Publish(fromZdo.m_uid, "worldtour", true, decl.Label, 10);
            }
        }
    }
}
