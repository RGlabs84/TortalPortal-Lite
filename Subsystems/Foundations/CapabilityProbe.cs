using System;
using System.Collections.Generic;
using UnityEngine;
using TortalPortalLite.Core;
using TortalPortalLite.Core.Data;

namespace TortalPortalLite.Subsystems.Foundations
{
    /// <summary>
    /// #202 CapabilityProbe. A one-shot-per-fact engine, ticked from OnUpdate (not OnWorldReady alone),
    /// that resolves the Inspector-data/singleton facts the decompile cannot answer and publishes them
    /// as named booleans/values every later-wave engine can check before relying on something
    /// unverifiable from source alone. ObjectDB/EnvMan/Console can be null at the FIRST OnWorldReady
    /// call (Wonderland's BuffRosterEngine precedent, cited in #84) - this retries once per tick, up to
    /// a cap, rather than assuming a single check is authoritative.
    ///
    /// Each probe is its own try/catch'd method, mirroring Core/Compat/GameShape.cs's mono-JIT
    /// discipline: a probe touching a member that doesn't exist on some future build throws at JIT of
    /// THAT method, never taking down the others.
    /// </summary>
    public static class CapabilityProbe
    {
        private const int MaxAttempts = 60; // ~1 minute at the 1s OnUpdate-adjacent poll below
        private static int _attempts;
        private static float _timer;
        private static bool _done;

        private static readonly Dictionary<string, bool> _flags = new Dictionary<string, bool>();
        private static readonly Dictionary<string, string> _notes = new Dictionary<string, string>();

        public static bool IsDone => _done;
        public static bool Get(string key, bool fallback = false) => _flags.TryGetValue(key, out bool v) ? v : fallback;
        public static string? NoteFor(string key) => _notes.TryGetValue(key, out string n) ? n : null;

        public static void OnUpdate(float dt)
        {
            if (_done)
            {
                return;
            }
            _timer += dt;
            if (_timer < 1f)
            {
                return;
            }
            _timer = 0f;
            RunOnce();
        }

        private static void RunOnce()
        {
            _attempts++;
            bool allResolved = true;

            allResolved &= ProbePortalPrefabs();
            allResolved &= ProbeSingletons();
            allResolved &= ProbeWard();
            allResolved &= ProbePlayerPrefab();

            if (allResolved || _attempts >= MaxAttempts)
            {
                _done = true;
                PublishSummary();
            }
        }

        private static bool ProbePortalPrefabs()
        {
            try
            {
                if (Game.instance == null || ZNetScene.instance == null)
                {
                    return false;
                }
                foreach (int hash in PortalRegistry.PrefabHashes)
                {
                    GameObject prefab = ZNetScene.instance.GetPrefab(hash);
                    if (prefab == null)
                    {
                        continue;
                    }
                    string key = $"portal.{prefab.name}";
                    ZNetView view = prefab.GetComponent<ZNetView>();
                    TeleportWorld tw = prefab.GetComponent<TeleportWorld>();
                    ZSyncTransform sync = prefab.GetComponent<ZSyncTransform>();

                    _flags[$"{key}.persistent"] = view != null && view.m_persistent;
                    _flags[$"{key}.hasSyncTransform"] = sync != null;
                    if (sync != null)
                    {
                        _flags[$"{key}.syncPosition"] = sync.m_syncPosition;
                        _flags[$"{key}.characterParentSync"] = sync.m_characterParentSync;
                    }
                    if (tw != null)
                    {
                        _notes[$"{key}.exitDistance"] = tw.m_exitDistance.ToString("F2");
                        _notes[$"{key}.activationRange"] = tw.m_activationRange.ToString("F2");
                        _flags[$"{key}.allowAllItems"] = tw.m_allowAllItems;
                    }
                    var componentNames = new List<string>();
                    foreach (MonoBehaviour mb in prefab.GetComponentsInChildren<MonoBehaviour>(true))
                    {
                        if (mb != null) componentNames.Add(mb.GetType().Name);
                    }
                    _notes[$"{key}.components"] = string.Join(",", componentNames);
                }
                return true;
            }
            catch (Exception ex)
            {
                PortalDebug.LogWarning($"[CapabilityProbe] portal-prefab probe failed (attempt {_attempts}): {ex.Message}");
                return false;
            }
        }

        private static bool ProbeSingletons()
        {
            try
            {
                _flags["singleton.Chat"] = Chat.instance != null;
                _flags["singleton.MessageHud"] = MessageHud.instance != null;
                _flags["singleton.Minimap"] = Minimap.instance != null;
                _flags["singleton.Console"] = Console.instance != null;
                _flags["singleton.ObjectDB"] = ObjectDB.instance != null;
                _flags["singleton.EnvMan"] = EnvMan.instance != null;
                // These are the singletons every later engine is most likely to null-check for before
                // assuming a client-visible-feedback path is available; add more here as later waves
                // discover a need, rather than inside a Subsystems/ folder (same rule as PortalKeys.cs).
                return true;
            }
            catch (Exception ex)
            {
                PortalDebug.LogWarning($"[CapabilityProbe] singleton probe failed (attempt {_attempts}): {ex.Message}");
                return false;
            }
        }

        private static bool ProbeWard()
        {
            try
            {
                if (ZNetScene.instance == null)
                {
                    return false;
                }
                GameObject wardPrefab = ZNetScene.instance.GetPrefab("guard_stone");
                if (wardPrefab == null)
                {
                    _flags["ward.present"] = false;
                    return true;
                }
                PrivateArea area = wardPrefab.GetComponent<PrivateArea>();
                _flags["ward.present"] = area != null;
                if (area != null)
                {
                    _notes["ward.radius"] = area.m_radius.ToString("F1");
                    _flags["ward.enabledByDefault"] = area.m_enabledByDefault;
                }
                return true;
            }
            catch (Exception ex)
            {
                PortalDebug.LogWarning($"[CapabilityProbe] ward probe failed (attempt {_attempts}): {ex.Message}");
                return false;
            }
        }

        private static bool ProbePlayerPrefab()
        {
            try
            {
                if (ZNetScene.instance == null)
                {
                    return false;
                }
                GameObject playerPrefab = ZNetScene.instance.GetPrefab("Player");
                if (playerPrefab == null)
                {
                    return false;
                }
                ZSyncTransform sync = playerPrefab.GetComponent<ZSyncTransform>();
                _flags["player.characterParentSync"] = sync != null && sync.m_characterParentSync;
                return true;
            }
            catch (Exception ex)
            {
                PortalDebug.LogWarning($"[CapabilityProbe] player-prefab probe failed (attempt {_attempts}): {ex.Message}");
                return false;
            }
        }

        private static void PublishSummary()
        {
            int trueCount = 0;
            foreach (bool v in _flags.Values) if (v) trueCount++;
            PortalDebug.LogAlways($"[CapabilityProbe] resolved after {_attempts} attempt(s): {_flags.Count} flag(s) ({trueCount} true), {_notes.Count} note(s).");
        }
    }
}
