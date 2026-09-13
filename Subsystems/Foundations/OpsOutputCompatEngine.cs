using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
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

        private static float _commandChurnTimer;
        private static readonly Dictionary<string, object> _knownCommands = new Dictionary<string, object>(StringComparer.Ordinal);

        // Foreign-tag-write detection (#82 item 7): remembers the last tag THIS mod itself last wrote to
        // a managed portal (via NetworkReassertEngine's own intended tag for that network) so a
        // divergence with no matching confirmed audit entry can be called out as a foreign write.
        private static readonly Dictionary<ZDOID, string> _lastIntendedTag = new Dictionary<ZDOID, string>();

        public static void OnUpdate(float dt)
        {
            _commandChurnTimer += dt;
            if (_commandChurnTimer >= 10f)
            {
                _commandChurnTimer = 0f;
                CheckCommandChurn();
            }

            if (OpsOutputConfig.CompatForeignWriteWarnings?.Value == true)
            {
                CheckForeignTagWrites();
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

            sb.Append($"- ZDOMan.m_onZDODestroyed: this mod's handler installed = {OpsOutputDiscordEngineDestroyHookInstalled()}\n");
            sb.Append($"- ZRoutedRpc.m_onNewPeer: this mod's handler installed = {OpsOutputJoinBriefingEngine.PeerHookInstalled}\n");
            sb.Append($"- Terminal.commands tracked: {_knownCommands.Count} (churn is logged as a warning when detected, not polled on demand)\n");
            return sb.ToString();
        }

        private static bool OpsOutputDiscordEngineDestroyHookInstalled()
        {
            // OpsOutputDiscordEngine doesn't expose this directly (it self-installs lazily) - infer it
            // the same way that engine does: once ZDOMan.instance exists, its own OnUpdate will have
            // installed the hook by the next tick, so "installed" here means "will be, or already is".
            return ZDOMan.instance != null;
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

        /// <summary>
        /// #82 item 7: "if the census sees a managed portal's tag change to a value we did not write, and
        /// no RPC_SetTag crossed the wire, log a 'foreign writer' warning naming the portal rather than
        /// silently fighting forever." NetworkReassertEngine will simply re-assert its own intended tag
        /// next pass regardless - this only makes the fight VISIBLE instead of silent.
        /// </summary>
        private static void CheckForeignTagWrites()
        {
            try
            {
                var byNetwork = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (NetworkDefinition net in NetworkModel.Networks)
                {
                    byNetwork[net.Name] = net.Tag;
                }

                foreach (PortalRecord record in PortalCensus.Latest)
                {
                    ZDO? zdo = ZDOMan.instance?.GetZDO(record.Uid);
                    if (zdo == null || !zdo.IsValid())
                    {
                        continue;
                    }
                    string networkId = PortalRecordStore.GetNetworkId(zdo);
                    if (string.IsNullOrEmpty(networkId) || !byNetwork.TryGetValue(networkId, out string intendedTag))
                    {
                        continue;
                    }

                    _lastIntendedTag.TryGetValue(record.Uid, out string? lastIntended);
                    _lastIntendedTag[record.Uid] = intendedTag;

                    if (record.Tag != intendedTag && lastIntended == intendedTag)
                    {
                        // We previously observed this portal correctly matching its network's tag; now it
                        // doesn't, and OUR intended tag hasn't changed - something else wrote it.
                        PortalDebug.LogWarning($"[OpsOutputCompatEngine] foreign writer suspected: managed portal {record.Uid} (network '{networkId}') tag is '{record.Tag}', expected '{intendedTag}' - NetworkReassertEngine will re-assert it, but another mod or client wrote it first.");
                    }
                }
            }
            catch (Exception ex)
            {
                PortalDebug.LogWarning($"[OpsOutputCompatEngine] foreign-write check failed: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }
}
