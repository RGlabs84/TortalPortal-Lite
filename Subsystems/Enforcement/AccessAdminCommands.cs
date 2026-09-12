using System;
using System.Linq;
using UnityEngine;
using TortalPortalLite.Core;
using TortalPortalLite.Core.Data;
using TortalPortalLite.Subsystems.Foundations;

namespace TortalPortalLite.Subsystems.Enforcement
{
    /// <summary>
    /// #220 Admin Remote Command Channel - the access domain's admin verbs. A vanilla dedicated server
    /// has exactly one text-input channel from an admin's UNMODIFIED client: a remote-flagged vanilla
    /// command (`removekey`) forwarded verbatim via `RPC_RemoteCommand`, which
    /// Subsystems/Foundations/CommandEngine.cs (Wave 0/1) already claims as ITS OWN direct patch on
    /// `Terminal.TryRunCommand` - deliberately not a Core/Hooks/ broker, because at the time it was
    /// written "nothing else in this mod has any reason to intercept TryRunCommand". That is no longer
    /// true (this domain wants its own admin verbs), but this wave's rules forbid installing a second,
    /// competing Harmony patch on the same vanilla method (exactly the "undefined prefix ordering, veto
    /// races" hazard Core/Hooks/ exists to prevent) and forbid editing Foundations files.
    ///
    /// So this class implements the real verb logic and is ready to be called, but is NOT YET REACHABLE
    /// from an admin's console until CommandEngine.cs's own `Dispatch(string verb, string[] args)` switch
    /// gets one added line forwarding an unrecognised verb here, e.g.:
    ///     default: return AccessAdminCommands.TryDispatch(verb, args, out string r) ? r : "tpl verbs: ...";
    /// This is a cross-file wiring dependency, not a missing key or hook - flagged explicitly in this
    /// wave's completion report rather than silently left unreachable.
    ///
    /// Verbs: acl-lock &lt;x&gt; &lt;y&gt; &lt;z&gt; &lt;none|retag|relink|full&gt;, acl-coowner &lt;x&gt; &lt;y&gt; &lt;z&gt; &lt;platformId&gt;,
    /// acl-transfer &lt;x&gt; &lt;y&gt; &lt;z&gt; &lt;platformId&gt;, pin &lt;x&gt; &lt;y&gt; &lt;z&gt;, unpin &lt;x&gt; &lt;y&gt; &lt;z&gt;,
    /// team-create &lt;name&gt; &lt;platformId&gt;, team-invite &lt;name&gt; &lt;platformId&gt;,
    /// progression-set &lt;network&gt; &lt;globalKey&gt;, progression-clear &lt;network&gt;, boss-lockdown &lt;on|off&gt;,
    /// access-status.
    /// </summary>
    public static class AccessAdminCommands
    {
        public static bool TryDispatch(string verb, string[] args, out string response)
        {
            switch (verb)
            {
                case "acl-lock": response = AclLock(args); return true;
                case "acl-coowner": response = AclCoOwner(args); return true;
                case "acl-transfer": response = AclTransfer(args); return true;
                case "pin": response = PinAt(args, true); return true;
                case "unpin": response = PinAt(args, false); return true;
                case "team-create": response = TeamCreate(args); return true;
                case "team-invite": response = TeamInvite(args); return true;
                case "progression-set": response = ProgressionSet(args); return true;
                case "progression-clear": response = ProgressionClear(args); return true;
                case "boss-lockdown": response = BossLockdown(args); return true;
                case "access-status": response = Status(); return true;
                default:
                    response = "";
                    return false;
            }
        }

        private static bool TryPos(string[] args, int offset, out Vector3 pos)
        {
            pos = default;
            if (args.Length < offset + 3
                || !float.TryParse(args[offset], out float x)
                || !float.TryParse(args[offset + 1], out float y)
                || !float.TryParse(args[offset + 2], out float z))
            {
                return false;
            }
            pos = new Vector3(x, y, z);
            return true;
        }

        private static bool TryZdo(Vector3 pos, out ZDO zdo, out PortalRecord record)
        {
            zdo = null;
            if (!PortalCensus.TryGetByPosition(pos, out record))
            {
                return false;
            }
            zdo = ZDOMan.instance?.GetZDO(record.Uid)!;
            return zdo != null && zdo.IsValid();
        }

        private static string AclLock(string[] args)
        {
            if (!TryPos(args, 0, out Vector3 pos) || args.Length < 4)
            {
                return "tpl: syntax: acl-lock <x> <y> <z> <none|retag|relink|full>";
            }
            if (!Enum.TryParse(args[3], true, out AccessLockLevel level))
            {
                return "tpl: lock level must be none, retag, relink or full.";
            }
            if (!TryZdo(pos, out ZDO zdo, out PortalRecord record))
            {
                return "tpl: no portal at that position.";
            }
            AccessAclEntry entry = AccessAclStore.GetOrCreate(pos);
            entry.Lock = level;
            AccessAclStore.MarkDirty();
            PortalRecordStore.SetLocked(zdo, level != AccessLockLevel.None);
            return $"tpl: {record.Uid} lock set to {level}.";
        }

        private static string AclCoOwner(string[] args)
        {
            if (!TryPos(args, 0, out Vector3 pos) || args.Length < 4)
            {
                return "tpl: syntax: acl-coowner <x> <y> <z> <platformId>";
            }
            if (!TryZdo(pos, out _, out PortalRecord record))
            {
                return "tpl: no portal at that position.";
            }
            AccessAclEntry entry = AccessAclStore.GetOrCreate(pos);
            string platformId = args[3];
            if (!entry.CoOwners.Contains(platformId))
            {
                entry.CoOwners.Add(platformId);
                AccessAclStore.MarkDirty();
            }
            return $"tpl: {platformId} added as co-owner of {record.Uid}.";
        }

        private static string AclTransfer(string[] args)
        {
            if (!TryPos(args, 0, out Vector3 pos) || args.Length < 4)
            {
                return "tpl: syntax: acl-transfer <x> <y> <z> <platformId>";
            }
            return "tpl: " + AccessCreatorReassignmentEngine.ReassignToConnectedAccount(pos, args[3]);
        }

        private static string PinAt(string[] args, bool pin)
        {
            if (!TryPos(args, 0, out Vector3 pos))
            {
                return $"tpl: syntax: {(pin ? "pin" : "unpin")} <x> <y> <z>";
            }
            if (!TryZdo(pos, out ZDO zdo, out PortalRecord record))
            {
                return "tpl: no portal at that position.";
            }
            if (pin)
            {
                AccessOwnershipPinEngine.Pin(zdo, "admin command");
            }
            else
            {
                AccessOwnershipPinEngine.Unpin(zdo);
            }
            return $"tpl: {record.Uid} {(pin ? "pinned" : "unpinned")}.";
        }

        private static string TeamCreate(string[] args)
        {
            if (args.Length < 2)
            {
                return "tpl: syntax: team-create <name> <founderPlatformId>";
            }
            AccessTeam team = AccessTeamRoster.CreateOrGet(args[0], args[1]);
            AccessTeamRoster.MarkDirty();
            return $"tpl: team '{team.Name}' created/founded by {args[1]}.";
        }

        private static string TeamInvite(string[] args)
        {
            if (args.Length < 2 || !AccessTeamRoster.TryGetByName(args[0], out AccessTeam team))
            {
                return "tpl: syntax: team-invite <name> <candidatePlatformId> (team must already exist)";
            }
            AccessTeamRoster.Invite(team, args[1]);
            return $"tpl: {args[1]} invited to '{team.Name}'.";
        }

        private static string ProgressionSet(string[] args)
        {
            if (args.Length < 2)
            {
                return "tpl: syntax: progression-set <network> <globalKey>";
            }
            AccessProgressionGateEngine.SetRequirement(args[0], args[1]);
            return $"tpl: network '{args[0]}' now requires global key '{args[1]}'.";
        }

        private static string ProgressionClear(string[] args)
        {
            if (args.Length < 1)
            {
                return "tpl: syntax: progression-clear <network>";
            }
            AccessProgressionGateEngine.ClearRequirement(args[0]);
            return $"tpl: network '{args[0]}' requirement cleared.";
        }

        private static string BossLockdown(string[] args)
        {
            if (args.Length < 1 || (args[0] != "on" && args[0] != "off"))
            {
                return "tpl: syntax: boss-lockdown <on|off>";
            }
            if (AccessConfig.BossLockdownActive != null)
            {
                AccessConfig.BossLockdownActive.Value = args[0] == "on";
            }
            return $"tpl: boss lockdown {(args[0] == "on" ? "engaged" : "released")} (applied on next tick).";
        }

        private static string Status()
        {
            int acl = AccessAclStore.All().Count();
            int teams = AccessTeamRoster.All().Count();
            int progression = AccessProgressionGateEngine.All().Count();
            return $"tpl: access domain - {acl} ACL record(s), {teams} team(s), {progression} progression requirement(s).";
        }
    }
}
