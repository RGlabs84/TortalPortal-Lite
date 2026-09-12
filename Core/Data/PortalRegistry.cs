using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace TortalPortalLite.Core.Data
{
    /// <summary>
    /// The set of portal prefab hashes/names - resolved directly from vanilla's own
    /// Game.instance.PortalPrefabHash (SERVER decompile :100021 - confirmed public
    /// `List&lt;int&gt; PortalPrefabHash { get; private set; }`, populated at :100031 from whatever the
    /// Inspector-configured portal prefab list contains). This settles option #1's own open question
    /// ("which prefabs are in Game.m_portalPrefabs - Inspector data, needs an in-game check") by reading
    /// vanilla's already-computed answer instead of re-deriving it: ZDOMan itself gates a large amount
    /// of portal-specific bookkeeping on this exact same list (:73754, :76576, :76595, :76980, :77572,
    /// :77734), so it is unambiguously the authoritative source, not a guess.
    ///
    /// Available only once Game.instance exists, which on a dedicated server is from Game.Awake -
    /// before ZNetScene.Awake (this mod's own OnWorldReady point). Reads it lazily and caches, so first
    /// access after either is ready works regardless of exact ordering.
    /// </summary>
    public static class PortalRegistry
    {
        private static List<int> _prefabHashes;
        private static List<string> _prefabNames;

        public static IReadOnlyList<int> PrefabHashes
        {
            get
            {
                if (_prefabHashes == null)
                {
                    Discover();
                }
                return _prefabHashes;
            }
        }

        public static IReadOnlyList<string> PrefabNames
        {
            get
            {
                if (_prefabNames == null)
                {
                    Discover();
                }
                return _prefabNames;
            }
        }

        public static void Discover()
        {
            _prefabHashes = new List<int>();
            _prefabNames = new List<string>();
            if (Game.instance == null)
            {
                return;
            }

            _prefabHashes.AddRange(Game.instance.PortalPrefabHash);

            if (ZNetScene.instance != null)
            {
                foreach (int hash in _prefabHashes)
                {
                    GameObject prefab = ZNetScene.instance.GetPrefab(hash);
                    if (prefab != null)
                    {
                        _prefabNames.Add(prefab.name);
                    }
                }
            }

            PortalDebug.LogAlways($"[PortalRegistry] {_prefabHashes.Count} portal prefab hash(es) from Game.PortalPrefabHash" +
                (_prefabNames.Count > 0 ? $": {string.Join(", ", _prefabNames)}" : " (names not resolved yet - ZNetScene not ready)."));
        }

        public static bool IsPortalPrefabHash(int prefabHash) => PrefabHashes.Contains(prefabHash);
    }
}
