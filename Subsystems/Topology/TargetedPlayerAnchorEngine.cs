using System.Collections.Generic;
using UnityEngine;
using TortalPortalLite.Core;
using TortalPortalLite.Core.Data;
using TortalPortalLite.Subsystems.Foundations;

namespace TortalPortalLite.Subsystems.Topology
{
    /// <summary>
    /// #192 Player-Placed Anchor - lets players designate destinations with things the server can read
    /// from ZDOs, in four of the catalog's five named mechanisms:
    ///
    ///  (1) SIGN: Sign.SetText (SERVER decompile :141913-141928) writes `ZDOVars.s_text` with no RPC -
    ///      polled here via TargetedPrefabDiscovery.SignHashes. A sign whose text starts with the
    ///      configured prefix (default "#tpl ") registers its OWNER'S name-&gt;position anchor; ownership
    ///      is Piece.SetCreator's own `s_creator` (long profile UID, :136415-136424) - the same field
    ///      every placed piece carries - NOT Sign's own `s_author` (a platform-user-id STRING written by
    ///      the retag UI, a different identity system), matching the catalog's own explicit rule
    ///      ("s_creator on the anchor piece... vs the emoting player's s_playerID").
    ///  (3) WARD: a PrivateArea the claimant owns (s_creator) is offered as a plain "Ward" candidate -
    ///      no name of its own is needed (the catalog allows "simply the ward the player owns").
    ///  (5) TAGGED PORTAL: a player's own portal whose tag starts with '@' (TagCodec's own reserved
    ///      biome/region-shorthand sigil, reused here per the catalog's own worked example
    ///      `Portal tag:"@Mine"`) registers itself AS a destination directly - no phantom at all. Linking
    ///      rewrites the hub's tag to match the target portal's literal tag (both must agree for
    ///      Game.ConnectPortals' 5 s reconciler, :100594-100606) via PortalOwnership.ClaimAndWrite.
    ///      NEEDS NEW KEY: TPL_origtag, purpose: remember the target portal's own tag from before this
    ///      link overwrote it, so unlinking could restore the player's original text - not implemented
    ///      without this key (see the point of use in Unlink below); linking itself works regardless.
    ///
    ///  (2) ITEM STAND and (4) CHEST TOKEN are implemented as one shared "physical trigger near the hub"
    ///      check (TryPhysicalTriggerOverride): an admin maps a recognised item PREFAB NAME to an anchor
    ///      NAME via the route's Params (ItemMap="SurtlingCore:Mine,Coins:Bank"), and whichever mapped
    ///      item currently sits on a nearby ItemStand (`ZDOVars.s_item`) or inside a nearby Container's
    ///      inventory (ZdoInventoryIO.Load) overrides the hub's current selection.
    /// </summary>
    public static class TargetedPlayerAnchorEngine
    {
        private const string Kind = "PlayerAnchor";
        private static float _timer;
        private static float _pollTimer;

        private static ZdoSpatialQuery.PrefabSetSweeper _signSweeper;
        private static ZdoSpatialQuery.PrefabSetSweeper _wardSweeper;

        // creatorId -> ordered list of (name, pos, rot)
        private static readonly Dictionary<long, List<(string name, Vector3 pos)>> _signAnchorsByCreator = new Dictionary<long, List<(string, Vector3)>>();
        private static readonly Dictionary<long, List<Vector3>> _wardAnchorsByCreator = new Dictionary<long, List<Vector3>>();

        public static void Initialize()
        {
            EmoteSignals.Register(OnEmote);
        }

        public static void OnUpdate(float dt)
        {
            _pollTimer += dt;
            float pollInterval = TargetedConfig.PlayerAnchorPollSeconds?.Value ?? 3f;
            if (_pollTimer >= pollInterval)
            {
                _pollTimer = 0f;
                PollSigns();
                PollWards();
            }

            _timer += dt;
            float interval = TargetedConfig.MaintenanceIntervalSeconds?.Value ?? 2f;
            if (_timer < interval)
            {
                return;
            }
            _timer = 0f;
            ReassertAll();
        }

        private static void PollSigns()
        {
            if (TargetedPrefabDiscovery.SignHashes.Count == 0 || ZDOMan.instance == null)
            {
                return;
            }
            _signSweeper ??= new ZdoSpatialQuery.PrefabSetSweeper(TargetedPrefabDiscovery.SignHashes);
            var results = new List<ZDO>();
            _signSweeper.Advance(20, results);

            string prefix = TargetedConfig.PlayerAnchorSignPrefix?.Value ?? "#tpl ";
            foreach (ZDO sign in results)
            {
                if (sign == null || !sign.IsValid())
                {
                    continue;
                }
                long creator = sign.GetLong(ZDOVars.s_creator, 0L);
                if (creator == 0L)
                {
                    continue;
                }
                string text = sign.GetString(ZDOVars.s_text, "");
                if (string.IsNullOrEmpty(text) || !text.StartsWith(prefix, System.StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                string name = text.Substring(prefix.Length).Trim();
                if (name.Length == 0)
                {
                    continue;
                }
                if (!_signAnchorsByCreator.TryGetValue(creator, out var list))
                {
                    list = new List<(string, Vector3)>();
                    _signAnchorsByCreator[creator] = list;
                }
                int existing = list.FindIndex(e => e.name == name);
                Vector3 pos = sign.GetPosition();
                if (existing >= 0)
                {
                    list[existing] = (name, pos);
                }
                else
                {
                    list.Add((name, pos));
                }
            }
        }

        private static void PollWards()
        {
            if (TargetedPrefabDiscovery.PrivateAreaHashes.Count == 0 || ZDOMan.instance == null)
            {
                return;
            }
            _wardSweeper ??= new ZdoSpatialQuery.PrefabSetSweeper(TargetedPrefabDiscovery.PrivateAreaHashes);
            var results = new List<ZDO>();
            _wardSweeper.Advance(20, results);

            foreach (ZDO ward in results)
            {
                if (ward == null || !ward.IsValid())
                {
                    continue;
                }
                long creator = ward.GetLong(ZDOVars.s_creator, 0L);
                if (creator == 0L)
                {
                    continue;
                }
                if (!_wardAnchorsByCreator.TryGetValue(creator, out var list))
                {
                    list = new List<Vector3>();
                    _wardAnchorsByCreator[creator] = list;
                }
                Vector3 pos = ward.GetPosition();
                int idx = list.FindIndex(p => (p - pos).sqrMagnitude < 1f);
                if (idx < 0)
                {
                    list.Add(pos);
                }
            }
        }

        /// <summary>Every anchor candidate this player may cycle through, across signs/wards/tagged portals - see class remarks.</summary>
        private static List<(string name, Vector3 pos)> BuildCandidates(long playerId)
        {
            var list = new List<(string name, Vector3 pos)>();
            if (_signAnchorsByCreator.TryGetValue(playerId, out var signs))
            {
                list.AddRange(signs);
            }
            if (_wardAnchorsByCreator.TryGetValue(playerId, out var wards))
            {
                for (int i = 0; i < wards.Count; i++)
                {
                    list.Add((wards.Count == 1 ? "Ward" : $"Ward {i + 1}", wards[i]));
                }
            }
            foreach (PortalRecord rec in PortalCensus.Latest)
            {
                if (rec.Creator == playerId && TagCodec.HasSigil(rec.Tag, '@'))
                {
                    list.Add((TagCodec.PayloadAfter(rec.Tag, '@'), rec.Position));
                }
            }
            return list;
        }

        private static void OnEmote(ConnectedCharacter who, string emote)
        {
            if (!EmoteSignals.Is(emote, TargetedConfig.CyclePlayerEmote?.Value, "wave"))
            {
                return;
            }
            ZDO hub = FindNearestHub(who.Position, 10f);
            if (hub == null)
            {
                return;
            }

            List<(string name, Vector3 pos)> candidates = BuildCandidates(who.PlayerId);
            if (candidates.Count == 0)
            {
                PlayerNotify.Toast(who, "No destinations registered yet - place a sign with '#tpl <name>'.");
                return;
            }

            string previousName = ParseAnchorName(TargetedPhantomPortalFactory.GetKind(TargetedPhantomPortalFactory.ResolveExistingPhantom(hub)));
            int startIndex = candidates.FindIndex(c => c.name == previousName);
            int nextIndex = (startIndex + 1) % candidates.Count;
            (string name, Vector3 pos) chosen = candidates[nextIndex];

            PlaceFor(hub, who.PlayerId, chosen.name, chosen.pos);
            PlayerNotify.Toast(who, $"Destination '{chosen.name}' selected.");
        }

        private static void PlaceFor(ZDO hub, long playerId, string name, Vector3 anchorPos)
        {
            float offset = TargetedConfig.PlayerAnchorOffsetMeters?.Value ?? 2.5f;
            TargetedWorldGenValidation.Result result = TargetedWorldGenValidation.BestCompassOffset(anchorPos, offset, samples: 8, allowUnderwater: true);
            Vector3 pos = result.Ok ? result.Position : anchorPos + Vector3.forward * offset;
            Quaternion rot = TargetedWorldGenValidation.FacingTowards(pos, anchorPos);
            TargetedPhantomPortalFactory.CreateOrRetarget(hub, pos, rot, TargetedTagFormat.Named(name), $"playeranchor:{playerId}:{name}");
        }

        private static string ParseAnchorName(string kind)
        {
            if (string.IsNullOrEmpty(kind))
            {
                return null;
            }
            string[] parts = kind.Split(new[] { ':' }, 3);
            return parts.Length == 3 && parts[0] == "playeranchor" ? parts[2] : null;
        }

        private static void ReassertAll()
        {
            if (ZDOMan.instance == null)
            {
                return;
            }
            foreach (TargetedRoute route in TargetedRouteStore.RoutesOfKind(Kind))
            {
                if (!PortalCensus.TryGetByPosition(route.SourcePosition, out PortalRecord sourceRec))
                {
                    continue;
                }
                ZDO source = ZDOMan.instance.GetZDO(sourceRec.Uid);
                if (source == null || !source.IsValid())
                {
                    continue;
                }

                // Physical-trigger override (item stand / chest token) takes priority if configured and present.
                string itemMap = route.Get("ItemMap", "");
                if (!string.IsNullOrEmpty(itemMap) && TryPhysicalTriggerOverride(source.GetPosition(), itemMap, out string triggeredName, out Vector3 triggeredPos))
                {
                    long ownerForTrigger = 0L; // physical triggers aren't tied to a specific claimant
                    PlaceFor(source, ownerForTrigger, triggeredName, triggeredPos);
                    continue;
                }

                ZDO phantom = TargetedPhantomPortalFactory.ResolveExistingPhantom(source);
                if (phantom == null)
                {
                    continue;
                }
                string kind = TargetedPhantomPortalFactory.GetKind(phantom);
                string[] parts = kind.Split(new[] { ':' }, 3);
                if (parts.Length != 3 || parts[0] != "playeranchor" || !long.TryParse(parts[1], out long playerId))
                {
                    continue;
                }
                string name = parts[2];
                List<(string name, Vector3 pos)> candidates = BuildCandidates(playerId);
                int idx = candidates.FindIndex(c => c.name == name);
                if (idx < 0)
                {
                    continue; // the sign/ward/tagged-portal that named this destination is gone - leave the last-known phantom in place
                }
                PlaceFor(source, playerId, name, candidates[idx].pos);
            }
        }

        private static ZDO FindNearestHub(Vector3 pos, float radius)
        {
            if (ZDOMan.instance == null)
            {
                return null;
            }
            ZDO best = null;
            float bestDistSqr = radius * radius;
            foreach (TargetedRoute route in TargetedRouteStore.RoutesOfKind(Kind))
            {
                float d = (route.SourcePosition - pos).sqrMagnitude;
                if (d > bestDistSqr)
                {
                    continue;
                }
                if (!PortalCensus.TryGetByPosition(route.SourcePosition, out PortalRecord rec))
                {
                    continue;
                }
                ZDO z = ZDOMan.instance.GetZDO(rec.Uid);
                if (z == null || !z.IsValid())
                {
                    continue;
                }
                bestDistSqr = d;
                best = z;
            }
            return best;
        }

        /// <summary>ItemMap format: "PrefabName:AnchorName,PrefabName2:AnchorName2". Checks an item stand or container within 2x the configured offset of the hub.</summary>
        private static bool TryPhysicalTriggerOverride(Vector3 hubPos, string itemMap, out string anchorName, out Vector3 anchorPos)
        {
            anchorName = null;
            anchorPos = default;
            float radius = (TargetedConfig.PlayerAnchorOffsetMeters?.Value ?? 2.5f) * 2f;
            var nearby = ZdoSpatialQuery.FindNear(hubPos, radius);

            var map = new Dictionary<int, string>();
            foreach (string pair in itemMap.Split(','))
            {
                string[] kv = pair.Split(':');
                if (kv.Length == 2)
                {
                    map[kv[0].Trim().GetStableHashCode()] = kv[1].Trim();
                }
            }
            if (map.Count == 0)
            {
                return false;
            }

            foreach (ZDO zdo in nearby)
            {
                int prefab = zdo.GetPrefab();
                if (TargetedPrefabDiscovery.IsItemStand(prefab))
                {
                    int itemHash = zdo.GetInt(ZDOVars.s_item, 0);
                    if (itemHash != 0 && map.TryGetValue(itemHash, out string name))
                    {
                        anchorName = name;
                        anchorPos = zdo.GetPosition();
                        return true;
                    }
                }
                else if (TargetedPrefabDiscovery.IsContainer(prefab))
                {
                    if (ZdoInventoryIO.IsBusy(zdo))
                    {
                        continue; // someone has the container UI open - skip this tick rather than race them
                    }
                    Inventory inv = ZdoInventoryIO.Load(zdo, 4, 4);
                    if (inv == null)
                    {
                        continue;
                    }
                    foreach (ItemDrop.ItemData item in inv.GetAllItems())
                    {
                        if (item?.m_dropPrefab == null)
                        {
                            continue;
                        }
                        int itemHash = item.m_dropPrefab.name.GetStableHashCode();
                        if (map.TryGetValue(itemHash, out string name))
                        {
                            anchorName = name;
                            anchorPos = zdo.GetPosition();
                            return true;
                        }
                    }
                }
            }
            return false;
        }
    }
}
