using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using UnityEngine;
using TortalPortalLite.Core;
using TortalPortalLite.Core.Data;
using TortalPortalLite.Subsystems.Foundations;

namespace TortalPortalLite.Subsystems.Enforcement
{
    /// <summary>One recorded link, keyed by rounded world position on BOTH ends - never by ZDOID (catalog #112's own citation: ZDO.Load re-mints m_uid every world load via ZDOID.m_loadID, exactly the bug TortalPortal's own Favorites.cs post-mortem documents).</summary>
    public sealed class VaultRecord
    {
        public float Ax, Ay, Az;
        public float Bx, By, Bz;
        public string TagBefore = "";
        public string Reason = "";
        public long LockedAtUtcTicks;

        [JsonIgnore] public Vector3 PosA => new Vector3(Ax, Ay, Az);
        [JsonIgnore] public Vector3 PosB => new Vector3(Bx, By, Bz);
    }

    public sealed class VaultFile
    {
        public int SchemaVersion = 1;
        public List<VaultRecord> Records = new List<VaultRecord>();
    }

    /// <summary>
    /// #112 Connection Vault and Crash Recovery. A position-keyed side file recording the TRUE wiring of
    /// every portal before any lockdown engine touches it, so a crash mid-lockdown never scrambles the
    /// network - restored at OnWorldReady (the cheapest possible window: every ZDO comes back with
    /// Owned=false/OwnerRevision=0/DataRevision=0 and no peer is connected yet, so Game.SetConnection's
    /// owner==0 immediate-write branch always takes the local path with zero contention, per #112's own
    /// citation).
    ///
    /// Also hosts #113 Save-Boundary Transparency's restore/re-lock PAIR as plain static methods
    /// (PreSaveRestoreAll/PostSaveRelockAll) - the logic is complete and correct (the exact
    /// SetConnection-based sandwich the catalog specifies), but wiring it to actually bracket
    /// ZDOMan.PrepareSave needs a prefix+postfix pair on that method, which none of Core/Hooks/'s
    /// existing brokers cover:
    /// NEEDS NEW HOOK BROKER on ZDOMan.PrepareSave(): purpose - prefix restores every vaulted
    /// connection from the vault (so the save snapshot captures the TRUE wiring), postfix re-nulls them
    /// immediately after (so no peer ever observes the unlocked state) - see LockdownConfig.SaveBoundarySandwichEnabled.
    /// Until that broker exists, LockdownInvariantHarness's fast re-assert tick is the authoritative
    /// defence against a lockdown ever reaching disk unlocked (same "make the re-assert tick
    /// authoritative rather than trusting the postfix alone" hedge the catalog itself recommends).
    /// </summary>
    public static class LockdownVault
    {
        private const float PositionToleranceSqr = 1.0f * 1.0f;
        private static readonly Dictionary<string, VaultRecord> _records = new Dictionary<string, VaultRecord>(); // keyed by "reason|roundedA"
        private static readonly HashSet<Vector3> _lockedPositions = new HashSet<Vector3>(); // both ends of every current record, rounded - O(1) "is this portal currently force-disconnected by someone" for hooks
        private static bool _loaded;

        public static int Count => _records.Count;

        /// <summary>O(1) check used by FindRandomUnconnectedPortalHook/ConnectPortalsHook consumers: is this position one end of ANY currently-vaulted (i.e. force-disconnected) link, regardless of which engine/reason locked it?</summary>
        public static bool IsLocked(Vector3 pos) => _lockedPositions.Contains(Round(pos));

        private static void RebuildLockedPositionIndex()
        {
            _lockedPositions.Clear();
            foreach (VaultRecord r in _records.Values)
            {
                _lockedPositions.Add(Round(r.PosA));
                _lockedPositions.Add(Round(r.PosB));
            }
        }

        private static string FilePath()
        {
            string world = ZNet.instance != null ? ZNet.instance.GetWorldName() : "default";
            string name = LockdownConfig.VaultFileName?.Value ?? "lockdown_vault.json";
            string dir = BepInEx.Paths.ConfigPath;
            return Path.Combine(dir, $"TortalPortalLite.{name}.{world}.json");
        }

        private static Vector3 Round(Vector3 v) => new Vector3(Mathf.Round(v.x * 10f) / 10f, Mathf.Round(v.y * 10f) / 10f, Mathf.Round(v.z * 10f) / 10f);

        private static string KeyOf(string reason, Vector3 posA) => reason + "|" + Round(posA);

        /// <summary>Records the true wiring of one link BEFORE any lockdown write touches it. Idempotent per (reason, posA) - a second Record for the same pending lock is ignored so re-locking mid-lockdown never overwrites the ORIGINAL pre-lock state with an already-locked one.</summary>
        public static void Record(Vector3 posA, Vector3 posB, string tagBefore, string reason)
        {
            string key = KeyOf(reason, posA);
            if (_records.ContainsKey(key))
            {
                return;
            }
            _records[key] = new VaultRecord
            {
                Ax = posA.x, Ay = posA.y, Az = posA.z,
                Bx = posB.x, By = posB.y, Bz = posB.z,
                TagBefore = tagBefore ?? "",
                Reason = reason ?? "",
                LockedAtUtcTicks = DateTime.UtcNow.Ticks
            };
            RebuildLockedPositionIndex();
        }

        /// <summary>Drops the vault record for this (reason, posA) pair - call once a lock has been cleanly lifted and its wiring restored.</summary>
        public static void Forget(Vector3 posA, string reason)
        {
            if (_records.Remove(KeyOf(reason, posA)))
            {
                RebuildLockedPositionIndex();
            }
        }

        public static IEnumerable<VaultRecord> RecordsForReason(string reason)
        {
            foreach (VaultRecord r in _records.Values)
            {
                if (string.Equals(r.Reason, reason, StringComparison.Ordinal))
                {
                    yield return r;
                }
            }
        }

        /// <summary>Snapshot-and-fsync FIRST, before any nulling begins - the catalog's own "never interleave" rule. Call this, wait for it to return, THEN start disconnecting.</summary>
        public static void Save()
        {
            try
            {
                var file = new VaultFile { Records = new List<VaultRecord>(_records.Values) };
                string json = JsonConvert.SerializeObject(file, Formatting.Indented);
                string path = FilePath();
                string temp = path + ".tmp";
                File.WriteAllText(temp, json);
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
                File.Move(temp, path);
                PortalDebug.LogInfo($"[LockdownVault] saved {file.Records.Count} record(s) to '{path}'.");
            }
            catch (Exception ex)
            {
                PortalDebug.LogError($"[LockdownVault] save failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static void Load()
        {
            _records.Clear();
            try
            {
                string path = FilePath();
                if (!File.Exists(path))
                {
                    return;
                }
                string json = File.ReadAllText(path);
                var parsed = JsonConvert.DeserializeObject<VaultFile>(json);
                if (parsed?.Records == null)
                {
                    return;
                }
                foreach (VaultRecord r in parsed.Records)
                {
                    _records[KeyOf(r.Reason, r.PosA)] = r;
                }
                RebuildLockedPositionIndex();
                PortalDebug.LogAlways($"[LockdownVault] loaded {_records.Count} record(s) from '{path}'.");
            }
            catch (Exception ex)
            {
                PortalDebug.LogError($"[LockdownVault] load failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>
        /// Restore pass - run once at OnWorldReady. For every vault record, finds the live portal ZDOs
        /// nearest posA/posB (within tolerance), verifies both still carry a tag consistent with what was
        /// vaulted (or at least agree with each other), and re-wires them. Discards (and logs) any record
        /// whose endpoints no longer resolve - that portal was demolished during the lockdown.
        ///
        /// EVERY record is cleared after this pass, successfully-restored ones included (catalog #112's
        /// own citation: "Then delete the vault") - a fresh boot always starts from vanilla's true wiring
        /// with no lockdown state carried forward; whether to re-engage a lockdown is a LIVE decision the
        /// higher-level engines (schedule/ruleset/raid-geofence) make for themselves on their own next
        /// reconcile tick, from current conditions, never from stale vault contents (the same
        /// level-triggered-not-edge-triggered discipline #114 documents).
        /// </summary>
        public static void RestoreAll()
        {
            if (_loaded)
            {
                return;
            }
            _loaded = true;
            Load();
            RestoreAllInternal();
        }

        /// <summary>
        /// #125 Invariant Assertion Harness's PANIC RESTORE - identical restore logic to the boot-time
        /// RestoreAll pass, but callable at ANY time (not gated on the boot-only `_loaded` flag) so a
        /// mid-session unrepairable invariant violation can unconditionally hand every currently-vaulted
        /// portal back its true wiring/tag rather than continuing to run a lockdown engine that might be
        /// actively corrupting the world.
        /// </summary>
        public static void EmergencyRestoreAllNow()
        {
            RestoreAllInternal();
        }

        private static void RestoreAllInternal()
        {
            if (_records.Count == 0 || ZDOMan.instance == null)
            {
                return;
            }

            int restored = 0, dropped = 0;
            var toForget = new List<string>(_records.Keys);
            foreach (var kvp in _records)
            {
                VaultRecord rec = kvp.Value;
                bool selfPair = (rec.PosA - rec.PosB).sqrMagnitude < 0.01f; // tag-only record (quarantine/legibility) - PosA==PosB by construction, never wire a self-loop connection.

                if (selfPair)
                {
                    if (!PortalCensus.TryGetByPosition(rec.PosA, out PortalRecord solo))
                    {
                        dropped++;
                        continue;
                    }
                    ZDO soloZdo = ZDOMan.instance.GetZDO(solo.Uid);
                    if (soloZdo == null || !soloZdo.IsValid())
                    {
                        dropped++;
                        continue;
                    }
                    if (!string.IsNullOrEmpty(rec.TagBefore))
                    {
                        PortalOwnership.ClaimAndWrite(soloZdo, z => z.Set(ZDOVars.s_tag, rec.TagBefore));
                    }
                    restored++;
                    continue;
                }

                bool okA = PortalCensus.TryGetByPosition(rec.PosA, out PortalRecord a);
                bool okB = PortalCensus.TryGetByPosition(rec.PosB, out PortalRecord b);
                if (!okA || !okB)
                {
                    dropped++;
                    continue;
                }

                ZDO zdoA = ZDOMan.instance.GetZDO(a.Uid);
                ZDO zdoB = ZDOMan.instance.GetZDO(b.Uid);
                if (zdoA == null || !zdoA.IsValid() || zdoB == null || !zdoB.IsValid())
                {
                    dropped++;
                    continue;
                }

                PortalOwnership.ClaimAndWrite(zdoA, z =>
                {
                    if (!string.IsNullOrEmpty(rec.TagBefore)) z.Set(ZDOVars.s_tag, rec.TagBefore);
                    z.SetConnection(ZDOExtraData.ConnectionType.Portal, zdoB.m_uid);
                });
                PortalOwnership.ClaimAndWrite(zdoB, z =>
                {
                    if (!string.IsNullOrEmpty(rec.TagBefore)) z.Set(ZDOVars.s_tag, rec.TagBefore);
                    z.SetConnection(ZDOExtraData.ConnectionType.Portal, zdoA.m_uid);
                });
                restored++;
            }
            foreach (string key in toForget)
            {
                _records.Remove(key);
            }
            RebuildLockedPositionIndex();
            PortalDebug.LogAlways($"[LockdownVault] restored {restored}/{restored + dropped} portal link(s) from vault ({dropped} endpoint(s) missing - demolished during lockdown).");
            if (restored > 0)
            {
                Save();
            }
        }

        // ------------------------------------------------------------------- #113 save-boundary sandwich

        /// <summary>
        /// Would-be ZDOMan.PrepareSave PREFIX body - writes every vaulted TRUE connection back so the
        /// save snapshot captures reality, not the lockdown. See this file's own NEEDS NEW HOOK BROKER
        /// note; exposed publicly so a future broker (or an admin-triggered manual "clean save" verb) can
        /// call it directly.
        /// </summary>
        public static void PreSaveRestoreAll()
        {
            if (ZDOMan.instance == null)
            {
                return;
            }
            foreach (VaultRecord rec in _records.Values)
            {
                if ((rec.PosA - rec.PosB).sqrMagnitude < 0.01f)
                {
                    continue; // Tag-only record (quarantine/legibility) - no connection to restore, never wire a self-loop.
                }
                if (!PortalCensus.TryGetByPosition(rec.PosA, out PortalRecord a) || !PortalCensus.TryGetByPosition(rec.PosB, out PortalRecord b))
                {
                    continue;
                }
                ZDO zdoA = ZDOMan.instance.GetZDO(a.Uid);
                ZDO zdoB = ZDOMan.instance.GetZDO(b.Uid);
                if (zdoA == null || !zdoA.IsValid() || zdoB == null || !zdoB.IsValid())
                {
                    continue;
                }
                PortalOwnership.ClaimAndWrite(zdoA, z => z.SetConnection(ZDOExtraData.ConnectionType.Portal, zdoB.m_uid));
                PortalOwnership.ClaimAndWrite(zdoB, z => z.SetConnection(ZDOExtraData.ConnectionType.Portal, zdoA.m_uid));
            }
        }

        /// <summary>Would-be ZDOMan.PrepareSave POSTFIX body - immediately re-nulls, same frame, no ZDOMan.Update tick runs between prefix and postfix so no peer ever observes the unlocked state.</summary>
        public static void PostSaveRelockAll()
        {
            if (ZDOMan.instance == null)
            {
                return;
            }
            foreach (VaultRecord rec in _records.Values)
            {
                if (!PortalCensus.TryGetByPosition(rec.PosA, out PortalRecord a))
                {
                    continue;
                }
                ZDO zdoA = ZDOMan.instance.GetZDO(a.Uid);
                if (zdoA == null || !zdoA.IsValid())
                {
                    continue;
                }
                PortalOwnership.ClaimAndWrite(zdoA, z => z.SetConnection(ZDOExtraData.ConnectionType.Portal, ZDOID.None));
            }
        }
    }
}
