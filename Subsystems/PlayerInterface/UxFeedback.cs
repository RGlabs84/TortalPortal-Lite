using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using TortalPortalLite.Core;
using TortalPortalLite.Core.Data;

namespace TortalPortalLite.Subsystems.PlayerInterface
{
    /// <summary>
    /// The domain's shared OUTPUT toolkit - four independently-cataloged feedback layers that are all,
    /// mechanically, "one server->client RPC, no capture needed", so they live together rather than as
    /// four near-empty files:
    ///
    ///   #168 Tag echo        - Echo(zdo, text) rewrites s_tag (&lt;=10 ASCII, no rich text) via
    ///                          PortalOwnership.ClaimAndWrite. GetHoverText (:143453) reads the ZDO live
    ///                          every frame, so this is the fastest, most in-context confirmation
    ///                          surface - and it needs no separate "echo alphabet": the echo IS the
    ///                          canonical parsed command (e.g. writing back "#Base" or "&gt;Haldor"),
    ///                          which is what makes re-submitting it a harmless no-op (#168's own
    ///                          idempotence requirement).
    ///   #169 Toast            - Toast/Toasts wrap PlayerNotify.Toast (Core/Data - the per-character
    ///                          "Message" RPC) with a multi-line stacking helper for directory dumps.
    ///   #172 World text       - WorldText(who, pos, text) is a hand-packed RPC_DamageText ZPackage,
    ///                          per-peer, anchored at a specific portal so multi-portal hubs get
    ///                          "WHICH one changed" instead of just a corner toast.
    ///   #176 VFX/SFX          - Vfx(who, pos, effectPrefabName) is "SpawnObject", a non-networked
    ///                          effect prefab instantiated locally on the recipient's client only - never
    ///                          use a ZNetView-bearing prefab here (would create a duplicate ZDO per
    ///                          recipient, the catalog's own explicit warning).
    /// </summary>
    public static class UxFeedback
    {
        // ---- #168 tag echo ----

        /// <summary>Clamps to TagCodec's 10-char budget, strips characters that would corrupt the quoted hover string or a `&lt;...&gt;` rich-text scan, then writes via PortalOwnership (mandatory SetDirtyPortals/ForceSendZDO bookkeeping - a tag-only write is otherwise lost at the next save, #168's own failure mode).</summary>
        public static void Echo(ZDO portalZdo, string text)
        {
            if (portalZdo == null || !portalZdo.IsValid())
            {
                return;
            }
            string clean = Sanitize(text);
            if (clean.Length > TagCodec.MaxTagLength)
            {
                clean = clean.Substring(0, TagCodec.MaxTagLength);
            }
            PortalOwnership.ClaimAndWrite(portalZdo, z => z.Set(ZDOVars.s_tag, clean));
        }

        /// <summary>ASCII-only, no `&lt;`/`&gt;` (would be stripped or misparsed by RemoveRichTextTags), no literal newline (TextInput.OnEnter unescapes "\\n" - #152's own failure mode).</summary>
        private static string Sanitize(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return "";
            }
            var sb = new StringBuilder(text.Length);
            foreach (char c in text)
            {
                if (c == '\n' || c == '\r' || c == '<' || c == '>')
                {
                    continue;
                }
                if (c < 0x20 || c > 0x7E)
                {
                    continue;
                }
                sb.Append(c);
            }
            return sb.ToString();
        }

        // ---- #169 toasts ----

        public static void Toast(ConnectedCharacter who, string message, bool center = false) => PlayerNotify.Toast(who, message, center);

        /// <summary>Stacks several TopLeft lines in one frame - how a directory listing is delivered (the catalog's own recipe: "sending 5 toasts in one frame produces 5 stacked TopLeft lines"). Caps at 6 - "keep it to ~6 lines before they scroll off".</summary>
        public static void Toasts(ConnectedCharacter who, IEnumerable<string> lines)
        {
            int n = 0;
            foreach (string line in lines)
            {
                if (n++ >= 6)
                {
                    break;
                }
                PlayerNotify.Toast(who, line);
            }
        }

        // ---- #172 floating world text (RPC_DamageText) ----

        private enum DamageTextType { Normal, Resistant, Weak, Immune, Heal, TooHard, Blocked, Bonus }

        /// <summary>Bonus = orange, 1.5x font, ~3s - the attention-grabbing, non-mangling variant (avoid TooHard/Blocked, which vanilla overwrites/prefixes with its own localized string, #172's own noted trap). 30m culling (m_maxTextDistance) - a "label what's right in front of you" surface only.</summary>
        public static void WorldText(ConnectedCharacter who, Vector3 pos, string text, bool positive = true)
        {
            if (UxConfig.WorldTextEnabled?.Value == false || ZRoutedRpc.instance == null || who.Peer == null)
            {
                return;
            }
            try
            {
                var pkg = new ZPackage();
                pkg.Write((int)(positive ? DamageTextType.Heal : DamageTextType.Bonus));
                pkg.Write(pos + Vector3.up * 2.2f);
                pkg.Write(text);
                pkg.Write(false);
                ZRoutedRpc.instance.InvokeRoutedRPC(who.Peer.m_uid, "RPC_DamageText", pkg);
            }
            catch (Exception ex)
            {
                PortalDebug.LogWarning($"[UxFeedback] WorldText failed for {who.Name}: {ex.Message}");
            }
        }

        // ---- #176 server-driven VFX/SFX (SpawnObject) ----

        /// <summary>`effectPrefabName` MUST be a non-networked effect prefab (vfx_*/sfx_*), never anything carrying a ZNetView - each recipient independently Instantiates it, so a networked prefab would mint one duplicate, permanent ZDO per recipient (the catalog's own explicit warning).</summary>
        public static void Vfx(ConnectedCharacter who, Vector3 pos, string effectPrefabName)
        {
            if (UxConfig.VfxEnabled?.Value == false || ZRoutedRpc.instance == null || who.Peer == null || string.IsNullOrEmpty(effectPrefabName))
            {
                return;
            }
            try
            {
                ZRoutedRpc.instance.InvokeRoutedRPC(who.Peer.m_uid, "SpawnObject", pos, Quaternion.identity, effectPrefabName.GetStableHashCode());
            }
            catch (Exception ex)
            {
                PortalDebug.LogWarning($"[UxFeedback] Vfx failed for {who.Name}: {ex.Message}");
            }
        }
    }
}
