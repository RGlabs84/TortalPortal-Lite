using System;
using System.Collections.Generic;
using UnityEngine;
using TortalPortalLite.Core;
using TortalPortalLite.Core.Data;

namespace TortalPortalLite.Subsystems.WorldOps
{
    /// <summary>Admin declaration of the server treasury's physical chest (economy.json section "treasury" - at most one entry is meaningful; the first is used).</summary>
    public sealed class EconomyTreasuryDeclaration
    {
        public EconomyPosition Position = new EconomyPosition();
        public string ContainerPrefab = "piece_chest_wood";
    }

    /// <summary>One collective-unlock spend target (economy.json section "treasurySpends").</summary>
    public sealed class EconomyTreasurySpendDeclaration
    {
        public string Key = "";
        public string Label = "";
        public string Currency = "Coins";
        public int Cost = 20000;

        /// <summary>Real seconds the unlock stays active once funded; 0 = permanent once bought (never auto-revoked).</summary>
        public float DurationSeconds = 0f;

        /// <summary>0 disables. A community bonus that rides along with the unlock: every online player's own nearest Fireplace (#273 Station Credit Primitives - the "refund into a player's own station instead of the ground" primitive) is credited this much fuel the moment the spend fires, so a collective unlock feels tangible even for a player who never sees the vault.</summary>
        public float RewardFireplaceFuel = 0f;

        /// <summary>Optional companion to RewardFireplaceFuel: if both this and RewardSmelterOreAmount are set, every online player's own nearest Smelter is credited that many finished units of this raw-ore name (#274 Smelter Output Queue Credit) - e.g. an "Iron Boom" unlock crediting real bars, not just fuel.</summary>
        public string? RewardSmelterOreName;
        public int RewardSmelterOreAmount = 0;
    }

    /// <summary>
    /// #100 Server Treasury, Tax Skim and Collective Unlocks. Fabricates a real, server-owned Container
    /// ZDO that every toll/turnstile debit can route a skim into (EconomyTollEscrowEngine's own
    /// TreasurySkim field), then spends the accumulated balance on world-scale unlocks nobody could buy
    /// individually - implemented here as CUSTOM `mod_route_*`/`mod_treasury_*` global keys only
    /// (ZoneSystem.SetGlobalKey/RemoveGlobalKey with an arbitrary string, which any name not matching a
    /// GlobalKeys enum member automatically becomes - catalog's own citation, ZoneSystem.GetKeyValue
    /// classifies it NonServerOption and ZoneSystem.Save persists it in the .db).
    ///
    /// Deliberately NOT spending on the catalog's own suggested TeleportAll/NoPortals/DungeonBuild world-
    /// modifier keys: Subsystems/Enforcement/LockdownGlobalKeyEngine.cs (Wave 2, already built) already
    /// owns and unconditionally reasserts those exact four keys from its own private want-flags every
    /// tick - a treasury-funded SetGlobalKey("teleportall") here would be silently reverted by that
    /// engine's very next tick (it wants "false" unless something calls its own setters, which this
    /// domain has no business calling). Rather than fight a concretely-identified sibling-domain owner,
    /// this engine's spend targets are exclusively this mod's own custom keys - still delivers the
    /// catalog's core idea (a collective, server-wide, individually-unaffordable unlock) without the
    /// cross-domain collision. The 17 numeric Game.UpdateWorldRates keys are likewise out of scope for
    /// this pass (no existing precedent in this codebase to build on, and each is its own small design
    /// surface) - flagged here rather than half-implemented.
    /// </summary>
    public static class EconomyTreasuryEngine
    {
        private static int _lastVersion = -1;
        private static List<EconomyTreasuryDeclaration> _treasuries = new List<EconomyTreasuryDeclaration>();
        private static List<EconomyTreasurySpendDeclaration> _spends = new List<EconomyTreasurySpendDeclaration>();
        private static readonly Dictionary<string, float> _activeUntilClock = new Dictionary<string, float>();
        private static float _clock;
        private static float _timer;
        private static ZDOID _treasuryZdoId = ZDOID.None;

        public static void OnUpdate(float dt)
        {
            _clock += dt;
            if (EconomyConfig.TreasuryEnabled?.Value == false)
            {
                return;
            }
            RefreshIfNeeded();
            _timer += dt;
            float interval = EconomyConfig.TreasuryEvalSeconds?.Value ?? 2f;
            if (_timer < interval)
            {
                return;
            }
            _timer = 0f;
            ExpireLapsedUnlocks();
            EvaluateSpends();
        }

        private static void RefreshIfNeeded()
        {
            if (EconomyRegistry.Version != _lastVersion)
            {
                _lastVersion = EconomyRegistry.Version;
                _treasuries = EconomyRegistry.Section<EconomyTreasuryDeclaration>("treasury");
                _spends = EconomyRegistry.Section<EconomyTreasurySpendDeclaration>("treasurySpends");
            }
        }

        /// <summary>Deposits <paramref name="amount"/> of <paramref name="currency"/> into the treasury chest, fabricating it lazily. Silently drops the deposit if no treasury is declared or the chest cannot be resolved this tick (the skim is a bonus, never the toll's own enforcement path).</summary>
        public static void Deposit(string currency, int amount)
        {
            if (amount <= 0 || EconomyConfig.TreasuryEnabled?.Value == false)
            {
                return;
            }
            ZDO? chest = GetOrCreateTreasuryChest();
            if (chest == null || ZdoInventoryIO.IsBusy(chest))
            {
                return;
            }
            GameObject? itemPrefab = ObjectDB.instance?.GetItemPrefab(currency);
            if (itemPrefab == null)
            {
                return;
            }
            Inventory? inv = ZdoInventoryIO.Load(chest, 6, 4);
            if (inv == null)
            {
                return;
            }
            if (inv.AddItem(itemPrefab, amount))
            {
                ZdoInventoryIO.Save(chest, inv);
                ItemLedger.RecordTransfer("EconomyTreasury", currency, amount);
            }
            else
            {
                ItemLedger.RecordRejection("EconomyTreasury", currency, amount, "treasury chest full");
            }
        }

        public static int Balance(string currency)
        {
            ZDO? chest = GetOrCreateTreasuryChest();
            if (chest == null || ZdoInventoryIO.IsBusy(chest))
            {
                return 0;
            }
            Inventory? inv = ZdoInventoryIO.Load(chest, 6, 4);
            return inv?.CountItems(currency) ?? 0;
        }

        private static void EvaluateSpends()
        {
            foreach (EconomyTreasurySpendDeclaration spend in _spends)
            {
                if (string.IsNullOrEmpty(spend.Key) || ZoneSystem.instance == null)
                {
                    continue;
                }
                bool alreadyActive = ZoneSystem.instance.GetGlobalKey(spend.Key);
                if (alreadyActive)
                {
                    continue;
                }
                ZDO? chest = GetOrCreateTreasuryChest();
                if (chest == null || ZdoInventoryIO.IsBusy(chest))
                {
                    continue;
                }
                Inventory? inv = ZdoInventoryIO.Load(chest, 6, 4);
                if (inv == null)
                {
                    continue;
                }
                int balance = inv.CountItems(spend.Currency);
                if (balance < spend.Cost)
                {
                    continue;
                }
                inv.RemoveItem(spend.Currency, spend.Cost);
                ZdoInventoryIO.Save(chest, inv);
                ItemLedger.RecordTransfer("EconomyTreasurySpend", spend.Currency, spend.Cost);

                ZoneSystem.instance.SetGlobalKey(spend.Key);
                if (spend.DurationSeconds > 0f)
                {
                    _activeUntilClock[spend.Key] = _clock + spend.DurationSeconds;
                }
                string label = string.IsNullOrEmpty(spend.Label) ? spend.Key : spend.Label;
                EconomyAnnounce.Broadcast($"The vaults open: {label} is funded!", center: true);
                PortalDebug.LogAlways($"[EconomyTreasuryEngine] funded '{spend.Key}' ({label}) for {spend.Cost} {spend.Currency}.");

                if (spend.RewardFireplaceFuel > 0f)
                {
                    RewardEveryoneFireplaceFuel(spend.RewardFireplaceFuel);
                }
                if (spend.RewardSmelterOreAmount > 0 && !string.IsNullOrEmpty(spend.RewardSmelterOreName))
                {
                    RewardEveryoneSmelterGoods(spend.RewardSmelterOreName, spend.RewardSmelterOreAmount);
                }
            }
        }

        private static void RewardEveryoneFireplaceFuel(float amount)
        {
            foreach (ConnectedCharacter who in ConnectedCharacters.All())
            {
                ZDO? fireplace = EconomyStationCreditEngine.FindOwnStation(who, EconomyBindingRegistry.FixtureKind.Fireplace);
                if (fireplace != null)
                {
                    EconomyStationCreditEngine.CreditFireplaceFuel(fireplace, amount);
                }
            }
        }

        private static void RewardEveryoneSmelterGoods(string oreFromName, int amount)
        {
            foreach (ConnectedCharacter who in ConnectedCharacters.All())
            {
                ZDO? smelter = EconomyStationCreditEngine.FindOwnStation(who, EconomyBindingRegistry.FixtureKind.Smelter);
                if (smelter != null)
                {
                    EconomySmelterCreditEngine.CreditFinishedGoods(smelter, oreFromName, amount);
                }
            }
        }

        private static void ExpireLapsedUnlocks()
        {
            if (_activeUntilClock.Count == 0 || ZoneSystem.instance == null)
            {
                return;
            }
            var expired = new List<string>();
            foreach (KeyValuePair<string, float> kvp in _activeUntilClock)
            {
                if (_clock >= kvp.Value)
                {
                    expired.Add(kvp.Key);
                }
            }
            foreach (string key in expired)
            {
                ZoneSystem.instance.RemoveGlobalKey(key);
                _activeUntilClock.Remove(key);
                EconomyAnnounce.Broadcast($"The vaults close: '{key}' has ended.", center: true);
            }
        }

        private static ZDO? GetOrCreateTreasuryChest()
        {
            if (_treasuries.Count == 0)
            {
                return null;
            }
            EconomyTreasuryDeclaration decl = _treasuries[0];

            if (_treasuryZdoId != ZDOID.None)
            {
                ZDO existing = ZDOMan.instance?.GetZDO(_treasuryZdoId);
                if (existing != null && existing.IsValid())
                {
                    return existing;
                }
                _treasuryZdoId = ZDOID.None;
            }

            // Recover a chest fabricated in a PREVIOUS session before falling back to creating a new one -
            // ZDOID renumbers at every world load (ZDO.Load/ZDOID.m_loadID), so the in-memory
            // _treasuryZdoId above is only ever valid within one boot. A tight radius is safe here
            // (looking for the exact fabrication point, not a fuzzy nearby match).
            ZDO? recovered = EconomyBindingRegistry.FindNearest(decl.Position.ToVector3(), 1f, EconomyBindingRegistry.FixtureKind.Container);
            if (recovered != null)
            {
                _treasuryZdoId = recovered.m_uid;
                return recovered;
            }

            try
            {
                if (ZDOMan.instance == null)
                {
                    return null;
                }
                GameObject? prefab = ZNetScene.instance?.GetPrefab(decl.ContainerPrefab);
                int hash = prefab != null ? prefab.name.GetStableHashCode() : decl.ContainerPrefab.GetStableHashCode();
                if (prefab == null)
                {
                    PortalDebug.LogWarning($"[EconomyTreasuryEngine] container prefab '{decl.ContainerPrefab}' not found - treasury chest not fabricated.");
                    return null;
                }

                ZDO fresh = ZDOMan.instance.CreateNewZDO(decl.Position.ToVector3(), hash);
                fresh.Persistent = true;
                fresh.Type = ZDO.ObjectType.Default;
                fresh.Distant = false;
                fresh.SetPrefab(hash);
                fresh.SetOwner(ZDOMan.GetSessionID());
                ZDOMan.instance.ForceSendZDO(fresh.m_uid);
                _treasuryZdoId = fresh.m_uid;
                PortalDebug.LogAlways($"[EconomyTreasuryEngine] fabricated treasury chest at {decl.Position.ToVector3()}.");
                return fresh;
            }
            catch (Exception ex)
            {
                PortalDebug.LogError($"[EconomyTreasuryEngine] failed to fabricate treasury chest: {ex.GetType().Name}: {ex.Message}");
                return null;
            }
        }
    }
}
