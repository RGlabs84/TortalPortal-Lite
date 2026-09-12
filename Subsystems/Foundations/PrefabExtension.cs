using System.Collections.Generic;
using TortalPortalLite.Core;

namespace TortalPortalLite.Subsystems.Foundations
{
    /// <summary>
    /// #138 Non-Portals As Portals - extending Game.PortalPrefabHash at runtime. The property's SETTER
    /// is private, but the LIST OBJECT it returns is not: `Game.instance.PortalPrefabHash.Add(hash)` is
    /// a legal, no-reflection mutation of live game state (SERVER decompile :100021). Everything
    /// portal-specific keys off membership in this exact list (ZDOMan.AddIfPortal :77734, ZDO.SetSector
    /// :73754, ZDOMan.AddObjectsPerChunk :77572, GetSaveClonePerChunk :77621) - adding a hash makes any
    /// FUTURE ZDO of that prefab a portal for all four purposes and puts it in Game.ConnectPortals's
    /// pairing loop. It does NOT retroactively re-file already-instantiated ZDOs of that prefab (AddIfPortal
    /// only runs from CreateNewZDO and the RPC_ZDOData deserialize loop) - so extending must happen before
    /// any ZDO of that prefab exists, ideally at OnWorldReady before any player has created one this session.
    ///
    /// Deliberately conservative: this class only ADDS, never removes - the catalog names removal as the
    /// dangerous half (un-registering an already-tracked prefab does not un-file existing ZDOs from
    /// m_portalObjects, leaving them stuck in a state nothing then correctly maintains).
    /// </summary>
    public static class PrefabExtension
    {
        private static readonly HashSet<int> _extendedByThisMod = new HashSet<int>();

        public static bool IsExtended(int prefabHash) => _extendedByThisMod.Contains(prefabHash);

        /// <summary>Enrolls a prefab hash into Game.PortalPrefabHash if not already present. Safe to call repeatedly (idempotent).</summary>
        public static bool TryExtend(int prefabHash)
        {
            if (Game.instance == null)
            {
                return false;
            }
            if (Game.instance.PortalPrefabHash.Contains(prefabHash))
            {
                return true;
            }
            Game.instance.PortalPrefabHash.Add(prefabHash);
            _extendedByThisMod.Add(prefabHash);
            PortalDebug.LogAlways($"[PrefabExtension] enrolled prefab hash {prefabHash} into Game.PortalPrefabHash - future instances of it are portals for save/sector/pairing purposes.");
            return true;
        }
    }
}
