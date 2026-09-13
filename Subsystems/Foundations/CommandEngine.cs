using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using TortalPortalLite.Core;
using TortalPortalLite.Core.Data;

namespace TortalPortalLite.Subsystems.Foundations
{
    /// <summary>
    /// #198 RemoteCommand Piggyback + #199 tpl: grammar/dispatcher, combined (transport and grammar are
    /// two options in the catalog but one class here - the transport has no purpose without a grammar
    /// to dispatch to). A vanilla dedicated server has no stdin console of its own worth relying on; the
    /// ONLY channel an admin has ever had into this game's console is a remote-flagged vanilla command
    /// forwarded from their own client - `removekey` was chosen as the carrier because its real effect
    /// (attempting to remove a global key literally named "tpl &lt;verb&gt; ...", which never exists) is a
    /// provable no-op, and this engine intercepts it BEFORE that no-op runs anyway.
    ///
    /// Patch point: Terminal.TryRunCommand(string text, bool silentFail, bool skipAllowedCheck) (SERVER
    /// decompile :45734, confirmed public on the abstract `Terminal : MonoBehaviour` base class; `Console
    /// : Terminal` is the concrete instance Game.Awake instantiates headless via m_consolePrefab - so
    /// this genuinely runs on a dedicated server, unlike TeleportWorld/Player). A vanilla admin's client
    /// forwards the WHOLE line via RPC_RemoteCommand -> here regardless of what follows "removekey ".
    ///
    /// Deliberately its own direct patch, not a Core/Hooks/ broker: nothing else in this mod has any
    /// reason to intercept TryRunCommand.
    /// </summary>
    public static class CommandEngine
    {
        private const string Carrier = "removekey";
        private const string Namespace = "tpl";

        public static bool PatchOk { get; private set; }

        public static void Install(Harmony harmony)
        {
            try
            {
                var target = AccessTools.Method(typeof(Terminal), "TryRunCommand", new[] { typeof(string), typeof(bool), typeof(bool) });
                if (target == null)
                {
                    throw new MissingMethodException("Terminal.TryRunCommand(string, bool, bool) not found - vanilla method signature may have changed.");
                }
                harmony.Patch(target, prefix: new HarmonyMethod(typeof(CommandEngine), nameof(Prefix)));
                PatchOk = true;
            }
            catch (Exception ex)
            {
                PatchOk = false;
                PortalDebug.LogError($"[CommandEngine] failed to bind Terminal.TryRunCommand(...): {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static bool Prefix(string text, Terminal __instance)
        {
            try
            {
                if (string.IsNullOrEmpty(text))
                {
                    return true;
                }
                string[] words = text.Split(' ');
                if (words.Length < 3 || !words[0].Equals(Carrier, StringComparison.OrdinalIgnoreCase) || !words[1].Equals(Namespace, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                string verb = words[2].ToLowerInvariant();
                string[] args = words.Skip(3).ToArray();
                string response = Dispatch(verb, args);
                __instance.AddString(response);
                return false; // skip vanilla's own removekey handler - the carrier's real effect is a deliberate, provable no-op
            }
            catch (Exception ex)
            {
                PortalDebug.LogError($"[CommandEngine] dispatch failed for '{text}': {ex.GetType().Name}: {ex.Message}");
                __instance.AddString($"tpl: internal error - {ex.Message}");
                return false;
            }
        }

        private static string Dispatch(string verb, string[] args)
        {
            switch (verb)
            {
                case "list":
                    return ListPortals();
                case "status":
                    return Status();
                case "health":
                    return HealthReport();
                case "reassert":
                    NetworkReassertEngineForceTick();
                    return "tpl: reassert forced.";
                case "lock":
                case "unlock":
                    return SetLock(args, locked: verb == "lock");
                case "uninstall":
                    return args.Length > 0 && args[0] == "--clean-keys" ? UninstallEngine.CleanKeys() : "tpl: syntax: uninstall --clean-keys";
                case "help":
                    return "tpl verbs: list, status, health, reassert, lock <x> <y> <z>, unlock <x> <y> <z>, uninstall --clean-keys, help, "
                         + "+ access domain verbs (acl-lock, acl-coowner, acl-transfer, pin, unpin, team-create, team-invite, progression-set, progression-clear, boss-lockdown, access-status)";
                default:
                    // Every domain's admin verbs route through this single shared entry point (see
                    // AccessAdminCommands.cs's class doc comment for why - one Terminal.TryRunCommand
                    // patch, not one per domain). Tried in the order each domain was integrated.
                    if (TortalPortalLite.Subsystems.Enforcement.AccessAdminCommands.TryDispatch(verb, args, out string accessResponse))
                    {
                        return accessResponse;
                    }
                    if (OpsOutputAdminCommands.TryDispatch(verb, args, out string opsResponse))
                    {
                        return opsResponse;
                    }
                    if (TortalPortalLite.Subsystems.WorldOps.WildcardAAdminCommands.TryDispatch(verb, args, out string wildcardResponse))
                    {
                        return wildcardResponse;
                    }
                    return $"tpl: unknown verb '{verb}'. Type 'removekey tpl help' for the list.";
            }
        }

        private static string ListPortals()
        {
            var lines = PortalCensus.Latest.Select(r => $"{r.Uid} tag='{r.Tag}' pos=({r.Position.x:F0},{r.Position.y:F0},{r.Position.z:F0}) conn={r.Connection}");
            string body = string.Join(" | ", lines.Take(50));
            int total = PortalCensus.Latest.Count;
            return $"tpl: {total} portal(s) (showing up to 50): {body}";
        }

        private static string Status()
        {
            return $"tpl: {PortalCensus.Latest.Count} portals, {NetworkModel.Networks.Count} declared network(s), {HealthScanEngine.Findings.Count} health finding(s), last census {PortalCensus.LastScanUtc:HH:mm:ss} UTC.";
        }

        private static string HealthReport()
        {
            if (HealthScanEngine.Findings.Count == 0)
            {
                return "tpl: no health findings.";
            }
            var lines = HealthScanEngine.Findings.Select(f => $"[{f.Severity}] {f.Category}: {f.Detail}");
            return "tpl: " + string.Join(" | ", lines.Take(20));
        }

        private static void NetworkReassertEngineForceTick()
        {
            // The engine's own OnUpdate is timer-gated; an explicit admin request should not wait.
            typeof(NetworkReassertEngine).GetMethod("Reassert", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)?.Invoke(null, null);
        }

        private static string SetLock(string[] args, bool locked)
        {
            if (args.Length < 3 || !float.TryParse(args[0], out float x) || !float.TryParse(args[1], out float y) || !float.TryParse(args[2], out float z))
            {
                return $"tpl: syntax: {(locked ? "lock" : "unlock")} <x> <y> <z>";
            }
            if (!PortalCensus.TryGetByPosition(new UnityEngine.Vector3(x, y, z), out PortalRecord record))
            {
                return "tpl: no portal at that position.";
            }
            ZDO? zdo = ZDOMan.instance?.GetZDO(record.Uid);
            if (zdo == null)
            {
                return "tpl: portal ZDO no longer resolvable.";
            }
            PortalRecordStore.SetLocked(zdo, locked);
            return $"tpl: {(locked ? "locked" : "unlocked")} {record.Uid}.";
        }
    }
}
