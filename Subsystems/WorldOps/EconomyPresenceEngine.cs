using System.Collections.Generic;
using TortalPortalLite.Core;
using TortalPortalLite.Core.Data;

namespace TortalPortalLite.Subsystems.WorldOps
{
    /// <summary>Admin declaration for one presence-gated route (economy.json section "presenceRoutes"). #278 and #279 share this shape - the difference is only which gate is named and whether a grace window applies, per each option's own "same predicate machinery, polarity flipped per direction" framing.</summary>
    public sealed class EconomyPresenceDeclaration
    {
        public EconomyPosition Portal = new EconomyPosition();
        public EconomyPosition? Destination;
        public string Label = "GATE";

        /// <summary>PlayerProfile ids (ZDOVars.s_playerID / s_creator's shared id space) whose presence keeps this route wired.</summary>
        public List<long> OwnerIds = new List<long>();

        /// <summary>"presence" (#278: gate open only while an owner is online) or "offlineShield" (#279: same predicate, but with a grace window so a crash-and-rejoin doesn't strand the owner outside their own base).</summary>
        public string Mode = "presence";

        /// <summary>offlineShield only: keeps the route open for this many real seconds after the last owner leaves before actually cutting it.</summary>
        public float GraceSeconds = 60f;
    }

    /// <summary>
    /// #278 Presence-Wired Routes + #279 Offline-Raid Shield (Inverse Presence) - a route exists only
    /// while its owning network has at least one member online. Inputs are all server-resident:
    /// ConnectedCharacters.All() gives every currently-connected PlayerId (ZDOVars.s_playerID); the
    /// declaration names which ids count as "this route's network" rather than deriving it from
    /// s_creator automatically (catalog's own failure mode: s_creator is 0 for admin/world-gen
    /// structures, and deriving network membership purely from tag-sharing risks pulling in unrelated
    /// portals) - an admin declares the roster explicitly in economy.json.
    ///
    /// #279's own distinguishing idea ("cutting the far end's connection is sufficient - B's own state is
    /// irrelevant to inbound travel, because TeleportWorld.Teleport resolves the destination from the
    /// SOURCE portal's connection") is exactly why this engine gates the DECLARED portal's own
    /// destination rather than needing to touch the protected base's gate at all - an offlineShield
    /// declaration simply names the PUBLIC/inbound gate as `Portal` and the protected base as
    /// `Destination`.
    /// </summary>
    public static class EconomyPresenceEngine
    {
        private static int _lastVersion = -1;
        private static List<EconomyPresenceDeclaration> _routes = new List<EconomyPresenceDeclaration>();
        private static float _timer;

        public static void OnUpdate(float dt)
        {
            RefreshIfNeeded();
            _timer += dt;
            float interval = EconomyConfig.PresenceEvalSeconds?.Value ?? 1f;
            if (_timer < interval || _routes.Count == 0)
            {
                return;
            }
            _timer = 0f;

            var onlineIds = new HashSet<long>();
            foreach (ConnectedCharacter c in ConnectedCharacters.All())
            {
                if (c.PlayerId != 0)
                {
                    onlineIds.Add(c.PlayerId);
                }
            }

            foreach (EconomyPresenceDeclaration decl in _routes)
            {
                Evaluate(decl, onlineIds);
            }
        }

        private static void RefreshIfNeeded()
        {
            if (EconomyRegistry.Version != _lastVersion)
            {
                _lastVersion = EconomyRegistry.Version;
                _routes = EconomyRegistry.Section<EconomyPresenceDeclaration>("presenceRoutes");
            }
        }

        private static void Evaluate(EconomyPresenceDeclaration decl, HashSet<long> onlineIds)
        {
            ZDO? gateZdo = EconomyWriteOps.ResolveLivePortal(decl.Portal);
            if (gateZdo == null)
            {
                return;
            }
            ZDOID gateUid = gateZdo.m_uid;

            if (decl.Destination != null)
            {
                ZDO? destZdo = EconomyWriteOps.ResolveLivePortal(decl.Destination);
                if (destZdo != null)
                {
                    EconomyRoutingKernel.SetDestination(gateUid, destZdo.m_uid);
                }
            }

            bool anyOwnerOnline = false;
            foreach (long id in decl.OwnerIds)
            {
                if (onlineIds.Contains(id))
                {
                    anyOwnerOnline = true;
                    break;
                }
            }

            string stateKey = EconomyStateStore.PositionKey("presence", decl.Portal.ToVector3());
            bool open;
            if (decl.Mode == "offlineShield")
            {
                if (anyOwnerOnline)
                {
                    EconomyStateStore.RemoveLong(stateKey);
                    open = true;
                }
                else
                {
                    long lastOnlineMs = EconomyStateStore.GetLong(stateKey, 0L);
                    if (lastOnlineMs == 0L)
                    {
                        // First tick observed offline - start the grace window now.
                        lastOnlineMs = (long)(ZNet.instance != null ? ZNet.instance.GetTimeSeconds() * 1000.0 : 0.0);
                        EconomyStateStore.SetLong(stateKey, lastOnlineMs);
                    }
                    double elapsedSinceOffline = ZNet.instance != null ? ZNet.instance.GetTimeSeconds() - lastOnlineMs / 1000.0 : double.MaxValue;
                    open = elapsedSinceOffline < decl.GraceSeconds;
                }
            }
            else
            {
                open = anyOwnerOnline;
            }

            EconomyRoutingKernel.Publish(gateUid, "presence", open, open ? decl.Label : $"{decl.Label} away", 10);
        }
    }
}
