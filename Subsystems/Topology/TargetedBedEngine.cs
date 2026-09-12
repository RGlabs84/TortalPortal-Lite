using System;
using System.Collections.Generic;
using UnityEngine;
using TortalPortalLite.Core;
using TortalPortalLite.Core.Data;
using TortalPortalLite.Subsystems.Foundations;

namespace TortalPortalLite.Subsystems.Topology
{
    /// <summary>
    /// #187 Player's Bed - portal to a bed a player has claimed, found by scanning bed ZDOs for
    /// ZDOVars.s_owner == the player's stable profile ID (Bed.RPC_SetOwner writes s_owner/s_ownerName,
    /// SERVER decompile :119813-119819 - persistent, offline-readable). Bed prefabs are discovered by
    /// component (TargetedPrefabDiscovery), not by guessed name, and indexed via a resumable
    /// ZdoSpatialQuery.PrefabSetSweeper rather than a one-shot GetAllZDOIDsWithHash scan, since s_owner
    /// is a long field shared with TombStone/GrapplingPoint (#187's own citation) and there is no way to
    /// ask "every ZDO with s_owner == X" without also matching those.
    ///
    /// Ambiguity rule (vanilla lets a player own unlimited beds and never clears s_owner, Bed.Interact
    /// :119670-119725): this engine tracks the tick each bed ZDO's s_owner was FIRST observed to match a
    /// given player and, on claim, picks that player's most-recently-first-seen-owned bed - a practical
    /// stand-in for "the bed you most recently claimed" without needing a client-side timestamp.
    ///
    /// Hub source portals are declared in targeted_routes.json with Kind="Bed" (TargetedRouteStore,
    /// same file every other targeted destination uses); which PLAYER a given "Home" hub currently
    /// points at is stored on the phantom's own TargetedZdoKeys.Kind field ("bed:&lt;playerId&gt;"), so it
    /// survives a world reload without a second, parallel store.
    /// </summary>
    public static class TargetedBedEngine
    {
        private const string Kind = "Bed";

        private static ZdoSpatialQuery.PrefabSetSweeper _sweeper;
        private static readonly Dictionary<long, ZDOID> _mostRecentBedByPlayer = new Dictionary<long, ZDOID>();
        private static readonly Dictionary<ZDOID, long> _firstSeenOwnerTickByBed = new Dictionary<ZDOID, long>();
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
            if (TargetedPrefabDiscovery.BedHashes.Count == 0 || ZDOMan.instance == null)
            {
                return;
            }
            _sweeper ??= new ZdoSpatialQuery.PrefabSetSweeper(TargetedPrefabDiscovery.BedHashes);

            var results = new List<ZDO>();
            _sweeper.Advance(20, results);
            foreach (ZDO bed in results)
            {
                if (bed == null || !bed.IsValid())
                {
                    continue;
                }
                long owner = bed.GetLong(ZDOVars.s_owner, 0L);
                if (owner == 0L)
                {
                    continue;
                }

                if (!_firstSeenOwnerTickByBed.TryGetValue(bed.m_uid, out long firstSeen))
                {
                    firstSeen = DateTime.UtcNow.Ticks;
                    _firstSeenOwnerTickByBed[bed.m_uid] = firstSeen;
                }

                bool replace = !_mostRecentBedByPlayer.TryGetValue(owner, out ZDOID current)
                    || !_firstSeenOwnerTickByBed.TryGetValue(current, out long currentTick)
                    || firstSeen >= currentTick;
                if (replace)
                {
                    _mostRecentBedByPlayer[owner] = bed.m_uid;
                }
            }
        }

        /// <summary>Shared with #207 Corpse-Run Gate, which resolves a death's origin from the same claimed-bed tracking rather than re-implementing bed discovery a second time.</summary>
        public static bool TryGetMostRecentBed(long playerId, out ZDOID bedId) => _mostRecentBedByPlayer.TryGetValue(playerId, out bedId);

        private static void OnEmote(ConnectedCharacter who, string emote)
        {
            if (!EmoteSignals.Is(emote, TargetedConfig.ClaimBedEmote?.Value, "wave"))
            {
                return;
            }

            ZDO hub = FindNearestHub(who.Position, 10f);
            if (hub == null)
            {
                return;
            }

            if (!_mostRecentBedByPlayer.TryGetValue(who.PlayerId, out ZDOID bedId))
            {
                PlayerNotify.Toast(who, "No claimed bed found yet.");
                return;
            }
            ZDO bed = ZDOMan.instance?.GetZDO(bedId);
            if (bed == null || !bed.IsValid())
            {
                return;
            }

            PlaceFor(hub, bed, who.PlayerId, who.Name);
            PlayerNotify.Toast(who, "Home portal set to your bed.");
        }

        private static void PlaceFor(ZDO hub, ZDO bed, long playerId, string playerName)
        {
            float offset = TargetedConfig.BedOffsetMeters?.Value ?? 3f;
            // #188's own integration point: prefer the inferred custom spawn point over the raw claimed
            // bed position when one has been observed (respawn/sleep/login leak), since that is more
            // often where the player actually respawns than an arbitrary other bed they once claimed.
            Vector3 bedPos = TargetedSpawnPointEngine.TryGetInferredHome(playerId, out Vector3 inferred) ? inferred : bed.GetPosition();
            TargetedWorldGenValidation.Result result = TargetedWorldGenValidation.BestCompassOffset(bedPos, offset, samples: 8, allowUnderwater: true);
            Vector3 pos = result.Ok ? result.Position : bedPos + Vector3.forward * offset;
            Quaternion rot = TargetedWorldGenValidation.FacingTowards(pos, bedPos);
            string tag = TargetedTagFormat.Named($"Home: {playerName}");
            TargetedPhantomPortalFactory.CreateOrRetarget(hub, pos, rot, tag, $"bed:{playerId}");
        }

        /// <summary>Re-derives each managed Bed hub's current claim from its own phantom's Kind field (survives a reload) and reaps it if the bed ZDO is gone.</summary>
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
                    continue; // nobody has claimed this hub yet - nothing to reassert
                }
                string kind = TargetedPhantomPortalFactory.GetKind(phantom);
                if (!kind.StartsWith("bed:", StringComparison.Ordinal) || !long.TryParse(kind.Substring(4), out long playerId))
                {
                    continue;
                }

                if (!_mostRecentBedByPlayer.TryGetValue(playerId, out ZDOID bedId))
                {
                    continue; // haven't (re)found this player's bed yet this session - leave the existing phantom alone
                }
                ZDO bed = ZDOMan.instance.GetZDO(bedId);
                if (bed == null || !bed.IsValid())
                {
                    TargetedPhantomPortalFactory.ReleaseSource(source);
                    continue;
                }
                PlaceFor(source, bed, playerId, ResolvePlayerName(playerId));
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
