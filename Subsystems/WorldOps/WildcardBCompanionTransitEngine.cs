using System.Collections.Generic;
using UnityEngine;
using TortalPortalLite.Core;
using TortalPortalLite.Core.Data;
using TortalPortalLite.Subsystems.Foundations;

namespace TortalPortalLite.Subsystems.WorldOps
{
    /// <summary>
    /// #238 "Tame And Cart Follow-Through - Relocating Companions Across A Transit". Non-portal ZDOs,
    /// unlike portals, re-file on position change (`ZDO.SetSector`, SERVER decompile :73752-73768), so a
    /// relocated creature/cart simply falls out of the source peer's active area and into the
    /// destination peer's on the very next sector-sync pass - no special handling anywhere. Selection
    /// criteria, confirmed directly against the decompile: a Tameable creature is eligible when
    /// `ZDOVars.s_tamed` is true (`Character.SetTamed`, :4482-4495) and `ZDOVars.s_follow` (a character
    /// NAME string, :78429) equals the travelling player's own name (written by `Tameable.RPC_Command`,
    /// :19457, cleared to "" on Stay, :19444); a `Vagon` cart is eligible when `ZDOVars.s_attachJointHash`
    /// (a BOOL despite the name, :78367) is false (:147617/:147661/:147686 - true means currently being
    /// pulled, must not be moved out from under its puller).
    ///
    /// Transit detection: this domain has no server-initiated-teleport signal to hook (that belongs to
    /// #136's own waygate mechanism, a DIFFERENT transport path this option's own catalog text explicitly
    /// separates from ordinary vanilla portal travel), so this engine implements the catalog's own
    /// documented fallback, `PositionWatch.IsPortalTransit`: it samples every connected character's
    /// position on a timer and, when a player's PREVIOUS sample was within
    /// `CompanionTransitRadius` of some portal P's own position and P has a live Connection, and their
    /// CURRENT sample is within the same radius of P's connected partner's position, treats that as a
    /// completed transit from P to its partner - a coarse (poll-interval resolution) but genuinely
    /// server-observable signal built entirely from PortalCensus + ConnectedCharacters, this mod's own
    /// standard primitives.
    ///
    /// Prefab discovery follows this codebase's own established idiom (see
    /// Subsystems/Enforcement/LockdownBossWatchdogEngine.cs's boss-prefab scan): every ZNetScene prefab is
    /// inspected ONCE for a `Tameable`/`Vagon` component (an asset read, no instantiation).
    /// </summary>
    public static class WildcardBCompanionTransitEngine
    {
        private static readonly HashSet<int> _tameableHashes = new HashSet<int>();
        private static readonly HashSet<int> _vagonHashes = new HashSet<int>();
        private static readonly Dictionary<int, bool> _summonExclusion = new Dictionary<int, bool>(); // prefab hash -> "is an owner-logout summon, never relocate"
        private static bool _prefabsDiscovered;

        private static readonly Dictionary<long, Vector3> _lastPos = new Dictionary<long, Vector3>();
        private static readonly Dictionary<long, float> _cooldownRemaining = new Dictionary<long, float>();
        private static float _timer;

        public static void OnUpdate(float dt)
        {
            if (WildcardBConfig.CompanionTransitEnabled?.Value != true)
            {
                return;
            }
            DiscoverPrefabsOnce();

            foreach (KeyValuePair<long, float> kv in new List<KeyValuePair<long, float>>(_cooldownRemaining))
            {
                float remaining = kv.Value - dt;
                if (remaining <= 0f) _cooldownRemaining.Remove(kv.Key);
                else _cooldownRemaining[kv.Key] = remaining;
            }

            _timer += dt;
            float interval = WildcardBConfig.CompanionTransitPollSeconds?.Value ?? 1f;
            if (_timer < interval)
            {
                return;
            }
            _timer = 0f;
            Sample();
        }

        private static void DiscoverPrefabsOnce()
        {
            if (_prefabsDiscovered || ZNetScene.instance == null)
            {
                return;
            }
            _prefabsDiscovered = true;
            foreach (string name in ZNetScene.instance.GetPrefabNames())
            {
                try
                {
                    GameObject prefab = ZNetScene.instance.GetPrefab(name);
                    if (prefab == null) continue;
                    int hash = name.GetStableHashCode();
                    Tameable tameable = prefab.GetComponent<Tameable>();
                    if (tameable != null)
                    {
                        _tameableHashes.Add(hash);
                        _summonExclusion[hash] = tameable.m_unsummonOnOwnerLogoutSeconds > 0f;
                    }
                    if (prefab.GetComponent<Vagon>() != null)
                    {
                        _vagonHashes.Add(hash);
                    }
                }
                catch (System.Exception ex)
                {
                    PortalDebug.LogWarning($"[WildcardBCompanionTransitEngine] prefab '{name}' inspection failed: {ex.Message}");
                }
            }
            PortalDebug.LogAlways($"[WildcardBCompanionTransitEngine] discovered {_tameableHashes.Count} tameable prefab(s), {_vagonHashes.Count} cart prefab(s).");
        }

        private static void Sample()
        {
            if (ZDOMan.instance == null)
            {
                return;
            }
            float radius = WildcardBConfig.CompanionTransitRadius?.Value ?? 12f;

            foreach (ConnectedCharacter who in ConnectedCharacters.All())
            {
                Vector3 current = who.Position;
                if (_lastPos.TryGetValue(who.PlayerId, out Vector3 prev))
                {
                    if (!_cooldownRemaining.ContainsKey(who.PlayerId) && TryDetectTransit(prev, current, radius, out Vector3 sourcePortalPos, out ZDO destPortal))
                    {
                        RelocateFollowers(who, sourcePortalPos, destPortal, radius);
                        _cooldownRemaining[who.PlayerId] = WildcardBConfig.CompanionTransitCooldownSeconds?.Value ?? 20f;
                    }
                }
                _lastPos[who.PlayerId] = current;
            }
        }

        private static bool TryDetectTransit(Vector3 prevPlayerPos, Vector3 currentPlayerPos, float radius, out Vector3 sourcePortalPos, out ZDO destPortal)
        {
            sourcePortalPos = Vector3.zero;
            destPortal = null;
            float radiusSqr = radius * radius;

            foreach (PortalRecord rec in PortalCensus.Latest)
            {
                if (rec.Connection == ZDOID.None)
                {
                    continue;
                }
                if ((rec.Position - prevPlayerPos).sqrMagnitude > radiusSqr)
                {
                    continue;
                }
                if (!PortalCensus.TryGet(rec.Connection, out PortalRecord destRec))
                {
                    continue;
                }
                if ((destRec.Position - currentPlayerPos).sqrMagnitude > radiusSqr)
                {
                    continue;
                }
                ZDO dest = ZDOMan.instance.GetZDO(destRec.Uid);
                if (dest == null || !dest.IsValid())
                {
                    continue;
                }
                sourcePortalPos = rec.Position;
                destPortal = dest;
                return true;
            }
            return false;
        }

        private static void RelocateFollowers(ConnectedCharacter traveller, Vector3 sourcePortalPos, ZDO destPortal, float radius)
        {
            if (ZDOMan.instance == null)
            {
                return;
            }
            List<ZDO> nearby = ZdoSpatialQuery.FindNear(sourcePortalPos, radius);
            if (nearby.Count == 0)
            {
                return;
            }

            Vector3 destPos = destPortal.GetPosition();
            Quaternion destRot = destPortal.GetRotation();
            float groundY = WorldGenerator.instance != null ? WorldGenerator.instance.GetHeight(destPos.x, destPos.z) : destPos.y;

            int maxFollowers = WildcardBConfig.CompanionTransitMaxFollowers?.Value ?? 6;
            int relocated = 0;
            string travellerName = traveller.Name;

            foreach (ZDO candidate in nearby)
            {
                if (relocated >= maxFollowers)
                {
                    break;
                }
                if (candidate == null || !candidate.IsValid())
                {
                    continue;
                }
                int prefab = candidate.GetPrefab();

                bool isEligibleTame = _tameableHashes.Contains(prefab)
                    && candidate.GetBool(ZDOVars.s_tamed)
                    && candidate.GetString(ZDOVars.s_follow, "") == travellerName
                    && !(_summonExclusion.TryGetValue(prefab, out bool isSummon) && isSummon);

                bool isEligibleCart = _vagonHashes.Contains(prefab) && !candidate.GetBool(ZDOVars.s_attachJointHash);

                if (!isEligibleTame && !isEligibleCart)
                {
                    continue;
                }

                Vector3 spread = destRot * new Vector3(((relocated % 3) - 1) * 1.5f, 0f, -2f - relocated * 0.5f);
                Vector3 newPos = new Vector3(destPos.x + spread.x, groundY + 0.5f, destPos.z + spread.z);

                PortalOwnership.ClaimAndWrite(candidate, z =>
                {
                    z.SetPosition(newPos);
                    z.SetRotation(destRot);
                });
                relocated++;
            }

            if (relocated > 0)
            {
                PortalDebug.LogInfo($"[WildcardBCompanionTransitEngine] relocated {relocated} companion(s) for {travellerName} from {sourcePortalPos:F0} to {destPos:F0}.");
            }
        }
    }
}
