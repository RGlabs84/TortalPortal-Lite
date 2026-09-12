using System;
using System.Collections.Generic;
using TortalPortalLite.Core;
using TortalPortalLite.Core.Data;
using TortalPortalLite.Core.Hooks;
using TortalPortalLite.Subsystems.Foundations;

namespace TortalPortalLite.Subsystems.PlayerInterface
{
    /// <summary>
    /// #152 The Dial - portal tag as a command line. Per the catalog's own OBSERVATION, `RPC_SetTag` is
    /// dead server-side (handled locally by the retagging client's own `ZNetView.InvokeRPC`, which
    /// resolves the target as the ZDO's owner and skips `RouteRPC` entirely when that owner is the
    /// requester itself) - the change instead arrives as ordinary ZDO data through
    /// `ZDOMan.RPC_ZDOData` -&gt; `ZDO.Deserialize`, which `RpcZdoDataHook` already turns into a
    /// (sender, zdoid) postfix event for every incoming client write, portal or not. This engine filters
    /// to portals via `PortalCensus.TryGet` and diffs the tag.
    ///
    /// GRAMMAR (char 0 = opcode, payload after it), deliberately narrower than the catalog's own
    /// worked example: '&gt;' and '@' are ALREADY reserved by TagCodec to the targeted domain
    /// ("named-NPC/trader/location shorthand" and "biome/region shorthand" respectively - see
    /// TagCodec.cs's own sigil table), and '!' is reserved to access/lockdown and explicitly
    /// "never player-writable by convention". So this grammar keeps only the sigil TagCodec's own doc
    /// comment already assigns jointly to "targeted/UX domain" ('#') and reuses '&gt;' with the IDENTICAL
    /// meaning Targeted already gives it ("points at a named target") rather than inventing a
    /// conflicting one - a player typing "&gt;Haldor" and Targeted's own engines auto-writing "&gt;Haldor"
    /// onto a hub agree on what the sigil means, they just have different authors. Lock ('!') and
    /// world-biome-target ('@') are intentionally NOT implemented here: the former belongs to the
    /// lockdown domain (this engine only ever READS PortalRecordStore.IsLocked, via UxDialAction's
    /// guard), the latter is already Targeted's own biome-shorthand feature (#185) and re-implementing
    /// it here would duplicate, not extend, Wave 1's work.
    ///   '#NAME'   name this portal (an address-book alias - see UxAddressBook)
    ///   '&gt;NAME'   dial to a named portal
    ///   '&gt;N'      dial by index (the N-th entry of the last '?' listing)
    ///   '-'       unlink
    ///   '?'       help + directory (six-toast dump, #177's own bootstrap)
    ///
    /// IDEMPOTENCE (the catalog's own explicit failure mode: "a player who opens the dialog and presses
    /// OK re-submits the echo as a command"): after every dispatch this engine re-reads the ZDO's tag
    /// (now the canonical echo UxDialAction just wrote) and remembers THAT as the last-seen value, so a
    /// verbatim re-submission of the current echo is a silent no-op on the next incoming write. Our own
    /// echo write never re-enters this handler in the first place - RpcZdoDataHook only fires for
    /// INCOMING client packets, never the server's own ZDOMan.ForceSendZDO - but a player re-opening the
    /// box and hitting Enter on the same text IS a new incoming client packet, hence this guard.
    /// </summary>
    public static class UxDialEngine
    {
        private static readonly Dictionary<ZDOID, string> _lastSeenTag = new Dictionary<ZDOID, string>();

        public static void Initialize()
        {
            RpcZdoDataHook.RegisterPostfix(100, OnZdoDataFromClient);
        }

        private static void OnZdoDataFromClient(ZNetPeer? sender, ZDOID zdoid)
        {
            if (UxConfig.Enabled?.Value == false || UxConfig.DialEnabled?.Value == false)
            {
                return;
            }
            if (!PortalCensus.TryGet(zdoid, out PortalRecord rec))
            {
                return;
            }
            string tag = rec.Tag;
            if (string.IsNullOrEmpty(tag))
            {
                return;
            }
            if (_lastSeenTag.TryGetValue(zdoid, out string last) && string.Equals(last, tag, StringComparison.Ordinal))
            {
                return;
            }

            if (ZDOMan.instance == null)
            {
                return;
            }
            ZDO zdo = ZDOMan.instance.GetZDO(zdoid);
            if (zdo == null || !zdo.IsValid())
            {
                return;
            }

            ConnectedCharacter? requester = sender != null ? ResolveByPeer(sender) : null;
            try
            {
                Dispatch(zdo, tag, requester);
            }
            catch (Exception ex)
            {
                PortalDebug.LogError($"[UxDialEngine] dispatch failed for tag '{tag}': {ex.Message}");
            }

            // Remember the RESULT (our own canonical echo, if we wrote one) so a verbatim re-submission is a no-op next time.
            _lastSeenTag[zdoid] = zdo.GetString(ZDOVars.s_tag, tag);
        }

        private static void Dispatch(ZDO zdo, string tag, ConnectedCharacter? requester)
        {
            char op = tag[0];
            string payload = tag.Length > 1 ? tag.Substring(1) : "";

            switch (op)
            {
                case '#':
                    UxDialAction.TryName(zdo, payload, requester, out _);
                    break;

                case '>':
                    if (int.TryParse(payload, out int index))
                    {
                        UxDialAction.TryDialByIndex(zdo, index, requester, out _);
                    }
                    else
                    {
                        UxDialAction.TryDial(zdo, payload, requester, out _);
                    }
                    break;

                case '-':
                    UxDialAction.TryUnlink(zdo, requester, out _);
                    break;

                case '?':
                    if (requester.HasValue)
                    {
                        UxFeedback.Toasts(requester.Value, UxDialAction.HelpLines());
                    }
                    UxFeedback.Echo(zdo, "?help");
                    break;

                default:
                    // No recognised opcode - a bare/plain tag is vanilla's own business (its own
                    // identical-tag pairing convention or untouched user text, per TagCodec's sigil
                    // table); this engine leaves it alone.
                    break;
            }
        }

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
