using System;
using System.Collections.Generic;
using UnityEngine;
using TortalPortalLite.Core;
using TortalPortalLite.Core.Data;
using TortalPortalLite.Subsystems.Foundations;

namespace TortalPortalLite.Subsystems.Topology
{
    /// <summary>
    /// #189 Player's Death Spot / Tombstone - portal to a player's most recent tombstone, read straight
    /// off the TombStone container ZDO: `s_ownerName`/`s_owner` (TombStone.Setup, SERVER decompile
    /// :19845-19853) and `s_timeOfDeath`/`s_spawnPoint` (TombStone.Awake, :19732-19740, written once by
    /// the owning client). Unlike #187 Bed, s_timeOfDeath is already a real timestamp, so "newest
    /// tombstone" needs no extra first-seen bookkeeping - just the max over a player's owned tombstones.
    ///
    /// Tombstone prefabs are discovered by component (TargetedPrefabDiscovery), not by a guessed name
    /// (the catalog's own text flags "Player_tombstone" as an assumed, unverified name). Filtered on the
    /// TombStone component specifically because s_owner is shared with Bed/GrapplingPoint.
    ///
    /// Lifecycle: TombStone.UpdateDespawn destroys the ZDO once empty and not in use
    /// (:19875-19890) - reaped here via the same sweep noticing the ZDO is gone, at which point the
    /// managed phantom is released and the player is told their grave has been recovered.
    /// </summary>
    public static class TargetedTombstoneEngine
    {
        private const string Kind = "Grave";

        private static ZdoSpatialQuery.PrefabSetSweeper _sweeper;
        private static readonly Dictionary<long, ZDOID> _newestTombstoneByPlayer = new Dictionary<long, ZDOID>();
        private static float _timer;

        public static void Initialize()
        {
            EmoteSignals.Register(OnEmote);
        }

        public static void OnUpdate(float dt)
        {
            SweepTick();

            _timer += dt;
            float interval = TargetedConfig.MaintenanceIntervalSeconds?.Value ?? 2f;
            if (_timer < interval)
            {
                return;
            }
            _timer = 0f;
            ReassertAll();
        }

        private static void SweepTick()
        {
            if (TargetedPrefabDiscovery.TombstoneHashes.Count == 0 || ZDOMan.instance == null)
            {
                return;
            }
            _sweeper ??= new ZdoSpatialQuery.PrefabSetSweeper(TargetedPrefabDiscovery.TombstoneHashes);

            var results = new List<ZDO>();
            _sweeper.Advance(20, results);
            foreach (ZDO tomb in results)
            {
                if (tomb == null || !tomb.IsValid())
                {
                    continue;
                }
                long owner = tomb.GetLong(ZDOVars.s_owner, 0L);
                if (owner == 0L)
                {
                    continue;
                }
                long timeOfDeath = tomb.GetLong(ZDOVars.s_timeOfDeath, 0L);

                if (!_newestTombstoneByPlayer.TryGetValue(owner, out ZDOID currentId))
                {
                    _newestTombstoneByPlayer[owner] = tomb.m_uid;
                    continue;
                }
                ZDO current = ZDOMan.instance.GetZDO(currentId);
                long currentTime = current != null && current.IsValid() ? current.GetLong(ZDOVars.s_timeOfDeath, 0L) : -1L;
                if (current == null || !current.IsValid() || timeOfDeath >= currentTime)
                {
                    _newestTombstoneByPlayer[owner] = tomb.m_uid;
                }
            }
        }

        /// <summary>Shared with #207 Corpse-Run Gate, which builds on the same sweep rather than re-implementing tombstone discovery a second time.</summary>
        public static bool TryGetNewestTombstone(long playerId, out ZDOID tombId) => _newestTombstoneByPlayer.TryGetValue(playerId, out tombId);

        private static void OnEmote(ConnectedCharacter who, string emote)
        {
            if (!EmoteSignals.Is(emote, TargetedConfig.ClaimGraveEmote?.Value, "point"))
            {
                return;
            }

            ZDO hub = FindNearestHub(who.Position, 10f);
            if (hub == null)
            {
                return;
            }

            if (!_newestTombstoneByPlayer.TryGetValue(who.PlayerId, out ZDOID tombId))
            {
                PlayerNotify.Toast(who, "No grave found yet.");
                return;
            }
            ZDO tomb = ZDOMan.instance?.GetZDO(tombId);
            if (tomb == null || !tomb.IsValid())
            {
                return;
            }

            PlaceFor(hub, tomb, who.PlayerId);

            string itemNote = "";
            Inventory inv = ZdoInventoryIO.Load(tomb, 6, 4);
            if (inv != null)
            {
                itemNote = $", {inv.NrOfItems()} item(s) waiting";
            }
            PlayerNotify.Toast(who, $"Grave portal set{itemNote}.");
        }

        private static void PlaceFor(ZDO hub, ZDO tomb, long playerId)
        {
            float offset = TargetedConfig.TombstoneOffsetMeters?.Value ?? 3f;
            float safeRadius = TargetedConfig.TombstoneSafeRadius?.Value ?? 15f;
            Vector3 tombPos = tomb.GetPosition();

            TargetedWorldGenValidation.Result result = TargetedWorldGenValidation.BestCompassOffset(tombPos, offset, samples: 8, allowUnderwater: false);
            if (!result.Ok)
            {
                // Death in deep water/lava (#189's own failure mode) - widen the search up to the safe radius.
                result = TargetedWorldGenValidation.BestCompassOffset(tombPos, safeRadius, samples: 12, allowUnderwater: false);
            }
            Vector3 pos = result.Ok ? result.Position : tombPos + Vector3.up * 0.2f;
            Quaternion rot = TargetedWorldGenValidation.FacingTowards(pos, tombPos);

            long ticks = tomb.GetLong(ZDOVars.s_timeOfDeath, 0L);
            string tag = TargetedTagFormat.Named($"Grave: {ResolvePlayerName(playerId)}");
            TargetedPhantomPortalFactory.CreateOrRetarget(hub, pos, rot, tag, $"grave:{playerId}:{ticks}");
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

                ZDO phantom = TargetedPhantomPortalFactory.ResolveExistingPhantom(source);
                if (phantom == null)
                {
                    continue;
                }
                string kind = TargetedPhantomPortalFactory.GetKind(phantom);
                string[] parts = kind.Split(':');
                if (parts.Length < 2 || parts[0] != "grave" || !long.TryParse(parts[1], out long playerId))
                {
                    continue;
                }

                if (!_newestTombstoneByPlayer.TryGetValue(playerId, out ZDOID tombId))
                {
                    continue;
                }
                ZDO tomb = ZDOMan.instance.GetZDO(tombId);
                if (tomb == null || !tomb.IsValid())
                {
                    // #189's own lifecycle rule: the grave was looted/despawned - reap and notify.
                    TargetedPhantomPortalFactory.ReleaseSource(source);
                    NotifyPlayer(playerId, "Your grave has been recovered.");
                    continue;
                }
                PlaceFor(source, tomb, playerId);
            }
        }

        private static void NotifyPlayer(long playerId, string message)
        {
            foreach (ConnectedCharacter cc in ConnectedCharacters.All())
            {
                if (cc.PlayerId == playerId)
                {
                    PlayerNotify.Toast(cc, message);
                    return;
                }
            }
        }

        private static string ResolvePlayerName(long playerId)
        {
            foreach (ConnectedCharacter cc in ConnectedCharacters.All())
            {
                if (cc.PlayerId == playerId)
                {
                    return cc.Name;
                }
            }
            return "Player";
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
