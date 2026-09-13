using System.Collections.Generic;
using UnityEngine;
using TortalPortalLite.Core;
using TortalPortalLite.Core.Data;

namespace TortalPortalLite.Subsystems.WorldOps
{
    /// <summary>Admin declaration for one escort-gated route (economy.json section "escortGates").</summary>
    public sealed class EconomyEscortDeclaration
    {
        public EconomyPosition Portal = new EconomyPosition();
        public EconomyPosition? Destination;
        public List<long> OwnerIds = new List<long>();
        public string Label = "GATE";

        /// <summary>0 = use EconomyConfig.EscortRadius.</summary>
        public float Radius = 0f;
    }

    /// <summary>
    /// #280 Escort Gate (Owner-Nearby Routes). A route is wired only while its builder (or another named
    /// escort) stands within radius of either end - travellers can only pass under escort. Character
    /// positions are refreshed by the owning client's own ZSyncTransform every physics tick and reach the
    /// server via the distance-independent client change queue, so ConnectedCharacter.Position is fresh
    /// to well under this engine's own poll interval (catalog's own citation on freshness).
    /// </summary>
    public static class EconomyEscortGateEngine
    {
        private static int _lastVersion = -1;
        private static List<EconomyEscortDeclaration> _escorts = new List<EconomyEscortDeclaration>();
        private static float _timer;

        public static void OnUpdate(float dt)
        {
            RefreshIfNeeded();
            _timer += dt;
            float interval = EconomyConfig.PresenceEvalSeconds?.Value ?? 1f;
            if (_timer < interval || _escorts.Count == 0)
            {
                return;
            }
            _timer = 0f;

            List<ConnectedCharacter> characters = ConnectedCharacters.All();
            foreach (EconomyEscortDeclaration decl in _escorts)
            {
                Evaluate(decl, characters);
            }
        }

        private static void RefreshIfNeeded()
        {
            if (EconomyRegistry.Version != _lastVersion)
            {
                _lastVersion = EconomyRegistry.Version;
                _escorts = EconomyRegistry.Section<EconomyEscortDeclaration>("escortGates");
            }
        }

        private static void Evaluate(EconomyEscortDeclaration decl, List<ConnectedCharacter> characters)
        {
            ZDO? gateZdo = EconomyWriteOps.ResolveLivePortal(decl.Portal);
            if (gateZdo == null)
            {
                return;
            }
            ZDOID gateUid = gateZdo.m_uid;
            Vector3 gatePos = gateZdo.GetPosition();

            Vector3? destPos = null;
            if (decl.Destination != null)
            {
                ZDO? destZdo = EconomyWriteOps.ResolveLivePortal(decl.Destination);
                if (destZdo != null)
                {
                    EconomyRoutingKernel.SetDestination(gateUid, destZdo.m_uid);
                    destPos = destZdo.GetPosition();
                }
            }

            float radius = decl.Radius > 0f ? decl.Radius : (EconomyConfig.EscortRadius?.Value ?? 15f);
            float radiusSqr = radius * radius;
            bool escorted = false;
            foreach (ConnectedCharacter c in characters)
            {
                if (!decl.OwnerIds.Contains(c.PlayerId))
                {
                    continue;
                }
                if ((c.Position - gatePos).sqrMagnitude <= radiusSqr || (destPos.HasValue && (c.Position - destPos.Value).sqrMagnitude <= radiusSqr))
                {
                    escorted = true;
                    break;
                }
            }

            EconomyRoutingKernel.Publish(gateUid, "escort", escorted, escorted ? decl.Label : $"{decl.Label} unescorted", 10);
        }
    }
}
