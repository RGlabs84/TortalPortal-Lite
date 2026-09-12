using System.Collections.Generic;
using TortalPortalLite.Core;
using TortalPortalLite.Core.Data;
using TortalPortalLite.Core.Hooks;
using TortalPortalLite.Subsystems.Foundations;

namespace TortalPortalLite.Subsystems.Enforcement
{
    /// <summary>
    /// #125 Invariant Assertion Harness and Panic Restore. A runtime self-check verifying a fixed list of
    /// never-violate invariants and, on repeated/unrepairable violations, restoring the vault and
    /// setting a STICKY panic flag every write-capable engine in this domain checks before acting - "fail
    /// open (portals work)" is always the right failure direction for a portal mod.
    ///
    /// Implements the invariants that are genuinely checkable from this codebase's own primitives:
    ///  I2 LOCK COMPLETENESS - fast tick, over PortalCensus.
    ///  I3 NO SELF-LOOP - fast tick.
    ///  I5 NO DUPLICATE PORTAL ENTRY - slow tick, GetPortalList().Count vs distinct Uid count; NO REPAIR
    ///     (the catalog's own words: this is unrecoverable m_portalObjects corruption, detect-only).
    ///  I9 SINGLE WRITER (approximated) - tamper-rate tracking below doubles as a "something keeps
    ///     rewriting this locked portal" signal, which is the practically useful half of I9 without
    ///     needing a genuine per-tick write-attribution ledger across every engine in the mod.
    ///  I10 NO OUT-OF-RANGE COORDINATE - slow tick, ZoneSystem's own ~16320m sector clamp radius.
    /// I1 (orphaned vault), I4 (dangling target) and I6 (stale ZDO refs) are already structurally
    /// impossible in this codebase: every lockdown engine here re-resolves ZDOs through
    /// ZDOMan.GetZDO/PortalCensus every tick rather than caching ZDO references across ticks, and
    /// LockdownVault.RestoreAll already discards (never trusts) an unresolvable record. I7 (dirty-flag
    /// discipline) and I8 (target-bit) are asserted implicitly by the engines that own each write path
    /// rather than re-derived here from private ZDOExtraData internals this codebase has no clean way to
    /// inspect.
    /// </summary>
    public static class LockdownInvariantHarness
    {
        private static float _fastTimer;
        private static float _slowTimer;
        private static bool _hooksInstalled;

        public static bool Panicked { get; private set; }

        // Tamper tracking: relink events per portal within a rolling ~60s window.
        private static readonly Dictionary<ZDOID, List<float>> _relinkTimestamps = new Dictionary<ZDOID, List<float>>();
        private static float _clock;

        public static void Initialize()
        {
            if (_hooksInstalled)
            {
                return;
            }
            _hooksInstalled = true;
            SetConnectionHook.RegisterPostfix(900, OnSetConnectionObserved); // low priority: observe after ForceDisconnectEngine's own correction already ran
        }

        public static void OnUpdate(float dt)
        {
            _clock += dt;
            if (Panicked)
            {
                return; // Sticky - stays panicked until an operator restarts the plugin/world. Nothing further to check while every write path should already be standing down.
            }

            _fastTimer += dt;
            if (_fastTimer >= (LockdownConfig.InvariantFastCheckSeconds?.Value ?? 1f))
            {
                _fastTimer = 0f;
                FastChecks();
            }

            _slowTimer += dt;
            if (_slowTimer >= (LockdownConfig.InvariantSlowCheckSeconds?.Value ?? 30f))
            {
                _slowTimer = 0f;
                SlowChecks();
            }
        }

        private static void OnSetConnectionObserved(ZDO portal, ZDOID connection, bool forceImmediateConnection)
        {
            if (portal == null || !portal.IsValid() || connection == ZDOID.None)
            {
                return;
            }
            if (!LockdownVault.IsLocked(portal.GetPosition()))
            {
                return;
            }
            if (!_relinkTimestamps.TryGetValue(portal.m_uid, out var list))
            {
                list = new List<float>();
                _relinkTimestamps[portal.m_uid] = list;
            }
            list.Add(_clock);
            list.RemoveAll(t => _clock - t > 60f);

            int threshold = LockdownConfig.InvariantTamperThreshold?.Value ?? 8;
            if (list.Count < threshold)
            {
                return;
            }
            list.Clear();

            string actor = SenderContext.HostNameOf(SenderContext.Current) ?? "unknown";
            PortalDebug.LogWarning($"[SECURITY:LockTamper] {actor} - portal {portal.m_uid} at {portal.GetPosition():F0} relinked {threshold}+ times in 60s.");

            if (LockdownConfig.InvariantKickOnTamper?.Value == true && SenderContext.Current != null)
            {
                try
                {
                    string hostName = SenderContext.HostNameOf(SenderContext.Current);
                    if (!string.IsNullOrEmpty(hostName))
                    {
                        ZNet.instance?.Kick(hostName);
                    }
                }
                catch (System.Exception ex)
                {
                    PortalDebug.LogWarning($"[LockdownInvariantHarness] kick failed: {ex.Message}");
                }
            }
        }

        private static void FastChecks()
        {
            if (ZDOMan.instance == null)
            {
                return;
            }
            int repairs = 0;
            foreach (PortalRecord rec in PortalCensus.Latest)
            {
                // I3: self-loop.
                if (rec.Connection == rec.Uid)
                {
                    ZDO zdo = ZDOMan.instance.GetZDO(rec.Uid);
                    if (zdo != null && zdo.IsValid())
                    {
                        PortalOwnership.ClaimAndWrite(zdo, z => z.SetConnection(ZDOExtraData.ConnectionType.Portal, ZDOID.None));
                        repairs++;
                        PortalDebug.LogWarning($"[LockdownInvariantHarness] I3 violated: {rec.Uid} was connected to itself - repaired (nulled).");
                    }
                }

                // I2: lock completeness.
                if (LockdownVault.IsLocked(rec.Position) && rec.Connection != ZDOID.None)
                {
                    ZDO zdo = ZDOMan.instance.GetZDO(rec.Uid);
                    if (zdo != null && zdo.IsValid())
                    {
                        PortalOwnership.ClaimAndWrite(zdo, z => z.SetConnection(ZDOExtraData.ConnectionType.Portal, ZDOID.None));
                        repairs++;
                    }
                }
            }
            if (repairs > 20)
            {
                // A single tick repairing this many locked portals at once smells like a systemic problem
                // (e.g. Game.ConnectPortals racing this engine faster than expected), not routine drift.
                PortalDebug.LogWarning($"[LockdownInvariantHarness] {repairs} repair(s) in one fast-check tick - unusually high, investigate if this recurs.");
            }
        }

        private static void SlowChecks()
        {
            if (ZDOMan.instance == null)
            {
                return;
            }
            try
            {
                List<ZDO> list = ZDOMan.instance.GetPortalList();
                var seen = new HashSet<ZDOID>();
                int duplicates = 0;
                int outOfRange = 0;
                foreach (ZDO zdo in list)
                {
                    if (zdo == null || !zdo.IsValid())
                    {
                        continue;
                    }
                    if (!seen.Add(zdo.m_uid))
                    {
                        duplicates++;
                        continue;
                    }
                    UnityEngine.Vector3 pos = zdo.GetPosition();
                    if (UnityEngine.Mathf.Abs(pos.x) > 16000f || UnityEngine.Mathf.Abs(pos.z) > 16000f)
                    {
                        outOfRange++;
                    }
                }

                if (duplicates > 0)
                {
                    // I5: NO REPAIR by the catalog's own explicit statement - this is unrecoverable
                    // m_portalObjects corruption; detect and refuse to make it worse only.
                    PortalDebug.LogError($"[LockdownInvariantHarness] I5 violated: {duplicates} duplicate portal registry entr(y/ies) detected - UNREPAIRABLE, logging only.");
                    Panic($"I5 duplicate portal entries x{duplicates}");
                }
                if (outOfRange > 0)
                {
                    PortalDebug.LogWarning($"[LockdownInvariantHarness] I10: {outOfRange} portal(s) beyond the ~16000m sector-clamp radius - these collide with Sector 0, the same bucket the server's own pinned reference position maps to.");
                }
            }
            catch (System.Exception ex)
            {
                PortalDebug.LogError($"[LockdownInvariantHarness] slow check failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static void Panic(string reason)
        {
            if (Panicked)
            {
                return;
            }
            Panicked = true;
            PortalDebug.LogError($"[TortalPortalLite] PANIC: {reason} - lockdown disabled and wiring restored. Clears only on plugin/world restart.");
            LockdownVault.EmergencyRestoreAllNow();
            if (DiscordWebhook.IsConfigured())
            {
                DiscordWebhook.Send($"PANIC: {reason} - TortalPortalLite lockdown disabled and wiring restored.");
            }
        }
    }
}
