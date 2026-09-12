using System.Collections.Generic;
using UnityEngine;
using TortalPortalLite.Core;
using TortalPortalLite.Core.Data;
using TortalPortalLite.Subsystems.Foundations;

namespace TortalPortalLite.Subsystems.Topology
{
    /// <summary>
    /// #207 Corpse-Run Gate - on a player's death the server raises a private, ephemeral portal PAIR
    /// (origin near their bed/the world hub, destination near their tombstone) tagged with a zero-width
    /// per-player prefix so vanilla's pairer keeps the two together and typing the tag is impractical
    /// (not impossible - see failure modes) - both are torn down when the tombstone despawns or a TTL
    /// expires.
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

        private static readonly Dictionary<long, Gate> _activeGates = new Dictionary<long, Gate>();
        private static readonly Dictionary<long, ZDOID> _lastSeenTombstone = new Dictionary<long, ZDOID>();
        private static float _timer;

        public static void OnUpdate(float dt)
        {
            if (TargetedConfig.CorpseRunEnabled?.Value == false || ZDOMan.instance == null)
            {
                return;
            }

            DetectNewDeaths();

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
                RaiseGate(cc.PlayerId, tombId);
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

            Vector3 originAnchor = ResolveOrigin(playerId);
            float originOffset = TargetedConfig.BedOffsetMeters?.Value ?? 3f;
            TargetedWorldGenValidation.Result originResult = TargetedWorldGenValidation.BestCompassOffset(originAnchor, originOffset, samples: 8, allowUnderwater: true);
            Vector3 originPos = originResult.Ok ? originResult.Position : originAnchor + Vector3.forward * originOffset;
            Quaternion originRot = TargetedWorldGenValidation.FacingTowards(originPos, originAnchor);

            Vector3 tombPos = tomb.GetPosition();
            float graveOffset = TargetedConfig.CorpseRunOffsetMeters?.Value ?? 2f;
            TargetedWorldGenValidation.Result graveResult = TargetedWorldGenValidation.BestCompassOffset(tombPos, graveOffset, samples: 8, allowUnderwater: false);
            Vector3 gravePos = graveResult.Ok ? graveResult.Position : tombPos + Vector3.up * 0.2f;
            Quaternion graveRot = TargetedWorldGenValidation.FacingTowards(gravePos, tombPos);

            // Zero-width prefix keeps players from casually typing it (no length cap on a server write -
            // #178's own citation); uid makes it unique per player so an unrelated third portal can never
            // share the tag by coincidence.
            string tag = $"​cr:{playerId}";

            (ZDO origin, ZDO destination) = TargetedPhantomPortalFactory.CreateStandalonePair(
                originPos, originRot, "corpserungate:origin",
                gravePos, graveRot, "corpserungate:dest",
                tag);

            if (origin == null || destination == null)
            {
                return;
            }

            _activeGates[playerId] = new Gate(origin.m_uid, destination.m_uid, tombId, 0f);

            ConnectedCharacter? owner = FindConnected(playerId);
            if (owner.HasValue)
            {
                PlayerNotify.Toast(owner.Value, "A corpse gate has opened beside your bed.");
                TargetedMapPingEngine.PushSavedPin(owner.Value, "Corpse gate", originPos);
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

        private static void TearDown(Gate gate)
        {
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
    }
}
