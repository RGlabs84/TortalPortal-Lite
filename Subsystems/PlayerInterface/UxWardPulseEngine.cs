using System;
using TortalPortalLite.Core;

namespace TortalPortalLite.Subsystems.PlayerInterface
{
    /// <summary>
    /// #263 Ward Pulse - FlashShield on any guard stone. `PrivateArea.Awake` registers the no-arg
    /// `"FlashShield"` RPC, whose handler is a bare `m_flashEffect.Create(...)` with no owner/sender/
    /// enabled check - the server only needs the ward's ZDOID (from UxWardIndex's own discovery, no
    /// separate lookup). Paired with UxDialAction's own ward-permission denial: a non-permitted player
    /// trying to dial a warded portal gets a visible "someone is trying that ward" pulse at the ward
    /// itself, not just a toast in the corner.
    /// </summary>
    public static class UxWardPulseEngine
    {
        public static void Pulse(ZDO ward)
        {
            if (ward == null || !ward.IsValid() || ZRoutedRpc.instance == null)
            {
                return;
            }
            try
            {
                ZRoutedRpc.instance.InvokeRoutedRPC(0L, ward.m_uid, "FlashShield");
            }
            catch (Exception ex)
            {
                PortalDebug.LogWarning($"[UxWardPulseEngine] pulse failed: {ex.Message}");
            }
        }
    }
}
