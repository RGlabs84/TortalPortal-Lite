using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using TortalPortalLite.Core;
using TortalPortalLite.Core.Data;
using TortalPortalLite.Subsystems.Foundations;

namespace TortalPortalLite.Subsystems.Enforcement
{
    /// <summary>One admin-declared allowed-pairing group: portals whose biome falls in this group's list share this group's tag suffix, so they can only ever pair with each other.</summary>
    public sealed class LockdownRegionGroup
    {
        public string Name = "";
        public List<string> Biomes = new List<string>();

        /// <summary>Appended to the portal's own tag - kept short deliberately (catalog #118's own 10-char TextInput.RequestText cap warning).</summary>
        public string Suffix = "";
    }

    public sealed class LockdownRegionRulesFile
    {
        public int SchemaVersion = 1;
        public List<LockdownRegionGroup> Groups = new List<LockdownRegionGroup>();
    }

    /// <summary>
    /// #118 Biome and Region Restricted Networks. Recruits Game.ConnectPortals instead of fighting it:
    /// its phase 1 tears down any link whose partner's s_tag string differs and phase 2 only pairs
    /// portals whose tags are byte-identical, so enforcing "may only pair within an allowed region"
    /// reduces to rewriting s_tag so two disallowed regions can never share a tag string - a per-region
    /// suffix appended to the portal's own base tag.
    ///
    /// Biome classification is WorldGenerator.GetBiome, the only terrain oracle genuinely available
    /// headless (same reasoning PortalCensus.BiomeAt and Topology's TargetedWorldGenValidation already
    /// document: every collider-based helper is dead server-side). Because biome boundaries are
    /// fractal and WorldGenerator is blind to terraforming, a portal sitting near a seam can flip region
    /// between samples - hysteresis (N consecutive samples of a NEW group) is mandatory and implemented
    /// here, not optional.
    ///
    /// Tag writes never auto-dirty the portal chunk (only SetConnection/AddIfPortal/HandleDestroyedZDO
    /// do) so every applied rename calls ZDOMan.instance.SetDirtyPortals() explicitly - forgetting it is,
    /// per the catalog's own words, "a classic silent data-loss bug".
    /// </summary>
    public static class LockdownRegionEngine
    {
        private const float FilePollInterval = 5f;
        private static float _fileTimer;
        private static DateTime _fileStamp = DateTime.MinValue;
        private static List<LockdownRegionGroup> _groups = new List<LockdownRegionGroup>();

        private static float _reconcileTimer;
        private static List<LockdownRegionGroup> _programmaticGroups; // set by LockdownRulesetEngine's TieredTravel preset - takes precedence over the JSON file while non-null

        /// <summary>#121 Ironman/Hardcore Rulesets' TieredTravel preset computes region groups from live defeated_* progression keys rather than an admin-declared file - pass null to fall back to the JSON file.</summary>
        public static void SetProgrammaticGroups(List<LockdownRegionGroup> groups)
        {
            _programmaticGroups = groups;
            if (groups != null)
            {
                _groups = groups;
            }
        }

        // Hysteresis state per portal (keyed by live ZDOID - a restart simply re-samples fresh, which is fine, this is a slow-drifting classification not persisted state).
        private static readonly Dictionary<ZDOID, (string candidateGroup, int streak)> _hysteresis = new Dictionary<ZDOID, (string, int)>();

        public static void OnUpdate(float dt)
        {
            if (_programmaticGroups == null)
            {
                _fileTimer += dt;
                if (_fileTimer >= FilePollInterval)
                {
                    _fileTimer = 0f;
                    TryReloadFile();
                }
            }
            if (_groups.Count == 0)
            {
                return;
            }

            _reconcileTimer += dt;
            float interval = LockdownConfig.RegionReconcileSeconds?.Value ?? 30f;
            if (_reconcileTimer < interval)
            {
                return;
            }
            _reconcileTimer = 0f;
            Reconcile();
        }

        private static string FilePath()
        {
            string name = LockdownConfig.RegionRulesFile?.Value ?? "lockdown_regions.json";
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
                var parsed = JsonConvert.DeserializeObject<LockdownRegionRulesFile>(File.ReadAllText(path));
                if (parsed?.Groups == null)
                {
                    return;
                }
                _groups = parsed.Groups;
                _fileStamp = stamp;
                PortalDebug.LogAlways($"[LockdownRegionEngine] loaded {_groups.Count} region group(s) from '{path}'.");
            }
            catch (Exception ex)
            {
                PortalDebug.LogError($"[LockdownRegionEngine] failed to load region rules: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static void Reconcile()
        {
            if (ZDOMan.instance == null)
            {
                return;
            }
            int requiredSamples = LockdownConfig.RegionHysteresisSamples?.Value ?? 3;
            bool anyDirty = false;

            foreach (PortalRecord rec in PortalCensus.Latest)
            {
                LockdownRegionGroup group = ResolveGroup(rec.Position);
                string candidateSuffix = group?.Suffix ?? "";

                string currentSuffix = CurrentAppliedSuffix(rec.Tag);
                if (currentSuffix == candidateSuffix)
                {
                    _hysteresis.Remove(rec.Uid);
                    continue;
                }

                (string candidateGroup, int streak) state = _hysteresis.TryGetValue(rec.Uid, out var s) ? s : (candidateSuffix, 0);
                if (state.candidateGroup != candidateSuffix)
                {
                    state = (candidateSuffix, 1);
                }
                else
                {
                    state.streak++;
                }
                _hysteresis[rec.Uid] = state;

                if (state.streak < requiredSamples)
                {
                    continue; // Not enough consecutive samples yet - avoid thrash on a biome seam.
                }
                _hysteresis.Remove(rec.Uid);

                string baseTag = StripKnownSuffix(rec.Tag);
                string desiredTag = baseTag + candidateSuffix;
                if (desiredTag.Length > TagCodec.MaxTagLength)
                {
                    PortalDebug.LogWarning($"[LockdownRegionEngine] '{desiredTag}' ({desiredTag.Length} chars) exceeds the 10-char tag budget for portal {rec.Uid} - suffix skipped.");
                    continue;
                }
                if (!LockdownWriteBudget.TryConsume())
                {
                    continue;
                }

                ZDO zdo = ZDOMan.instance.GetZDO(rec.Uid);
                if (zdo == null || !zdo.IsValid())
                {
                    continue;
                }
                PortalOwnership.ClaimAndWrite(zdo, z => z.Set(ZDOVars.s_tag, desiredTag));
                anyDirty = true;
                PortalDebug.LogInfo($"[LockdownRegionEngine] retagged {rec.Uid} '{rec.Tag}' -> '{desiredTag}' (region group '{group?.Name ?? "(none)"}').");
            }

            if (anyDirty)
            {
                ZDOMan.instance.SetDirtyPortals();
            }
        }

        private static LockdownRegionGroup ResolveGroup(UnityEngine.Vector3 pos)
        {
            Heightmap.Biome biome = PortalCensus.BiomeAt(pos);
            string biomeName = biome.ToString();
            foreach (LockdownRegionGroup g in _groups)
            {
                if (g.Biomes != null && g.Biomes.Contains(biomeName))
                {
                    return g;
                }
            }
            return null;
        }

        private static string CurrentAppliedSuffix(string tag)
        {
            foreach (LockdownRegionGroup g in _groups)
            {
                if (!string.IsNullOrEmpty(g.Suffix) && tag.EndsWith(g.Suffix, StringComparison.Ordinal))
                {
                    return g.Suffix;
                }
            }
            return "";
        }

        private static string StripKnownSuffix(string tag)
        {
            string suffix = CurrentAppliedSuffix(tag);
            return string.IsNullOrEmpty(suffix) ? tag : tag.Substring(0, tag.Length - suffix.Length);
        }
    }
}
