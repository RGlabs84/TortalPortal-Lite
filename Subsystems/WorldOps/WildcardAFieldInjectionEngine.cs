using TortalPortalLite.Core;
using TortalPortalLite.Core.Data;

namespace TortalPortalLite.Subsystems.WorldOps
{
    /// <summary>
    /// #135 Field Injection Through ZNetView.LoadFields, CORRECTED by #241 ("Field Injection,
    /// Corrected - Which Portal Owns Which Field"). One engine implements both catalog entries per the
    /// task's own merge instruction - #241 does not add a new mechanism, it fixes an inverted claim in
    /// #135's own field-ownership table, so there is exactly one write recipe below, reflecting the
    /// corrected understanding.
    ///
    /// Mechanism (unchanged from #135): ZNetView.LoadFields (SERVER decompile :82669-82739, byte-identical
    /// client-side :82898-82970) runs once at the end of ZNetView.Awake, before AddInstance. If
    /// `zdo.GetBool("HasFields")` and `zdo.GetBool("HasFields" + typeName)` are both true, it reflects
    /// every PUBLIC INSTANCE field of that component off ZDO keys named "&lt;TypeName&gt;.&lt;fieldName&gt;"
    /// for int/float/bool/Vector3/string/GameObject/ItemDrop. This is a supported data contract - vanilla's
    /// own `spawn` console command writes exactly these keys (client :44423-44430) - not an exploit.
    ///
    /// THE CORRECTION (#241, which supersedes #135's own inverted table): TeleportWorld.Teleport
    /// (:143517-143549) reads `m_allowAllItems`/`m_exitDistance`/`m_activationRange` off `this` - the
    /// SOURCE portal the player is walking INTO - never the destination. So:
    ///  - m_allowAllItems: write on the SOURCE to unlock non-teleportable cargo for trips OUT of it
    ///    (still hard-blocked by Inventory.IsTeleportable's m_toolTier &gt;= 1000 gate, checked BEFORE
    ///    allowAllItems, :143533/:68862-68872).
    ///  - m_exitDistance: write on the SOURCE - it controls how far in front of the DESTINATION the
    ///    traveller appears, so to change the landing offset around hub H, write it on every portal that
    ///    points AT H, never on H itself.
    ///  - m_activationRange: write on the SOURCE - feeds the glow/target-found effect only
    ///    (UpdatePortal, :143491-143509); the transit trigger volume is a separate child collider and is
    ///    NOT affected.
    /// A write on the wrong end (the destination) is a real, silent no-op - #241's whole point.
    ///
    /// Applies-at-instantiation only (both #135 and #241 agree): a client that already has this portal
    /// instantiated keeps the prefab's default values until it leaves the ~96-112m active area and
    /// returns, or relogs (ZNetScene.PointInsideActiveArea, :82298-82320).
    /// </summary>
    public static class WildcardAFieldInjectionEngine
    {
        /// <summary>Marks HasFields/HasFields&lt;TypeName&gt; and writes one bool field, then dirties the portal chunk so the write actually persists to the next save.</summary>
        public static void SetBoolField(ZDO zdo, string componentTypeName, string fieldName, bool value)
        {
            if (zdo == null || !zdo.IsValid())
            {
                return;
            }
            PortalOwnership.ClaimAndWrite(zdo, z =>
            {
                z.Set(WildcardAZdoKeys.HasFields, true);
                z.Set(WildcardAZdoKeys.BuildHasFieldsKey(componentTypeName), true);
                z.Set(WildcardAZdoKeys.BuildFieldKey(componentTypeName, fieldName), value);
            });
            ZDOMan.instance?.SetDirtyPortals();
        }

        public static void SetFloatField(ZDO zdo, string componentTypeName, string fieldName, float value)
        {
            if (zdo == null || !zdo.IsValid())
            {
                return;
            }
            PortalOwnership.ClaimAndWrite(zdo, z =>
            {
                z.Set(WildcardAZdoKeys.HasFields, true);
                z.Set(WildcardAZdoKeys.BuildHasFieldsKey(componentTypeName), true);
                z.Set(WildcardAZdoKeys.BuildFieldKey(componentTypeName, fieldName), value);
            });
            ZDOMan.instance?.SetDirtyPortals();
        }

        // ------------------------------------------------------------------- corrected per-field helpers

        /// <summary>#241: SOURCE-side ore/cargo policy for trips leaving through <paramref name="sourcePortal"/>.</summary>
        public static void SetAllowAllItems(ZDO sourcePortal, bool allow) => SetBoolField(sourcePortal, "TeleportWorld", "m_allowAllItems", allow);

        /// <summary>#241: SOURCE-side landing offset for arrivals stepping OUT of <paramref name="sourcePortal"/> at whatever it points at.</summary>
        public static void SetExitDistance(ZDO sourcePortal, float metres) => SetFloatField(sourcePortal, "TeleportWorld", "m_exitDistance", metres);

        /// <summary>#241: SOURCE-side glow/target-found activation range - cosmetic only, does not resize the transit trigger.</summary>
        public static void SetActivationRange(ZDO sourcePortal, float metres) => SetFloatField(sourcePortal, "TeleportWorld", "m_activationRange", metres);

        public static void SetHoverOffset(ZDO portal, float metres) => SetFloatField(portal, "TeleportWorld", "m_hoverOffset", metres);

        /// <summary>Used by WildcardAAdamantGateEngine (#134/#243) - vanilla hammer refuses removal outright (Player.RemovePiece, :12364-12378) before any RPC is even sent.</summary>
        public static void SetCanBeRemoved(ZDO portal, bool canBeRemoved) => SetBoolField(portal, "Piece", "m_canBeRemoved", canBeRemoved);

        public static void SetNoSupportWear(ZDO portal, bool noSupportWear) => SetBoolField(portal, "WearNTear", "m_noSupportWear", noSupportWear);

        public static void SetNoRoofWear(ZDO portal, bool noRoofWear) => SetBoolField(portal, "WearNTear", "m_noRoofWear", noRoofWear);
    }
}
