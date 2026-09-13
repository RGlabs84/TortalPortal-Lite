using System;
using System.IO;
using BepInEx;
using HarmonyLib;
using ServerSync;
using TortalPortalLite.Core;
using TortalPortalLite.Core.Compat;
using TortalPortalLite.Core.Data;
using TortalPortalLite.Core.Hooks;
using TortalPortalLite.Subsystems.Foundations;
using TortalPortalLite.Subsystems.Topology;
using TortalPortalLite.Subsystems.PlayerInterface;
using TortalPortalLite.Subsystems.WorldOps;

namespace TortalPortalLite
{
    /// <summary>
    /// TortalPortal Lite - a strictly server-side Valheim 1.0.7+ portal-network mod. Forked from the
    /// sibling mod Wonderland's Plugin.cs shell (Awake/Update/OnDestroy shape, config hot-reload poll)
    /// per catalog option #84's own architecture spec, which names this exact shape as the scaffold to
    /// copy.
    /// </summary>
    [BepInPlugin(ModGUID, ModName, ModVersion)]
    public class Plugin : BaseUnityPlugin
    {
        public const string ModGUID = "wubarrk.tortalportallite";
        public const string ModName = "TortalPortalLite";
        public const string ModVersion = "0.1.0";

        private const float ConfigPollInterval = 5f;

        public static Plugin Instance { get; private set; } = null!;
        public static ConfigSync ConfigSync { get; private set; } = null!;

        private readonly Harmony _harmony = new Harmony(ModGUID);
        public SubsystemRegistry Subsystems { get; } = new SubsystemRegistry();

        private float _configPollTimer;
        private DateTime _configStamp;

        private void Awake()
        {
            Instance = this;
            PortalDebug.Init(Logger);

            PortalDebug.LogAlways($"Starting TortalPortalLite v{ModVersion} (server-only, built for Valheim 1.0.7+)...");

            GameShape.Detect();

            ConfigSync = new ConfigSync(ModGUID)
            {
                DisplayName = ModName,
                CurrentVersion = ModVersion,
                MinimumRequiredVersion = ModVersion
            };

            GlobalConfig.Bind(Config, ConfigSync);
            _configStamp = ReadConfigStamp();

            HookInstaller.InstallAll(_harmony);

            RegisterSubsystems();
            Subsystems.InitializeAll(Config, ConfigSync, _harmony);
            SubsystemRegistry.SafePatch(_harmony, typeof(WorldReadyHook));

            PortalDebug.LogAlways("TortalPortalLite initialized successfully.");
        }

        /// <summary>
        /// Registers exactly this product's final feature set: #24, #29, #70, #71, #73, #81, #82, #84,
        /// #98, #148, #207, #224, #225 (plus #178/#187/#189 as load-bearing substrate under #207, and
        /// #23 as load-bearing substrate under the routing engines). Everything else the original
        /// research catalog covered was deliberately cut, not merely disabled - see git history for the
        /// full 263-option build if it's ever needed again.
        /// </summary>
        private void RegisterSubsystems()
        {
            Subsystems.Register(new PortalOpsSubsystem());
            Subsystems.Register(new RoutingSubsystem());
            Subsystems.Register(new TargetedSubsystem());
            Subsystems.Register(new UxSubsystem());
            Subsystems.Register(new EconomySubsystem());
            Subsystems.Register(new WildcardBSubsystem());
        }

        private void Update()
        {
            PollConfigFile();
            Heartbeat.OnUpdate(UnityEngine.Time.deltaTime);
            Subsystems.OnUpdate();
        }

        private void OnDestroy()
        {
            Subsystems.ShutdownAll();
            _harmony.UnpatchSelf();
            DiscordWebhook.SendBlocking("TortalPortalLite offline.");
        }

        /// <summary>
        /// Admins edit the .cfg on a running server; pick the change up without a restart. Poll-based,
        /// not a FileSystemWatcher - BepInEx.Configuration.ConfigFile has no built-in file watching.
        /// SaveOnConfigSet is parked false during Reload() so a re-applied value doesn't rewrite the
        /// file and trigger another reload on the very next poll.
        /// </summary>
        private void PollConfigFile()
        {
            _configPollTimer += UnityEngine.Time.deltaTime;
            if (_configPollTimer < ConfigPollInterval)
            {
                return;
            }
            _configPollTimer = 0f;

            try
            {
                DateTime stamp = ReadConfigStamp();
                if (stamp == _configStamp)
                {
                    return;
                }

                bool save = Config.SaveOnConfigSet;
                Config.SaveOnConfigSet = false;
                try
                {
                    Config.Reload();
                }
                finally
                {
                    Config.SaveOnConfigSet = save;
                }
                _configStamp = ReadConfigStamp();
                PortalDebug.LogAlways("[Config] config file changed on disk - settings reloaded without a restart.");
            }
            catch (Exception ex)
            {
                PortalDebug.LogWarning($"[Config] hot-reload failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private DateTime ReadConfigStamp()
        {
            try
            {
                return File.Exists(Config.ConfigFilePath) ? File.GetLastWriteTimeUtc(Config.ConfigFilePath) : DateTime.MinValue;
            }
            catch
            {
                return DateTime.MinValue;
            }
        }
    }

    /// <summary>
    /// BepInEx's own Awake() runs long before ZNetScene/ObjectDB/Game populate their prefab lists and
    /// singletons, so any subsystem setup that reads them has to wait for this instead - the first
    /// point those lists are actually filled in.
    /// </summary>
    [HarmonyPatch(typeof(ZNetScene), "Awake")]
    public static class WorldReadyHook
    {
        [HarmonyPostfix]
        public static void Postfix()
        {
            Plugin.Instance.Subsystems.OnWorldReady();
        }
    }
}
