using TortalPortalLite.Core;
using TortalPortalLite.Core.Data;
using TortalPortalLite.Subsystems.Topology;

namespace TortalPortalLite.Subsystems.WorldOps
{
    /// <summary>
    /// #93 Reactive Transit Toll. There is no server hook on the moment of transit (catalog's own
    /// citation: TeleportWorldTrigger.OnTriggerEnter is gated on Player.m_localPlayer == component, which
    /// never runs server-side) - this engine detects that a transit ALREADY HAPPENED via
    /// RoutingTransitDetector's position-straddle heuristic and bills afterwards. Genuinely useful for
    /// metering/auditing a toll's PerTransit consumption layer, structurally incapable of preventing
    /// anything - honestly "reactive-detection-only" per the catalog's own enforcement tier, never
    /// promoted to "server-enforced" here.
    ///
    /// Billing target is the FROM portal's bound escrow chest (the gate the traveller departed through) -
    /// debited via EconomyTollEscrowEngine.TryDebit, which is itself self-correcting (catalog's own
    /// citation: a debit that races and loses just means the NEXT standing-balance read reflects
    /// whatever is really in the chest, so the enforcement decision in EconomyTollEscrowEngine is never
    /// wrong even if this billing layer is). A shortfall becomes debt via EconomyDebtLienEngine (#94)
    /// rather than ever attempting to reverse or refuse the trip that already happened.
    /// </summary>
    public static class EconomyTransitTollEngine
    {
        private static readonly RoutingTransitDetector _detector = new RoutingTransitDetector();
        private static float _timer;

        public static void OnUpdate(float dt)
        {
            if (EconomyConfig.Enabled?.Value == false)
            {
                return;
            }
            _timer += dt;
            float interval = EconomyConfig.TransitPollSeconds?.Value ?? 0.25f;
            if (_timer < interval)
            {
                return;
            }
            _timer = 0f;

            float radius = EconomyConfig.TransitStraddleRadius?.Value ?? 8f;
            var transits = _detector.Poll(radius);
            if (transits.Count == 0)
            {
                return;
            }

            var tolls = EconomyTollEscrowEngine.Declarations;
            if (tolls.Count == 0)
            {
                return;
            }

            foreach (RoutingTransitDetector.Transit transit in transits)
            {
                // False-positive suppression (catalog's own citations): a dead character sample and an
                // admin's own goto/recall teleport should never be billed.
                if (transit.Character.Zdo.GetBool(ZDOVars.s_dead))
                {
                    continue;
                }
                string? host = SenderContext.HostNameOf(transit.Character.Peer);
                if (!string.IsNullOrEmpty(host) && ZNet.instance != null && ZNet.instance.IsAdmin(host))
                {
                    continue;
                }

                foreach (EconomyTollDeclaration toll in tolls)
                {
                    if (toll.PerTransit <= 0)
                    {
                        continue;
                    }
                    ZDO? gateZdo = EconomyWriteOps.ResolveLivePortal(toll.Portal);
                    if (gateZdo == null || gateZdo.m_uid != transit.From.Uid)
                    {
                        continue;
                    }

                    float bindRadius = toll.BindRadius > 0f ? toll.BindRadius : (EconomyConfig.BindRadius?.Value ?? 4f);
                    ZDO? chest = EconomyBindingRegistry.FindNearest(gateZdo.GetPosition(), bindRadius, EconomyBindingRegistry.FixtureKind.Container);
                    int paid = chest != null ? EconomyTollEscrowEngine.TryDebit(chest, toll.Currency, toll.PerTransit, toll.TreasurySkim) : 0;
                    int owed = toll.PerTransit - paid;

                    if (owed > 0)
                    {
                        EconomyDebtLienEngine.AddDebt(transit.Character.PlayerId, owed);
                        PlayerNotify.Toast(transit.Character, $"{toll.Label} toll unpaid - {owed} owed.");
                    }
                    else if (paid > 0)
                    {
                        PlayerNotify.Toast(transit.Character, $"{toll.Label} toll: {paid} {toll.Currency}.");
                    }
                }
            }
        }
    }
}
