using System.Collections.Generic;
using UnityEngine;
using TortalPortalLite.Core;
using TortalPortalLite.Core.Data;
using TortalPortalLite.Subsystems.Foundations;

namespace TortalPortalLite.Subsystems.Topology
{
    /// <summary>
    /// #190 Player's Ship - portal toward a ship a player is steering, tracked live via a moving
    /// Invisible Anchor (#179/#196) positioned astern rather than the more invasive direct-ZDO-link
    /// mechanism the catalog also describes (writing a Portal connection straight onto the ship's own
    /// ZDO): that variant fights Game.ConnectPortals' 5 s reconciler unless the ship ZDO ALSO carries a
    /// matching s_tag and reciprocal connection, leaves a Portal link on the ship ZDO that must be
    /// scrubbed if the mod is removed, and buys nothing this domain's anchor primitive doesn't already
    /// give more cheaply and more safely.
    ///
    /// Helmsman signal: `shipZdo.GetLong(ZDOVars.s_user)` equals the steering player's stable profile ID
    /// (ShipControlls.RPC_RequestControl writes s_user, SERVER decompile :141326-141342; zeroed on
    /// release). Passenger detection (mechanism (b), ZSyncTransform's m_characterParentSync) is not
    /// implemented - the catalog itself names the helmsman-only signal "the safe subset" and flags
    /// m_characterParentSync as unverified; CapabilityProbe (#202) already probes it
    /// ("player.characterParentSync") for a later wave to build on if desired.
    ///
    /// Arrival is always the vanilla 8 s frozen-snapshot behind wherever the ship was (Player.
    /// UpdateTeleport, SERVER decompile :15337-15352) - there is no way to land on a moving deck.
    /// </summary>
    public static class TargetedShipEngine
    {
        private const string Kind = "Ship";

        private static ZdoSpatialQuery.PrefabSetSweeper _sweeper;
        private static readonly Dictionary<long, ZDOID> _shipByHelmsman = new Dictionary<long, ZDOID>();
        private static readonly Dictionary<ZDOID, (Vector3 pos, float lastSeenTime)> _lastSample = new Dictionary<ZDOID, (Vector3, float)>();
        private static readonly Dictionary<Vector3, float> _releaseGraceRemaining = new Dictionary<Vector3, float>();
        private static float _clock;
        private static float _timer;

        public static void Initialize()
        {
            EmoteSignals.Register(OnEmote);
        }

        public static void OnUpdate(float dt)
        {
            _clock += dt;
            SweepTick();

            _timer += dt;
            float interval = TargetedMovingTargetEngine.TickSeconds;
            if (_timer < interval)
            {
                return;
            }
            float elapsed = _timer;
            _timer = 0f;
            ReassertAll(elapsed);
        }

        private static void SweepTick()
        {
            if (TargetedPrefabDiscovery.ShipHashes.Count == 0 || ZDOMan.instance == null)
            {
                return;
            }
            _sweeper ??= new ZdoSpatialQuery.PrefabSetSweeper(TargetedPrefabDiscovery.ShipHashes);

            var results = new List<ZDO>();
            _sweeper.Advance(20, results);
            foreach (ZDO ship in results)
            {
                if (ship == null || !ship.IsValid())
                {
                    continue;
                }
                long user = ship.GetLong(ZDOVars.s_user, 0L);
                if (user != 0L)
                {
                    _shipByHelmsman[user] = ship.m_uid;
                }
            }
        }

        private static void OnEmote(ConnectedCharacter who, string emote)
        {
            if (!EmoteSignals.Is(emote, TargetedConfig.ClaimShipEmote?.Value, "point"))
            {
                return;
            }

            ZDO hub = FindNearestHub(who.Position, 10f);
            if (hub == null)
            {
                return;
            }

            if (!_shipByHelmsman.TryGetValue(who.PlayerId, out ZDOID shipId))
            {
                PlayerNotify.Toast(who, "You must be steering a ship to link it.");
                return;
            }
            ZDO ship = ZDOMan.instance?.GetZDO(shipId);
            if (ship == null || !ship.IsValid())
            {
                return;
            }

            bool moored = IsMoored(ship);
            PlaceFor(hub, ship, who.PlayerId, who.Name);
            PlayerNotify.Toast(who, $"Linked to {who.Name}'s ship ({(moored ? "moored" : "under way")}).");
        }

        private static bool IsMoored(ZDO ship)
        {
            float threshold = TargetedConfig.ShipMooredSpeedThreshold?.Value ?? 0.5f;
            Vector3 pos = ship.GetPosition();
            if (_lastSample.TryGetValue(ship.m_uid, out (Vector3 pos, float lastSeenTime) last))
            {
                float dt = _clock - last.lastSeenTime;
                if (dt > 0.01f)
                {
                    float speed = (pos - last.pos).magnitude / dt;
                    return speed < threshold;
                }
            }
            return true; // no sample yet - assume moored rather than scaring the player with a false "under way"
        }

        private static void PlaceFor(ZDO hub, ZDO ship, long playerId, string playerName)
        {
            float offset = TargetedConfig.ShipOffsetMeters?.Value ?? 6f;
            Vector3 shipPos = ship.GetPosition();
            Quaternion shipRot = ship.GetRotation();
            Vector3 pos = shipPos + (shipRot * Vector3.back) * offset;
            string tag = TargetedTagFormat.Named($"Ship: {playerName}");
            ZDO anchor = TargetedAnchorFactory.CreateOrRetarget(hub, pos, shipRot, tag, $"ship:{playerId}");
            if (anchor != null)
            {
                TargetedMovingTargetEngine.ForceSendToNearbySourcePeers(hub, anchor);
            }
            _lastSample[ship.m_uid] = (shipPos, _clock);
        }

        private static void ReassertAll(float dt)
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

                ZDO anchor = TargetedAnchorFactory.ResolveExistingAnchor(source);
                if (anchor == null)
                {
                    continue;
                }
                string kind = TargetedAnchorFactory.GetKind(anchor);
                string[] parts = kind.Split(':');
                if (parts.Length != 2 || parts[0] != "ship" || !long.TryParse(parts[1], out long playerId))
                {
                    continue;
                }

                ZDO ship = _shipByHelmsman.TryGetValue(playerId, out ZDOID shipId) ? ZDOMan.instance.GetZDO(shipId) : null;
                bool stillHelming = ship != null && ship.IsValid() && ship.GetLong(ZDOVars.s_user, 0L) == playerId;

                Vector3 graceKey = route.SourcePosition;
                if (!stillHelming)
                {
                    float remaining = (_releaseGraceRemaining.TryGetValue(graceKey, out float r) ? r : TargetedConfig.ShipReleaseGraceSeconds?.Value ?? 30f) - dt;
                    if (remaining <= 0f)
                    {
                        TargetedAnchorFactory.ReleaseSource(source);
                        _releaseGraceRemaining.Remove(graceKey);
                        continue;
                    }
                    _releaseGraceRemaining[graceKey] = remaining;
                    if (ship == null || !ship.IsValid())
                    {
                        continue; // ship itself is gone - nothing left to track during the grace period
                    }
                }
                else
                {
                    _releaseGraceRemaining.Remove(graceKey);
                }

                Vector3 shipPos = ship.GetPosition();
                Quaternion shipRot = ship.GetRotation();
                float offset = TargetedConfig.ShipOffsetMeters?.Value ?? 6f;
                Vector3 targetPos = shipPos + (shipRot * Vector3.back) * offset;

                if (TargetedMovingTargetEngine.MovedEnough(anchor.GetPosition(), targetPos) && TargetedAnchorFactory.MoveTo(anchor, targetPos, shipRot))
                {
                    TargetedMovingTargetEngine.ForceSendToNearbySourcePeers(source, anchor);
                }
                _lastSample[ship.m_uid] = (shipPos, _clock);
            }
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
