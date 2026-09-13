using TortalPortalLite.Core;
using TortalPortalLite.Core.Data;

namespace TortalPortalLite.Subsystems.WorldOps
{
    /// <summary>
    /// #235 "Phantom Survival - Beating The Structural-Integrity Pass". `WearNTear.UpdateWear` (SERVER
    /// decompile :149662-149830) runs only on the ZDO's OWNING client, only after a 30s grace from
    /// instantiation, and - directly confirmed against the decompile by this build, not just trusted
    /// from the catalog text - `if (m_noSupportWear) { UpdateSupport(); if (!HaveSupport()) num = 100f; }`
    /// (:149705-149712): despite the field's own name, `m_noSupportWear == true` is what makes the
    /// support-collapse check RUN (both fields default true on the bare component, :149201/:149205 -
    /// ordinary building prefabs override them false in their own Inspector data so buildings normally
    /// DO need support). An unsupported phantom (floating a hand's width above a slope, or with no
    /// touching WearNTear-bearing piece) accumulates `num = 100` wear per update, i.e. the PREFAB's full
    /// m_health per tick (:149822-149828) - so a phantom with s_health = k*m_health survives exactly k
    /// updates then gets destroyed out from under whichever player is standing at it.
    ///
    /// The fix this engine applies is lever (2) from the catalog's own ranked list: a LoadFields override
    /// (`ZNetView.LoadFields`, client-honoured, reflects PUBLIC instance fields at instantiation time,
    /// SERVER decompile :82898-82970) setting `WearNTear.m_noSupportWear = false` and
    /// `WearNTear.m_noRoofWear = false` on the phantom's own ZDO - support and rain wear are then never
    /// evaluated for that instance at all, independent of placement height or nearby geometry. This is
    /// strictly cheaper and more reliable than lever (1)/(4) (placing the phantom exactly at real ground
    /// height, or building a Landing Pad under it first) and does not touch the world-wide
    /// GlobalKeys.NoBuildingFall modifier (lever (3), which disables support wear for EVERY building on
    /// the server and is persisted into the .fwl).
    ///
    /// Scope: this engine only ever hardens ZDOs THIS domain (wildcard cluster B) itself fabricates -
    /// #131 Ephemeral Event Gate portals and #130 Crypt Ingress interior anchors call
    /// <see cref="ApplyIfEnabled"/> immediately after creation. It deliberately does not reach into
    /// Subsystems/Topology's own #178/#179 phantom/anchor factories (a different domain's fabrication
    /// lifecycle, built and frozen in an earlier wave) - if that domain's own phantoms need the same
    /// hardening, that is that domain's own call to make, not this one's to impose from outside.
    /// </summary>
    public static class WildcardBPhantomSurvivalEngine
    {
        /// <summary>
        /// Writes the WearNTear LoadFields override onto <paramref name="zdo"/> if
        /// WildcardBConfig.PhantomSurvivalHardenOnCreate is on. Idempotent (safe to call every time a
        /// caller (re)creates/retargets the same ZDO) - PortalOwnership.ClaimAndWrite already no-ops
        /// cheaply when the values are already correct save for the harmless revision-tie bump.
        /// </summary>
        public static void ApplyIfEnabled(ZDO zdo)
        {
            if (zdo == null || !zdo.IsValid())
            {
                return;
            }
            if (WildcardBConfig.PhantomSurvivalHardenOnCreate?.Value == false)
            {
                return;
            }

            PortalOwnership.ClaimAndWrite(zdo, z =>
            {
                z.Set(WildcardBZdoKeys.HasFields, true);
                z.Set(WildcardBZdoKeys.HasFieldsWearNTear, true);
                z.Set(WildcardBZdoKeys.WearNTearNoSupportWear, false);
                z.Set(WildcardBZdoKeys.WearNTearNoRoofWear, false);
            });
        }
    }
}
