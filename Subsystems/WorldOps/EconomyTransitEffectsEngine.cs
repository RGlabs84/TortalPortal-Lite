using System;
using System.Collections.Generic;
using TortalPortalLite.Core;
using TortalPortalLite.Core.Data;
using TortalPortalLite.Subsystems.Foundations;
using TortalPortalLite.Subsystems.Topology;

namespace TortalPortalLite.Subsystems.WorldOps
{
    /// <summary>Admin declaration for one transit-triggered status effect (economy.json section "transitEffects").</summary>
    public sealed class EconomyTransitEffectDeclaration
    {
        /// <summary>"sickness" (#281, granted on arrival), "blessing" (#282, granted on arrival) or "visibleCooldown" (#283, granted on departure, escalates on repeat abuse).</summary>
        public string Kind = "sickness";

        /// <summary>Vanilla status effect display name, hashed the same way SEMan's own precomputed hashes are (name.GetStableHashCode()). Catalog-verified safe choices: "Wet", "Frost" (sickness/cooldown-visible - both survive a remote grant per Player.UpdateEnvStatusEffects' own auto-removal list), "Rested" (blessing).</summary>
        public string StatusEffectName = "Wet";

        /// <summary>How long (real seconds) this engine keeps re-pinging the grant to hold it alive.</summary>
        public float WindowSeconds = 120f;

        /// <summary>Empty = triggers on ANY detected transit; non-empty = only transits through one of these declared portals.</summary>
        public List<EconomyPosition> Portals = new List<EconomyPosition>();

        /// <summary>visibleCooldown only: a second transit by the same player within this many seconds of the first escalates to a temporary Debtor's-Lien-style restriction on every gate that player built (catalog's own "the only real enforcement available" escalation).</summary>
        public float PerPlayerCooldownSeconds = 60f;
    }

    /// <summary>
    /// #281 Portal Sickness + #282 Arrival Blessing + #283 Per-Player Cooldown Made Visible - grouped
    /// because all three are the exact same primitive (detect a transit, grant a vanilla status effect,
    /// re-ping it to survive past its own TTL) aimed at three different feelings. Honestly
    /// "reactive-detection-only": there is no server hook on the moment of transit
    /// (RoutingTransitDetector's own doc comment), so nothing here ever stops a trip - it only decorates
    /// one that already happened, and #283's real enforcement escalation is delegated entirely to
    /// EconomyDebtLienEngine's temporary-restriction lien, the one part of this family that is genuinely
    /// server-enforced.
    ///
    /// There is no remove-status-effect RPC anywhere in the assembly (grep confirms it), so a grant can
    /// only be allowed to expire - this engine's only lever is re-pinging (StatusEffect.ResetTime) within
    /// the configured window and then simply stopping.
    /// </summary>
    public static class EconomyTransitEffectsEngine
    {
        private sealed class ActiveGrant
        {
            public long PlayerId;
            public string EffectName = "";
            public float ExpiresAtClock;
        }

        private static int _lastVersion = -1;
        private static List<EconomyTransitEffectDeclaration> _declarations = new List<EconomyTransitEffectDeclaration>();
        private static readonly RoutingTransitDetector _detector = new RoutingTransitDetector();
        private static readonly List<ActiveGrant> _active = new List<ActiveGrant>();

        // visibleCooldown abuse tracking: (declaration index, playerId) -> world-time of last departure transit.
        private static readonly Dictionary<(int, long), double> _lastTransitTime = new Dictionary<(int, long), double>();

        private static float _pollTimer;
        private static float _repingTimer;
        private static float _clock;

        public static void OnUpdate(float dt)
        {
            _clock += dt;
            if (EconomyConfig.TransitEffectsEnabled?.Value == false)
            {
                return;
            }
            RefreshIfNeeded();

            _pollTimer += dt;
            float pollInterval = EconomyConfig.TransitEffectsPollSeconds?.Value ?? 0.5f;
            if (_pollTimer >= pollInterval)
            {
                _pollTimer = 0f;
                PollTransits();
            }

            _repingTimer += dt;
            float repingInterval = EconomyConfig.StatusRepingSeconds?.Value ?? 8f;
            if (_repingTimer >= repingInterval)
            {
                _repingTimer = 0f;
                RepingActive();
            }
        }

        private static void RefreshIfNeeded()
        {
            if (EconomyRegistry.Version != _lastVersion)
            {
                _lastVersion = EconomyRegistry.Version;
                _declarations = EconomyRegistry.Section<EconomyTransitEffectDeclaration>("transitEffects");
            }
        }

        private static void PollTransits()
        {
            if (_declarations.Count == 0)
            {
                return;
            }
            float radius = EconomyConfig.TransitStraddleRadius?.Value ?? 8f;
            var transits = _detector.Poll(radius);
            if (transits.Count == 0)
            {
                return;
            }

            for (int i = 0; i < _declarations.Count; i++)
            {
                EconomyTransitEffectDeclaration decl = _declarations[i];
                foreach (RoutingTransitDetector.Transit transit in transits)
                {
                    bool onArrival = decl.Kind != "visibleCooldown";
                    PortalRecord relevant = onArrival ? transit.To : transit.From;
                    if (!Matches(decl, relevant))
                    {
                        continue;
                    }

                    if (decl.Kind == "visibleCooldown")
                    {
                        HandleVisibleCooldown(decl, i, transit.Character);
                    }
                    else
                    {
                        Grant(transit.Character, decl.StatusEffectName, decl.WindowSeconds);
                    }
                }
            }
        }

        private static bool Matches(EconomyTransitEffectDeclaration decl, PortalRecord record)
        {
            if (decl.Portals.Count == 0)
            {
                return true;
            }
            foreach (EconomyPosition pos in decl.Portals)
            {
                if (PortalCensusPositionMatches(pos, record))
                {
                    return true;
                }
            }
            return false;
        }

        private static bool PortalCensusPositionMatches(EconomyPosition pos, PortalRecord record)
        {
            UnityEngine.Vector3 declared = pos.ToVector3();
            return (declared - record.Position).sqrMagnitude < 1f;
        }

        private static void HandleVisibleCooldown(EconomyTransitEffectDeclaration decl, int declIndex, ConnectedCharacter who)
        {
            if (who.PlayerId == 0 || ZNet.instance == null)
            {
                return;
            }
            double now = ZNet.instance.GetTimeSeconds();
            var key = (declIndex, who.PlayerId);
            if (_lastTransitTime.TryGetValue(key, out double last) && now - last < decl.PerPlayerCooldownSeconds)
            {
                // Repeat hop inside the visible-cooldown window - escalate (#283's own citation: this is
                // the only genuinely enforceable consequence available here).
                EconomyDebtLienEngine.AddTemporaryRestriction(who.PlayerId, decl.PerPlayerCooldownSeconds);
                PlayerNotify.Toast(who, "Your gates are suspended - hopping too fast.");
            }
            _lastTransitTime[key] = now;
            Grant(who, decl.StatusEffectName, decl.PerPlayerCooldownSeconds);
        }

        private static void Grant(ConnectedCharacter who, string effectName, float windowSeconds)
        {
            if (string.IsNullOrEmpty(effectName) || ZRoutedRpc.instance == null || who.Peer == null)
            {
                return;
            }
            try
            {
                int nameHash = effectName.GetStableHashCode();
                ZRoutedRpc.instance.InvokeRoutedRPC(who.Peer.m_uid, who.Zdo.m_uid, "RPC_AddStatusEffect", nameHash, true, 0, 0f, -1);

                _active.RemoveAll(g => g.PlayerId == who.PlayerId && g.EffectName == effectName);
                _active.Add(new ActiveGrant { PlayerId = who.PlayerId, EffectName = effectName, ExpiresAtClock = _clock + windowSeconds });
            }
            catch (Exception ex)
            {
                PortalDebug.LogWarning($"[EconomyTransitEffectsEngine] status grant '{effectName}' failed for {who.Name}: {ex.Message}");
            }
        }

        private static void RepingActive()
        {
            if (_active.Count == 0)
            {
                return;
            }
            _active.RemoveAll(g => _clock >= g.ExpiresAtClock);
            if (_active.Count == 0)
            {
                return;
            }

            var byId = new Dictionary<long, ConnectedCharacter>();
            foreach (ConnectedCharacter c in ConnectedCharacters.All())
            {
                if (c.PlayerId != 0)
                {
                    byId[c.PlayerId] = c;
                }
            }

            foreach (ActiveGrant grant in _active)
            {
                if (byId.TryGetValue(grant.PlayerId, out ConnectedCharacter who))
                {
                    if (ZRoutedRpc.instance != null && who.Peer != null)
                    {
                        try
                        {
                            int nameHash = grant.EffectName.GetStableHashCode();
                            ZRoutedRpc.instance.InvokeRoutedRPC(who.Peer.m_uid, who.Zdo.m_uid, "RPC_AddStatusEffect", nameHash, true, 0, 0f, -1);
                        }
                        catch (Exception ex)
                        {
                            PortalDebug.LogWarning($"[EconomyTransitEffectsEngine] re-ping failed for {who.Name}: {ex.Message}");
                        }
                    }
                }
            }
        }
    }
}
