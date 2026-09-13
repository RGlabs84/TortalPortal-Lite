using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;
using TortalPortalLite.Core;

namespace TortalPortalLite.Subsystems.WorldOps
{
    /// <summary>
    /// Shared hot-reloaded declaration file for the whole `economy` domain - one admin-facing
    /// "economy.json" with one top-level array per mechanism ("tolls", "turnstiles", "chargeCells",
    /// "cooldowns", "leases", "oneWayRoutes", "auctions", "eventChains", "progressionRoutes",
    /// "reservedNames", ...), polled and parsed once here rather than each engine opening its own file.
    /// Mirrors Subsystems/Topology/RoutingManagedPortalRegistry.cs's own pattern almost exactly (which
    /// itself mirrors Foundations' NetworkModel.cs) - deliberately its own file/type rather than reusing
    /// either of those: per RoutingManagedPortalRegistry's own doc comment, each domain's declaration
    /// schema should never break if a sibling domain's schema changes shape later, which matters even
    /// more this wave since three other agents are editing sibling domains concurrently.
    ///
    /// Each engine owns its own DTO class (co-located in its own file, not here) and calls
    /// <see cref="Section{T}"/> with its own section key and a cached <see cref="Version"/> check, so a
    /// malformed DTO in one engine's section can never take down another engine's declarations.
    /// </summary>
    public static class EconomyRegistry
    {
        private const float PollInterval = 5f;
        private static float _timer;
        private static DateTime _fileStamp = DateTime.MinValue;
        private static JObject? _root;

        /// <summary>Bumped on every successful reparse (including "file missing -> empty root"). Engines cache their own parsed section against this to avoid re-parsing every tick.</summary>
        public static int Version { get; private set; }

        private static string FilePath =>
            Path.Combine(Path.GetDirectoryName(EconomyConfig.RegistryFile?.ConfigFile?.ConfigFilePath ?? "") ?? ".",
                EconomyConfig.RegistryFile?.Value ?? "economy.json");

        public static void OnUpdate(float dt)
        {
            _timer += dt;
            if (_timer < PollInterval)
            {
                return;
            }
            _timer = 0f;
            TryReload();
        }

        private static void TryReload()
        {
            try
            {
                string path = FilePath;
                if (!File.Exists(path))
                {
                    if (_root != null)
                    {
                        _root = null;
                        Version++;
                    }
                    return;
                }

                DateTime stamp = File.GetLastWriteTimeUtc(path);
                if (stamp == _fileStamp)
                {
                    return;
                }

                string json = File.ReadAllText(path);
                _root = JObject.Parse(json);
                _fileStamp = stamp;
                Version++;
                PortalDebug.LogAlways($"[EconomyRegistry] reloaded '{path}' (version {Version}).");
            }
            catch (Exception ex)
            {
                PortalDebug.LogError($"[EconomyRegistry] failed to load economy.json: {ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>Decodes one top-level array property into a list of <typeparamref name="T"/>. Never throws - a malformed section logs and yields an empty list.</summary>
        public static List<T> Section<T>(string key)
        {
            var result = new List<T>();
            JToken? token = _root?[key];
            if (token == null)
            {
                return result;
            }
            try
            {
                foreach (JToken item in token)
                {
                    try
                    {
                        T? parsed = item.ToObject<T>();
                        if (parsed != null)
                        {
                            result.Add(parsed);
                        }
                    }
                    catch (Exception itemEx)
                    {
                        PortalDebug.LogWarning($"[EconomyRegistry] section '{key}': one entry failed to parse and was skipped: {itemEx.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                PortalDebug.LogWarning($"[EconomyRegistry] section '{key}' is not an array - ignoring: {ex.Message}");
            }
            return result;
        }
    }

    /// <summary>A serializable world position - own type per RoutingManagedPortalRegistry's own precedent (economy.json's schema should never break if routing.json's/networks.json's shape changes).</summary>
    public sealed class EconomyPosition
    {
        public float X;
        public float Y;
        public float Z;
        public UnityEngine.Vector3 ToVector3() => new UnityEngine.Vector3(X, Y, Z);
    }
}
