using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using TortalPortalLite.Core;
using TortalPortalLite.Core.Data;
using TortalPortalLite.Subsystems.Foundations;

namespace TortalPortalLite.Subsystems.PlayerInterface
{
    public sealed class UxEquipBindingsFile
    {
        public int SchemaVersion = 1;

        /// <summary>Item prefab name (e.g. "Hammer", "PickaxeAntler") -&gt; address-book destination alias. Case-insensitive on the item side.</summary>
        public Dictionary<string, string> ItemToDestination = new Dictionary<string, string>();
    }

    /// <summary>
    /// #160 Equipped item as a destination selector. `VisEquipment` mirrors every VISIBLE equipped item
    /// into the owning character's ZDO as a prefab stable hash (`s_rightItem` etc, written client-side by
    /// the owner) - the only channel that is simultaneously zero extra actions, physically legible to
    /// bystanders and thematically native. Reads `s_rightItem` only (the catalog's primary slot); the
    /// back/left/helmet slots are visible too but left to an admin's binding file rather than hard-coded
    /// here.
    ///
    /// CRITICAL LIMIT the catalog is explicit about: only EQUIPPED items are ever mirrored into a ZDO - a
    /// living player's inventory never is (`s_items` is a Container-only field). So this can only be
    /// "hold this to mean that", never "carry N of this", and never a fee (see #162 Chest Tokens for the
    /// one channel that CAN charge).
    /// </summary>
    public static class UxEquipItemSelectorEngine
    {
        private static Dictionary<string, string> _bindings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static DateTime _fileStamp = DateTime.MinValue;
        private static readonly Dictionary<long, int> _lastRightItemHash = new Dictionary<long, int>();
        private static float _timer;

        public static void OnUpdate(float dt)
        {
            if (UxConfig.Enabled?.Value == false || UxConfig.EquipSelectorEnabled?.Value == false)
            {
                return;
            }
            TryReloadBindings();

            _timer += dt;
            float interval = UxConfig.EquipPollSeconds?.Value ?? 0.5f;
            if (_timer < interval)
            {
                return;
            }
            _timer = 0f;
            Poll();
        }

        private static void TryReloadBindings()
        {
            try
            {
                string path = UxFilePaths.Resolve(UxConfig.EquipBindingsFile, "ux_equip_bindings.json");
                if (!File.Exists(path))
                {
                    return;
                }
                DateTime stamp = File.GetLastWriteTimeUtc(path);
                if (stamp == _fileStamp)
                {
                    return;
                }
                UxEquipBindingsFile parsed = JsonConvert.DeserializeObject<UxEquipBindingsFile>(File.ReadAllText(path));
                if (parsed?.ItemToDestination == null)
                {
                    return;
                }
                _bindings = new Dictionary<string, string>(parsed.ItemToDestination, StringComparer.OrdinalIgnoreCase);
                _fileStamp = stamp;
                PortalDebug.LogAlways($"[UxEquipItemSelectorEngine] loaded {_bindings.Count} item->destination binding(s) from '{path}'.");
            }
            catch (Exception ex)
            {
                PortalDebug.LogError($"[UxEquipItemSelectorEngine] failed to load bindings: {ex.Message}");
            }
        }

        private static void Poll()
        {
            if (_bindings.Count == 0 || ZNetScene.instance == null || ZDOMan.instance == null)
            {
                return;
            }
            foreach (ConnectedCharacter who in ConnectedCharacters.All())
            {
                int hash = who.Zdo.GetInt(ZDOVars.s_rightItem, 0);
                bool changed = !_lastRightItemHash.TryGetValue(who.PlayerId, out int last) || last != hash;
                _lastRightItemHash[who.PlayerId] = hash;
                if (!changed || hash == 0)
                {
                    continue;
                }
                if (!UxArmingGate.IsArmed(who.PlayerId))
                {
                    continue; // mandatory - equipping happens constantly in normal play (the catalog's own failure mode)
                }

                UnityEngine.GameObject prefab = ZNetScene.instance.GetPrefab(hash);
                if (prefab == null || !_bindings.TryGetValue(prefab.name, out string destAlias))
                {
                    continue;
                }
                if (!UxArmingGate.TryGetArmedPortal(who.PlayerId, out PortalRecord source) ||
                    !UxAddressBook.TryGet(destAlias, out PortalRecord dest))
                {
                    continue;
                }
                ZDO sourceZdo = ZDOMan.instance.GetZDO(source.Uid);
                if (sourceZdo == null || !sourceZdo.IsValid())
                {
                    continue;
                }
                UxArmingGate.Touch(who.PlayerId);
                UxDialAction.TryDialToRecord(sourceZdo, dest, who, out _);
            }
        }
    }
}
