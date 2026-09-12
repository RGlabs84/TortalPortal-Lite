using System.Collections.Generic;
using UnityEngine;
using TortalPortalLite.Core;
using TortalPortalLite.Core.Data;
using TortalPortalLite.Core.Hooks;
using TortalPortalLite.Subsystems.Foundations;

namespace TortalPortalLite.Subsystems.Enforcement
{
    /// <summary>
    /// #254 Portal Sanctuary. Destroys a hostile creature ZDO the moment it arrives within range of any
    /// portal, making arrival points safe without wards.
    ///
    /// Catalog's own prescribed hook shape is a CreateNewZDO(ZDOID,Vector3,int)+ZDO.Deserialize postfix
    /// PAIR (Wonderland's SpawnGovernor precedent) - neither is covered by Core/Hooks/. Rather than
    /// leaving this as a NEEDS NEW HOOK BROKER stub, it is built on the broker that IS available and
    /// already fires for every incoming client-authored ZDO write regardless of whether the ZDO is brand
    /// new or an update: RpcZdoDataHook's postfix (the same choke point AuditEngine/PortalDirtyFlagGuardian
    /// already use). "First time this engine has ever seen this ZDOID" is tracked in a bounded dedupe set,
    /// which is functionally equivalent to "arrival" for a creature (a creature ZDO is created once and
    /// then only its position/state fields change) without needing a second Harmony patch pair.
    /// </summary>
    public static class LockdownPortalSanctuaryEngine
    {
        private const int SeenCap = 20000;
        private static readonly HashSet<ZDOID> _seen = new HashSet<ZDOID>();

        private static int _destroysThisSecond;
        private static float _rateTimer;

        public static void Initialize()
        {
            RpcZdoDataHook.RegisterPostfix(300, OnZdoDataArrived);
        }

        public static void OnUpdate(float dt)
        {
            _rateTimer += dt;
            if (_rateTimer >= 1f)
            {
                _rateTimer = 0f;
                _destroysThisSecond = 0;
            }
        }

        private static void OnZdoDataArrived(ZNetPeer? sender, ZDOID zdoid)
        {
            if (LockdownConfig.SanctuaryEnabled?.Value != true)
            {
                return;
            }
            if (_seen.Count >= SeenCap)
            {
                _seen.Clear(); // Bounded dedupe set - an occasional false "arrival" re-evaluation of a long-lived ZDO after a clear is harmless (see this engine's own known-limitation note).
            }
            if (!_seen.Add(zdoid))
            {
                return; // Not the first sighting - an ordinary update to an already-known ZDO, not an arrival.
            }
            if (ZDOMan.instance == null)
            {
                return;
            }
            ZDO zdo = ZDOMan.instance.GetZDO(zdoid);
            if (zdo == null || !zdo.IsValid())
            {
                return;
            }
            EvaluateArrival(zdo);
        }

        private static void EvaluateArrival(ZDO zdo)
        {
            if (ZNetScene.instance == null)
            {
                return;
            }
            GameObject prefab;
            try
            {
                prefab = ZNetScene.instance.GetPrefab(zdo.GetPrefab());
            }
            catch
            {
                return;
            }
            if (prefab == null)
            {
                return;
            }
            Character character = prefab.GetComponent<Character>();
            if (character == null || character.m_boss)
            {
                return; // Not a creature, or a boss - never touch bosses.
            }
            if (character.m_faction == Character.Faction.Players || character.m_faction == Character.Faction.PlayerSpawned)
            {
                return; // Player characters and player-summoned allies.
            }
            if (zdo.GetBool(ZDOVars.s_tamed))
            {
                return; // A tame (including a newborn Procreation offspring, which carries s_tamed in its first packet).
            }
            bool isEventCreature = zdo.GetBool(ZDOVars.s_eventCreature);
            if (isEventCreature && LockdownConfig.SanctuaryIncludeEventCreatures?.Value == false)
            {
                return;
            }

            Vector3 pos = zdo.GetPosition();
            float radius = LockdownConfig.SanctuaryRadius?.Value ?? 24f;
            float radiusSqr = radius * radius;
            bool nearPortal = false;
            foreach (PortalRecord rec in PortalCensus.Latest)
            {
                if ((rec.Position - pos).sqrMagnitude <= radiusSqr)
                {
                    nearPortal = true;
                    break;
                }
            }
            if (!nearPortal)
            {
                return;
            }

            if (TryDestroyHostile(zdo, skipIfAlertNearPlayer: false))
            {
                PortalDebug.LogInfo($"[LockdownPortalSanctuaryEngine] destroyed newly-arrived '{prefab.name}' at {pos:F0} - within {radius:F0}m of a portal.");
            }
        }

        /// <summary>Shared destroy primitive - also used by #259 Beacon Cleanup Sweep for its own leftover-event-creature pass.</summary>
        public static bool TryDestroyHostile(ZDO zdo, bool skipIfAlertNearPlayer)
        {
            if (zdo == null || !zdo.IsValid() || ZDOMan.instance == null)
            {
                return false;
            }
            int budget = LockdownConfig.SanctuaryMaxDestroysPerSecond?.Value ?? 32;
            if (_destroysThisSecond >= budget)
            {
                return false;
            }
            if (skipIfAlertNearPlayer && zdo.GetBool(ZDOVars.s_alert))
            {
                Vector3 pos = zdo.GetPosition();
                foreach (ConnectedCharacter c in ConnectedCharacters.All())
                {
                    if ((c.Position - pos).sqrMagnitude < 20f * 20f)
                    {
                        return false; // Let a fight already in progress finish rather than yank the creature away mid-combat.
                    }
                }
            }
            try
            {
                zdo.SetOwner(ZDOMan.GetSessionID());
                ZDOMan.instance.DestroyZDO(zdo);
                _destroysThisSecond++;
                return true;
            }
            catch (System.Exception ex)
            {
                PortalDebug.LogWarning($"[LockdownPortalSanctuaryEngine] destroy failed: {ex.Message}");
                return false;
            }
        }
    }
}
