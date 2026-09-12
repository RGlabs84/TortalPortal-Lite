using System;
using System.Collections.Generic;
using UnityEngine;
using TortalPortalLite.Core;
using TortalPortalLite.Core.Data;
using TortalPortalLite.Subsystems.Foundations;
using TortalPortalLite.Subsystems.Topology;

namespace TortalPortalLite.Subsystems.PlayerInterface
{
    /// <summary>
    /// #154 The Ledger - signs as a server-written live directory. Any ordinary Sign within radius of a
    /// NAMED portal (an address-book entry, see UxAddressBook) is adopted and rewritten into a paged
    /// destination directory. `Sign.UpdateText` early-returns once `m_lastRevision == zdo.DataRevision`,
    /// so any server write to `s_text` (which bumps DataRevision) is enough - every client in range
    /// repaints within its own 2s `InvokeRepeating` cadence with no further work from this mod.
    ///
    /// `s_author` MUST be written as an empty string, never "host": `Sign.UpdateViewPermission` maps the
    /// literal string "host" to a null `PlatformUserID`, then falls through with no `return` to
    /// `m_author.Value.IsValid` - a `Nullable&lt;T&gt;.Value` access on null, thrown on every viewing
    /// client every 2s (the catalog's own precisely-cited crash). Empty string maps to
    /// `PlatformUserID.None`, which HasValue and is Granted.
    ///
    /// Discovery reuses Topology's `TargetedPrefabDiscovery.SignHashes` (already built in Wave 1) over
    /// `ZdoSpatialQuery.FindNear`, sorted top-to-bottom then by bearing so a stacked wall reads in
    /// building order. If #155 Signs-as-input is also enabled, the LOWEST sign in the stack is reserved
    /// as that engine's own prompt/reply line and is never overwritten here - see UxSignInputEngine.
    /// </summary>
    public static class UxLedgerEngine
    {
        private static readonly Dictionary<ZDOID, int> _pageByPortal = new Dictionary<ZDOID, int>();
        private static float _timer;

        public static void OnUpdate(float dt)
        {
            if (UxConfig.Enabled?.Value == false || UxConfig.LedgerEnabled?.Value == false)
            {
                return;
            }
            _timer += dt;
            float interval = UxConfig.LedgerRewriteSeconds?.Value ?? 10f;
            if (_timer < interval)
            {
                return;
            }
            _timer = 0f;
            Rewrite();
        }

        /// <summary>Explicit page pin from #155's sign-input grammar ("page N") - overrides the auto-advance below.</summary>
        public static void SetPage(ZDOID portalUid, int zeroBasedPage) => _pageByPortal[portalUid] = Math.Max(0, zeroBasedPage);

        /// <summary>The lowest (input/reply) sign at a portal, if any - #155 owns writing to it and must never be double-written by this engine.</summary>
        public static bool TryGetInputSign(PortalRecord portal, out ZDO inputSign)
        {
            List<ZDO> signs = DiscoverSigns(portal.Position);
            inputSign = null;
            if (signs.Count == 0)
            {
                return false;
            }
            inputSign = signs[signs.Count - 1];
            return true;
        }

        private static void Rewrite()
        {
            foreach (UxAddressBook.AddressEntry entry in UxAddressBook.Ordered())
            {
                try
                {
                    RewriteFor(entry.Record);
                }
                catch (Exception ex)
                {
                    PortalDebug.LogError($"[UxLedgerEngine] rewrite failed for '{entry.Name}': {ex.Message}");
                }
            }
        }

        private static List<ZDO> DiscoverSigns(Vector3 portalPos)
        {
            float radius = UxConfig.LedgerSignRadius?.Value ?? 8f;
            List<ZDO> nearby = ZdoSpatialQuery.FindNear(portalPos, radius);
            var signs = new List<ZDO>();
            foreach (ZDO z in nearby)
            {
                if (TargetedPrefabDiscovery.IsSign(z.GetPrefab()))
                {
                    signs.Add(z);
                }
            }
            signs.Sort((a, b) =>
            {
                float ay = a.GetPosition().y, by = b.GetPosition().y;
                if (Mathf.Abs(ay - by) > 0.1f)
                {
                    return by.CompareTo(ay); // higher first
                }
                return Bearing(portalPos, a.GetPosition()).CompareTo(Bearing(portalPos, b.GetPosition()));
            });
            return signs;
        }

        private static void RewriteFor(PortalRecord portal)
        {
            List<ZDO> signs = DiscoverSigns(portal.Position);
            if (signs.Count == 0)
            {
                return;
            }

            bool reserveInputSign = UxConfig.SignInputEnabled?.Value == true && signs.Count > 1;
            int outputCount = reserveInputSign ? signs.Count - 1 : signs.Count;
            if (outputCount <= 0)
            {
                return; // the only sign here is #155's own prompt line
            }

            List<UxAddressBook.AddressEntry> all = UxAddressBook.Ordered();
            int perPage = Math.Max(1, outputCount - (outputCount > 1 ? 1 : 0)); // reserve line 0 for the header when there's room
            bool hasHeaderLine = outputCount > 1;
            int pageCount = Math.Max(1, (int)Math.Ceiling(all.Count / (double)perPage));

            if (!_pageByPortal.TryGetValue(portal.Uid, out int page))
            {
                page = 0;
            }
            page = pageCount == 0 ? 0 : ((page % pageCount) + pageCount) % pageCount;

            var lines = new List<string>();
            if (hasHeaderLine)
            {
                lines.Add($"PORTALS {page + 1}/{pageCount} ({all.Count})");
            }
            int start = page * perPage;
            for (int i = 0; i < perPage && start + i < all.Count; i++)
            {
                UxAddressBook.AddressEntry e = all[start + i];
                float dist = Vector3.Distance(portal.Position, e.Record.Position);
                string marker = e.Record.Uid == portal.Uid ? "HOME" : (portal.Connection == e.Record.Uid ? "->" : "");
                lines.Add($"{start + i + 1} {Truncate(e.Name, 12)} {dist:0}m {marker}");
            }

            for (int i = 0; i < outputCount; i++)
            {
                string text = i < lines.Count ? lines[i] : "";
                WriteSign(signs[i], text);
            }

            // Auto-advance so, left alone, a multi-page directory eventually shows everything - #155's
            // "page N" command (via SetPage) simply overrides whatever this produces next cycle.
            _pageByPortal[portal.Uid] = page + 1;
        }

        private static void WriteSign(ZDO sign, string text)
        {
            string current = sign.GetString(ZDOVars.s_text, "");
            if (string.Equals(current, text, StringComparison.Ordinal))
            {
                return; // avoid needless ForceSendZDO churn when nothing actually changed
            }
            PortalOwnership.ClaimAndWrite(sign, z =>
            {
                z.Set(ZDOVars.s_text, text);
                z.Set(ZDOVars.s_author, ""); // NEVER "host" - see class doc comment
            });
        }

        private static float Bearing(Vector3 origin, Vector3 point)
        {
            Vector3 d = point - origin;
            return Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg;
        }

        private static string Truncate(string s, int n) => string.IsNullOrEmpty(s) || s.Length <= n ? s : s.Substring(0, n);
    }
}
