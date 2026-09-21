using System;
using System.Collections.Generic;
using UnityEngine;
using TortalPortalLite.Core;
using TortalPortalLite.Core.Data;

namespace TortalPortalLite.Subsystems.Topology
{
    /// <summary>
    /// Headless ground-height oracle: reproduces the terrain mesh height a client actually sees at any
    /// (x,z) with no collider and no Heightmap instance - none exist server-side, see
    /// TargetedWorldGenValidation's header. Two layers, both mirrored from the SERVER decompile rather
    /// than approximated:
    ///
    ///  1. HeightmapBuilder.Build (:129774-129830). A zone whose four corners share one biome samples
    ///     GetBiomeHeight(biome) directly; a zone straddling biomes smoothstep-lerps the four corner
    ///     biomes' heights across the 64 m zone. Bare WorldGenerator.GetHeight (what the previous
    ///     placement code used) returns the un-blended single-biome height, which at a biome border can
    ///     sit metres above or below the real mesh - a floating or buried portal.
    ///  2. TerrainComp (Load :143900-143932, ApplyToHeightmap :143988-144010). Player terraforming
    ///     (hoe / pickaxe / cultivator) lives in the zone's _TerrainCompiler ZDO as a gzip'd per-vertex
    ///     (level delta, smooth delta) array under ZDOVars.s_TCData, and the mesh vertex is
    ///     clamp(base + level + smooth, base - 8, base + 8). Read straight off that ZDO here so a gate
    ///     raised beside a bed on a levelled base sits on the levelled floor, not the pre-hoe slope.
    ///
    /// Vertices sit on the integer world grid (zone origin - 32 + i, HeightmapBuilder.Build's own
    /// `vector.x + l * scale` with scale 1); a point between them is bilinearly interpolated, which
    /// matches the collision mesh to within the quad's diagonal twist - centimetres on 1 m quads.
    /// One instance per placement search: every vertex and zone it touches is memoised, so the ~2k
    /// candidate probes of a full search cost one noise evaluation per distinct vertex, not per probe.
    /// </summary>
    public sealed class TargetedGroundProbe
    {
        /// <summary>ZoneSystem.c_ZoneSize - a zone is 64 m, Heightmap.m_width 64 at scale 1 (65 vertices per side).</summary>
        private const int ZoneSize = 64;
        private const int VertexPitch = ZoneSize + 1;
        /// <summary>TerrainComp.ApplyToHeightmap's own clamp on how far terraforming may move a vertex (:144005).</summary>
        private const float MaxTerraformDelta = 8f;

        private sealed class ZoneData
        {
            public Heightmap.Biome B0, B1, B2, B3;
            public bool SingleBiome;
            /// <summary>level + smooth delta per vertex (row-major, z * 65 + x), or null when the zone was never terraformed.</summary>
            public float[]? Deltas;
        }

        private readonly Dictionary<long, ZoneData> _zones = new Dictionary<long, ZoneData>();
        private readonly Dictionary<long, float> _vertexHeights = new Dictionary<long, float>();

        public static bool Ready => WorldGenerator.instance != null;

        public static float WaterLevel => ZoneSystem.instance != null ? ZoneSystem.instance.m_waterLevel : ZoneSystem.c_WaterLevel;

        /// <summary>Same test ZoneSystem itself runs pre-heightmap (static, pure WorldGenerator mask lookup).</summary>
        public static bool IsLava(Vector3 pos) => ZoneSystem.IsLavaPreHeightmap(pos);

        /// <summary>Exact terrain-mesh height (see class remarks) at a world (x,z).</summary>
        public float HeightAt(float x, float z)
        {
            int gx = Mathf.FloorToInt(x);
            int gz = Mathf.FloorToInt(z);
            float fx = x - gx;
            float fz = z - gz;
            float h00 = VertexHeight(gx, gz);
            float h10 = VertexHeight(gx + 1, gz);
            float h01 = VertexHeight(gx, gz + 1);
            float h11 = VertexHeight(gx + 1, gz + 1);
            return Mathf.Lerp(Mathf.Lerp(h00, h10, fx), Mathf.Lerp(h01, h11, fx), fz);
        }

        /// <summary>Y component of the terrain normal at (x,z) by central differences over 2 m - 1.0 is dead flat, 0.87 is a 30 degree slope.</summary>
        public float NormalYAt(float x, float z)
        {
            float dx = (HeightAt(x + 1f, z) - HeightAt(x - 1f, z)) * 0.5f;
            float dz = (HeightAt(x, z + 1f) - HeightAt(x, z - 1f)) * 0.5f;
            return 1f / Mathf.Sqrt(1f + (dx * dx) + (dz * dz));
        }

        // ------------------------------------------------------------------------------------ vertices

        private float VertexHeight(int gx, int gz)
        {
            long key = Pack(gx, gz);
            if (_vertexHeights.TryGetValue(key, out float cached))
            {
                return cached;
            }

            // ZoneSystem.GetZone's own math (:115744): floor((p + 32) / 64). A vertex on a shared zone
            // edge resolves to the eastern/northern zone's column 0, which holds the same value.
            int zx = Mathf.FloorToInt((gx + 32) / (float)ZoneSize);
            int zz = Mathf.FloorToInt((gz + 32) / (float)ZoneSize);
            ZoneData zone = GetZone(zx, zz);
            int vx = gx - ((zx * ZoneSize) - (ZoneSize / 2));
            int vz = gz - ((zz * ZoneSize) - (ZoneSize / 2));

            WorldGenerator wg = WorldGenerator.instance;
            float h;
            if (zone.SingleBiome)
            {
                h = wg.GetBiomeHeight(zone.B0, gx, gz, out _);
            }
            else
            {
                float t2 = DUtils.SmoothStep(0f, 1f, vx / (float)ZoneSize);
                float t = DUtils.SmoothStep(0f, 1f, vz / (float)ZoneSize);
                float h0 = wg.GetBiomeHeight(zone.B0, gx, gz, out _);
                float h1 = wg.GetBiomeHeight(zone.B1, gx, gz, out _);
                float h2 = wg.GetBiomeHeight(zone.B2, gx, gz, out _);
                float h3 = wg.GetBiomeHeight(zone.B3, gx, gz, out _);
                h = DUtils.Lerp(DUtils.Lerp(h0, h1, t2), DUtils.Lerp(h2, h3, t2), t);
            }

            if (zone.Deltas != null)
            {
                float delta = zone.Deltas[(vz * VertexPitch) + vx];
                if (delta != 0f)
                {
                    h = Mathf.Clamp(h + delta, h - MaxTerraformDelta, h + MaxTerraformDelta);
                }
            }

            _vertexHeights[key] = h;
            return h;
        }

        private ZoneData GetZone(int zx, int zz)
        {
            long key = Pack(zx, zz);
            if (_zones.TryGetValue(key, out ZoneData existing))
            {
                return existing;
            }

            // HeightmapBuilder.Build samples the four corners of the zone's vertex grid (zone centre
            // minus half a width, then plus the full width), via GetBiome - not GetBiomeSector.
            WorldGenerator wg = WorldGenerator.instance;
            float x0 = (zx * ZoneSize) - (ZoneSize / 2);
            float z0 = (zz * ZoneSize) - (ZoneSize / 2);
            var zone = new ZoneData
            {
                B0 = wg.GetBiome(x0, z0),
                B1 = wg.GetBiome(x0 + ZoneSize, z0),
                B2 = wg.GetBiome(x0, z0 + ZoneSize),
                B3 = wg.GetBiome(x0 + ZoneSize, z0 + ZoneSize),
            };
            zone.SingleBiome = zone.B1 == zone.B0 && zone.B2 == zone.B0 && zone.B3 == zone.B0;
            zone.Deltas = LoadTerraformDeltas(zx, zz);
            _zones[key] = zone;
            return zone;
        }

        /// <summary>
        /// The zone's _TerrainCompiler ZDO sits exactly at ZoneSystem.GetZonePos (x*64, 0, z*64) - it is
        /// instantiated at the Heightmap's own transform (Heightmap.GetAndCreateTerrainCompiler :129569).
        /// Identified by carrying an s_TCData byte array rather than by prefab name, so a renamed or
        /// modded compiler prefab still resolves.
        /// </summary>
        private static float[]? LoadTerraformDeltas(int zx, int zz)
        {
            if (ZDOMan.instance == null)
            {
                return null;
            }
            var zonePos = new Vector3(zx * ZoneSize, 0f, zz * ZoneSize);
            List<ZDO> near = ZdoSpatialQuery.FindNear(zonePos, ZoneSize);
            foreach (ZDO zdo in near)
            {
                byte[] data = zdo.GetByteArray(ZDOVars.s_TCData);
                if (data == null)
                {
                    continue;
                }
                Vector3 p = zdo.GetPosition();
                if (Mathf.Abs(p.x - zonePos.x) > 2f || Mathf.Abs(p.z - zonePos.z) > 2f)
                {
                    continue;
                }
                try
                {
                    return ParseTerrainCompData(data);
                }
                catch (Exception ex)
                {
                    PortalDebug.LogWarning($"[TargetedGroundProbe] terrain data for zone ({zx},{zz}) unreadable, using base terrain: {ex.GetType().Name}: {ex.Message}");
                    return null;
                }
            }
            return null;
        }

        /// <summary>TerrainComp.Load's own read order (:143908-143932), paint section ignored.</summary>
        private static float[]? ParseTerrainCompData(byte[] data)
        {
            var pkg = new ZPackage(Utils.Decompress(data));
            pkg.ReadInt();      // terrainCompVersion
            pkg.ReadInt();      // m_operations
            pkg.ReadVector3();  // m_lastOpPoint
            pkg.ReadSingle();   // m_lastOpRadius
            int count = pkg.ReadInt();
            if (count != VertexPitch * VertexPitch)
            {
                PortalDebug.LogWarning($"[TargetedGroundProbe] terrain data vertex count {count} != {VertexPitch * VertexPitch}, ignoring");
                return null;
            }
            var deltas = new float[count];
            bool any = false;
            for (int i = 0; i < count; i++)
            {
                if (!pkg.ReadBool())
                {
                    continue;
                }
                float level = pkg.ReadSingle();
                float smooth = pkg.ReadSingle();
                deltas[i] = level + smooth;
                any |= deltas[i] != 0f;
            }
            return any ? deltas : null;
        }

        private static long Pack(int a, int b) => ((long)a << 32) | ((long)b & 0xFFFFFFFFL);
    }
}
