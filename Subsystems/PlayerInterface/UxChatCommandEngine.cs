using System;
using TortalPortalLite.Core.Data;
using TortalPortalLite.Subsystems.Foundations;

namespace TortalPortalLite.Subsystems.PlayerInterface
{
    /// <summary>
    /// #271 Plain chat as a command line - needs-ingame-check. The catalog's own re-classification:
    /// `Chat.InputText` hands slash text to the LOCAL `Terminal` (never leaves the client) and rewrites
    /// plain text to `"say &lt;text&gt;"`, which goes through `CheckPermissionsAndSendChatMessageRPCsAsync`.
    /// THAT method has two shapes depending on whether `PlatformManager.DistributionPlatform.
    /// RelationsProvider` is null: if null, it broadcasts to `targetPeerID 0` (which DOES reach a
    /// dedicated server, even for a solo player); otherwise a solo player's chat produces zero packets at
    /// all. Whether Steam's `RelationsProvider` is null on THIS build is not something this mod can
    /// determine from the decompile alone (the Splatform assembly's Steam backend isn't in the
    /// decompiled set) - the catalog's own circumstantial evidence leans toward "non-null" (a "Relations
    /// provider was unavailable... this should never happen!" log line and a friends-list UI branch both
    /// assume Steam always has one). Per the catalog's own instruction: do not ship a feature on this
    /// channel before an in-game test confirms a solo player's chat text actually reaches
    /// `ZRoutedRpc.RPC_RoutedRPC` with `m_targetPeerID == 0`.
    ///
    /// Consequently `UxConfig.ChatCommandEnabled` defaults OFF, and CAPTURE IS NOT WIRED UP for the same
    /// structural reason as #156/#157: reading a routed RPC's payload by method name (here,
    /// `"ChatMessage"` with `type` 1/2, Normal/Shout) needs `ZRoutedRpc.RPC_RoutedRPC(ZRpc, ZPackage)` in
    /// scope, which no existing `Core/Hooks/` broker exposes to handlers.
    ///
    /// // NEEDS NEW HOOK BROKER on ZRoutedRpc.RPC_RoutedRPC(ZRpc,ZPackage): purpose - the same broker
    /// #156/#157 need (observe a routed RPC's deserialized parameters by method-hash match, authenticated
    /// socket in scope, ZPackage read position preserved). Once BOTH that broker exists AND the in-game
    /// test above confirms solo chat actually reaches the server, wire `HandleChatText` below in as its
    /// receiver for `"ChatMessage"` where `type == 1 || type == 2`.
    ///
    /// The grammar below is real and ready: a configurable prefix (default "tp:") followed by the same
    /// small command set the tag CLI and sign input already share, bound to whichever portal the sender
    /// is standing nearest, via the one shared UxDialAction.
    /// </summary>
    public static class UxChatCommandEngine
    {
        /// <summary>Ready-to-call receiver for the capture broker described above.</summary>
        public static void HandleChatText(ZNetPeer senderPeer, string rawText)
        {
            if (UxConfig.Enabled?.Value == false || UxConfig.ChatCommandEnabled?.Value != true || string.IsNullOrEmpty(rawText))
            {
                return;
            }
            string prefix = UxConfig.ChatCommandPrefix?.Value ?? "tp:";
            if (!rawText.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            string command = rawText.Substring(prefix.Length).Trim();
            if (command.Length == 0)
            {
                return;
            }

            ConnectedCharacter? whoOpt = ResolveByPeer(senderPeer);
            if (!whoOpt.HasValue)
            {
                return;
            }
            ConnectedCharacter who = whoOpt.Value;

            float radius = UxConfig.PortalProximityRadius?.Value ?? 6f;
            if (!UxAddressBook.TryNearestAnyPortal(who.Position, radius, out PortalRecord portal))
            {
                UxFeedback.Toast(who, "Stand near a portal first.");
                return;
            }
            ZDO portalZdo = ZDOMan.instance?.GetZDO(portal.Uid);
            if (portalZdo == null || !portalZdo.IsValid())
            {
                return;
            }

            string lower = command.ToLowerInvariant();
            if (lower == "?" || lower == "help")
            {
                UxFeedback.Toasts(who, UxDialAction.HelpLines());
            }
            else if (lower.StartsWith("dial "))
            {
                UxDialAction.TryDial(portalZdo, command.Substring(5).Trim(), who, out _);
            }
            else if (lower.StartsWith("name "))
            {
                UxDialAction.TryName(portalZdo, command.Substring(5).Trim(), who, out _);
            }
            else if (lower == "unlink" || lower == "-")
            {
                UxDialAction.TryUnlink(portalZdo, who, out _);
            }
            else
            {
                UxFeedback.Toast(who, $"Unknown command: {command}");
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
