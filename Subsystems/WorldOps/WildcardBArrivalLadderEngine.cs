using UnityEngine;
using TortalPortalLite.Core;

namespace TortalPortalLite.Subsystems.WorldOps
{
    /// <summary>
    /// #129 "Skyfall, Seabed and Underworld Exits" - a pure classification/advisory engine, not a
    /// stateful one: it never writes a ZDO itself. `Player.UpdateTeleport` (SERVER decompile
    /// :15329-15379) is a four-stage ladder with hard numbers this domain's own coordinate-computing
    /// engines (#130 Crypt Ingress, #131 Event Gates, #234 Landing Pad) need to reason about BEFORE
    /// they fabricate a destination, so every one of them calls <see cref="Classify"/> on its computed
    /// exit point and logs (never blocks - the catalog explicitly frames two of the three outcomes as
    /// legitimate design choices, not bugs) when the outcome is one of the two black-screen-lengthening
    /// regimes:
    ///
    ///  - H (metres of clear air straight down from the exit point) &lt;= 1000: `ZoneSystem.FindFloor`
    ///    (:115604-115613, a 1000m downward raycast from target+up*1) hits - teleport completes at the
    ///    normal t&gt;=8s gate (:143528... wait, :143528 is the boss gate; the exit gate itself is
    ///    :15349), `m_maxAirAltitude` is released at the target's own y (:15346), and the player falls
    ///    the full H metres on arrival, taking real fall damage this mod cannot see or prevent
    ///    server-side (only the s_dead flag becomes visible after the fact).
    ///  - 1000 &lt; H &lt;= 2000: FindFloor's 1000m ray misses, so the 15s fallback fires:
    ///    `ZoneSystem.GetSolidHeight` (:115525-115534, a 2000m ray spanning target.y+1000 down to
    ///    target.y-1000) hits and the player is placed 0.5m above the real ground - a soft landing, but
    ///    behind a 15-second black screen a player will report as a freeze.
    ///  - H &gt; 2000: both rays miss; the player is left at the ORIGINAL height + 0.5 and falls the
    ///    entire distance - always the worst outcome, never a legitimate design choice.
    ///
    /// Below-ground and underwater exits are the two cases the catalog names as free primitives rather
    /// than hazards: `Character.UnderWorldCheck` (:1055-1075) pops a buried arrival straight to the
    /// surface every frame with no height math needed, and the seabed (always within FindFloor's 1000m
    /// reach given sea level is a fixed 30, :113424's own m_solidRayMask setup) always completes cleanly
    /// at the normal 8s gate. Those two are classified <see cref="ArrivalOutcome.Soft"/> as well but for
    /// a different underlying reason, distinguished only in this method's own doc comment, not by a
    /// separate enum value the catalog itself does not need one for.
    ///
    /// H itself needs a real ground height, which this mod's own headless server cannot get from a
    /// physics raycast (Game.FixedUpdate pins the reference position elsewhere every tick, so
    /// ZoneSystem.GetGroundHeight's collider query always sees the no-hit fallback here) - so this
    /// method takes H as a parameter, sourced by the CALLER from `WorldGenerator.instance.GetHeight`
    /// (pure procedural noise, safe headless) plus any known TerrainComp delta (#234), never from a
    /// collider query this process cannot perform.
    /// </summary>
    public static class WildcardBArrivalLadderEngine
    {
        public enum ArrivalOutcome
        {
            /// <summary>H &lt;= 1000m: normal 8s gate, but the player free-falls H metres and takes real fall damage on landing.</summary>
            FallOnArrival,
            /// <summary>1000 &lt; H &lt;= 2000m: a 15-second black screen (vanilla portals never impose this), then a soft 0.5m landing.</summary>
            LongBlackScreenThenSoftLanding,
            /// <summary>H &gt; 2000m: both floor rays miss; the player is left at the original height and free-falls the entire distance.</summary>
            HardFail,
        }

        /// <summary>Classifies the arrival ladder outcome for an exit point <paramref name="heightAboveTerrain"/> metres above the real ground/water directly beneath it.</summary>
        public static ArrivalOutcome Classify(float heightAboveTerrain)
        {
            if (heightAboveTerrain <= 1000f)
            {
                return ArrivalOutcome.FallOnArrival;
            }
            if (heightAboveTerrain <= 2000f)
            {
                return ArrivalOutcome.LongBlackScreenThenSoftLanding;
            }
            return ArrivalOutcome.HardFail;
        }

        /// <summary>
        /// Convenience wrapper for a caller that already has the exit position and the real ground
        /// height beneath it (e.g. WorldGenerator.instance.GetHeight(exit) + any known TerrainComp
        /// delta) - logs via WildcardBConfig.WarnOnDangerousAltitude when the outcome is not a clean
        /// FallOnArrival-at-ground-level (heightAboveTerrain near zero) and returns the classification
        /// for the caller to act on (e.g. reject the coordinate, or accept it as a deliberate
        /// "punishment gate"/"stealth long-hop" per the catalog's own playerExperience framing).
        /// </summary>
        public static ArrivalOutcome ClassifyAndWarn(Vector3 exitPos, float groundHeightAtExit, string callerTag)
        {
            float heightAboveTerrain = Mathf.Max(0f, exitPos.y - groundHeightAtExit);
            ArrivalOutcome outcome = Classify(heightAboveTerrain);
            if (WildcardBConfig.WarnOnDangerousAltitude?.Value == true && outcome != ArrivalOutcome.FallOnArrival)
            {
                PortalDebug.LogWarning(
                    $"[WildcardBArrivalLadderEngine] {callerTag}: exit at {exitPos:F0} sits {heightAboveTerrain:F0}m above terrain -> {outcome} " +
                    "(Player.UpdateTeleport 8s/15s ladder, SERVER decompile :15329-15379).");
            }
            return outcome;
        }
    }
}
