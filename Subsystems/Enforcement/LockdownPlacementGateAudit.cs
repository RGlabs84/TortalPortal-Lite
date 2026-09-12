using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using TortalPortalLite.Core;

namespace TortalPortalLite.Subsystems.Enforcement
{
    /// <summary>
    /// #250 Teleport-Area Placement Gate Audit. Catalog feasibility "needs-ingame-check" - Piece.m_onlyInTeleportArea
    /// and EffectArea.m_type are both Inspector data with no prefab name baked into the decompile, so the
    /// only way to know which prefabs carry either is to actually walk the tables at runtime and log
    /// what's there. Built SECOND in this wave (right after #123): #197/#208 cite this audit's output for
    /// site selection when a relay lobby wants to sit inside a Teleport-flagged area.
    ///
    /// One-shot pass at OnWorldReady over two tables: ZNetScene.instance.GetPrefabNames() (every
    /// networked prefab - always safe, no asset loading) and ZoneSystem.instance.m_locations (location
    /// prefabs are NOT in ZNetScene; each is a SoftReference&lt;GameObject&gt; that must be Load()ed then
    /// Release()d - the catalog's own documented memory-spike risk, so this pass is defensive: every
    /// per-entry lookup is wrapped so one odd asset shape can never abort the whole audit).
    /// </summary>
    public static class LockdownPlacementGateAudit
    {
        public readonly struct Finding
        {
            public readonly string PrefabName;
            public readonly bool OnlyInTeleportArea;
            public readonly EffectArea.Type AreaFlags;

            public Finding(string name, bool onlyInTeleportArea, EffectArea.Type areaFlags)
            {
                PrefabName = name;
                OnlyInTeleportArea = onlyInTeleportArea;
                AreaFlags = areaFlags;
            }
        }

        private static List<Finding> _findings = new List<Finding>();
        private static bool _ran;

        public static IReadOnlyList<Finding> Findings => _findings;
        public static bool HasRun => _ran;

        /// <summary>Convenience for #251 Fabricated Location Pad's own site-selection question: does any audited entry carry the Teleport bit?</summary>
        public static bool AnyCarriesTeleportArea(out string prefabName)
        {
            foreach (Finding f in _findings)
            {
                if ((f.AreaFlags & EffectArea.Type.Teleport) != 0)
                {
                    prefabName = f.PrefabName;
                    return true;
                }
            }
            prefabName = "";
            return false;
        }

        public static void Run()
        {
            if (_ran || LockdownConfig.PlacementGateAuditOnBoot?.Value == false)
            {
                return;
            }
            _ran = true;
            var findings = new List<Finding>();

            AuditZNetScenePrefabs(findings);
            AuditZoneLocations(findings);

            _findings = findings;
            LogSummary(findings);
        }

        private static void AuditZNetScenePrefabs(List<Finding> findings)
        {
            if (ZNetScene.instance == null)
            {
                PortalDebug.LogWarning("[LockdownPlacementGateAudit] ZNetScene.instance not ready - skipping prefab table pass.");
                return;
            }
            try
            {
                foreach (string name in ZNetScene.instance.GetPrefabNames())
                {
                    try
                    {
                        GameObject prefab = ZNetScene.instance.GetPrefab(name);
                        if (prefab == null)
                        {
                            continue;
                        }
                        bool onlyInArea = false;
                        Piece piece = prefab.GetComponent<Piece>();
                        if (piece != null)
                        {
                            onlyInArea = piece.m_onlyInTeleportArea;
                        }
                        EffectArea.Type flags = EffectArea.Type.None;
                        foreach (EffectArea area in prefab.GetComponentsInChildren<EffectArea>(true))
                        {
                            flags |= area.m_type;
                        }
                        if (onlyInArea || flags != EffectArea.Type.None)
                        {
                            findings.Add(new Finding(name, onlyInArea, flags));
                        }
                    }
                    catch (Exception ex)
                    {
                        PortalDebug.LogWarning($"[LockdownPlacementGateAudit] prefab '{name}' inspection failed (skipped): {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                PortalDebug.LogWarning($"[LockdownPlacementGateAudit] ZNetScene pass failed: {ex.Message}");
            }
        }

        /// <summary>
        /// ZoneLocation.m_prefab is a SoftReference&lt;GameObject&gt; from the SoftReferenceableAssets
        /// assembly, which this project does not (and per the task rules, must not) add a reference to
        /// (CS0012 if the field is touched with its real static type). Resolved entirely through
        /// reflection instead - boxed field value, then Load()/Release() invoked by name - so this
        /// compiles with zero dependency on that assembly. Fully best-effort: any reflection failure
        /// degrades this pass to "0 locations audited" rather than aborting the whole audit or the build.
        /// </summary>
        private static void AuditZoneLocations(List<Finding> findings)
        {
            if (ZoneSystem.instance == null || ZoneSystem.instance.m_locations == null)
            {
                PortalDebug.LogWarning("[LockdownPlacementGateAudit] ZoneSystem.m_locations not ready - skipping location table pass.");
                return;
            }
            int inspected = 0, failed = 0;
            foreach (ZoneSystem.ZoneLocation loc in ZoneSystem.instance.m_locations)
            {
                object softRef = null;
                System.Reflection.MethodInfo loadMethod = null;
                System.Reflection.MethodInfo releaseMethod = null;
                bool loaded = false;
                try
                {
                    if (loc == null || string.IsNullOrEmpty(loc.m_prefabName))
                    {
                        continue;
                    }
                    System.Reflection.FieldInfo prefabField = typeof(ZoneSystem.ZoneLocation).GetField("m_prefab");
                    if (prefabField == null)
                    {
                        continue;
                    }
                    softRef = prefabField.GetValue(loc);
                    if (softRef == null)
                    {
                        continue;
                    }
                    Type softRefType = softRef.GetType();
                    loadMethod = softRefType.GetMethod("Load", Type.EmptyTypes);
                    releaseMethod = softRefType.GetMethod("Release", Type.EmptyTypes);
                    if (loadMethod == null)
                    {
                        continue;
                    }

                    // Load() may mutate the boxed struct's internal state - re-fetch softRef as an
                    // object reference isn't meaningful for a struct, so invoke Load() on the SAME boxed
                    // instance and trust its return value directly (a GameObject).
                    object result = loadMethod.Invoke(softRef, null);
                    GameObject prefab = result as GameObject;
                    loaded = prefab != null;
                    if (!loaded)
                    {
                        continue;
                    }

                    bool onlyInArea = false;
                    Piece piece = prefab.GetComponent<Piece>();
                    if (piece != null)
                    {
                        onlyInArea = piece.m_onlyInTeleportArea;
                    }
                    EffectArea.Type flags = EffectArea.Type.None;
                    foreach (EffectArea area in prefab.GetComponentsInChildren<EffectArea>(true))
                    {
                        flags |= area.m_type;
                    }
                    inspected++;
                    if (onlyInArea || flags != EffectArea.Type.None)
                    {
                        findings.Add(new Finding(loc.m_prefabName, onlyInArea, flags));
                    }
                }
                catch (Exception ex)
                {
                    failed++;
                    PortalDebug.LogWarning($"[LockdownPlacementGateAudit] location '{loc?.m_prefabName}' inspection failed (skipped): {ex.GetType().Name}: {ex.Message}");
                }
                finally
                {
                    if (loaded && releaseMethod != null && softRef != null)
                    {
                        try { releaseMethod.Invoke(softRef, null); } catch { /* best-effort - never let a release failure mask the real finding */ }
                    }
                }
            }
            PortalDebug.LogInfo($"[LockdownPlacementGateAudit] location pass: inspected {inspected}, {failed} failed/skipped.");
        }

        private static void LogSummary(List<Finding> findings)
        {
            if (findings.Count == 0)
            {
                PortalDebug.LogAlways("[LockdownPlacementGateAudit] no prefab carries Piece.m_onlyInTeleportArea or any EffectArea flag.");
                return;
            }
            var sb = new StringBuilder();
            sb.Append($"[LockdownPlacementGateAudit] {findings.Count} flagged prefab(s): ");
            for (int i = 0; i < findings.Count && i < 40; i++)
            {
                Finding f = findings[i];
                sb.Append($"{f.PrefabName}(teleportArea={f.OnlyInTeleportArea}, areas={f.AreaFlags}) ");
            }
            PortalDebug.LogAlways(sb.ToString());
        }
    }
}
