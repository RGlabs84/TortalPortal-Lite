using System.Collections.Generic;
using UnityEngine;
using TortalPortalLite.Core;
using TortalPortalLite.Core.Data;
using TortalPortalLite.Core.Hooks;
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
    /// Two promises this engine has to keep, and what each one costs:
    ///
    /// BOTH ENDS ALWAYS APPEAR. Death detection is driven off the tombstone table
    /// (TargetedTombstoneEngine), never off the connected-player list, so a player who died and
    /// disconnected before a gate could be raised still gets one - standing and waiting when they return,
    /// with the notification deferred until they are back. Detection latency is closed from the other
    /// side too: a player whose character ZDO is replaced (respawn) or disappears (logout) opens a short
    /// death watch that scans their last known position directly, which is where a fresh tombstone is, so
    /// a death resolves in a tick or two rather than waiting for the paced background sweep's cursor.
    /// Nothing is ever dropped silently: a raise that cannot complete is retried with backoff and the
    /// death is only marked handled once a gate is genuinely standing. And every maintenance pass checks
    /// BOTH ends and re-mints whichever one is missing at the position it was placed at, so losing an end
    /// to anything at all is self-healing rather than terminal.
    ///
    /// The single biggest reason this feature was unreliable before 1.0.8 is not in this file: vanilla's
    /// own Game.ConnectPortals reconciler tears down any portal link whose partner points at None
    /// (:100474-100481), and a one-way gate's destination points at None BY DESIGN - so vanilla cleared
    /// the origin's connection within 5 s of every raise, and the phantom factory's generic "connection
    /// target gone -> reap" rule then destroyed the bed-side gate a moment later. Both halves are fixed:
    /// the ends are minted engine-owned so the factory leaves them alone, and OnVanillaReconcilePass
    /// re-asserts the one-way link from a ConnectPortals POSTFIX - synchronously, in the same frame, so no
    /// client ever receives the intermediate disconnected state.
    ///
    /// THE PORTALS CANNOT BE DESTROYED. Both ends - not just the grave side - are registered with
    /// TargetedPortalProtection, which owns the ownership lock, the removal veto and the WearNTear no-ops.
    /// Read that file's header for why server ownership alone is a complete answer to damage, including a
    /// troll's AoE: damage is never applied by the attacker, only ever by the ZDO's owner.
    ///
    /// Origin resolution prefers #187 Bed Engine's own claimed-bed tracking
    /// (TargetedBedEngine.TryGetMostRecentBed) and falls back to the world's Start Temple
    /// (TargetedLocationRegistry), then to the player's own last known position - never to (0,0,0), which
    /// is open ocean on most seeds.
    ///
    /// Placement of both ends goes through TargetedWorldGenValidation.FindPortalPlacement: real
    /// heightmap ground (terraforming included), edge-to-edge clearance from every solid thing's
    /// collider footprint graded down from CorpseRunClearanceMeters but never below MinRoomMeters,
    /// searched outward to CorpseRunSearchRadiusMeters and wider if that is not enough.
    /// </summary>
    public static class TargetedCorpseRunEngine
    {
        private sealed class Gate
        {
            public long PlayerId;
            public ZDOID Origin;
            public ZDOID Destination;
            public ZDOID Tombstone;
            public float Age;
            public string Tag = "";
            public Vector3 OriginPos;
            public Quaternion OriginRot;
            public Vector3 GravePos;
            public Quaternion GraveRot;
            public bool OriginIsBed;
            public bool GraveFromInterior;
            public int Remints;
        }

        private struct PendingGate
        {
            public ZDOID Tombstone;
            public float Remaining;
            public int Attempts;
        }

        private struct DeathWatch
        {
            public Vector3 LastKnownPosition;
            public float Remaining;
            public float NextScanIn;
        }

        private struct TrackedPlayer
        {
            public ZDOID Character;
            public Vector3 Position;
        }

        /// <summary>How long a freshly seen tombstone gets to stop falling/sliding before its position is trusted for placement.</summary>
        private const float SettleSeconds = 3f;
        /// <summary>Wait between attempts when a raise could not complete, and how many attempts before giving up on that death.</summary>
        private const float RetrySeconds = 2f;
        private const int MaxAttempts = 15;
        /// <summary>How long a death watch keeps scanning a vanished player's last position, and how often.</summary>
        private const float DeathWatchSeconds = 30f;
        private const float DeathWatchScanSeconds = 0.5f;
        /// <summary>A tombstone drops and slides from the death point but never far; this covers it with room to spare.</summary>
        private const float DeathWatchRadius = 64f;
        /// <summary>Cadence of the "gates are up and holding" heartbeat, so the promise is observable in the log rather than assumed.</summary>
        private const float StatusSeconds = 300f;

        private const string OriginKind = "corpserungate:origin";
        private const string DestinationKind = "corpserungate:dest";

        private static readonly Dictionary<long, Gate> _activeGates = new Dictionary<long, Gate>();
        private static readonly Dictionary<long, PendingGate> _pendingGates = new Dictionary<long, PendingGate>();
        private static readonly Dictionary<long, ZDOID> _handledTombstone = new Dictionary<long, ZDOID>();
        private static readonly Dictionary<long, DeathWatch> _deathWatches = new Dictionary<long, DeathWatch>();
        private static readonly Dictionary<long, TrackedPlayer> _trackedPlayers = new Dictionary<long, TrackedPlayer>();
        private static readonly HashSet<long> _pendingAnnounce = new HashSet<long>();
        private static readonly List<KeyValuePair<long, ZDOID>> _trackedTombstones = new List<KeyValuePair<long, ZDOID>>();
        private static readonly HashSet<long> _seenThisTick = new HashSet<long>();
        private static float _timer;
        private static float _statusTimer;
        private static bool _hookRegistered;

        public static void Initialize()
        {
            if (_hookRegistered)
            {
                return;
            }
            _hookRegistered = true;
            // Priority -1000: the same "correct vanilla's pass in the same frame it ran" slot
            // RoutingPairingAuthorityEngine uses. A postfix, never a prefix veto - see the hook's header.
            ConnectPortalsHook.RegisterPostfix(-1000, OnVanillaReconcilePass);
        }

        public static void OnUpdate(float dt)
        {
            if (TargetedConfig.CorpseRunEnabled?.Value == false || ZDOMan.instance == null)
            {
                return;
            }

            TrackPlayers();
            RunDeathWatches(dt);
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
            Heartbeat(elapsed);
        }

        // ------------------------------------------------------------------------------ death detection

        /// <summary>
        /// Keeps each connected player's character ZDO and position current, and opens a death watch the
        /// moment a tracked player's character is replaced or disappears. Respawning mints a brand-new
        /// Player ZDO (a new ZDOID arrives via RPC_CharacterID), so "the character behind this playerID
        /// changed" is a death; "it vanished" is a death or a logout, and both are worth scanning - the
        /// logout case simply finds no new tombstone.
        /// </summary>
        private static void TrackPlayers()
        {
            List<ConnectedCharacter> connected = ConnectedCharacters.All();
            HashSet<long> seen = _seenThisTick;
            seen.Clear();

            foreach (ConnectedCharacter cc in connected)
            {
                long playerId = cc.PlayerId;
                if (playerId == 0L)
                {
                    continue;
                }
                seen.Add(playerId);
                ZDOID character = cc.Zdo.m_uid;
                if (_trackedPlayers.TryGetValue(playerId, out TrackedPlayer previous) && previous.Character != character)
                {
                    OpenDeathWatch(playerId, previous.Position);
                }
                _trackedPlayers[playerId] = new TrackedPlayer { Character = character, Position = cc.Position };

                // A gate raised while they were away is announced the moment they are back.
                if (_pendingAnnounce.Contains(playerId) && _activeGates.TryGetValue(playerId, out Gate waiting))
                {
                    _pendingAnnounce.Remove(playerId);
                    Announce(cc, waiting);
                }
            }

            if (_trackedPlayers.Count == seen.Count)
            {
                return;
            }
            List<long> gone = null;
            foreach (KeyValuePair<long, TrackedPlayer> kvp in _trackedPlayers)
            {
                if (!seen.Contains(kvp.Key))
                {
                    (gone ??= new List<long>()).Add(kvp.Key);
                }
            }
            if (gone == null)
            {
                return;
            }
            foreach (long playerId in gone)
            {
                OpenDeathWatch(playerId, _trackedPlayers[playerId].Position);
                _trackedPlayers.Remove(playerId);
            }
        }

        private static void OpenDeathWatch(long playerId, Vector3 lastKnownPosition)
        {
            _deathWatches[playerId] = new DeathWatch
            {
                LastKnownPosition = lastKnownPosition,
                Remaining = DeathWatchSeconds,
                NextScanIn = 0f,
            };
        }

        /// <summary>
        /// Scans a vanished player's last known position for a tombstone, repeatedly and briefly. This is
        /// purely a latency shortcut - the background sweep would find the same tombstone eventually, just
        /// tens of seconds later on a built-up map.
        /// </summary>
        private static void RunDeathWatches(float dt)
        {
            if (_deathWatches.Count == 0)
            {
                return;
            }
            List<long> expired = null;
            var keys = new List<long>(_deathWatches.Keys);
            foreach (long playerId in keys)
            {
                DeathWatch watch = _deathWatches[playerId];
                watch.Remaining -= dt;
                watch.NextScanIn -= dt;
                if (watch.NextScanIn <= 0f)
                {
                    watch.NextScanIn = DeathWatchScanSeconds;
                    TargetedTombstoneEngine.ObserveNear(watch.LastKnownPosition, DeathWatchRadius);
                }
                if (watch.Remaining <= 0f)
                {
                    (expired ??= new List<long>()).Add(playerId);
                }
                _deathWatches[playerId] = watch;
            }
            if (expired != null)
            {
                foreach (long playerId in expired)
                {
                    _deathWatches.Remove(playerId);
                }
            }
        }

        /// <summary>
        /// Queues a gate for every tracked tombstone this engine has not already raised one for - online
        /// or not. A tombstone standing at boot counts as a new death: its grave was never recovered, so a
        /// gate to it is exactly as useful as one raised the moment it appeared.
        /// </summary>
        private static void DetectNewDeaths()
        {
            TargetedTombstoneEngine.Tracked(_trackedTombstones);
            for (int i = 0; i < _trackedTombstones.Count; i++)
            {
                long playerId = _trackedTombstones[i].Key;
                ZDOID tombId = _trackedTombstones[i].Value;
                if (playerId == 0L || tombId == ZDOID.None)
                {
                    continue;
                }
                if (_handledTombstone.TryGetValue(playerId, out ZDOID handled) && handled == tombId)
                {
                    continue;
                }
                if (_pendingGates.TryGetValue(playerId, out PendingGate queued) && queued.Tombstone == tombId)
                {
                    continue;
                }
                if (IsTooOld(tombId))
                {
                    // Recorded as handled so an age-barred grave is not re-examined every tick.
                    _handledTombstone[playerId] = tombId;
                    continue;
                }
                _pendingGates[playerId] = new PendingGate { Tombstone = tombId, Remaining = SettleSeconds, Attempts = 0 };
            }
        }

        private static bool IsTooOld(ZDOID tombId)
        {
            float maxMinutes = TargetedConfig.CorpseRunMaxGraveAgeMinutes?.Value ?? 0f;
            if (maxMinutes <= 0f)
            {
                return false;
            }
            ZDO tomb = ZDOMan.instance.GetZDO(tombId);
            System.TimeSpan age = TargetedTombstoneEngine.AgeOf(tomb);
            // An unreadable timestamp is "unknown", never "ancient" - barring a gate on a missing field
            // would silently drop real deaths.
            return age != System.TimeSpan.MaxValue && age.TotalMinutes > maxMinutes;
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
                PendingGate pending = _pendingGates[playerId];
                if (TryRaiseGate(playerId, pending.Tombstone, out bool retry))
                {
                    _pendingGates.Remove(playerId);
                    _handledTombstone[playerId] = pending.Tombstone;
                    continue;
                }
                if (!retry)
                {
                    // The grave itself is gone - nothing left to gate to, and not a failure.
                    _pendingGates.Remove(playerId);
                    _handledTombstone[playerId] = pending.Tombstone;
                    continue;
                }
                pending.Attempts++;
                if (pending.Attempts >= MaxAttempts)
                {
                    _pendingGates.Remove(playerId);
                    _handledTombstone[playerId] = pending.Tombstone;
                    PortalDebug.LogError($"[CorpseRun] gave up raising a gate for player {playerId} after {pending.Attempts} attempts - their grave at {DescribePosition(pending.Tombstone)} has no gate. The portal prefab registry is the usual cause (see VersionMigration's PortalPrefabHash check).");
                    continue;
                }
                pending.Remaining = RetrySeconds;
                _pendingGates[playerId] = pending;
                if (pending.Attempts % 5 == 0)
                {
                    PortalDebug.LogWarning($"[CorpseRun] still retrying the gate for player {playerId} (attempt {pending.Attempts} of {MaxAttempts}).");
                }
            }
        }

        // ------------------------------------------------------------------------------------- raising

        /// <summary>
        /// Raises both ends. Returns false with retry=true when something transient got in the way (the
        /// portal prefab not resolved yet, a half-failed mint), false with retry=false when the grave
        /// itself is gone and there is nothing left to build a gate to.
        /// </summary>
        private static bool TryRaiseGate(long playerId, ZDOID tombId, out bool retry)
        {
            retry = true;
            ZDO tomb = ZDOMan.instance.GetZDO(tombId);
            if (tomb == null || !tomb.IsValid())
            {
                retry = false;
                return false;
            }

            if (PortalRegistry.PrefabHashes.Count == 0)
            {
                // Nothing can be minted yet (pre-world-ready, or a build whose Game.PortalPrefabHash is
                // empty) - found out here rather than after paying for two full placement scans, since
                // this is the one failure the retry loop actually waits on.
                return false;
            }

            // #207's own rule: cap at one active gate per player - tear down any previous gate first.
            if (_activeGates.TryGetValue(playerId, out Gate old))
            {
                TearDown(old);
                _activeGates.Remove(playerId);
            }

            float clearance = TargetedConfig.CorpseRunClearanceMeters?.Value ?? 10f;
            float searchRadius = TargetedConfig.CorpseRunSearchRadiusMeters?.Value ?? 40f;
            float minRoom = TargetedConfig.CorpseRunMinRoomMeters?.Value ?? TargetedWorldGenValidation.DefaultMinRoom;

            Vector3 originAnchor = ResolveOrigin(playerId, out bool originIsBed);
            float originMinOffset = TargetedConfig.BedOffsetMeters?.Value ?? 3f;
            Vector3 tombPos = tomb.GetPosition();
            float graveMinOffset = TargetedConfig.CorpseRunOffsetMeters?.Value ?? 2f;

            TargetedWorldGenValidation.Placement origin;
            TargetedWorldGenValidation.Placement grave;
            try
            {
                origin = TargetedWorldGenValidation.FindPortalPlacement(
                    originAnchor, originMinOffset, searchRadius, clearance, ignoreZdo: default, avoid: null, minRoom: minRoom);
                // The origin's spot is spoken for before it exists as a ZDO - a death beside one's own
                // bed must not land both ends of the gate in the same clearing.
                grave = TargetedWorldGenValidation.FindPortalPlacement(
                    tombPos, graveMinOffset, searchRadius, clearance, ignoreZdo: tombId, avoid: new[] { origin.Position }, minRoom: minRoom);
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

            // Zero-width prefix keeps players from casually typing it (no length cap on a server write -
            // #178's own citation); uid makes it unique per player so an unrelated third portal can never
            // share the tag by coincidence.
            string tag = $"​cr:{playerId}";

            // One-way trip by design: the origin (bed-side) leads to the grave, but the grave-side end
            // is not wired back - no automatic return leg. BOTH ends are minted indestructible.
            (ZDO originZdo, ZDO destinationZdo) = TargetedPhantomPortalFactory.CreateStandaloneOneWay(
                origin.Position, origin.Rotation, OriginKind,
                grave.Position, grave.Rotation, DestinationKind,
                tag,
                indestructible: true);

            if (originZdo == null || destinationZdo == null)
            {
                PortalDebug.LogError($"[CorpseRun] gate for player {playerId} could not be minted (portal prefab unresolved?) - will retry.");
                return false;
            }

            var gate = new Gate
            {
                PlayerId = playerId,
                Origin = originZdo.m_uid,
                Destination = destinationZdo.m_uid,
                Tombstone = tombId,
                Age = 0f,
                Tag = tag,
                OriginPos = origin.Position,
                OriginRot = origin.Rotation,
                GravePos = grave.Position,
                GraveRot = grave.Rotation,
                OriginIsBed = originIsBed,
                GraveFromInterior = grave.ReanchoredFromInterior,
            };
            TargetedPortalProtection.Protect(gate.Origin);
            TargetedPortalProtection.Protect(gate.Destination);
            _activeGates[playerId] = gate;

            ConnectedCharacter? owner = FindConnected(playerId);
            if (owner.HasValue)
            {
                Announce(owner.Value, gate);
            }
            else
            {
                // Raised for an offline player: the gate stands, the telling waits for them.
                _pendingAnnounce.Add(playerId);
                PortalDebug.LogAlways($"[CorpseRun] gate raised for offline player {playerId} - it stands at their {(originIsBed ? "bed" : "spawn")} until they return.");
            }
            return true;
        }

        private static void Announce(ConnectedCharacter who, Gate gate)
        {
            string where = gate.OriginIsBed ? "near your bed" : "at the world spawn";
            string note = gate.GraveFromInterior ? " It comes out at the dungeon entrance." : "";
            PlayerNotify.Toast(who, $"A corpse gate has opened {where}.{note}");
            TargetedMapPingEngine.PushSavedPin(who, "Corpse gate", gate.OriginPos);
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
                PortalDebug.LogWarning(line + " - nowhere within the widened search radius had even MinRoomMeters of room; consider raising SearchRadiusMeters or lowering MinRoomMeters in section 41");
            }
        }

        /// <summary>
        /// Where the bed-side end belongs: the player's own claimed bed, else the world's Start Temple,
        /// else wherever they last stood. Never (0,0,0) - on most seeds that is open ocean, and a gate
        /// floating out there is worse than no gate at all.
        /// </summary>
        private static Vector3 ResolveOrigin(long playerId, out bool isBed)
        {
            isBed = false;
            if (TargetedBedEngine.TryGetMostRecentBed(playerId, out ZDOID bedId))
            {
                ZDO bed = ZDOMan.instance.GetZDO(bedId);
                if (bed != null && bed.IsValid())
                {
                    isBed = true;
                    return bed.GetPosition();
                }
            }
            if (TargetedLocationRegistry.TryGetStartTemple(out Vector3 templePos))
            {
                return templePos;
            }
            if (_trackedPlayers.TryGetValue(playerId, out TrackedPlayer tracked) && tracked.Position != Vector3.zero)
            {
                PortalDebug.LogWarning($"[CorpseRun] player {playerId} has no claimed bed and the Start Temple could not be resolved - anchoring the origin end at their last known position instead.");
                return tracked.Position;
            }
            PortalDebug.LogWarning($"[CorpseRun] player {playerId} has no bed, no resolvable Start Temple and no known position - the origin end is anchored at the world origin, which is usually ocean.");
            return Vector3.zero;
        }

        // --------------------------------------------------------------------------------- maintenance

        private static void MaintainGates(float dt)
        {
            ReapUnknownGates();
            if (_activeGates.Count == 0)
            {
                return;
            }
            float ttlSeconds = (TargetedConfig.CorpseRunGateTtlMinutes?.Value ?? 0f) * 60f;

            List<long> toRemove = null;
            var keys = new List<long>(_activeGates.Keys);
            foreach (long playerId in keys)
            {
                Gate gate = _activeGates[playerId];
                gate.Age += dt;

                ZDO tomb = ZDOMan.instance.GetZDO(gate.Tombstone);
                bool tombGone = tomb == null || !tomb.IsValid();
                bool expired = ttlSeconds > 0f && gate.Age >= ttlSeconds;

                if (tombGone || expired)
                {
                    TearDown(gate);
                    (toRemove ??= new List<long>()).Add(playerId);
                    if (tombGone)
                    {
                        NotifyIfConnected(playerId, "Your grave has been recovered - the corpse gate has closed.");
                    }
                    else
                    {
                        NotifyIfConnected(playerId, "Your corpse gate has expired.");
                    }
                    continue;
                }

                EnsureBothEnds(gate);
            }

            if (toRemove != null)
            {
                foreach (long id in toRemove)
                {
                    _activeGates.Remove(id);
                    _pendingAnnounce.Remove(id);
                }
            }
        }

        /// <summary>
        /// The "always on both ends" guarantee, enforced rather than assumed: whichever end is missing is
        /// re-minted at the position it was placed at, the one-way link is re-established, and both ends'
        /// health and server ownership are re-asserted. Under the protection layers nothing here should
        /// ever have to act - Remints and TargetedPortalProtection's counters are how we find out
        /// otherwise instead of guessing.
        /// </summary>
        private static void EnsureBothEnds(Gate gate)
        {
            ZDO origin = ZDOMan.instance.GetZDO(gate.Origin);
            ZDO destination = ZDOMan.instance.GetZDO(gate.Destination);
            bool originGone = origin == null || !origin.IsValid();
            bool destinationGone = destination == null || !destination.IsValid();

            if (originGone)
            {
                TargetedPortalProtection.Release(gate.Origin);
                origin = TargetedPhantomPortalFactory.MintEngineOwned(gate.OriginPos, gate.OriginRot, gate.Tag, OriginKind, indestructible: true);
                if (origin != null)
                {
                    gate.Origin = origin.m_uid;
                    gate.Remints++;
                    TargetedPortalProtection.Protect(gate.Origin);
                    PortalDebug.LogWarning($"[CorpseRun] the origin end of player {gate.PlayerId}'s gate had gone missing - re-minted at {gate.OriginPos.x:F0},{gate.OriginPos.y:F0},{gate.OriginPos.z:F0} (re-mints for this gate: {gate.Remints}).");
                }
            }
            if (destinationGone)
            {
                TargetedPortalProtection.Release(gate.Destination);
                destination = TargetedPhantomPortalFactory.MintEngineOwned(gate.GravePos, gate.GraveRot, gate.Tag, DestinationKind, indestructible: true);
                if (destination != null)
                {
                    gate.Destination = destination.m_uid;
                    gate.Remints++;
                    TargetedPortalProtection.Protect(gate.Destination);
                    PortalDebug.LogWarning($"[CorpseRun] the grave end of player {gate.PlayerId}'s gate had gone missing - re-minted at {gate.GravePos.x:F0},{gate.GravePos.y:F0},{gate.GravePos.z:F0} (re-mints for this gate: {gate.Remints}).");
                }
            }

            if (origin == null || destination == null)
            {
                return;
            }
            if (originGone || destinationGone || origin.GetConnectionZDOID(ZDOExtraData.ConnectionType.Portal) != destination.m_uid)
            {
                TargetedPhantomPortalFactory.LinkOneWay(origin, destination);
            }
            TargetedPortalProtection.Reassert(origin);
            TargetedPortalProtection.Reassert(destination);
        }

        /// <summary>
        /// Vanilla's Game.ConnectPortals clears the origin's connection on every pass, because a one-way
        /// gate's destination points at None and that is exactly the shape its phase-1 reconciler treats
        /// as a broken link (:100474-100481). Restoring it from the POSTFIX of that same call puts it back
        /// synchronously, before ZDOMan's own send pass runs, so the intermediate state is never observed
        /// by any client.
        /// </summary>
        private static void OnVanillaReconcilePass()
        {
            if (_activeGates.Count == 0 || ZDOMan.instance == null)
            {
                return;
            }
            foreach (Gate gate in _activeGates.Values)
            {
                ZDO origin = ZDOMan.instance.GetZDO(gate.Origin);
                ZDO destination = ZDOMan.instance.GetZDO(gate.Destination);
                if (origin == null || !origin.IsValid() || destination == null || !destination.IsValid())
                {
                    // The next maintenance pass re-mints; nothing useful to link this frame.
                    continue;
                }
                if (origin.GetConnectionZDOID(ZDOExtraData.ConnectionType.Portal) != destination.m_uid)
                {
                    TargetedPhantomPortalFactory.LinkOneWay(origin, destination);
                }
            }
        }

        /// <summary>
        /// Any corpse-run portal (by Kind) that no active gate accounts for is a leftover - from a
        /// previous server run (gate bookkeeping lives only in memory, the ZDOs are persistent) or a
        /// mint that failed halfway - and is destroyed. A tombstone still standing after a restart
        /// gets a fresh gate through DetectNewDeaths' own rule, so nothing is lost.
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
                // Protection is dropped first so this mod's own reap is not refused by its own veto.
                TargetedPortalProtection.Release(zdo.m_uid);
                TargetedPhantomPortalFactory.DestroyStandalone(zdo);
            }
        }

        private static void TearDown(Gate gate)
        {
            TargetedPortalProtection.Release(gate.Origin);
            TargetedPortalProtection.Release(gate.Destination);

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

        /// <summary>
        /// While any gate is up, one line every StatusSeconds saying both ends are still there and what
        /// the protection layers had to refuse. Silent when nothing is up, so an idle server stays quiet.
        /// </summary>
        private static void Heartbeat(float dt)
        {
            if (_activeGates.Count == 0)
            {
                _statusTimer = 0f;
                return;
            }
            _statusTimer += dt;
            if (_statusTimer < StatusSeconds)
            {
                return;
            }
            _statusTimer = 0f;

            var counters = TargetedPortalProtection.Counters;
            foreach (Gate gate in _activeGates.Values)
            {
                bool originUp = ZDOMan.instance?.GetZDO(gate.Origin)?.IsValid() == true;
                bool destUp = ZDOMan.instance?.GetZDO(gate.Destination)?.IsValid() == true;
                PortalDebug.LogAlways($"[CorpseRun] gate for player {gate.PlayerId}: origin {(originUp ? "up" : "MISSING")}, grave end {(destUp ? "up" : "MISSING")}, {gate.Age / 60f:F0} min old, {gate.Remints} re-mint(s).");
            }
            PortalDebug.LogAlways($"[CorpseRun] protection refused {counters.damage} damage call(s), {counters.removals} removal(s), {counters.steals} ownership steal(s); watchdog restored health {counters.healthRestores}x and ownership {counters.ownerRestores}x.");
        }

        private static string DescribePosition(ZDOID uid)
        {
            ZDO zdo = ZDOMan.instance?.GetZDO(uid);
            if (zdo == null || !zdo.IsValid())
            {
                return "(gone)";
            }
            Vector3 p = zdo.GetPosition();
            return $"{p.x:F0},{p.y:F0},{p.z:F0}";
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
