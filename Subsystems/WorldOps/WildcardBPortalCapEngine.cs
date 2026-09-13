using System.Collections.Generic;
using TortalPortalLite.Core;
using TortalPortalLite.Core.Data;
using TortalPortalLite.Core.Hooks;
using TortalPortalLite.Subsystems.Foundations;

namespace TortalPortalLite.Subsystems.WorldOps
{
    /// <summary>
    /// #148 "Server-Enforced Portal Caps Via The ZDO Arrival Hook". The catalog's own verified recipe is
    /// a two-hook pair - a postfix on the PRIVATE `ZDOMan.CreateNewZDO(ZDOID, Vector3, int)` (which can
    /// only remember the ZDOID, since the network-receive path's prefabHashIn is 0/-1 at that point) plus
    /// a postfix on `ZDO.Deserialize(ZPackage)` (the first moment the real prefab hash exists). Neither
    /// of those two specific methods is covered by any existing Core/Hooks/ broker, and this mod's rule
    /// is to flag a missing broker rather than add a competing Harmony patch on a hot vanilla method
    /// (four agents are editing this codebase concurrently this wave; Core/Hooks/'s whole reason to
    /// exist is exactly to prevent that).
    ///
    /// This engine gets the SAME observable result - a genuinely unbypassable-by-an-unmodded-client,
    /// per-creator portal cap enforced on arrival - without needing either hook, by combining two things
    /// this mod already has: (1) Subsystems.Foundations.PortalCensus already re-scans
    /// `ZDOMan.GetPortalList()` on its own timer and already carries `Creator` (`ZDOVars.s_creator`) per
    /// record - i.e. it is ALREADY the cached per-creator tally the catalog's own failure-mode list asks
    /// for ("cache a per-player tally... rather than walking GetPortalList() on every arrival"); (2)
    /// Core/Hooks/RpcZdoDataHook - already installed, postfix-only, fires after EVERY incoming
    /// `ZDOMan.RPC_ZDOData` call, which is exactly the network-receive path the catalog's own two-hook
    /// recipe targets - is registered here purely as a fast-path trigger that shortens this engine's next
    /// sweep to (effectively) the next frame rather than waiting out the full sweep interval, WITHOUT
    /// this engine trusting the hook's own peeked ZDOID for anything (it only flips a "recheck soon"
    /// flag - the actual enforcement always re-derives from PortalCensus, the same authoritative source
    /// every other Foundations-dependent engine in this mod already reads).
    ///
    /// Enforcement: destroys the NEWEST excess ZDOs (highest DataRevision, a real-time-ordered proxy - no
    /// true creation timestamp exists in a PortalRecord) past the configured per-creator cap, via the
    /// same owner-then-destroy recipe every other reap path in this mod uses
    /// (`SetOwner(GetSessionID())` + `DestroyZDO` - ZDOMan.DestroyZDO is a silent no-op without ownership,
    /// SERVER decompile :76929-76935) and notifies the affected player if they are currently connected
    /// (s_creator is confirmed, by reading `Player.PlacePiece`'s own call site at :12516, to be the SAME
    /// persistent id `ConnectedCharacter.PlayerId` reads off a character ZDO's `s_playerID` - both trace
    /// to `Player.GetPlayerID()`, :10144).
    /// </summary>
    public static class WildcardBPortalCapEngine
    {
        private static bool _installed;
        private static float _timer;
        private static bool _recheckSoon;

        public static void Initialize()
        {
            if (_installed)
            {
                return;
            }
            _installed = true;
            // Fast-path trigger only - see class remarks. Never used to identify WHICH zdo arrived.
            RpcZdoDataHook.RegisterPostfix(100, OnAnyZdoArrived);
        }

        private static void OnAnyZdoArrived(ZNetPeer? sender, ZDOID zdoid)
        {
            _recheckSoon = true;
        }

        public static void OnUpdate(float dt)
        {
            if (WildcardBConfig.PortalCapEnabled?.Value != true)
            {
                return;
            }

            _timer += dt;
            float interval = WildcardBConfig.PortalCapSweepSeconds?.Value ?? 2f;
            if (!_recheckSoon && _timer < interval)
            {
                return;
            }
            _timer = 0f;
            _recheckSoon = false;
            Sweep();
        }

        private static void Sweep()
        {
            if (ZDOMan.instance == null)
            {
                return;
            }
            int cap = WildcardBConfig.PortalCapPerCreator?.Value ?? 6;
            if (cap <= 0)
            {
                return;
            }

            var byCreator = new Dictionary<long, List<PortalRecord>>();
            foreach (PortalRecord rec in PortalCensus.Latest)
            {
                if (rec.Creator == 0L)
                {
                    continue; // 0 = uncapped bucket (pre-existing/admin-spawned portals with no recorded creator) - catalog's own explicit guidance
                }
                if (!byCreator.TryGetValue(rec.Creator, out List<PortalRecord> list))
                {
                    byCreator[rec.Creator] = list = new List<PortalRecord>();
                }
                list.Add(rec);
            }

            foreach (KeyValuePair<long, List<PortalRecord>> kv in byCreator)
            {
                List<PortalRecord> list = kv.Value;
                if (list.Count <= cap)
                {
                    continue;
                }
                list.Sort((a, b) => a.DataRevision.CompareTo(b.DataRevision)); // oldest-ish first
                int excess = list.Count - cap;
                for (int i = 0; i < excess; i++)
                {
                    PortalRecord victim = list[list.Count - 1 - i]; // newest excess first
                    DestroyAndNotify(victim, kv.Key, cap);
                }
            }
        }

        private static void DestroyAndNotify(PortalRecord victim, long creatorId, int cap)
        {
            ZDO zdo = ZDOMan.instance.GetZDO(victim.Uid);
            if (zdo == null || !zdo.IsValid())
            {
                return;
            }
            try
            {
                zdo.SetOwner(ZDOMan.GetSessionID());
                ZDOMan.instance.DestroyZDO(zdo);
                PortalDebug.LogAlways($"[WildcardBPortalCapEngine] destroyed excess portal {victim.Uid} (creator {creatorId}, cap {cap}) at {victim.Position:F0}.");
            }
            catch (System.Exception ex)
            {
                PortalDebug.LogWarning($"[WildcardBPortalCapEngine] destroy failed for {victim.Uid}: {ex.Message}");
                return;
            }

            foreach (ConnectedCharacter who in ConnectedCharacters.All())
            {
                if (who.PlayerId == creatorId)
                {
                    PlayerNotify.Toast(who, $"Portal limit reached ({cap} of {cap}). Your newest extra portal was removed.", center: true);
                    break;
                }
            }
        }
    }
}
