using System.Collections.Generic;
using UnityEngine;
using TortalPortalLite.Core;
using TortalPortalLite.Core.Data;
using TortalPortalLite.Subsystems.Topology;

namespace TortalPortalLite.Subsystems.WorldOps
{
    /// <summary>Admin declaration for one fuel-burning portal (economy.json section "chargeCells").</summary>
    public sealed class EconomyChargeCellDeclaration
    {
        public EconomyPosition Portal = new EconomyPosition();
        public EconomyPosition? Destination;
        public string Label = "GATE";
        public float IdleBurnPerSecond = 0.02f;
        public float PerTransitBurn = 0.5f;
        public float BindRadius = 0f;
    }

    /// <summary>
    /// #95 Charge Cells. A gate that burns fuel out of the fuel float of a vanilla Fireplace bolted to
    /// the frame and disconnects the moment the tank runs dry - the fireplace-tank variant the catalog
    /// itself recommends ("dramatically cheaper... one float, no ObjectDB, no Inventory, no
    /// Instantiate"). The server drives the decrement directly (ZDOVars.s_fuel is a plain float field,
    /// catalog's own citation :78433) rather than relying on vanilla's own owner-only burn-down
    /// (Fireplace.UpdateFireplace only runs "if m_nview.IsOwner()", so an unattended fireplace never
    /// drains on its own) - this is precisely what makes an idle gate cost something to keep lit.
    /// </summary>
    public static class EconomyChargeCellEngine
    {
        private static int _lastVersion = -1;
        private static List<EconomyChargeCellDeclaration> _cells = new List<EconomyChargeCellDeclaration>();
        private static readonly Dictionary<int, float> _maxFuelCache = new Dictionary<int, float>();
        private static readonly RoutingTransitDetector _detector = new RoutingTransitDetector();
        private static float _timer;

        public static void OnUpdate(float dt)
        {
            RefreshIfNeeded();
            _timer += dt;
            float interval = EconomyConfig.ChargeCellPollSeconds?.Value ?? 1f;
            if (_timer < interval || _cells.Count == 0)
            {
                return;
            }
            float elapsed = _timer;
            _timer = 0f;

            // Per-transit burns, detected against the gates this engine actually manages.
            var byUid = new Dictionary<ZDOID, EconomyChargeCellDeclaration>();
            foreach (EconomyChargeCellDeclaration decl in _cells)
            {
                ZDO? gz = EconomyWriteOps.ResolveLivePortal(decl.Portal);
                if (gz != null)
                {
                    byUid[gz.m_uid] = decl;
                }
            }
            var extraBurn = new Dictionary<ZDOID, float>();
            if (byUid.Count > 0)
            {
                foreach (RoutingTransitDetector.Transit transit in _detector.Poll(EconomyConfig.TransitStraddleRadius?.Value ?? 8f))
                {
                    if (byUid.TryGetValue(transit.From.Uid, out EconomyChargeCellDeclaration decl) && decl.PerTransitBurn > 0f)
                    {
                        extraBurn.TryGetValue(transit.From.Uid, out float existing);
                        extraBurn[transit.From.Uid] = existing + decl.PerTransitBurn;
                    }
                }
            }
            else
            {
                _detector.Poll(EconomyConfig.TransitStraddleRadius?.Value ?? 8f);
            }

            float idleSuppressRadius = EconomyConfig.ChargeIdleBurnSuppressRadius?.Value ?? 0f;
            foreach (EconomyChargeCellDeclaration decl in _cells)
            {
                Evaluate(decl, elapsed, idleSuppressRadius, extraBurn);
            }
        }

        private static void RefreshIfNeeded()
        {
            if (EconomyRegistry.Version != _lastVersion)
            {
                _lastVersion = EconomyRegistry.Version;
                _cells = EconomyRegistry.Section<EconomyChargeCellDeclaration>("chargeCells");
            }
        }

        private static void Evaluate(EconomyChargeCellDeclaration decl, float elapsedSeconds, float idleSuppressRadius, Dictionary<ZDOID, float> extraBurn)
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

            float radius = decl.BindRadius > 0f ? decl.BindRadius : (EconomyConfig.BindRadius?.Value ?? 4f);
            ZDO? fireplace = EconomyBindingRegistry.FindNearest(gateZdo.GetPosition(), radius, EconomyBindingRegistry.FixtureKind.Fireplace);
            if (fireplace == null)
            {
                EconomyRoutingKernel.Publish(gateUid, "charge", false, $"{decl.Label} NO BRAZIER", 10);
                return;
            }

            bool suppressIdle = idleSuppressRadius > 0f && !AnyPeerWithin(gateZdo.GetPosition(), idleSuppressRadius);
            float burn = suppressIdle ? 0f : decl.IdleBurnPerSecond * elapsedSeconds;
            if (extraBurn.TryGetValue(gateUid, out float transitBurn))
            {
                burn += transitBurn;
            }

            float current = fireplace.GetFloat(ZDOVars.s_fuel);
            float updated = Mathf.Max(0f, current - burn);
            if (!Mathf.Approximately(updated, current))
            {
                PortalOwnership.ClaimAndWrite(fireplace, z => z.Set(ZDOVars.s_fuel, updated));
            }
            EconomyValidatorEngine.SnapshotFireplaceFuel(fireplace);

            float maxFuel = ResolveMaxFuel(fireplace.GetPrefab());
            bool open = updated > 0f;
            string bar = BarString(updated, maxFuel);
            EconomyRoutingKernel.Publish(gateUid, "charge", open, $"{decl.Label} {bar}", 10);
        }

        private static bool AnyPeerWithin(Vector3 pos, float radius)
        {
            float sq = radius * radius;
            foreach (ConnectedCharacter c in ConnectedCharacters.All())
            {
                if ((c.Position - pos).sqrMagnitude <= sq)
                {
                    return true;
                }
            }
            return false;
        }

        private static float ResolveMaxFuel(int prefabHash)
        {
            if (_maxFuelCache.TryGetValue(prefabHash, out float cached))
            {
                return cached;
            }
            GameObject? prefab = ZNetScene.instance?.GetPrefab(prefabHash);
            Fireplace? fp = prefab != null ? prefab.GetComponent<Fireplace>() : null;
            float max = fp != null ? fp.m_maxFuel : 1f;
            if (max <= 0f)
            {
                max = 1f;
            }
            _maxFuelCache[prefabHash] = max;
            return max;
        }

        private static string BarString(float value, float max)
        {
            const int slots = 5;
            int filled = Mathf.Clamp(Mathf.RoundToInt(value / max * slots), 0, slots);
            return new string('▮', filled) + new string('▯', slots - filled);
        }
    }
}
