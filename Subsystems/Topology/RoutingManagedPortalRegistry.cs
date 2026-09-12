using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;
using TortalPortalLite.Core;

namespace TortalPortalLite.Subsystems.Topology
{
    /// <summary>
    /// Shared hot-reloaded declaration file for the whole `routing` domain - one admin-facing
    /// "routing.json" with one top-level object per mechanism ("hubs", "rings", "schedules", ...),
    /// polled and parsed once here rather than each of the ~20 engines below opening/parsing its own
    /// file. Mirrors NetworkModel.cs's own reasoning almost exactly (catalog #23's own text: "Membership
    /// is decided by a mod-side registry keyed by rounded world position ... NEVER by ZDOID, which
    /// ZDO.Load renumbers on every world load").
    ///
    /// Each engine owns its OWN DTO class (co-located in its own file, not here) and calls
    /// <see cref="Section{T}"/> with its own section key and a cached <see cref="Version"/> check, so a
    /// malformed DTO in one engine's section can never take down another engine's parsing - each
    /// section is decoded independently and a bad one just logs and yields an empty list.
    /// </summary>
    public static class RoutingManagedPortalRegistry
    {
        private const float PollInterval = 5f;
        private static float _timer;
        private static DateTime _fileStamp = DateTime.MinValue;
        private static JObject? _root;

        /// <summary>Bumped on every successful reparse (including "file missing -> empty root"). Engines cache their own parsed section against this to avoid re-parsing every tick.</summary>
        public static int Version { get; private set; }

        private static string FilePath =>
            Path.Combine(Path.GetDirectoryName(RoutingConfig.RegistryFile?.ConfigFile?.ConfigFilePath ?? "") ?? ".",
                RoutingConfig.RegistryFile?.Value ?? "routing.json");

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
                        // File removed mid-session: clear declarations rather than keep stale state forever.
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
                PortalDebug.LogAlways($"[RoutingManagedPortalRegistry] reloaded '{path}' (version {Version}).");
            }
            catch (Exception ex)
            {
                PortalDebug.LogError($"[RoutingManagedPortalRegistry] failed to load routing.json: {ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>Decodes one top-level array property into a list of <typeparamref name="T"/>. Never throws - a malformed section logs and yields an empty list so one bad entry cannot break every other engine's declarations.</summary>
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
                        PortalDebug.LogWarning($"[RoutingManagedPortalRegistry] section '{key}': one entry failed to parse and was skipped: {itemEx.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                PortalDebug.LogWarning($"[RoutingManagedPortalRegistry] section '{key}' is not an array - ignoring: {ex.Message}");
            }
            return result;
        }
    }

    /// <summary>
    /// A serializable world position, identical in shape to Foundations' NetworkMemberPosition, kept as
    /// its own type in this domain rather than reusing that one directly - the routing.json schema
    /// belongs entirely to this domain and should never break if Foundations' networks.json schema
    /// changes shape later.
    /// </summary>
    public sealed class RoutingPosition
    {
        public float X;
        public float Y;
        public float Z;
        public UnityEngine.Vector3 ToVector3() => new UnityEngine.Vector3(X, Y, Z);
    }
}
