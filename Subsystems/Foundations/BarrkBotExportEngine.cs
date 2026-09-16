using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using UnityEngine;
using TortalPortalLite.Core;
using TortalPortalLite.Core.Data;
using TortalPortalLite.Subsystems.WorldOps;

namespace TortalPortalLite.Subsystems.Foundations
{
    /// <summary>
    /// Re-adds the one piece of the original (non-Lite) TortalPortal's ops-output wave this build actually
    /// still wants: a live, atomically-written "who owns how many portals" file for BarrkBOT (or any other
    /// reader following the same ingestion contract) to pick up over the filesystem it already watches.
    /// Schema and path are pinned to BarrkBOT's own documented contract (its docs/MOD_EXPORT_CONTRACT.md
    /// v4, confirmed directly against its reader code, 2026-09-15) rather than reverse-engineered: the
    /// schema_version/generated_at/source/intervals metadata envelope the contract requires of every
    /// producer, wrapped around the exact cap/cap_enabled/total_portals/unattributed_portals/players shape
    /// the real TortalPortal 1.4.2 export already writes today. Two fields deviate from that original,
    /// both confirmed against BarrkBOT's live reader on the same day: `player_id` is a STRING, not a JSON
    /// number - a SteamID64 exceeds 2^53 and a reader parsing through a double silently mangles it - and a
    /// player row is omitted entirely (never sent with `name: null`) until a display name resolves, since
    /// one null-named row demotes BarrkBOT's whole `players` collection out of per-player ranking.
    ///
    /// Player identity is s_creator (PortalRecord.Creator), the same persistent id
    /// ConnectedCharacter.PlayerId exposes for a currently-online player (WildcardBPortalCapEngine already
    /// established the two are the same field via Player.PlacePiece/Player.GetPlayerID) - never a
    /// ZNetPeer session id, which does not survive a reconnect. An offline creator's display name is
    /// resolved the same way OpsOutputReportModel.ResolveBuilder already does: ZNet.World.m_playerHistory
    /// indexed by the census record's own CreatorIndex, since a dedicated server never instantiates a
    /// Player and so keeps no other record of someone who isn't currently connected.
    /// </summary>
    public static class BarrkBotExportEngine
    {
        private const int SchemaVersion = 3;
        private const string FileName = "barrkbot_portals.json";

        private static float _timer;
        private static float _lastWriteRealtime = float.NegativeInfinity;

        // BarrkBOT's contract keys a producer by the folder name directly under <BepInEx>/config, so
        // this has to be the mod's own name, not an arbitrary subfolder.
        private static string ExportDir => Path.Combine(OpsOutputConfig.PluginConfigDir, Plugin.ModName);
        private static string ExportPath => Path.Combine(ExportDir, FileName);

        private static float FloorSeconds => Math.Max(60f, OpsOutputConfig.BarrkBotExportIntervalSeconds?.Value ?? 60f);

        public static void OnUpdate(float dt)
        {
            if (OpsOutputConfig.BarrkBotExportEnabled?.Value != true)
            {
                return;
            }

            _timer += dt;
            if (_timer < FloorSeconds)
            {
                return;
            }
            _timer = 0f;
            ExportNow();
        }

        /// <summary>
        /// Also the target of the on-demand `removekey tpl barrkbot` admin verb - the write-floor check
        /// below applies to BOTH callers, not just the OnUpdate timer, so an admin (or a script) spamming
        /// that command can't push writes out faster than BarrkBOT's own contract ("write no faster than
        /// 60s") allows.
        /// </summary>
        public static string ExportNow()
        {
            if (ZDOMan.instance == null)
            {
                return "tpl: ZDOMan not ready, barrkbot export skipped.";
            }

            float floor = FloorSeconds;
            float sinceLastWrite = Time.realtimeSinceStartup - _lastWriteRealtime;
            if (sinceLastWrite < floor)
            {
                return $"tpl: barrkbot export skipped - last write was {sinceLastWrite:F0}s ago (floor is {floor:F0}s, per BarrkBOT's own contract).";
            }

            try
            {
                var counts = new Dictionary<long, int>();
                var creatorIndexByCreator = new Dictionary<long, int>();
                int unattributed = 0;

                foreach (PortalRecord record in PortalCensus.Latest)
                {
                    if (record.Creator == 0L)
                    {
                        // No creator recorded: admin-spawned, or older than the field. Counted in the
                        // world total but never attributed to player zero, who does not exist.
                        unattributed++;
                        continue;
                    }
                    counts.TryGetValue(record.Creator, out int n);
                    counts[record.Creator] = n + 1;
                    if (!creatorIndexByCreator.ContainsKey(record.Creator))
                    {
                        creatorIndexByCreator[record.Creator] = record.CreatorIndex;
                    }
                }

                Dictionary<long, string> onlineNames = ConnectedCharacters.All()
                    .GroupBy(c => c.PlayerId)
                    .ToDictionary(g => g.Key, g => g.First().Name);

                // Somebody logged in who owns no portals still gets a row - "we have no record of them"
                // and "they have none" are different answers a reader has to be able to tell apart.
                foreach (long id in onlineNames.Keys)
                {
                    if (!counts.ContainsKey(id))
                    {
                        counts[id] = 0;
                    }
                }

                bool capToggleOn = WildcardBConfig.PortalCapEnabled?.Value == true;
                int capValue = WildcardBConfig.PortalCapPerCreator?.Value ?? 0;
                bool capped = capToggleOn && capValue > 0;

                var players = new Dictionary<string, object>();
                foreach (KeyValuePair<long, int> pair in counts.OrderByDescending(p => p.Value))
                {
                    bool online = onlineNames.TryGetValue(pair.Key, out string? liveName);
                    string? name = online ? liveName : ResolveOfflineName(creatorIndexByCreator, pair.Key);

                    // BarrkBOT's reader classifies the whole players collection as per-player ranking data
                    // only when EVERY row carries a resolved string name - one stray null demotes the
                    // entire export to an unranked summary. A portal_count row for someone we can't yet
                    // name carries no information anyway, so it's omitted rather than sent as null.
                    if (string.IsNullOrEmpty(name))
                    {
                        continue;
                    }

                    players[pair.Key.ToString(CultureInfo.InvariantCulture)] = new Dictionary<string, object?>
                    {
                        // A SteamID64 exceeds 2^53, so a JSON number here loses precision on any reader
                        // that parses through a double (confirmed against BarrkBOT's own JS reader) -
                        // string is the only lossless representation.
                        ["player_id"] = pair.Key.ToString(CultureInfo.InvariantCulture),
                        ["name"] = name,
                        ["online"] = online,
                        ["portal_count"] = pair.Value,
                        ["at_cap"] = capped && pair.Value >= capValue,
                        ["over_cap"] = capped && pair.Value > capValue,
                    };
                }

                var doc = new Dictionary<string, object?>
                {
                    ["schema_version"] = SchemaVersion,
                    ["generated_at"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                    ["source"] = $"{Plugin.ModName} {Plugin.ModVersion}",
                    ["intervals"] = new Dictionary<string, object> { ["write_seconds"] = (int)floor },
                    ["cap"] = capped ? (object)capValue : null,
                    ["cap_enabled"] = capped,
                    ["total_portals"] = PortalCensus.Latest.Count,
                    ["unattributed_portals"] = unattributed,
                    ["players"] = players,
                };

                string json = JsonConvert.SerializeObject(doc, Formatting.Indented);
                if (!OpsOutputAtomicFile.TryWriteAllText(ExportPath, json, out string? error))
                {
                    return $"tpl: barrkbot export failed: {error}";
                }

                _lastWriteRealtime = Time.realtimeSinceStartup;
                int unnamed = counts.Count - players.Count;
                string result = $"tpl: barrkbot export wrote {players.Count} player row(s) ({unnamed} omitted, name unresolved), {PortalCensus.Latest.Count} portal(s).";
                PortalDebug.LogInfo($"[BarrkBotExportEngine] {result}");
                return result;
            }
            catch (Exception ex)
            {
                PortalDebug.LogError($"[BarrkBotExportEngine] export failed (non-fatal, previous file stands): {ex.GetType().Name}: {ex.Message}");
                return $"tpl: barrkbot export failed: {ex.GetType().Name}: {ex.Message}";
            }
        }

        private static string? ResolveOfflineName(Dictionary<long, int> creatorIndexByCreator, long creator)
        {
            if (!creatorIndexByCreator.TryGetValue(creator, out int creatorIndex) || creatorIndex < 0)
            {
                return null;
            }
            try
            {
                var history = ZNet.World?.m_playerHistory;
                if (history != null && creatorIndex < history.Count)
                {
                    string name = history[creatorIndex].m_displayName;
                    return string.IsNullOrEmpty(name) ? null : name;
                }
            }
            catch (Exception ex)
            {
                PortalDebug.LogWarning($"[BarrkBotExportEngine] offline name lookup failed: {ex.Message}");
            }
            return null;
        }
    }
}
