using System;
using System.Collections.Generic;
using TortalPortalLite.Core;
using TortalPortalLite.Core.Data;
using TortalPortalLite.Subsystems.Foundations;

namespace TortalPortalLite.Subsystems.Enforcement
{
    /// <summary>
    /// #119 Per-Network Quarantine-Tag Lockdown. Locks a named tag-group by rewriting every member's
    /// `s_tag` to a per-portal-UNIQUE quarantine string, so vanilla's OWN reconciler tears the links down
    /// and can never rebuild them - no Game.ConnectPortals suppression needed at all, a major robustness
    /// win over the connection-nulling approach (#111): Game.ConnectPortals phase 1 sees each portal's
    /// partner carrying a different s_tag and clears the connection within 5s, and phase 2 can never
    /// re-pair them because FindRandomUnconnectedPortal requires exact ordinal tag equality and no two
    /// quarantine strings match.
    ///
    /// Uses Core/Data/TagCodec.cs's own reserved '!' sigil ("admin/lockdown-only marker... never
    /// player-writable by convention") for exactly its documented purpose, rather than inventing a new
    /// one - satisfying TagCodec's own "do not add a new sigil yourself" rule because '!' already exists
    /// and is already earmarked for this domain.
    ///
    /// Tag writes do NOT set DirtyPortalObjects on their own (only SetConnection/AddIfPortal/
    /// HandleDestroyedZDO do - catalog #119's own explicit warning), so every write here calls the public
    /// ZDOMan.SetDirtyPortals() explicitly or the rename is silently lost at the next autosave.
    /// </summary>
    public static class LockdownQuarantineTagEngine
    {
        /// <summary>Instant teardown variant: also forces both ends' connections to None in the same pass, rather than waiting up to ~10s for vanilla's own phase-1 teardown near an online peer (catalog #119's own citation on the deferred SetConnection(None) branch).</summary>
        public static void EngageTag(string tag, string reason, bool instantTeardown = true)
        {
            if (string.IsNullOrEmpty(tag) || ZDOMan.instance == null)
            {
                return;
            }
            var members = new List<PortalRecord>();
            foreach (PortalRecord rec in PortalCensus.Latest)
            {
                if (rec.Tag == tag)
                {
                    members.Add(rec);
                }
            }
            EngageMembers(members, reason, instantTeardown);
        }

        /// <summary>Quarantines every currently-managed (non-empty-tag) portal in the given scope, grouped by tag - used by the Schedule/Ruleset engines' "TagQuarantine" mode as a blanket blackout.</summary>
        public static void EngageAllManaged(string reason)
        {
            EngageMembers(PortalCensus.Latest, reason, instantTeardown: true);
        }

        private static void EngageMembers(IEnumerable<PortalRecord> members, string reason, bool instantTeardown)
        {
            if (LockdownInvariantHarness.Panicked)
            {
                return;
            }
            bool anyVaulted = false;
            foreach (PortalRecord rec in members)
            {
                if (string.IsNullOrEmpty(rec.Tag) || TagCodec.HasSigil(rec.Tag, '!'))
                {
                    continue; // untagged or already quarantined
                }
                int before = LockdownVault.Count;
                LockdownVault.Record(rec.Position, rec.Position, rec.Tag, reason);
                anyVaulted |= LockdownVault.Count != before;
            }
            if (anyVaulted)
            {
                LockdownVault.Save();
            }

            bool dirtied = false;
            foreach (PortalRecord rec in members)
            {
                if (string.IsNullOrEmpty(rec.Tag) || TagCodec.HasSigil(rec.Tag, '!'))
                {
                    continue;
                }
                if (!LockdownWriteBudget.TryConsume())
                {
                    break;
                }
                ZDO zdo = ZDOMan.instance.GetZDO(rec.Uid);
                if (zdo == null || !zdo.IsValid())
                {
                    continue;
                }
                string quarantineTag = QuarantineTagFor(rec.Position);
                PortalOwnership.ClaimAndWrite(zdo, z =>
                {
                    z.Set(ZDOVars.s_tag, quarantineTag);
                    if (instantTeardown)
                    {
                        z.SetConnection(ZDOExtraData.ConnectionType.Portal, ZDOID.None);
                    }
                });
                dirtied = true;
                LockdownWriteBudget.ForceSendNearbyOnly(zdo.m_uid, rec.Position);
            }
            if (dirtied)
            {
                ZDOMan.instance.SetDirtyPortals(); // #119's own warning: a tag-only write never dirties the portal chunk by itself.
            }
        }

        /// <summary>Restores every quarantined tag (any reason) back to its original text and forgets the vault records. Does NOT re-establish connections - vanilla's own phase-2 re-pairs same-tag portals within 5s once tags match again (catalog's own documented caveat: a 3+ member group may come back re-shuffled unless the vault also re-asserts connections explicitly).</summary>
        public static void DisengageAllManaged(string reason) => Disengage(reason);

        public static void DisengageTag(string reason) => Disengage(reason);

        private static void Disengage(string reason)
        {
            if (ZDOMan.instance == null)
            {
                return;
            }
            var records = new List<VaultRecord>(LockdownVault.RecordsForReason(reason));
            bool dirtied = false;
            foreach (VaultRecord rec in records)
            {
                if (PortalCensus.TryGetByPosition(rec.PosA, out PortalRecord live))
                {
                    ZDO zdo = ZDOMan.instance.GetZDO(live.Uid);
                    if (zdo != null && zdo.IsValid() && !string.IsNullOrEmpty(rec.TagBefore))
                    {
                        PortalOwnership.ClaimAndWrite(zdo, z => z.Set(ZDOVars.s_tag, rec.TagBefore));
                        dirtied = true;
                    }
                }
                LockdownVault.Forget(rec.PosA, reason);
            }
            if (dirtied)
            {
                ZDOMan.instance.SetDirtyPortals();
                // Immediate re-pair rather than waiting up to 5s for vanilla's own next ConnectPortals tick.
                Game.instance?.ConnectPortals();
            }
        }

        private static string QuarantineTagFor(UnityEngine.Vector3 pos)
        {
            string prefixWithSigil = LockdownConfig.QuarantineMarkerPrefix?.Value ?? "!lk";
            if (string.IsNullOrEmpty(prefixWithSigil) || prefixWithSigil[0] != '!')
            {
                prefixWithSigil = "!" + prefixWithSigil;
            }
            int budget = TagCodec.MaxTagLength - prefixWithSigil.Length;
            if (budget <= 0)
            {
                return prefixWithSigil.Substring(0, TagCodec.MaxTagLength);
            }
            uint hash = unchecked((uint)pos.GetHashCode());
            string suffix = hash.ToString("x").Substring(0, Math.Min(budget, hash.ToString("x").Length));
            string tag = prefixWithSigil + suffix;
            return tag.Length > TagCodec.MaxTagLength ? tag.Substring(0, TagCodec.MaxTagLength) : tag;
        }
    }
}
