using System.Collections.Generic;
using TortalPortalLite.Core;
using TortalPortalLite.Core.Data;

namespace TortalPortalLite.Subsystems.WorldOps
{
    /// <summary>Admin declaration for one contested route auction (economy.json section "auctions").</summary>
    public sealed class EconomyAuctionDeclaration
    {
        public string Key = "";
        public EconomyPosition TrunkPortal = new EconomyPosition();
        public List<EconomyPosition> Candidates = new List<EconomyPosition>();
        public string Label = "TRUNK";
        public string Currency = "Coins";
        public float CycleSeconds = 0f; // 0 = use EconomyConfig.AuctionCycleSeconds
        public float BindRadius = 0f;
    }

    /// <summary>
    /// #106 Route Auctions. Scarce trunk slots awarded each cycle to whoever has the largest STANDING
    /// escrow balance - the cleanest possible use of the standing-balance primitive, because nothing is
    /// ever debited during bidding: the server reads every candidate's bound chest, ranks them, and
    /// writes the topology. Losers' coins are untouched in their own chests - no clawback, no race with
    /// `ZDOMan.RPC_ZDOData`'s whole-blob conflict resolution, no refund path to get wrong.
    ///
    /// Cycle boundary is tracked in EconomyStateStore (world-time ticks, `ZNet.GetTimeSeconds()`), and the
    /// current standing-high-bid is shown in every LOSING candidate's own tag - a bidder can read the
    /// clearing price by walking to their own gate, with zero extra bookkeeping.
    /// </summary>
    public static class EconomyAuctionEngine
    {
        private static int _lastVersion = -1;
        private static List<EconomyAuctionDeclaration> _auctions = new List<EconomyAuctionDeclaration>();
        private static float _timer;

        public static void OnUpdate(float dt)
        {
            RefreshIfNeeded();
            _timer += dt;
            if (_timer < 2f || _auctions.Count == 0 || ZNet.instance == null)
            {
                return;
            }
            _timer = 0f;
            foreach (EconomyAuctionDeclaration decl in _auctions)
            {
                Evaluate(decl);
            }
        }

        private static void RefreshIfNeeded()
        {
            if (EconomyRegistry.Version != _lastVersion)
            {
                _lastVersion = EconomyRegistry.Version;
                _auctions = EconomyRegistry.Section<EconomyAuctionDeclaration>("auctions");
            }
        }

        private static void Evaluate(EconomyAuctionDeclaration decl)
        {
            if (string.IsNullOrEmpty(decl.Key) || decl.Candidates.Count == 0)
            {
                return;
            }
            ZDO? trunkZdo = EconomyWriteOps.ResolveLivePortal(decl.TrunkPortal);
            if (trunkZdo == null)
            {
                return;
            }

            double now = ZNet.instance.GetTimeSeconds();
            string cycleKey = $"auctioncycle:{decl.Key}";
            long nextCycleMs = EconomyStateStore.GetLong(cycleKey, 0L);
            float cycleSeconds = decl.CycleSeconds > 0f ? decl.CycleSeconds : (EconomyConfig.AuctionCycleSeconds?.Value ?? 86400f);
            if (nextCycleMs == 0L)
            {
                EconomyStateStore.SetLong(cycleKey, (long)((now + cycleSeconds) * 1000.0));
            }
            else if (now >= nextCycleMs / 1000.0)
            {
                EconomyStateStore.SetLong(cycleKey, (long)((now + cycleSeconds) * 1000.0));
                EconomyStateStore.SetLong($"auctionwinner:{decl.Key}", -1L); // force a fresh award this pass
            }

            float radius = decl.BindRadius > 0f ? decl.BindRadius : (EconomyConfig.BindRadius?.Value ?? 4f);

            int bestIndex = -1;
            int bestBalance = -1;
            var balances = new int[decl.Candidates.Count];
            for (int i = 0; i < decl.Candidates.Count; i++)
            {
                balances[i] = 0;
                ZDO? candidateZdo = EconomyWriteOps.ResolveLivePortal(decl.Candidates[i]);
                if (candidateZdo == null)
                {
                    continue;
                }
                ZDO? chest = EconomyBindingRegistry.FindNearest(candidateZdo.GetPosition(), radius, EconomyBindingRegistry.FixtureKind.Container);
                if (chest == null || ZdoInventoryIO.IsBusy(chest))
                {
                    continue;
                }
                (int width, int height) = EconomyTollEscrowEngine.ResolveContainerSize(chest);
                if (width <= 0 || height <= 0)
                {
                    continue;
                }
                Inventory? inv = ZdoInventoryIO.Load(chest, width, height);
                if (inv == null)
                {
                    continue;
                }
                balances[i] = inv.CountItems(decl.Currency);
                if (balances[i] > bestBalance)
                {
                    bestBalance = balances[i];
                    bestIndex = i;
                }
            }

            if (bestIndex < 0)
            {
                EconomyRoutingKernel.Publish(trunkZdo.m_uid, "auction", false, $"{decl.Label} no bids", 10);
                return;
            }

            ZDO? winnerZdo = EconomyWriteOps.ResolveLivePortal(decl.Candidates[bestIndex]);
            if (winnerZdo == null)
            {
                return;
            }

            EconomyRoutingKernel.SetDestination(trunkZdo.m_uid, winnerZdo.m_uid);
            EconomyRoutingKernel.Publish(trunkZdo.m_uid, "auction", true, $"{decl.Label} ★ held", 10);

            for (int i = 0; i < decl.Candidates.Count; i++)
            {
                ZDO? candidateZdo = EconomyWriteOps.ResolveLivePortal(decl.Candidates[i]);
                if (candidateZdo == null)
                {
                    continue;
                }
                if (i == bestIndex)
                {
                    EconomyRoutingKernel.SetDestination(candidateZdo.m_uid, trunkZdo.m_uid);
                    EconomyRoutingKernel.Publish(candidateZdo.m_uid, "auction", true, $"{decl.Label} ★ — held", 10);
                }
                else
                {
                    EconomyRoutingKernel.Publish(candidateZdo.m_uid, "auction", false, $"{decl.Label} outbid ({bestBalance}c)", 10);
                }
            }

            long lastWinner = EconomyStateStore.GetLong($"auctionwinner:{decl.Key}", -2L);
            if (lastWinner != bestIndex)
            {
                EconomyStateStore.SetLong($"auctionwinner:{decl.Key}", bestIndex);
                EconomyAnnounce.Broadcast($"Auction: {decl.Label} awarded (clearing {bestBalance}c).", center: true);
                EconomyAnnounce.PushPinToEveryone($"{decl.Label} terminus", winnerZdo.GetPosition());
            }
        }
    }
}
