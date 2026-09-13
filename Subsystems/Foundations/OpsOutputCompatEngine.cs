using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using BepInEx.Bootstrap;
using HarmonyLib;
using TortalPortalLite.Core;
using TortalPortalLite.Core.Data;

namespace TortalPortalLite.Subsystems.Foundations
{
    /// <summary>
    /// #82 Compatibility with other server mods that touch ZDOs. Turns a "my portals keep unpairing"
    /// support ticket into one command's output by enumerating who else has a Harmony patch on every
    /// contention point the catalog names, via Harmony.GetPatchInfo(MethodBase) - no reflection into
    /// private state beyond what AccessTools.Method already resolves for any Harmony consumer.
    ///
    /// Two contention points the catalog names are NOT Harmony patches and so cannot appear in
    /// GetPatchInfo output at all:
    ///  - ZDOMan.m_onZDODestroyed / ZRoutedRpc.m_onNewPeer are plain delegate fields (Delegate.Combine,
    ///    never assign - see OpsOutputDiscordEngine/OpsOutputJoinBriefingEngine, both of which combine
    ///    onto them rather than assigning). This report can only confirm THIS mod's own handlers made it
    ///    into the invocation list, not enumerate every other subscriber (delegates don't expose that).
    ///  - Terminal.commands (protected static Dictionary&lt;string, ConsoleCommand&gt;, :43241 - accessible
    ///    directly once Publicize=true recompiles `protected` to `public`) is a plain indexer assignment
    ///    with no exception on collision (:43096/:43114) - watched here for CHURN (a known command name's
    ///    registered instance changing between two snapshots), which is the only way to observe a silent
    ///    clobber after the fact.
    /// </summary>
    public static class OpsOutputCompatEngine
    {
        private static readonly (Type type, string method, Type[] parameters)[] WatchedMethods =
        {
            (typeof(ZDO), "SetOwner", new[] { typeof(long) }),
            (typeof(Game), "ConnectPortals", Type.EmptyTypes),
            (typeof(Terminal), "TryRunCommand", new[] { typeof(string), typeof(bool), typeof(bool) }),
            (typeof(ZDOMan), "RPC_ZDOData", new[] { typeof(ZRpc), typeof(ZPackage) }),
            (typeof(ZDOMan), "RPC_DestroyZDO", new[] { typeof(long), typeof(ZPackage) }),
            (typeof(ZDOMan), "HandleDestroyedZDO", new[] { typeof(ZDOID) }),
            (typeof(ZRoutedRpc), "RPC_RoutedRPC", new[] { typeof(ZRpc), typeof(ZPackage) }),
        };

        // Known sibling mods this server is expected to run alongside - GUID, plain name, and (per a
        // direct read of each mod's own source) the specific vanilla methods it patches that overlap
        // with this mod's own WatchedMethods list above, plus whether that overlap is a genuine hazard.
        private static readonly (string guid, string name, string knownOverlap)[] KnownSiblingMods =
        {
            ("wubarrk.wonderland", "Wonderland",
                "Patches ZDO.SetOwner (WaterBuoyancyEngine) and ZRoutedRpc.HandleRoutedRPC (a DIFFERENT method than the RPC_RoutedRPC(ZRpc,ZPackage) this mod patches) - both prefixes veto only ZDOs WaterBuoyancyEngine itself tracks (buoyant water items), never portals. No known interference."),
            ("wubarrk.getoffmylawn", "GetOffMyLawn",
                "Patches ZDO.SetOwner (OwnershipHold) - the prefix vetoes only ZDOs explicitly held in its own ward-lock set (building pieces), never portals. No known interference."),
        };

        private static float _commandChurnTimer;
        private static readonly Dictionary<string, object> _knownCommands = new Dictionary<string, object>(StringComparer.Ordinal);

        public static void OnUpdate(float dt)
        {
            _commandChurnTimer += dt;
            if (_commandChurnTimer >= 10f)
            {
                _commandChurnTimer = 0f;
                CheckCommandChurn();
            }
        }

        public static string BuildReport()
        {
            var sb = new StringBuilder();
            sb.Append("tpl compat report:\n");
            foreach (var (type, method, parameters) in WatchedMethods)
            {
                MethodBase? target = AccessTools.Method(type, method, parameters);
                if (target == null)
                {
                    sb.Append($"- {type.Name}.{method}: NOT FOUND (vanilla signature may have changed)\n");
                    continue;
                }
                Patches? patches = Harmony.GetPatchInfo(target);
                int count = (patches?.Prefixes.Count ?? 0) + (patches?.Postfixes.Count ?? 0) + (patches?.Transpilers.Count ?? 0) + (patches?.Finalizers.Count ?? 0);
                var owners = patches?.Owners?.ToList() ?? new List<string>();
                sb.Append($"- {type.Name}.{method}: {count} patch(es)");
                if (owners.Count > 0)
                {
                    sb.Append(" [").Append(string.Join(", ", owners)).Append(']');
                }
                sb.Append('\n');
            }

            sb.Append($"- Terminal.commands tracked: {_knownCommands.Count} (churn is logged as a warning when detected, not polled on demand)\n");

            sb.Append("- known sibling mods:\n");
            foreach ((string guid, string name, string knownOverlap) in KnownSiblingMods)
            {
                bool loaded = Chainloader.PluginInfos.ContainsKey(guid);
                sb.Append($"  - {name} ({guid}): {(loaded ? "loaded" : "not loaded")}");
                if (loaded)
                {
                    sb.Append(" - ").Append(knownOverlap);
                }
                sb.Append('\n');
            }
            return sb.ToString();
        }

        private static void CheckCommandChurn()
        {
            try
            {
                foreach (var kvp in Terminal.commands)
                {
                    if (_knownCommands.TryGetValue(kvp.Key, out object? previous))
                    {
                        if (!ReferenceEquals(previous, kvp.Value))
                        {
                            PortalDebug.LogWarning($"[OpsOutputCompatEngine] Terminal command '{kvp.Key}' was re-registered by a different ConsoleCommand instance since last checked - Terminal.commands is a plain indexer with no collision exception (:43096/:43114), so the earlier registrant was silently clobbered.");
                        }
                    }
                    _knownCommands[kvp.Key] = kvp.Value;
                }
            }
            catch (Exception ex)
            {
                PortalDebug.LogWarning($"[OpsOutputCompatEngine] command-churn check failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

    }
}
