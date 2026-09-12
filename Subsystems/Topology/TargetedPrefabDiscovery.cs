using System.Collections.Generic;
using UnityEngine;
using TortalPortalLite.Core;

namespace TortalPortalLite.Subsystems.Topology
{
    /// <summary>
    /// One boot-time pass over ZNetScene.instance.m_prefabs (SERVER decompile `public List&lt;GameObject&gt;
    /// m_prefabs` :81921) classifying every prefab by the gameplay MonoBehaviour it carries - the
    /// "Wonderland ContainerRegistry pattern" several catalog options (#187 Bed, #189 Tombstone, #192
    /// Player-Placed Anchor, #195 Base Centroid) cite as their own prerequisite, since none of these
    /// prefab NAMES are asset data available from the decompile alone but the COMPONENT types are
    /// ordinary compiled C# this mod already references.
    ///
    /// Only runs once (idempotent) - OnWorldReady per IPortalSubsystem's own contract (first point
    /// ZNetScene.instance.m_prefabs is actually populated).
    /// </summary>
    public static class TargetedPrefabDiscovery
    {
        private static readonly List<int> _bedHashes = new List<int>();
        private static readonly List<int> _tombstoneHashes = new List<int>();
        private static readonly List<int> _signHashes = new List<int>();
        private static readonly List<int> _itemStandHashes = new List<int>();
        private static readonly List<int> _privateAreaHashes = new List<int>();
        private static readonly List<int> _containerHashes = new List<int>();
        private static readonly List<int> _pieceHashes = new List<int>();
        private static readonly List<int> _shipHashes = new List<int>();
        private static bool _done;

        public static bool IsDone => _done;
        public static IReadOnlyList<int> BedHashes => _bedHashes;
        public static IReadOnlyList<int> TombstoneHashes => _tombstoneHashes;
        public static IReadOnlyList<int> SignHashes => _signHashes;
        public static IReadOnlyList<int> ItemStandHashes => _itemStandHashes;
        public static IReadOnlyList<int> PrivateAreaHashes => _privateAreaHashes;
        public static IReadOnlyList<int> ContainerHashes => _containerHashes;
        public static IReadOnlyList<int> PieceHashes => _pieceHashes;
        public static IReadOnlyList<int> ShipHashes => _shipHashes;

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
                if (prefab.GetComponent<Sign>() != null) _signHashes.Add(hash);
                if (prefab.GetComponent<ItemStand>() != null) _itemStandHashes.Add(hash);
                if (prefab.GetComponent<PrivateArea>() != null) _privateAreaHashes.Add(hash);
                if (prefab.GetComponent<Container>() != null) _containerHashes.Add(hash);
                if (prefab.GetComponent<Piece>() != null) _pieceHashes.Add(hash);
                if (prefab.GetComponent<ShipControlls>() != null) _shipHashes.Add(hash);
            }

            PortalDebug.LogAlways("[TargetedPrefabDiscovery] " +
                $"bed={_bedHashes.Count} tombstone={_tombstoneHashes.Count} sign={_signHashes.Count} " +
                $"itemStand={_itemStandHashes.Count} privateArea={_privateAreaHashes.Count} " +
                $"container={_containerHashes.Count} piece={_pieceHashes.Count} ship={_shipHashes.Count}");
        }

        public static bool IsBed(int prefabHash) => _bedHashes.Contains(prefabHash);
        public static bool IsTombstone(int prefabHash) => _tombstoneHashes.Contains(prefabHash);
        public static bool IsSign(int prefabHash) => _signHashes.Contains(prefabHash);
        public static bool IsItemStand(int prefabHash) => _itemStandHashes.Contains(prefabHash);
        public static bool IsPrivateArea(int prefabHash) => _privateAreaHashes.Contains(prefabHash);
        public static bool IsContainer(int prefabHash) => _containerHashes.Contains(prefabHash);
        public static bool IsPiece(int prefabHash) => _pieceHashes.Contains(prefabHash);
        public static bool IsShip(int prefabHash) => _shipHashes.Contains(prefabHash);
    }
}
