using System.Collections.Generic;
using TortalPortalLite.Core;
using TortalPortalLite.Core.Data;

namespace TortalPortalLite.Subsystems.WorldOps
{
    /// <summary>Admin declaration for one progression-gated route (economy.json section "progressionRoutes").</summary>
    public sealed class EconomyProgressionRouteDeclaration
    {
        public EconomyPosition Portal = new EconomyPosition();
        public EconomyPosition? Destination;
        public string Label = "GATE";
        public string ClosedTag = "sealed";

        /// <summary>Every key here must be present (ZoneSystem.GetGlobalKey(string)) for the route to exist - real boss-defeat keys ("defeated_bonemass") and/or this mod's own custom presence keys ("mod_route_northgate") uniformly, since the string overload classifies either kind the same way.</summary>
        public List<string> RequiredKeys = new List<string>();
    }

    /// <summary>
    /// #97 Progression-Gated Route Tiers. Boss-defeat keys are genuinely server-authoritative
    /// (`ZoneSystem.RPC_SetGlobalKey` is registered ONLY on the server, catalog's own citation
    /// :113445-113449) and readable at any time with `ZoneSystem.instance.GetGlobalKey(string)` - a plain
    /// dictionary lookup with no per-tick cost worth avoiding. Implementation is trivial once a route can
    /// be parked/released at all (EconomyRoutingKernel): each declared route's edge exists only while
    /// every required key is present, so the world's portal graph literally grows as the server
    /// progresses, identically for every player.
    ///
    /// Deliberately does NOT attempt "per-player progression gating" (a player's own boss kills/skills
    /// live entirely in their local .fch and never reach the server, catalog's own citation on
    /// Player.Save/PlayerProfile.SavePlayerData) - only server-wide keys are ever consulted.
    /// </summary>
    public static class EconomyProgressionTierEngine
    {
        private static int _lastVersion = -1;
        private static List<EconomyProgressionRouteDeclaration> _routes = new List<EconomyProgressionRouteDeclaration>();
        private static float _timer;

        public static void OnUpdate(float dt)
        {
            RefreshIfNeeded();
            _timer += dt;
            float interval = EconomyConfig.ProgressionEvalSeconds?.Value ?? 1f;
            if (_timer < interval || _routes.Count == 0 || ZoneSystem.instance == null)
            {
                return;
            }
            _timer = 0f;
            foreach (EconomyProgressionRouteDeclaration decl in _routes)
            {
                Evaluate(decl);
            }
        }

        private static void RefreshIfNeeded()
        {
            if (EconomyRegistry.Version != _lastVersion)
            {
                _lastVersion = EconomyRegistry.Version;
                _routes = EconomyRegistry.Section<EconomyProgressionRouteDeclaration>("progressionRoutes");
            }
        }

        private static void Evaluate(EconomyProgressionRouteDeclaration decl)
        {
            ZDO? gateZdo = EconomyWriteOps.ResolveLivePortal(decl.Portal);
            if (gateZdo == null)
            {
                return;
            }
            ZDOID gateUid = gateZdo.m_uid;

            if (decl.Destination != null)
            {
                ZDO? destZdo = EconomyWriteOps.ResolveLivePortal(decl.Destination);
                if (destZdo != null)
                {
                    EconomyRoutingKernel.SetDestination(gateUid, destZdo.m_uid);
                }
            }

            bool open = true;
            foreach (string key in decl.RequiredKeys)
            {
                if (string.IsNullOrEmpty(key) || !ZoneSystem.instance.GetGlobalKey(key))
                {
                    open = false;
                    break;
                }
            }

            EconomyRoutingKernel.Publish(gateUid, "progression", open, open ? decl.Label : $"{decl.Label} {decl.ClosedTag}", 10);
        }
    }
}
