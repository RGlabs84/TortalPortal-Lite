using System.Collections.Generic;
using UnityEngine;
using TortalPortalLite.Core.Data;
using TortalPortalLite.Subsystems.Foundations;

namespace TortalPortalLite.Subsystems.PlayerInterface
{
    /// <summary>
    /// #152/#177 L3 ACTION - the one atomic "change what this unmanaged portal points at" routine every
    /// UX input channel funnels through (tag CLI, sign input, map ping, gesture cycling, emote cycling,
    /// equipped item, item stand, chest token, positional plates, build-shape sockets, warp verbs). This
    /// is the shared core the catalog's #177 asks for ("one atomic routine") and the reason this whole
    /// domain has so few internal dependency edges - every input channel is thin; all the actual policy
    /// (guards, echo, pre-warm, feedback) lives exactly once, here.
    ///
    /// GUARDS, in order, matching the constraint brief's own architecture:
    ///  - a portal already carrying a NetworkId (PortalRecordStore) is owned by Foundations'
    ///    NetworkReassertEngine (#69) - this class never fights it; express a change to a MANAGED
    ///    portal's routing as a networks.json edit instead, never a direct write here.
    ///  - PortalRecordStore.IsLocked - a mod-private lock flag distinct from vanilla's tag text, the
    ///    primitive the sibling lockdown/access domains are expected to set. This class only ever reads
    ///    it; it never sets it, and never writes a '!' tag itself (TagCodec reserves '!' to
    ///    access/lockdown - see TagCodec.cs's own sigil table).
    ///  - UxWardIndex.CanDial - ward-derived permission (#167), read-only here too.
    ///
    /// For an UNMANAGED portal (no NetworkId - the common case for anything a player names/dials
    /// through this whole domain) a direct PortalOwnership.ClaimAndWrite is correct per the
    /// constraint brief's own explicit carve-out, so this class calls it directly rather than routing
    /// through NetworkReassertEngine.
    ///
    /// The connection write is immediate, never delayed for a cosmetic burst (see UxFreeBurst.cs's own
    /// doc comment for why that trade-off belongs to an explicit opt-in, not this default path) -
    /// #152's own emphasis is that PRE-WARMing the destination ZDO so the first walk-through works
    /// matters more than a visual flourish.
    /// </summary>
    public static class UxDialAction
    {
        public static bool TryName(ZDO portalZdo, string alias, ConnectedCharacter? requester, out string message)
        {
            if (!GuardCommon(portalZdo, requester, out message))
            {
                return false;
            }

            string clean = CleanAlias(alias);
            if (string.IsNullOrEmpty(clean))
            {
                message = "Name can't be empty.";
                return false;
            }
            if (UxAddressBook.TryGet(clean, out PortalRecord existing) && existing.Uid != portalZdo.m_uid)
            {
                message = $"'{clean}' is already used by another portal.";
                Notify(requester, message);
                return false;
            }

            if (!TagCodec.TryFormat('#', clean, out string tag))
            {
                clean = clean.Substring(0, System.Math.Max(1, TagCodec.MaxTagLength - 1));
                TagCodec.TryFormat('#', clean, out tag);
            }

            UxFeedback.Echo(portalZdo, tag);
            PortalRecordStore.EnsureRecordId(portalZdo);
            message = $"Named: {clean}.";
            Notify(requester, message);
            if (requester.HasValue)
            {
                UxMapPinFeedback.OnPortalNamed(requester.Value, clean, portalZdo.GetPosition());
            }
            return true;
        }

        public static bool TryDial(ZDO portalZdo, string destinationName, ConnectedCharacter? requester, out string message)
        {
            if (!GuardCommon(portalZdo, requester, out message))
            {
                return false;
            }
            if (!UxAddressBook.TryGet(destinationName, out PortalRecord dest))
            {
                message = UxAddressBook.TryFindClosest(destinationName, out string suggestion)
                    ? $"No portal named {destinationName}. Did you mean: {suggestion}?"
                    : $"No portal named {destinationName}.";
                UxFeedback.Echo(portalZdo, ("?" + destinationName));
                Notify(requester, message);
                return false;
            }
            return TryDialToRecord(portalZdo, dest, requester, out message);
        }

        public static bool TryDialByIndex(ZDO portalZdo, int oneBasedIndex, ConnectedCharacter? requester, out string message)
        {
            if (!UxAddressBook.TryGetByIndex(oneBasedIndex, out UxAddressBook.AddressEntry entry))
            {
                message = $"No destination #{oneBasedIndex}.";
                Notify(requester, message);
                return false;
            }
            return TryDialToRecord(portalZdo, entry.Record, requester, out message);
        }

        /// <summary>For channels that resolve a destination by proximity/selection rather than by typed name (map ping, positional plates, item selectors).</summary>
        public static bool TryDialToRecord(ZDO portalZdo, PortalRecord dest, ConnectedCharacter? requester, out string message)
        {
            if (!GuardCommon(portalZdo, requester, out message))
            {
                return false;
            }
            if (dest.Uid == portalZdo.m_uid)
            {
                message = "A portal can't dial itself.";
                Notify(requester, message);
                return false;
            }

            string destName = TagCodec.HasSigil(dest.Tag, '#') ? TagCodec.PayloadAfter(dest.Tag, '#') : "?";
            if (!TagCodec.TryFormat('>', destName, out string tag))
            {
                string clipped = destName.Substring(0, System.Math.Max(1, TagCodec.MaxTagLength - 1));
                TagCodec.TryFormat('>', clipped, out tag);
            }

            PortalOwnership.ClaimAndWrite(portalZdo, z => z.SetConnection(ZDOExtraData.ConnectionType.Portal, dest.Uid));
            UxFeedback.Echo(portalZdo, tag);
            PortalRecordStore.EnsureRecordId(portalZdo);

            if (requester.HasValue && requester.Value.Peer != null && ZDOMan.instance != null)
            {
                // PRE-WARM: push the destination ZDO to the dialling player NOW, so TargetFound() succeeds
                // on the very first walk-through instead of silently failing while RequestZDO round-trips.
                ZDOMan.instance.ForceSendZDO(requester.Value.Peer.m_uid, dest.Uid);
            }

            Vector3 fromPos = requester?.Position ?? portalZdo.GetPosition();
            float distance = Vector3.Distance(fromPos, dest.Position);
            string biome = PortalCensus.BiomeAt(dest.Position).ToString();
            message = $"Dialed -> {destName} ({distance:0}m, {biome})";
            Notify(requester, message);
            if (requester.HasValue)
            {
                UxFeedback.WorldText(requester.Value, portalZdo.GetPosition(), "-> " + destName.ToUpperInvariant());
            }
            return true;
        }

        public static bool TryUnlink(ZDO portalZdo, ConnectedCharacter? requester, out string message)
        {
            if (!GuardCommon(portalZdo, requester, out message))
            {
                return false;
            }
            PortalOwnership.ClaimAndWrite(portalZdo, z => z.SetConnection(ZDOExtraData.ConnectionType.Portal, ZDOID.None));
            UxFeedback.Echo(portalZdo, "-");
            message = "Unlinked.";
            Notify(requester, message);
            return true;
        }

        private static bool GuardCommon(ZDO portalZdo, ConnectedCharacter? requester, out string message)
        {
            message = "";
            if (portalZdo == null || !portalZdo.IsValid())
            {
                message = "That portal is gone.";
                return false;
            }
            if (!string.IsNullOrEmpty(PortalRecordStore.GetNetworkId(portalZdo)))
            {
                message = "Managed by network config - ask an admin.";
                Notify(requester, message);
                return false;
            }
            if (PortalRecordStore.IsLocked(portalZdo))
            {
                message = "Locked.";
                Notify(requester, message);
                return false;
            }
            if (requester.HasValue && !UxWardIndex.CanDial(portalZdo.GetPosition(), requester.Value.PlayerId, out string deny))
            {
                message = deny == "warded" ? "Warded - ask the owner." : deny;
                Notify(requester, message);
                if (deny == "warded" && UxWardIndex.TryFindWardNear(portalZdo.GetPosition(), out ZDO ward))
                {
                    UxWardPulseEngine.Pulse(ward); // #263 - visible acknowledgement at the ward itself, not just a corner toast
                }
                return false;
            }
            return true;
        }

        private static void Notify(ConnectedCharacter? requester, string message)
        {
            if (requester.HasValue)
            {
                UxFeedback.Toast(requester.Value, message);
            }
        }

        private static string CleanAlias(string alias)
        {
            return string.IsNullOrWhiteSpace(alias) ? "" : alias.Trim();
        }

        /// <summary>Shared grammar help, used by the tag CLI's '?' opcode, sign input's "help"/"list" verbs, and the login-toast bootstrap.</summary>
        public static IEnumerable<string> HelpLines()
        {
            List<UxAddressBook.AddressEntry> ordered = UxAddressBook.Ordered();
            yield return $"-- PORTALS -- {ordered.Count} named";
            yield return "#name  name this portal";
            yield return ">name / >N  dial to a portal";
            yield return "-  unlink   ?  this help";
        }
    }
}
