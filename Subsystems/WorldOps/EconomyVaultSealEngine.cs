using System.Collections.Generic;
using TortalPortalLite.Core;
using TortalPortalLite.Core.Data;

namespace TortalPortalLite.Subsystems.WorldOps
{
    /// <summary>Admin declaration for one sealed vault chest (economy.json section "vaultSeals") - a direct world position (not a portal).</summary>
    public sealed class EconomyVaultSealDeclaration
    {
        public EconomyPosition Position = new EconomyPosition();
        public bool Sealed = true;
    }

    /// <summary>
    /// #277 Vault Seal. While the server holds ownership of a container ZDO, no vanilla client can open
    /// it: `Container.Interact` never opens locally, it invokes `RPC_RequestOpen` at the ZDO OWNER
    /// (catalog's own citation :122058); if the server owns the chest, that routed RPC arrives at the
    /// server, `ZRoutedRpc.HandleRoutedRPC` -> `ZNetScene.FindInstance` returns null (no GameObject exists
    /// server-side to receive it) and the request silently evaporates.
    ///
    /// Honest limitation this pass does not close: `ZDOMan.ReleaseNearbyZDOS` reassigns ownership to any
    /// PEER within range roughly every 2s regardless of who currently owns a ZDO, so a true, gap-free
    /// seal needs a Harmony prefix on `ZDO.SetOwner` to refuse that reassignment for a sealed chest (the
    /// WaterBuoyancyEngine `ZdoSetOwnerPatch` pattern the catalog itself cites) - no broker for that
    /// method exists among this mod's five allowed Core/Hooks/ targets, and this wave's rules forbid
    /// installing a new Harmony patch directly. What IS implemented is a fast RE-CLAIM: this engine polls
    /// well under 2s and re-asserts server ownership every tick, which closes the window to "at most one
    /// poll interval after ReleaseNearbyZDOS reassigns it" rather than gap-free.
    ///
    /// // NEEDS NEW HOOK BROKER on ZDO.SetOwner(long): purpose - a prefix that refuses to change the
    /// // owner of a ZDO in this engine's sealed set (mirroring HandleDestroyedZdoHook's first-veto-wins
    /// // shape) would close the ReleaseNearbyZDOS reassignment window entirely instead of merely
    /// // narrowing it to one poll interval.
    /// </summary>
    public static class EconomyVaultSealEngine
    {
        private static int _lastVersion = -1;
        private static List<EconomyVaultSealDeclaration> _vaults = new List<EconomyVaultSealDeclaration>();
        private static float _timer;

        public static void OnUpdate(float dt)
        {
            if (EconomyRegistry.Version != _lastVersion)
            {
                _lastVersion = EconomyRegistry.Version;
                _vaults = EconomyRegistry.Section<EconomyVaultSealDeclaration>("vaultSeals");
            }
            _timer += dt;
            float interval = EconomyConfig.DoorPollSeconds?.Value ?? 0.5f;
            if (_timer < interval || _vaults.Count == 0 || ZDOMan.instance == null)
            {
                return;
            }
            _timer = 0f;

            long serverUid = ZDOMan.GetSessionID();
            foreach (EconomyVaultSealDeclaration decl in _vaults)
            {
                ZDO? chest = EconomyBindingRegistry.FindNearest(decl.Position.ToVector3(), 1f, EconomyBindingRegistry.FixtureKind.Container);
                if (chest == null)
                {
                    continue;
                }
                if (decl.Sealed)
                {
                    if (chest.GetOwner() != serverUid)
                    {
                        chest.SetOwner(serverUid);
                        ZDOMan.instance.ForceSendZDO(chest.m_uid);
                    }
                }
                else if (chest.GetOwner() == serverUid)
                {
                    // Release: hand ownership back to nobody in particular - the next Interact's
                    // RPC_RequestOpen will simply be granted to whichever client asks (vanilla's own
                    // Container.RPC_RequestOpen hands ownership to the requester once granted).
                    chest.SetOwner(0L);
                    ZDOMan.instance.ForceSendZDO(chest.m_uid);
                }
            }
        }
    }
}
