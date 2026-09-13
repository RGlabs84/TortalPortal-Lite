using System;
using System.IO;
using System.Reflection;
using System.Text;
using BepInEx;
using TortalPortalLite.Core;

namespace TortalPortalLite.Subsystems.Foundations
{
    /// <summary>
    /// #67 File-queue command channel - a watched text file an operator appends command lines to from
    /// ssh/cron; polled by mtime (exactly Wonderland's WonderlandPlugin.PollConfigFile pattern, Plugin.cs:97,
    /// which this mod's own Plugin.cs already reuses for the .cfg itself - compare stamp == cached, not
    /// greater-than, so a rename-based editor write that jumps mtime backwards is still picked up).
    ///
    /// On a detected change: read every line, run each through the SAME verb table CommandEngine.Dispatch
    /// already exposes (reflection into its private Dispatch(string,string[]) - the file-queue channel is
    /// deliberately a second TRANSPORT for the one grammar CommandEngine owns, not a second grammar),
    /// append "&lt;timestamp&gt; &lt;command&gt; -&gt; &lt;result&gt;" to the sibling .cmd.out file, then truncate the
    /// input file. Everything happens on the main thread inside OnUpdate (ZDOMan has no locking anywhere -
    /// vanilla's only background thread, ZNet.SaveWorldThread :80583, deliberately operates on a
    /// PrepareSave clone built on the main thread, :76197-76205 - so this channel never touches ZDOMan
    /// from anywhere but the main-thread tick).
    ///
    /// Security model: the file lives inside BepInEx's config directory, so filesystem permission on the
    /// server box IS the authorization - refuses to poll a resolved path outside BepInEx.Paths.ConfigPath.
    /// </summary>
    public static class OpsOutputFileQueueEngine
    {
        private static float _timer;
        private static DateTime _lastStamp = DateTime.MinValue;
        private static bool _pathValidated;
        private static bool _pathOk;
        private static string _cmdPath = "";
        private static string _outPath = "";

        public static void Initialize()
        {
            try
            {
                string fileName = OpsOutputConfig.FileQueueFileName?.Value ?? "TortalPortalLite.cmd";
                // Never accept a path component that could escape the config directory - a bare file
                // name only, combined with the plugin's own config dir, matches #67's own security
                // convention ("refuse a command file outside BepInEx.Paths.ConfigPath").
                fileName = Path.GetFileName(fileName);
                string candidate = Path.Combine(OpsOutputConfig.PluginConfigDir, fileName);
                string fullConfigPath = Path.GetFullPath(Paths.ConfigPath);
                string fullCandidate = Path.GetFullPath(candidate);

                if (!fullCandidate.StartsWith(fullConfigPath, StringComparison.OrdinalIgnoreCase))
                {
                    PortalDebug.LogError($"[OpsOutputFileQueueEngine] resolved command file path '{fullCandidate}' is outside BepInEx.Paths.ConfigPath ('{fullConfigPath}') - refusing to poll it.");
                    _pathOk = false;
                    _pathValidated = true;
                    return;
                }

                _cmdPath = fullCandidate;
                _outPath = fullCandidate + ".out";
                _pathOk = true;
                _pathValidated = true;
                PortalDebug.LogAlways($"[OpsOutputFileQueueEngine] watching '{_cmdPath}' for queued command lines (results -> '{_outPath}').");
            }
            catch (Exception ex)
            {
                _pathOk = false;
                _pathValidated = true;
                PortalDebug.LogError($"[OpsOutputFileQueueEngine] initialization failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        public static void OnUpdate(float dt)
        {
            if (!_pathValidated)
            {
                Initialize();
            }
            if (!_pathOk || OpsOutputConfig.FileQueueEnabled?.Value == false)
            {
                return;
            }

            _timer += dt;
            float interval = OpsOutputConfig.FileQueuePollSeconds?.Value ?? 1f;
            if (_timer < interval)
            {
                return;
            }
            _timer = 0f;

            try
            {
                if (!File.Exists(_cmdPath))
                {
                    return;
                }
                DateTime stamp = File.GetLastWriteTimeUtc(_cmdPath);
                if (stamp == _lastStamp)
                {
                    return;
                }
                _lastStamp = stamp;

                string[] lines = File.ReadAllLines(_cmdPath);
                if (lines.Length == 0)
                {
                    return;
                }

                var results = new StringBuilder();
                int executed = 0;
                foreach (string rawLine in lines)
                {
                    string line = rawLine?.Trim() ?? "";
                    if (line.Length == 0 || line.StartsWith("#"))
                    {
                        continue;
                    }
                    string result = Execute(line);
                    results.Append(DateTime.UtcNow.ToString("o")).Append(' ').Append(line).Append(" -> ").Append(result).Append('\n');
                    executed++;
                }

                if (executed > 0)
                {
                    AppendOut(results.ToString());
                }

                // Truncate so an unrelated later mtime bump (another process touching the file, a
                // filesystem quirk) never re-executes the same lines - #67's own explicit failure mode.
                File.WriteAllText(_cmdPath, "");
                _lastStamp = File.GetLastWriteTimeUtc(_cmdPath);
            }
            catch (Exception ex)
            {
                PortalDebug.LogError($"[OpsOutputFileQueueEngine] poll failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static MethodInfo? _dispatchMethod;

        /// <summary>
        /// CommandEngine.Dispatch(string, string[]) is private - reflection into a private static method
        /// is already this mod's own established pattern for exactly this situation (CommandEngine's own
        /// NetworkReassertEngineForceTick does the same to force NetworkReassertEngine.Reassert()). This
        /// channel is deliberately a second TRANSPORT onto the SAME grammar, not a second grammar, so it
        /// must call the real Dispatch, not reimplement a parallel verb table that could drift from it.
        /// </summary>
        private static string Execute(string line)
        {
            try
            {
                string[] words = line.Split(' ');
                string verb = words[0].ToLowerInvariant();
                var args = new string[words.Length - 1];
                Array.Copy(words, 1, args, 0, args.Length);

                _dispatchMethod ??= typeof(CommandEngine).GetMethod("Dispatch", BindingFlags.NonPublic | BindingFlags.Static);
                if (_dispatchMethod == null)
                {
                    return "error: CommandEngine.Dispatch not found via reflection (signature may have changed).";
                }
                object? result = _dispatchMethod.Invoke(null, new object[] { verb, args });
                return result as string ?? "(no response)";
            }
            catch (Exception ex)
            {
                return $"error: {ex.GetType().Name}: {ex.Message}";
            }
        }

        private static void AppendOut(string text)
        {
            try
            {
                // Appending is not the torn-read hazard #67 warns about (that's about the COMMAND file
                // being replaced mid-execution) - a response log is expected to grow, so a plain append
                // (not atomic-swap) is correct here, matching the citation's own ".cmd.out" tail -f use case.
                File.AppendAllText(_outPath, text);
            }
            catch (Exception ex)
            {
                PortalDebug.LogWarning($"[OpsOutputFileQueueEngine] failed to append to '{_outPath}': {ex.Message}");
            }
        }
    }
}
