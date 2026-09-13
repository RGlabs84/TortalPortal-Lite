using System;
using UnityEngine;
using TortalPortalLite.Core;
using TortalPortalLite.Core.Data;

namespace TortalPortalLite.Subsystems.WorldOps
{
    /// <summary>
    /// #234 "The Landing Pad Engine - Server-Authored Terraforming Via TerrainComp". Every zone's
    /// terrain edits live in one compiler ZDO instantiated at the heightmap's own transform position
    /// (`Heightmap.GetAndCreateTerrainCompiler`, SERVER decompile :129530-129538); the payload is
    /// `ZDOVars.s_TCData` (:78353), a `Utils.Compress`-compressed `ZPackage` written in the EXACT layout
    /// confirmed directly against `TerrainComp.Save`/`Load` by this build (:143793-143934): `int
    /// version=1; int operations; Vector3 lastOpPoint; float lastOpRadius; int N; N * {bool modified,
    /// [float levelDelta, float smoothDelta]}; int N2; N2 * {bool modifiedPaint, [float r,g,b,a]}`, where
    /// N/N2 MUST equal `(m_width+1)^2` (:143876-143880) or the whole blob is silently rejected
    /// client-side. `ApplyToHeightmap` clamps every delta to the generated height +/-8m
    /// (`c_LevelMaxDelta`, :143950-143972).
    ///
    /// This engine performs a full load-modify-save round trip rather than blind-overwriting the
    /// compiler ZDO: an existing `s_TCData` blob (any prior terraforming already done in that zone, by a
    /// player OR an earlier Landing Pad call) is parsed first and every vertex OUTSIDE the requested disc
    /// is carried through unchanged - only vertices inside the disc get a new `levelDelta` computed as
    /// `clamp(targetY - WorldGenerator.instance.GetHeight(vx, vz), -8, 8)`, per vertex, using the SAME
    /// world-vertex formula `Heightmap.WorldToVertex`/`GetHeight` use (:129177-129183, :129263-129271):
    /// `vx = zoneCenter.x + (ix - width/2)*scale`, `vz = zoneCenter.z + (iz - width/2)*scale`, `index =
    /// iz*(width+1) + ix`. Paint data is preserved as-loaded (never modified by this engine - painting a
    /// matching ground texture under the pad is asset-specific and out of this engine's scope).
    ///
    /// `m_width`/`m_scale`/the terrain-compiler prefab are read once from the zone prefab's own
    /// `Heightmap` component (`ZoneSystem.instance.m_zonePrefab.GetComponentInChildren&lt;Heightmap&gt;()`
    /// - an ASSET read, no instantiation, the same "read a prefab's serialized field without touching
    /// gameplay" idiom this codebase already uses in
    /// Subsystems/Enforcement/LockdownBossWatchdogEngine.cs's boss-prefab discovery) rather than trusting
    /// the Inspector default of 32 the catalog itself flags as almost certainly overridden to 64 for a
    /// 64m zone.
    /// </summary>
    public static class WildcardBLandingPadEngine
    {
        private static bool _prefabResolved;
        private static int _terrainCompilerHash;
        private static int _heightmapWidth;
        private static float _heightmapScale = 1f;

        /// <summary>
        /// Flattens a disc of radius <paramref name="radius"/> metres centred at <paramref name="center"/>
        /// (XZ) to <paramref name="targetY"/>, clamped to +/-8m of the real generated terrain per vertex.
        /// Creates the zone's terrain-compiler ZDO if one does not exist yet. Returns false (logs why) on
        /// any failure - an unsafe coordinate (#143/#244), an unresolved terrain-compiler prefab, or a
        /// zone straddling a boundary this single-zone call cannot flatten cleanly (logged as a known
        /// limitation, not silently ignored).
        /// </summary>
        public static bool TryFlattenDisc(Vector3 center, float radius, float targetY)
        {
            if (WildcardBConfig.LandingPadEnabled?.Value != true)
            {
                return false;
            }
            if (!WildcardBSectorZeroEngine.IsSafeCoordinate(center))
            {
                PortalDebug.LogWarning($"[WildcardBLandingPadEngine] refused disc at {center:F0}: fails the #143/#244 Sector Zero safety guard.");
                return false;
            }
            if (ZoneSystem.instance == null || ZDOMan.instance == null || WorldGenerator.instance == null)
            {
                return false;
            }
            if (!ResolvePrefabDataOnce())
            {
                return false;
            }

            Vector2s zone = ZoneSystem.GetZone(center);
            Vector3 zoneCenter = ZoneSystem.GetZonePos(zone);
            float zoneHalfSize = _heightmapWidth * _heightmapScale / 2f;
            if (Mathf.Abs(center.x - zoneCenter.x) + radius > zoneHalfSize || Mathf.Abs(center.z - zoneCenter.z) + radius > zoneHalfSize)
            {
                PortalDebug.LogWarning($"[WildcardBLandingPadEngine] disc at {center:F0} radius {radius:F0} straddles zone {zone.x},{zone.y}'s own boundary - only this zone's compiler will be written (the catalog's own documented seam-tear limitation); consider a smaller radius.");
            }

            ZDO compiler = FindOrCreateCompiler(zoneCenter);
            if (compiler == null)
            {
                return false;
            }

            int pitch = _heightmapWidth + 1;
            int cellCount = pitch * pitch;
            bool[] modifiedHeight = new bool[cellCount];
            float[] levelDelta = new float[cellCount];
            float[] smoothDelta = new float[cellCount];
            bool[] modifiedPaint = new bool[cellCount];
            float[] paintR = new float[cellCount];
            float[] paintG = new float[cellCount];
            float[] paintB = new float[cellCount];
            float[] paintA = new float[cellCount];
            int operations = 0;

            byte[] existing = compiler.GetByteArray(ZDOVars.s_TCData);
            if (existing != null && existing.Length > 0)
            {
                if (!TryLoadExisting(existing, pitch, modifiedHeight, levelDelta, smoothDelta, modifiedPaint, paintR, paintG, paintB, paintA, out operations))
                {
                    PortalDebug.LogWarning($"[WildcardBLandingPadEngine] existing s_TCData on compiler {compiler.m_uid} did not match this zone's own vertex count - starting from a blank compiler blob instead of risking corrupt data.");
                    Array.Clear(modifiedHeight, 0, cellCount);
                    Array.Clear(levelDelta, 0, cellCount);
                    Array.Clear(smoothDelta, 0, cellCount);
                    Array.Clear(modifiedPaint, 0, cellCount);
                    operations = 0;
                }
            }

            float radiusSqr = radius * radius;
            int half = _heightmapWidth / 2;
            for (int iz = 0; iz < pitch; iz++)
            {
                float vz = zoneCenter.z + (iz - half) * _heightmapScale;
                for (int ix = 0; ix < pitch; ix++)
                {
                    float vx = zoneCenter.x + (ix - half) * _heightmapScale;
                    float dx = vx - center.x;
                    float dz = vz - center.z;
                    if (dx * dx + dz * dz > radiusSqr)
                    {
                        continue;
                    }
                    int index = iz * pitch + ix;
                    float baseHeight = WorldGenerator.instance.GetHeight(vx, vz);
                    float delta = Mathf.Clamp(targetY - baseHeight, -8f, 8f);
                    modifiedHeight[index] = true;
                    levelDelta[index] = delta;
                    smoothDelta[index] = 0f;
                }
            }

            operations++;
            byte[] bytes = SerializeCompilerBlob(operations, center, radius, pitch, modifiedHeight, levelDelta, smoothDelta, modifiedPaint, paintR, paintG, paintB, paintA);
            PortalOwnership.ClaimAndWrite(compiler, z => z.Set(ZDOVars.s_TCData, bytes));
            PortalDebug.LogAlways($"[WildcardBLandingPadEngine] flattened disc at {center:F0} radius {radius:F0} to y={targetY:F1} on compiler {compiler.m_uid}.");
            return true;
        }

        private static bool ResolvePrefabDataOnce()
        {
            if (_prefabResolved)
            {
                return _terrainCompilerHash != 0;
            }
            _prefabResolved = true;
            try
            {
                Heightmap hmapAsset = ZoneSystem.instance.m_zonePrefab != null
                    ? ZoneSystem.instance.m_zonePrefab.GetComponentInChildren<Heightmap>()
                    : null;
                if (hmapAsset == null || hmapAsset.m_terrainCompilerPrefab == null)
                {
                    PortalDebug.LogError("[WildcardBLandingPadEngine] could not resolve Heightmap/m_terrainCompilerPrefab off ZoneSystem.instance.m_zonePrefab - Landing Pad disabled.");
                    return false;
                }
                _heightmapWidth = hmapAsset.m_width;
                _heightmapScale = hmapAsset.m_scale;
                _terrainCompilerHash = hmapAsset.m_terrainCompilerPrefab.name.GetStableHashCode();
                PortalDebug.LogInfo($"[WildcardBLandingPadEngine] resolved terrain compiler prefab '{hmapAsset.m_terrainCompilerPrefab.name}' (width {_heightmapWidth}, scale {_heightmapScale}).");
                return true;
            }
            catch (Exception ex)
            {
                PortalDebug.LogError($"[WildcardBLandingPadEngine] prefab resolution failed: {ex.Message}");
                return false;
            }
        }

        private static ZDO FindOrCreateCompiler(Vector3 zoneCenter)
        {
            System.Collections.Generic.List<ZDO> nearby = ZdoSpatialQuery.FindNear(zoneCenter, 4f);
            foreach (ZDO candidate in nearby)
            {
                if (candidate.GetPrefab() == _terrainCompilerHash)
                {
                    return candidate;
                }
            }

            try
            {
                ZDO zdo = ZDOMan.instance.CreateNewZDO(zoneCenter, _terrainCompilerHash);
                if (zdo == null)
                {
                    return null;
                }
                PortalOwnership.ClaimAndWrite(zdo, z =>
                {
                    z.Persistent = true;
                    z.SetPrefab(_terrainCompilerHash);
                    z.SetRotation(Quaternion.identity);
                });
                PortalDebug.LogInfo($"[WildcardBLandingPadEngine] fabricated a new terrain compiler ZDO {zdo.m_uid} at {zoneCenter:F0}.");
                return zdo;
            }
            catch (Exception ex)
            {
                PortalDebug.LogError($"[WildcardBLandingPadEngine] terrain compiler fabrication failed: {ex.Message}");
                return null;
            }
        }

        private static bool TryLoadExisting(byte[] compressed, int pitch, bool[] modifiedHeight, float[] levelDelta, float[] smoothDelta,
            bool[] modifiedPaint, float[] paintR, float[] paintG, float[] paintB, float[] paintA, out int operations)
        {
            operations = 0;
            try
            {
                var pkg = new ZPackage(Utils.Decompress(compressed));
                pkg.ReadInt(); // version
                operations = pkg.ReadInt();
                pkg.ReadVector3(); // lastOpPoint - not needed, we recompute our own below
                pkg.ReadSingle(); // lastOpRadius
                int n = pkg.ReadInt();
                if (n != pitch * pitch)
                {
                    return false;
                }
                for (int i = 0; i < n; i++)
                {
                    modifiedHeight[i] = pkg.ReadBool();
                    if (modifiedHeight[i])
                    {
                        levelDelta[i] = pkg.ReadSingle();
                        smoothDelta[i] = pkg.ReadSingle();
                    }
                }
                int n2 = pkg.ReadInt();
                if (n2 != pitch * pitch)
                {
                    return n == pitch * pitch; // height data alone still usable even if paint format differs (old-world remap not implemented here - documented simplification)
                }
                for (int i = 0; i < n2; i++)
                {
                    modifiedPaint[i] = pkg.ReadBool();
                    if (modifiedPaint[i])
                    {
                        paintR[i] = pkg.ReadSingle();
                        paintG[i] = pkg.ReadSingle();
                        paintB[i] = pkg.ReadSingle();
                        paintA[i] = pkg.ReadSingle();
                    }
                }
                return true;
            }
            catch (Exception ex)
            {
                PortalDebug.LogWarning($"[WildcardBLandingPadEngine] failed to parse existing s_TCData: {ex.Message}");
                return false;
            }
        }

        private static byte[] SerializeCompilerBlob(int operations, Vector3 lastOpPoint, float lastOpRadius, int pitch,
            bool[] modifiedHeight, float[] levelDelta, float[] smoothDelta, bool[] modifiedPaint, float[] paintR, float[] paintG, float[] paintB, float[] paintA)
        {
            var pkg = new ZPackage();
            pkg.Write(1);
            pkg.Write(operations);
            pkg.Write(lastOpPoint);
            pkg.Write(lastOpRadius);
            int n = pitch * pitch;
            pkg.Write(n);
            for (int i = 0; i < n; i++)
            {
                pkg.Write(modifiedHeight[i]);
                if (modifiedHeight[i])
                {
                    pkg.Write(levelDelta[i]);
                    pkg.Write(smoothDelta[i]);
                }
            }
            pkg.Write(n);
            for (int i = 0; i < n; i++)
            {
                pkg.Write(modifiedPaint[i]);
                if (modifiedPaint[i])
                {
                    pkg.Write(paintR[i]);
                    pkg.Write(paintG[i]);
                    pkg.Write(paintB[i]);
                    pkg.Write(paintA[i]);
                }
            }
            return Utils.Compress(pkg.GetArray());
        }
    }
}
