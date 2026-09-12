namespace TortalPortalLite.Core.Data
{
    /// <summary>
    /// Single source of truth for every custom ZDO key this mod writes (never `s_tag`/`s_tagauthor`
    /// themselves - those are vanilla's own TeleportWorld fields, see TagCodec.cs for the budget inside
    /// them). Exists because 95 buildable catalog options write custom ZDO state and portals save
    /// wholesale with no schema check - a silent key collision between two engines corrupts data on
    /// disk. Every key is a stable string hash (ZDOVars.GetStableHashCode(), same mechanism vanilla's
    /// own ZDOVars class uses), named "tpl_&lt;group&gt;_&lt;field&gt;" so a collision is a collision of
    /// readable strings, not of hand-picked integers.
    ///
    /// RULE: adding a key here is an orchestrator action between build waves, not something a subagent
    /// does mid-wave - see the implementation plan's "shared ZDO key / tag-budget namespace" hazard.
    /// A wave that discovers it needs a new field flags it; the field is added here before the next
    /// wave starts, never invented ad hoc inside a Subsystems/&lt;Group&gt; folder.
    /// </summary>
    public static class PortalKeys
    {
        // --- foundations: identity, record store, capability probe ---

        /// <summary>Authentic Sender Context (#44) - the verified platform user id of whoever last authored a change to this ZDO, independent of ZDO ownership.</summary>
        public static readonly int VerifiedAuthorId = "tpl_foundations_verifiedauthorid".GetStableHashCode();

        /// <summary>Portal Record Store (#50) - this portal's stable identity, independent of its per-session ZDOID (which regenerates every world load).</summary>
        public static readonly int RecordId = "tpl_foundations_recordid".GetStableHashCode();

        /// <summary>Portal Record Store (#50) - which managed network (if any) this portal belongs to, as assigned by NetworkReassertEngine - never by a player's own tag write.</summary>
        public static readonly int NetworkId = "tpl_foundations_networkid".GetStableHashCode();

        /// <summary>CapabilityProbe (#202) results, cached on a well-known world ZDO so every engine can read "is X available on this build" without re-probing.</summary>
        public static readonly int CapabilityProbeResults = "tpl_foundations_capabilityproberesults".GetStableHashCode();

        /// <summary>Schema/format version this mod's ZDO fields were last written under (#81 versioning) - lets a future version detect and migrate old data instead of misreading it.</summary>
        public static readonly int SchemaVersion = "tpl_foundations_schemaversion".GetStableHashCode();

        /// <summary>RemoteCommand Piggyback (#198) / grammar dispatcher (#199) - a monotonically increasing request id so a duplicate/replayed command is recognised and ignored.</summary>
        public static readonly int CommandSeq = "tpl_foundations_commandseq".GetStableHashCode();

        /// <summary>Portal Record Store (#50) - the verified platform user id string of whoever owns this portal, distinct from vanilla's s_creator (a bare long with no room for the rest of this record).</summary>
        public static readonly int RecordOwner = "tpl_foundations_recordowner".GetStableHashCode();

        /// <summary>Portal Record Store (#50) - 1 if this portal is admin-locked (its tag/connection are NetworkReassertEngine's alone to write; any client-authored change is reverted).</summary>
        public static readonly int RecordLocked = "tpl_foundations_recordlocked".GetStableHashCode();

        /// <summary>Portal Record Store (#50) - DateTime.UtcNow.Ticks (stamped by the server clock, at write time - never trust a client-supplied timestamp) of when this record was last claimed/modified by the mod.</summary>
        public static readonly int RecordClaimedTicks = "tpl_foundations_recordclaimedticks".GetStableHashCode();

        // --- data-store / prefab-extension (wildcard #137/#138) ---

        /// <summary>Portals As A Data Store (#137) - an arbitrary mod-private string payload piggybacked on a portal ZDO that already exists for a real reason.</summary>
        public static readonly int DataStorePayload = "tpl_wildcard_datastorepayload".GetStableHashCode();

        /// <summary>Portals As A Data Store (#137) - which logical namespace/owner-subsystem a DataStorePayload entry belongs to, so two engines sharing a host ZDO don't overwrite each other's payload.</summary>
        public static readonly int DataStoreNamespace = "tpl_wildcard_datastorenamespace".GetStableHashCode();
    }
}
