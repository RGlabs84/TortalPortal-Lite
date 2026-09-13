using System.Collections.Generic;
using UnityEngine;
using TortalPortalLite.Core.Data;

namespace TortalPortalLite.Subsystems.WorldOps
{
    /// <summary>
    /// Resolves the fixture (chest, item stand, fireplace, door, ...) physically bound to a managed
    /// portal by proximity - the shared primitive behind #91 Prepaid Toll Escrow, #92 Token Turnstile,
    /// #95 Charge Cells, #99 Physical Tiers, #275/#276 Door Lever/Portcullis and #277 Vault Seal.
    ///
    /// Deliberately NOT cached across ticks by ZDOID: catalog #91's own citation (ZDO.Load re-mints
    /// m_uid via ZDOID.m_loadID at every world load) means any persisted binding must key on rounded
    /// POSITION, never a raw ZDOID - and since ZdoSpatialQuery.FindNear is a bounded, cheap sector query
    /// (its own doc comment: "safe to call every tick for every tracked object"), the simplest robust
    /// design is to just re-resolve the nearest matching fixture fresh on every poll instead of
    /// maintaining a persisted binding table at all. This is also self-healing: a destroyed/replaced
    /// chest is picked up automatically on the very next poll with no explicit orphan-detection pass.
    ///
    /// Classification is by component presence on the prefab (ZNetScene.instance.GetPrefab(hash) is
    /// populated server-side, catalog's own citation :81941) - cached per prefab hash since a prefab's
    /// component set never changes at runtime.
    /// </summary>
    public static class EconomyBindingRegistry
    {
        public enum FixtureKind
        {
            Container,
            ItemStand,
            Fireplace,
            Door,
            Smelter,
            CookingStation,
            Fermenter,
            Beehive
        }

        private static readonly Dictionary<int, FixtureKind?> _prefabKindCache = new Dictionary<int, FixtureKind?>();
        private static readonly List<ZDO> _scratch = new List<ZDO>();

        /// <summary>Nearest ZDO of the given fixture kind within radius of a world position, or null.</summary>
        public static ZDO? FindNearest(Vector3 pos, float radius, FixtureKind kind)
        {
            if (ZDOMan.instance == null)
            {
                return null;
            }
            ZdoSpatialQuery.FindNear(pos, radius, _scratch);

            ZDO best = null;
            float bestDistSqr = float.MaxValue;
            foreach (ZDO zdo in _scratch)
            {
                if (Classify(zdo.GetPrefab()) != kind)
                {
                    continue;
                }
                float d = (zdo.GetPosition() - pos).sqrMagnitude;
                if (d < bestDistSqr)
                {
                    bestDistSqr = d;
                    best = zdo;
                }
            }
            return best;
        }

        /// <summary>Every ZDO within radius matching the given kind, nearest-first. Caller owns the returned list.</summary>
        public static List<ZDO> FindAll(Vector3 pos, float radius, FixtureKind kind)
        {
            var result = new List<ZDO>();
            if (ZDOMan.instance == null)
            {
                return result;
            }
            var raw = new List<ZDO>();
            ZdoSpatialQuery.FindNear(pos, radius, raw);
            foreach (ZDO zdo in raw)
            {
                if (Classify(zdo.GetPrefab()) == kind)
                {
                    result.Add(zdo);
                }
            }
            result.Sort((a, b) => (a.GetPosition() - pos).sqrMagnitude.CompareTo((b.GetPosition() - pos).sqrMagnitude));
            return result;
        }

        public static FixtureKind? Classify(int prefabHash)
        {
            if (_prefabKindCache.TryGetValue(prefabHash, out FixtureKind? cached))
            {
                return cached;
            }
            FixtureKind? kind = ClassifySlow(prefabHash);
            _prefabKindCache[prefabHash] = kind;
            return kind;
        }

        private static FixtureKind? ClassifySlow(int prefabHash)
        {
            GameObject? prefab = ZNetScene.instance?.GetPrefab(prefabHash);
            if (prefab == null)
            {
                return null;
            }
            // Order matters only in that these are mutually exclusive vanilla component sets in practice.
            if (prefab.GetComponent<Fireplace>() != null) return FixtureKind.Fireplace;
            if (prefab.GetComponent<Smelter>() != null) return FixtureKind.Smelter;
            if (prefab.GetComponent<CookingStation>() != null) return FixtureKind.CookingStation;
            if (prefab.GetComponent<Fermenter>() != null) return FixtureKind.Fermenter;
            if (prefab.GetComponent<Beehive>() != null) return FixtureKind.Beehive;
            if (prefab.GetComponent<ItemStand>() != null) return FixtureKind.ItemStand;
            if (prefab.GetComponent<Door>() != null) return FixtureKind.Door;
            if (prefab.GetComponent<Container>() != null) return FixtureKind.Container;
            return null;
        }
    }
}
