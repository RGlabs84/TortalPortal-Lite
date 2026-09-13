using System;
using UnityEngine;
using TortalPortalLite.Core;
using TortalPortalLite.Core.Data;
using TortalPortalLite.Subsystems.Foundations;

namespace TortalPortalLite.Subsystems.WorldOps
{
    /// <summary>
    /// Admin verbs for the wildcard cluster A domain, following the exact shape
    /// Subsystems/Enforcement/AccessAdminCommands.cs already established for its own domain (own class,
    /// own TryDispatch(verb,args,out response), reusing Subsystems/Foundations/CommandEngine.cs's shared
    /// "removekey tpl &lt;verb&gt; ..." transport rather than adding a second Terminal.TryRunCommand patch).
    ///
    /// SAME CROSS-FILE WIRING GAP AccessAdminCommands.cs's own header once documented: this class is not
    /// yet reachable from an admin's console. CommandEngine.cs's default case currently forwards an
    /// unrecognised verb only to Enforcement.AccessAdminCommands.TryDispatch - reaching this class needs
    /// one more forwarding line in EITHER CommandEngine.cs's default case or AccessAdminCommands' own
    /// default case, e.g.:
    ///     return TortalPortalLite.Subsystems.WorldOps.WildcardAAdminCommands.TryDispatch(verb, args, out string r) ? r : "...";
    /// Both files are off-limits this wave (CommandEngine.cs by explicit task rule; AccessAdminCommands.cs
    /// as another domain's Wave-2 file) - flagged here rather than silently left unreachable, exactly as
    /// the task's own completion-report instructions ask for.
    ///
    /// Verbs: wc-a-fortify/wc-a-unfortify &lt;x&gt; &lt;y&gt; &lt;z&gt;, wc-a-decoy/wc-a-undecoy &lt;x&gt; &lt;y&gt; &lt;z&gt;,
    /// wc-a-signage &lt;x&gt; &lt;y&gt; &lt;z&gt; &lt;text...&gt;, wc-a-scale &lt;x&gt; &lt;y&gt; &lt;z&gt; &lt;factor&gt;,
    /// wc-a-migrate-enroll/wc-a-migrate-reverse &lt;prefabName&gt;, wc-a-status.
    /// </summary>
    public static class WildcardAAdminCommands
    {
        public static bool TryDispatch(string verb, string[] args, out string response)
        {
            switch (verb)
            {
                case "wc-a-fortify": response = Fortify(args, true); return true;
                case "wc-a-unfortify": response = Fortify(args, false); return true;
                case "wc-a-decoy": response = Decoy(args, true); return true;
                case "wc-a-undecoy": response = Decoy(args, false); return true;
                case "wc-a-signage": response = Signage(args); return true;
                case "wc-a-scale": response = Scale(args); return true;
                case "wc-a-migrate-enroll": response = args.Length > 0 ? WildcardANonPortalMigrationEngine.EnrollWithMigration(args[0]) : "tpl: syntax: wc-a-migrate-enroll <prefabName>"; return true;
                case "wc-a-migrate-reverse": response = args.Length > 0 ? WildcardANonPortalMigrationEngine.ReverseEnrollment(args[0]) : "tpl: syntax: wc-a-migrate-reverse <prefabName>"; return true;
                case "wc-a-status": response = Status(); return true;
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

        private static string Fortify(string[] args, bool on)
        {
            if (!TryPos(args, 0, out Vector3 pos))
            {
                return $"tpl: syntax: wc-a-{(on ? "fortify" : "unfortify")} <x> <y> <z>";
            }
            if (!TryZdo(pos, out ZDO zdo, out PortalRecord record))
            {
                return "tpl: no portal at that position.";
            }
            if (on)
            {
                WildcardAAdamantGateEngine.Fortify(zdo);
            }
            else
            {
                WildcardAAdamantGateEngine.Unfortify(zdo);
            }
            return $"tpl: {record.Uid} {(on ? "fortified" : "unfortified")}.";
        }

        private static string Decoy(string[] args, bool on)
        {
            if (!TryPos(args, 0, out Vector3 pos))
            {
                return $"tpl: syntax: wc-a-{(on ? "decoy" : "undecoy")} <x> <y> <z>";
            }
            if (!TryZdo(pos, out ZDO zdo, out PortalRecord record))
            {
                return "tpl: no portal at that position.";
            }
            if (on)
            {
                return WildcardADecoyGateEngine.MakeDecoy(zdo)
                    ? $"tpl: {record.Uid} decoyed (connected-looking, permanently non-functional)."
                    : $"tpl: could not decoy {record.Uid} (already network-managed?).";
            }
            WildcardADecoyGateEngine.Release(zdo);
            return $"tpl: {record.Uid} decoy released.";
        }

        private static string Signage(string[] args)
        {
            if (!TryPos(args, 0, out Vector3 pos) || args.Length < 4)
            {
                return "tpl: syntax: wc-a-signage <x> <y> <z> <text...>";
            }
            if (!TryZdo(pos, out ZDO zdo, out PortalRecord record))
            {
                return "tpl: no portal at that position.";
            }
            string text = string.Join(" ", args, 3, args.Length - 3);
            return WildcardATagBroadcastEngine.TrySetSignage(zdo, text)
                ? $"tpl: {record.Uid} signage set."
                : $"tpl: could not set signage on {record.Uid} (already network-managed?).";
        }

        private static string Scale(string[] args)
        {
            if (!TryPos(args, 0, out Vector3 pos) || args.Length < 4 || !float.TryParse(args[3], out float factor))
            {
                return "tpl: syntax: wc-a-scale <x> <y> <z> <factor>";
            }
            if (!TryZdo(pos, out ZDO zdo, out PortalRecord record))
            {
                return "tpl: no portal at that position.";
            }
            WildcardAPortalScalingEngine.TrySetUniformScale(zdo, factor);
            string caveat = WildcardAPortalScalingEngine.LikelyWorks == false
                ? " (probe found no portal prefab with m_syncInitialScale set - this write is likely inert, no error either way)"
                : "";
            return $"tpl: {record.Uid} scale write issued (factor {factor:0.###}, applies next instantiation only){caveat}.";
        }

        private static string Status()
        {
            return $"tpl: wildcard-A domain - {PortalCensus.Latest.Count} portal(s) in census.";
        }
    }
}
