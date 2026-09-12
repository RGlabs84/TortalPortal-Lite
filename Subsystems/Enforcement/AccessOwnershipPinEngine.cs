using System;
using System.Collections.Generic;
using UnityEngine;
using TortalPortalLite.Core;
using TortalPortalLite.Core.Data;
using TortalPortalLite.Core.Hooks;
using TortalPortalLite.Subsystems.Foundations;

namespace TortalPortalLite.Subsystems.Enforcement
{
    /// <summary>
    /// #48 Ownership Pin + #216 Real Ownership Pin, combined - #216 is explicitly the corrected/complete
    /// mechanism for the same idea #48 describes ("the correction the existing research misses"), so one
    /// engine implements both catalog entries rather than shipping two competing pin mechanisms that
    /// would both want to react to the exact same ZDO.
    ///
    /// HONESTY NOTE ON ENFORCEMENT TIER: the catalog's full #216 mechanism is three parts - (1) a prefix
    /// on `ZDO.SetOwner` blocking in-process callers (ReleaseNearbyZDOS, Game.SetConnection), (2) a
    /// postfix on `ZDOMan.RPC_ZDOData` re-claiming a pinned ZDO whose owner drifted, (3) a ping-pong kick
    /// bound. No Core/Hooks/ broker exists for `ZDO.SetOwner`/`ZDO.SetOwnerInternal`, and this wave's own
    /// rules require flagging that rather than installing a new patch. Parts (2) and (3) ARE fully
    /// implemented here (piggybacked on RpcZdoDataHook, which already exists) - and the catalog's own
    /// text explicitly endorses this as sufficient ("You must also prefix SetOwnerInternal... OR
    /// re-assert ownership in the ZDO.Deserialize postfix on every arriving portal packet"). What is
    /// NOT closed without the flagged hook is `ReleaseNearbyZDOS`'s silent, server-internal
    /// reassignment, which never goes through RPC_ZDOData at all - covered here only by the slow
    /// reconcile sweep (OnUpdate, every PinReconcileSeconds), meaning there is a real bounded window
    /// (not the catalog's claimed "genuinely unbypassable") until that hook lands. This is closer to
    /// "fast reactive re-assert" than true prevention - stated plainly rather than upgraded to a false
    /// "server-enforced, no window" claim.
    ///
    /// NEEDS NEW HOOK BROKER on ZDO.SetOwner(long) / ZDO.SetOwnerInternal(long): purpose - a true prefix
    /// veto (return false when the target ZDO is pinned and the incoming uid is neither 0 nor the
    /// server's own session id) would close the ReleaseNearbyZDOS window entirely and make the pin
    /// genuinely instantaneous instead of bounded by PinReconcileSeconds.
    ///
    /// Pinned portals are tracked BY POSITION (AccessAclStore.RoundPos), not by ZDOID - ZDOIDs
    /// regenerate on every world load (ZDO.Load remints m_uid via ZDOID.m_loadID), so the pin set is
    /// rebuilt against the live census every reconcile tick rather than trusted as a fixed id set.
    /// </summary>
    public static class AccessOwnershipPinEngine
    {
        private sealed class PinRecord
        {
            public Vector3 Position;
            public string Reason = "";
        }

        private static readonly Dictionary<Vector3, PinRecord> _pinnedByPosition = new Dictionary<Vector3, PinRecord>();
        private static readonly Dictionary<ZDOID, PinRecord> _pinnedByUid = new Dictionary<ZDOID, PinRecord>();

        private static readonly Dictionary<string, Queue<DateTime>> _claimAttempts = new Dictionary<string, Queue<DateTime>>(StringComparer.OrdinalIgnoreCase);

        private static float _timer;

        public static void Initialize()
        {
            RpcZdoDataHook.RegisterPostfix(10, OnZdoData); // low priority number = runs early, before the tag watchdog's own attribution reads the same packet.
        }

        public static bool IsPinned(ZDOID uid) => _pinnedByUid.ContainsKey(uid);

        public static void Pin(ZDO zdo, string reason)
        {
            if (zdo == null || !zdo.IsValid())
            {
                return;
            }
            Vector3 key = AccessAclStore.RoundPos(zdo.GetPosition());
            var record = new PinRecord { Position = zdo.GetPosition(), Reason = reason };
            _pinnedByPosition[key] = record;
            _pinnedByUid[zdo.m_uid] = record;
            PortalOwnership.ClaimAndWrite(zdo, _ => { });
            PortalDebug.LogAlways($"[AccessOwnershipPinEngine] pinned {zdo.m_uid} ({reason}).");
        }

        public static void Unpin(ZDO zdo)
        {
            if (zdo == null)
            {
                return;
            }
            _pinnedByPosition.Remove(AccessAclStore.RoundPos(zdo.GetPosition()));
            _pinnedByUid.Remove(zdo.m_uid);
            try
            {
                zdo.SetOwner(0L);
                ZDOMan.instance?.ForceSendZDO(zdo.m_uid);
            }
            catch (Exception ex)
            {
                PortalDebug.LogWarning($"[AccessOwnershipPinEngine] unpin failed for {zdo.m_uid}: {ex.Message}");
            }
        }

        private static void OnZdoData(ZNetPeer? sender, ZDOID zdoid)
        {
            if (ZDOMan.instance == null || !_pinnedByUid.TryGetValue(zdoid, out PinRecord record))
            {
                return;
            }
            ZDO zdo = ZDOMan.instance.GetZDO(zdoid);
            if (zdo == null || !zdo.IsValid())
            {
                return;
            }
            if (zdo.GetOwner() == ZDOMan.GetSessionID())
            {
                return; // still ours - nothing to reclaim.
            }

            // #216 part (2): the packet that just landed applied SetOwnerInternal directly (bypassing
            // ZDO.SetOwner) - reclaim right now rather than waiting for the slow sweep.
            PortalOwnership.ClaimAndWrite(zdo, _ => { });

            string host = SenderContext.HostNameOf(sender) ?? "";
            if (!string.IsNullOrEmpty(host))
            {
                RecordClaimAttempt(host, record.Reason);
            }
        }

        public static void OnUpdate(float dt)
        {
            _timer += dt;
            float interval = AccessConfig.PinReconcileSeconds?.Value ?? 2f;
            if (_timer < interval || ZDOMan.instance == null)
            {
                return;
            }
            _timer = 0f;

            // #216 part (2)'s slow-sweep half: catches ReleaseNearbyZDOS's silent server-internal
            // reassignment, which never touches RPC_ZDOData and so the fast path above never sees it.
            foreach (var kvp in _pinnedByPosition)
            {
                if (!PortalCensus.TryGetByPosition(kvp.Key, out PortalRecord census))
                {
                    continue; // portal not in this tick's census (e.g. mid-load) - try again next sweep.
                }
                _pinnedByUid[census.Uid] = kvp.Value;

                ZDO zdo = ZDOMan.instance.GetZDO(census.Uid);
                if (zdo == null || !zdo.IsValid())
                {
                    continue;
                }
                if (zdo.GetOwner() != ZDOMan.GetSessionID())
                {
                    PortalOwnership.ClaimAndWrite(zdo, _ => { });
                    PortalDebug.LogInfo($"[AccessOwnershipPinEngine] reconcile sweep re-claimed drifted pin on {census.Uid}.");
                }
            }
        }

        private static void RecordClaimAttempt(string host, string pinReason)
        {
            if (!_claimAttempts.TryGetValue(host, out Queue<DateTime> queue))
            {
                queue = new Queue<DateTime>();
                _claimAttempts[host] = queue;
            }
            DateTime now = DateTime.UtcNow;
            queue.Enqueue(now);
            float window = AccessConfig.PinClaimWindowSeconds?.Value ?? 30f;
            while (queue.Count > 0 && (now - queue.Peek()).TotalSeconds > window)
            {
                queue.Dequeue();
            }

            int limit = AccessConfig.PinClaimsBeforeKick?.Value ?? 8;
            if (queue.Count >= limit)
            {
                PortalDebug.LogAlways($"[AccessOwnershipPinEngine] ping-pong bound exceeded for {host} against a pinned portal ({pinReason}) - kicking.");
                try
                {
                    ZNet.instance?.Kick(host);
                }
                catch (Exception ex)
                {
                    PortalDebug.LogWarning($"[AccessOwnershipPinEngine] kick failed for {host}: {ex.Message}");
                }
                queue.Clear();
            }
        }
    }
}
