using System;
using System.Collections.Generic;
using HarmonyLib;
using TortalPortalLite.Core;
using TortalPortalLite.Core.Hooks;

namespace TortalPortalLite.Subsystems.Topology
{
    /// <summary>
    /// "This portal ZDO cannot be damaged, removed, or reassigned" - the single registry and patch set
    /// behind #207 Corpse-Run Gate's indestructibility promise, for BOTH ends of a gate.
    ///
    /// How damage actually reaches a portal, read off the 1.0.16 SERVER decompile rather than assumed:
    /// every damage source in the game - melee, arrows, a troll's AoE swing, an Aoe component's fire DoT
    /// (:125041-125077), anything holding an IDestructible - ends at WearNTear.Damage(HitData), which
    /// does exactly one thing: `m_nview.InvokeRPC("RPC_Damage", hit)` (:150275-150281). ZNetView.InvokeRPC
    /// is `ZRoutedRpc.instance.InvokeRoutedRPC(m_zdo.GetOwner(), m_zdo.m_uid, method, parameters)`
    /// (:82901-82904) - so damage is never applied locally by the attacker, it is ALWAYS arbitrated on
    /// the machine that owns the ZDO. And ZRoutedRpc.HandleRoutedRPC (:83692-83711) resolves the target
    /// through `ZNetScene.instance.FindInstance(zDO)` and silently drops the call when that is null.
    ///
    /// A dedicated server pins ZNet's reference position at (1e6,0,1e6) (:100340) and therefore never
    /// instantiates a ZNetView at any real player coordinate. So a portal ZDO the SERVER owns receives
    /// every damage RPC aimed at it and drops 100% of them - there is no health threshold involved and
    /// no AoE radius large enough, because no code ever runs. Server ownership is not one layer of the
    /// defence, it IS the defence; everything else here exists to make sure it cannot be lost:
    ///
    ///  1. Ownership lock. ZDOMan.ReleaseZDOS runs every 2 s and calls ReleaseNearbyZDOS(peer.m_refPos,
    ///     peer.m_uid) for every connected peer (:76925-76932), which hands every persistent ZDO in that
    ///     player's active area to that player - unconditionally for ours, because the owner check is
    ///     `!IsInPeerActiveArea(position, owner)` and the server's own active area is out at 1e6
    ///     (:76969). The ZDO.SetOwner/SetOwnerInternal prefixes below refuse any non-server owner for a
    ///     protected ZDO, which is what keeps step 1 from ever happening.
    ///  2. Removal veto. ZDOMan.RPC_DestroyZDO applies whatever ZDOIDs a client sends with NO sender
    ///     check, and ZDOMan.DestroyZDO broadcasts a removal to every client when the server itself owns
    ///     the ZDO (:76977-76983). Either one deletes a protected portal outright, health and ownership
    ///     irrelevant, so both are vetoed here - HandleDestroyedZDO through Core/Hooks' existing broker,
    ///     DestroyZDO through a prefix. A blocked removal force-sends the ZDO so the client that had
    ///     already dropped its local copy gets the portal back.
    ///  3. WearNTear no-ops. Defence in depth for the one case the ownership argument does not cover: a
    ///     server that DOES hold an instance (another mod moving the reference position, a future vanilla
    ///     change). Every entry point that can lower health or destroy the piece is refused outright.
    ///  4. Field overrides at mint time (TargetedPhantomPortalFactory) for the window where a client
    ///     holds an instance: 1e9 health plus the wear immunities. Note these are load-bearing on their
    ///     own and NOT backed up by vanilla's own `if (num > 0f && !CanBeRemoved()) num = 0f` wear shield
    ///     (WearNTear.UpdateWear :149777), because Piece.CanBeRemoved() (:136203) does not read
    ///     Piece.m_canBeRemoved at all - it only delegates to a child Container/Ship. m_canBeRemoved is
    ///     still worth writing: Player.RemovePiece checks it directly (:12365) so a player cannot hammer
    ///     a gate down.
    ///  5. A per-tick watchdog (Reassert) that restores health and ownership and COUNTS every time it had
    ///     to, so "is anything getting through" is an answerable question instead of a guess.
    ///
    /// Protection is keyed on ZDOID and released explicitly before the owning engine tears a gate down -
    /// so the mod's own destroy path is never blocked by its own veto, without needing a re-entrancy
    /// scope.
    /// </summary>
    public static class TargetedPortalProtection
    {
        /// <summary>Health written on a protected portal and restored by the watchdog if it ever reads lower.</summary>
        public const float HealthFloor = 1000000000f;

        private static readonly HashSet<ZDOID> _protected = new HashSet<ZDOID>();
        private static readonly object _lock = new object();
        /// <summary>
        /// Read without the lock as a fast path. ZDO.SetOwnerInternal is one of the hottest methods on a
        /// dedicated server (every ZDO in every peer's active area, every 2 s, plus every incoming client
        /// ZDO write), and with no gate up the honest answer is always "not protected" - so the common case
        /// must not pay for a monitor. Volatile, and only ever read as "is it exactly zero".
        /// </summary>
        private static volatile int _protectedCount;

        private static long _blockedSteals;
        private static long _blockedRemovals;
        private static long _blockedDamage;
        private static long _healthRestores;
        private static long _ownerRestores;
        private static bool _installed;

        public static int ProtectedCount => _protectedCount;

        /// <summary>Ownership-steal attempts refused, removals refused, damage entry points refused, health restores, ownership restores.</summary>
        public static (long steals, long removals, long damage, long healthRestores, long ownerRestores) Counters =>
            (_blockedSteals, _blockedRemovals, _blockedDamage, _healthRestores, _ownerRestores);

        public static void Protect(ZDOID uid)
        {
            if (uid == ZDOID.None)
            {
                return;
            }
            lock (_lock)
            {
                _protected.Add(uid);
                _protectedCount = _protected.Count;
            }
        }

        /// <summary>Drops protection. The owning engine calls this immediately BEFORE destroying a gate end, so its own teardown is not vetoed.</summary>
        public static void Release(ZDOID uid)
        {
            lock (_lock)
            {
                _protected.Remove(uid);
                _protectedCount = _protected.Count;
            }
        }

        public static bool IsProtected(ZDOID uid)
        {
            if (_protectedCount == 0)
            {
                return false;
            }
            lock (_lock)
            {
                return _protected.Contains(uid);
            }
        }

        /// <summary>
        /// Per-tick watchdog: restores the health floor and server ownership if either slipped, and
        /// counts it. Under a working ownership lock neither branch should ever fire, so a non-zero
        /// counter in `tpl: corpserun` is the signal that one of the layers above is not holding.
        /// </summary>
        public static void Reassert(ZDO zdo)
        {
            if (zdo == null || !zdo.IsValid() || ZDOMan.instance == null)
            {
                return;
            }

            if (zdo.GetFloat(ZDOVars.s_health, 0f) < HealthFloor)
            {
                _healthRestores++;
                PortalDebug.LogWarning($"[PortalProtection] health on protected portal {zdo.m_uid} had dropped to {zdo.GetFloat(ZDOVars.s_health, 0f):F0} - restoring the floor (restores so far: {_healthRestores}).");
                Core.Data.PortalOwnership.ClaimAndWrite(zdo, z => z.Set(ZDOVars.s_health, HealthFloor));
                return;
            }

            long session = ZDOMan.GetSessionID();
            if (zdo.GetOwner() != session)
            {
                _ownerRestores++;
                PortalDebug.LogWarning($"[PortalProtection] protected portal {zdo.m_uid} was owned by {zdo.GetOwner()} rather than this server - reclaiming (reclaims so far: {_ownerRestores}). The SetOwner lock should have prevented this.");
                zdo.SetOwner(session);
                ZDOMan.instance.ForceSendZDO(zdo.m_uid);
            }
        }

        // ------------------------------------------------------------------------------- installation

        /// <summary>
        /// Registers the removal veto and installs every patch individually, so one signature drift
        /// disables one layer with a named warning instead of silently dropping the whole set (what a
        /// single SafePatch over a class of patches would do).
        /// </summary>
        public static void Install(Harmony harmony)
        {
            if (_installed)
            {
                return;
            }
            _installed = true;

            // Priority 10: ahead of AuditEngine's observe-only handler at 1000.
            HandleDestroyedZdoHook.Register(10, VetoRemoval);

            int ok = 0;
            int total = 0;

            // 1. Ownership lock - the load-bearing one.
            ok += Bind(harmony, ref total, typeof(ZDO), nameof(ZDO.SetOwner), new[] { typeof(long) }, nameof(OwnerPrefix));
            ok += Bind(harmony, ref total, typeof(ZDO), nameof(ZDO.SetOwnerInternal), new[] { typeof(long) }, nameof(OwnerPrefix));

            // 2. Removal veto, server-broadcast half.
            ok += Bind(harmony, ref total, typeof(ZDOMan), nameof(ZDOMan.DestroyZDO), new[] { typeof(ZDO) }, nameof(DestroyZdoPrefix));

            // 3. WearNTear no-ops, for a server that does hold an instance.
            ok += Bind(harmony, ref total, typeof(WearNTear), "RPC_Damage", new[] { typeof(long), typeof(HitData) }, nameof(WearNTearDamagePrefix));
            ok += Bind(harmony, ref total, typeof(WearNTear), nameof(WearNTear.Damage), new[] { typeof(HitData) }, nameof(WearNTearDamagePrefix));
            ok += Bind(harmony, ref total, typeof(WearNTear), nameof(WearNTear.ApplyDamage), new[] { typeof(float), typeof(HitData) }, nameof(ApplyDamagePrefix));
            ok += Bind(harmony, ref total, typeof(WearNTear), nameof(WearNTear.UpdateWear), new[] { typeof(float) }, nameof(SkipForProtectedPrefix));
            ok += Bind(harmony, ref total, typeof(WearNTear), nameof(WearNTear.Remove), new[] { typeof(bool) }, nameof(SkipForProtectedPrefix));
            ok += Bind(harmony, ref total, typeof(WearNTear), "RPC_Remove", new[] { typeof(long), typeof(bool) }, nameof(SkipForProtectedPrefix));
            ok += Bind(harmony, ref total, typeof(WearNTear), "Destroy", new[] { typeof(HitData), typeof(bool) }, nameof(SkipForProtectedPrefix));

            if (ok == total)
            {
                PortalDebug.LogAlways($"[PortalProtection] all {total} protection patches installed (ownership lock, removal veto, WearNTear no-ops).");
            }
            else
            {
                PortalDebug.LogWarning($"[PortalProtection] {ok}/{total} protection patches installed - see the warnings above for which layer is missing. Server ownership plus the removal veto are the load-bearing ones; the WearNTear no-ops only matter on a server that instantiates pieces.");
            }
        }

        private static int Bind(Harmony harmony, ref int total, Type type, string method, Type[] args, string prefix)
        {
            total++;
            try
            {
                var target = AccessTools.Method(type, method, args);
                if (target == null)
                {
                    throw new MissingMethodException($"{type.Name}.{method} not found - vanilla signature may have changed.");
                }
                harmony.Patch(target, prefix: new HarmonyMethod(typeof(TargetedPortalProtection), prefix));
                return 1;
            }
            catch (Exception ex)
            {
                PortalDebug.LogWarning($"[PortalProtection] could not patch {type.Name}.{method}: {ex.GetType().Name}: {ex.Message}");
                return 0;
            }
        }

        // ------------------------------------------------------------------------------------ patches

        /// <summary>
        /// Refuses to hand a protected portal to anyone but this server. Release-to-nobody (uid 0) is
        /// allowed through: ZDOMan only does that for ZDOs outside the releasing machine's own active
        /// area, and the watchdog reclaims it on the next tick either way.
        /// </summary>
        private static bool OwnerPrefix(ZDO __instance, long uid)
        {
            if (uid == 0L || (ZDOMan.instance != null && uid == ZDOMan.GetSessionID()))
            {
                return true;
            }
            if (!IsProtected(__instance.m_uid))
            {
                return true;
            }
            _blockedSteals++;
            return false;
        }

        /// <summary>
        /// ZDOMan.DestroyZDO adds the ZDO to m_destroySendList whenever the caller owns it (:76977) -
        /// which for a protected portal is always this server - and every client then deletes it. The
        /// engine's own teardown calls Release() first, so only a removal this mod did not ask for
        /// reaches here.
        /// </summary>
        private static bool DestroyZdoPrefix(ZDO zdo)
        {
            if (zdo == null || !IsProtected(zdo.m_uid))
            {
                return true;
            }
            _blockedRemovals++;
            LogBlockedRemoval(zdo.m_uid, "ZDOMan.DestroyZDO");
            return false;
        }

        /// <summary>
        /// The other removal half: a client's RPC_DestroyZDO, applied with no sender check. Vetoing it
        /// leaves the server holding the ZDO while the client that sent it has already dropped its local
        /// copy, so the ZDO is force-sent to put the portal back on every client.
        /// </summary>
        private static bool VetoRemoval(ZDO zdo, long sender)
        {
            if (zdo == null || !IsProtected(zdo.m_uid))
            {
                return true;
            }
            _blockedRemovals++;
            LogBlockedRemoval(zdo.m_uid, sender == 0L ? "server-internal" : $"client {sender}");
            try
            {
                ZDOMan.instance?.ForceSendZDO(zdo.m_uid);
            }
            catch (Exception ex)
            {
                PortalDebug.LogWarning($"[PortalProtection] could not re-send vetoed portal {zdo.m_uid}: {ex.Message}");
            }
            return false;
        }

        private static void LogBlockedRemoval(ZDOID uid, string origin)
        {
            // Every blocked removal is worth a line: a protected portal is only ever removed by this
            // mod, so anything reaching here is a real attempt (a client's WearNTear deciding the piece
            // died, a griefing packet, another mod) and the count is the evidence.
            PortalDebug.LogWarning($"[PortalProtection] refused removal of protected corpse-run portal {uid} requested by {origin} (refusals so far: {_blockedRemovals}).");
        }

        /// <summary>Prefix for every void WearNTear entry point that could lower health or destroy the piece.</summary>
        private static bool SkipForProtectedPrefix(WearNTear __instance)
        {
            if (!IsProtectedPiece(__instance))
            {
                return true;
            }
            _blockedDamage++;
            return false;
        }

        private static bool WearNTearDamagePrefix(WearNTear __instance)
        {
            if (!IsProtectedPiece(__instance))
            {
                return true;
            }
            _blockedDamage++;
            return false;
        }

        private static bool ApplyDamagePrefix(WearNTear __instance, ref bool __result)
        {
            if (!IsProtectedPiece(__instance))
            {
                return true;
            }
            _blockedDamage++;
            __result = false;
            return false;
        }

        private static bool IsProtectedPiece(WearNTear wnt)
        {
            if (wnt == null)
            {
                return false;
            }
            try
            {
                ZNetView nview = wnt.m_nview;
                if (nview == null || !nview.IsValid())
                {
                    return false;
                }
                ZDO zdo = nview.GetZDO();
                return zdo != null && IsProtected(zdo.m_uid);
            }
            catch
            {
                // A half-initialised WearNTear is not a protected portal; never let this throw inside a
                // vanilla damage path.
                return false;
            }
        }
    }
}
