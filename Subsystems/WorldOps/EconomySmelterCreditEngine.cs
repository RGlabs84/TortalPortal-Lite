using System.Collections.Generic;
using UnityEngine;
using TortalPortalLite.Core;
using TortalPortalLite.Core.Data;

namespace TortalPortalLite.Subsystems.WorldOps
{
    /// <summary>
    /// #274 Smelter Output Queue Credit (Finished Goods). Credits FINISHED bars, not raw ore: writes
    /// `ZDOVars.s_spawnOre`/`s_spawnAmount` directly (a plain field pair vanilla's own `QueueProcessed`
    /// already uses, not an RPC - catalog's own citation :142484-142485) and lets the owning client's own
    /// `Smelter.UpdateSmelter` 1s tick spawn the stack at the output chute via `SpawnProcessed`, exactly
    /// as if a normal batch had completed. Works for the whole Smelter-family prefab set (smelter, blast
    /// furnace, kiln, spinning wheel, windmill, eitr refinery all share this component).
    ///
    /// The ore NAME (the `m_from` prefab name, not the bar's own name) is what `GetItemConversion`
    /// matches on (catalog's own citation :142548-142557) - resolved here from the smelter prefab's own
    /// public `m_conversion` list so a caller only ever has to know the raw ore's name, and capped at the
    /// output item's own `m_maxStackSize` per the catalog's own failure-mode note (an uncapped amount
    /// spawns one over-stacked ItemDrop).
    /// </summary>
    public static class EconomySmelterCreditEngine
    {
        private static readonly Dictionary<int, Smelter?> _prefabComponentCache = new Dictionary<int, Smelter?>();

        /// <summary>Credits up to <paramref name="amount"/> finished units of whatever <paramref name="oreFromName"/> converts to. Returns the amount actually credited (0 if the smelter already has a pending spawn, the ore isn't a recognised input, or the smelter cannot be resolved).</summary>
        public static int CreditFinishedGoods(ZDO smelterZdo, string oreFromName, int amount)
        {
            if (smelterZdo == null || !smelterZdo.IsValid() || amount <= 0 || string.IsNullOrEmpty(oreFromName))
            {
                return 0;
            }

            // A pending spawn already occupies the single s_spawnOre/s_spawnAmount slot - do not clobber it.
            if (!string.IsNullOrEmpty(smelterZdo.GetString(ZDOVars.s_spawnOre, "")) && smelterZdo.GetInt(ZDOVars.s_spawnAmount) > 0)
            {
                return 0;
            }

            Smelter? component = ResolveComponent(smelterZdo.GetPrefab());
            if (component == null)
            {
                return 0;
            }

            int maxStack = 1;
            bool recognised = false;
            foreach (Smelter.ItemConversion conversion in component.m_conversion)
            {
                if (conversion.m_from != null && conversion.m_from.gameObject.name == oreFromName)
                {
                    recognised = true;
                    if (conversion.m_to != null)
                    {
                        maxStack = Mathf.Max(1, conversion.m_to.m_itemData.m_shared.m_maxStackSize);
                    }
                    break;
                }
            }
            if (!recognised)
            {
                return 0;
            }

            int toCredit = Mathf.Min(amount, maxStack);
            PortalOwnership.ClaimAndWrite(smelterZdo, z =>
            {
                z.Set(ZDOVars.s_spawnOre, oreFromName);
                z.Set(ZDOVars.s_spawnAmount, toCredit);
            });
            ItemLedger.RecordTransfer("EconomySmelterCredit", oreFromName, toCredit);
            return toCredit;
        }

        private static Smelter? ResolveComponent(int prefabHash)
        {
            if (_prefabComponentCache.TryGetValue(prefabHash, out Smelter? cached))
            {
                return cached;
            }
            GameObject? prefab = ZNetScene.instance?.GetPrefab(prefabHash);
            Smelter? smelter = prefab != null ? prefab.GetComponent<Smelter>() : null;
            _prefabComponentCache[prefabHash] = smelter;
            return smelter;
        }
    }
}
