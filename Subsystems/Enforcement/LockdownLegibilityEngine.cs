using System.Collections.Generic;
using UnityEngine;
using TortalPortalLite.Core;
using TortalPortalLite.Core.Data;
using TortalPortalLite.Subsystems.Foundations;

namespace TortalPortalLite.Subsystems.Enforcement
{
    /// <summary>
    /// #257 Lockdown Legibility Rule + #258 Locked-Gate Overlay - one file, since #258 is literally "the
    /// mechanics behind #257". Any key-only (Tier-A) lockdown running longer than
    /// LockdownConfig.LegibilityThresholdSeconds must carry an in-world signal - with only NoPortals set,
    /// a vanilla portal is indistinguishable from a working one (hover text and the glow both come from
    /// HaveTarget()/TargetFound(), neither of which reads NoPortals; the only feedback is $msg_blocked
    /// AFTER a failed walk-in). A Tier-B force-disconnect already flips hover to $piece_portal_unconnected
    /// for free - this engine's real job is dressing up a Tier-A (key-only) lockdown that would otherwise
    /// look like a bug.
    ///
    /// #258's mechanics, exactly as the catalog specifies: group portals by their CURRENT shared tag,
    /// rewrite the WHOLE group atomically to tag+marker in one pass (same-group members keep identical
    /// strings so Game.ConnectPortals phase 1 sees no mismatch and an existing link survives), vaulted by
    /// original tag for restore; plus a budgeted per-nearby-peer RPC_DamageText loop reading the marker
    /// text, reusing LockdownAnnouncementEngine.SendDamageText (this file's own citation of DamageText's
    /// 1.5s/30m constants).
    /// </summary>
    public static class LockdownLegibilityEngine
    {
        private const string Reason = "legibility";
        private static float _keyOnlyEngagedSeconds;
        private static bool _wasKeyOnlyActive;

        private static float _overlayTimer;

        public static void OnUpdate(float dt)
        {
            if (LockdownConfig.LegibilityEnabled?.Value != true)
            {
                if (_wasKeyOnlyActive)
                {
                    Disengage();
                }
                return;
            }

            // A "Tier-A, key-only" lockdown running with no Tier-B signal of its own: NoPortals/NoBossPortals
            // set, but nothing has force-disconnected or quarantined anything (no vault records at all).
            bool keyOnlyActive = (LockdownGlobalKeyEngine.NoPortalsWanted || LockdownGlobalKeyEngine.NoBossPortalsWanted) && LockdownVault.Count == 0;

            if (keyOnlyActive)
            {
                _keyOnlyEngagedSeconds += dt;
            }
            else
            {
                if (_wasKeyOnlyActive)
                {
                    Disengage();
                }
                _keyOnlyEngagedSeconds = 0f;
            }
            _wasKeyOnlyActive = keyOnlyActive;

            float threshold = LockdownConfig.LegibilityThresholdSeconds?.Value ?? 30f;
            if (keyOnlyActive && _keyOnlyEngagedSeconds >= threshold && LockdownVault.Count == 0)
            {
                Engage();
            }

            if (LockdownVault.Count > 0 || AnyLegibilityRecords())
            {
                RunOverlay(dt);
            }
        }

        private static bool AnyLegibilityRecords()
        {
            foreach (VaultRecord _ in LockdownVault.RecordsForReason(Reason))
            {
                return true;
            }
            return false;
        }

        /// <summary>Escalates the invisible Tier-A lockdown to a legible one: same-tag-group-atomic quarantine tag rewrite (never touches connections - #111 already owns that mechanism for anything that genuinely needs it).</summary>
        private static void Engage()
        {
            if (ZDOMan.instance == null)
            {
                return;
            }
            string marker = LockdownConfig.LegibilityTagMarker?.Value ?? " [X]";
            var byTag = new Dictionary<string, List<PortalRecord>>();
            foreach (PortalRecord rec in PortalCensus.Latest)
            {
                if (!byTag.TryGetValue(rec.Tag ?? "", out var list))
                {
                    list = new List<PortalRecord>();
                    byTag[rec.Tag ?? ""] = list;
                }
                list.Add(rec);
            }

            bool anyDirty = false;
            foreach (var kvp in byTag)
            {
                if (kvp.Key.EndsWith(marker))
                {
                    continue; // already marked
                }
                string desiredTag = kvp.Key + marker;
                if (desiredTag.Length > TagCodec.MaxTagLength)
                {
                    continue; // Not enough room in the 10-char budget for this base tag - skip, don't corrupt it.
                }
                foreach (PortalRecord rec in kvp.Value)
                {
                    if (!LockdownWriteBudget.TryConsume())
                    {
                        continue;
                    }
                    ZDO zdo = ZDOMan.instance.GetZDO(rec.Uid);
                    if (zdo == null || !zdo.IsValid())
                    {
                        continue;
                    }
                    LockdownVault.Record(rec.Position, rec.Position, kvp.Key, Reason);
                    PortalOwnership.ClaimAndWrite(zdo, z => z.Set(ZDOVars.s_tag, desiredTag));
                    anyDirty = true;
                }
            }
            if (anyDirty)
            {
                ZDOMan.instance.SetDirtyPortals();
                LockdownVault.Save();
                PortalDebug.LogAlways("[LockdownLegibilityEngine] escalated an invisible key-only lockdown to a legible one (tag marker applied).");
            }
        }

        private static void Disengage()
        {
            if (ZDOMan.instance == null)
            {
                return;
            }
            var records = new List<VaultRecord>(LockdownVault.RecordsForReason(Reason));
            bool anyDirty = false;
            foreach (VaultRecord rec in records)
            {
                if (PortalCensus.TryGetByPosition(rec.PosA, out PortalRecord live))
                {
                    ZDO zdo = ZDOMan.instance.GetZDO(live.Uid);
                    if (zdo != null && zdo.IsValid())
                    {
                        PortalOwnership.ClaimAndWrite(zdo, z => z.Set(ZDOVars.s_tag, rec.TagBefore));
                        anyDirty = true;
                    }
                }
                LockdownVault.Forget(rec.PosA, Reason);
            }
            if (anyDirty)
            {
                ZDOMan.instance.SetDirtyPortals();
            }
        }

        private static void RunOverlay(float dt)
        {
            _overlayTimer += dt;
            float interval = LockdownConfig.OverlayIntervalSeconds?.Value ?? 1.2f;
            if (_overlayTimer < interval)
            {
                return;
            }
            _overlayTimer = 0f;

            float radius = LockdownConfig.OverlayRadius?.Value ?? 30f;
            string text = LockdownConfig.OverlayText?.Value ?? "Portal locked";

            foreach (PortalRecord rec in PortalCensus.Latest)
            {
                if (!LockdownVault.IsLocked(rec.Position))
                {
                    continue;
                }
                LockdownAnnouncementEngine.FloatingTextNear(rec.Position, radius, text, DamageText.TextType.Bonus);
            }
        }
    }
}
