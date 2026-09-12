using System.Collections.Generic;
using UnityEngine;

namespace TortalPortalLite.Subsystems.Topology
{
    /// <summary>
    /// Thin wrapper over ZoneSystem's own already-public location-registry API (confirmed against the
    /// decompile - `FindClosestLocation`, `FindLocations`, `GetLocationIcon` are all `public`, no
    /// Publicize needed): the vocabulary is `ZoneSystem.instance.m_locations` (List&lt;ZoneSystem.ZoneLocation&gt;,
    /// SERVER decompile :113248), instances live in `m_locationInstances` (:113295, populated at world
    /// load by GenerateLocationsIfNeeded regardless of whether any player has visited), and this class
    /// exists only to (a) hand back a sane exterior-radius default and (b) keep every engine's location
    /// lookup consistent rather than each re-deriving the same eight-compass-offset placement.
    ///
    /// Deliberately does NOT hard-code any location prefab name (e.g. a guess at "Eikthyrnir"): the
    /// catalog's own verified text flags these as Unity asset data outside the decompile, needing an
    /// in-game dump to confirm (#182/#183/#184's own prerequisites). Every engine that needs a specific
    /// location type instead takes its exact name from an admin-authored TargetedRouteStore route -
    /// wrong or renamed names then just fail closed (FindLocations returns empty), not silently wrong.
    /// </summary>
    public static class TargetedLocationRegistry
    {
        private const float DefaultExteriorRadius = 12f;

        public static bool TryFindClosest(string locationName, Vector3 near, out ZoneSystem.LocationInstance instance)
        {
            instance = default;
            if (ZoneSystem.instance == null || string.IsNullOrEmpty(locationName))
            {
                return false;
            }
            return ZoneSystem.instance.FindClosestLocation(locationName, near, out instance);
        }

        public static bool TryFindAll(string locationName, out List<ZoneSystem.LocationInstance> instances)
        {
            instances = new List<ZoneSystem.LocationInstance>();
            if (ZoneSystem.instance == null || string.IsNullOrEmpty(locationName))
            {
                return false;
            }
            return ZoneSystem.instance.FindLocations(locationName, ref instances);
        }

        /// <summary>
        /// The ZoneSystem.ZoneLocation DEFINITION (not an instance) by its exact prefab name -
        /// `ZoneSystem.instance.m_locations` is already public (List&lt;ZoneSystem.ZoneLocation&gt;, SERVER
        /// decompile :113248). Matched on `m_prefabName` (a plain string), NOT `m_prefab.Name`
        /// (FindClosestLocation/FindLocations's own comparison) - `m_prefab` is a `SoftReference&lt;GameObject&gt;`
        /// whose `.Name` property lives in the SoftReferenceableAssets assembly, which this project does
        /// not reference (and must not start referencing - the .csproj is off-limits). This is exactly
        /// the fallback the catalog's own #186 howItWorks names for this exact hazard: "if [SoftReference
        /// access] forces an asset load, index by ZoneLocation.m_prefabName (plain string) instead."
        /// </summary>
        public static bool TryGetZoneLocation(string locationName, out ZoneSystem.ZoneLocation location)
        {
            location = null;
            if (ZoneSystem.instance == null || string.IsNullOrEmpty(locationName))
            {
                return false;
            }
            foreach (ZoneSystem.ZoneLocation candidate in ZoneSystem.instance.m_locations)
            {
                if (candidate != null && candidate.m_prefabName == locationName)
                {
                    location = candidate;
                    return true;
                }
            }
            return false;
        }

        public static float ExteriorRadiusOf(ZoneSystem.LocationInstance instance)
        {
            float r = instance.m_location != null ? instance.m_location.m_exteriorRadius : 0f;
            return r > 0.1f ? r : DefaultExteriorRadius;
        }

        /// <summary>
        /// Boss/DLC-agnostic defeat check. ZoneSystem.GlobalKeyAdd (SERVER decompile :113483-113511)
        /// unconditionally populates `m_globalKeysValues[keyValue] = value` for EVERY key it ever adds,
        /// including a bare no-value key like "defeated_eikthyr" (value == "") - so the string overload
        /// `GetGlobalKey(string)` (:115968, checks m_globalKeysValues presence) is a reliable presence
        /// test for classic enum-backed bosses AND any newer boss tracked only by a raw string key, and
        /// is used uniformly here rather than the separate `GetGlobalKey(GlobalKeys)` overload (which
        /// checks a different set, m_globalKeysEnums, populated only for keys that parse against the
        /// fixed GlobalKeys enum and would miss a DLC boss added after this decompile).
        /// </summary>
        public static bool IsGlobalKeySet(string globalKeyName)
        {
            return !string.IsNullOrEmpty(globalKeyName) && ZoneSystem.instance != null && ZoneSystem.instance.GetGlobalKey(globalKeyName);
        }

        /// <summary>Vanilla's own Start Temple lookup (#181) - server branch scans m_locationInstances for the icon-flagged StartTemple entry (SERVER decompile :115422-115447).</summary>
        public static bool TryGetStartTemple(out Vector3 pos)
        {
            pos = Vector3.zero;
            if (ZoneSystem.instance == null || Game.instance == null)
            {
                return false;
            }
            return ZoneSystem.instance.GetLocationIcon(Game.instance.m_StartLocation, out pos);
        }
    }
}
