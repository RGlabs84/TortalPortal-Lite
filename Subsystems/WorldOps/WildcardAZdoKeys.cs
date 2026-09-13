namespace TortalPortalLite.Subsystems.WorldOps
{
    /// <summary>
    /// Mod-private ZDO key hashes for the wildcard cluster A domain only - NOT added to the shared
    /// Core/Data/PortalKeys.cs (that file is an orchestrator-only edit point between waves; four agents
    /// write new engines concurrently this wave, so each domain mints its own subsystem-local key
    /// constants exactly as Subsystems/Topology/TargetedZdoKeys.cs already does for its own domain).
    /// Every string is prefixed "tplwc_a_" - distinct from PortalKeys.cs's own "tpl_" prefix and from
    /// any other concurrently-written wave-3 domain's own local key file - so a stable-hash collision
    /// would require an actual string collision, not just a shared short prefix.
    /// </summary>
    public static class WildcardAZdoKeys
    {
        // --- #128 Void Anchor ---
        public static readonly int VoidAnchorMarker = "tplwc_a_voidanchor".GetStableHashCode();

        // --- #132 Decoy Gate ---
        public static readonly int DecoyMarker = "tplwc_a_decoy".GetStableHashCode();

        /// <summary>
        /// ZDOID storage is a special case: ZDOExtraData.Type has no ZDOID case at all (only Float,
        /// Vec3, Quat, Int, Long, String, ByteArray - confirmed directly against the decompile), and ZDO
        /// only exposes a STRING-keyed Set(string,ZDOID)/GetZDOID(string) pair, never an int-hash-keyed
        /// one. So this is a plain string constant, not a pre-hashed int like every other key in this
        /// file - deliberately, not an oversight.
        /// </summary>
        public const string DecoyFakeTarget = "tplwc_a_decoyfaketarget";

        // --- #134/#243 Adamant Gate ---
        public static readonly int FortifiedMarker = "tplwc_a_fortified".GetStableHashCode();
        public static readonly int FortifiedIntendedHealth = "tplwc_a_fortifiedhealth".GetStableHashCode();

        // --- #135/#241 Field Injection (generic LoadFields writer keys are built from type/field names
        // at call time via BuildFieldKey below - no fixed constant needed per field).

        // --- #140 Self-Organising Networks ---
        public static readonly int TransitCount = "tplwc_a_transitcount".GetStableHashCode();
        public static readonly int LastTransitTicks = "tplwc_a_lasttransitticks".GetStableHashCode();

        // --- #142 Puzzle gates ---
        public static readonly int PuzzleName = "tplwc_a_puzzlename".GetStableHashCode();
        public static readonly int PuzzleHardLocked = "tplwc_a_puzzlehardlocked".GetStableHashCode();
        public static readonly int PuzzleSolved = "tplwc_a_puzzlesolved".GetStableHashCode();

        // --- #242 Non-Portal migration ---
        public static readonly int MigratedByThisMod = "tplwc_a_migrated".GetStableHashCode();

        /// <summary>
        /// ZNetView.LoadFields' own per-field key shape (SERVER decompile :82669-82739): the ZDO key is
        /// literally "&lt;ComponentTypeName&gt;.&lt;fieldName&gt;", read back through ZDO's STRING-keyed
        /// Get/Set overloads (which hash internally with the same GetStableHashCode() every ZDO key
        /// uses) - NOT one of this class's own "tplwc_a_" int constants, because vanilla itself reads
        /// this exact string at instantiation. Same convention Subsystems/Topology/TargetedZdoKeys.cs
        /// already uses for its own LoadFields keys (e.g. "TeleportWorld.m_exitDistance"). Exposed here
        /// so every engine that writes a LoadFields override (WildcardAFieldInjectionEngine and its
        /// callers) builds the key identically.
        /// </summary>
        public static string BuildFieldKey(string componentTypeName, string fieldName) => $"{componentTypeName}.{fieldName}";

        public static string BuildHasFieldsKey(string componentTypeName) => $"HasFields{componentTypeName}";

        public const string HasFields = "HasFields";
    }
}
