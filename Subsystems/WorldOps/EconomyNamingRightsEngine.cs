using System.Collections.Generic;
using TortalPortalLite.Core;
using TortalPortalLite.Core.Data;

namespace TortalPortalLite.Subsystems.WorldOps
{
    /// <summary>Admin declaration for one purchased/reserved gate name (economy.json section "namedGates") - added by the admin once payment has been received, the same convention EconomyOreGateEngine uses.</summary>
    public sealed class EconomyNamedGateDeclaration
    {
        public EconomyPosition Portal = new EconomyPosition();
        public string Name = "";
        public bool PushPinOnFirstApply = true;
    }

    /// <summary>
    /// #104 Naming Rights and Prestige. Once EconomyRoutingKernel owns a portal's tag composition, `s_tag`
    /// is decoupled from vanilla pairing and becomes a pure display string this mod fully controls - no
    /// length cap on the write side (the 10-character limit lives only in the client's text WIDGET,
    /// catalog's own citation), no profanity filter needed server-side beyond what the admin themselves
    /// typed. Enforcement of "this name is reserved" falls straight out of the kernel's own steady-state
    /// reassertion: a rival's rename is simply overwritten back within one kernel tick, the same way any
    /// other economy condition's tag fragment self-heals - no extra mechanism required.
    ///
    /// Fragment order 0 (lowest) - a naming-rights label is meant to lead every other status fragment a
    /// stacked mechanism might contribute ("★ THE BIFROST 43/50c" reads as name-then-status).
    /// </summary>
    public static class EconomyNamingRightsEngine
    {
        private static int _lastVersion = -1;
        private static List<EconomyNamedGateDeclaration> _names = new List<EconomyNamedGateDeclaration>();
        private static readonly HashSet<ZDOID> _announced = new HashSet<ZDOID>();
        private static float _timer;

        public static void OnUpdate(float dt)
        {
            RefreshIfNeeded();
            _timer += dt;
            if (_timer < 2f || _names.Count == 0)
            {
                return;
            }
            _timer = 0f;

            foreach (EconomyNamedGateDeclaration decl in _names)
            {
                ZDO? gateZdo = EconomyWriteOps.ResolveLivePortal(decl.Portal);
                if (gateZdo == null || string.IsNullOrEmpty(decl.Name))
                {
                    continue;
                }
                ZDOID gateUid = gateZdo.m_uid;
                EconomyRoutingKernel.Publish(gateUid, "naming", true, decl.Name, 0);

                if (decl.PushPinOnFirstApply && _announced.Add(gateUid))
                {
                    EconomyAnnounce.PushPinToEveryone(decl.Name, gateZdo.GetPosition());
                    EconomyAnnounce.Broadcast($"{decl.Name} has been named.", center: true);
                }
            }
        }

        private static void RefreshIfNeeded()
        {
            if (EconomyRegistry.Version != _lastVersion)
            {
                _lastVersion = EconomyRegistry.Version;
                _names = EconomyRegistry.Section<EconomyNamedGateDeclaration>("namedGates");
            }
        }
    }
}
