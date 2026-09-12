using System.Collections.Generic;
using UnityEngine;
using TortalPortalLite.Core;
using TortalPortalLite.Core.Data;
using TortalPortalLite.Core.Hooks;
using TortalPortalLite.Subsystems.Foundations;

namespace TortalPortalLite.Subsystems.Enforcement
{
    /// <summary>
    /// #111 Force-Disconnect Lockdown. Nulls every in-scope portal's Portal connection server-side and
    /// suppresses vanilla's own 5s re-pairing, leaving even a fully hacked client with no destination
    /// coordinate to travel to. Every other reason-scoped lockdown engine in this domain
    /// (LockdownScheduleEngine, LockdownRaidGeofenceEngine, LockdownRulesetEngine) calls into this one
    /// rather than re-deriving the disconnect recipe - this is Lockdown's own equivalent of
    /// Foundations' NetworkReassertEngine ("ops is the architecture, not a domain").
    ///
    /// Three defences from the catalog's own howItWorks, in the order it names them:
    ///  (1) Vanilla re-pairing (Game.ConnectPortals phase 2) - defended by registering on the existing
    ///      FindRandomUnconnectedPortalHook (override-capable prefix) to decline any candidate this engine
    ///      currently has vaulted/locked, exactly the shape TargetedPhantomPortalFactory already uses to
    ///      keep phantoms out of random pairing.
    ///  (2) Ownership theft via ZDOMan.ReleaseNearbyZDOS - the catalog's own prescribed defence is a
    ///      prefix on ZDO.SetOwner, which Core/Hooks/ does not broker:
    ///      NEEDS NEW HOOK BROKER on ZDO.SetOwner(long): purpose - refuse to hand a locked portal's
    ///      ownership to a nearby peer while its connection is meant to stay None. Mitigated today by (3).
    ///  (3) Client write-back via Game.RPC_SetConnection - not broker-covered (SetConnectionHook is
    ///      postfix/observe-only by design), so this engine relies on the SAME re-assert discipline
    ///      Foundations' NetworkReassertEngine and every Wave-1 engine registered on ConnectPortalsHook
    ///      already use: a postfix correction, synchronous in the same frame vanilla's own pass runs,
    ///      that re-nulls any locked portal whose connection somehow became non-None. Because this runs
    ///      EVERY ConnectPortals pass (vanilla's own 5s cadence) and ALSO every OnUpdate tick via
    ///      SetConnectionHook's postfix-triggered immediate correction, the unlocked window is at most one
    ///      frame - functionally equivalent to the veto the catalog asks for, without a second Harmony
    ///      patch on a hot vanilla method.
    /// </summary>
    public static class LockdownForceDisconnectEngine
    {
        private static bool _hooksInstalled;

        public static void Initialize()
        {
            if (_hooksInstalled)
            {
                return;
            }
            _hooksInstalled = true;
            FindRandomUnconnectedPortalHook.Register(50, DeclineLockedCandidates);
            ConnectPortalsHook.RegisterPostfix(50, ReassertAfterVanillaPass);
            SetConnectionHook.RegisterPostfix(50, OnSetConnectionObserved);
        }

        /// <summary>
        /// Vaults every currently-connected pair once (snapshot-and-fsync FIRST, the catalog's own
        /// "never interleave" rule), then nulls connections. Idempotent per reason - a portal already
        /// vaulted under this reason is left alone rather than re-vaulted over its own already-locked state.
        /// </summary>
        public static void EngageScope(IEnumerable<PortalRecord> scope, string reason)
        {
            if (LockdownInvariantHarness.Panicked)
            {
                return; // Fail open - never start a new lock while the harness has judged this domain's write paths unsafe.
            }
            var list = new List<PortalRecord>(scope);
            bool anyNewVault = false;
            foreach (PortalRecord rec in list)
            {
                if (rec.Connection != ZDOID.None && PortalCensus.TryGet(rec.Connection, out PortalRecord partner))
                {
                    int before = LockdownVault.Count;
                    LockdownVault.Record(rec.Position, partner.Position, rec.Tag, reason);
                    anyNewVault |= LockdownVault.Count != before;
                }
            }
            if (anyNewVault)
            {
                LockdownVault.Save();
            }

            if (ZDOMan.instance == null)
            {
                return;
            }
            foreach (PortalRecord rec in list)
            {
                if (!LockdownWriteBudget.TryConsume())
                {
                    break; // #124: remaining portals pick up on the next tick's reconcile pass instead of spiking this frame.
                }
                ZDO zdo = ZDOMan.instance.GetZDO(rec.Uid);
                if (zdo == null || !zdo.IsValid() || zdo.GetConnectionZDOID(ZDOExtraData.ConnectionType.Portal) == ZDOID.None)
                {
                    continue;
                }
                PortalOwnership.ClaimAndWrite(zdo, z => z.SetConnection(ZDOExtraData.ConnectionType.Portal, ZDOID.None));
                LockdownWriteBudget.ForceSendNearbyOnly(zdo.m_uid, rec.Position);
            }
        }

        /// <summary>Restores every link vaulted under this reason and forgets those vault records. Safe to call even if nothing was ever engaged under this reason.</summary>
        public static void DisengageScope(string reason)
        {
            if (ZDOMan.instance == null)
            {
                return;
            }
            var records = new List<VaultRecord>(LockdownVault.RecordsForReason(reason));
            foreach (VaultRecord rec in records)
            {
                bool okA = PortalCensus.TryGetByPosition(rec.PosA, out PortalRecord a);
                bool okB = PortalCensus.TryGetByPosition(rec.PosB, out PortalRecord b);
                if (okA && okB)
                {
                    ZDO zdoA = ZDOMan.instance.GetZDO(a.Uid);
                    ZDO zdoB = ZDOMan.instance.GetZDO(b.Uid);
                    if (zdoA != null && zdoA.IsValid() && zdoB != null && zdoB.IsValid())
                    {
                        PortalOwnership.ClaimAndWrite(zdoA, z => z.SetConnection(ZDOExtraData.ConnectionType.Portal, zdoB.m_uid));
                        PortalOwnership.ClaimAndWrite(zdoB, z => z.SetConnection(ZDOExtraData.ConnectionType.Portal, zdoA.m_uid));
                    }
                }
                LockdownVault.Forget(rec.PosA, reason);
            }
        }

        /// <summary>True if this position is one end of any currently force-disconnected link, under any reason.</summary>
        public static bool IsLocked(Vector3 pos) => LockdownVault.IsLocked(pos);

        // ------------------------------------------------------------------------------------- defences

        private static bool DeclineLockedCandidates(List<ZDO> portals, ZDO skip, string tag, out ZDO? result)
        {
            result = null;
            // Only override when the candidate vanilla WOULD have picked is itself locked - every other
            // call declines (returns false) and vanilla's own random selection runs unmodified, the same
            // discipline TargetedPhantomPortalFactory's own exclusion filter documents.
            foreach (ZDO portal in portals)
            {
                if (portal == skip || portal.GetString(ZDOVars.s_tag) != tag)
                {
                    continue;
                }
                if (portal.GetConnectionZDOID(ZDOExtraData.ConnectionType.Portal) != ZDOID.None)
                {
                    continue;
                }
                if (LockdownVault.IsLocked(portal.GetPosition()))
                {
                    // At least one candidate is locked; re-scan for a non-locked one instead of letting
                    // vanilla's own unfiltered random pick possibly choose it.
                    return TryPickNonLocked(portals, skip, tag, out result);
                }
            }
            return false;
        }

        private static bool TryPickNonLocked(List<ZDO> portals, ZDO skip, string tag, out ZDO? result)
        {
            var candidates = new List<ZDO>();
            foreach (ZDO portal in portals)
            {
                if (portal == skip || portal.GetString(ZDOVars.s_tag) != tag)
                {
                    continue;
                }
                if (portal.GetConnectionZDOID(ZDOExtraData.ConnectionType.Portal) != ZDOID.None)
                {
                    continue;
                }
                if (LockdownVault.IsLocked(portal.GetPosition()))
                {
                    continue;
                }
                candidates.Add(portal);
            }
            result = candidates.Count > 0 ? candidates[Random.Range(0, candidates.Count)] : null;
            return true;
        }

        private static void ReassertAfterVanillaPass()
        {
            RenullDrifted();
        }

        private static void OnSetConnectionObserved(ZDO portal, ZDOID connection, bool forceImmediateConnection)
        {
            if (portal == null || !portal.IsValid() || connection == ZDOID.None)
            {
                return;
            }
            if (LockdownVault.IsLocked(portal.GetPosition()))
            {
                PortalOwnership.ClaimAndWrite(portal, z => z.SetConnection(ZDOExtraData.ConnectionType.Portal, ZDOID.None));
            }
        }

        private static void RenullDrifted()
        {
            if (ZDOMan.instance == null)
            {
                return;
            }
            foreach (PortalRecord rec in PortalCensus.Latest)
            {
                if (rec.Connection == ZDOID.None || !LockdownVault.IsLocked(rec.Position))
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
                PortalOwnership.ClaimAndWrite(zdo, z => z.SetConnection(ZDOExtraData.ConnectionType.Portal, ZDOID.None));
                PortalDebug.LogWarning($"[LockdownForceDisconnectEngine] re-nulled drifted connection on locked portal {rec.Uid} at {rec.Position:F0}.");
            }
        }
    }
}
