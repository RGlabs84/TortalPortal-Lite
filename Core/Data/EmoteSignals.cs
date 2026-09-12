using System;
using System.Collections.Generic;
using TortalPortalLite.Core;

namespace TortalPortalLite.Core.Data
{
    /// <summary>
    /// The one player-to-server signal a completely vanilla client always delivers: emotes. This is
    /// TortalPortal Lite's only vanilla-client input channel besides the tag string and map ping - the
    /// UX subsystem's gesture-based interaction schemes (arming/confirming a destination pick, etc.)
    /// are built on this.
    ///
    /// Why not chat commands: a stock client never sends a custom slash command anywhere -
    /// Chat.InputText strips the slash and runs the rest as a LOCAL console command. Plain chat text is
    /// one routed RPC per OTHER listed player, and ZRoutedRpc.InvokeRoutedRPC only routes when
    /// targetPeerID != m_id - a player alone on the server produces no chat traffic at all.
    ///
    /// Emotes are different: Player.StartEmote writes the emote name into the character ZDO (s_emote)
    /// and bumps a counter (s_emoteID), and that ZDO is synced to the server unconditionally. This
    /// poller watches the counter on every connected character and hands each new emote to the
    /// registered handlers with the player attached. StopEmote bumps the counter with an empty name;
    /// those are ignored. The first sighting of a character only seeds its counter, so an emote that
    /// was mid-way when the server learned about the player never fires.
    ///
    /// Forked with edits from Wonderland's Core/Data/EmoteSignals.cs: its config gate
    /// (WonderlandConfig.PlayerControlsEnabled) is Wonderland-specific and not carried over - this
    /// mod's gate is the mod-wide GlobalConfig.Enabled, since emotes are this mod's primary input
    /// channel, not an optional extra.
    /// </summary>
    public static class EmoteSignals
    {
        private const float PollInterval = 0.25f;

        private static readonly Dictionary<ZDOID, int> _lastEmoteId = new Dictionary<ZDOID, int>();
        private static readonly HashSet<ZDOID> _seen = new HashSet<ZDOID>();
        private static readonly List<ZDOID> _departed = new List<ZDOID>();
        private static readonly List<Action<ConnectedCharacter, string>> _handlers = new List<Action<ConnectedCharacter, string>>();
        private static float _timer;

        public static void Register(Action<ConnectedCharacter, string> handler)
        {
            if (handler != null && !_handlers.Contains(handler))
            {
                _handlers.Add(handler);
            }
        }

        public static void OnUpdate(float dt)
        {
            if (GlobalConfig.Enabled?.Value != true)
            {
                if (_lastEmoteId.Count > 0)
                {
                    // Re-enabling later must seed afresh, not replay whatever happened while disabled.
                    _lastEmoteId.Clear();
                }
                return;
            }

            _timer += dt;
            if (_timer < PollInterval)
            {
                return;
            }
            _timer = 0f;

            _seen.Clear();
            foreach (ConnectedCharacter who in ConnectedCharacters.All())
            {
                ZDOID id = who.Zdo.m_uid;
                _seen.Add(id);

                int emoteId = who.Zdo.GetInt(ZDOVars.s_emoteID, 0);
                if (!_lastEmoteId.TryGetValue(id, out int last))
                {
                    _lastEmoteId[id] = emoteId;
                    continue;
                }
                if (emoteId == last)
                {
                    continue;
                }
                _lastEmoteId[id] = emoteId;

                string emote = who.Zdo.GetString(ZDOVars.s_emote, "");
                if (string.IsNullOrEmpty(emote))
                {
                    continue;
                }
                Dispatch(who, emote);
            }

            if (_lastEmoteId.Count > _seen.Count)
            {
                _departed.Clear();
                foreach (ZDOID id in _lastEmoteId.Keys)
                {
                    if (!_seen.Contains(id))
                    {
                        _departed.Add(id);
                    }
                }
                foreach (ZDOID id in _departed)
                {
                    _lastEmoteId.Remove(id);
                }
            }
        }

        private static void Dispatch(ConnectedCharacter who, string emote)
        {
            PortalDebug.LogInfo($"[EmoteSignals] {who.Name} -> {emote} at {who.Position:F0}");
            foreach (Action<ConnectedCharacter, string> handler in _handlers)
            {
                try
                {
                    handler(who, emote);
                }
                catch (Exception ex)
                {
                    PortalDebug.LogError($"[EmoteSignals] handler failed for '{emote}' from {who.Name}: {ex.Message}\n{ex.StackTrace}");
                }
            }
        }

        /// <summary>Case-insensitive match of a received emote against a configured name, with a fallback when the config is blank.</summary>
        public static bool Is(string emote, string? configured, string fallback)
        {
            string want = string.IsNullOrWhiteSpace(configured) ? fallback : configured!.Trim();
            return emote.Equals(want, StringComparison.OrdinalIgnoreCase);
        }
    }
}
