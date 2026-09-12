using System;
using System.Collections.Generic;
using TortalPortalLite.Core;
using TortalPortalLite.Core.Data;

namespace TortalPortalLite.Subsystems.PlayerInterface
{
    /// <summary>
    /// #173 Status effect as a persistent state indicator, and #262 Puppet Strings (server-invoked
    /// animator triggers on a player's own body) - grouped as two small, output-only, "grant/play
    /// something on a character's own ZDO-scoped routed RPC" channels.
    ///
    /// #173: `SEMan`'s constructor registers `"RPC_AddStatusEffect"(int nameHash, bool resetTime, int
    /// itemLevel, float skillLevel, int variant)` on the character's own ZNetView; the receiving client
    /// resolves `nameHash` purely against its OWN `ObjectDB.m_StatusEffects`, so only effects every
    /// vanilla client already ships can ever be granted, and there is NO removal RPC anywhere in the
    /// assembly - a granted effect can only be allowed to expire. This engine therefore re-pings the
    /// effect on a fixed short interval (2s - safely below any real vanilla effect's duration) for as
    /// long as `UxArmingGate` reports the player armed, and simply stops when they aren't, letting the
    /// last grant expire naturally. `nameHash` is computed the same way vanilla's own precomputed
    /// `ZDOVars.s_statusEffectRested = "Rested".GetStableHashCode()` etc. are - a real effect's display
    /// name hashed with `GetStableHashCode()`.
    ///
    /// #262: `ZSyncAnimation.RPC_SetTrigger` is a bare `m_animator.SetTrigger(name)` with NO owner check,
    /// so the server can play any of the small set of triggers Player code itself fires (interact,
    /// stagger, ...) on a player's own body, purely cosmetic (no ZDO/gameplay state changes) - a nod or a
    /// stagger as a non-textual "request accepted"/"rejected" acknowledgement, visible to bystanders too.
    /// </summary>
    public static class UxStatusAndPuppetEngine
    {
        private const float RepingIntervalSeconds = 2f;
        private static readonly Dictionary<long, float> _nextPingClock = new Dictionary<long, float>();
        private static float _clock;

        public static void Initialize()
        {
            UxArmingGate.RegisterArmed((who, rec) => Grant(who));
            UxArmingGate.RegisterCommitRequested(Forget);
        }

        public static void OnUpdate(float dt)
        {
            _clock += dt;
            if (UxConfig.Enabled?.Value == false || UxConfig.StatusIndicatorEnabled?.Value != true)
            {
                return;
            }
            foreach (ConnectedCharacter who in ConnectedCharacters.All())
            {
                if (!UxArmingGate.IsArmed(who.PlayerId))
                {
                    _nextPingClock.Remove(who.PlayerId);
                    continue;
                }
                if (_nextPingClock.TryGetValue(who.PlayerId, out float next) && _clock < next)
                {
                    continue;
                }
                _nextPingClock[who.PlayerId] = _clock + RepingIntervalSeconds;
                Grant(who);
            }
        }

        private static void Grant(ConnectedCharacter who)
        {
            if (UxConfig.StatusIndicatorEnabled?.Value != true || ZRoutedRpc.instance == null || who.Peer == null)
            {
                return;
            }
            string name = UxConfig.ArmedStatusEffectName?.Value;
            if (string.IsNullOrEmpty(name))
            {
                return;
            }
            try
            {
                int nameHash = name.GetStableHashCode();
                ZRoutedRpc.instance.InvokeRoutedRPC(who.Peer.m_uid, who.Zdo.m_uid, "RPC_AddStatusEffect", nameHash, true, 0, 0f, -1);
            }
            catch (Exception ex)
            {
                PortalDebug.LogWarning($"[UxStatusAndPuppetEngine] status grant failed for {who.Name}: {ex.Message}");
            }
        }

        private static void Forget(ConnectedCharacter who) => _nextPingClock.Remove(who.PlayerId);

        /// <summary>#262. `everyoneNearby: true` broadcasts (every peer holding that character's instance plays it); false plays it only on the acting player's own screen.</summary>
        public static void PlayBodyTrigger(ConnectedCharacter who, string triggerName, bool everyoneNearby = false)
        {
            if (UxConfig.PuppetStringsEnabled?.Value != true || ZRoutedRpc.instance == null)
            {
                return;
            }
            long target = everyoneNearby ? 0L : (who.Peer?.m_uid ?? 0L);
            if (target == 0L && !everyoneNearby)
            {
                return;
            }
            try
            {
                ZRoutedRpc.instance.InvokeRoutedRPC(target, who.Zdo.m_uid, "SetTrigger", triggerName);
            }
            catch (Exception ex)
            {
                PortalDebug.LogWarning($"[UxStatusAndPuppetEngine] body trigger '{triggerName}' failed for {who.Name}: {ex.Message}");
            }
        }
    }
}
