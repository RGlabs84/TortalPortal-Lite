using System.Collections.Generic;
using UnityEngine;
using TortalPortalLite.Core;
using TortalPortalLite.Core.Data;

namespace TortalPortalLite.Subsystems.Topology
{
    /// <summary>Admin declaration for one player-requestable gate (routing.json section "playerRequestGates").</summary>
    public sealed class RoutingPlayerRequestGateDefinition
    {
        public string Name = "";
        public RoutingPosition Position = new RoutingPosition();
        public List<RoutingPosition> Choices = new List<RoutingPosition>();
        public List<string>? ChoiceNames;

        /// <summary>Emote name (case-insensitive) that cycles to the next choice while a player stands within ScanRadius. Defaults to "wave" - matches Core/Data/EmoteSignals.Is's own fallback convention.</summary>
        public string CycleEmote = "wave";
        public float ScanRadius = 8f;

        /// <summary>Optional physical item-stand position near the gate - its currently mounted item (ZDOVars.s_item, an int prefab hash) selects a choice via ItemPrefabNames (index-matched to Choices).</summary>
        public RoutingPosition? ItemStandPosition;
        public List<string>? ItemPrefabNames;
    }

    /// <summary>
    /// #36 Player-Requested Routing. Two of the catalog's three vanilla client-&gt;server channels are
    /// implemented directly, with zero client install:
    ///
    ///  - EMOTE: Core/Data/EmoteSignals already polls every connected character's s_emoteID at 0.25s and
    ///    dispatches new emotes with the player attached (Player.StartEmote writes s_emote/s_emoteID,
    ///    :15585-15598) - this engine registers a handler and, for any player within ScanRadius of a
    ///    declared gate whose emote matches CycleEmote, advances that gate's cycle
    ///    (RoutingPlayerSelectionStore) and writes the gate directly - the player is standing right next
    ///    to it, so there is no approach/JIT step involved, just an immediate Reassert.
    ///  - ITEM STAND: ZDOVars.s_item on an ItemStand ZDO (confirmed directly against the decompile:
    ///    ItemStand.SetVisualItem writes `m_nview.GetZDO().Set(ZDOVars.s_item, stableHashCode)`, and
    ///    HaveItem/GetItem both read the same field via GetInt) holds the mounted item's prefab stable
    ///    hash, 0 when empty - a physical, self-documenting destination menu polled once per tick via
    ///    ZdoSpatialQuery.FindNear (ItemStand is not a portal prefab, so PortalCensus does not index it).
    ///
    /// MAP PING is NOT implemented:
    /// // NEEDS NEW HOOK BROKER on Chat.RPC_ChatMessage(long, Vector3, int, UserInfo, string): a
    /// // postfix capturing type==3 (Talker.Type.Ping) pings would let this engine offer "middle-click
    /// // the map" as a third selection channel (Chat.SendPing invokes the routed RPC "ChatMessage" at
    /// // targetPeerID 0, registered via ZRoutedRpc.instance.Register&lt;Vector3,int,UserInfo,string&gt;
    /// // ("ChatMessage", RPC_ChatMessage) in Chat.Awake, :41626) - none of Core/Hooks/'s brokers cover
    /// // ZRoutedRpc dispatch by method name/hash, and three agents are editing Core/Hooks/ concurrently
    /// // this wave. <see cref="HandleMapPingIfWired"/> below implements the resolution logic a future
    /// // broker's handler would call - it is simply never invoked yet.
    /// </summary>
    public static class RoutingPlayerRequestEngine
    {
        private static float _timer;
        private static int _lastRegistryVersion = -1;
        private static List<RoutingPlayerRequestGateDefinition> _gates = new List<RoutingPlayerRequestGateDefinition>();
        private static bool _emoteRegistered;

        public static void Initialize()
        {
            if (_emoteRegistered)
            {
                return;
            }
            EmoteSignals.Register(OnEmote);
            _emoteRegistered = true;
        }

        public static void OnUpdate(float dt)
        {
            if (RoutingConfig.TakeoverEnabled?.Value == false || ZDOMan.instance == null)
            {
                return;
            }
            _timer += dt;
            if (_timer < 1.0f)
            {
                return;
            }
            _timer = 0f;

            if (RoutingManagedPortalRegistry.Version != _lastRegistryVersion)
            {
                _lastRegistryVersion = RoutingManagedPortalRegistry.Version;
                _gates = RoutingManagedPortalRegistry.Section<RoutingPlayerRequestGateDefinition>("playerRequestGates");
            }

            foreach (RoutingPlayerRequestGateDefinition gate in _gates)
            {
                if (gate.Choices.Count == 0 || gate.ItemStandPosition == null || gate.ItemPrefabNames == null)
                {
                    continue;
                }
                if (!RoutingPairingAuthorityEngine.TryClaim(gate.Position, $"playerrequest:{gate.Name}"))
                {
                    continue;
                }
                PollItemStand(gate);
            }
        }

        private static void PollItemStand(RoutingPlayerRequestGateDefinition gate)
        {
            List<ZDO> nearby = ZdoSpatialQuery.FindNear(gate.ItemStandPosition!.ToVector3(), 2f);
            if (nearby.Count == 0)
            {
                return;
            }
            int mountedHash = nearby[0].GetInt(ZDOVars.s_item, 0);
            if (mountedHash == 0)
            {
                return;
            }
            for (int i = 0; i < gate.ItemPrefabNames!.Count && i < gate.Choices.Count; i++)
            {
                if (string.IsNullOrEmpty(gate.ItemPrefabNames[i]))
                {
                    continue;
                }
                if (gate.ItemPrefabNames[i].GetStableHashCode() == mountedHash)
                {
                    WriteChoice(gate, i, playerId: 0L, toastTo: null);
                    return;
                }
            }
        }

        private static void OnEmote(ConnectedCharacter character, string emote)
        {
            if (RoutingConfig.TakeoverEnabled?.Value == false)
            {
                return;
            }
            foreach (RoutingPlayerRequestGateDefinition gate in _gates)
            {
                if (gate.Choices.Count == 0)
                {
                    continue;
                }
                if (!EmoteSignals.Is(emote, gate.CycleEmote, "wave"))
                {
                    continue;
                }
                float sqr = (character.Position - gate.Position.ToVector3()).sqrMagnitude;
                if (sqr > gate.ScanRadius * gate.ScanRadius)
                {
                    continue;
                }
                if (!RoutingPairingAuthorityEngine.TryClaim(gate.Position, $"playerrequest:{gate.Name}"))
                {
                    continue;
                }

                long playerId = character.PlayerId;
                int index = RoutingPlayerSelectionStore.AdvanceCycle(gate.Name, playerId, gate.Choices.Count);
                WriteChoice(gate, index, playerId, character);
            }
        }

        private static void WriteChoice(RoutingPlayerRequestGateDefinition gate, int index, long playerId, ConnectedCharacter? toastTo)
        {
            RoutingPosition destPos = gate.Choices[index];
            RoutingPlayerSelectionStore.SetSelection(gate.Name, playerId, destPos);

            ZDO? gateZdo = RoutingWriteOps.ResolveLive(gate.Position);
            ZDO? destZdo = RoutingWriteOps.ResolveLive(destPos);
            if (gateZdo == null || destZdo == null)
            {
                return;
            }

            string? name = gate.ChoiceNames != null && index < gate.ChoiceNames.Count ? gate.ChoiceNames[index] : null;
            string tag = string.IsNullOrEmpty(name) ? $"#{index + 1}/{gate.Choices.Count}" : Truncate(name!);

            if (toastTo.HasValue)
            {
                RoutingWriteOps.PrewarmToPeer(toastTo.Value.Peer.m_uid, destZdo.m_uid);
                PlayerNotify.Toast(toastTo.Value, string.IsNullOrEmpty(name) ? $"Gate set: {index + 1}/{gate.Choices.Count}" : $"Gate set: {name}");
            }

            RoutingWriteOps.Reassert(gateZdo, tag, destZdo.m_uid);
            RoutingPairingAuthorityEngine.Publish(gateZdo.m_uid, tag, destZdo.m_uid);
        }

        private static string Truncate(string s) => s.Length <= 10 ? s : s.Substring(0, 10);

        /// <summary>
        /// Dormant until a ZRoutedRpc/Chat.RPC_ChatMessage broker exists (see this file's own doc
        /// comment) - a future handler would call this with the resolved sender and pinged world
        /// position. Finds the nearest declared gate to the SENDER and, among that gate's declared
        /// Choices, the one nearest the PINGED coordinate - exactly the two-step resolution #36 describes.
        /// </summary>
        internal static void HandleMapPingIfWired(ConnectedCharacter sender, Vector3 pingedPosition)
        {
            RoutingPlayerRequestGateDefinition? nearestGate = null;
            float nearestGateSqr = float.MaxValue;
            foreach (RoutingPlayerRequestGateDefinition gate in _gates)
            {
                float sqr = (sender.Position - gate.Position.ToVector3()).sqrMagnitude;
                if (sqr < nearestGateSqr)
                {
                    nearestGateSqr = sqr;
                    nearestGate = gate;
                }
            }
            if (nearestGate == null || nearestGate.Choices.Count == 0)
            {
                return;
            }

            int bestIndex = 0;
            float bestSqr = float.MaxValue;
            for (int i = 0; i < nearestGate.Choices.Count; i++)
            {
                float sqr = (nearestGate.Choices[i].ToVector3() - pingedPosition).sqrMagnitude;
                if (sqr < bestSqr)
                {
                    bestSqr = sqr;
                    bestIndex = i;
                }
            }
            WriteChoice(nearestGate, bestIndex, sender.PlayerId, sender);
        }
    }
}
