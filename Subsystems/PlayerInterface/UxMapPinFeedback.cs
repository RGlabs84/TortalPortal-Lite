using System;
using System.Collections.Generic;
using UnityEngine;
using TortalPortalLite.Core;
using TortalPortalLite.Core.Data;

namespace TortalPortalLite.Subsystems.PlayerInterface
{
    /// <summary>
    /// #170 Feedback: server-pushed map pin. `Game.Start` registers `"RPC_DiscoverLocationResponse"`
    /// (string pinName, int pinType, Vector3 pos, bool showMap) on every peer, which calls
    /// `Minimap.instance.DiscoverLocation` - `save: true` HARD-CODED and no removal RPC anywhere in the
    /// assembly (the catalog's own citation), so this is a one-shot discovery gift, never a live-synced
    /// set. Sane use, per the catalog: push exactly once per (player, portal) at naming time, always
    /// `showMap: false` (a `showMap: true` re-push spams `$msg_pin_exist` and re-centres the player's
    /// map).
    ///
    /// No persisted ledger is needed to avoid duplicate pushes across a restart: `Minimap.HaveSimilarPin`
    /// already dedups by (name, type, save, position within 1m) before `AddPin` runs, so a redundant
    /// push of the identical pin is itself idempotent and silent per the catalog's own citation - an
    /// in-memory "already greeted this session" set is enough (same pattern Topology's own
    /// TargetedMapPingEngine.GreetNewArrivals already uses for its own, separate route set).
    /// </summary>
    public static class UxMapPinFeedback
    {
        private static readonly HashSet<long> _greetedThisSession = new HashSet<long>();
        private static float _timer;

        public static void OnUpdate(float dt)
        {
            if (UxConfig.Enabled?.Value == false || UxConfig.MapPinFeedbackEnabled?.Value == false)
            {
                return;
            }
            _timer += dt;
            if (_timer < 5f)
            {
                return;
            }
            _timer = 0f;
            GreetNewArrivals();
        }

        /// <summary>Called by UxDialAction the moment a player successfully names a portal (#152's "#NAME").</summary>
        public static void OnPortalNamed(ConnectedCharacter who, string name, Vector3 pos)
        {
            if (UxConfig.MapPinFeedbackEnabled?.Value == false)
            {
                return;
            }
            Push(who, $"Portal: {name}", pos);
        }

        private static void GreetNewArrivals()
        {
            foreach (ConnectedCharacter cc in ConnectedCharacters.All())
            {
                if (cc.PlayerId == 0L || !_greetedThisSession.Add(cc.PlayerId))
                {
                    continue;
                }
                foreach (UxAddressBook.AddressEntry entry in UxAddressBook.Ordered())
                {
                    Push(cc, $"Portal: {entry.Name}", entry.Record.Position);
                }
            }
        }

        private static void Push(ConnectedCharacter who, string label, Vector3 pos)
        {
            if (ZRoutedRpc.instance == null || who.Peer == null)
            {
                return;
            }
            try
            {
                ZRoutedRpc.instance.InvokeRoutedRPC(who.Peer.m_uid, "RPC_DiscoverLocationResponse", label, (int)Minimap.PinType.Icon3, pos, false);
            }
            catch (Exception ex)
            {
                PortalDebug.LogWarning($"[UxMapPinFeedback] pin push failed for {who.Name}: {ex.Message}");
            }
        }
    }
}
