using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using UnityEngine;
using TortalPortalLite.Core;
using TortalPortalLite.Core.Data;
using TortalPortalLite.Subsystems.Foundations;

namespace TortalPortalLite.Subsystems.PlayerInterface
{
    public sealed class UxPlateDef
    {
        public float X;
        public float Y;
        public float Z;
        public float Radius = 2f;

        /// <summary>Address-book alias to dial the nearest portal to. The plate itself is pure decoration - the mod needs only this coordinate list, per the catalog's own framing.</summary>
        public string Destination = "";
    }

    public sealed class UxApproachVectorDef
    {
        public float X;
        public float Y;
        public float Z;

        /// <summary>Clockwise from north, evenly split - entry index i means "approached from sector i". Opt-in per portal (declared here), never applied to a portal an admin didn't list, since silently redirecting a portal based on which way someone walked in would be a bad surprise otherwise.</summary>
        public List<string> SectorDestinations = new List<string>();
    }

    public sealed class UxPlatesFileModel
    {
        public int SchemaVersion = 1;
        public List<UxPlateDef> Plates = new List<UxPlateDef>();
        public List<UxApproachVectorDef> ApproachVectors = new List<UxApproachVectorDef>();
    }

    /// <summary>
    /// #164 Positional input - plates, walk-sequences, approach vector. Position is the cheapest and
    /// freshest signal the server has: `ZSyncTransform.OwnerSync` writes the character ZDO's position
    /// every FixedUpdate on the owning client, and delivery to the server is distance-independent - so
    /// `ConnectedCharacters.All()` positions are fresh to ~50-100ms regardless of how far a player is
    /// from the world-loading reference point this mod's whole existence works around.
    ///
    /// Two of the catalog's four primitives are implemented: PLATE (a declared coordinate+radius; dwell
    /// past a threshold dials the nearest portal to a declared destination) and APPROACH VECTOR (the
    /// bearing a player enters a declared portal's radius from, quantised into N sectors, each mapped to
    /// a destination). SEQUENCE (an ordered chain of plates) and LOOK DIRECTION disambiguation are not
    /// implemented - plates alone already cover the catalog's own primary playerExperience ("stand on a
    /// tile"), and adding ordered-sequence state on top is deferred rather than half-built.
    /// </summary>
    public static class UxPositionalInputEngine
    {
        private static List<UxPlateDef> _plates = new List<UxPlateDef>();
        private static List<UxApproachVectorDef> _vectors = new List<UxApproachVectorDef>();
        private static DateTime _fileStamp = DateTime.MinValue;

        private static readonly Dictionary<(long playerId, int plateIndex), float> _dwellSeconds = new Dictionary<(long, int), float>();
        private static readonly Dictionary<(long playerId, int vectorIndex), bool> _wasInsideVector = new Dictionary<(long, int), bool>();
        private static float _timer;

        public static void OnUpdate(float dt)
        {
            if (UxConfig.Enabled?.Value == false || UxConfig.PositionalInputEnabled?.Value == false)
            {
                return;
            }
            TryReload();

            _timer += dt;
            float interval = UxConfig.PositionalPollSeconds?.Value ?? 0.15f;
            if (_timer < interval)
            {
                return;
            }
            float sampleDt = _timer;
            _timer = 0f;
            if (_plates.Count == 0 && _vectors.Count == 0)
            {
                return;
            }
            Poll(sampleDt);
        }

        private static void TryReload()
        {
            try
            {
                string path = UxFilePaths.Resolve(UxConfig.PlatesFile, "ux_plates.json");
                if (!File.Exists(path))
                {
                    return;
                }
                DateTime stamp = File.GetLastWriteTimeUtc(path);
                if (stamp == _fileStamp)
                {
                    return;
                }
                UxPlatesFileModel parsed = JsonConvert.DeserializeObject<UxPlatesFileModel>(File.ReadAllText(path));
                if (parsed == null)
                {
                    return;
                }
                _plates = parsed.Plates ?? new List<UxPlateDef>();
                _vectors = parsed.ApproachVectors ?? new List<UxApproachVectorDef>();
                _fileStamp = stamp;
                PortalDebug.LogAlways($"[UxPositionalInputEngine] loaded {_plates.Count} plate(s), {_vectors.Count} approach-vector portal(s) from '{path}'.");
            }
            catch (Exception ex)
            {
                PortalDebug.LogError($"[UxPositionalInputEngine] failed to load '{UxConfig.PlatesFile?.Value}': {ex.Message}");
            }
        }

        private static void Poll(float sampleDt)
        {
            float dwellNeeded = UxConfig.PlateDwellSeconds?.Value ?? 1.5f;
            float approachRadius = UxConfig.PortalProximityRadius?.Value ?? 6f;

            foreach (ConnectedCharacter who in ConnectedCharacters.All())
            {
                for (int i = 0; i < _plates.Count; i++)
                {
                    UxPlateDef p = _plates[i];
                    var key = (who.PlayerId, i);
                    bool inside = HorizontalDistSqr(who.Position, p.X, p.Z) <= p.Radius * p.Radius;
                    if (!inside)
                    {
                        _dwellSeconds.Remove(key);
                        continue;
                    }
                    float before = _dwellSeconds.TryGetValue(key, out float existing) ? existing : 0f;
                    float after = before + sampleDt;
                    _dwellSeconds[key] = after;
                    if (before < dwellNeeded && after >= dwellNeeded)
                    {
                        FirePlate(who, p);
                    }
                }

                for (int i = 0; i < _vectors.Count; i++)
                {
                    UxApproachVectorDef v = _vectors[i];
                    var key = (who.PlayerId, i);
                    bool inside = HorizontalDistSqr(who.Position, v.X, v.Z) <= approachRadius * approachRadius;
                    bool was = _wasInsideVector.TryGetValue(key, out bool w) && w;
                    _wasInsideVector[key] = inside;
                    if (inside && !was)
                    {
                        FireApproachVector(who, v);
                    }
                }
            }
        }

        private static void FirePlate(ConnectedCharacter who, UxPlateDef p)
        {
            if (string.IsNullOrEmpty(p.Destination) || ZDOMan.instance == null)
            {
                return;
            }
            if (!UxAddressBook.TryNearestAnyPortal(new Vector3(p.X, p.Y, p.Z), p.Radius + 3f, out PortalRecord source))
            {
                return;
            }
            if (!UxAddressBook.TryGet(p.Destination, out PortalRecord dest))
            {
                return;
            }
            ZDO sourceZdo = ZDOMan.instance.GetZDO(source.Uid);
            if (sourceZdo == null || !sourceZdo.IsValid())
            {
                return;
            }
            UxDialAction.TryDialToRecord(sourceZdo, dest, who, out _);
        }

        private static void FireApproachVector(ConnectedCharacter who, UxApproachVectorDef v)
        {
            if (v.SectorDestinations.Count == 0 || ZDOMan.instance == null)
            {
                return;
            }
            Vector3 portalPos = new Vector3(v.X, v.Y, v.Z);
            if (!UxAddressBook.TryNearestAnyPortal(portalPos, 3f, out PortalRecord source))
            {
                return;
            }

            Vector3 d = who.Position - portalPos;
            float bearing = (Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg + 360f) % 360f;
            int sectorCount = v.SectorDestinations.Count;
            int sector = Mathf.FloorToInt(bearing / (360f / sectorCount)) % sectorCount;
            string destName = v.SectorDestinations[sector];
            if (string.IsNullOrEmpty(destName) || !UxAddressBook.TryGet(destName, out PortalRecord dest))
            {
                return;
            }
            ZDO sourceZdo = ZDOMan.instance.GetZDO(source.Uid);
            if (sourceZdo == null || !sourceZdo.IsValid())
            {
                return;
            }
            UxDialAction.TryDialToRecord(sourceZdo, dest, who, out _);
        }

        private static float HorizontalDistSqr(Vector3 pos, float x, float z)
        {
            float dx = pos.x - x, dz = pos.z - z;
            return dx * dx + dz * dz;
        }
    }
}
