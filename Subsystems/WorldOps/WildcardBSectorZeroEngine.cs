using UnityEngine;
using TortalPortalLite.Core;

namespace TortalPortalLite.Subsystems.WorldOps
{
    /// <summary>
    /// #143 "Sector Zero - Portals Outside The World, And Server-Side Instantiation" MERGED with its own
    /// gap-fill correction #244 "Sector Zero, Named - The Headless Zone-Spawn Predicate And Its Reaper"
    /// into ONE engine reflecting the corrected understanding (per this wave's own instructions: #244 is
    /// not a second, conflicting mechanism, it is #143 with the exact predicate and reaper pinned down).
    ///
    /// The mechanism (confirmed against the decompile directly by this build, not just trusted from the
    /// catalog text): `ZoneSystem.SectorToIndex(int sectorX, int sectorY)` (SERVER decompile
    /// :115771-115783) computes `(uint)(sectorX+256)` / `(uint)(sectorY+256)` and clamps to `Sector = 0`
    /// whenever either exceeds 511 - i.e. legal zone coordinates are sectorX/sectorY in [-256, 255]
    /// inclusive. Zone (-256,-256) legitimately maps to bucket 0 as well (it is IN range, but happens to
    /// BE index 0's own zone). `Game.FixedUpdate` pins the server's own reference position at
    /// (1e6, 0, 1e6) every physics tick (zone (15625, 15625), also out of range) - so the server's own
    /// area-of-interest and every out-of-range mod ZDO share exactly one bucket.
    ///
    /// Consequence #244 pins down precisely: whether the server actually INSTANTIATES GameObjects for
    /// that shared bucket (rather than merely bookkeeping it) hinges on one predicate,
    /// `ZoneSystem.instance.IsActiveAreaLoaded()` evaluated at zone (15625,15625) - which itself depends
    /// on `CreateLocalZones`/`SpawnZone` having actually succeeded for every zone within simulation
    /// distance of that pinned point (unverified without a live server; this engine logs the decisive
    /// test on a timer rather than assuming either way). If true, `ZNetScene.CreateObjectsSorted`'s
    /// SERVER-ONLY branch (:82137-82143) is the ONLY code in the game that reaps a ZDO whose prefab
    /// cannot be instantiated (a Void Anchor is prefab 0, i.e. always "cannot be instantiated") - and it
    /// only ever sees bucket 0. So an out-of-range anchor is either reaped within a frame (if the
    /// predicate is true) or sits forever in a bucket nothing but the server's own bookkeeping ever
    /// queries (if false) - never a stable, safe destination either way.
    ///
    /// This engine's real job for the rest of this domain is the guard, `IsSafeCoordinate`: every other
    /// WildcardB engine that computes a destination coordinate (Crypt Ingress interior probe, an Event
    /// Gate's configured position, a Landing Pad disc centre) calls this FIRST and refuses to fabricate
    /// anything at a coordinate that would land in bucket 0, exactly reproducing
    /// `ZoneSystem.SectorToIndex`'s own clamp test rather than an approximate world-edge margin.
    /// </summary>
    public static class WildcardBSectorZeroEngine
    {
        private static readonly Vector2s PinnedServerZone = new Vector2s(15625, 15625);
        private static readonly Vector2s Bucket0OwnZone = new Vector2s(-256, -256);

        private static float _timer;

        public static void OnUpdate(float dt)
        {
            if (WildcardBConfig.Enabled?.Value == false)
            {
                return;
            }
            _timer += dt;
            float interval = WildcardBConfig.SectorZeroProbeIntervalSeconds?.Value ?? 60f;
            if (_timer < interval)
            {
                return;
            }
            _timer = 0f;
            ProbeOnce();
        }

        private static void ProbeOnce()
        {
            if (ZoneSystem.instance == null || ZNetScene.instance == null)
            {
                return;
            }
            try
            {
                bool activeAreaLoaded = ZoneSystem.instance.IsActiveAreaLoaded();
                bool pinnedZoneCreated = ZoneSystem.instance.m_zones.ContainsKey(PinnedServerZone);
                int instances = ZNetScene.instance.NrOfInstances();
                PortalDebug.LogInfo(
                    $"[WildcardBSectorZeroEngine] #244 decisive test - IsActiveAreaLoaded={activeAreaLoaded}, " +
                    $"m_zones.ContainsKey({PinnedServerZone.x},{PinnedServerZone.y})={pinnedZoneCreated}, NrOfInstances={instances}. " +
                    (activeAreaLoaded
                        ? "Predicate TRUE on this build - out-of-range ZDOs (prefab 0 especially) ARE reaped/instantiated server-side; never fabricate there."
                        : "Predicate FALSE (or not yet observed) on this build - out-of-range ZDOs sit inert in bucket 0; still never fabricate there (undefined, not 'safe')."));
            }
            catch (System.Exception ex)
            {
                PortalDebug.LogWarning($"[WildcardBSectorZeroEngine] probe failed: {ex.Message}");
            }
        }

        /// <summary>
        /// True only if <paramref name="pos"/>'s zone is in the legal [-256,255]x[-256,255] range AND is
        /// not zone (-256,-256) itself (legal, but shares bucket 0 with every out-of-range coordinate -
        /// see #244's own failure-mode note). Every WildcardB fabrication call site should gate on this
        /// before minting a phantom/anchor/terrain-compiler ZDO at a computed coordinate.
        /// </summary>
        public static bool IsSafeCoordinate(Vector3 pos)
        {
            Vector2s zone = ZoneSystem.GetZone(pos);
            uint zx = (uint)(zone.x + 256);
            uint zy = (uint)(zone.y + 256);
            bool outOfRange = zx >= 512u || zy >= 512u;
            if (outOfRange)
            {
                return false;
            }
            return zone.x != Bucket0OwnZone.x || zone.y != Bucket0OwnZone.y;
        }
    }
}
