using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using ServerSync;

namespace TortalPortalLite.Core
{
    public class SubsystemRegistry
    {
        private readonly List<IPortalSubsystem> _subsystems = new List<IPortalSubsystem>();
        private bool _initialized = false;
        private bool _worldReady = false;

        public void Register(IPortalSubsystem subsystem)
        {
            if (!_subsystems.Contains(subsystem))
            {
                _subsystems.Add(subsystem);
            }
        }

        public void InitializeAll(ConfigFile config, ConfigSync configSync, Harmony harmony)
        {
            if (_initialized) return;
            _initialized = true;

            foreach (var subsystem in _subsystems)
            {
                try
                {
                    PortalDebug.LogInfo($"Initializing subsystem: {subsystem.Name}...");
                    subsystem.Initialize(config, configSync, harmony);
                    PortalDebug.LogInfo($"Subsystem {subsystem.Name} initialized successfully.");
                }
                catch (Exception ex)
                {
                    PortalDebug.LogError($"Error initializing subsystem '{subsystem.Name}': {ex.Message}\n{ex.StackTrace}");
                }
            }
        }

        /// <summary>Fires once, from a Harmony postfix on ZNetScene.Awake - see IPortalSubsystem.OnWorldReady.</summary>
        public void OnWorldReady()
        {
            if (!_initialized || _worldReady) return;
            _worldReady = true;

            foreach (var subsystem in _subsystems)
            {
                try
                {
                    subsystem.OnWorldReady();
                }
                catch (Exception ex)
                {
                    PortalDebug.LogError($"Error in subsystem '{subsystem.Name}' OnWorldReady: {ex.Message}\n{ex.StackTrace}");
                }
            }
        }

        public void OnUpdate()
        {
            if (!_initialized) return;

            foreach (var subsystem in _subsystems)
            {
                if (!subsystem.IsEnabled) continue;
                try
                {
                    subsystem.OnUpdate();
                }
                catch (Exception ex)
                {
                    PortalDebug.LogError($"Error in subsystem '{subsystem.Name}' OnUpdate: {ex.Message}");
                }
            }
        }

        public void ShutdownAll()
        {
            foreach (var subsystem in _subsystems)
            {
                try
                {
                    subsystem.Shutdown();
                }
                catch (Exception ex)
                {
                    PortalDebug.LogError($"Error shutting down subsystem '{subsystem.Name}': {ex.Message}");
                }
            }
            _subsystems.Clear();
            _initialized = false;
            _worldReady = false;
        }

        /// <summary>
        /// Applies a Harmony patch set and swallows a failure into a warning log rather than crashing
        /// plugin startup - used for ordinary, best-effort patch groups. NOT used for
        /// Core/Hooks/ brokers, whose targets are load-bearing for every consumer registered against
        /// them: see PatchSelfTest, which hard-disables a broker (and every engine that registered
        /// against it) if its target failed to resolve, instead of silently continuing.
        /// </summary>
        public static bool SafePatch(Harmony harmony, Type patchType)
        {
            try
            {
                harmony.PatchAll(patchType);
                PortalDebug.LogInfo($"Successfully applied Harmony patch set: {patchType.Name}");
                return true;
            }
            catch (Exception ex)
            {
                PortalDebug.LogWarning($"Failed to apply Harmony patch set '{patchType.Name}': {ex.Message}");
                return false;
            }
        }
    }
}
