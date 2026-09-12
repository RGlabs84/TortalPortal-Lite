using System;
using System.Collections.Generic;
using UnityEngine;
using TortalPortalLite.Core;
using TortalPortalLite.Core.Data;

namespace TortalPortalLite.Subsystems.PlayerInterface
{
    /// <summary>
    /// #174 Feedback: forced teleport as the delivery mechanism. `Chat.Awake` registers
    /// `"RPC_TeleportPlayer"(Vector3, Quaternion, bool)` on EVERY peer with no sender/admin/item check
    /// (SERVER decompile citation in the catalog entry); `Player.TeleportTo(pos, rot, distantTeleport:
    /// true)` is byte-for-byte the same experience as walking through a portal (same swirl, same black
    /// screen, ~8s) and, critically, CANNOT fail the way `distantTeleport: false` can (a 15s fallback
    /// force-lands on solid ground rather than silently rubber-banding with a misleading
    /// `$msg_portal_blocked`) - so this class always passes `true`.
    ///
    /// Bypasses every vanilla teleport rule (NoPortals, wards, ore-carry checks) - it is meant as the
    /// escape hatch (home/back/spawn, admin rescue, TPA), never the primary movement, exactly as the
    /// catalog frames it. Shared by #268 Summoning Gaze and #269 Warp Verbs so there is exactly one
    /// send-path, one cooldown ledger and one pre-warp position ring in the whole domain.
    /// </summary>
    public static class UxForcedTeleport
    {
        private const int MaxHistoryPerPlayer = 5;

        private static readonly Dictionary<long, float> _cooldownUntilClock = new Dictionary<long, float>();
        private static readonly Dictionary<long, List<(Vector3 pos, Quaternion rot)>> _history = new Dictionary<long, List<(Vector3, Quaternion)>>();
        private static float _clock;

        public static void OnUpdate(float dt) => _clock += dt;

        /// <summary>Mandatory server-readable pre-checks (all off the character ZDO, per the catalog's own citations): a dead player latches `m_teleporting` and never advances; a sleeping/attached player ping-pongs against UpdateAttach, which runs before UpdateTeleport every FixedUpdate.</summary>
        public static bool CanTeleport(ConnectedCharacter who, out string reason)
        {
            reason = "";
            if (who.Zdo.GetBool(ZDOVars.s_dead, false))
            {
                reason = "dead";
                return false;
            }
            if (who.Zdo.GetBool(ZDOVars.s_inBed, false))
            {
                reason = "sleeping";
                return false;
            }
            if (_cooldownUntilClock.TryGetValue(who.PlayerId, out float until) && _clock < until)
            {
                reason = $"cooling down ({Mathf.CeilToInt(until - _clock)}s)";
                return false;
            }
            return true;
        }

        /// <summary>Records the player's CURRENT position/rotation before warping (for #269's `/back`), enforces the shared cooldown, and fires the RPC. Caller is expected to have already checked CanTeleport and toasted accordingly - this re-checks and silently refuses rather than double-toasting.</summary>
        public static bool TryTeleport(ConnectedCharacter who, Vector3 pos, Quaternion rot, bool recordForBack = true)
        {
            if (ZRoutedRpc.instance == null || who.Peer == null)
            {
                return false;
            }
            if (!CanTeleport(who, out _))
            {
                return false;
            }

            if (recordForBack)
            {
                RecordHistory(who.PlayerId, who.Position, who.Zdo.GetRotation());
            }
            _cooldownUntilClock[who.PlayerId] = _clock + (UxConfig.ForcedTeleportCooldownSeconds?.Value ?? 12f);

            try
            {
                ZRoutedRpc.instance.InvokeRoutedRPC(who.Peer.m_uid, "RPC_TeleportPlayer", pos, rot, true);
                return true;
            }
            catch (Exception ex)
            {
                PortalDebug.LogWarning($"[UxForcedTeleport] teleport failed for {who.Name}: {ex.Message}");
                return false;
            }
        }

        private static void RecordHistory(long playerId, Vector3 pos, Quaternion rot)
        {
            if (!_history.TryGetValue(playerId, out var list))
            {
                list = new List<(Vector3, Quaternion)>();
                _history[playerId] = list;
            }
            list.Add((pos, rot));
            while (list.Count > MaxHistoryPerPlayer)
            {
                list.RemoveAt(0);
            }
        }

        /// <summary>#269 `/back` - pops the most recent pre-warp position. Only the server can implement this at all: a client's own death/logout/home points never leave its local .fch (the catalog's own citation).</summary>
        public static bool TryPopHistory(long playerId, out Vector3 pos, out Quaternion rot)
        {
            pos = default;
            rot = Quaternion.identity;
            if (!_history.TryGetValue(playerId, out var list) || list.Count == 0)
            {
                return false;
            }
            int last = list.Count - 1;
            (pos, rot) = list[last];
            list.RemoveAt(last);
            return true;
        }
    }
}
