using System.Collections.Generic;
using TortalPortalLite.Core;
using TortalPortalLite.Core.Data;

namespace TortalPortalLite.Subsystems.WorldOps
{
    /// <summary>Admin declaration for one door-selected route (economy.json section "doorLevers").</summary>
    public sealed class EconomyDoorLeverDeclaration
    {
        public EconomyPosition Portal = new EconomyPosition();
        public string Label = "GATE";

        /// <summary>Destination while the bound door reads s_state == 1 (opened from the front).</summary>
        public EconomyPosition? DestinationForward;

        /// <summary>Destination while the bound door reads s_state == -1 (opened from the back).</summary>
        public EconomyPosition? DestinationBackward;

        public float BindRadius = 0f;
    }

    /// <summary>Admin declaration for one output-only door signal (economy.json section "portcullises") - the door physically opens/shuts in lockstep with the given portal's own economy-computed open/closed state.</summary>
    public sealed class EconomyPortcullisDeclaration
    {
        public EconomyPosition Portal = new EconomyPosition();
        public float BindRadius = 0f;
    }

    /// <summary>
    /// #275 Door Lever (Three-State Physical Input) + #276 Server Portcullis (Door as Output) - combined
    /// because both are "a vanilla Door's ZDOVars.s_state read or written next to a managed portal",
    /// input and output halves of the same primitive.
    ///
    /// Lever (input, #275): `Door.RPC_UseDoor` writes s_state to 0 (closed)/1 (opened forward)/-1 (opened
    /// backward) on the door's OWNING client - which side the player stood on decides the sign, so a
    /// single vanilla door is a 3-way selector operated by walking around it. This engine only READS the
    /// field (client-authored, catalog's own citation: "treat it as a selector, never as a payment") and
    /// selects a destination; it never writes the lever door itself.
    ///
    /// Portcullis (output, #276): the server WRITES s_state directly (`ZDO.Set` has no ownership check,
    /// catalog's own citation :73648-73654) to mirror EconomyRoutingKernel's own open/closed verdict for a
    /// portal onto a real door blocking its frame - a physical, in-world signal that needs no HUD channel
    /// and is honestly documented as UX only (catalog's own citation: the connection write remains the
    /// real enforcement; a player can still vault/destroy the door).
    /// </summary>
    public static class EconomyDoorEngine
    {
        private static int _lastLeverVersion = -1;
        private static int _lastPortcullisVersion = -1;
        private static List<EconomyDoorLeverDeclaration> _levers = new List<EconomyDoorLeverDeclaration>();
        private static List<EconomyPortcullisDeclaration> _portcullises = new List<EconomyPortcullisDeclaration>();
        private static float _timer;

        public static void OnUpdate(float dt)
        {
            RefreshIfNeeded();
            _timer += dt;
            float interval = EconomyConfig.DoorPollSeconds?.Value ?? 0.5f;
            if (_timer < interval)
            {
                return;
            }
            _timer = 0f;

            foreach (EconomyDoorLeverDeclaration decl in _levers)
            {
                EvaluateLever(decl);
            }
            foreach (EconomyPortcullisDeclaration decl in _portcullises)
            {
                EvaluatePortcullis(decl);
            }
        }

        private static void RefreshIfNeeded()
        {
            if (EconomyRegistry.Version != _lastLeverVersion)
            {
                _lastLeverVersion = EconomyRegistry.Version;
                _levers = EconomyRegistry.Section<EconomyDoorLeverDeclaration>("doorLevers");
            }
            if (EconomyRegistry.Version != _lastPortcullisVersion)
            {
                _lastPortcullisVersion = EconomyRegistry.Version;
                _portcullises = EconomyRegistry.Section<EconomyPortcullisDeclaration>("portcullises");
            }
        }

        private static void EvaluateLever(EconomyDoorLeverDeclaration decl)
        {
            ZDO? gateZdo = EconomyWriteOps.ResolveLivePortal(decl.Portal);
            if (gateZdo == null)
            {
                return;
            }
            ZDOID gateUid = gateZdo.m_uid;

            float radius = decl.BindRadius > 0f ? decl.BindRadius : (EconomyConfig.DoorScanRadius?.Value ?? 6f);
            ZDO? door = EconomyBindingRegistry.FindNearest(gateZdo.GetPosition(), radius, EconomyBindingRegistry.FixtureKind.Door);
            if (door == null)
            {
                EconomyRoutingKernel.Publish(gateUid, "leverselect", false, $"{decl.Label} NO LEVER", 10);
                return;
            }

            int state = door.GetInt(ZDOVars.s_state);
            EconomyPosition? target = state == 1 ? decl.DestinationForward : state == -1 ? decl.DestinationBackward : null;

            if (target == null)
            {
                EconomyRoutingKernel.Publish(gateUid, "leverselect", false, $"{decl.Label} closed", 10);
                return;
            }

            ZDO? destZdo = EconomyWriteOps.ResolveLivePortal(target);
            if (destZdo == null)
            {
                EconomyRoutingKernel.Publish(gateUid, "leverselect", false, $"{decl.Label} no dest", 10);
                return;
            }
            EconomyRoutingKernel.SetDestination(gateUid, destZdo.m_uid);
            EconomyRoutingKernel.Publish(gateUid, "leverselect", true, $"{decl.Label}", 10);
        }

        private static void EvaluatePortcullis(EconomyPortcullisDeclaration decl)
        {
            ZDO? gateZdo = EconomyWriteOps.ResolveLivePortal(decl.Portal);
            if (gateZdo == null)
            {
                return;
            }

            float radius = decl.BindRadius > 0f ? decl.BindRadius : (EconomyConfig.DoorScanRadius?.Value ?? 6f);
            ZDO? door = EconomyBindingRegistry.FindNearest(gateZdo.GetPosition(), radius, EconomyBindingRegistry.FixtureKind.Door);
            if (door == null)
            {
                return;
            }

            bool open = EconomyRoutingKernel.IsCurrentlyOpen(gateZdo.m_uid);
            int desiredState = open ? 1 : 0;
            if (door.GetInt(ZDOVars.s_state) != desiredState)
            {
                PortalOwnership.ClaimAndWrite(door, z => z.Set(ZDOVars.s_state, desiredState));
            }
        }
    }
}
