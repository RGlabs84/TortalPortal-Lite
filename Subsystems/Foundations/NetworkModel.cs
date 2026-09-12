using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using UnityEngine;
using TortalPortalLite.Core;

namespace TortalPortalLite.Subsystems.Foundations
{
    public sealed class NetworkMemberPosition
    {
        public float X;
        public float Y;
        public float Z;
        public Vector3 ToVector3() => new Vector3(X, Y, Z);
    }

    /// <summary>One admin-declared network: a shared tag every member should carry, and the positions of its members.</summary>
    public sealed class NetworkDefinition
    {
        public string Name = "";
        public string Tag = "";
        public List<NetworkMemberPosition> Members = new List<NetworkMemberPosition>();
    }

    public sealed class NetworksFile
    {
        public int SchemaVersion = 1;
        public List<NetworkDefinition> Networks = new List<NetworkDefinition>();
    }

    /// <summary>
    /// NetworkReassertEngine's (#69) required prerequisite: "network definition as a hot-reloaded JSON
    /// file". Admins declare networks by the POSITIONS they can see/measure in-game, not by ZDOID (not
    /// stable across a reload) or by a RecordId (doesn't exist until the mod has already seen the
    /// portal once) - resolution against PortalCensus's position index is what turns a position into a
    /// live ZDO each tick. Polled every 5s like Plugin's own config file (same BepInEx-has-no-file-watcher
    /// reasoning), independent of that poll so the two files can change independently.
    /// </summary>
    public static class NetworkModel
    {
        private const float PollInterval = 5f;
        private static float _timer;
        private static DateTime _fileStamp = DateTime.MinValue;
        private static List<NetworkDefinition> _networks = new List<NetworkDefinition>();

        public static IReadOnlyList<NetworkDefinition> Networks => _networks;

        private static string FilePath =>
            Path.Combine(Path.GetDirectoryName(FoundationsConfig.NetworksFile?.ConfigFile?.ConfigFilePath ?? "") ?? ".",
                FoundationsConfig.NetworksFile?.Value ?? "networks.json");

        public static void OnUpdate(float dt)
        {
            _timer += dt;
            if (_timer < PollInterval)
            {
                return;
            }
            _timer = 0f;
            TryReload();
        }

        private static void TryReload()
        {
            try
            {
                string path = FilePath;
                if (!File.Exists(path))
                {
                    return;
                }
                DateTime stamp = File.GetLastWriteTimeUtc(path);
                if (stamp == _fileStamp)
                {
                    return;
                }

                string json = File.ReadAllText(path);
                var parsed = JsonConvert.DeserializeObject<NetworksFile>(json);
                if (parsed?.Networks == null)
                {
                    PortalDebug.LogWarning($"[NetworkModel] '{path}' parsed but had no 'Networks' array - ignoring.");
                    return;
                }

                _networks = parsed.Networks;
                _fileStamp = stamp;
                PortalDebug.LogAlways($"[NetworkModel] loaded {_networks.Count} network definition(s) from '{path}'.");
            }
            catch (Exception ex)
            {
                PortalDebug.LogError($"[NetworkModel] failed to load networks.json: {ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>Resolves every declared member position against the current census. A position with no matching portal is simply absent from the result (HealthScanEngine reports it separately as 'declared but not found').</summary>
        public static IEnumerable<(NetworkDefinition network, PortalRecord record)> ResolveAll()
        {
            foreach (NetworkDefinition network in _networks)
            {
                foreach (NetworkMemberPosition memberPos in network.Members)
                {
                    if (PortalCensus.TryGetByPosition(memberPos.ToVector3(), out PortalRecord record))
                    {
                        yield return (network, record);
                    }
                }
            }
        }
    }
}
