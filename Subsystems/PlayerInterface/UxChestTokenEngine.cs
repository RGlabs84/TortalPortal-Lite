using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using TortalPortalLite.Core;
using TortalPortalLite.Core.Data;
using TortalPortalLite.Core.Hooks;
using TortalPortalLite.Subsystems.Foundations;
using TortalPortalLite.Subsystems.Topology;

namespace TortalPortalLite.Subsystems.PlayerInterface
{
    public sealed class UxChestBindingsFile
    {
        public int SchemaVersion = 1;

        /// <summary>Item display name (ItemDrop.ItemData.m_shared.m_name, e.g. "$item_coal") -&gt; destination alias. One matching item is consumed as the toll.</summary>
        public Dictionary<string, string> TokenToDestination = new Dictionary<string, string>();
    }

    /// <summary>
    /// #162 Chest tokens - item type as destination, stack count as index. The only channel that can
    /// also charge a fee: a Container's whole inventory is one serialized blob on `ZDOVars.s_items`
    /// (`Container.Save`), decoded/re-encoded through the shared `Core/Data/ZdoInventoryIO` (a scratch
    /// `Inventory` over the blob - no live Container/GameObject ever needed). `ZDOVars.s_inUse`
    /// (`Container.UpdateUseVisual`) is checked first: never touch a chest's blob while a player has its
    /// UI open, or the write races the client's own `Save()` (the catalog's own explicit warning) -
    /// `ZdoInventoryIO.IsBusy` wraps exactly that check.
    /// </summary>
    public static class UxChestTokenEngine
    {
        private static Dictionary<string, string> _bindings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static DateTime _fileStamp = DateTime.MinValue;

        public static void Initialize()
        {
            RpcZdoDataHook.RegisterPostfix(100, OnZdoDataFromClient);
        }

        public static void OnUpdate(float dt)
        {
            if (UxConfig.Enabled?.Value == false || UxConfig.ChestTokenEnabled?.Value == false)
            {
                return;
            }
            TryReloadBindings();
        }

        private static void TryReloadBindings()
        {
            try
            {
                string path = UxFilePaths.Resolve(UxConfig.ChestTokenBindingsFile, "ux_chest_bindings.json");
                if (!File.Exists(path))
                {
                    return;
                }
                DateTime stamp = File.GetLastWriteTimeUtc(path);
                if (stamp == _fileStamp)
                {
                    return;
                }
                UxChestBindingsFile parsed = JsonConvert.DeserializeObject<UxChestBindingsFile>(File.ReadAllText(path));
                if (parsed?.TokenToDestination == null)
                {
                    return;
                }
                _bindings = new Dictionary<string, string>(parsed.TokenToDestination, StringComparer.OrdinalIgnoreCase);
                _fileStamp = stamp;
                PortalDebug.LogAlways($"[UxChestTokenEngine] loaded {_bindings.Count} token->destination binding(s) from '{path}'.");
            }
            catch (Exception ex)
            {
                PortalDebug.LogError($"[UxChestTokenEngine] failed to load bindings: {ex.Message}");
            }
        }

        private static void OnZdoDataFromClient(ZNetPeer? sender, ZDOID zdoid)
        {
            if (UxConfig.Enabled?.Value == false || UxConfig.ChestTokenEnabled?.Value == false || _bindings.Count == 0)
            {
                return;
            }
            if (ZDOMan.instance == null)
            {
                return;
            }
            ZDO chest = ZDOMan.instance.GetZDO(zdoid);
            if (chest == null || !chest.IsValid() || !TargetedPrefabDiscovery.IsContainer(chest.GetPrefab()))
            {
                return;
            }
            if (ZdoInventoryIO.IsBusy(chest))
            {
                return; // player still has it open - the write would race their own Save()
            }

            float radius = UxConfig.ChestDiscoveryRadius?.Value ?? 8f;
            if (!UxAddressBook.TryNearestAnyPortal(chest.GetPosition(), radius, out PortalRecord portal))
            {
                return;
            }
            ZDO portalZdo = ZDOMan.instance.GetZDO(portal.Uid);
            if (portalZdo == null || !portalZdo.IsValid())
            {
                return;
            }

            Inventory inv = ZdoInventoryIO.Load(chest, 10, 10);
            if (inv == null)
            {
                return;
            }

            ItemDrop.ItemData match = null;
            string destAlias = null;
            foreach (ItemDrop.ItemData item in inv.GetAllItems())
            {
                if (_bindings.TryGetValue(item.m_shared.m_name, out string alias))
                {
                    match = item;
                    destAlias = alias;
                    break;
                }
            }
            if (match == null || !UxAddressBook.TryGet(destAlias, out PortalRecord dest))
            {
                return;
            }

            ConnectedCharacter? requester = sender != null ? ResolveByPeer(sender) : null;
            if (!UxDialAction.TryDialToRecord(portalZdo, dest, requester, out string message))
            {
                return;
            }

            inv.RemoveItem(match, 1);
            ZdoInventoryIO.Save(chest, inv);
            ItemLedger.RecordTransfer("UxChestTokenEngine", match.m_shared.m_name, 1);
        }

        private static ConnectedCharacter? ResolveByPeer(ZNetPeer peer)
        {
            foreach (ConnectedCharacter cc in ConnectedCharacters.All())
            {
                if (cc.Peer == peer)
                {
                    return cc;
                }
            }
            return null;
        }
    }
}
