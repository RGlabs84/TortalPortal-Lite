using System;
using System.Collections.Generic;
using UnityEngine;
using TortalPortalLite.Core;
using TortalPortalLite.Core.Data;
using TortalPortalLite.Subsystems.Foundations;

namespace TortalPortalLite.Subsystems.Enforcement
{
    /// <summary>One admin-declared key-item binding for #59 - portal, its intended partner, and the bound container, all identified by rounded position.</summary>
    public sealed class KeyItemBinding
    {
        public Vector3 PortalPosition;
        public Vector3 PartnerPosition;
        public Vector3 ContainerPosition;
        public string ItemName = "";
        public int RequiredCount = 1;
        public bool Consume;
        public bool TollPaid; // once true for a Consume binding, stays open regardless of the container's later contents.
    }

    /// <summary>
    /// #59 Key-Item Requirement. The server cannot see a traveller's CARRIED inventory at all -
    /// `Player.Save` writes it only into that client's own local .fch file, and
    /// `ZNet.SaveOtherPlayerProfiles` just tells each client to save its own disk file and returns
    /// nothing. What the server CAN read is a container's `ZDOVars.s_items` byte blob
    /// (`Container.Save()`'s one live write path) - so "must be carrying X" is impossible, "must have
    /// DEPOSITED X in this chest" is easy and is what this engine implements.
    ///
    /// Simplification vs the catalog's own "nearest container auto-detected by radius" design: this
    /// codebase does not port Wonderland's ContainerRegistry helper (no reliable "is this ZDO a
    /// container" test exists here without it), so the container is an explicit admin-designated
    /// position rather than auto-discovered - functionally identical once bound, just configured
    /// explicitly instead of inferred. Bindings are in-memory only for the same time-budget reason
    /// AccessProgressionGateEngine's requirement map is.
    ///
    /// Genuinely server-enforced: an unconnected portal cannot teleport anyone
    /// (TeleportWorld.Teleport gate 1 is `!TargetFound()`, and the destination is re-read from the
    /// live ZDO connection at the instant of transit) - a modified client cannot conjure a connection
    /// this engine did not write.
    /// </summary>
    public static class AccessKeyItemRequirementEngine
    {
        private static readonly Dictionary<Vector3, KeyItemBinding> _bindings = new Dictionary<Vector3, KeyItemBinding>();
        private static float _timer;

        public static void Bind(Vector3 portalPos, Vector3 partnerPos, Vector3 containerPos, string itemName, int requiredCount, bool consume)
        {
            Vector3 key = AccessAclStore.RoundPos(portalPos);
            _bindings[key] = new KeyItemBinding
            {
                PortalPosition = key,
                PartnerPosition = AccessAclStore.RoundPos(partnerPos),
                ContainerPosition = AccessAclStore.RoundPos(containerPos),
                ItemName = itemName,
                RequiredCount = Math.Max(1, requiredCount),
                Consume = consume
            };
        }

        public static bool Unbind(Vector3 portalPos) => _bindings.Remove(AccessAclStore.RoundPos(portalPos));

        public static IEnumerable<KeyItemBinding> All() => _bindings.Values;

        public static void OnUpdate(float dt)
        {
            _timer += dt;
            float interval = AccessConfig.KeyItemScanSeconds?.Value ?? 2f;
            if (_timer < interval || ZDOMan.instance == null)
            {
                return;
            }
            _timer = 0f;

            foreach (KeyItemBinding binding in _bindings.Values)
            {
                try
                {
                    Evaluate(binding);
                }
                catch (Exception ex)
                {
                    PortalDebug.LogWarning($"[AccessKeyItemRequirementEngine] evaluation failed for binding at {binding.PortalPosition}: {ex.Message}");
                }
            }
        }

        private static void Evaluate(KeyItemBinding binding)
        {
            if (binding.Consume && binding.TollPaid)
            {
                EnsureConnected(binding);
                return;
            }

            if (!PortalCensus.TryGetByPosition(binding.ContainerPosition, out PortalRecord containerRecord))
            {
                return; // container not currently resident (unloaded region) - fail closed, leave state as-is rather than force-open.
            }
            ZDO? containerZdo = ZDOMan.instance.GetZDO(containerRecord.Uid);
            if (containerZdo == null || !containerZdo.IsValid())
            {
                return;
            }

            Inventory? inv = ZdoInventoryIO.Load(containerZdo, 10, 10);
            if (inv == null)
            {
                return; // gone or currently open in a player's UI - skip this tick rather than guess.
            }

            int count = 0;
            foreach (ItemDrop.ItemData item in inv.GetAllItems())
            {
                if (string.Equals(item.m_shared.m_name, binding.ItemName, StringComparison.OrdinalIgnoreCase))
                {
                    count += item.m_stack;
                }
            }

            bool satisfied = count >= binding.RequiredCount;
            if (satisfied && binding.Consume)
            {
                int toRemove = binding.RequiredCount;
                foreach (ItemDrop.ItemData item in inv.GetAllItems())
                {
                    if (toRemove <= 0)
                    {
                        break;
                    }
                    if (!string.Equals(item.m_shared.m_name, binding.ItemName, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    int take = Math.Min(toRemove, item.m_stack);
                    inv.RemoveItem(item, take);
                    toRemove -= take;
                }
                ZdoInventoryIO.Save(containerZdo, inv);
                ItemLedger.RecordTransfer("AccessKeyItemRequirementEngine", binding.ItemName, binding.RequiredCount);
                binding.TollPaid = true;
            }

            if (satisfied)
            {
                EnsureConnected(binding);
            }
            else
            {
                EnsureDisconnected(binding);
            }
        }

        private static void EnsureConnected(KeyItemBinding binding)
        {
            if (!PortalCensus.TryGetByPosition(binding.PortalPosition, out PortalRecord portal)
                || !PortalCensus.TryGetByPosition(binding.PartnerPosition, out PortalRecord partner))
            {
                return;
            }
            ZDO? zdo = ZDOMan.instance.GetZDO(portal.Uid);
            if (zdo == null || !zdo.IsValid() || zdo.GetConnectionZDOID(ZDOExtraData.ConnectionType.Portal) == partner.Uid)
            {
                return;
            }
            PortalOwnership.ClaimAndWrite(zdo, z => z.SetConnection(ZDOExtraData.ConnectionType.Portal, partner.Uid));
            ZDOMan.instance.ForceSendZDO(partner.Uid); // #59's own failure mode: the first walk-through to a newly-connected distant partner fails silently unless pre-pushed.
        }

        private static void EnsureDisconnected(KeyItemBinding binding)
        {
            if (!PortalCensus.TryGetByPosition(binding.PortalPosition, out PortalRecord portal))
            {
                return;
            }
            ZDO? zdo = ZDOMan.instance.GetZDO(portal.Uid);
            if (zdo == null || !zdo.IsValid() || zdo.GetConnectionZDOID(ZDOExtraData.ConnectionType.Portal) == ZDOID.None)
            {
                return;
            }
            PortalOwnership.ClaimAndWrite(zdo, z => z.SetConnection(ZDOExtraData.ConnectionType.Portal, ZDOID.None));
        }
    }
}
