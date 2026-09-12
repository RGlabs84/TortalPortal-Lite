namespace TortalPortalLite.Subsystems.Topology
{
    /// <summary>
    /// #231 Revision-Bump Redelivery, "the Touch primitive". ZDOMan.AddForceSendZdos only inserts a ZDO
    /// into a peer's next sync list when ZDOPeer.ShouldSend is true, which compares DataRevision/
    /// OwnerRevision against that peer's cached copy - so ForceSendZDO alone does NOT guarantee
    /// re-delivery of a ZDO whose fields did not actually change value (ZDO.Set is a no-op on an equal
    /// value and bumps no revision). Every other delivery-timing engine in this domain
    /// (DestinationPrewarm #224, Moving Destination Anchor #40, Join-Time Broadcast #223 for
    /// post-join changes) depends on this exact recipe to force a re-send, verbatim from Wonderland's
    /// WaterBuoyancyEngine (WaterBuoyancyEngine.cs:158-164): claim ownership, bump DataRevision by a
    /// large stride (so the server's revision strictly exceeds whatever a racing owner-client might
    /// reach), then ForceSendZDO.
    /// </summary>
    public static class RoutingRevisionTouch
    {
        /// <summary>Bumps DataRevision without changing any field - the caller should mutate fields BEFORE calling this if a real change is also being pushed.</summary>
        public static void Touch(ZDO zdo, bool claimOwnership = true)
        {
            if (zdo == null || !zdo.IsValid())
            {
                return;
            }
            if (claimOwnership)
            {
                // No setter checks ownership, but claiming first means OUR write, not a racing owner's
                // next small increment, is what other peers converge on next sync cycle.
                zdo.SetOwner(ZDOMan.GetSessionID());
            }
            int stride = RoutingConfig.RevisionTouchStride?.Value ?? 4096;
            zdo.DataRevision += (uint)stride;
        }

        /// <summary>Touch, then broadcast-resend to every connected peer.</summary>
        public static void TouchAndBroadcast(ZDO zdo, bool claimOwnership = true)
        {
            Touch(zdo, claimOwnership);
            if (zdo != null && zdo.IsValid())
            {
                ZDOMan.instance?.ForceSendZDO(zdo.m_uid);
            }
        }

        /// <summary>Touch, then force-resend to exactly one peer - the usual case (a specific traveller's client is stale, nobody else's is).</summary>
        public static void TouchAndSendTo(ZDO zdo, long peerUid, bool claimOwnership = true)
        {
            Touch(zdo, claimOwnership);
            if (zdo != null && zdo.IsValid())
            {
                ZDOMan.instance?.ForceSendZDO(peerUid, zdo.m_uid);
            }
        }
    }
}
