using System;
using UnityEngine;
using TortalPortalLite.Core;
using TortalPortalLite.Core.Data;

namespace TortalPortalLite.Subsystems.WorldOps
{
    /// <summary>
    /// #273 Station Credit Primitives (Refund by RPC). Pays a refund/reward into a player's own
    /// fireplace/smelter (fuel or ore) instead of dropping items on the ground - driven by invoking the
    /// station's OWN vanilla ZNetView RPC at its owning client, exactly the way this mod already drives
    /// Game.RPC_SetConnection: `ZRoutedRpc.instance.InvokeRoutedRPC(owner, zdoid, method, args)`.
    ///
    /// Two-path rule (catalog's own framing): if the station is owned by a currently-connected peer, send
    /// the RPC so the owning client plays its own add-effect and the player SEES the credit land; if the
    /// station is unowned (zone unloaded, nobody near), write the ZDO field directly - there is no owner
    /// to race and the next client to load the zone reads the current value. Never RPC an unowned ZDO -
    /// InvokeRoutedRPC(0, ...) is a broadcast nobody applies.
    ///
    /// Implements the two station families the catalog documents with fully concrete field/RPC names:
    /// Fireplace (RPC_AddFuelAmount, signed delta, owner-gated, clamped to m_maxFuel in the handler
    /// itself) and Smelter (RPC_AddOre / RPC_AddFuel, owner-gated, NO capacity clamp in the handler - this
    /// engine clamps itself before sending). CookingStation/Fermenter/Beehive credit (also named in the
    /// catalog) is NOT implemented here - flagged rather than guessed, since this pass did not verify
    /// their exact field names to the same standard as the two implemented paths.
    /// </summary>
    public static class EconomyStationCreditEngine
    {
        /// <summary>Finds <paramref name="who"/>'s own station (s_creator == their PlayerId) of the given kind within EconomyConfig.StationScanRadius of their current position.</summary>
        public static ZDO? FindOwnStation(ConnectedCharacter who, EconomyBindingRegistry.FixtureKind kind)
        {
            long playerId = who.PlayerId;
            if (playerId == 0)
            {
                return null;
            }
            float radius = EconomyConfig.StationScanRadius?.Value ?? 16f;
            foreach (ZDO candidate in EconomyBindingRegistry.FindAll(who.Position, radius, kind))
            {
                if (candidate.GetLong(ZDOVars.s_creator, 0L) == playerId)
                {
                    return candidate;
                }
            }
            return null;
        }

        /// <summary>Credits up to <paramref name="amount"/> fuel to a Fireplace ZDO. Returns the amount actually credited (capped to m_maxFuel).</summary>
        public static float CreditFireplaceFuel(ZDO fireplaceZdo, float amount)
        {
            if (fireplaceZdo == null || !fireplaceZdo.IsValid() || amount <= 0f)
            {
                return 0f;
            }
            GameObject? prefab = ZNetScene.instance?.GetPrefab(fireplaceZdo.GetPrefab());
            Fireplace? fp = prefab != null ? prefab.GetComponent<Fireplace>() : null;
            float maxFuel = fp != null ? fp.m_maxFuel : float.MaxValue;
            float current = fireplaceZdo.GetFloat(ZDOVars.s_fuel);
            float room = Mathf.Max(0f, maxFuel - current);
            float toAdd = Mathf.Min(amount, room);
            if (toAdd <= 0f)
            {
                return 0f;
            }

            if (TryRoutedRpc(fireplaceZdo, "RPC_AddFuelAmount", toAdd))
            {
                return toAdd;
            }
            // Unowned - direct write, no race to lose.
            PortalOwnership.ClaimAndWrite(fireplaceZdo, z => z.Set(ZDOVars.s_fuel, Mathf.Clamp(current + toAdd, 0f, maxFuel)));
            return toAdd;
        }

        /// <summary>Credits one unit of named ore into a Smelter's processing queue via RPC_AddOre. The handler performs no capacity check itself (catalog's own citation), so the caller should avoid over-crediting past a reasonable queue depth; this primitive does not second-guess the caller's amount beyond requiring the station be resolvable and owned/reachable.</summary>
        public static bool CreditSmelterOre(ZDO smelterZdo, string oreItemPrefabName)
        {
            if (smelterZdo == null || !smelterZdo.IsValid() || string.IsNullOrEmpty(oreItemPrefabName))
            {
                return false;
            }
            if (TryRoutedRpc(smelterZdo, "RPC_AddOre", oreItemPrefabName, false))
            {
                return true;
            }
            // Unowned smelter: mirror QueueOre's own direct-write recipe (catalog's citation :142279-142285).
            try
            {
                int queueSize = smelterZdo.GetInt(ZDOVars.s_queued);
                PortalOwnership.ClaimAndWrite(smelterZdo, z =>
                {
                    z.Set("item" + queueSize, oreItemPrefabName);
                    z.Set(ZDOVars.s_queued, queueSize + 1);
                });
                return true;
            }
            catch (Exception ex)
            {
                PortalDebug.LogWarning($"[EconomyStationCreditEngine] direct ore credit failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>Credits one unit of Smelter fuel via RPC_AddFuel (no amount parameter server-side - vanilla's own handler adds exactly 1 per call).</summary>
        public static bool CreditSmelterFuel(ZDO smelterZdo)
        {
            if (smelterZdo == null || !smelterZdo.IsValid())
            {
                return false;
            }
            if (TryRoutedRpc(smelterZdo, "RPC_AddFuel"))
            {
                return true;
            }
            PortalOwnership.ClaimAndWrite(smelterZdo, z => z.Set(ZDOVars.s_fuel, z.GetFloat(ZDOVars.s_fuel) + 1f));
            return true;
        }

        private static bool TryRoutedRpc(ZDO zdo, string method, params object[] args)
        {
            long owner = zdo.GetOwner();
            if (owner == 0L || ZRoutedRpc.instance == null || ZNet.instance == null)
            {
                return false;
            }
            ZNetPeer? peer = ZNet.instance.GetPeer(owner);
            if (peer == null || !peer.IsReady())
            {
                return false;
            }
            try
            {
                ZRoutedRpc.instance.InvokeRoutedRPC(owner, zdo.m_uid, method, args);
                return true;
            }
            catch (Exception ex)
            {
                PortalDebug.LogWarning($"[EconomyStationCreditEngine] RPC '{method}' failed: {ex.Message}");
                return false;
            }
        }
    }
}
