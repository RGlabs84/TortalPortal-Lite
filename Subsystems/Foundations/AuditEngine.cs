using System;
using System.Collections.Generic;
using TortalPortalLite.Core;
using TortalPortalLite.Core.Data;
using TortalPortalLite.Core.Hooks;

namespace TortalPortalLite.Subsystems.Foundations
{
    public readonly struct AuditEntry
    {
        public readonly DateTime WhenUtc;
        public readonly ZDOID Portal;
        public readonly string Change;
        public readonly string Actor;
        public readonly bool Attributed;

        public AuditEntry(ZDOID portal, string change, string actor, bool attributed)
        {
            WhenUtc = DateTime.UtcNow;
            Portal = portal;
            Change = change;
            Actor = actor;
            Attributed = attributed;
        }
    }

    /// <summary>
    /// #72 AuditEngine - attributed change log for every tag/connection write. Two complementary
    /// sources:
    ///  (1) Census diff (census-tick latency) - the only COMPLETE source: it catches changes vanilla
    ///      itself never puts on the wire (an owning client's own in-process RPC_SetTag never leaves
    ///      that client - ZRoutedRpc.InvokeRoutedRPC skips routing when targetPeerID == m_id - but the
    ///      resulting ZDO change still reaches the server via the ordinary ZDOMan.ClientChanged sync
    ///      path, so the census still sees it, just one tick later).
    ///  (2) RpcZdoDataHook's postfix (immediate, "probable actor") - fires with the socket-verified
    ///      sender peer already resolved (piggybacked, not a second patch - see RpcZdoDataHook /
    ///      SenderContext). A change attributed this way is logged as "confirmed"; a change the census
    ///      diff catches with no matching recent RPC event is logged as "probable" - AuditLog.Flag's own
    ///      "Unknown is a different fact than unauthorized" distinction, carried through as
    ///      confirmed-vs-probable rather than conflating the two.
    /// A destroy is attributed via HandleDestroyedZdoHook, which already carries the RPC_DestroyZDO
    /// sender when the destroy came from a client request.
    /// </summary>
    public static class AuditEngine
    {
        private static readonly LinkedList<AuditEntry> _entries = new LinkedList<AuditEntry>();
        private static Dictionary<ZDOID, (string tag, ZDOID connection, long owner)> _lastSeen = new Dictionary<ZDOID, (string, ZDOID, long)>();

        /// <summary>Recent (sender, when) per portal from RpcZdoDataHook - a change diffed within this window of a matching RPC is "confirmed", not "probable".</summary>
        private static readonly Dictionary<ZDOID, (string actor, float t)> _recentRpc = new Dictionary<ZDOID, (string, float)>();
        private const float AttributionWindowSeconds = 3f;
        private static float _clock;

        public static IReadOnlyCollection<AuditEntry> Entries => _entries;

        public static void Initialize()
        {
            RpcZdoDataHook.RegisterPostfix(0, OnZdoDataFromClient);
            HandleDestroyedZdoHook.Register(1000, OnBeforeDestroy); // low priority: observe, never veto on its own
        }

        public static void OnUpdate(float dt)
        {
            _clock += dt;
            if (FoundationsConfig.AuditEnabled?.Value == false)
            {
                return;
            }
            DiffCensus();
        }

        private static void OnZdoDataFromClient(ZNetPeer? sender, ZDOID zdoid)
        {
            string actor = sender != null ? (SenderContext.HostNameOf(sender) ?? $"peer:{sender.m_uid}") : "unknown";
            _recentRpc[zdoid] = (actor, _clock);
        }

        private static bool OnBeforeDestroy(ZDO zdo, long rpcSender)
        {
            string actor = rpcSender != 0 ? $"peer-uid:{rpcSender}" : "server";
            Append(new AuditEntry(zdo.m_uid, "destroyed", actor, attributed: rpcSender != 0));
            return true; // AuditEngine never vetoes - it only watches.
        }

        private static void DiffCensus()
        {
            var current = new Dictionary<ZDOID, (string tag, ZDOID connection, long owner)>();
            foreach (PortalRecord record in PortalCensus.Latest)
            {
                current[record.Uid] = (record.Tag, record.Connection, record.Owner);
                if (_lastSeen.TryGetValue(record.Uid, out var prior))
                {
                    if (prior.tag != record.Tag)
                    {
                        AttributeAndAppend(record.Uid, $"tag '{prior.tag}' -> '{record.Tag}'");
                    }
                    if (prior.connection != record.Connection)
                    {
                        AttributeAndAppend(record.Uid, $"connection {prior.connection} -> {record.Connection}");
                    }
                }
            }
            _lastSeen = current;
        }

        private static void AttributeAndAppend(ZDOID uid, string change)
        {
            bool confirmed = _recentRpc.TryGetValue(uid, out var rpc) && (_clock - rpc.t) <= AttributionWindowSeconds;
            string actor = confirmed ? rpc.actor : "probable: recent server write or unattributed client change";
            Append(new AuditEntry(uid, change, actor, confirmed));

            if (!confirmed && FoundationsConfig.AuditToastOnBlockedRetag?.Value == true)
            {
                // A future access-control engine (Wave 2) is expected to be the one that actually
                // reverts an unauthorized change and toasts the player - AuditEngine only logs.
            }
        }

        private static void Append(AuditEntry entry)
        {
            _entries.AddLast(entry);
            int cap = FoundationsConfig.AuditRingSize?.Value ?? 2000;
            while (_entries.Count > cap)
            {
                _entries.RemoveFirst();
            }
            PortalDebug.LogInfo($"[AuditEngine] {entry.Portal} {entry.Change} (actor: {entry.Actor}, {(entry.Attributed ? "confirmed" : "probable")})");
        }
    }
}
