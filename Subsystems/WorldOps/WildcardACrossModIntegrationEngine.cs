using System;
using System.Collections.Generic;
using TortalPortalLite.Core;
using TortalPortalLite.Subsystems.Foundations;

namespace TortalPortalLite.Subsystems.WorldOps
{
    /// <summary>
    /// #149 Cross-Mod Integration Surface. An admin-facing-only, small-effort documentation-plus-registry
    /// class covering the five real contention points a portal mod shares with any OTHER server-only mod
    /// running alongside it, and exposing the two integration points #149 itself recommends: combining
    /// onto ZDOMan's own public observer field rather than a competing Harmony patch, and a read-only
    /// view of this mod's own managed-network state.
    ///
    /// (1) ZDO.SetOwner prefixes: a global hot path touched by ZDOMan.ReleaseNearbyZDOS every ~2s for
    ///     every peer. This mod does not install one; if a future engine ever needs to, the rule is fail
    ///     fast on prefab hash first and never block uid==0/uid==SessionID (release/claim).
    /// (2) Game.ConnectPortals: public, runs every 5s. This mod never prefixes it to return false - every
    ///     wildcard-A engine that needs to react to it uses the existing Core/Hooks/ConnectPortalsHook
    ///     postfix broker or the FindRandomUnconnectedPortalHook override broker, never a private patch.
    /// (3) Game.PortalPrefabHash: a mutable List&lt;int&gt; with no ownership model - WildcardANonPortalMigrationEngine
    ///     is the only engine in this domain that mutates it, and only via Foundations/PrefabExtension's
    ///     own Add-only entry point plus this domain's own reversal method (see that engine's own remarks
    ///     on why a full remove-tracking API can't be added to PrefabExtension itself this wave).
    /// (4) ZRoutedRpc.HandleRoutedRPC/RouteRPC/RPC_RoutedRPC: the universal observation point - this
    ///     domain never patches it directly, using Core/Data/SenderContext.RegisterObserver instead.
    /// (5) ZDO key namespace: every custom key this domain writes is prefixed "tplwc_a_" (see
    ///     WildcardAZdoKeys.cs's own doc comment) and no field in ZDOVars.s_sessionHashes is ever reused.
    /// </summary>
    public static class WildcardACrossModIntegrationEngine
    {
        /// <summary>Fired whenever WildcardASelfOrganizingNetworkEngine records a transit - the one piece of behavioural data #149 names as worth exposing to a sibling mod (e.g. a security mod's speed-cheat detector, so it can suppress a false positive the same way Wonderland's own PositionWatch.IsPortalTransit already does internally).</summary>
        public static event Action<ZDOID>? TransitDetected;

        internal static void RaiseTransitDetected(ZDOID sourcePortalUid)
        {
            try
            {
                TransitDetected?.Invoke(sourcePortalUid);
            }
            catch (Exception ex)
            {
                PortalDebug.LogError($"[WildcardACrossModIntegrationEngine] a TransitDetected subscriber threw: {ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>Read-only view of this mod's own portal census - #149's own "expose a read-only accessor for the mod's managed-network view".</summary>
        public static IReadOnlyList<PortalRecord> ManagedNetworkView => PortalCensus.Latest;

        private static bool _combinedOntoDestroyed;

        /// <summary>
        /// #149's own recommended integration point: `ZDOMan.m_onZDODestroyed` is a public
        /// `Action&lt;ZDO&gt;` field (SERVER decompile :76033), invoked from HandleDestroyedZDO
        /// (:76975-76978) BEFORE the ZDO leaves m_objectsByID - so the ZDO is still fully readable in the
        /// callback. Combining onto it (Delegate.Combine via +=) is the documented non-patch way for a
        /// sibling mod (or another engine in this codebase) to observe every destroy without adding a
        /// second competing Harmony patch. Idempotent - safe to call more than once.
        /// </summary>
        public static void RegisterOnZdoDestroyed(Action<ZDO> observer)
        {
            if (observer == null || ZDOMan.instance == null)
            {
                return;
            }
            try
            {
                ZDOMan.instance.m_onZDODestroyed += ZdoOwned => SafeInvoke(observer, ZdoOwned);
                _combinedOntoDestroyed = true;
            }
            catch (Exception ex)
            {
                PortalDebug.LogError($"[WildcardACrossModIntegrationEngine] failed to combine onto ZDOMan.m_onZDODestroyed: {ex.Message}");
            }
        }

        private static void SafeInvoke(Action<ZDO> observer, ZDO zdo)
        {
            try
            {
                observer(zdo);
            }
            catch (Exception ex)
            {
                PortalDebug.LogError($"[WildcardACrossModIntegrationEngine] m_onZDODestroyed observer threw: {ex.GetType().Name}: {ex.Message}");
            }
        }

        public static bool IsWiredToDestroyEvents => _combinedOntoDestroyed;
    }
}
