using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using UnityEngine;
using TortalPortalLite.Core;

namespace TortalPortalLite.Subsystems.Topology
{
    /// <summary>One admin-declared destination route: a source portal (by position - see PortalCensus.TryGetByPosition's own rationale) wanting a computed destination of some Kind with free-form Params.</summary>
    public sealed class TargetedRoute
    {
        public float X;
        public float Y;
        public float Z;
        public string Kind = "";
        public string Label = "";
        public Dictionary<string, string> Params = new Dictionary<string, string>();

        [JsonIgnore]
        public Vector3 SourcePosition => new Vector3(X, Y, Z);

        public string Get(string key, string fallback = "") => Params != null && Params.TryGetValue(key, out string v) ? v : fallback;
        public float GetFloat(string key, float fallback) => Params != null && Params.TryGetValue(key, out string v) && float.TryParse(v, out float f) ? f : fallback;
        public int GetInt(string key, int fallback) => Params != null && Params.TryGetValue(key, out string v) && int.TryParse(v, out int i) ? i : fallback;
        public bool GetBool(string key, bool fallback) => Params != null && Params.TryGetValue(key, out string v) && bool.TryParse(v, out bool b) ? b : fallback;
    }

    public sealed class TargetedRoutesFile
    {
        public int SchemaVersion = 1;
        public List<TargetedRoute> Routes = new List<TargetedRoute>();
    }

    /// <summary>
    /// The operator-facing declaration surface for every STATIC/admin-declared targeted destination
    /// (#180 Admin Coordinates, #181 World Spawn, #182 Boss Altars, #183 Traders, #184 Dungeons, #185
    /// Biomes, #186 Named Locations): "this source portal, identified by position, wants a destination
    /// of kind K with these params". Modelled directly on Foundations/NetworkModel.cs's own
    /// hot-reloaded-JSON-by-position pattern (same reasoning: ZDOIDs are not stable across a world
    /// reload, and a RecordId does not exist until the mod has already seen the portal once, so a
    /// position is the only thing an admin can actually name).
    ///
    /// Dynamic/per-player options (#187 Bed, #189 Tombstone, #190 Ship, #191 Nearest Player, #192
    /// Player Anchor, #193 Map Ping, #194 Builder Trace) are claimed by emote/action instead and do NOT
    /// go through this file - seeded routes are for things only an admin can decide.
    /// </summary>
    public static class TargetedRouteStore
    {
        private const float PollInterval = 5f;
        private static float _timer;
        private static DateTime _fileStamp = DateTime.MinValue;
        private static List<TargetedRoute> _routes = new List<TargetedRoute>();

        public static IReadOnlyList<TargetedRoute> Routes => _routes;

        private static string FilePath =>
            Path.Combine(Path.GetDirectoryName(TargetedConfig.RoutesFile?.ConfigFile?.ConfigFilePath ?? "") ?? ".",
                TargetedConfig.RoutesFile?.Value ?? "targeted_routes.json");

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
                    return;
                }
                DateTime stamp = File.GetLastWriteTimeUtc(path);
                if (stamp == _fileStamp)
                {
                    return;
                }

                string json = File.ReadAllText(path);
                var parsed = JsonConvert.DeserializeObject<TargetedRoutesFile>(json);
                if (parsed?.Routes == null)
                {
                    PortalDebug.LogWarning($"[TargetedRouteStore] '{path}' parsed but had no 'Routes' array - ignoring.");
                    return;
                }

                _routes = parsed.Routes;
                _fileStamp = stamp;
                PortalDebug.LogAlways($"[TargetedRouteStore] loaded {_routes.Count} route(s) from '{path}'.");
            }
            catch (Exception ex)
            {
                PortalDebug.LogError($"[TargetedRouteStore] failed to load targeted_routes.json: {ex.GetType().Name}: {ex.Message}");
            }
        }

        public static IEnumerable<TargetedRoute> RoutesOfKind(string kind)
        {
            foreach (TargetedRoute r in _routes)
            {
                if (string.Equals(r.Kind, kind, StringComparison.OrdinalIgnoreCase))
                {
                    yield return r;
                }
            }
        }
    }
}
