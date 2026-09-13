using System;
using System.Collections.Generic;
using TortalPortalLite.Core;
using TortalPortalLite.Core.Data;
using TortalPortalLite.Core.Hooks;
using TortalPortalLite.Subsystems.Foundations;

namespace TortalPortalLite.Subsystems.WorldOps
{
    /// <summary>Admin declaration for one leased portal bay (economy.json section "leases").</summary>
    public sealed class EconomyLeaseDeclaration
    {
        public EconomyPosition Portal = new EconomyPosition();
        public EconomyPosition? Destination;
        public string Label = "BAY";
        public string Currency = "Coins";
        public int RentPrice = 100;

        /// <summary>Real seconds one paid lease term lasts.</summary>
        public float LeaseSeconds = 604800f;

        /// <summary>Real seconds after expiry before repossession, if AutoRepossess is on.</summary>
        public float GraceSeconds = 86400f;

        /// <summary>Off by default - repossession (ZDO destruction) is irreversible, so an admin must opt in explicitly; a lapsed lease still parks the gate either way.</summary>
        public bool AutoRepossess = false;

        public float BindRadius = 0f;
    }

    /// <summary>
    /// #103 Leases, Rent, Repossession and Caps. The lease clock is `ZNet.instance.GetTimeSeconds()` - the
    /// monotonic, server-authoritative, persisted world clock (catalog's own citation) - never
    /// `Time.time`, which resets on restart. Auto-renewal reuses EconomyTollEscrowEngine.TryDebit against
    /// the SAME bound-chest primitive every toll declaration uses: as long as the bay's chest can cover
    /// `RentPrice` when the term rolls over, the lease silently renews for another `LeaseSeconds`; when it
    /// cannot, the gate parks (`LAPSED`) and, only if the admin opted in, is repossessed after
    /// `GraceSeconds` of continued non-payment.
    ///
    /// State (lease-until timestamp) lives in EconomyStateStore, not a new ZDO key, per this wave's
    /// "do not invent new PortalKeys.cs entries mid-wave" rule - see that store's own doc comment for the
    /// full reasoning and the NEEDS NEW KEY note it already carries.
    /// </summary>
    public static class EconomyLeaseEngine
    {
        private static int _lastVersion = -1;
        private static List<EconomyLeaseDeclaration> _leases = new List<EconomyLeaseDeclaration>();
        private static float _timer;

        public static void OnUpdate(float dt)
        {
            RefreshIfNeeded();
            _timer += dt;
            float interval = EconomyConfig.LeaseEvalSeconds?.Value ?? 5f;
            if (_timer < interval || _leases.Count == 0 || ZNet.instance == null)
            {
                return;
            }
            _timer = 0f;
            foreach (EconomyLeaseDeclaration decl in _leases)
            {
                Evaluate(decl);
            }
        }

        private static void RefreshIfNeeded()
        {
            if (EconomyRegistry.Version != _lastVersion)
            {
                _lastVersion = EconomyRegistry.Version;
                _leases = EconomyRegistry.Section<EconomyLeaseDeclaration>("leases");
            }
        }

        private static void Evaluate(EconomyLeaseDeclaration decl)
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

            string key = EconomyStateStore.PositionKey("lease", decl.Portal.ToVector3());
            double now = ZNet.instance.GetTimeSeconds();
            long untilMs = EconomyStateStore.GetLong(key, 0L);
            if (untilMs == 0L)
            {
                // First time this bay has been seen - seed a fresh full term rather than starting lapsed.
                untilMs = (long)((now + decl.LeaseSeconds) * 1000.0);
                EconomyStateStore.SetLong(key, untilMs);
            }
            double until = untilMs / 1000.0;

            if (now >= until)
            {
                float radius = decl.BindRadius > 0f ? decl.BindRadius : (EconomyConfig.BindRadius?.Value ?? 4f);
                ZDO? chest = EconomyBindingRegistry.FindNearest(gateZdo.GetPosition(), radius, EconomyBindingRegistry.FixtureKind.Container);
                int paid = chest != null ? EconomyTollEscrowEngine.TryDebit(chest, decl.Currency, decl.RentPrice) : 0;
                if (paid >= decl.RentPrice)
                {
                    until = now + decl.LeaseSeconds;
                    EconomyStateStore.SetLong(key, (long)(until * 1000.0));
                }
            }

            bool lapsed = now >= until;
            if (lapsed && decl.AutoRepossess && now >= until + decl.GraceSeconds)
            {
                Repossess(gateZdo, key);
                return;
            }

            string tag = lapsed ? $"{decl.Label} LAPSED" : $"{decl.Label} {DaysLeft(until, now)}d left";
            EconomyRoutingKernel.Publish(gateUid, "lease", !lapsed, tag, 10);
        }

        private static int DaysLeft(double until, double now) => Math.Max(0, (int)((until - now) / 86400.0));

        private static void Repossess(ZDO gateZdo, string stateKey)
        {
            try
            {
                gateZdo.SetOwner(ZDOMan.GetSessionID());
                ZDOMan.instance?.DestroyZDO(gateZdo);
                EconomyStateStore.RemoveLong(stateKey);
                PortalDebug.LogAlways($"[EconomyLeaseEngine] repossessed lapsed lease at {gateZdo.GetPosition()}.");
            }
            catch (Exception ex)
            {
                PortalDebug.LogWarning($"[EconomyLeaseEngine] repossession failed: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// #103's other half - the build cap. The one place a server-only mod is STRONGER than a client mod:
    /// a dedicated server cannot hook `Player.PlacePiece` or `ZNetScene.Instantiate` (no `Player` instance
    /// ever exists server-side), but this mod already has a working, general "a client-authored ZDO
    /// change just arrived" broker in `Core/Hooks/RpcZdoDataHook` - the very first `ZDOMan.RPC_ZDOData`
    /// call that ever delivers a brand-new portal ZDO to the server passes through it. Counting existing
    /// portals live off `ZDOMan.instance.GetPortalList()` (never the 1Hz PortalCensus snapshot, which
    /// could be stale by the time a rapid string of placements arrives) keeps this exact and race-free.
    /// </summary>
    public static class EconomyBuildCapEngine
    {
        private static readonly HashSet<ZDOID> _seen = new HashSet<ZDOID>();

        public static void Initialize()
        {
            RpcZdoDataHook.RegisterPostfix(40, OnZdoData);
        }

        private static void OnZdoData(ZNetPeer? sender, ZDOID zdoid)
        {
            int cap = EconomyConfig.BuildCapDefault?.Value ?? 0;
            if (cap <= 0 || ZDOMan.instance == null || _seen.Contains(zdoid))
            {
                return;
            }
            ZDO? zdo = ZDOMan.instance.GetZDO(zdoid);
            if (zdo == null || !zdo.IsValid() || !PortalRegistry.IsPortalPrefabHash(zdo.GetPrefab()))
            {
                return;
            }
            _seen.Add(zdoid);

            long creator = zdo.GetLong(ZDOVars.s_creator, 0L);
            if (creator == 0)
            {
                // #103's own citation: creator 0 is world-gen/admin/pre-field-tracking infrastructure -
                // never capped.
                return;
            }

            int count = 0;
            foreach (ZDO p in ZDOMan.instance.GetPortalList())
            {
                if (p != null && p.IsValid() && p.GetLong(ZDOVars.s_creator, 0L) == creator)
                {
                    count++;
                }
            }

            if (count > cap)
            {
                try
                {
                    zdo.SetOwner(ZDOMan.GetSessionID());
                    ZDOMan.instance.DestroyZDO(zdo);
                    PortalDebug.LogAlways($"[EconomyBuildCapEngine] destroyed over-cap portal for creator {creator} ({count}/{cap}).");
                    NotifyCreator(creator, cap);
                }
                catch (Exception ex)
                {
                    PortalDebug.LogWarning($"[EconomyBuildCapEngine] failed to enforce build cap: {ex.Message}");
                }
            }
        }

        private static void NotifyCreator(long creatorPlayerId, int cap)
        {
            foreach (ConnectedCharacter who in ConnectedCharacters.All())
            {
                if (who.PlayerId == creatorPlayerId)
                {
                    PlayerNotify.Toast(who, $"Portal limit reached ({cap}). Dismantle one first.");
                    return;
                }
            }
        }
    }
}
