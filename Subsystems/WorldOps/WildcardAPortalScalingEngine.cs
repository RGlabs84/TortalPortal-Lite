using System;
using UnityEngine;
using TortalPortalLite.Core;
using TortalPortalLite.Core.Data;

namespace TortalPortalLite.Subsystems.WorldOps
{
    /// <summary>
    /// #146 Portal Scaling Through s_scaleHash - feasibility "needs-ingame-check", honoured here rather
    /// than assumed. ZNetView.Awake's client-instantiation branch (SERVER decompile :82591-82606)
    /// applies a ZDO-stored localScale ONLY `if (m_syncInitialScale)`:
    /// `Vector3 vec = m_zdo.GetVec3(ZDOVars.s_scaleHash, Vector3.zero); if (vec != Vector3.zero)
    /// transform.localScale = vec; else { float f = m_zdo.GetFloat(ZDOVars.s_scaleScalarHash,
    /// transform.localScale.x); ... }`. `m_syncInitialScale` (ZNetView, public bool, :82543) is
    /// Inspector data this mod cannot see from the decompile alone - and per #146's own citation, even
    /// LoadFields cannot bootstrap it if false, since LoadFields runs AFTER this scale block and Unity's
    /// Instantiate always copies the PREFAB's own serialized value, never a previous instance's.
    ///
    /// So this engine actually PROBES the portal prefabs at OnWorldReady (same shape as
    /// Foundations/CapabilityProbe.cs's own ProbePortalPrefabs) and logs a definitive yes/no BEFORE any
    /// admin leans on this feature, rather than silently writing a field that may or may not do anything.
    /// Writes still happen when requested regardless of the probe result (per #146's own citation: "if it
    /// does not work, absolutely nothing happens and no error is logged" - harmless either way), but the
    /// boot-time log line tells an admin whether to expect it to work.
    /// </summary>
    public static class WildcardAPortalScalingEngine
    {
        private static bool _probed;
        private static bool? _likelyWorks;

        public static void OnWorldReady()
        {
            Probe();
        }

        public static bool? LikelyWorks => _likelyWorks;

        private static void Probe()
        {
            if (_probed || Game.instance == null || ZNetScene.instance == null)
            {
                return;
            }
            _probed = true;
            try
            {
                bool anyTrue = false;
                bool anyChecked = false;
                foreach (int hash in PortalRegistry.PrefabHashes)
                {
                    GameObject prefab = ZNetScene.instance.GetPrefab(hash);
                    ZNetView view = prefab != null ? prefab.GetComponent<ZNetView>() : null;
                    if (view == null)
                    {
                        continue;
                    }
                    anyChecked = true;
                    if (view.m_syncInitialScale)
                    {
                        anyTrue = true;
                    }
                }
                _likelyWorks = anyChecked ? anyTrue : (bool?)null;
                PortalDebug.LogAlways(_likelyWorks == true
                    ? "[WildcardAPortalScalingEngine] at least one portal prefab has ZNetView.m_syncInitialScale set - #146 scale writes are expected to work."
                    : "[WildcardAPortalScalingEngine] no portal prefab has ZNetView.m_syncInitialScale set - #146 scale writes will be silently inert (no error, per the catalog's own citation). Writes are still applied if requested.");
            }
            catch (Exception ex)
            {
                PortalDebug.LogWarning($"[WildcardAPortalScalingEngine] probe failed: {ex.Message}");
            }
        }

        /// <summary>Writes ZDOVars.s_scaleHash. Applies only at the NEXT instantiation of this ZDO on each client (leave-and-return or relog) - never a live-in-place resize.</summary>
        public static bool TrySetScale(ZDO portal, Vector3 scale)
        {
            if (portal == null || !portal.IsValid() || WildcardAConfig.PortalScalingEnabled?.Value == false)
            {
                return false;
            }
            PortalOwnership.ClaimAndWrite(portal, z => z.Set(ZDOVars.s_scaleHash, scale));
            return true;
        }

        public static bool TrySetUniformScale(ZDO portal, float scale) => TrySetScale(portal, new Vector3(scale, scale, scale));
    }
}
