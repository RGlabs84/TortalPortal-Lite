using System.Collections.Generic;
using UnityEngine;
using TortalPortalLite.Core;
using TortalPortalLite.Core.Data;

namespace TortalPortalLite.Subsystems.WorldOps
{
    /// <summary>Admin declaration for one Ore Gate upgrade (economy.json section "oreGates") - added by the admin once payment (a toll/turnstile purchase) has been received; this engine only keeps the flags asserted, it does not itself judge payment.</summary>
    public sealed class EconomyOreGateDeclaration
    {
        public EconomyPosition Portal = new EconomyPosition();
        public bool AllowAllItems = true;
        public float? ExitDistance;
    }

    /// <summary>
    /// #98 The Ore Gate - Per-Portal Field Overrides. `ZNetView.LoadFields()` walks every PUBLIC INSTANCE
    /// FIELD on a freshly-instantiated prefab's MonoBehaviours and overrides it from matching ZDO keys -
    /// a 100%-vanilla-client mechanism this mod can drive without any client mod. The three required
    /// keys are fixed by that vanilla contract, not a custom namespace this mod owns, so they are written
    /// as the literal strings the catalog specifies (never through PortalKeys.cs, which only owns this
    /// mod's OWN "tpl_*" keys):
    ///  - "HasFields" (bool gate for the whole mechanism)
    ///  - "HasFieldsTeleportWorld" (bool gate for the TeleportWorld component specifically)
    ///  - "TeleportWorld.m_allowAllItems" (the payload - TeleportWorld's own public field)
    ///
    /// Honest tier: catalog's own citation - LoadFields runs only inside ZNetView.Awake, i.e. at
    /// instantiation, so a flag flip does not affect a client already standing at the portal (they must
    /// leave and return), and a modified client can simply set m_allowAllItems=true on every portal
    /// locally regardless of what the server ever wrote. This is CLIENT-HONOURED, not server-enforced -
    /// never composed into EconomyRoutingKernel's open/closed gating, which is reserved for mechanisms
    /// this mod can actually refuse.
    /// </summary>
    public static class EconomyOreGateEngine
    {
        private const float ReassertSeconds = 5f;
        private static int _lastVersion = -1;
        private static List<EconomyOreGateDeclaration> _gates = new List<EconomyOreGateDeclaration>();
        private static float _timer;

        public static void OnUpdate(float dt)
        {
            RefreshIfNeeded();
            _timer += dt;
            if (_timer < ReassertSeconds || _gates.Count == 0)
            {
                return;
            }
            _timer = 0f;
            foreach (EconomyOreGateDeclaration decl in _gates)
            {
                Apply(decl);
            }
        }

        private static void RefreshIfNeeded()
        {
            if (EconomyRegistry.Version != _lastVersion)
            {
                _lastVersion = EconomyRegistry.Version;
                _gates = EconomyRegistry.Section<EconomyOreGateDeclaration>("oreGates");
            }
        }

        private static void Apply(EconomyOreGateDeclaration decl)
        {
            ZDO? zdo = EconomyWriteOps.ResolveLivePortal(decl.Portal);
            if (zdo == null)
            {
                return;
            }

            bool hasFields = zdo.GetBool("HasFields");
            bool hasFieldsTw = zdo.GetBool("HasFieldsTeleportWorld");
            bool allowAll = zdo.GetBool("TeleportWorld.m_allowAllItems");
            bool exitOk = !decl.ExitDistance.HasValue || Mathf.Approximately(zdo.GetFloat("TeleportWorld.m_exitDistance"), decl.ExitDistance.Value);

            if (hasFields && hasFieldsTw && allowAll == decl.AllowAllItems && exitOk)
            {
                return;
            }

            PortalOwnership.ClaimAndWrite(zdo, z =>
            {
                z.Set("HasFields", true);
                z.Set("HasFieldsTeleportWorld", true);
                z.Set("TeleportWorld.m_allowAllItems", decl.AllowAllItems);
                if (decl.ExitDistance.HasValue)
                {
                    z.Set("TeleportWorld.m_exitDistance", decl.ExitDistance.Value);
                }
            });
            // Catalog's own citation: a LoadFields-style write does not set DirtyPortalObjects itself -
            // without this the flag is silently lost at the next world save.
            ZDOMan.instance?.SetDirtyPortals();
        }
    }
}
