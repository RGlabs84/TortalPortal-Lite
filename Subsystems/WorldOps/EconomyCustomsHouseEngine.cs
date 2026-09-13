using System;
using System.Collections.Generic;
using UnityEngine;
using TortalPortalLite.Core;
using TortalPortalLite.Core.Data;

namespace TortalPortalLite.Subsystems.WorldOps
{
    /// <summary>
    /// #90 The Customs House. Instead of a bare disconnect, a parked gate is redirected to a
    /// server-fabricated dead-end anchor - the portal reads Connected and glows, but walking through
    /// drops the traveller in a holding area (a real, admin-built structure around the anchor position)
    /// or - the cheap variant - a couple of metres away facing back at the gate they just used.
    ///
    /// Anchor flavour: PORTAL-prefab (Game.instance.PortalPrefabHash[0]), matching the catalog's own
    /// "cheap, no extra bookkeeping" option. This means the anchor participates in vanilla's own
    /// GetPortalList()/ConnectPortals reconciler like any other portal, so it needs a tag that matches
    /// its partner (the parked gate) and a non-None connection of its own (:100600-100604's teardown
    /// test) - satisfied here with a SELF-LOOP (anchor -> itself), the same "stable topology vanilla's
    /// reconciler tolerates indefinitely" the catalog names explicitly for rings/self-loops (#101's own
    /// howItWorks). The anchor's tag is mirrored from the live gate's own current s_tag every tick
    /// (rather than composed independently), so the two can never drift out of sync for longer than one
    /// poll interval even while the gate's tag is itself being composed from several stacked economy
    /// conditions (EconomyRoutingKernel).
    ///
    /// Lifecycle is liveness-based, not JSON-diff-based: a caller "keeps an anchor alive" by calling
    /// <see cref="EnsureAnchorFacingGate"/> every tick it still wants the sink; an anchor not touched for
    /// <see cref="ReapAfterSeconds"/> is assumed abandoned (its owning mechanism stopped calling in,
    /// declaration removed, engine disabled, ...) and is destroyed - catalog #90's own "anchors
    /// accumulate... without a reaper they leak into the save forever" failure mode, addressed directly.
    /// </summary>
    public static class EconomyCustomsHouseEngine
    {
        private const float ReapAfterSeconds = 30f;

        private sealed class AnchorEntry
        {
            public ZDOID Anchor;
            public float LastTouchClock;
        }

        private static readonly Dictionary<string, AnchorEntry> _anchors = new Dictionary<string, AnchorEntry>();
        private static float _clock;
        private static float _reapTimer;

        public static void OnUpdate(float dt)
        {
            _clock += dt;
            _reapTimer += dt;
            if (_reapTimer < 5f)
            {
                return;
            }
            _reapTimer = 0f;
            Reap();
        }

        /// <summary>
        /// Ensures a decoy-sink anchor exists for <paramref name="key"/>, facing back at
        /// <paramref name="gateZdo"/> from <see cref="EconomyConfig.CustomsHouseExitDistance"/> metres in
        /// front of it, and mirrors the gate's current tag + self-loop connection onto it. Returns the
        /// anchor's ZDOID (a stable destination for the caller to point the parked gate at instead of
        /// ZDOID.None), or null if fabrication/lookup failed this tick (caller should fall back to a bare
        /// disconnect for this tick rather than block on the sink).
        /// </summary>
        public static ZDOID? EnsureAnchorFacingGate(string key, ZDO gateZdo)
        {
            if (gateZdo == null || !gateZdo.IsValid())
            {
                return null;
            }
            Vector3 gatePos = gateZdo.GetPosition();
            Quaternion gateRot = gateZdo.GetRotation();
            float exitDistance = EconomyConfig.CustomsHouseExitDistance?.Value ?? 2f;
            Vector3 anchorPos = gatePos + gateRot * Vector3.forward * exitDistance + Vector3.up * 0.1f;
            Quaternion anchorRot = gateRot * Quaternion.Euler(0f, 180f, 0f); // facing back at the gate
            return EnsureAnchorAt(key, anchorPos, anchorRot, gateZdo);
        }

        /// <summary>
        /// Ensures a decoy-sink anchor exists for <paramref name="key"/> at an ARBITRARY absolute world
        /// position (e.g. a rotating-gate destination location, unrelated to the gate it will be pointed
        /// at) and mirrors <paramref name="mirrorTagFrom"/>'s current tag + a self-loop connection onto
        /// it. Returns the anchor's ZDOID, or null if fabrication/lookup failed this tick.
        /// </summary>
        public static ZDOID? EnsureAnchorAt(string key, Vector3 pos, Quaternion rot, ZDO mirrorTagFrom)
        {
            if (EconomyConfig.CustomsHouseEnabled?.Value == false || ZDOMan.instance == null || mirrorTagFrom == null || !mirrorTagFrom.IsValid())
            {
                return null;
            }

            ZDO? anchorZdo = GetOrCreateAnchor(key, pos, rot);
            if (anchorZdo == null)
            {
                return null;
            }

            // Mirror: same tag as the live gate (satisfies vanilla's pass-1 "partner tag differs" teardown
            // test), self-loop connection (satisfies "partner's own connection is None" teardown test).
            string gateTag = mirrorTagFrom.GetString(ZDOVars.s_tag, "");
            EconomyWriteOps.Reassert(anchorZdo, gateTag, anchorZdo.m_uid);

            if (_anchors.TryGetValue(key, out AnchorEntry entry))
            {
                entry.LastTouchClock = _clock;
            }
            return anchorZdo.m_uid;
        }

        /// <summary>Explicit early release - destroys the anchor immediately rather than waiting for the reaper.</summary>
        public static void Release(string key)
        {
            if (_anchors.TryGetValue(key, out AnchorEntry entry))
            {
                Destroy(entry.Anchor);
                _anchors.Remove(key);
            }
        }

        private static ZDO? GetOrCreateAnchor(string key, Vector3 pos, Quaternion rot)
        {
            if (_anchors.TryGetValue(key, out AnchorEntry entry))
            {
                ZDO existing = ZDOMan.instance.GetZDO(entry.Anchor);
                if (existing != null && existing.IsValid())
                {
                    entry.LastTouchClock = _clock;
                    return existing;
                }
                // Anchor ZDO vanished (should not normally happen - re-fabricate rather than fail the sink).
                _anchors.Remove(key);
            }

            try
            {
                int portalHash = ResolvePortalPrefabHash();
                if (portalHash == 0)
                {
                    return null;
                }
                ZDO fresh = ZDOMan.instance.CreateNewZDO(pos, portalHash);
                // The five mandatory ZNetView.Awake-mirroring writes (catalog #90's own citation) -
                // CreateNewZDO alone sets neither persistence nor prefab, so a fabricated anchor without
                // these vanishes at the next world save.
                fresh.Persistent = true;
                fresh.Type = ZDO.ObjectType.Default;
                fresh.Distant = false;
                fresh.SetPrefab(portalHash);
                fresh.SetRotation(rot);
                fresh.SetOwner(ZDOMan.GetSessionID());
                fresh.Set(ZDOVars.s_tag, "");
                ZDOMan.instance.ForceSendZDO(fresh.m_uid);

                _anchors[key] = new AnchorEntry { Anchor = fresh.m_uid, LastTouchClock = _clock };
                PortalDebug.LogInfo($"[EconomyCustomsHouseEngine] fabricated anchor '{key}' at {pos}.");
                return fresh;
            }
            catch (Exception ex)
            {
                PortalDebug.LogError($"[EconomyCustomsHouseEngine] failed to fabricate anchor '{key}': {ex.GetType().Name}: {ex.Message}");
                return null;
            }
        }

        private static int ResolvePortalPrefabHash()
        {
            var hashes = Game.instance?.PortalPrefabHash;
            return hashes != null && hashes.Count > 0 ? hashes[0] : 0;
        }

        private static void Reap()
        {
            if (ZDOMan.instance == null || _anchors.Count == 0)
            {
                return;
            }
            var stale = new List<string>();
            foreach (KeyValuePair<string, AnchorEntry> kvp in _anchors)
            {
                if (_clock - kvp.Value.LastTouchClock > ReapAfterSeconds)
                {
                    stale.Add(kvp.Key);
                }
            }
            foreach (string key in stale)
            {
                Destroy(_anchors[key].Anchor);
                _anchors.Remove(key);
                PortalDebug.LogInfo($"[EconomyCustomsHouseEngine] reaped abandoned anchor '{key}'.");
            }
        }

        private static void Destroy(ZDOID anchorId)
        {
            ZDO zdo = ZDOMan.instance?.GetZDO(anchorId);
            if (zdo == null || !zdo.IsValid())
            {
                return;
            }
            try
            {
                zdo.SetOwner(ZDOMan.GetSessionID());
                ZDOMan.instance.DestroyZDO(zdo);
            }
            catch (Exception ex)
            {
                PortalDebug.LogWarning($"[EconomyCustomsHouseEngine] failed to destroy anchor {anchorId}: {ex.Message}");
            }
        }
    }
}
