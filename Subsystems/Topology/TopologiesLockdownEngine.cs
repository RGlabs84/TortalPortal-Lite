using System;
using System.Collections.Generic;
using UnityEngine;
using TortalPortalLite.Core;
using TortalPortalLite.Core.Data;
using TortalPortalLite.Subsystems.Foundations;

namespace TortalPortalLite.Subsystems.Topology
{
    /// <summary>
    /// #17 Tag-Scramble Lockdown - a server-ENFORCED portal shutdown, not the client-honoured
    /// `ZoneSystem.SetGlobalKey(GlobalKeys.NoPortals)` (TeleportWorld.Teleport reads that key on the
    /// TRAVELLER'S OWN machine, :143523, and RPC_SetGlobalKey/RPC_RemoveGlobalKey perform zero sender
    /// validation, :116022-116037 - a modified client can clear it with one packet) and not a bare
    /// `SetConnection(Portal, ZDOID.None)` either (vanilla's own pass 2 re-pairs any same-tag
    /// unconnected portal within 5s, :100607-100622).
    ///
    /// The enforced recipe: give every targeted portal a per-portal-UNIQUE invisible tag suffix (via
    /// TopologiesTagShardEngine) so FindRandomUnconnectedPortal's candidate list is permanently empty for
    /// every one of them (:100674-100678 - it returns null rather than ever finding a match), THEN null
    /// the connection. TeleportWorld.Teleport's gate 1 (`if (!TargetFound()) return;`, :143519) is the
    /// ONE of its four gates that is genuinely server-controllable, and a None connection fails it on any
    /// client, modified or not - completely silently (no message, no black screen), which is why this
    /// engine also toasts players on transition (TopologiesConfig.LockdownToastPlayers) rather than
    /// leaving the shutdown looking like a server bug.
    ///
    /// The scramble is derived deterministically from the ZDO's OWN uid every tick (never stored
    /// separately) so re-asserting it is naturally idempotent; the portal's TRUE original tag is stashed
    /// once, in TopologiesKeys.LockdownTagStash (a ZDO key, not a mod-side file keyed by ZDOID - ZDOIDs
    /// renumber on every world load, ZDO.Load :74552, so a ZDOID-keyed file would be worthless after a
    /// restart), and restored byte-for-byte on unlock. Restoring the tag alone (never the connection
    /// directly) is deliberate: whichever engine originally owned that portal's membership (Foundations'
    /// NetworkReassertEngine or this domain's own TopologiesShapeEngine) notices the connection is wrong
    /// on ITS OWN very next tick and repairs it - this engine does not need to know or care whether a
    /// given portal belonged to a declared network before being locked down.
    /// </summary>
    public static class TopologiesLockdownEngine
    {
        private static float _timer;
        private static bool _lastActive;

        public static void OnUpdate(float dt)
        {
            _timer += dt;
            float interval = TopologiesConfig.ShapeReassertSeconds?.Value ?? 2.0f;
            if (_timer < interval)
            {
                return;
            }
            _timer = 0f;

            bool active = TopologiesConfig.LockdownActive?.Value == true;
            if (active != _lastActive)
            {
                ToastTransition(active);
                _lastActive = active;
            }

            if (active)
            {
                Engage();
            }
            else
            {
                Restore();
            }
        }

        private static IEnumerable<PortalRecord> TargetPortals()
        {
            List<TopologyVec3>? declared = TopologiesDefinitions.Current.Lockdown?.Portals;
            if (declared == null || declared.Count == 0)
            {
                foreach (PortalRecord r in PortalCensus.Latest)
                {
                    yield return r;
                }
                yield break;
            }
            foreach (TopologyVec3 pos in declared)
            {
                if (PortalCensus.TryGetByPosition(pos.ToVector3(), out PortalRecord r))
                {
                    yield return r;
                }
            }
        }

        private static void Engage()
        {
            if (ZDOMan.instance == null || !VersionMigration.DestructivePassesAllowed)
            {
                return;
            }

            int budget = TopologiesConfig.MaxWritesPerTick?.Value ?? 50;
            int written = 0;

            foreach (PortalRecord record in TargetPortals())
            {
                if (written >= budget)
                {
                    break;
                }
                ZDO zdo = ZDOMan.instance.GetZDO(record.Uid);
                if (zdo == null || !zdo.IsValid())
                {
                    continue;
                }

                // Stash the true original tag exactly once - never clobber an existing stash with what
                // might already be a scrambled value from an earlier tick this lock-cycle.
                if (zdo.GetString(TopologiesKeys.LockdownTagStash, "").Length == 0)
                {
                    string toStash = record.Tag;
                    PortalOwnership.ClaimAndWrite(zdo, z => z.Set(TopologiesKeys.LockdownTagStash, toStash));
                }

                string scrambled = "<L" + TopologiesTagShardEngine.ShortCode(zdo.m_uid, 7) + ">";
                bool tagWrong = record.Tag != scrambled;
                bool connWrong = record.Connection != ZDOID.None;
                if (!tagWrong && !connWrong)
                {
                    continue;
                }

                try
                {
                    PortalOwnership.ClaimAndWrite(zdo, z =>
                    {
                        if (tagWrong)
                        {
                            z.Set(ZDOVars.s_tag, scrambled);
                        }
                        if (connWrong)
                        {
                            z.SetConnection(ZDOExtraData.ConnectionType.Portal, ZDOID.None);
                        }
                    });
                    written++;
                }
                catch (Exception ex)
                {
                    PortalDebug.LogError($"[TopologiesLockdownEngine] failed to lock {record.Uid}: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }

        private static void Restore()
        {
            if (ZDOMan.instance == null || !VersionMigration.DestructivePassesAllowed)
            {
                return;
            }

            int budget = TopologiesConfig.MaxWritesPerTick?.Value ?? 50;
            int written = 0;

            foreach (PortalRecord record in TargetPortals())
            {
                if (written >= budget)
                {
                    break;
                }
                ZDO zdo = ZDOMan.instance.GetZDO(record.Uid);
                if (zdo == null || !zdo.IsValid())
                {
                    continue;
                }
                string stash = zdo.GetString(TopologiesKeys.LockdownTagStash, "");
                if (stash.Length == 0)
                {
                    continue; // never locked this cycle, or already fully restored
                }

                try
                {
                    PortalOwnership.ClaimAndWrite(zdo, z =>
                    {
                        z.Set(ZDOVars.s_tag, stash);
                        z.Set(TopologiesKeys.LockdownTagStash, "");
                    });
                    written++;
                }
                catch (Exception ex)
                {
                    PortalDebug.LogError($"[TopologiesLockdownEngine] failed to restore {record.Uid}: {ex.GetType().Name}: {ex.Message}");
                }
            }

            // Ask vanilla to re-pair immediately rather than wait up to 5s - Game.ConnectPortals is
            // public and safe to call on demand (:100589, same citation Roulette/#19 uses for a forced
            // re-roll). This whole restore pass runs synchronously within one Update - Unity's
            // single-threaded frame model means vanilla's own 5s-interval ConnectPortalsCoroutine cannot
            // interleave mid-batch, so there is no genuine "prefix ConnectPortals during the restore"
            // race to guard against here the way the catalog's own citation worries about.
            if (written > 0 && Game.instance != null)
            {
                Game.instance.ConnectPortals();
            }
        }

        /// <summary>
        /// Plugin.OnDestroy safety net - the catalog's own explicit warning: "the most dangerous option
        /// in the catalogue... always restore before shutdown". Unlike Restore() above, this scans EVERY
        /// census portal (not just whatever topologies.json currently declares as targets - an admin who
        /// edited that file, or is uninstalling the mod entirely, may have already cleared it) and
        /// restores anything still carrying a non-empty stash, unconditionally, regardless of
        /// VersionMigration.DestructivePassesAllowed - leaving a portal permanently scrambled is a worse
        /// outcome than attempting one last write on a build this mod hasn't verified.
        /// </summary>
        public static void ForceRestoreAll()
        {
            if (ZDOMan.instance == null)
            {
                return;
            }
            int restored = 0;
            foreach (PortalRecord record in PortalCensus.Latest)
            {
                ZDO zdo = ZDOMan.instance.GetZDO(record.Uid);
                if (zdo == null || !zdo.IsValid())
                {
                    continue;
                }
                string stash = zdo.GetString(TopologiesKeys.LockdownTagStash, "");
                if (stash.Length == 0)
                {
                    continue;
                }
                try
                {
                    PortalOwnership.ClaimAndWrite(zdo, z =>
                    {
                        z.Set(ZDOVars.s_tag, stash);
                        z.Set(TopologiesKeys.LockdownTagStash, "");
                    });
                    restored++;
                }
                catch (Exception ex)
                {
                    PortalDebug.LogError($"[TopologiesLockdownEngine] shutdown restore failed for {record.Uid}: {ex.GetType().Name}: {ex.Message}");
                }
            }
            if (restored > 0)
            {
                PortalDebug.LogAlways($"[TopologiesLockdownEngine] restored {restored} portal(s) from lockdown stash on shutdown.");
                if (Game.instance != null)
                {
                    Game.instance.ConnectPortals();
                }
            }
        }

        private static void ToastTransition(bool active)
        {
            if (TopologiesConfig.LockdownToastPlayers?.Value != true)
            {
                return;
            }
            string msg = active ? "The ways are shut." : "The ways are open again.";
            foreach (ConnectedCharacter who in ConnectedCharacters.All())
            {
                PlayerNotify.Toast(who, msg);
            }
        }
    }
}
