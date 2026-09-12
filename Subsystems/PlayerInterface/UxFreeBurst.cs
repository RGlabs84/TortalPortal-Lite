using System.Collections.Generic;
using TortalPortalLite.Core.Data;

namespace TortalPortalLite.Subsystems.PlayerInterface
{
    /// <summary>
    /// #264 The Free Burst - vanilla's own "portal connected" effect via a None-&gt;X connection flip.
    /// `TeleportWorld.UpdatePortal` polls `HaveTarget()` every 0.5s and fires `m_connected.Create(...)`
    /// only on the RISING edge (was disconnected, now connected) - so a portal going straight from one
    /// real target to ANOTHER real target never re-triggers it. This engine forces that edge: write
    /// `ConnectionType.Portal -&gt; ZDOID.None` now, wait long enough for every nearby client to have
    /// polled and observed it (two 0.5s polls plus network slack), then write the real target.
    ///
    /// Deliberately NOT wired into UxDialAction's default path: the None step is a genuine ~1.1s window
    /// where the portal is un-walkable, which trades away #152's own "PRE-WARM so the first walk-through
    /// works" guarantee for a cosmetic flourish. Exposed as an explicit opt-in for callers that want the
    /// visual replay and can tolerate the delay (e.g. an admin "reroute" command, not a player's own
    /// live dial) - see UxDialAction's own doc comment for why its default path writes the real
    /// connection immediately instead.
    /// </summary>
    public static class UxFreeBurst
    {
        private const float ReconnectDelaySeconds = 1.1f;

        private readonly struct Pending
        {
            public readonly ZDOID Zdo;
            public readonly ZDOID Target;
            public readonly float FireAtClock;
            public Pending(ZDOID zdo, ZDOID target, float fireAtClock) { Zdo = zdo; Target = target; FireAtClock = fireAtClock; }
        }

        private static readonly List<Pending> _pending = new List<Pending>();
        private static float _clock;

        public static void OnUpdate(float dt)
        {
            _clock += dt;
            if (_pending.Count == 0 || ZDOMan.instance == null)
            {
                return;
            }
            for (int i = _pending.Count - 1; i >= 0; i--)
            {
                Pending p = _pending[i];
                if (_clock < p.FireAtClock)
                {
                    continue;
                }
                _pending.RemoveAt(i);
                ZDO zdo = ZDOMan.instance.GetZDO(p.Zdo);
                if (zdo == null || !zdo.IsValid())
                {
                    continue;
                }
                PortalOwnership.ClaimAndWrite(zdo, z => z.SetConnection(ZDOExtraData.ConnectionType.Portal, p.Target));
            }
        }

        /// <summary>Schedules the None-&gt;target replay cycle. Safe to call on a portal that is currently unconnected too (the None write is then a harmless no-op per ZDOExtraData's own equal-value skip).</summary>
        public static void ScheduleReconnectBurst(ZDO portalZdo, ZDOID target)
        {
            if (portalZdo == null || !portalZdo.IsValid())
            {
                return;
            }
            PortalOwnership.ClaimAndWrite(portalZdo, z => z.SetConnection(ZDOExtraData.ConnectionType.Portal, ZDOID.None));
            _pending.Add(new Pending(portalZdo.m_uid, target, _clock + ReconnectDelaySeconds));
        }
    }
}
