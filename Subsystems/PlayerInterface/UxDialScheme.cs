using System.Collections.Generic;
using TortalPortalLite.Core.Data;
using TortalPortalLite.Subsystems.Foundations;

namespace TortalPortalLite.Subsystems.PlayerInterface
{
    /// <summary>
    /// #177 RECOMMENDED - "The Dial": the integration wrapper, built last, once its five pieces already
    /// existed (per this wave's own instructions): the tag CLI (#152, UxDialEngine), the sign terminal
    /// (#154/#155, UxLedgerEngine/UxSignInputEngine), map ping (#156/#270, UxMapPingEngine), gesture
    /// arming (#157/#158, UxGestureTriggerEngine/UxArmingGate), and the three feedback layers (#168 tag
    /// echo, #169 toasts, #172 world text - all UxFeedback) that every one of those input channels
    /// already funnels through via the single shared UxDialAction. This class adds only what is uniquely
    /// #177's own: the DISCOVERABILITY bootstrap the catalog calls its "honest weak point" and attacks
    /// with independent, passive chances to stumble into the system.
    ///
    /// PAIRING TAKEOVER, explicitly NOT implemented: #177's own spec calls for a Harmony PREFIX on
    /// `Game.ConnectPortals` returning false, to fully re-implement vanilla's pairing pass for unmanaged
    /// portals. `Core/Hooks/ConnectPortalsHook` is postfix-only by deliberate design (its own doc comment
    /// records that three Wave 1 engines already asked for a prefix-veto variant and were declined - a
    /// world-wide-blast-radius veto that arbitrary engines would fight over). This domain follows the
    /// same established mitigation every Wave 1 engine facing the identical limit already uses: none of
    /// this domain's engines needs it, because #152/#155/#156/etc. only ever act on portals with NO
    /// declared NetworkId - `Game.ConnectPortals` tearing down a mismatched-tag pair among UNMANAGED
    /// portals is vanilla's own long-standing behaviour, not something this domain promises to change.
    /// A player who dials by name and gets a byte-identical tag on both ends (the common case, since the
    /// echo IS the canonical name) is naturally stable under vanilla's own reconciler with no takeover
    /// needed at all.
    /// </summary>
    public static class UxDialScheme
    {
        private static readonly HashSet<long> _greetedThisSession = new HashSet<long>();
        private static float _greetTimer;

        public static void OnUpdate(float dt)
        {
            if (UxConfig.Enabled?.Value == false || UxConfig.DialEnabled?.Value == false)
            {
                return;
            }

            if (UxConfig.DialSeedHelpTagOnNewPortals?.Value != false)
            {
                SeedNewPortals();
            }

            _greetTimer += dt;
            if (_greetTimer < 5f)
            {
                return;
            }
            _greetTimer = 0f;
            GreetNewArrivals();
        }

        /// <summary>A brand-new portal's tag is empty until someone touches it. Seeding it to "?help" (the SAME string the '?' opcode itself echoes back) means the very first hover already hints at the system - the catalog's first of four independent discoverability chances.</summary>
        private static void SeedNewPortals()
        {
            if (ZDOMan.instance == null)
            {
                return;
            }
            foreach (PortalRecord rec in PortalCensus.Latest)
            {
                if (!string.IsNullOrEmpty(rec.Tag))
                {
                    continue; // already touched (by a player, by Foundations, or by a prior seed) - never overwrite
                }
                ZDO zdo = ZDOMan.instance.GetZDO(rec.Uid);
                if (zdo == null || !zdo.IsValid())
                {
                    continue;
                }
                if (!string.IsNullOrEmpty(PortalRecordStore.GetNetworkId(zdo)) || PortalRecordStore.IsLocked(zdo))
                {
                    continue; // managed or locked - not this scheme's business
                }
                UxFeedback.Echo(zdo, "?help");
            }
        }

        /// <summary>The catalog's second discoverability chance: a one-line login toast, once per player per session.</summary>
        private static void GreetNewArrivals()
        {
            foreach (ConnectedCharacter cc in ConnectedCharacters.All())
            {
                if (cc.PlayerId == 0L || !_greetedThisSession.Add(cc.PlayerId))
                {
                    continue;
                }
                UxFeedback.Toast(cc, "Portals: press E on any portal and type ? for help.");
            }
        }
    }
}
