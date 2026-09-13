using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using TortalPortalLite.Core;

namespace TortalPortalLite.Subsystems.Foundations
{
    /// <summary>
    /// #77 Output surface: generated realm map, SVG only (the PNG stretch goal is explicitly NOT
    /// attempted: a dedicated server reports SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null,
    /// same branch vanilla itself takes at ZNet.UpdatePlayerList :81245-81299, so Texture2D/EncodeToPNG
    /// would need a hand-rolled PNG writer to avoid touching the graphics device at all - out of scope
    /// for this wave's effort budget; SVG needs zero image libraries and zero graphics device).
    ///
    /// World XZ maps to SVG coordinates with sx=(x+waterEdge)*scale, sy=(waterEdge-z)*scale
    /// (WorldGenerator.worldSize=10000f/waterEdge=10500f, :151118-151122). The biome-tint background is
    /// sampled ONCE via WorldGenerator.instance.GetBiome(x,z) (:151725, pure procedural noise - safe
    /// headless, unlike any ZoneSystem raycast helper) across a coarse grid, TIME-SLICED across ticks
    /// (a handful of cells per OnUpdate call, since this class has no MonoBehaviour to host a coroutine)
    /// and cached forever after - catalog #77's own explicit warning against resampling per export.
    /// </summary>
    public static class OpsOutputMapEngine
    {
        private const int CellsPerTick = 1024;
        private static float _timer;
        private static bool _biomeGridReady;
        private static int _gridProgress;
        private static int _gridRes;
        private static Heightmap.Biome[,]? _biomeGrid;

        public static void OnUpdate(float dt)
        {
            if (OpsOutputConfig.MapEnabled?.Value != true)
            {
                return;
            }

            if (!_biomeGridReady)
            {
                BuildBiomeGridStep();
            }

            _timer += dt;
            float interval = OpsOutputConfig.MapIntervalSeconds?.Value ?? 300f;
            if (_timer < interval)
            {
                return;
            }
            _timer = 0f;
            RenderNow();
        }

        private static void BuildBiomeGridStep()
        {
            if (WorldGenerator.instance == null)
            {
                return; // not ready yet - retried next tick, same tolerant pattern CapabilityProbe uses
            }

            int res = Math.Max(16, OpsOutputConfig.MapBiomeGridResolution?.Value ?? 128);
            if (_biomeGrid == null || _gridRes != res)
            {
                _gridRes = res;
                _biomeGrid = new Heightmap.Biome[res, res];
                _gridProgress = 0;
            }

            int total = res * res;
            int budget = CellsPerTick;
            float half = WorldGenerator.waterEdge;
            while (budget-- > 0 && _gridProgress < total)
            {
                int ix = _gridProgress % res;
                int iy = _gridProgress / res;
                float worldX = ((ix + 0.5f) / res * 2f - 1f) * half;
                float worldZ = ((iy + 0.5f) / res * 2f - 1f) * half;
                _biomeGrid[ix, iy] = WorldGenerator.instance.GetBiome(worldX, worldZ);
                _gridProgress++;
            }

            if (_gridProgress >= total)
            {
                _biomeGridReady = true;
                PortalDebug.LogAlways($"[OpsOutputMapEngine] biome background grid cached ({res}x{res} cells) - never resampled again this session.");
            }
        }

        public static string RenderNow()
        {
            if (!_biomeGridReady)
            {
                return "tpl: map not ready yet (biome background still sampling - retry shortly).";
            }
            try
            {
                string svg = BuildSvg();
                string path = Path.Combine(OpsOutputConfig.PluginConfigDir, "TortalPortalLite.portals.svg");
                if (OpsOutputAtomicFile.TryWriteAllText(path, svg, out string? err))
                {
                    return $"tpl: map written ({OpsOutputReportModel.Latest.Count} portal(s), {svg.Length / 1024}KB).";
                }
                return $"tpl: map write failed: {err}";
            }
            catch (Exception ex)
            {
                PortalDebug.LogError($"[OpsOutputMapEngine] render failed: {ex.GetType().Name}: {ex.Message}");
                return $"tpl: map render failed: {ex.GetType().Name}: {ex.Message}";
            }
        }

        private const float CanvasSize = 900f;

        private static string BuildSvg()
        {
            float half = WorldGenerator.waterEdge;
            float scale = CanvasSize / (2f * half);
            float Sx(float worldX) => (worldX + half) * scale;
            float Sy(float worldZ) => (half - worldZ) * scale;

            var sb = new StringBuilder(64 * 1024);
            sb.Append("<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 ").Append(CanvasSize).Append(' ').Append(CanvasSize)
              .Append("' font-family='sans-serif' font-size='11'>\n");
            sb.Append("<rect width='100%' height='100%' fill='#0b3d55'/>\n"); // ocean base

            if (_biomeGrid != null)
            {
                float cell = CanvasSize / _gridRes;
                for (int iy = 0; iy < _gridRes; iy++)
                {
                    for (int ix = 0; ix < _gridRes; ix++)
                    {
                        string? color = BiomeColor(_biomeGrid[ix, iy]);
                        if (color == null) continue; // Ocean/None - already the background
                        sb.Append("<rect x='").Append((ix * cell).ToString("F1")).Append("' y='").Append((iy * cell).ToString("F1"))
                          .Append("' width='").Append((cell + 0.6f).ToString("F1")).Append("' height='").Append((cell + 0.6f).ToString("F1"))
                          .Append("' fill='").Append(color).Append("' fill-opacity='0.75'/>\n");
                    }
                }
            }

            IReadOnlyList<OpsPortalReport> reports = OpsOutputReportModel.Latest;

            // Connections - drawn once per pair regardless of direction, so a mutual pair isn't drawn twice.
            var drawnPairs = new HashSet<string>();
            foreach (OpsPortalReport r in reports)
            {
                if (!r.Connected) continue;
                string key = string.CompareOrdinal(r.Id, r.PartnerId) < 0 ? $"{r.Id}|{r.PartnerId}" : $"{r.PartnerId}|{r.Id}";
                if (!drawnPairs.Add(key)) continue;
                string stroke = r.Reciprocal ? "#ffffff" : "#ff6666";
                sb.Append("<line x1='").Append(Sx(r.X).ToString("F1")).Append("' y1='").Append(Sy(r.Z).ToString("F1"))
                  .Append("' x2='").Append(Sx(r.PartnerX).ToString("F1")).Append("' y2='").Append(Sy(r.PartnerZ).ToString("F1"))
                  .Append("' stroke='").Append(stroke).Append("' stroke-width='1' stroke-opacity='0.7'/>\n");
            }

            // Portal dots, coloured by network (grey = unmanaged), interior portals drawn smaller with a distinct ring - catalog #77's own warning against drawing them identically to a surface gate.
            var networkColors = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (OpsPortalReport r in reports)
            {
                string color = string.IsNullOrEmpty(r.NetworkId) ? "#cccccc" : NetworkColor(r.NetworkId, networkColors);
                float radius = r.Interior ? 2.2f : 3.2f;
                sb.Append("<circle cx='").Append(Sx(r.X).ToString("F1")).Append("' cy='").Append(Sy(r.Z).ToString("F1"))
                  .Append("' r='").Append(radius.ToString("F1")).Append("' fill='").Append(color)
                  .Append("' stroke='").Append(r.Interior ? "#ff00ff" : "#000000").Append("' stroke-width='0.6'/>\n");
            }

            // Labels: one per TAG (first member only) - catalog #77's own warning that 200 overlapping per-portal labels are unreadable.
            var labelledTags = new HashSet<string>(StringComparer.Ordinal);
            foreach (OpsPortalReport r in reports)
            {
                if (string.IsNullOrEmpty(r.Tag) || !labelledTags.Add(r.Tag)) continue;
                sb.Append("<text x='").Append((Sx(r.X) + 5f).ToString("F1")).Append("' y='").Append(Sy(r.Z).ToString("F1"))
                  .Append("' fill='#ffffff' stroke='#000000' stroke-width='0.3'>").Append(EscapeXml(r.Tag)).Append("</text>\n");
            }

            sb.Append("</svg>\n");
            return sb.ToString();
        }

        private static string? BiomeColor(Heightmap.Biome biome) => biome switch
        {
            Heightmap.Biome.Meadows => "#7fae56",
            Heightmap.Biome.BlackForest => "#3f5e3a",
            Heightmap.Biome.Swamp => "#5b5330",
            Heightmap.Biome.Mountain => "#e6f0f5",
            Heightmap.Biome.Plains => "#cdb35a",
            Heightmap.Biome.AshLands => "#7a2323",
            Heightmap.Biome.DeepNorth => "#dfe9f0",
            Heightmap.Biome.Mistlands => "#4a4a52",
            _ => null, // None/Ocean - the base rect already paints this
        };

        private static string NetworkColor(string networkId, Dictionary<string, string> cache)
        {
            if (cache.TryGetValue(networkId, out string? existing))
            {
                return existing;
            }
            int hue = Math.Abs(networkId.GetHashCode()) % 360;
            string color = $"hsl({hue},80%,60%)";
            cache[networkId] = color;
            return color;
        }

        private static string EscapeXml(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
    }
}
