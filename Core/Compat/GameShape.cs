using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using TortalPortalLite.Core;

namespace TortalPortalLite.Core.Compat
{
    /// <summary>
    /// Detects which shape of the sector API the running game exposes and bridges the one call this
    /// mod makes through it - ZDOMan.FindSectorObjects, keyed by ZoneSystem.GetZone(Vector3) - which
    /// Core/Data/ZdoSpatialQuery.FindNear uses for every radius query in the mod. Forked verbatim from
    /// Wonderland's Core/Compat/GameShape.cs (namespace renamed only) - pure reflection over vanilla
    /// types, no mod-specific logic to adapt.
    ///
    /// Two shapes exist (ground truth: libs-Tools/1.0/DECOMPILED for the release, the OLD-DECOMPILED-*
    /// folders for the older builds):
    ///
    ///  - Release 1.0.7+ (server build 25185644, network version 39):
    ///    FindSectorObjects(Vector2s sector, SimulationDistance distance, List&lt;ZDO&gt; near, List&lt;ZDO&gt; distant = null).
    ///    The old (int area, int distantArea) pair collapsed into the SimulationDistance struct, and
    ///    unless IsClassic is set the sweep is a disc filtered through ZoneSystem.ZonesWithinRadius
    ///    rather than the square ring older builds swept. This mod compiles against this build, so this
    ///    is the native, statically-typed path.
    ///  - Legacy five-arg (pre-1.0 builds, boxed sector object): FindSectorObjects(sector, int area, int
    ///    distantArea, List&lt;ZDO&gt;, List&lt;ZDO&gt;). Reached purely by reflection, passing the boxed sector
    ///    object GetZone hands back straight through, so neither sector type is ever named at compile time.
    ///
    /// The mono rule that dictates this layout: a compile-time binding to a member the loaded assembly
    /// lacks throws at JIT of the CALLING method, taking the whole method down even with a try/catch
    /// inside it. So the native path lives in its own method that is only ever called - and therefore
    /// only ever JIT-compiled - once Detect() has confirmed the release shape, and Detect() itself
    /// compares type NAMES rather than using typeof() on anything that might be missing.
    ///
    /// A second rule: a patcher that bridges old mods onto 1.0 can inject its own same-name,
    /// same-parameter overload directly onto a native type. Type.GetMethod(name, bindingFlags, binder,
    /// types, modifiers) - the single-result overload - throws AmbiguousMatchException the instant two
    /// methods share a name and parameter list, even though only one is real; it doesn't matter that the
    /// binder was given exact parameter types, because those two methods can differ only in return type,
    /// which GetMethod does not use to disambiguate. So Detect() never calls that overload: every lookup
    /// goes through GetMethods() and is filtered and paired by hand, and Release is tried against every
    /// GetZone candidate before Legacy so a shimmed build still prefers the native path.
    /// </summary>
    public static class GameShape
    {
        public enum Build
        {
            Unknown,
            /// <summary>Valheim 1.0.7+: FindSectorObjects takes a SimulationDistance struct.</summary>
            Release10_SimulationDistance,
            /// <summary>Pre-1.0 builds: FindSectorObjects takes (int area, int distantArea).</summary>
            Legacy_FiveArgSectors
        }

        public static Build Detected { get; private set; } = Build.Unknown;

        private static bool _probed;
        private static MethodInfo _legacyGetZone;
        private static MethodInfo _legacyFindSectorObjects;

        /// <summary>
        /// Probes the loaded assembly by reflection only (inspecting MethodInfo/ParameterInfo, never
        /// invoking or statically referencing a possibly-missing type) - safe to call unconditionally on
        /// any build. Call once at startup before anything in ZdoSpatialQuery runs.
        /// </summary>
        public static void Detect()
        {
            if (_probed)
            {
                return;
            }
            _probed = true;

            try
            {
                MethodInfo[] getZoneCandidates = typeof(ZoneSystem)
                    .GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .Where(m => m.Name == "GetZone" && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == typeof(Vector3))
                    .ToArray();

                if (getZoneCandidates.Length == 0)
                {
                    PortalDebug.LogWarning("[GameShape] ZoneSystem.GetZone(Vector3) not found at all - every radius query in this mod will return nothing on this build until it is re-verified against the decompile.");
                    return;
                }

                MethodInfo[] findSectorCandidates = typeof(ZDOMan)
                    .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .Where(m => m.Name == "FindSectorObjects")
                    .ToArray();

                foreach (MethodInfo getZone in getZoneCandidates)
                {
                    Type sectorType = getZone.ReturnType;
                    if (findSectorCandidates.Any(m => IsReleaseShape(m, sectorType)))
                    {
                        Detected = Build.Release10_SimulationDistance;
                        PortalDebug.LogAlways($"[GameShape] detected the Valheim 1.0.7+ sector API ({sectorType.Name} sectors, SimulationDistance) - using the native path.");
                        return;
                    }
                }

                foreach (MethodInfo getZone in getZoneCandidates)
                {
                    Type sectorType = getZone.ReturnType;
                    MethodInfo legacyFind = findSectorCandidates.FirstOrDefault(m => IsLegacyShape(m, sectorType));
                    if (legacyFind != null)
                    {
                        _legacyGetZone = getZone;
                        _legacyFindSectorObjects = legacyFind;
                        Detected = Build.Legacy_FiveArgSectors;
                        PortalDebug.LogAlways($"[GameShape] detected a pre-1.0 sector API ({sectorType.Name} sectors, five-arg FindSectorObjects) - bridging via reflection. This mod is built and tested against 1.0.7; treat this path as best-effort.");
                        return;
                    }
                }

                PortalDebug.LogWarning($"[GameShape] no matching GetZone/FindSectorObjects pair found on this build ({getZoneCandidates.Length} GetZone overload(s), {findSectorCandidates.Length} FindSectorObjects overload(s)) - every radius query in this mod will return nothing until Core/Compat/GameShape.cs is re-verified against this build's decompile.");
            }
            catch (Exception ex)
            {
                PortalDebug.LogWarning($"[GameShape] detection threw unexpectedly ({ex.GetType().Name}: {ex.Message}) - every radius query in this mod will return nothing on this build. This should never happen; please report it.");
            }
        }

        private static bool IsReleaseShape(MethodInfo m, Type sectorType)
        {
            ParameterInfo[] p = m.GetParameters();
            return p.Length == 4
                && p[0].ParameterType == sectorType
                && p[1].ParameterType.Name == "SimulationDistance"
                && p[2].ParameterType == typeof(List<ZDO>);
        }

        private static bool IsLegacyShape(MethodInfo m, Type sectorType)
        {
            ParameterInfo[] p = m.GetParameters();
            return p.Length == 5
                && p[0].ParameterType == sectorType
                && p[1].ParameterType == typeof(int)
                && p[2].ParameterType == typeof(int)
                && p[3].ParameterType == typeof(List<ZDO>);
        }

        /// <summary>
        /// The legacy bridge for ZdoSpatialQuery.FindNear's inner call: GetZone's boxed result is handed
        /// straight to the five-arg FindSectorObjects with distantArea 0, which is the old square-ring
        /// sweep of <paramref name="area"/> sectors around the anchor. Never invoked on the release build.
        /// </summary>
        public static void FindSectorObjectsLegacy(Vector3 worldPos, int area, List<ZDO> results)
        {
            if (_legacyGetZone == null || _legacyFindSectorObjects == null || ZDOMan.instance == null)
            {
                return;
            }

            try
            {
                object sector = _legacyGetZone.Invoke(null, new object[] { worldPos });
                _legacyFindSectorObjects.Invoke(ZDOMan.instance, new object[] { sector, area, 0, results, null });
            }
            catch (Exception ex)
            {
                PortalDebug.LogWarning($"[GameShape] legacy sector bridge failed: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }
}
