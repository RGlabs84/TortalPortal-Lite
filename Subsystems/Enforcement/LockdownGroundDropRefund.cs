using System.Collections.Generic;
using UnityEngine;
using TortalPortalLite.Core;

namespace TortalPortalLite.Subsystems.Enforcement
{
    /// <summary>
    /// #253 Ground-Drop Refund. When #252's Placement Policy Engine destroys a freshly-placed portal, this
    /// drops its full build cost on the ground at the site - the server can never touch the placer's
    /// inventory (never networked), but it can create item ZDOs directly.
    ///
    /// Reads the cost from the PREFAB's own Piece.m_resources (Requirement[] of m_resItem/m_amount), not
    /// ObjectDB - Requirement.m_resItem is itself an ItemDrop template whose own m_itemData.m_dropPrefab
    /// is unset (ItemDrop.Awake, which would normally set it up, never runs for a bare prefab reference) -
    /// setting it explicitly before calling the static ItemDrop.DropItem is mandatory, confirmed directly
    /// against the decompile: DropItem's own body does `Object.Instantiate(item.m_dropPrefab, ...)`,
    /// reading rather than deriving that field.
    /// </summary>
    public static class LockdownGroundDropRefund
    {
        private static int _refundsThisSecond;
        private static float _timer;
        private static readonly Dictionary<long, int> _perPlayerThisSecond = new Dictionary<long, int>();

        public static void OnUpdate(float dt)
        {
            _timer += dt;
            if (_timer >= 1f)
            {
                _timer = 0f;
                _refundsThisSecond = 0;
                _perPlayerThisSecond.Clear();
            }
        }

        public static void Refund(int prefabHash, Vector3 pos, long creatorId)
        {
            if (LockdownConfig.GroundDropRefundEnabled?.Value == false || ZNetScene.instance == null)
            {
                return;
            }
            int budget = LockdownConfig.GroundDropRefundMaxPerSecond?.Value ?? 5;
            if (_refundsThisSecond >= budget)
            {
                return;
            }
            _perPlayerThisSecond.TryGetValue(creatorId, out int playerCount);
            if (creatorId != 0L && playerCount >= budget)
            {
                return; // Per-player rate limit too - blunts a repeat-send farming exploit even if the global budget still has room.
            }

            GameObject prefab;
            try
            {
                prefab = ZNetScene.instance.GetPrefab(prefabHash);
            }
            catch
            {
                return;
            }
            Piece piece = prefab != null ? prefab.GetComponent<Piece>() : null;
            if (piece == null || piece.m_resources == null || piece.m_resources.Length == 0)
            {
                return;
            }

            bool anyDropped = false;
            foreach (Piece.Requirement req in piece.m_resources)
            {
                if (req == null || req.m_resItem == null || req.m_amount <= 0)
                {
                    continue;
                }
                try
                {
                    ItemDrop.ItemData data = req.m_resItem.m_itemData.Clone();
                    data.m_dropPrefab = req.m_resItem.gameObject; // Mandatory - ItemDrop.Awake never ran for this bare prefab reference.
                    int maxStack = data.m_shared != null && data.m_shared.m_maxStackSize > 0 ? data.m_shared.m_maxStackSize : req.m_amount;
                    int remaining = req.m_amount;
                    while (remaining > 0)
                    {
                        int chunk = Mathf.Min(remaining, maxStack);
                        Vector3 dropPos = pos + Vector3.up * 0.5f + Random.insideUnitSphere * 0.5f;
                        ItemDrop.DropItem(data, chunk, dropPos, Quaternion.identity);
                        remaining -= chunk;
                        anyDropped = true;
                    }
                }
                catch (System.Exception ex)
                {
                    PortalDebug.LogWarning($"[LockdownGroundDropRefund] failed to drop requirement for '{prefab.name}': {ex.GetType().Name}: {ex.Message}");
                }
            }

            if (anyDropped)
            {
                _refundsThisSecond++;
                if (creatorId != 0L)
                {
                    _perPlayerThisSecond[creatorId] = playerCount + 1;
                }
                PortalDebug.LogInfo($"[LockdownGroundDropRefund] refunded build cost for '{prefab.name}' at {pos:F0}.");
            }
        }
    }
}
