using System;
using System.Collections.Generic;
using TortalPortalLite.Core;

namespace TortalPortalLite.Subsystems.Topology
{
    /// <summary>One admin-declared time window and what a schedule should read while it matches.</summary>
    public sealed class RoutingScheduleWindow
    {
        /// <summary>DayFraction clock: 0..1 (supports wraparound, From &gt; To means "crosses midnight", e.g. dusk-to-dawn).</summary>
        public float From = 0f;
        public float To = 1f;

        /// <summary>DayCounter clock only: window matches when EnvMan.GetDay() % EveryNDays == 0. 0 disables this window under DayCounter.</summary>
        public int EveryNDays = 0;

        /// <summary>RealClock clock only: local "HH:mm" bounds. Null disables this window under RealClock.</summary>
        public string? FromClock;
        public string? ToClock;

        public RoutingPosition? Destination;
        public string? Tag;
    }

    /// <summary>Admin declaration for one scheduled portal (routing.json section "schedules").</summary>
    public sealed class RoutingScheduleDefinition
    {
        public string Name = "";
        public RoutingPosition Position = new RoutingPosition();

        /// <summary>"dayfraction" | "daycounter" | "realclock".</summary>
        public string Clock = "dayfraction";

        public List<RoutingScheduleWindow> Windows = new List<RoutingScheduleWindow>();

        /// <summary>Tag shown when no window currently matches (portal reads unconnected). Null = leave the tag alone entirely.</summary>
        public string? ClosedTag;
    }

    /// <summary>
    /// #28 Scheduled Routing. Three independent server-readable clocks, exactly as the catalog
    /// distinguishes them: EnvMan.instance.GetDayFraction() (:96290, m_smoothDayFraction - confirmed
    /// live headless: Game.UpdateSleeping dereferences EnvMan.instance unguarded inside an
    /// IsServer()-gated block, :100714-100731), EnvMan.instance.GetDay() (:96295-96298, a plain
    /// ZNet.GetTimeSeconds()/m_dayLengthSec division, m_dayLengthSec=1200, :95406), and plain
    /// DateTime.UtcNow for a real-world wall-clock schedule, immune to in-game sleep-skipping.
    ///
    /// LEVEL-TRIGGERED BY CONSTRUCTION, not edge-triggered: every tick this engine computes "what
    /// window matches RIGHT NOW" from scratch and writes only through RoutingWriteOps.Reassert, which
    /// already no-ops when the computed state matches the current one. This is deliberate and load
    /// bearing - EnvMan.SkipToMorning ramps time forward ~12 real seconds across a whole in-game night
    /// when everyone sleeps (:96310-96323), which would silently skip an edge-triggered "did we just
    /// cross a boundary" check entirely. Comparing "what should it be now" against "what is it now"
    /// every tick can never miss a transition this way, sleep-skipped or not.
    /// </summary>
    public static class RoutingScheduledEngine
    {
        private static float _timer;
        private static int _lastRegistryVersion = -1;
        private static List<RoutingScheduleDefinition> _schedules = new List<RoutingScheduleDefinition>();

        public static void OnUpdate(float dt)
        {
            if (RoutingConfig.TakeoverEnabled?.Value == false || ZDOMan.instance == null)
            {
                return;
            }
            _timer += dt;
            float interval = RoutingConfig.ScheduleEvalSeconds?.Value ?? 1.0f;
            if (_timer < interval)
            {
                return;
            }
            _timer = 0f;

            if (RoutingManagedPortalRegistry.Version != _lastRegistryVersion)
            {
                _lastRegistryVersion = RoutingManagedPortalRegistry.Version;
                _schedules = RoutingManagedPortalRegistry.Section<RoutingScheduleDefinition>("schedules");
            }
            if (_schedules.Count == 0)
            {
                return;
            }

            foreach (RoutingScheduleDefinition schedule in _schedules)
            {
                if (!RoutingPairingAuthorityEngine.TryClaim(schedule.Position, $"schedule:{schedule.Name}"))
                {
                    continue;
                }
                ZDO? zdo = RoutingWriteOps.ResolveLive(schedule.Position);
                if (zdo == null)
                {
                    continue;
                }

                RoutingScheduleWindow? match = FindMatch(schedule);
                ZDOID desiredConnection = ZDOID.None;
                if (match?.Destination != null)
                {
                    ZDO? destZdo = RoutingWriteOps.ResolveLive(match.Destination);
                    if (destZdo != null)
                    {
                        desiredConnection = destZdo.m_uid;
                    }
                }
                string? desiredTag = match?.Tag ?? schedule.ClosedTag;

                RoutingWriteOps.Reassert(zdo, desiredTag, desiredConnection);
                RoutingPairingAuthorityEngine.Publish(zdo.m_uid, desiredTag, desiredConnection);
            }
        }

        private static RoutingScheduleWindow? FindMatch(RoutingScheduleDefinition schedule)
        {
            switch (schedule.Clock)
            {
                case "daycounter":
                    return FindDayCounterMatch(schedule);
                case "realclock":
                    return FindRealClockMatch(schedule);
                default:
                    return FindDayFractionMatch(schedule);
            }
        }

        private static RoutingScheduleWindow? FindDayFractionMatch(RoutingScheduleDefinition schedule)
        {
            if (EnvMan.instance == null)
            {
                return null;
            }
            float frac = EnvMan.instance.GetDayFraction();
            foreach (RoutingScheduleWindow window in schedule.Windows)
            {
                bool inWindow = window.From <= window.To
                    ? frac >= window.From && frac < window.To
                    : frac >= window.From || frac < window.To; // wraps across midnight
                if (inWindow)
                {
                    return window;
                }
            }
            return null;
        }

        private static RoutingScheduleWindow? FindDayCounterMatch(RoutingScheduleDefinition schedule)
        {
            if (EnvMan.instance == null)
            {
                return null;
            }
            int day = EnvMan.instance.GetDay();
            foreach (RoutingScheduleWindow window in schedule.Windows)
            {
                if (window.EveryNDays > 0 && day % window.EveryNDays == 0)
                {
                    return window;
                }
            }
            return null;
        }

        private static RoutingScheduleWindow? FindRealClockMatch(RoutingScheduleDefinition schedule)
        {
            TimeSpan now = DateTime.UtcNow.TimeOfDay;
            foreach (RoutingScheduleWindow window in schedule.Windows)
            {
                if (window.FromClock == null || window.ToClock == null)
                {
                    continue;
                }
                if (!TimeSpan.TryParse(window.FromClock, out TimeSpan from) || !TimeSpan.TryParse(window.ToClock, out TimeSpan to))
                {
                    continue;
                }
                bool inWindow = from <= to ? now >= from && now < to : now >= from || now < to;
                if (inWindow)
                {
                    return window;
                }
            }
            return null;
        }
    }
}
