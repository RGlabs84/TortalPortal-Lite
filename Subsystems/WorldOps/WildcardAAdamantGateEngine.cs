using System;
using System.Collections.Generic;
using TortalPortalLite.Core;
using TortalPortalLite.Core.Data;
using TortalPortalLite.Core.Hooks;

namespace TortalPortalLite.Subsystems.WorldOps
{
    /// <summary>
    /// #134 The Adamant Gate, REWRITTEN by #243 ("The Adamant Gate, Rewritten - Indestructible Only As
    /// A Composite"). One engine implements both catalog entries per the task's merge instruction: #243
    /// does not replace #134's mechanism, it corrects the headline claim - a huge s_health alone defeats
    /// only damage-and-Repair, NOT hammer removal, support wear, a forged DestroyZDO, or a wholesale
    /// RPC_ZDOData overwrite. "Indestructible" only holds when all five levers below are present
    /// together; this engine always applies all five as one unit (Fortify), never s_health alone.
    ///
    /// Lever 1 - damage buffer: WearNTear.ApplyDamage (:150387-150409) reads current health straight off
    /// the ZDO every hit and Repair (:149522-149531) refuses when stored health >= prefab m_health - so
    /// a large stored s_health defeats both, but drains given enough time against ongoing wear
    /// (WearNTear.UpdateWear support/roof damage, :149705-149712/:149822-149828) unless lever 2 also
    /// disables that source.
    /// Lever 2 - vanilla-honoured LoadFields overrides (WildcardAFieldInjectionEngine, #135/#241):
    /// Piece.m_canBeRemoved=false (Player.RemovePiece refuses at :12364-12378 before any RPC is sent)
    /// and WearNTear.m_noSupportWear/m_noRoofWear=false (per-update wear is never evaluated at all,
    /// :149686/:149705) - both client-honoured only.
    /// Lever 3 - ownership pin: re-claiming ZDO ownership on every watchdog tick means a forged
    /// RPC_Remove/RPC_Damage from a modified client, routed to this ZDO's owner, dies in
    /// ZNetScene.FindInstance (no live GameObject exists server-side to receive it,
    /// ZRoutedRpc.HandleRoutedRPC, :83646-83663) - server-enforced against a modified client. Accepted
    /// side effect (#243's own note): RPC_SetTag routes the same way and also dies, so a pinned gate is
    /// retag-frozen; acceptable for an admin-managed gate.
    /// Lever 4 - destroy veto: HandleDestroyedZdoHook (true first-veto-wins prefix) blocks a client-
    /// originated destroy of a fortified portal outright - server-enforced.
    /// Lever 5 - RPC_ZDOData watchdog: RpcZdoDataHook postfix re-checks a fortified portal's stored
    /// health after every incoming client ZDO write and restores it if a modified client set s_health
    /// directly (RPC_ZDOData applies any higher-revision blob wholesale with zero validation,
    /// :77076-77139) - detect-and-revert, this mod's accepted tier per RpcZdoDataHook's own doc comment.
    /// </summary>
    public static class WildcardAAdamantGateEngine
    {
        private static readonly HashSet<ZDOID> _fortified = new HashSet<ZDOID>();
        private static float _timer;
        private static bool _bootScanDone;
        private static bool _hooksInstalled;

        public static void Initialize()
        {
            if (_hooksInstalled)
            {
                return;
            }
            _hooksInstalled = true;
            // Lever 4: true first-veto-wins prefix (task instructions call this hook out as "likely
            // relevant to indestructible portal options"). Priority 100 - a mid-range slot, since other
            // wave-3 domains may also register destroy vetoes for unrelated reasons.
            HandleDestroyedZdoHook.Register(100, OnDestroyAttempt);
            // Lever 5: watchdog restoring s_health after any client-authored ZDO write lands.
            RpcZdoDataHook.RegisterPostfix(100, OnZdoDataFromClient);
        }

        public static void OnWorldReady()
        {
            BootScan();
        }

        private static void BootScan()
        {
            if (_bootScanDone || ZDOMan.instance == null)
            {
                return;
            }
            _bootScanDone = true;
            try
            {
                List<ZDOID> ids = ZDOExtraData.GetAllZDOIDsWithHash(ZDOExtraData.Type.Int, WildcardAZdoKeys.FortifiedMarker);
                foreach (ZDOID id in ids)
                {
                    ZDO zdo = ZDOMan.instance.GetZDO(id);
                    if (zdo != null && zdo.IsValid() && zdo.GetInt(WildcardAZdoKeys.FortifiedMarker) == 1)
                    {
                        _fortified.Add(id);
                    }
                }
                PortalDebug.LogInfo($"[WildcardAAdamantGateEngine] boot scan found {_fortified.Count} pre-existing fortified portal(s).");
            }
            catch (Exception ex)
            {
                PortalDebug.LogWarning($"[WildcardAAdamantGateEngine] boot scan failed: {ex.Message}");
            }
        }

        public static void OnUpdate(float dt)
        {
            if (WildcardAConfig.Enabled?.Value == false)
            {
                return;
            }
            _timer += dt;
            float interval = WildcardAConfig.AdamantWatchdogSeconds?.Value ?? 5.0f;
            if (_timer < interval)
            {
                return;
            }
            _timer = 0f;
            Watchdog();
        }

        public static bool IsFortified(ZDO zdo) => zdo != null && zdo.IsValid() && zdo.GetInt(WildcardAZdoKeys.FortifiedMarker) == 1;

        /// <summary>Applies all five levers to <paramref name="portal"/> as one unit - #243's own headline correction, never s_health alone.</summary>
        public static void Fortify(ZDO portal)
        {
            if (portal == null || !portal.IsValid())
            {
                return;
            }
            float health = WildcardAConfig.AdamantHealthMultiplier?.Value ?? 1e9f;

            PortalOwnership.ClaimAndWrite(portal, z =>
            {
                z.Set(WildcardAZdoKeys.FortifiedMarker, 1);
                z.Set(WildcardAZdoKeys.FortifiedIntendedHealth, health);
                z.Set(ZDOVars.s_health, health); // lever 1
            });

            // lever 2 - client-honoured LoadFields overrides, via this domain's own field-injection engine.
            WildcardAFieldInjectionEngine.SetCanBeRemoved(portal, false);
            WildcardAFieldInjectionEngine.SetNoSupportWear(portal, false);
            WildcardAFieldInjectionEngine.SetNoRoofWear(portal, false);

            // lever 3 - ownership pin, first claim now; Watchdog() re-claims every tick from here on.
            try
            {
                portal.SetOwner(ZDOMan.GetSessionID());
            }
            catch (Exception ex)
            {
                PortalDebug.LogWarning($"[WildcardAAdamantGateEngine] initial ownership pin failed for {portal.m_uid}: {ex.Message}");
            }

            _fortified.Add(portal.m_uid);
            PortalDebug.LogAlways($"[WildcardAAdamantGateEngine] fortified {portal.m_uid} (health={health:E1}). Retagging this portal will no longer reach it while pinned (#243's accepted tradeoff).");
        }

        /// <summary>Admin release - drops the marker and the ownership pin's re-claim; does NOT restore health/LoadFields to prefab defaults (matching UninstallEngine's own "not fully vanilla-authored" precedent for managed state).</summary>
        public static void Unfortify(ZDO portal)
        {
            if (portal == null || !portal.IsValid())
            {
                return;
            }
            _fortified.Remove(portal.m_uid);
            PortalOwnership.ClaimAndWrite(portal, z => z.RemoveInt(WildcardAZdoKeys.FortifiedMarker));
        }

        private static void Watchdog()
        {
            if (ZDOMan.instance == null || _fortified.Count == 0)
            {
                return;
            }
            List<ZDOID> stale = null;
            var snapshot = new List<ZDOID>(_fortified);
            foreach (ZDOID id in snapshot)
            {
                ZDO zdo = ZDOMan.instance.GetZDO(id);
                if (zdo == null || !zdo.IsValid())
                {
                    (stale ??= new List<ZDOID>()).Add(id);
                    continue;
                }
                RestoreHealthIfLowered(zdo);
                // lever 3 - re-claim ownership every watchdog tick (ZDOMan.ReleaseNearbyZDOS hands
                // ownership to any nearby peer every ~2s, same hazard NetworkReassertEngine's own doc
                // comment names).
                try
                {
                    zdo.SetOwner(ZDOMan.GetSessionID());
                }
                catch (Exception ex)
                {
                    PortalDebug.LogWarning($"[WildcardAAdamantGateEngine] re-pin failed for {id}: {ex.Message}");
                }
            }
            if (stale != null)
            {
                foreach (ZDOID id in stale)
                {
                    _fortified.Remove(id);
                }
            }
        }

        private static void RestoreHealthIfLowered(ZDO zdo)
        {
            float intended = zdo.GetFloat(WildcardAZdoKeys.FortifiedIntendedHealth, 0f);
            if (intended <= 0f)
            {
                return;
            }
            float current = zdo.GetFloat(ZDOVars.s_health, intended);
            if (current < intended)
            {
                PortalDebug.LogWarning($"[WildcardAAdamantGateEngine] {zdo.m_uid} s_health observed {current:E1} below intended {intended:E1} - restoring (lever 5 watchdog).");
                PortalOwnership.ClaimAndWrite(zdo, z => z.Set(ZDOVars.s_health, intended));
            }
        }

        private static void OnZdoDataFromClient(ZNetPeer? sender, ZDOID zdoid)
        {
            if (ZDOMan.instance == null || !_fortified.Contains(zdoid))
            {
                return;
            }
            ZDO zdo = ZDOMan.instance.GetZDO(zdoid);
            if (zdo != null && zdo.IsValid())
            {
                RestoreHealthIfLowered(zdo);
            }
        }

        /// <summary>Lever 4 - a client-originated destroy (sender != 0, i.e. arrived through RPC_DestroyZDO) of a fortified portal is refused outright; a server-internal destroy (sender == 0, e.g. an admin command) is allowed through.</summary>
        private static bool OnDestroyAttempt(ZDO zdo, long sender)
        {
            if (sender == 0L || !_fortified.Contains(zdo.m_uid))
            {
                return true;
            }
            PortalDebug.LogWarning($"[WildcardAAdamantGateEngine] blocked a client-originated destroy of fortified portal {zdo.m_uid} (sender peer-uid {sender}).");
            return false;
        }
    }
}
