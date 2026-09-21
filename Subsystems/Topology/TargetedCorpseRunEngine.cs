using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using TortalPortalLite.Core;
using TortalPortalLite.Core.Data;
using TortalPortalLite.Subsystems.Foundations;

namespace TortalPortalLite.Subsystems.Topology
{
    /// <summary>
    /// #207 Corpse-Run Gate - on a player's death the server raises a private, ephemeral, ONE-WAY portal
    /// (origin near their bed/the world hub, leading to their tombstone) tagged with a zero-width
    /// per-player prefix so typing the tag is impractical (not impossible - see failure modes). By
    /// design this is a one-way trip, not a round-trip pair: the grave-side end is never wired back to
    /// the origin (see TargetedPhantomPortalFactory.CreateStandaloneOneWay) - both ends are torn down
    /// together when the tombstone despawns or a TTL expires.
    ///
    /// Death detection reuses #189 Tombstone Engine's own sweep (TargetedTombstoneEngine.
    /// TryGetNewestTombstone) rather than re-implementing a second parallel tombstone tracker or a
    /// SpawnGovernor-style CreateNewZDO/Deserialize hook pair (not present in this codebase): a NEW
    /// tombstone ZDOID appearing for a player IS a new death, and that engine already resolves the
    /// prefab-vs-ownership ambiguity (s_owner shared with Bed/GrapplingPoint) once, correctly.
    ///
    /// Origin resolution prefers #187 Bed Engine's own claimed-bed tracking
    /// (TargetedBedEngine.TryGetMostRecentBed) and falls back to the world's Start Temple
    /// (TargetedLocationRegistry) if the player has never claimed a bed - matching the catalog's own
    /// documented fallback.
    ///
    /// Uses TargetedPhantomPortalFactory.CreateStandalonePair - #207 is the one option in this domain
    /// whose portal pair has NO existing player-placed source on either end, unlike every other engine
    /// here which pairs a phantom to a real portal.
    ///
    /// Placement of both ends goes through TargetedWorldGenValidation.FindPortalPlacement: real
    /// heightmap ground (terraforming included), edge-to-edge clearance from every solid thing's
    /// collider footprint (CorpseRunClearanceMeters), searched outward to CorpseRunSearchRadiusMeters.
    /// A gate is raised only after the tombstone has had SettleSeconds to come to rest (it is a
    /// client-owned rigidbody that drops/slides from the death point), and every tick's maintenance
    /// pass reaps any corpse-run portal this session does not know about - the pair a previous server
    /// run left standing, or one whose bookkeeping was lost - so a restart never leaves stray gates.
    /// </summary>
    public static class TargetedCorpseRunEngine
    {
        private readonly struct Gate
        {
            public readonly ZDOID Origin;
            public readonly ZDOID Destination;
            public readonly ZDOID Tombstone;
            public readonly float Age;

            public Gate(ZDOID origin, ZDOID destination, ZDOID tombstone, float age)
            {
                Origin = origin;
                Destination = destination;
                Tombstone = tombstone;
                Age = age;
            }
        }

        private struct PendingGate
        {
            public ZDOID Tombstone;
            public float Remaining;
        }

        /// <summary>How long a freshly seen tombstone gets to stop falling/sliding before its position is trusted for placement.</summary>
        private const float SettleSeconds = 3f;
        private const string OriginKind = "corpserungate:origin";
        private const string DestinationKind = "corpserungate:dest";

        private static readonly Dictionary<long, Gate> _activeGates = new Dictionary<long, Gate>();
        private static readonly Dictionary<long, PendingGate> _pendingGates = new Dictionary<long, PendingGate>();
        private static readonly Dictionary<long, ZDOID> _lastSeenTombstone = new Dictionary<long, ZDOID>();
        private static readonly HashSet<ZDOID> _indestructiblePortals = new HashSet<ZDOID>();
        private static float _timer;

        public static void OnUpdate(float dt)
        {
            if (TargetedConfig.CorpseRunEnabled?.Value == false || ZDOMan.instance == null)
            {
                return;
            }

            DetectNewDeaths();
            RaiseSettledGates(dt);

            _timer += dt;
            float interval = TargetedConfig.MaintenanceIntervalSeconds?.Value ?? 2f;
            if (_timer < interval)
            {
                return;
            }
            float elapsed = _timer;
            _timer = 0f;
            MaintainGates(elapsed);
        }

        private static void DetectNewDeaths()
        {
            foreach (ConnectedCharacter cc in ConnectedCharacters.All())
            {
                if (cc.PlayerId == 0L || !TargetedTombstoneEngine.TryGetNewestTombstone(cc.PlayerId, out ZDOID tombId) || tombId == ZDOID.None)
                {
                    continue;
                }
                if (_lastSeenTombstone.TryGetValue(cc.PlayerId, out ZDOID lastTomb) && lastTomb == tombId)
                {
                    continue; // same death already handled (or the very first tombstone seen this session with no prior gate expected)
                }
                _lastSeenTombstone[cc.PlayerId] = tombId;
                _pendingGates[cc.PlayerId] = new PendingGate { Tombstone = tombId, Remaining = SettleSeconds };
            }
        }

        private static void RaiseSettledGates(float dt)
        {
            if (_pendingGates.Count == 0)
            {
                return;
            }
            List<long> due = null;
            var keys = new List<long>(_pendingGates.Keys);
            foreach (long playerId in keys)
            {
                PendingGate pending = _pendingGates[playerId];
                pending.Remaining -= dt;
                _pendingGates[playerId] = pending;
                if (pending.Remaining <= 0f)
                {
                    (due ??= new List<long>()).Add(playerId);
                }
            }
            if (due == null)
            {
                return;
            }
            foreach (long playerId in due)
            {
                ZDOID tombId = _pendingGates[playerId].Tombstone;
                _pendingGates.Remove(playerId);
                RaiseGate(playerId, tombId);
            }
        }

        private static void RaiseGate(long playerId, ZDOID tombId)
        {
            ZDO tomb = ZDOMan.instance.GetZDO(tombId);
            if (tomb == null || !tomb.IsValid())
            {
                return;
            }

            // #207's own rule: cap at one active gate per player - tear down any previous gate first.
            if (_activeGates.TryGetValue(playerId, out Gate old))
            {
                TearDown(old);
            }

            float clearance = TargetedConfig.CorpseRunClearanceMeters?.Value ?? 10f;
            float searchRadius = TargetedConfig.CorpseRunSearchRadiusMeters?.Value ?? 40f;

            Vector3 originAnchor = ResolveOrigin(playerId);
            float originMinOffset = TargetedConfig.BedOffsetMeters?.Value ?? 3f;
            Vector3 tombPos = tomb.GetPosition();
            float graveMinOffset = TargetedConfig.CorpseRunOffsetMeters?.Value ?? 2f;

            TargetedWorldGenValidation.Placement origin;
            TargetedWorldGenValidation.Placement grave;
            try
            {
                origin = TargetedWorldGenValidation.FindPortalPlacement(
                    originAnchor, originMinOffset, searchRadius, clearance, ignoreZdo: default);
                // The origin's spot is spoken for before it exists as a ZDO - a death beside one's own
                // bed must not land both ends of the gate in the same clearing.
                grave = TargetedWorldGenValidation.FindPortalPlacement(
                    tombPos, graveMinOffset, searchRadius, clearance, ignoreZdo: tombId, avoid: new[] { origin.Position });
            }
            catch (System.Exception ex)
            {
                // A gate in a poor spot still beats no gate: never let a scan fault eat the death event.
                PortalDebug.LogError($"[CorpseRun] placement scan faulted for player {playerId}, falling back to fixed offsets: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
                origin = TargetedWorldGenValidation.FixedOffset(originAnchor, originMinOffset);
                grave = TargetedWorldGenValidation.FixedOffset(tombPos, graveMinOffset);
            }

            LogPlacement(playerId, "origin", originAnchor, origin);
            LogPlacement(playerId, "grave", tombPos, grave);

            Vector3 originPos = origin.Position;
            Quaternion originRot = origin.Rotation;
            Vector3 gravePos = grave.Position;
            Quaternion graveRot = grave.Rotation;

            // Zero-width prefix keeps players from casually typing it (no length cap on a server write -
            // #178's own citation); uid makes it unique per player so an unrelated third portal can never
            // share the tag by coincidence.
            string tag = $"​cr:{playerId}";

            // One-way trip by design: the origin (bed-side) leads to the grave, but the grave-side end
            // is not wired back - no automatic return leg. Destination portal is created indestructible.
            (ZDO originZdo, ZDO destinationZdo) = TargetedPhantomPortalFactory.CreateStandaloneOneWay(
                originPos, originRot, OriginKind,
                gravePos, graveRot, DestinationKind,
                tag,
                destinationIndestructible: true);

            if (originZdo == null || destinationZdo == null)
            {
                PortalDebug.LogError($"[CorpseRun] gate for player {playerId} could not be minted (portal prefab unresolved?)");
                return;
            }

            lock (_indestructiblePortals)
            {
                _indestructiblePortals.Add(destinationZdo.m_uid);
            }

            _activeGates[playerId] = new Gate(originZdo.m_uid, destinationZdo.m_uid, tombId, 0f);

            ConnectedCharacter? owner = FindConnected(playerId);
            if (owner.HasValue)
            {
                string where = OriginIsBed(playerId) ? "near your bed" : "at the world spawn";
                string note = grave.ReanchoredFromInterior ? " It comes out at the dungeon entrance." : "";
                PlayerNotify.Toast(owner.Value, $"A corpse gate has opened {where}.{note}");
                TargetedMapPingEngine.PushSavedPin(owner.Value, "Corpse gate", originPos);
            }
        }

        private static bool OriginIsBed(long playerId)
        {
            return TargetedBedEngine.TryGetMostRecentBed(playerId, out ZDOID bedId)
                && ZDOMan.instance?.GetZDO(bedId) is ZDO bed && bed.IsValid();
        }

        private static void LogPlacement(long playerId, string end, Vector3 anchor, TargetedWorldGenValidation.Placement placement)
        {
            string line = $"[CorpseRun] {end} gate for player {playerId} near {anchor.x:F1},{anchor.y:F1},{anchor.z:F1}: {placement.Describe()}";
            if (placement.IsGood)
            {
                PortalDebug.LogAlways(line);
            }
            else
            {
                PortalDebug.LogWarning(line + " - consider raising SearchRadiusMeters or lowering ClearanceMeters in section 41");
            }
        }

        private static Vector3 ResolveOrigin(long playerId)
        {
            if (TargetedBedEngine.TryGetMostRecentBed(playerId, out ZDOID bedId))
            {
                ZDO bed = ZDOMan.instance.GetZDO(bedId);
                if (bed != null && bed.IsValid())
                {
                    return bed.GetPosition();
                }
            }
            if (TargetedLocationRegistry.TryGetStartTemple(out Vector3 templePos))
            {
                return templePos;
            }
            return Vector3.zero;
        }

        private static void MaintainGates(float dt)
        {
            ReapUnknownGates();
            if (_activeGates.Count == 0)
            {
                return;
            }
            float ttlSeconds = (TargetedConfig.CorpseRunTtlMinutes?.Value ?? 30f) * 60f;

            List<long> toRemove = null;
            var keys = new List<long>(_activeGates.Keys);
            foreach (long playerId in keys)
            {
                Gate gate = _activeGates[playerId];
                float age = gate.Age + dt;

                ZDO tomb = ZDOMan.instance.GetZDO(gate.Tombstone);
                bool tombGone = tomb == null || !tomb.IsValid();
                bool expired = age >= ttlSeconds;

                if (tombGone || expired)
                {
                    TearDown(gate);
                    (toRemove ??= new List<long>()).Add(playerId);
                    if (tombGone)
                    {
                        NotifyIfConnected(playerId, "Your grave has been recovered - the corpse gate has closed.");
                    }
                    continue;
                }

                // Reinforce destination portal health and server ownership against any client drift
                ZDO destZdo = ZDOMan.instance.GetZDO(gate.Destination);
                if (destZdo != null && destZdo.IsValid())
                {
                    if (destZdo.GetFloat(ZDOVars.s_health, 0f) < 1e9f)
                    {
                        destZdo.Set(ZDOVars.s_health, 1000000000f);
                    }
                    if (destZdo.GetOwner() != ZDOMan.GetSessionID())
                    {
                        destZdo.SetOwner(ZDOMan.GetSessionID());
                        ZDOMan.instance.ForceSendZDO(destZdo.m_uid);
                    }
                }

                _activeGates[playerId] = new Gate(gate.Origin, gate.Destination, gate.Tombstone, age);
            }

            if (toRemove != null)
            {
                foreach (long id in toRemove)
                {
                    _activeGates.Remove(id);
                }
            }
        }

        /// <summary>
        /// Any corpse-run portal (by Kind) that no active gate accounts for is a leftover - from a
        /// previous server run (gate bookkeeping lives only in memory, the ZDOs are persistent) or a
        /// mint that failed halfway - and is destroyed. A tombstone still standing after a restart
        /// gets a fresh gate through DetectNewDeaths' first-seen rule, so nothing is lost.
        /// </summary>
        private static void ReapUnknownGates()
        {
            if (ZDOMan.instance == null)
            {
                return;
            }
            HashSet<ZDOID> known = null;
            foreach (PortalRecord rec in PortalCensus.Latest)
            {
                ZDO zdo = ZDOMan.instance.GetZDO(rec.Uid);
                if (zdo == null || !zdo.IsValid())
                {
                    continue;
                }
                string kind = TargetedPhantomPortalFactory.GetKind(zdo);
                if (kind != OriginKind && kind != DestinationKind)
                {
                    continue;
                }
                if (known == null)
                {
                    known = new HashSet<ZDOID>();
                    foreach (Gate gate in _activeGates.Values)
                    {
                        known.Add(gate.Origin);
                        known.Add(gate.Destination);
                    }
                }
                if (known.Contains(zdo.m_uid))
                {
                    continue;
                }
                PortalDebug.LogAlways($"[CorpseRun] reaping stray {kind} portal {zdo.m_uid} at {zdo.GetPosition()} (not raised by this server run)");
                lock (_indestructiblePortals)
                {
                    _indestructiblePortals.Remove(zdo.m_uid);
                }
                TargetedPhantomPortalFactory.DestroyStandalone(zdo);
            }
        }

        private static void TearDown(Gate gate)
        {
            lock (_indestructiblePortals)
            {
                _indestructiblePortals.Remove(gate.Destination);
            }

            ZDO origin = ZDOMan.instance?.GetZDO(gate.Origin);
            ZDO destination = ZDOMan.instance?.GetZDO(gate.Destination);
            if (origin != null)
            {
                TargetedPhantomPortalFactory.DestroyStandalone(origin);
            }
            if (destination != null)
            {
                TargetedPhantomPortalFactory.DestroyStandalone(destination);
            }
        }

        private static void NotifyIfConnected(long playerId, string message)
        {
            ConnectedCharacter? who = FindConnected(playerId);
            if (who.HasValue)
            {
                PlayerNotify.Toast(who.Value, message);
            }
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

        /// <summary>
        /// Prevents ReleaseNearbyZDOS or client packets from stealing ownership of the tombstone destination
        /// portal away from the dedicated server. Keeping server ownership ensures client damage RPCs
        /// (from mobs attacking the portal) route to the server where no WearNTear instance exists, completely
        /// neutralizing all incoming damage.
        /// </summary>
        [HarmonyPatch]
        public static class ZdoSetOwnerPatch
        {
            [HarmonyPatch(typeof(ZDO), nameof(ZDO.SetOwner), new[] { typeof(long) })]
            [HarmonyPrefix]
            public static bool PrefixSetOwner(ZDO __instance, long uid)
            {
                if (uid == 0L || (ZDOMan.instance != null && uid == ZDOMan.GetSessionID()))
                {
                    return true;
                }
                lock (_indestructiblePortals)
                {
                    if (_indestructiblePortals.Count > 0 && _indestructiblePortals.Contains(__instance.m_uid))
                    {
                        return false;
                    }
                }
                return true;
            }

            [HarmonyPatch(typeof(ZDO), nameof(ZDO.SetOwnerInternal))]
            [HarmonyPrefix]
            public static bool PrefixSetOwnerInternal(ZDO __instance, long uid)
            {
                if (uid == 0L || (ZDOMan.instance != null && uid == ZDOMan.GetSessionID()))
                {
                    return true;
                }
                lock (_indestructiblePortals)
                {
                    if (_indestructiblePortals.Count > 0 && _indestructiblePortals.Contains(__instance.m_uid))
                    {
                        return false;
                    }
                }
                return true;
            }
        }
    }
}
