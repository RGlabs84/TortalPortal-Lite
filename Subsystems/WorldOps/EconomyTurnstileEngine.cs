using System.Collections.Generic;
using TortalPortalLite.Core;
using TortalPortalLite.Core.Data;
using TortalPortalLite.Subsystems.Topology;

namespace TortalPortalLite.Subsystems.WorldOps
{
    /// <summary>Admin declaration for one token-gated portal (economy.json section "turnstiles").</summary>
    public sealed class EconomyTurnstileDeclaration
    {
        public EconomyPosition Portal = new EconomyPosition();
        public EconomyPosition? Destination;
        public string Label = "GATE";

        /// <summary>Drop-prefab names (ItemDrop.m_dropPrefab.name) that satisfy the turnstile - matched by hashing each name with the same GetStableHashCode() vanilla uses for ZDOVars.s_item, never by display name.</summary>
        public List<string> AllowedItems = new List<string>();

        /// <summary>If true, a valid token is consumed the moment a transit through this specific gate is detected (catalog's "one trophy, one trip"). If false, the token is a permanent key and is never removed.</summary>
        public bool Consume = true;

        public float BindRadius = 0f;
    }

    /// <summary>
    /// #92 The Token Turnstile. An item stand beside the gate is the payment slot - a single readable int
    /// on its ZDO (ZDOVars.s_item) decides whether the route is open, with no inventory deserialisation
    /// anywhere (catalog's own "no ObjectDB, no Inventory, no Instantiate" cost note) - cheap enough to
    /// poll far faster than the chest-based toll. Consumption (if configured) happens on a DETECTED
    /// TRANSIT through this specific gate, not the instant the token is mounted - matching the catalog's
    /// own playerExperience ("you step through; four seconds later the trophy is gone") rather than
    /// pulsing the gate open-then-immediately-closed on mount, which would leave no time to actually walk
    /// through.
    /// </summary>
    public static class EconomyTurnstileEngine
    {
        private static int _lastVersion = -1;
        private static List<EconomyTurnstileDeclaration> _turnstiles = new List<EconomyTurnstileDeclaration>();
        private static readonly Dictionary<EconomyTurnstileDeclaration, HashSet<int>> _allowedHashCache = new Dictionary<EconomyTurnstileDeclaration, HashSet<int>>();
        private static readonly RoutingTransitDetector _detector = new RoutingTransitDetector();
        private static float _timer;

        public static void OnUpdate(float dt)
        {
            RefreshIfNeeded();
            _timer += dt;
            float interval = EconomyConfig.TurnstilePollSeconds?.Value ?? 0.5f;
            if (_timer < interval || _turnstiles.Count == 0)
            {
                return;
            }
            _timer = 0f;

            var validNow = new HashSet<ZDOID>();
            foreach (EconomyTurnstileDeclaration decl in _turnstiles)
            {
                if (Evaluate(decl, out ZDOID gateUid, out bool tokenValid) && tokenValid)
                {
                    validNow.Add(gateUid);
                }
            }

            if (validNow.Count > 0)
            {
                foreach (RoutingTransitDetector.Transit transit in _detector.Poll(EconomyConfig.TransitStraddleRadius?.Value ?? 8f))
                {
                    if (validNow.Contains(transit.From.Uid))
                    {
                        ConsumeIfConfigured(transit.From.Uid);
                    }
                }
            }
            else
            {
                _detector.Poll(EconomyConfig.TransitStraddleRadius?.Value ?? 8f); // keep the detector's position cache warm even with nothing to consume
            }
        }

        private static void RefreshIfNeeded()
        {
            if (EconomyRegistry.Version != _lastVersion)
            {
                _lastVersion = EconomyRegistry.Version;
                _turnstiles = EconomyRegistry.Section<EconomyTurnstileDeclaration>("turnstiles");
                _allowedHashCache.Clear();
            }
        }

        private static bool Evaluate(EconomyTurnstileDeclaration decl, out ZDOID gateUid, out bool tokenValid)
        {
            gateUid = ZDOID.None;
            tokenValid = false;

            ZDO? gateZdo = EconomyWriteOps.ResolveLivePortal(decl.Portal);
            if (gateZdo == null)
            {
                return false;
            }
            gateUid = gateZdo.m_uid;

            if (decl.Destination != null)
            {
                ZDO? destZdo = EconomyWriteOps.ResolveLivePortal(decl.Destination);
                if (destZdo != null)
                {
                    EconomyRoutingKernel.SetDestination(gateUid, destZdo.m_uid);
                }
            }

            float radius = decl.BindRadius > 0f ? decl.BindRadius : (EconomyConfig.BindRadius?.Value ?? 4f);
            ZDO? stand = EconomyBindingRegistry.FindNearest(gateZdo.GetPosition(), radius, EconomyBindingRegistry.FixtureKind.ItemStand);
            if (stand == null)
            {
                EconomyRoutingKernel.Publish(gateUid, "turnstile", false, $"{decl.Label} NO STAND", 10);
                return true;
            }

            EconomyValidatorEngine.SnapshotItemStand(stand);
            int mounted = stand.GetInt(ZDOVars.s_item);
            HashSet<int> allowed = ResolveAllowedHashes(decl);
            tokenValid = mounted != 0 && allowed.Contains(mounted);

            EconomyRoutingKernel.Publish(gateUid, "turnstile", tokenValid, tokenValid ? $"{decl.Label} open" : $"{decl.Label} locked", 10);
            return true;
        }

        private static void ConsumeIfConfigured(ZDOID gateUid)
        {
            foreach (EconomyTurnstileDeclaration decl in _turnstiles)
            {
                if (!decl.Consume)
                {
                    continue;
                }
                ZDO? gateZdo = EconomyWriteOps.ResolveLivePortal(decl.Portal);
                if (gateZdo == null || gateZdo.m_uid != gateUid)
                {
                    continue;
                }
                float radius = decl.BindRadius > 0f ? decl.BindRadius : (EconomyConfig.BindRadius?.Value ?? 4f);
                ZDO? stand = EconomyBindingRegistry.FindNearest(gateZdo.GetPosition(), radius, EconomyBindingRegistry.FixtureKind.ItemStand);
                if (stand == null)
                {
                    continue;
                }
                PortalOwnership.ClaimAndWrite(stand, z => z.Set(ZDOVars.s_item, 0));
                ItemLedger.RecordTransfer("EconomyTurnstile", decl.Label, 1);
            }
        }

        private static HashSet<int> ResolveAllowedHashes(EconomyTurnstileDeclaration decl)
        {
            if (_allowedHashCache.TryGetValue(decl, out HashSet<int> cached))
            {
                return cached;
            }
            var set = new HashSet<int>();
            foreach (string name in decl.AllowedItems)
            {
                if (!string.IsNullOrEmpty(name))
                {
                    set.Add(name.GetStableHashCode());
                }
            }
            _allowedHashCache[decl] = set;
            return set;
        }
    }
}
