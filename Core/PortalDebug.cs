using BepInEx.Logging;

namespace TortalPortalLite.Core
{
    public static class PortalDebug
    {
        private static ManualLogSource? _logSource;

        public static void Init(ManualLogSource logSource)
        {
            _logSource = logSource;
        }

        public static void LogInfo(string message)
        {
            if (GlobalConfig.VerboseLogging != null && !GlobalConfig.VerboseLogging.Value) return;
            _logSource?.LogInfo($"[TortalPortalLite] {message}");
        }

        public static void LogWarning(string message)
        {
            _logSource?.LogWarning($"[TortalPortalLite] {message}");
        }

        public static void LogError(string message)
        {
            _logSource?.LogError($"[TortalPortalLite] {message}");
        }

        public static void LogAlways(string message)
        {
            _logSource?.LogMessage($"[TortalPortalLite] {message}");
        }
    }
}
