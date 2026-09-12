using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using TortalPortalLite.Core;
using TortalPortalLite.Core.Data;
using TortalPortalLite.Subsystems.Foundations;

namespace TortalPortalLite.Subsystems.Enforcement
{
    /// <summary>One admin-declared blackout window. Times are UTC HH:mm literals (catalog #114's own "compute in UTC, convert for display only" rule - a wall-clock schedule across a DST transition otherwise fires twice or not at all).</summary>
    public sealed class LockdownScheduleWindow
    {
        /// <summary>Weekday names ("Monday".."Sunday"), empty/null = every day.</summary>
        public List<string> Days = new List<string>();
        public string StartUtc = "22:00";
        public string EndUtc = "06:00";

        /// <summary>"KeyOnly" (NoPortals), "Disconnect" (force-disconnect), or "TagQuarantine".</summary>
        public string Mode = "Disconnect";
        public string Label = "";
    }

    public sealed class LockdownScheduleFile
    {
        public int SchemaVersion = 1;
        public List<LockdownScheduleWindow> Windows = new List<LockdownScheduleWindow>();
    }

    /// <summary>
    /// #114 Scheduled Blackout Windows. A cron-style schedule that engages/lifts portal restrictions at
    /// fixed times, LEVEL-TRIGGERED never edge-triggered - the reconciler computes the DESIRED state from
    /// wall-clock time every tick and reconciles toward it, so a missed tick (restart across the boundary,
    /// a long save freeze) leaves the world in the wrong state for at most one reconcile interval, never
    /// forever. Two or more overlapping windows resolve by strictest-wins precedence: Disconnect >
    /// TagQuarantine > KeyOnly.
    ///
    /// Persists its own "currently engaged" state (schedule_state.json, distinct from LockdownVault, which
    /// is wiped at every boot per #112's own "then delete the vault" rule) so a restart INSIDE an
    /// already-engaged window silently resumes the lock without re-running the announcement sequence -
    /// the catalog's own explicit warning against re-announcing/re-vaulting over an already-applied state.
    /// </summary>
    public static class LockdownScheduleEngine
    {
        private const string Reason = "schedule";
        private const float PollInterval = 5f;

        private static float _fileTimer;
        private static DateTime _fileStamp = DateTime.MinValue;
        private static List<LockdownScheduleWindow> _windows = new List<LockdownScheduleWindow>();

        private static float _reconcileTimer;
        private static bool _engaged;
        private static string _engagedMode = "";
        private static bool _stateLoaded;

        // Pre-warn announcement de-dupe, keyed by window index + threshold, reset once the window
        // becomes active (so a hot-reload or long-running server doesn't spam the same warning).
        private static readonly HashSet<string> _warned = new HashSet<string>();

        public static void OnUpdate(float dt)
        {
            LoadStateOnce();

            _fileTimer += dt;
            if (_fileTimer >= PollInterval)
            {
                _fileTimer = 0f;
                TryReloadFile();
            }

            _reconcileTimer += dt;
            float interval = LockdownConfig.ScheduleReconcileSeconds?.Value ?? 1f;
            if (_reconcileTimer < interval)
            {
                return;
            }
            _reconcileTimer = 0f;
            Reconcile();
        }

        private static string StateFilePath()
        {
            string world = ZNet.instance != null ? ZNet.instance.GetWorldName() : "default";
            return Path.Combine(BepInEx.Paths.ConfigPath, $"TortalPortalLite.lockdown_schedule_state.{world}.json");
        }

        private sealed class PersistedState
        {
            public bool Engaged;
            public string Mode = "";
        }

        private static void LoadStateOnce()
        {
            if (_stateLoaded)
            {
                return;
            }
            _stateLoaded = true;
            try
            {
                string path = StateFilePath();
                if (!File.Exists(path))
                {
                    return;
                }
                var parsed = JsonConvert.DeserializeObject<PersistedState>(File.ReadAllText(path));
                if (parsed != null)
                {
                    _engaged = parsed.Engaged;
                    _engagedMode = parsed.Mode ?? "";
                    PortalDebug.LogAlways($"[LockdownScheduleEngine] resumed persisted state: engaged={_engaged} mode='{_engagedMode}'.");
                }
            }
            catch (Exception ex)
            {
                PortalDebug.LogWarning($"[LockdownScheduleEngine] failed to load persisted state: {ex.Message}");
            }
        }

        private static void SaveState()
        {
            try
            {
                string json = JsonConvert.SerializeObject(new PersistedState { Engaged = _engaged, Mode = _engagedMode });
                File.WriteAllText(StateFilePath(), json);
            }
            catch (Exception ex)
            {
                PortalDebug.LogWarning($"[LockdownScheduleEngine] failed to save persisted state: {ex.Message}");
            }
        }

        private static string FilePath()
        {
            string name = LockdownConfig.ScheduleFile?.Value ?? "lockdown_schedule.json";
            return Path.Combine(BepInEx.Paths.ConfigPath, name);
        }

        private static void TryReloadFile()
        {
            try
            {
                string path = FilePath();
                if (!File.Exists(path))
                {
                    return;
                }
                DateTime stamp = File.GetLastWriteTimeUtc(path);
                if (stamp == _fileStamp)
                {
                    return;
                }
                var parsed = JsonConvert.DeserializeObject<LockdownScheduleFile>(File.ReadAllText(path));
                if (parsed?.Windows == null)
                {
                    return;
                }
                _windows = parsed.Windows;
                _fileStamp = stamp;
                _warned.Clear();
                PortalDebug.LogAlways($"[LockdownScheduleEngine] loaded {_windows.Count} window(s) from '{path}'.");
            }
            catch (Exception ex)
            {
                PortalDebug.LogError($"[LockdownScheduleEngine] failed to load schedule file: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static void Reconcile()
        {
            DateTime nowUtc = DateTime.UtcNow;
            LockdownScheduleWindow strictest = null;
            int strictestRank = -1;
            for (int i = 0; i < _windows.Count; i++)
            {
                LockdownScheduleWindow w = _windows[i];
                if (!IsActiveNow(w, nowUtc))
                {
                    AnnouncePreWarnIfDue(w, i, nowUtc);
                    continue;
                }
                int rank = ModeRank(w.Mode);
                if (rank > strictestRank)
                {
                    strictestRank = rank;
                    strictest = w;
                }
            }

            bool desiredEngaged = strictest != null;
            string desiredMode = strictest?.Mode ?? "";

            if (desiredEngaged == _engaged && desiredMode == _engagedMode)
            {
                return; // Already in the desired state - the common case every tick.
            }

            if (desiredEngaged && !_engaged)
            {
                Engage(strictest);
            }
            else if (!desiredEngaged && _engaged)
            {
                Disengage();
            }
            else
            {
                // Mode changed while still engaged (e.g. Disconnect window ended, TagQuarantine window
                // still running) - lift the old mode's effect and apply the new one.
                Disengage();
                Engage(strictest);
            }
        }

        private static int ModeRank(string mode) => mode switch
        {
            "Disconnect" => 3,
            "TagQuarantine" => 2,
            "KeyOnly" => 1,
            _ => 0
        };

        private static void Engage(LockdownScheduleWindow w)
        {
            bool resuming = _engaged && _engagedMode == w.Mode; // should not happen given the guard above, kept defensive
            _engaged = true;
            _engagedMode = w.Mode;
            SaveState();

            string label = string.IsNullOrEmpty(w.Label) ? "scheduled maintenance window" : w.Label;
            switch (w.Mode)
            {
                case "KeyOnly":
                    LockdownGlobalKeyEngine.SetNoPortals(true);
                    break;
                case "TagQuarantine":
                    LockdownQuarantineTagEngine.EngageAllManaged(Reason);
                    break;
                case "Disconnect":
                default:
                    LockdownForceDisconnectEngine.EngageScope(PortalCensus.Latest, Reason);
                    break;
            }

            if (!resuming)
            {
                LockdownAnnouncementEngine.BroadcastMessage($"Portal network is now closed ({label}).", center: true);
                PortalDebug.LogAlways($"[LockdownScheduleEngine] engaged window '{label}' mode={w.Mode}.");
            }
        }

        private static void Disengage()
        {
            string priorMode = _engagedMode;
            _engaged = false;
            _engagedMode = "";
            SaveState();

            switch (priorMode)
            {
                case "KeyOnly":
                    LockdownGlobalKeyEngine.SetNoPortals(false);
                    break;
                case "TagQuarantine":
                    LockdownQuarantineTagEngine.DisengageAllManaged(Reason);
                    break;
                case "Disconnect":
                default:
                    LockdownForceDisconnectEngine.DisengageScope(Reason);
                    break;
            }

            LockdownAnnouncementEngine.BroadcastMessage("Portal network is open.", center: true);
            PortalDebug.LogAlways("[LockdownScheduleEngine] window ended - lockdown lifted.");
        }

        /// <summary>Simplified next-occurrence search - checks today and tomorrow's start time only (covers the overwhelmingly common "nightly window" case); a schedule with gaps larger than ~48h between windows won't get an early pre-warn, only the instant-engage transition still fires correctly regardless.</summary>
        private static void AnnouncePreWarnIfDue(LockdownScheduleWindow w, int index, DateTime nowUtc)
        {
            double secondsUntilStart = SecondsUntilNextStart(w, nowUtc);
            if (secondsUntilStart < 0 || secondsUntilStart > 3600)
            {
                return;
            }
            string label = string.IsNullOrEmpty(w.Label) ? "the portal network" : w.Label;
            CheckThreshold(index, "1", secondsUntilStart, LockdownConfig.AnnouncePreWarnSeconds1?.Value ?? 300f, $"{label} closes in {FormatMinutes(LockdownConfig.AnnouncePreWarnSeconds1?.Value ?? 300f)}.", center: false);
            CheckThreshold(index, "2", secondsUntilStart, LockdownConfig.AnnouncePreWarnSeconds2?.Value ?? 60f, $"{label} closes in {FormatMinutes(LockdownConfig.AnnouncePreWarnSeconds2?.Value ?? 60f)}.", center: true);
            CheckThreshold(index, "3", secondsUntilStart, LockdownConfig.AnnouncePreWarnSeconds3?.Value ?? 10f, $"{label} closes in {(LockdownConfig.AnnouncePreWarnSeconds3?.Value ?? 10f):F0} seconds!", center: true);
        }

        private static string FormatMinutes(float seconds) => seconds >= 60f ? $"{seconds / 60f:F0} minute(s)" : $"{seconds:F0} second(s)";

        private static void CheckThreshold(int windowIndex, string thresholdKey, double secondsUntilStart, float threshold, string text, bool center)
        {
            string key = $"{windowIndex}:{thresholdKey}";
            if (secondsUntilStart <= threshold)
            {
                if (_warned.Add(key))
                {
                    LockdownAnnouncementEngine.BroadcastMessage(text, center);
                }
            }
            else
            {
                _warned.Remove(key); // Still far out - clear so the NEXT time we cross this threshold it fires again.
            }
        }

        private static double SecondsUntilNextStart(LockdownScheduleWindow w, DateTime nowUtc)
        {
            if (!TimeSpan.TryParse(w.StartUtc, out TimeSpan start))
            {
                return -1;
            }
            for (int dayOffset = 0; dayOffset <= 2; dayOffset++)
            {
                DateTime candidateDay = nowUtc.Date.AddDays(dayOffset);
                if (w.Days != null && w.Days.Count > 0 && !DayMatches(w.Days, candidateDay.DayOfWeek))
                {
                    continue;
                }
                DateTime candidate = candidateDay + start;
                double delta = (candidate - nowUtc).TotalSeconds;
                if (delta >= 0)
                {
                    return delta;
                }
            }
            return -1;
        }

        private static bool DayMatches(List<string> days, DayOfWeek day)
        {
            foreach (string d in days)
            {
                if (Enum.TryParse(d, true, out DayOfWeek parsed) && parsed == day)
                {
                    return true;
                }
            }
            return false;
        }

        private static bool IsActiveNow(LockdownScheduleWindow w, DateTime nowUtc)
        {
            if (!TimeSpan.TryParse(w.StartUtc, out TimeSpan start) || !TimeSpan.TryParse(w.EndUtc, out TimeSpan end))
            {
                return false;
            }
            TimeSpan now = nowUtc.TimeOfDay;
            bool inTimeRange = start <= end ? (now >= start && now < end) : (now >= start || now < end); // overnight wrap, e.g. 22:00-06:00

            if (!inTimeRange)
            {
                return false;
            }
            if (w.Days == null || w.Days.Count == 0)
            {
                return true;
            }
            // For an overnight window that wrapped past midnight, "today" for day-matching purposes is
            // the day the window STARTED on, i.e. yesterday if we're currently past midnight but before End.
            DayOfWeek effectiveDay = (start > end && now < start) ? nowUtc.AddDays(-1).DayOfWeek : nowUtc.DayOfWeek;
            return DayMatches(w.Days, effectiveDay);
        }
    }
}
