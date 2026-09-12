using System;
using System.Collections.Generic;
using TortalPortalLite.Core;
using TortalPortalLite.Core.Data;
using TortalPortalLite.Core.Hooks;
using TortalPortalLite.Subsystems.Foundations;
using TortalPortalLite.Subsystems.Topology;

namespace TortalPortalLite.Subsystems.PlayerInterface
{
    /// <summary>
    /// #155 Signs as INPUT - the 50-character command line. `Sign.SetText` is a pure client-side ZDO
    /// write (`ClaimOwnership()` + `Set(s_text, ...)`, no RPC at all) reaching the server as ordinary
    /// `RPC_ZDOData` - the same choke point `RpcZdoDataHook` already exposes, so this engine needs no
    /// separate discovery sweep: it resolves the incoming ZDOID directly and checks whether it is a
    /// Sign (`TargetedPrefabDiscovery.IsSign`) that happens to be the DESIGNATED input sign for some
    /// named portal within range (the lowest-y adopted sign, per `UxLedgerEngine.TryGetInputSign` - the
    /// same convention #154 reserves for it).
    ///
    /// Five-times the tag CLI's budget, on a UI every player already knows, and left permanently visible
    /// in the world rather than hidden behind a hover - the catalog's own case for why this is "the
    /// biggest usable-bandwidth win available to a zero-client mod". Reuses UxDialAction for every actual
    /// effect so a sign command and a tag command are two grammars over the exact same, single-owner
    /// action routine.
    /// </summary>
    public static class UxSignInputEngine
    {
        private static readonly Dictionary<ZDOID, string> _lastSeenText = new Dictionary<ZDOID, string>();

        public static void Initialize()
        {
            RpcZdoDataHook.RegisterPostfix(100, OnZdoDataFromClient);
        }

        private static void OnZdoDataFromClient(ZNetPeer? sender, ZDOID zdoid)
        {
            if (UxConfig.Enabled?.Value == false || UxConfig.SignInputEnabled?.Value == false)
            {
                return;
            }
            if (ZDOMan.instance == null)
            {
                return;
            }
            ZDO signZdo = ZDOMan.instance.GetZDO(zdoid);
            if (signZdo == null || !signZdo.IsValid() || !TargetedPrefabDiscovery.IsSign(signZdo.GetPrefab()))
            {
                return;
            }

            string text = signZdo.GetString(ZDOVars.s_text, "");
            if (string.IsNullOrWhiteSpace(text))
            {
                return;
            }
            if (_lastSeenText.TryGetValue(zdoid, out string last) && string.Equals(last, text, StringComparison.Ordinal))
            {
                return; // our own canonical reply, re-submitted verbatim - no-op
            }

            float radius = UxConfig.LedgerSignRadius?.Value ?? 8f;
            if (!UxAddressBook.TryNearestAnyPortal(signZdo.GetPosition(), radius, out PortalRecord portal))
            {
                return; // not near any portal at all
            }
            if (!UxLedgerEngine.TryGetInputSign(portal, out ZDO designated) || designated.m_uid != zdoid)
            {
                return; // this is a LEDGER (output) sign, not the designated input/reply sign
            }

            ZDO portalZdo = ZDOMan.instance.GetZDO(portal.Uid);
            if (portalZdo == null || !portalZdo.IsValid())
            {
                return;
            }

            ConnectedCharacter? requester = sender != null ? ResolveByPeer(sender) : null;
            string reply;
            try
            {
                reply = Execute(portalZdo, text.Trim(), requester);
            }
            catch (Exception ex)
            {
                PortalDebug.LogError($"[UxSignInputEngine] command '{text}' failed: {ex.Message}");
                reply = "error";
            }

            WriteReply(signZdo, reply);
            _lastSeenText[zdoid] = reply;
        }

        private static string Execute(ZDO portalZdo, string command, ConnectedCharacter? requester)
        {
            string lower = command.ToLowerInvariant();

            if (lower == "?" || lower == "help")
            {
                if (requester.HasValue)
                {
                    UxFeedback.Toasts(requester.Value, UxDialAction.HelpLines());
                }
                return "dial/name/list/-/?";
            }

            if (lower.StartsWith("page ") && int.TryParse(lower.Substring(5).Trim(), out int pageArg))
            {
                UxLedgerEngine.SetPage(portalZdo.m_uid, Math.Max(0, pageArg - 1));
                return $"page {pageArg}";
            }

            if (lower.StartsWith("dial "))
            {
                string dest = command.Substring(5).Trim();
                int and = dest.IndexOf(" and ", StringComparison.OrdinalIgnoreCase);
                if (and >= 0)
                {
                    dest = dest.Substring(0, and).Trim(); // "and lock"/"and ..." modifiers belong to the lockdown domain, not parsed here
                }
                UxDialAction.TryDial(portalZdo, dest, requester, out string msg);
                return Clip(msg);
            }

            if (lower.StartsWith("name this portal "))
            {
                UxDialAction.TryName(portalZdo, command.Substring("name this portal ".Length).Trim(), requester, out string msg);
                return Clip(msg);
            }
            if (lower.StartsWith("name "))
            {
                UxDialAction.TryName(portalZdo, command.Substring(5).Trim(), requester, out string msg);
                return Clip(msg);
            }

            if (lower == "unlink" || lower == "-")
            {
                UxDialAction.TryUnlink(portalZdo, requester, out string msg);
                return Clip(msg);
            }

            if (lower == "list" || lower.StartsWith("list "))
            {
                string filter = lower.Length > 4 ? lower.Substring(4).Trim() : "";
                var names = new List<string>();
                foreach (UxAddressBook.AddressEntry e in UxAddressBook.Ordered())
                {
                    if (filter.Length == 0 || e.Name.ToLowerInvariant().Contains(filter))
                    {
                        names.Add(e.Name);
                    }
                }
                return Clip(names.Count == 0 ? "none found" : string.Join(" ", names));
            }

            return $"? unknown: {Clip(command, 30)}";
        }

        private static void WriteReply(ZDO sign, string reply)
        {
            string clipped = Clip(reply);
            PortalOwnership.ClaimAndWrite(sign, z =>
            {
                z.Set(ZDOVars.s_text, clipped);
                z.Set(ZDOVars.s_author, "");
            });
        }

        private static string Clip(string s, int max = 50) => string.IsNullOrEmpty(s) || s.Length <= max ? s : s.Substring(0, max);

        private static ConnectedCharacter? ResolveByPeer(ZNetPeer peer)
        {
            foreach (ConnectedCharacter cc in ConnectedCharacters.All())
            {
                if (cc.Peer == peer)
                {
                    return cc;
                }
            }
            return null;
        }
    }
}
