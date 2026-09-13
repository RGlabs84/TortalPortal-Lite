using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using TortalPortalLite.Core;
using TortalPortalLite.Core.Data;
using TortalPortalLite.Subsystems.Foundations;

namespace TortalPortalLite.Subsystems.WorldOps
{
    internal sealed class EconomyDebtFile
    {
        public int SchemaVersion = 1;
        public Dictionary<long, long> Debt = new Dictionary<long, long>();
    }

    /// <summary>
    /// #94 Debtor's Lien and Personal Gates - the move that converts an unenforceable per-player rule
    /// ("this player owes coins") into an enforceable per-route one ("every gate this player built is
    /// parked"). Every player-placed portal's builder is server-resident: PortalCensus.PortalRecord.Creator
    /// already surfaces ZDOVars.s_creator (catalog's own citation: Piece.SetCreator writes it, only when
    /// the placing client owns the ZDO and it was previously 0 - :136417), the same PlayerProfile id
    /// space Player.SetPlayerID copies onto the live character ZDO as ZDOVars.s_playerID
    /// (ConnectedCharacter.PlayerId). Keying debt on that shared long id is what lets
    /// EconomyTransitTollEngine (which only ever sees a live, connected traveller) and this engine (which
    /// only ever sees an offline-readable portal ZDO) agree on "whose debt is this" without either one
    /// needing the other online at the same time.
    ///
    /// Honest limitation (catalog's own failure-mode #2): s_creator/s_playerID are client-self-reported
    /// PlayerProfile ids, not the platform-verified identity - a debtor CAN dodge by deleting their local
    /// character file and rejoining under a fresh one. The catalog's own fix (cross-check
    /// s_creatorIndex -> ZNet.World.m_playerHistory, which IS built from verified PlatformUserIDs) is not
    /// implemented here - m_playerHistory resolution was judged out of scope for this pass; this class
    /// documents the gap rather than silently pretending the id is unforgeable.
    ///
    /// Persistence: a small JSON ledger (economy_debt.json) - not a world global key. The catalog's own
    /// suggested `mod_debt_<playerID> <amount>` scheme requires encoding a numeric VALUE inside a global
    /// key's NAME (vanilla's global-key store is presence-only, a bare list of strings) and re-adding a
    /// freshly-named key on every single balance change, which is both fragile (parse-by-convention) and
    /// wasteful (a full re-broadcast of the entire key list per payment, per LockdownGlobalKeyEngine's own
    /// documented "PascalCase re-broadcast storm" hazard). AccessAclStore.cs already establishes the
    /// alternative this mod trusts for exactly this shape of state (a small, periodically-flushed,
    /// position/id-keyed JSON store) - reused here for a numeric ledger instead of an ACL record.
    /// </summary>
    public static class EconomyDebtLienEngine
    {
        private static readonly Dictionary<long, long> _debt = new Dictionary<long, long>();
        private static bool _loaded;
        private static bool _dirty;
        private static float _flushTimer;
        private static float _scanTimer;

        public static void Initialize()
        {
            Load();
        }

        public static void OnUpdate(float dt)
        {
            _flushTimer += dt;
            if (_flushTimer >= 10f)
            {
                _flushTimer = 0f;
                if (_dirty)
                {
                    Save();
                }
            }

            if (EconomyConfig.DebtLienEnabled?.Value == false)
            {
                return;
            }
            _scanTimer += dt;
            float interval = EconomyConfig.DebtCheckSeconds?.Value ?? 2f;
            if (_scanTimer < interval)
            {
                return;
            }
            _scanTimer = 0f;
            Scan();
        }

        // In-memory only (deliberately not persisted - these are short-lived cooldown-abuse penalties, not
        // real monetary debt): playerId -> world-time (ZNet.GetTimeSeconds()) the restriction lifts.
        private static readonly Dictionary<long, double> _temporaryRestrictionUntil = new Dictionary<long, double>();

        public static long GetDebt(long playerId) => playerId != 0 && _debt.TryGetValue(playerId, out long d) ? d : 0L;

        /// <summary>
        /// #283's own escalation citation ("the only real enforcement available - Debtor's Lien: unwire
        /// every portal that player built for the remainder of the window") - a non-monetary, time-boxed
        /// lien alongside the ordinary coin-debt one. Composes into the SAME "lien" condition Scan()
        /// already publishes, so a player under a temporary restriction sees exactly the same "every gate
        /// I built is dark" experience a real debtor does.
        /// </summary>
        public static void AddTemporaryRestriction(long playerId, float seconds)
        {
            if (playerId == 0 || seconds <= 0f || ZNet.instance == null)
            {
                return;
            }
            double until = ZNet.instance.GetTimeSeconds() + seconds;
            if (!_temporaryRestrictionUntil.TryGetValue(playerId, out double existing) || until > existing)
            {
                _temporaryRestrictionUntil[playerId] = until;
            }
        }

        private static bool IsTemporarilyRestricted(long playerId, out float secondsRemaining)
        {
            secondsRemaining = 0f;
            if (playerId == 0 || ZNet.instance == null || !_temporaryRestrictionUntil.TryGetValue(playerId, out double until))
            {
                return false;
            }
            double remaining = until - ZNet.instance.GetTimeSeconds();
            if (remaining <= 0)
            {
                _temporaryRestrictionUntil.Remove(playerId);
                return false;
            }
            secondsRemaining = (float)remaining;
            return true;
        }

        public static void AddDebt(long playerId, long amount)
        {
            if (playerId == 0 || amount <= 0)
            {
                return;
            }
            _debt[playerId] = GetDebt(playerId) + amount;
            _dirty = true;
        }

        /// <summary>Reduces a debtor's balance by amount (e.g. paid into any of their own escrow chests) - clamped at 0, never negative credit.</summary>
        public static void Pay(long playerId, long amount)
        {
            if (playerId == 0 || amount <= 0)
            {
                return;
            }
            long remaining = GetDebt(playerId) - amount;
            if (remaining <= 0)
            {
                _debt.Remove(playerId);
            }
            else
            {
                _debt[playerId] = remaining;
            }
            _dirty = true;
        }

        private static void Scan()
        {
            foreach (PortalRecord record in PortalCensus.Latest)
            {
                long creator = record.Creator;
                if (creator == 0)
                {
                    // #103's own citation applies equally here: creator 0 (world-gen/admin/pre-field
                    // structures) must never be treated as a debtor.
                    EconomyRoutingKernel.Clear(record.Uid, "lien");
                    continue;
                }

                long debt = GetDebt(creator);
                bool restricted = IsTemporarilyRestricted(creator, out float secondsRemaining);
                if (debt <= 0 && !restricted)
                {
                    EconomyRoutingKernel.Clear(record.Uid, "lien");
                    continue;
                }

                string tag = debt > 0 ? $"-{debt}c" : $"cd {(int)secondsRemaining}s";
                EconomyRoutingKernel.Publish(record.Uid, "lien", false, tag, 15);
            }
        }

        private static string FilePath()
        {
            string configPath = EconomyConfig.RegistryFile?.ConfigFile?.ConfigFilePath ?? "";
            string dir = Path.GetDirectoryName(configPath) ?? ".";
            return Path.Combine(dir, "economy_debt.json");
        }

        private static void Load()
        {
            if (_loaded)
            {
                return;
            }
            _loaded = true;
            try
            {
                string path = FilePath();
                if (!File.Exists(path))
                {
                    return;
                }
                string json = File.ReadAllText(path);
                EconomyDebtFile? parsed = JsonConvert.DeserializeObject<EconomyDebtFile>(json);
                if (parsed?.Debt == null)
                {
                    return;
                }
                foreach (KeyValuePair<long, long> kvp in parsed.Debt)
                {
                    _debt[kvp.Key] = kvp.Value;
                }
                PortalDebug.LogAlways($"[EconomyDebtLienEngine] loaded {_debt.Count} debt record(s).");
            }
            catch (Exception ex)
            {
                PortalDebug.LogError($"[EconomyDebtLienEngine] failed to load economy_debt.json: {ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>Public so EconomySubsystem.Shutdown() can force a final flush - the periodic timer alone might not have fired since the last write.</summary>
        public static void Save()
        {
            try
            {
                var file = new EconomyDebtFile { Debt = new Dictionary<long, long>(_debt) };
                File.WriteAllText(FilePath(), JsonConvert.SerializeObject(file, Formatting.Indented));
                _dirty = false;
            }
            catch (Exception ex)
            {
                PortalDebug.LogError($"[EconomyDebtLienEngine] failed to save economy_debt.json: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }
}
