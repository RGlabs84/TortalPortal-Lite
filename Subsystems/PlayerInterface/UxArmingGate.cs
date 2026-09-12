using System;
using System.Collections.Generic;
using TortalPortalLite.Core;
using TortalPortalLite.Core.Data;
using TortalPortalLite.Subsystems.Foundations;

namespace TortalPortalLite.Subsystems.PlayerInterface
{
    /// <summary>
    /// #158 Posture bits - crouch and block as a silent modifier key. NEW FINDING the catalog verifies:
    /// `ZSyncAnimation.SetBool` mirrors an animator bool into the character ZDO at a SESSION-hashed key
    /// `438569 + hash` (c_ZDOSalt, SERVER decompile :87144/:87270-87281), and `ZSyncAnimation.GetHash`
    /// (already public, :87199) is a thin wrapper over `Animator.StringToHash` - calling THAT static
    /// method directly (never `UnityEngine.Animator` itself) means this file never needs a reference to
    /// UnityEngine.AnimationModule.dll, which this project's .csproj does not carry (and which this
    /// agent may not add - the .csproj is off-limits).
    ///
    /// Semantics match the catalog's own playerExperience exactly: crouch RISING edge near a portal
    /// arms; crouch FALLING edge while armed commits (whatever a consumer's own selection cursor is
    /// currently pointing at - the Armed/CommitRequested events below let #157 (gesture cycling), #159
    /// (emote cycling), #160/#161/#162 (equip/stand/chest selectors) all share one arming primitive
    /// instead of five competing crouch pollers - this is one of the domain's few real internal
    /// dependency edges). `IsBlocking()` forcing crouch off (Player.UpdateCrouch, :15662) means
    /// crouch+block is unreachable - three modes exist, not four, exactly as the catalog notes; this
    /// class does not attempt to invent a fourth.
    /// </summary>
    public static class UxArmingGate
    {
        private static readonly Dictionary<long, float> _armedUntilClock = new Dictionary<long, float>();
        private static readonly Dictionary<long, PortalRecord> _armedPortal = new Dictionary<long, PortalRecord>();
        private static readonly Dictionary<long, bool> _lastCrouch = new Dictionary<long, bool>();
        private static readonly List<Action<ConnectedCharacter, PortalRecord>> _armedHandlers = new List<Action<ConnectedCharacter, PortalRecord>>();
        private static readonly List<Action<ConnectedCharacter>> _commitHandlers = new List<Action<ConnectedCharacter>>();

        private static int? _crouchKey;
        private static float _clock;
        private static float _timer;

        /// <summary>Fires once, on the rising crouch edge near a portal - "start a selection session here".</summary>
        public static void RegisterArmed(Action<ConnectedCharacter, PortalRecord> handler) => _armedHandlers.Add(handler);

        /// <summary>Fires once, on the falling crouch edge while armed, BEFORE the session is cleared - "commit whatever is currently selected".</summary>
        public static void RegisterCommitRequested(Action<ConnectedCharacter> handler) => _commitHandlers.Add(handler);

        public static void OnUpdate(float dt)
        {
            _clock += dt;
            if (UxConfig.Enabled?.Value == false || UxConfig.ArmingEnabled?.Value == false)
            {
                return;
            }
            _timer += dt;
            float interval = UxConfig.ArmingPollSeconds?.Value ?? 0.2f;
            if (_timer < interval)
            {
                return;
            }
            _timer = 0f;
            Poll();
        }

        private static void Poll()
        {
            float radius = UxConfig.PortalProximityRadius?.Value ?? 6f;
            foreach (ConnectedCharacter who in ConnectedCharacters.All())
            {
                bool crouching = IsCrouching(who.Zdo);
                bool wasCrouching = _lastCrouch.TryGetValue(who.PlayerId, out bool prev) && prev;
                _lastCrouch[who.PlayerId] = crouching;

                if (crouching && !wasCrouching)
                {
                    if (!IsArmed(who.PlayerId) && UxAddressBook.TryNearestAnyPortal(who.Position, radius, out PortalRecord rec))
                    {
                        Arm(who.PlayerId, rec);
                        Dispatch(_armedHandlers, who, rec);
                    }
                }
                else if (!crouching && wasCrouching && IsArmed(who.PlayerId))
                {
                    DispatchCommit(who);
                    Disarm(who.PlayerId);
                }
                else if (IsArmed(who.PlayerId) && _clock >= _armedUntilClock[who.PlayerId])
                {
                    Disarm(who.PlayerId); // silent auto-disarm, no commit
                }
            }
        }

        private static void Dispatch(List<Action<ConnectedCharacter, PortalRecord>> handlers, ConnectedCharacter who, PortalRecord rec)
        {
            foreach (var h in handlers)
            {
                try { h(who, rec); }
                catch (Exception ex) { PortalDebug.LogError($"[UxArmingGate] armed handler threw: {ex.Message}"); }
            }
        }

        private static void DispatchCommit(ConnectedCharacter who)
        {
            foreach (var h in _commitHandlers)
            {
                try { h(who); }
                catch (Exception ex) { PortalDebug.LogError($"[UxArmingGate] commit handler threw: {ex.Message}"); }
            }
        }

        private static int CrouchKey => _crouchKey ??= 438569 + ZSyncAnimation.GetHash("crouching");

        private static bool IsCrouching(ZDO zdo) => zdo.GetInt(CrouchKey, 0) != 0;

        /// <summary>`ZDOVars.s_isBlockingHash` ("IsBlocking") - a momentary secondary modifier distinct from the toggled crouch bit; exposed for consumers that want a transient "show more detail" mode.</summary>
        public static bool IsBlocking(ZDO zdo) => zdo.GetBool(ZDOVars.s_isBlockingHash, false);

        public static bool IsArmed(long playerId) => _armedUntilClock.TryGetValue(playerId, out float until) && _clock < until;

        public static bool TryGetArmedPortal(long playerId, out PortalRecord rec) => _armedPortal.TryGetValue(playerId, out rec) && IsArmed(playerId);

        /// <summary>Consumers call this whenever they observe in-session activity (a cycling jump/emote) so the 15s idle window doesn't lapse mid-use.</summary>
        public static void Touch(long playerId)
        {
            if (_armedUntilClock.ContainsKey(playerId))
            {
                _armedUntilClock[playerId] = _clock + (UxConfig.ArmingAutoDisarmSeconds?.Value ?? 15f);
            }
        }

        private static void Arm(long playerId, PortalRecord rec)
        {
            _armedUntilClock[playerId] = _clock + (UxConfig.ArmingAutoDisarmSeconds?.Value ?? 15f);
            _armedPortal[playerId] = rec;
        }

        public static void Disarm(long playerId)
        {
            _armedUntilClock.Remove(playerId);
            _armedPortal.Remove(playerId);
        }
    }
}
