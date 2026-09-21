using System;
using System.Collections.Generic;
using UnityEngine;
using TortalPortalLite.Core;
using TortalPortalLite.Core.Data;
using TortalPortalLite.Core.Hooks;
using TortalPortalLite.Subsystems.Foundations;

namespace TortalPortalLite.Subsystems.Topology
{
    /// <summary>
    /// #178 Phantom Destination Portal - THE shared primitive every other option in the `targeted`
    /// domain builds on: a persistent portal-prefab ZDO minted server-side at a computed coordinate,
    /// with no GameObject and no player nearby, reciprocally paired to a real source portal. Every
    /// other engine in Subsystems/Topology/ that needs a computed-destination portal calls into this
    /// class rather than repeating the creation recipe - see the background brief's own instruction.
    ///
    /// Creation replicates ZNetView.Awake's own fabrication sequence verbatim (SERVER decompile
    /// :82613-82631: `m_zdo.Persistent = m_persistent; m_zdo.Type = m_type; m_zdo.Distant = m_distant;
    /// m_zdo.SetPrefab(stableHashCode); m_zdo.SetRotation(...)`), confirmed field-for-field against the
    /// live decompile rather than assumed: ZDO.ObjectType is `{Default, Prioritized, Solid, Terrain}`
    /// (:73297-73303) and a portal is an ordinary persistent object, so Type = ObjectType.Default - not
    /// Prioritized (catalog option #178's own howItWorks text specifies Default explicitly; Prioritized
    /// is for player/character-class objects, not portals). ZDOMan.CreateNewZDO(Vector3,int) itself
    /// (:76674) pools the ZDO and runs the private AddIfPortal(zdo, hash) (:77732-77752) automatically
    /// IF the hash is already in Game.instance.PortalPrefabHash - it does NOT set Persistent/Type/
    /// Distant/prefab/rotation on its own, hence the explicit replication here.
    ///
    /// Pairing recipe is Game.SetConnection's own immediate-write shape (:100637-100651) reproduced via
    /// PortalOwnership.ClaimAndWrite per this build's ownership rule, and reciprocal-plus-same-tag is
    /// mandatory: Game.ConnectPortals' phase 1 (:100589-100606, confirmed verbatim below) tears down any
    /// link whose partner's tag differs or whose partner's own connection is None.
    /// </summary>
    public static class TargetedPhantomPortalFactory
    {
        private static float _timer;
        private static int _cachedPrefabHash = -1;

        public static void Initialize()
        {
            // #178 failure mode 1 / prerequisite 3: keep vanilla's random same-tag pairer away from a
            // phantom that is momentarily unconnected (e.g. the instant after the source's own
            // reconciler cleared a stale link, before this tick's maintenance pass re-asserts it).
            // FindRandomUnconnectedPortalHook is override-capable: declining (false) for every call
            // that has no phantom candidate leaves vanilla's own selection completely unmodified.
            FindRandomUnconnectedPortalHook.Register(100, ExcludePhantomsFromRandomPairing);
        }

        public static void OnUpdate(float dt)
        {
            _timer += dt;
            float interval = TargetedConfig.MaintenanceIntervalSeconds?.Value ?? 2.0f;
            if (_timer < interval)
            {
                return;
            }
            _timer = 0f;
            MaintenanceTick();
        }

        // ---------------------------------------------------------------- public API for other engines

        /// <summary>True if this ZDO is a phantom this factory minted (any kind).</summary>
        public static bool IsPhantom(ZDO zdo) => zdo != null && zdo.IsValid() && zdo.GetInt(TargetedZdoKeys.Phantom) == 1;

        public static string GetKind(ZDO phantom) => phantom != null && phantom.IsValid() ? phantom.GetString(TargetedZdoKeys.Kind, "") : "";

        /// <summary>Resolves the phantom currently paired to <paramref name="source"/>, if any (source's own Connection field, cross-checked for the phantom marker).</summary>
        public static ZDO ResolveExistingPhantom(ZDO source)
        {
            if (source == null || !source.IsValid() || ZDOMan.instance == null)
            {
                return null;
            }
            ZDOID targetId = source.GetConnectionZDOID(ZDOExtraData.ConnectionType.Portal);
            if (targetId == ZDOID.None)
            {
                return null;
            }
            ZDO target = ZDOMan.instance.GetZDO(targetId);
            return (target != null && target.IsValid() && target.GetInt(TargetedZdoKeys.Phantom) == 1) ? target : null;
        }

        /// <summary>
        /// Ensures <paramref name="source"/> is paired to a phantom at <paramref name="pos"/>/<paramref name="rot"/>
        /// carrying <paramref name="tag"/>/<paramref name="kind"/>. Idempotent: if a matching phantom of the
        /// same kind already sits within 0.5 m, only the tag is re-asserted (no destroy/recreate churn);
        /// a portal prefab cannot be relocated in place (SetSector early-return for portal prefabs, #196's
        /// own citation), so any real move destroys and recreates.
        /// </summary>
        public static ZDO CreateOrRetarget(ZDO source, Vector3 pos, Quaternion rot, string tag, string kind)
        {
            if (source == null || !source.IsValid() || ZDOMan.instance == null)
            {
                return null;
            }

            ZDO existing = ResolveExistingPhantom(source);
            if (existing != null)
            {
                bool sameKind = GetKind(existing) == kind;
                bool samePlace = (existing.GetPosition() - pos).sqrMagnitude < 0.25f;
                if (sameKind && samePlace)
                {
                    if (existing.GetString(ZDOVars.s_tag, "") != tag)
                    {
                        PortalOwnership.ClaimAndWrite(existing, z => z.Set(ZDOVars.s_tag, tag));
                        PortalOwnership.ClaimAndWrite(source, z => z.Set(ZDOVars.s_tag, tag));
                    }
                    return existing;
                }
                DestroyPhantom(existing);
            }

            ZDO phantom = CreateNewPhantom(pos, rot, tag, kind);
            if (phantom == null)
            {
                return null;
            }

            // Reciprocal pairing - same literal tag on both ends, non-None connection on both ends.
            PortalOwnership.ClaimAndWrite(source, z =>
            {
                z.Set(ZDOVars.s_tag, tag);
                z.SetConnection(ZDOExtraData.ConnectionType.Portal, phantom.m_uid);
            });
            PortalOwnership.ClaimAndWrite(phantom, z => z.SetConnection(ZDOExtraData.ConnectionType.Portal, source.m_uid));
            return phantom;
        }

        /// <summary>Drops management of this source: destroys its managed phantom (if any) and leaves the source's own connection to clear via vanilla's own reconciler within 5 s.</summary>
        public static void ReleaseSource(ZDO source)
        {
            ZDO existing = ResolveExistingPhantom(source);
            if (existing != null)
            {
                DestroyPhantom(existing);
            }
        }

        /// <summary>
        /// #207 Corpse-Run Gate's own shape: two brand-new phantom ZDOs paired reciprocally to EACH
        /// OTHER (neither is an existing player-placed portal) - e.g. one beside a bed, one beside a
        /// grave, raised and torn down together as a private ephemeral pair. Same literal tag on both
        /// ends (Game.ConnectPortals' own reconcile predicate, #178's own citation) so vanilla's 5 s
        /// reconciler leaves the pair alone once both sides are non-None and tag-equal.
        /// </summary>
        public static (ZDO? a, ZDO? b) CreateStandalonePair(Vector3 posA, Quaternion rotA, string kindA, Vector3 posB, Quaternion rotB, string kindB, string tag)
        {
            ZDO none = null;
            ZDO a = CreateNewPhantom(posA, rotA, tag, kindA);
            if (a == null)
            {
                return (none, none);
            }
            ZDO b = CreateNewPhantom(posB, rotB, tag, kindB);
            if (b == null)
            {
                DestroyPhantom(a);
                return (none, none);
            }

            PortalOwnership.ClaimAndWrite(a, z => z.SetConnection(ZDOExtraData.ConnectionType.Portal, b.m_uid));
            PortalOwnership.ClaimAndWrite(b, z => z.SetConnection(ZDOExtraData.ConnectionType.Portal, a.m_uid));
            return (a, b);
        }

        /// <summary>
        /// #207 Corpse-Run Gate, one-way variant: a genuine one-way trip, not a round-trip pair. Origin
        /// is a normally-managed phantom (still swept by MaintenanceTick below, still self-heals against
        /// vanilla's periodic reconciler) whose connection points at the destination; the destination is
        /// fabricated but deliberately left un-connected (Portal connection stays None) AND unmarked as a
        /// managed phantom, so MaintenanceTick's generic reciprocity sweep never touches it and never
        /// tries to reap it as "orphaned" - the caller (TargetedCorpseRunEngine) owns its whole lifecycle
        /// directly instead. PortalKeys.OneWayIntentional is set on the origin so both MaintenanceTick's
        /// own reciprocity fix-up (below) and HealthScanEngine's NonReciprocalLink check leave it alone.
        /// </summary>
        public static (ZDO? origin, ZDO? destination) CreateStandaloneOneWay(
            Vector3 posA, Quaternion rotA, string kindA,
            Vector3 posB, Quaternion rotB, string kindB,
            string tag,
            bool destinationIndestructible = false)
        {
            ZDO none = null;
            ZDO a = CreateNewPhantom(posA, rotA, tag, kindA, markAsManagedPhantom: true, indestructible: false);
            if (a == null)
            {
                return (none, none);
            }
            ZDO b = CreateNewPhantom(posB, rotB, tag, kindB, markAsManagedPhantom: false, indestructible: destinationIndestructible);
            if (b == null)
            {
                DestroyPhantom(a);
                return (none, none);
            }

            PortalOwnership.ClaimAndWrite(a, z =>
            {
                z.SetConnection(ZDOExtraData.ConnectionType.Portal, b.m_uid);
                z.Set(PortalKeys.OneWayIntentional, true);
            });
            // b intentionally left with Portal connection = None - no return leg, by design.
            return (a, b);
        }

        /// <summary>Owner-gated destroy, exposed for standalone-pair cleanup (#207) - same recipe every other reap path in this file uses.</summary>
        public static void DestroyStandalone(ZDO zdo) => DestroyPhantom(zdo);

        /// <summary>For a hub not currently satisfiable (e.g. an undiscovered trader in "discovered-only" mode) - releases any phantom and writes a waiting tag so the hub reads e.g. "&gt;Haldor (unfound)".</summary>
        public static void MarkWaiting(ZDO source, string waitingTag)
        {
            ReleaseSource(source);
            if (source != null && source.IsValid() && source.GetString(ZDOVars.s_tag, "") != waitingTag)
            {
                PortalOwnership.ClaimAndWrite(source, z => z.Set(ZDOVars.s_tag, waitingTag));
            }
        }

        // ---------------------------------------------------------------------------- creation/destroy

        private static ZDO CreateNewPhantom(Vector3 pos, Quaternion rot, string tag, string kind) =>
            CreateNewPhantom(pos, rot, tag, kind, markAsManagedPhantom: true, indestructible: false);

        /// <summary>markAsManagedPhantom=false skips the shared "Phantom" marker (used only by #207's destination end) so MaintenanceTick's generic reciprocity/reap sweep below never picks this ZDO up at all - its owning engine manages its whole lifecycle directly instead.</summary>
        private static ZDO CreateNewPhantom(Vector3 pos, Quaternion rot, string tag, string kind, bool markAsManagedPhantom, bool indestructible = false)
        {
            int hash = ResolvePortalPrefabHash();
            if (hash == 0)
            {
                PortalDebug.LogError("[TargetedPhantomPortalFactory] no portal prefab hash resolved - Game.PortalPrefabHash empty?");
                return null;
            }

            ZDO zdo;
            try
            {
                zdo = ZDOMan.instance.CreateNewZDO(pos, hash);
            }
            catch (Exception ex)
            {
                PortalDebug.LogError($"[TargetedPhantomPortalFactory] CreateNewZDO failed: {ex.GetType().Name}: {ex.Message}");
                return null;
            }
            if (zdo == null)
            {
                return null;
            }

            bool lockRemoval = TargetedConfig.LockPhantomPieceRemoval?.Value != false;
            float health = TargetedConfig.PhantomHealthOverride?.Value ?? 1e9f;
            float exitDistance = TargetedConfig.PhantomExitDistance?.Value ?? 1.5f;

            PortalOwnership.ClaimAndWrite(zdo, z =>
            {
                // ZNetView.Awake fabrication order, verbatim (SERVER decompile :82613-82620).
                z.Persistent = true;
                z.Type = ZDO.ObjectType.Default;
                z.Distant = false;
                z.SetPrefab(hash);
                z.SetRotation(rot);

                z.Set(ZDOVars.s_tag, tag);
                z.Set(ZDOVars.s_tagauthor, "server");
                if (markAsManagedPhantom)
                {
                    z.Set(TargetedZdoKeys.Phantom, 1);
                }
                z.Set(TargetedZdoKeys.Kind, kind ?? "");
                z.Set(ZDOVars.s_health, health);

                // LoadFields overrides (client-honoured only, SERVER decompile ZNetView.LoadFields
                // :82669-82720) - never touches gameplay balance server-side, purely a griefing/exploit
                // guard against "hammer the free phantom for materials" (#178 failure modes 2/3).
                z.Set(TargetedZdoKeys.HasFields, true);
                z.Set(TargetedZdoKeys.HasFieldsTeleportWorld, true);
                z.Set(TargetedZdoKeys.TeleportWorldExitDistance, exitDistance);

                if (lockRemoval || indestructible)
                {
                    z.Set(TargetedZdoKeys.HasFieldsPiece, true);
                    z.Set(TargetedZdoKeys.PieceCanBeRemoved, false);
                }

                if (indestructible)
                {
                    // Full immunity suite for tombstone corpse-run destination portal
                    z.Set(TargetedZdoKeys.PieceRandomTarget, false);
                    z.Set(TargetedZdoKeys.PiecePrimaryTarget, false);

                    z.Set(TargetedZdoKeys.HasFieldsWearNTear, true);
                    z.Set(TargetedZdoKeys.WearNTearHealth, 1000000000f);
                    z.Set(TargetedZdoKeys.WearNTearNoSupportWear, false);
                    z.Set(TargetedZdoKeys.WearNTearNoRoofWear, false);
                    z.Set(TargetedZdoKeys.WearNTearSnowDamageImmune, true);
                    z.Set(TargetedZdoKeys.WearNTearAshDamageImmune, true);
                    z.Set(TargetedZdoKeys.WearNTearBurnable, false);

                    z.Set(ZDOVars.s_health, 1000000000f);
                    z.Set(TargetedZdoKeys.Indestructible, 1);
                }
            });

            return zdo;
        }

        private static void DestroyPhantom(ZDO zdo)
        {
            if (zdo == null || !zdo.IsValid() || ZDOMan.instance == null)
            {
                return;
            }
            try
            {
                zdo.SetOwner(ZDOMan.GetSessionID());
                ZDOMan.instance.DestroyZDO(zdo);
            }
            catch (Exception ex)
            {
                PortalDebug.LogWarning($"[TargetedPhantomPortalFactory] destroy failed for {zdo.m_uid}: {ex.Message}");
            }
        }

        /// <summary>
        /// Picks the portal prefab hash to mint phantoms with: prefers whichever entry's resolved name
        /// contains "wood" (the always-available base-tier portal), falls back to the first entry.
        /// Never hard-coded - resolved at runtime from PortalRegistry (#178's own prerequisite).
        /// </summary>
        private static int ResolvePortalPrefabHash()
        {
            if (_cachedPrefabHash > 0)
            {
                return _cachedPrefabHash;
            }
            IReadOnlyList<int> hashes = PortalRegistry.PrefabHashes;
            IReadOnlyList<string> names = PortalRegistry.PrefabNames;
            if (hashes.Count == 0)
            {
                return 0;
            }
            for (int i = 0; i < names.Count && i < hashes.Count; i++)
            {
                if (names[i] != null && names[i].IndexOf("wood", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    _cachedPrefabHash = hashes[i];
                    return _cachedPrefabHash;
                }
            }
            _cachedPrefabHash = hashes[0];
            return _cachedPrefabHash;
        }

        // -------------------------------------------------------------------------------- maintenance

        private static void MaintenanceTick()
        {
            if (ZDOMan.instance == null)
            {
                return;
            }

            // Snapshot from PortalCensus (#64), never GetPortalList() directly here - the census
            // already ran this frame's rescan and this loop may destroy ZDOs, which is safe against a
            // snapshot copy but not against ZDOMan's own live list.
            foreach (PortalRecord rec in PortalCensus.Latest)
            {
                ZDO zdo = ZDOMan.instance.GetZDO(rec.Uid);
                if (zdo == null || !zdo.IsValid() || zdo.GetInt(TargetedZdoKeys.Phantom) != 1)
                {
                    continue;
                }

                ZDOID sourceId = zdo.GetConnectionZDOID(ZDOExtraData.ConnectionType.Portal);
                ZDO source = sourceId != ZDOID.None ? ZDOMan.instance.GetZDO(sourceId) : null;
                if (source == null || !source.IsValid())
                {
                    // #178 howItWorks maintenance rule: source gone -> reap the phantom.
                    DestroyPhantom(zdo);
                    continue;
                }

                string myTag = zdo.GetString(ZDOVars.s_tag, "");
                string sourceTag = source.GetString(ZDOVars.s_tag, "");
                if (myTag != sourceTag)
                {
                    // #178 failure mode "retag race": a client-owned source was retagged through the
                    // in-world UI. Accept the player's choice rather than fight it - drop management.
                    DestroyPhantom(zdo);
                    continue;
                }

                bool oneWay = zdo.GetBool(PortalKeys.OneWayIntentional);
                bool sourceWrong = !oneWay && source.GetConnectionZDOID(ZDOExtraData.ConnectionType.Portal) != zdo.m_uid;
                bool phantomWrong = zdo.GetConnectionZDOID(ZDOExtraData.ConnectionType.Portal) != source.m_uid;
                if (sourceWrong)
                {
                    PortalOwnership.ClaimAndWrite(source, z => z.SetConnection(ZDOExtraData.ConnectionType.Portal, zdo.m_uid));
                }
                if (phantomWrong)
                {
                    PortalOwnership.ClaimAndWrite(zdo, z => z.SetConnection(ZDOExtraData.ConnectionType.Portal, source.m_uid));
                }
            }
        }

        /// <summary>
        /// Replicates Game.FindRandomUnconnectedPortal's own candidate filter (SERVER decompile
        /// :100664-100677: `portal != skip &amp;&amp; tag matches &amp;&amp; connection == None &amp;&amp;
        /// !IsCurrentlyConnectingPortal(portal)`) minus phantom ZDOs, and ONLY overrides when a phantom
        /// would otherwise have been an eligible candidate - every other call declines (false) and
        /// vanilla's own random pick runs completely unmodified.
        /// </summary>
        private static bool ExcludePhantomsFromRandomPairing(List<ZDO> portals, ZDO skip, string tag, out ZDO? result)
        {
            result = null;
            if (Game.instance == null)
            {
                return false;
            }

            List<ZDO> nonPhantomCandidates = null;
            bool phantomWouldQualify = false;
            foreach (ZDO portal in portals)
            {
                if (portal == skip)
                {
                    continue;
                }
                if (portal.GetString(ZDOVars.s_tag) != tag)
                {
                    continue;
                }
                if (portal.GetConnectionZDOID(ZDOExtraData.ConnectionType.Portal) != ZDOID.None)
                {
                    continue;
                }
                if (Game.instance.IsCurrentlyConnectingPortal(portal))
                {
                    continue;
                }
                if (portal.GetInt(TargetedZdoKeys.Phantom) == 1)
                {
                    phantomWouldQualify = true;
                    continue;
                }
                (nonPhantomCandidates ??= new List<ZDO>()).Add(portal);
            }

            if (!phantomWouldQualify)
            {
                return false;
            }
            result = (nonPhantomCandidates != null && nonPhantomCandidates.Count > 0)
                ? nonPhantomCandidates[UnityEngine.Random.Range(0, nonPhantomCandidates.Count)]
                : null;
            return true;
        }
    }
}
