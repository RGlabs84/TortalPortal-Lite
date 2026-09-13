using System;
using System.Collections.Generic;
using UnityEngine;
using TortalPortalLite.Core;
using TortalPortalLite.Core.Data;
using TortalPortalLite.Core.Hooks;

namespace TortalPortalLite.Subsystems.WorldOps
{
    /// <summary>
    /// #284 Trust-Tier Correction + #285 Inline ZDOData Validator + #286 Sender-Owner Binding Rule,
    /// combined - #284 is the honest re-labelling this whole class embodies in code rather than prose:
    /// every escrow/turnstile/charge-cell CONDITION this domain reads is "server-enforced against
    /// unmodified clients; inputs forgeable by a modified client without a validator" (catalog's own
    /// corrected tier), never plain "server-enforced" - because the field a forger writes reaches the
    /// server through `ZDOMan.RPC_ZDOData`, which authenticates only the SOCKET and applies any
    /// higher-DataRevision blob with no ownership test and no field allowlist (catalog's own citation).
    ///
    /// This engine is the mitigation: a HANDLER on `Core/Hooks/RpcZdoDataHook`'s existing postfix broker
    /// (no new Harmony patch - that hook already exists and already resolves the sender), running
    /// SYNCHRONOUSLY in the same call stack as the write it is checking, before any other subsystem's
    /// next tick ever observes the forged value - as close to the catalog's "in-frame revert" as this
    /// mod's hook surface allows, and strictly faster than the tick-based diff
    /// Subsystems/Enforcement/AccessTagWatchdogEngine.cs uses for the same shape of problem on portal
    /// tags (that engine's own target is different ZDOs - managed portals - so there is no overlap).
    ///
    /// Rules applied to every watched economy fixture (toll chests, turnstile stands, charge-cell
    /// fireplaces): (#286) the packet's resulting owner must equal the actual sending peer - vanilla's
    /// own write paths for every one of these fields all claim ownership first (Container.RPC_RequestOpen,
    /// Fireplace.Interact, ...), so this holds for every unmodified client and fails for a forger who
    /// wrote directly into `s_items`/`s_item`/`s_fuel` without ever claiming the ZDO. (#285 proximity)
    /// the sending character must be within ValidatorProximityRadius of the fixture - a legitimate
    /// interaction always happens standing next to the thing. A write failing either check is reverted to
    /// the last snapshot a legitimate mechanism-engine read APPROVED of it (fed by
    /// <see cref="SnapshotFireplaceFuel"/>/<see cref="SnapshotItemStand"/>/<see cref="SnapshotContainerItems"/>,
    /// called by EconomyChargeCellEngine/EconomyTurnstileEngine/EconomyTollEscrowEngine on every normal
    /// read) - reverting the CONTAINER'S WHOLE `s_items` BLOB rather than trying to reconstruct individual
    /// item semantics, which sidesteps needing to know what any given chest is "supposed" to contain.
    ///
    /// Honest limits (documented, not silently assumed away): provenance is still unprovable - a forger
    /// who first claims ownership and stays in proximity passes both rules, and this validator does not
    /// close that gap, only raises its cost. A repeat offender (same authenticated host name, several
    /// rejections inside ValidatorWindowSeconds) is kicked as a backstop.
    /// </summary>
    public static class EconomyValidatorEngine
    {
        private const float WatchedSetRefreshSeconds = 2f;

        private sealed class Snapshot
        {
            public float? Fuel;
            public int? Item;
            public byte[]? ItemsBlob;
        }

        private static readonly Dictionary<ZDOID, Snapshot> _snapshots = new Dictionary<ZDOID, Snapshot>();
        private static readonly Dictionary<ZDOID, EconomyBindingRegistry.FixtureKind> _watched = new Dictionary<ZDOID, EconomyBindingRegistry.FixtureKind>();
        private static readonly Dictionary<string, List<float>> _rejectionClocks = new Dictionary<string, List<float>>(StringComparer.OrdinalIgnoreCase);
        private static float _refreshTimer;
        private static float _clock;

        public static void Initialize()
        {
            RpcZdoDataHook.RegisterPostfix(50, OnZdoData);
        }

        public static void OnUpdate(float dt)
        {
            _clock += dt;
            if (EconomyConfig.ValidatorEnabled?.Value == false)
            {
                return;
            }
            _refreshTimer += dt;
            if (_refreshTimer < WatchedSetRefreshSeconds)
            {
                return;
            }
            _refreshTimer = 0f;
            RefreshWatchedSet();
        }

        private static void RefreshWatchedSet()
        {
            _watched.Clear();
            float defaultRadius = EconomyConfig.BindRadius?.Value ?? 4f;

            foreach (EconomyTollDeclaration toll in EconomyTollEscrowEngine.Declarations)
            {
                ZDO? gate = EconomyWriteOps.ResolveLivePortal(toll.Portal);
                if (gate == null)
                {
                    continue;
                }
                float radius = toll.BindRadius > 0f ? toll.BindRadius : defaultRadius;
                ZDO? chest = EconomyBindingRegistry.FindNearest(gate.GetPosition(), radius, EconomyBindingRegistry.FixtureKind.Container);
                if (chest != null)
                {
                    _watched[chest.m_uid] = EconomyBindingRegistry.FixtureKind.Container;
                }
            }
        }

        /// <summary>Called by EconomyTollEscrowEngine after every legitimate (non-busy) read - the fixture becomes both watched and has an approved baseline to revert to.</summary>
        public static void SnapshotContainerItems(ZDO zdo)
        {
            if (EconomyConfig.ValidatorEnabled?.Value == false)
            {
                return;
            }
            _watched[zdo.m_uid] = EconomyBindingRegistry.FixtureKind.Container;
            GetOrCreate(zdo.m_uid).ItemsBlob = zdo.GetByteArray(ZDOVars.s_items);
        }

        public static void SnapshotItemStand(ZDO zdo)
        {
            if (EconomyConfig.ValidatorEnabled?.Value == false)
            {
                return;
            }
            _watched[zdo.m_uid] = EconomyBindingRegistry.FixtureKind.ItemStand;
            GetOrCreate(zdo.m_uid).Item = zdo.GetInt(ZDOVars.s_item);
        }

        public static void SnapshotFireplaceFuel(ZDO zdo)
        {
            if (EconomyConfig.ValidatorEnabled?.Value == false)
            {
                return;
            }
            _watched[zdo.m_uid] = EconomyBindingRegistry.FixtureKind.Fireplace;
            GetOrCreate(zdo.m_uid).Fuel = zdo.GetFloat(ZDOVars.s_fuel);
        }

        private static Snapshot GetOrCreate(ZDOID uid)
        {
            if (!_snapshots.TryGetValue(uid, out Snapshot snap))
            {
                snap = new Snapshot();
                _snapshots[uid] = snap;
            }
            return snap;
        }

        private static void OnZdoData(ZNetPeer? sender, ZDOID zdoid)
        {
            if (EconomyConfig.ValidatorEnabled?.Value == false || !_watched.TryGetValue(zdoid, out EconomyBindingRegistry.FixtureKind kind))
            {
                return;
            }
            if (sender == null || ZDOMan.instance == null)
            {
                // No resolvable sender (e.g. this mod's own server-side write, or an unauthenticated
                // internal path) - never flag our own writes.
                return;
            }
            ZDO? zdo = ZDOMan.instance.GetZDO(zdoid);
            if (zdo == null || !zdo.IsValid())
            {
                return;
            }

            if (IsPlausible(zdo, sender))
            {
                return;
            }

            Revert(zdo, kind);
            RecordRejection(sender);
        }

        private static bool IsPlausible(ZDO zdo, ZNetPeer sender)
        {
            // #286 Sender-Owner Binding Rule.
            if (zdo.GetOwner() != sender.m_uid)
            {
                return false;
            }

            // Proximity - skip (allow) only if the sender's own character cannot be resolved at all
            // (e.g. mid-join window), per the catalog's own "skip position, rely on the other rules"
            // guidance rather than falsely flagging a legitimate edge case.
            if (sender.m_characterID.IsNone())
            {
                return true;
            }
            ZDO? charZdo = ZDOMan.instance?.GetZDO(sender.m_characterID);
            if (charZdo == null || !charZdo.IsValid())
            {
                return true;
            }

            float radius = EconomyConfig.ValidatorProximityRadius?.Value ?? 6f;
            return (charZdo.GetPosition() - zdo.GetPosition()).sqrMagnitude <= radius * radius;
        }

        private static void Revert(ZDO zdo, EconomyBindingRegistry.FixtureKind kind)
        {
            if (!_snapshots.TryGetValue(zdo.m_uid, out Snapshot snap))
            {
                return;
            }
            PortalOwnership.ClaimAndWrite(zdo, z =>
            {
                switch (kind)
                {
                    case EconomyBindingRegistry.FixtureKind.Container:
                        if (snap.ItemsBlob != null)
                        {
                            z.Set(ZDOVars.s_items, snap.ItemsBlob);
                        }
                        break;
                    case EconomyBindingRegistry.FixtureKind.ItemStand:
                        if (snap.Item.HasValue)
                        {
                            z.Set(ZDOVars.s_item, snap.Item.Value);
                        }
                        break;
                    case EconomyBindingRegistry.FixtureKind.Fireplace:
                        if (snap.Fuel.HasValue)
                        {
                            z.Set(ZDOVars.s_fuel, snap.Fuel.Value);
                        }
                        break;
                }
            });
            PortalDebug.LogAlways($"[EconomyValidatorEngine] reverted implausible write to {zdo.m_uid} ({kind}).");
        }

        private static void RecordRejection(ZNetPeer sender)
        {
            string? host = SenderContext.HostNameOf(sender);
            if (string.IsNullOrEmpty(host))
            {
                return;
            }
            if (!_rejectionClocks.TryGetValue(host, out List<float> times))
            {
                times = new List<float>();
                _rejectionClocks[host] = times;
            }
            times.Add(_clock);

            float window = EconomyConfig.ValidatorWindowSeconds?.Value ?? 30f;
            times.RemoveAll(t => _clock - t > window);

            int threshold = EconomyConfig.ValidatorKickThreshold?.Value ?? 8;
            if (times.Count >= threshold && ZNet.instance != null)
            {
                PortalDebug.LogAlways($"[EconomyValidatorEngine] SECURITY: kicking '{host}' after {times.Count} rejected writes within {window}s.");
                try
                {
                    ZNet.instance.Kick(host);
                }
                catch (Exception ex)
                {
                    PortalDebug.LogWarning($"[EconomyValidatorEngine] kick failed: {ex.Message}");
                }
                times.Clear();
            }
        }
    }
}
