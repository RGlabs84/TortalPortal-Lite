using System;
using System.Collections.Generic;
using TortalPortalLite.Core;
using TortalPortalLite.Core.Data;

namespace TortalPortalLite.Subsystems.PlayerInterface
{
    /// <summary>
    /// #272 The Cartographer's Table - opt-in named-pin sync via a server-written MapTable. Unlike #170
    /// (a one-shot, unremovable, per-player push), this is a live, RE-readable, RE-syncable surface: the
    /// server periodically rewrites an adopted MapTable's `s_data` blob (the exact byte format
    /// `Minimap.GetSharedMapData` produces - version int, an explored-tile bool array, then one
    /// {ownerId, name, pos, PinType, checked, authorPlatformId} record per pin), and `MapTable.OnRead` -
    /// entirely CLIENT-side, no server RPC involved - decompresses and applies it the moment a player
    /// presses the table's own Read switch. `Minimap.AddSharedMapData` deletes any of the READER's own
    /// saved pins whose owner id matches an incoming batch's sentinel but is no longer listed, which is
    /// what makes a retired portal's pin actually disappear on a later re-read - the removable surface
    /// the catalog says nothing else in this toolkit provides.
    ///
    /// `MapTable` carries no component in Topology's own `TargetedPrefabDiscovery` (it only classifies
    /// Bed/TombStone/Sign/ItemStand/PrivateArea/Container/Piece/Ship), so this engine does its own small,
    /// one-time prefab scan for it - the same "classify by component" recipe, just for one more type.
    ///
    /// KNOWN SIMPLIFICATION: a player reading this table will also drop any pins they previously received
    /// from an unrelated, non-mod MapTable elsewhere (`AddSharedMapData` deletes any non-own pin not in
    /// the CURRENT batch) - the catalog's own suggested mitigation is to union in every other table's
    /// blob before writing, which this engine does not do (out of scope for a "small effort" catalog
    /// entry); documented here rather than silently accepted.
    /// </summary>
    public static class UxCartographersTableEngine
    {
        private const long SentinelOwnerId = 0x544F5254414C0001L;

        private static readonly List<int> _mapTablePrefabHashes = new List<int>();
        private static bool _prefabsDiscovered;
        private static ZdoSpatialQuery.PrefabSetSweeper? _sweeper;
        private static readonly HashSet<ZDOID> _knownTables = new HashSet<ZDOID>();
        private static int _probedTextureSize = -1;
        private static float _timer;

        public static void OnWorldReady()
        {
            if (_prefabsDiscovered || ZNetScene.instance == null)
            {
                return;
            }
            _prefabsDiscovered = true;
            foreach (UnityEngine.GameObject prefab in ZNetScene.instance.m_prefabs)
            {
                if (prefab != null && prefab.GetComponent<MapTable>() != null)
                {
                    _mapTablePrefabHashes.Add(prefab.name.GetStableHashCode());
                }
            }
            PortalDebug.LogAlways($"[UxCartographersTableEngine] found {_mapTablePrefabHashes.Count} MapTable prefab variant(s).");
        }

        public static void OnUpdate(float dt)
        {
            if (UxConfig.Enabled?.Value == false || UxConfig.CartographersTableEnabled?.Value == false)
            {
                return;
            }
            SweepDiscovery();

            _timer += dt;
            float interval = UxConfig.CartographersTableRewriteSeconds?.Value ?? 15f;
            if (_timer < interval)
            {
                return;
            }
            _timer = 0f;
            RewriteAll();
        }

        private static void SweepDiscovery()
        {
            if (_mapTablePrefabHashes.Count == 0 || ZDOMan.instance == null)
            {
                return;
            }
            _sweeper ??= new ZdoSpatialQuery.PrefabSetSweeper(_mapTablePrefabHashes);
            var results = new List<ZDO>();
            _sweeper.Advance(10, results);
            foreach (ZDO z in results)
            {
                if (z != null && z.IsValid())
                {
                    _knownTables.Add(z.m_uid);
                }
            }
        }

        private static void RewriteAll()
        {
            if (ZDOMan.instance == null || _knownTables.Count == 0)
            {
                return;
            }
            foreach (ZDOID id in _knownTables)
            {
                ZDO zdo = ZDOMan.instance.GetZDO(id);
                if (zdo == null || !zdo.IsValid())
                {
                    continue;
                }
                try
                {
                    WriteBlob(zdo);
                }
                catch (Exception ex)
                {
                    PortalDebug.LogError($"[UxCartographersTableEngine] blob write failed for {id}: {ex.Message}");
                }
            }
        }

        private static void WriteBlob(ZDO tableZdo)
        {
            ProbeTextureSizeIfNeeded(tableZdo);
            int textureSize = _probedTextureSize > 0 ? _probedTextureSize : 256; // Minimap.m_textureSize's compiled default - Inspector-overridable, hence the probe above
            int exploredLength = textureSize * textureSize;

            var pkg = new ZPackage();
            pkg.Write(3); // Version.SharedMap.PinsAuthor
            pkg.Write(exploredLength);
            for (int i = 0; i < exploredLength; i++)
            {
                pkg.Write(false); // never touch exploration state - false never changes it, per Minimap.AddSharedMapData
            }

            List<UxAddressBook.AddressEntry> ordered = UxAddressBook.Ordered();
            pkg.Write(ordered.Count);
            foreach (UxAddressBook.AddressEntry e in ordered)
            {
                pkg.Write(SentinelOwnerId);
                pkg.Write(e.Name);
                pkg.Write(e.Record.Position);
                pkg.Write((int)Minimap.PinType.Icon3);
                pkg.Write(false);
                pkg.Write("");
            }

            byte[] compressed = Utils.Compress(pkg.GetArray());
            PortalOwnership.ClaimAndWrite(tableZdo, z => z.Set(ZDOVars.s_data, compressed));
        }

        private static void ProbeTextureSizeIfNeeded(ZDO tableZdo)
        {
            if (_probedTextureSize > 0)
            {
                return;
            }
            byte[] existing = tableZdo.GetByteArray(ZDOVars.s_data);
            if (existing == null || existing.Length == 0)
            {
                return;
            }
            try
            {
                byte[] raw = Utils.Decompress(existing);
                var probe = new ZPackage(raw);
                probe.ReadInt(); // version
                int exploredLen = probe.ReadInt();
                if (exploredLen > 0)
                {
                    _probedTextureSize = (int)Math.Sqrt(exploredLen);
                }
            }
            catch
            {
                // Not our own format yet (e.g. a player's own prior write) - keep the 256 default.
            }
        }
    }
}
