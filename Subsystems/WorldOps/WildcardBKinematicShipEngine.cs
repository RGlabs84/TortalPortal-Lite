using UnityEngine;
using TortalPortalLite.Core;

namespace TortalPortalLite.Subsystems.WorldOps
{
    /// <summary>
    /// #237 "Player's Ship, Corrected: Kinematic Ship Motion With Riders Aboard". Two distinct limits
    /// govern a moving ship, confirmed directly against the decompile by this build: non-owning clients'
    /// `ZSyncTransform.SyncPosition` extrapolates by `s_velHash * timer` then lerps 20% per FixedUpdate
    /// when the gap is &lt;5m, hard-snapping otherwise (:87700-87724); a rider's own
    /// `Character.UpdateMotion` re-glues them to their attach body ONLY while the correction is &lt;4m,
    /// clearing the attach and dropping them otherwise (:2118-2135, cited by the catalog). A server that
    /// claims the ship ZDO's ownership and advances `SetPosition`/`SetRotation` in small steps therefore
    /// tows a crewed hull as a kinematic platform PROVIDED the per-call displacement stays comfortably
    /// under that 4m threshold - this engine defaults to 0.4m per call (config
    /// `KinematicShipMaxStepMeters`), an order of magnitude under the hard limit.
    ///
    /// Because the server owns the ship, no client runs `Ship.CustomFixedUpdate`'s own physics
    /// (owner-gated, :140601-140604) - no buoyancy, no wave rocking, no sail response - so this engine's
    /// caller is responsible for holding a sane Y (water level ~30 + the prefab's own float offset) and
    /// writing rotation itself; `s_forward`/`s_rudder` become irrelevant while under kinematic tow.
    ///
    /// This engine does NOT install a ZDO.SetOwner Harmony guard (the catalog's own prerequisite,
    /// "Wonderland WaterBuoyancyEngine.ZdoSetOwnerPatch pattern") - Core/Hooks/ has no broker for
    /// ZDO.SetOwner and this mod's rule is to flag, not add, a missing one:
    /// NEEDS NEW HOOK BROKER on ZDO.SetOwner(long): purpose - veto Ship.UpdateOwner/ZDOMan.ReleaseNearbyZDOS
    /// handing a server-kinematically-towed ship's ownership back to a rider mid-tow (both currently
    /// re-home ownership every ~2s, SERVER decompile :140968-140977 / :76901-76927). Without that guard,
    /// this engine simply RE-CLAIMS ownership on every tow step (the same "re-claim before every write,
    /// never assume a prior claim still holds" rule Core/Data/PortalOwnership.cs already documents for
    /// every other ZDO write in this mod) - a rider-owned window of up to ~2s between tow steps is
    /// possible but self-heals on the next call rather than needing a veto to be correct, at the cost of
    /// an occasional visible stutter the catalog itself already names as a known limitation.
    /// </summary>
    public static class WildcardBKinematicShipEngine
    {
        /// <summary>
        /// Advances <paramref name="shipZdo"/> towards <paramref name="targetPos"/>/<paramref name="targetRot"/>
        /// by at most WildcardBConfig.KinematicShipMaxStepMeters this call - call this every tick from a
        /// route runner (this engine deliberately holds no route state of its own; see
        /// WildcardBFerryRouteEngine for the sail-based alternative that keeps vanilla physics running).
        /// Returns true once the ship has reached the target (within 1cm).
        /// </summary>
        public static bool StepTowards(ZDO shipZdo, Vector3 targetPos, Quaternion targetRot)
        {
            if (WildcardBConfig.KinematicShipEnabled?.Value != true || shipZdo == null || !shipZdo.IsValid() || ZDOMan.instance == null)
            {
                return false;
            }

            Vector3 current = shipZdo.GetPosition();
            Vector3 toTarget = targetPos - current;
            float distance = toTarget.magnitude;
            if (distance < 0.01f)
            {
                return true;
            }

            float maxStep = Mathf.Max(0.01f, WildcardBConfig.KinematicShipMaxStepMeters?.Value ?? 0.4f);
            Vector3 nextPos = distance <= maxStep ? targetPos : current + toTarget.normalized * maxStep;
            Quaternion nextRot = Quaternion.RotateTowards(shipZdo.GetRotation(), targetRot, maxStep * 30f);

            try
            {
                shipZdo.SetOwner(ZDOMan.GetSessionID());
                shipZdo.SetPosition(nextPos);
                shipZdo.SetRotation(nextRot);
            }
            catch (System.Exception ex)
            {
                PortalDebug.LogWarning($"[WildcardBKinematicShipEngine] tow step failed for {shipZdo.m_uid}: {ex.Message}");
            }
            return false;
        }

        /// <summary>Holds the ship dead still at its current position/rotation - the "stop and hold" half of the #237 arrival composite (stop the ship for the ≥15s teleport window, land the traveller on the dock via a Void Anchor/phantom, then resume tow once their character is close).</summary>
        public static void HoldPosition(ZDO shipZdo)
        {
            if (shipZdo == null || !shipZdo.IsValid() || ZDOMan.instance == null)
            {
                return;
            }
            try
            {
                shipZdo.SetOwner(ZDOMan.GetSessionID());
                shipZdo.SetPosition(shipZdo.GetPosition());
            }
            catch (System.Exception ex)
            {
                PortalDebug.LogWarning($"[WildcardBKinematicShipEngine] hold failed for {shipZdo.m_uid}: {ex.Message}");
            }
        }
    }
}
