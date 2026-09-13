using System.Collections.Generic;
using UnityEngine;
using TortalPortalLite.Core;

namespace TortalPortalLite.Subsystems.Topology
{
    /// <summary>
    /// One boot-time pass over ZNetScene.instance.m_prefabs (SERVER decompile `public List&lt;GameObject&gt;
    /// m_prefabs` :81921) classifying every prefab by the gameplay MonoBehaviour it carries - trimmed to
    /// just the two component types this build's kept engines need (#187 Bed / #189 Tombstone, both
    /// consumed by #207 Corpse-Run Gate), since none of these prefab NAMES are asset data available from
    /// the decompile alone but the COMPONENT types are ordinary compiled C# this mod already references.
    ///
    /// Only runs once (idempotent) - OnWorldReady per IPortalSubsystem's own contract (first point
    /// ZNetScene.instance.m_prefabs is actually populated).
    /// </summary>
    public static class TargetedPrefabDiscovery
    {
        private static readonly List<int> _bedHashes = new List<int>();
        private static readonly List<int> _tombstoneHashes = new List<int>();
        private static bool _done;

        public static bool IsDone => _done;
        public static IReadOnlyList<int> BedHashes => _bedHashes;
        public static IReadOnlyList<int> TombstoneHashes => _tombstoneHashes;

        public static void OnWorldReady()
        {
            if (_done || ZNetScene.instance == null)
            {
                return;
            }
            _done = true;

            foreach (GameObject prefab in ZNetScene.instance.m_prefabs)
            {
                if (prefab == null)
                {
                    continue;
                }
                int hash = prefab.name.GetStableHashCode();

                if (prefab.GetComponent<Bed>() != null) _bedHashes.Add(hash);
                if (prefab.GetComponent<TombStone>() != null) _tombstoneHashes.Add(hash);
            }

            PortalDebug.LogAlways($"[TargetedPrefabDiscovery] bed={_bedHashes.Count} tombstone={_tombstoneHashes.Count}");
        }
    }
}
