using TortalPortalLite.Core;
using TortalPortalLite.Core.Data;
using TortalPortalLite.Subsystems.Foundations;

namespace TortalPortalLite.Subsystems.Enforcement
{
    /// <summary>
    /// #121's "ONE-WAY OUTBOUND ONLY" ruleset mechanism: every reciprocal pair keeps its outbound
    /// connection but the return leg is nulled. Runtime-only by construction - `ZDOMan.ConnectPortals`
    /// (the load-time hash-relinking pass, distinct from `Game.ConnectPortals`) writes BOTH directions
    /// from the matched hash pair on every world load, so this must be re-asserted continuously rather
    /// than applied once; a 2s reconcile tick does exactly that, and is cheap because
    /// ZDOExtraData.SetConnection's own idempotence means re-nulling an already-None connection performs
    /// no write at all.
    ///
    /// Canonical direction is derived from ZDOID ordering (UserID then ID) rather than any semantic
    /// "which one did the admin mean as the entrance" - the catalog names no such semantic, and a stable,
    /// arbitrary-but-consistent pick is sufficient to make the rule deterministic.
    /// </summary>
    public static class LockdownOneWayEngine
    {
        private const float ReconcileInterval = 2f;
        private static bool _enabled;
        private static float _timer;

        public static void SetEnabled(bool on) => _enabled = on;

        public static void OnUpdate(float dt)
        {
            if (!_enabled)
            {
                return;
            }
            _timer += dt;
            if (_timer < ReconcileInterval)
            {
                return;
            }
            _timer = 0f;
            Reconcile();
        }

        private static void Reconcile()
        {
            if (ZDOMan.instance == null)
            {
                return;
            }
            foreach (PortalRecord rec in PortalCensus.Latest)
            {
                if (rec.Connection == ZDOID.None || !PortalCensus.TryGet(rec.Connection, out PortalRecord partner))
                {
                    continue;
                }
                if (partner.Connection != rec.Uid)
                {
                    continue; // Not currently mutual - either already one-way, or mid-transition; leave it.
                }
                if (!IsCanonicalSource(rec.Uid, partner.Uid))
                {
                    continue; // Process each mutual pair once, from the canonical source's perspective.
                }
                if (!LockdownWriteBudget.TryConsume())
                {
                    continue;
                }
                ZDO returnLeg = ZDOMan.instance.GetZDO(partner.Uid);
                if (returnLeg == null || !returnLeg.IsValid())
                {
                    continue;
                }
                PortalOwnership.ClaimAndWrite(returnLeg, z => z.SetConnection(ZDOExtraData.ConnectionType.Portal, ZDOID.None));
            }
        }

        private static bool IsCanonicalSource(ZDOID a, ZDOID b)
        {
            return a.UserID != b.UserID ? a.UserID < b.UserID : a.ID < b.ID;
        }
    }
}
