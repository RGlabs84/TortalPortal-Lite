using TortalPortalLite.Core;
using TortalPortalLite.Subsystems.Foundations;

namespace TortalPortalLite.Subsystems.Enforcement
{
    /// <summary>#121's "NIGHT CURFEW" ruleset mechanism - a thin, level-triggered wrapper around Force-Disconnect keyed on the server's own in-game clock (EnvMan.IsNight(), static, server-authoritative) rather than #114's wall-clock schedule file.</summary>
    public static class LockdownNightCurfewEngine
    {
        private const string Reason = "nightcurfew";
        private static bool _enabled;
        private static bool _engaged;

        public static void SetEnabled(bool on)
        {
            _enabled = on;
            if (!on && _engaged)
            {
                LockdownForceDisconnectEngine.DisengageScope(Reason);
                _engaged = false;
            }
        }

        public static void OnUpdate(float dt)
        {
            if (!_enabled || EnvMan.instance == null)
            {
                return;
            }
            bool night = EnvMan.IsNight();
            if (night == _engaged)
            {
                return;
            }
            if (night)
            {
                LockdownForceDisconnectEngine.EngageScope(PortalCensus.Latest, Reason);
                LockdownAnnouncementEngine.BroadcastMessage("Portal network closed for the night.", center: true);
            }
            else
            {
                LockdownForceDisconnectEngine.DisengageScope(Reason);
                LockdownAnnouncementEngine.BroadcastMessage("Portal network is open.", center: true);
            }
            _engaged = night;
        }
    }
}
