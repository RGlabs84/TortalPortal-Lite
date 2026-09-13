using System.Collections.Generic;
using UnityEngine;
using TortalPortalLite.Core;
using TortalPortalLite.Core.Data;

namespace TortalPortalLite.Subsystems.WorldOps
{
    /// <summary>Admin declaration for one tolled portal (economy.json section "tolls"). Also the declaration EconomyTransitTollEngine (#93) reads for its per-transit debit layer, and EconomyTreasuryEngine (#100) for its tax-skim split - one declaration serves all three, matching the catalog's own "layer a meter on top" framing of #93 as sitting directly on #91.</summary>
    public sealed class EconomyTollDeclaration
    {
        public EconomyPosition Portal = new EconomyPosition();
        public EconomyPosition? Destination;
        public string Label = "TOLL";
        public string Currency = "Coins";
        public int Price = 10;

        /// <summary>0 disables per-transit consumption entirely - #91's own "flagship" design is a pure standing-balance gate, no debit at all.</summary>
        public int PerTransit = 0;

        /// <summary>0 = use EconomyConfig.BindRadius.</summary>
        public float BindRadius = 0f;

        /// <summary>0..1 fraction of every debit routed to the treasury instead of just vanishing (#100's tax skim). 0 = no skim.</summary>
        public float TreasurySkim = 0f;
    }

    /// <summary>
    /// #91 Prepaid Toll Escrow. A gate that is connected only while a bound chest holds at least N of an
    /// item - the flagship genuinely-enforced economy mechanic: the check happens BEFORE anyone travels,
    /// every tick, so there is no debit to race and no way to travel-then-refuse. Composes with
    /// EconomyRoutingKernel (#88/#89) as a single named condition ("toll") per declared portal - a chest
    /// that a player is holding open (s_inUse) or that has gone missing simply leaves the previous
    /// published condition in place for this tick, which is exactly "the gate holds its last state"
    /// mitigation the catalog's own failure-modes list calls for.
    /// </summary>
    public static class EconomyTollEscrowEngine
    {
        private static int _lastVersion = -1;
        private static List<EconomyTollDeclaration> _tolls = new List<EconomyTollDeclaration>();
        private static readonly Dictionary<int, (int width, int height)> _containerSizeCache = new Dictionary<int, (int, int)>();
        private static float _timer;

        public static IReadOnlyList<EconomyTollDeclaration> Declarations => _tolls;

        public static void OnUpdate(float dt)
        {
            RefreshIfNeeded();
            _timer += dt;
            float interval = EconomyConfig.EscrowPollSeconds?.Value ?? 1f;
            if (_timer < interval || _tolls.Count == 0)
            {
                return;
            }
            _timer = 0f;
            foreach (EconomyTollDeclaration toll in _tolls)
            {
                Evaluate(toll);
            }
        }

        private static void RefreshIfNeeded()
        {
            if (EconomyRegistry.Version != _lastVersion)
            {
                _lastVersion = EconomyRegistry.Version;
                _tolls = EconomyRegistry.Section<EconomyTollDeclaration>("tolls");
            }
        }

        private static void Evaluate(EconomyTollDeclaration toll)
        {
            ZDO? gateZdo = EconomyWriteOps.ResolveLivePortal(toll.Portal);
            if (gateZdo == null)
            {
                return;
            }
            ZDOID gateUid = gateZdo.m_uid;

            if (toll.Destination != null)
            {
                ZDO? destZdo = EconomyWriteOps.ResolveLivePortal(toll.Destination);
                if (destZdo != null)
                {
                    EconomyRoutingKernel.SetDestination(gateUid, destZdo.m_uid);
                }
            }

            float radius = toll.BindRadius > 0f ? toll.BindRadius : (EconomyConfig.BindRadius?.Value ?? 4f);
            ZDO? chest = EconomyBindingRegistry.FindNearest(gateZdo.GetPosition(), radius, EconomyBindingRegistry.FixtureKind.Container);
            if (chest == null)
            {
                EconomyRoutingKernel.Publish(gateUid, "toll", false, $"{toll.Label} NO METER", 10);
                return;
            }

            if (ZdoInventoryIO.IsBusy(chest))
            {
                // Held UI open - skip entirely this tick rather than misread a mid-edit blob.
                return;
            }

            (int width, int height) = ResolveContainerSize(chest);
            if (width <= 0 || height <= 0)
            {
                return;
            }

            Inventory? inv = ZdoInventoryIO.Load(chest, width, height);
            if (inv == null)
            {
                return;
            }
            EconomyValidatorEngine.SnapshotContainerItems(chest);

            int balance = inv.CountItems(toll.Currency);
            bool open = balance >= toll.Price;
            int shown = balance > toll.Price ? toll.Price : balance;
            string tag = $"{toll.Label} {shown}/{toll.Price}c";
            EconomyRoutingKernel.Publish(gateUid, "toll", open, tag, 10);
        }

        /// <summary>
        /// Debits <paramref name="amount"/> of <paramref name="currency"/> from a chest already confirmed
        /// not busy, splitting <paramref name="treasurySkimFraction"/> of it into the treasury chest if
        /// one is configured (#100). Self-correcting by design (catalog #91's own citation): if this
        /// write races and loses to the owning client's next whole-ZDO push, the NEXT standing-balance
        /// read in <see cref="Evaluate"/> simply reflects whatever the balance actually is - a lost debit
        /// is never a lost enforcement decision.
        /// </summary>
        public static int TryDebit(ZDO chest, string currency, int amount, float treasurySkimFraction = 0f)
        {
            if (chest == null || !chest.IsValid() || amount <= 0 || ZdoInventoryIO.IsBusy(chest))
            {
                return 0;
            }
            (int width, int height) = ResolveContainerSize(chest);
            if (width <= 0 || height <= 0)
            {
                return 0;
            }
            Inventory? inv = ZdoInventoryIO.Load(chest, width, height);
            if (inv == null)
            {
                return 0;
            }
            int available = inv.CountItems(currency);
            int toRemove = Mathf.Min(available, amount);
            if (toRemove <= 0)
            {
                return 0;
            }

            int toTreasury = treasurySkimFraction > 0f ? Mathf.Clamp(Mathf.RoundToInt(toRemove * treasurySkimFraction), 0, toRemove) : 0;
            inv.RemoveItem(currency, toRemove);
            ZdoInventoryIO.Save(chest, inv);
            EconomyValidatorEngine.SnapshotContainerItems(chest);
            ItemLedger.RecordTransfer("EconomyToll", currency, toRemove);

            if (toTreasury > 0)
            {
                EconomyTreasuryEngine.Deposit(currency, toTreasury);
            }
            return toRemove;
        }

        /// <summary>Public - reused by EconomyAuctionEngine so both engines resolve a chest's real Inventory dimensions the same way instead of guessing a fixed size.</summary>
        public static (int, int) ResolveContainerSize(ZDO chest)
        {
            int hash = chest.GetPrefab();
            if (_containerSizeCache.TryGetValue(hash, out var size))
            {
                return size;
            }
            GameObject? prefab = ZNetScene.instance?.GetPrefab(hash);
            Container? container = prefab != null ? prefab.GetComponent<Container>() : null;
            var result = container != null ? (container.m_width, container.m_height) : (0, 0);
            _containerSizeCache[hash] = result;
            return result;
        }
    }
}
