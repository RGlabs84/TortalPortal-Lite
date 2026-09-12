using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using TortalPortalLite.Core;

namespace TortalPortalLite.Subsystems.Enforcement
{
    /// <summary>One named network's roster (#60 Team / Guild Networks). Keyed by account, not by character - see class doc.</summary>
    public sealed class AccessTeam
    {
        public string Name = "";
        public string Leader = ""; // verified PlatformUserID string
        public List<string> Members = new List<string>(); // verified PlatformUserID strings, leader included
        public List<string> Invited = new List<string>(); // pending invites awaiting a /point-equivalent accept
        public long LastActiveTicks;
    }

    internal sealed class AccessRosterFile
    {
        public int SchemaVersion = 1;
        public List<AccessTeam> Teams = new List<AccessTeam>();
    }

    /// <summary>
    /// #60 Team / Guild Networks - a server-side roster keyed on the platform-verified account (never
    /// `s_playerID`, which is per-CHARACTER and a player with several characters would appear as several
    /// identities; never `ZNetPeer.m_playerID`, which is permanently 0 because no vanilla client ever
    /// invokes the "PlayerID" RPC - see the catalog's own citations). Stored as a mod-private JSON file
    /// rather than world global keys: the catalog is explicit that global keys are broadcast in full to
    /// every client on every change (ZoneSystem.SendGlobalKeys(0L)) and are world-readable, which is
    /// wrong for anything beyond a handful of members - this is the "preferred for anything beyond a
    /// handful of members" branch of that option's own design.
    ///
    /// Membership changes are driven by EmoteSignals (see AccessPortalAclEngine, which registers the
    /// actual emote handlers so every claim/lock/co-owner/team gesture shares one proximity-resolution
    /// helper) - this class only owns the roster DATA and lookups.
    /// </summary>
    public static class AccessTeamRoster
    {
        private static readonly Dictionary<string, AccessTeam> _byName = new Dictionary<string, AccessTeam>(StringComparer.OrdinalIgnoreCase);
        private static bool _dirty;
        private static float _flushTimer;
        private static bool _loaded;

        public static void Initialize() => Load();

        public static void OnUpdate(float dt)
        {
            _flushTimer += dt;
            float interval = AccessConfig.StoreFlushSeconds?.Value ?? 10f;
            if (_flushTimer < interval)
            {
                return;
            }
            _flushTimer = 0f;
            if (_dirty)
            {
                Save();
            }
        }

        public static IEnumerable<AccessTeam> All() => _byName.Values;

        public static bool TryGetByName(string name, out AccessTeam team) => _byName.TryGetValue(name ?? "", out team);

        /// <summary>The (at most one, by convention) team a verified account belongs to, or null.</summary>
        public static AccessTeam? TeamOf(string platformUserId)
        {
            if (string.IsNullOrEmpty(platformUserId))
            {
                return null;
            }
            foreach (AccessTeam team in _byName.Values)
            {
                if (team.Members.Contains(platformUserId))
                {
                    return team;
                }
            }
            return null;
        }

        public static bool IsMember(string networkName, string platformUserId)
        {
            return TryGetByName(networkName, out AccessTeam team) && team.Members.Contains(platformUserId);
        }

        public static AccessTeam CreateOrGet(string name, string founderPlatformUserId)
        {
            if (!_byName.TryGetValue(name, out AccessTeam team))
            {
                team = new AccessTeam { Name = name, Leader = founderPlatformUserId };
                team.Members.Add(founderPlatformUserId);
                _byName[name] = team;
                MarkDirty();
            }
            return team;
        }

        public static void Invite(AccessTeam team, string candidatePlatformUserId)
        {
            if (!team.Members.Contains(candidatePlatformUserId) && !team.Invited.Contains(candidatePlatformUserId))
            {
                team.Invited.Add(candidatePlatformUserId);
                MarkDirty();
            }
        }

        public static bool AcceptInvite(string candidatePlatformUserId, out AccessTeam? team)
        {
            foreach (AccessTeam t in _byName.Values)
            {
                if (t.Invited.Remove(candidatePlatformUserId))
                {
                    if (!t.Members.Contains(candidatePlatformUserId))
                    {
                        t.Members.Add(candidatePlatformUserId);
                    }
                    MarkDirty();
                    team = t;
                    return true;
                }
            }
            team = null;
            return false;
        }

        public static void Touch(AccessTeam team)
        {
            team.LastActiveTicks = DateTime.UtcNow.Ticks;
            MarkDirty();
        }

        public static void MarkDirty() => _dirty = true;

        private static string FilePath()
        {
            string configPath = AccessConfig.RosterFile?.ConfigFile?.ConfigFilePath ?? "";
            string dir = Path.GetDirectoryName(configPath) ?? ".";
            return Path.Combine(dir, AccessConfig.RosterFile?.Value ?? "access_rosters.json");
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
                var parsed = JsonConvert.DeserializeObject<AccessRosterFile>(File.ReadAllText(path));
                if (parsed?.Teams == null)
                {
                    return;
                }
                foreach (AccessTeam team in parsed.Teams)
                {
                    _byName[team.Name] = team;
                }
                PortalDebug.LogAlways($"[AccessTeamRoster] loaded {_byName.Count} team(s) from '{path}'.");
            }
            catch (Exception ex)
            {
                PortalDebug.LogError($"[AccessTeamRoster] failed to load: {ex.GetType().Name}: {ex.Message}");
            }
        }

        public static void Save()
        {
            try
            {
                string path = FilePath();
                var file = new AccessRosterFile { Teams = _byName.Values.ToList() };
                File.WriteAllText(path, JsonConvert.SerializeObject(file, Formatting.Indented));
                _dirty = false;
            }
            catch (Exception ex)
            {
                PortalDebug.LogError($"[AccessTeamRoster] failed to save: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }
}
