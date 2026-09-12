using BepInEx.Configuration;
using ServerSync;
using TortalPortalLite.Core;

namespace TortalPortalLite.Subsystems.PlayerInterface
{
    /// <summary>
    /// Config surface for the whole `ux` domain (wave2_ux.json, options #152-177/#260-272) - how a
    /// player on a 100% vanilla client actually drives this mod. Own subsystem, own config file, same
    /// rule every other subsystem in this mod follows (see Core/ConfigBinder.cs).
    ///
    /// Section numbering starts at "30" - Foundations uses 1-8, Topology/Routing/Targeted use their own
    /// low numbers, and the sibling Wave 2 domains (access/lockdown) are expected to claim their own
    /// blocks too. This is cosmetic ordering in the generated .cfg only; BepInEx sections never collide
    /// functionally across subsystems (config identity is section+key, and no other subsystem shares
    /// these key names).
    /// </summary>
    public static class UxConfig
    {
        // --- General ---
        public static ConfigEntry<bool>? Enabled;
        public static ConfigEntry<float>? PortalProximityRadius;

        // --- #152/#177 The Dial (tag CLI) ---
        public static ConfigEntry<bool>? DialEnabled;
        public static ConfigEntry<bool>? DialSeedHelpTagOnNewPortals;

        // --- #154 The Ledger / #155 Signs as input ---
        public static ConfigEntry<bool>? LedgerEnabled;
        public static ConfigEntry<float>? LedgerRewriteSeconds;
        public static ConfigEntry<float>? LedgerSignRadius;
        public static ConfigEntry<bool>? SignInputEnabled;

        // --- #156/#270 Map ping ---
        public static ConfigEntry<bool>? MapPingEnabled;
        public static ConfigEntry<float>? MapPingClaimRadius;
        public static ConfigEntry<float>? MapPingDebounceSeconds;
        public static ConfigEntry<bool>? MapPingRequireArmed;

        // --- #157 Gesture triggers (jump/dodge cycle) ---
        public static ConfigEntry<bool>? GestureTriggerEnabled;
        public static ConfigEntry<float>? GestureWindowSeconds;
        public static ConfigEntry<float>? GestureMaxMoveMeters;

        // --- #158 Posture bits / arming gate ---
        public static ConfigEntry<bool>? ArmingEnabled;
        public static ConfigEntry<float>? ArmingAutoDisarmSeconds;
        public static ConfigEntry<float>? ArmingPollSeconds;

        // --- #159 Emote input ---
        public static ConfigEntry<bool>? EmoteInputEnabled;
        public static ConfigEntry<string>? EmoteAdvance;
        public static ConfigEntry<string>? EmoteConfirm;
        public static ConfigEntry<string>? EmoteCancel;
        public static ConfigEntry<string>? EmoteList;

        // --- #160 Equipped item selector ---
        public static ConfigEntry<bool>? EquipSelectorEnabled;
        public static ConfigEntry<string>? EquipBindingsFile;
        public static ConfigEntry<float>? EquipPollSeconds;

        // --- #161 Item stand board ---
        public static ConfigEntry<bool>? ItemStandBoardEnabled;
        public static ConfigEntry<float>? ItemStandDiscoveryRadius;
        public static ConfigEntry<float>? ItemStandRescanSeconds;

        // --- #162 Chest tokens ---
        public static ConfigEntry<bool>? ChestTokenEnabled;
        public static ConfigEntry<string>? ChestTokenBindingsFile;
        public static ConfigEntry<float>? ChestDiscoveryRadius;
        public static ConfigEntry<float>? ChestRescanSeconds;

        // --- #164 Positional input (plates) ---
        public static ConfigEntry<bool>? PositionalInputEnabled;
        public static ConfigEntry<string>? PlatesFile;
        public static ConfigEntry<float>? PositionalPollSeconds;
        public static ConfigEntry<float>? PlateDwellSeconds;

        // --- #165 Build-shape switches ---
        public static ConfigEntry<bool>? BuildShapeEnabled;
        public static ConfigEntry<string>? SocketsFile;
        public static ConfigEntry<float>? SocketMatchRadius;
        public static ConfigEntry<bool>? EnforcePortalCap;
        public static ConfigEntry<int>? PortalCapPerPlayer;

        // --- #167/#263 Ward toggle / ward pulse ---
        public static ConfigEntry<bool>? WardIndexEnabled;
        public static ConfigEntry<float>? WardPollSeconds;
        public static ConfigEntry<float>? WardDoubleToggleWindowSeconds;
        public static ConfigEntry<float>? WardUnlockSeconds;
        public static ConfigEntry<float>? WardDefaultRadius;

        // --- #168/#169/#172/#176 Feedback toolkit ---
        public static ConfigEntry<bool>? WorldTextEnabled;
        public static ConfigEntry<bool>? VfxEnabled;

        // --- #170 Server-pushed map pin ---
        public static ConfigEntry<bool>? MapPinFeedbackEnabled;

        // --- #171 Voice / #260 Lights Out / #261 Dream Reel ---
        public static ConfigEntry<bool>? VoiceEnabled;
        public static ConfigEntry<string>? VoiceSpeakerName;
        public static ConfigEntry<bool>? LightsOutEnabled;
        public static ConfigEntry<string>? DreamReelVideoName;

        // --- #173 Status effect indicator / #262 Puppet strings ---
        public static ConfigEntry<bool>? StatusIndicatorEnabled;
        public static ConfigEntry<string>? ArmedStatusEffectName;
        public static ConfigEntry<bool>? PuppetStringsEnabled;

        // --- #174 Forced teleport primitive ---
        public static ConfigEntry<float>? ForcedTeleportCooldownSeconds;

        // --- #175 Global key feedback ---
        public static ConfigEntry<bool>? GlobalKeyFeedbackEnabled;
        public static ConfigEntry<float>? GlobalKeyPollSeconds;
        public static ConfigEntry<float>? GlobalKeyToastRadius;

        // --- #266 Live portal markers ---
        public static ConfigEntry<bool>? LiveMarkersEnabled;
        public static ConfigEntry<float>? LiveMarkersRadius;
        public static ConfigEntry<float>? LiveMarkersReassertSeconds;

        // --- #268 Summoning Gaze (TPA) ---
        public static ConfigEntry<bool>? SummoningGazeEnabled;
        public static ConfigEntry<string>? SummonRequestEmote;
        public static ConfigEntry<string>? SummonAcceptEmote;
        public static ConfigEntry<float>? SummonExpirySeconds;
        public static ConfigEntry<float>? SummonMaxLookRangeMeters;
        public static ConfigEntry<float>? SummonConeDegrees;

        // --- #269 Warp verbs ---
        public static ConfigEntry<bool>? WarpVerbsEnabled;
        public static ConfigEntry<string>? HomeEmote;
        public static ConfigEntry<string>? BackEmote;
        public static ConfigEntry<string>? SpawnEmote;

        // --- #271 Plain chat command line (needs-ingame-check) ---
        public static ConfigEntry<bool>? ChatCommandEnabled;
        public static ConfigEntry<string>? ChatCommandPrefix;

        // --- #272 Cartographer's Table ---
        public static ConfigEntry<bool>? CartographersTableEnabled;
        public static ConfigEntry<float>? CartographersTableRewriteSeconds;

        public static void Bind(ConfigFile config, ConfigSync configSync)
        {
            const string sGeneral = "30 - Ux: General";
            const string sDial = "31 - Ux: Dial";
            const string sLedger = "32 - Ux: Ledger and Signs";
            const string sPing = "33 - Ux: Map Ping";
            const string sGesture = "34 - Ux: Gesture and Posture";
            const string sEmote = "35 - Ux: Emote Input";
            const string sItems = "36 - Ux: Item Selectors";
            const string sPositional = "37 - Ux: Positional and Build-Shape";
            const string sWard = "38 - Ux: Ward";
            const string sFeedback = "39 - Ux: Feedback";
            const string sAdvanced = "40 - Ux: Advanced Schemes";

            Enabled = ConfigBinder.BindSynced(config, configSync, sGeneral, "Enabled", true,
                "Master switch for the whole ux domain. If off, every engine below still registers but no-ops.");
            PortalProximityRadius = ConfigBinder.BindSynced(config, configSync, sGeneral, "PortalProximityRadius", 6f,
                "Default 'standing at a portal' radius (metres) used by gesture/posture/emote input channels that need to bind an action to the nearest portal.", 1f, 30f);

            DialEnabled = ConfigBinder.BindLocal(config, sDial, "Enabled", true,
                "#152/#177 The Dial: parse a player's in-world portal retag as a command line ('#name', '>name', '-', '?').");
            DialSeedHelpTagOnNewPortals = ConfigBinder.BindLocal(config, sDial, "SeedHelpTagOnNewPortals", true,
                "#177 discoverability: a brand-new unnamed/unmanaged portal gets its tag seeded to '?help' so the hover text itself hints at the system.");

            LedgerEnabled = ConfigBinder.BindLocal(config, sLedger, "Enabled", true,
                "#154 The Ledger: adopt ordinary Signs near a named portal and rewrite them into a live paged directory.");
            LedgerRewriteSeconds = ConfigBinder.BindSynced(config, configSync, sLedger, "RewriteSeconds", 10f,
                "How often an adopted sign's directory text is recomputed and rewritten.", 2f, 60f);
            LedgerSignRadius = ConfigBinder.BindSynced(config, configSync, sLedger, "SignAdoptionRadius", 8f,
                "Radius (metres) around a named portal within which ordinary Signs are adopted as directory boards.", 1f, 30f);
            SignInputEnabled = ConfigBinder.BindLocal(config, sLedger, "SignInputEnabled", true,
                "#155 Signs as input: the lowest adopted sign within radius is also read as a 50-character command line.");

            MapPingEnabled = ConfigBinder.BindLocal(config, sPing, "Enabled", true,
                "#156/#270 Map ping destination picker. Capture requires a routed-RPC payload hook this codebase does not yet have (see UxMapPingEngine's own NEEDS NEW HOOK BROKER note) - this toggle only gates the ready-to-call resolution/action side.");
            MapPingClaimRadius = ConfigBinder.BindSynced(config, configSync, sPing, "ClaimRadius", 64f,
                "Max distance (metres) from a ping's x/z to the nearest managed portal for it to be treated as a destination pick.", 5f, 500f);
            MapPingDebounceSeconds = ConfigBinder.BindSynced(config, configSync, sPing, "DebounceSeconds", 5f,
                "Minimum time between two ping-driven dials from the same player.", 0.5f, 60f);
            MapPingRequireArmed = ConfigBinder.BindLocal(config, sPing, "RequireArmed", true,
                "#270's own correction: pings are free, public and spammable - require the player to be armed (crouched near a portal) for a ping to act.");

            GestureTriggerEnabled = ConfigBinder.BindLocal(config, sGesture, "Enabled", true,
                "#157 jump/dodge gesture cycling. Capture requires a routed-RPC payload hook this codebase does not yet have (see UxGestureTriggerEngine's own NEEDS NEW HOOK BROKER note) - this toggle only gates the ready-to-call FSM.");
            GestureWindowSeconds = ConfigBinder.BindSynced(config, configSync, sGesture, "WindowSeconds", 4f,
                "Time window within which consecutive jumps count as one gesture sequence.", 1f, 15f);
            GestureMaxMoveMeters = ConfigBinder.BindSynced(config, configSync, sGesture, "MaxMoveMeters", 2f,
                "Max horizontal movement between jumps for them to still count as 'jumping in place' rather than travel.", 0.2f, 10f);

            ArmingEnabled = ConfigBinder.BindLocal(config, sGesture, "ArmingEnabled", true,
                "#158 posture bits: crouch near a portal arms input channels that would otherwise fire by accident (ping, gesture, equip-item).");
            ArmingAutoDisarmSeconds = ConfigBinder.BindSynced(config, configSync, sGesture, "AutoDisarmSeconds", 15f,
                "An armed state with no confirming action for this long auto-disarms.", 3f, 120f);
            ArmingPollSeconds = ConfigBinder.BindSynced(config, configSync, sGesture, "PollSeconds", 0.2f,
                "How often the crouch/block ZDO bits are polled for every connected character.", 0.05f, 1f);

            EmoteInputEnabled = ConfigBinder.BindLocal(config, sEmote, "Enabled", true,
                "#159 emote signalling near a portal (advance/confirm/cancel/list).");
            EmoteAdvance = ConfigBinder.BindSynced(config, configSync, sEmote, "Advance", "point",
                "Emote (lowercase vanilla name) that advances the destination cursor.");
            EmoteConfirm = ConfigBinder.BindSynced(config, configSync, sEmote, "Confirm", "thumbsup",
                "Emote that confirms/dials the currently-cycled destination.");
            EmoteCancel = ConfigBinder.BindSynced(config, configSync, sEmote, "Cancel", "nonono",
                "Emote that cancels the current cycle with no change.");
            EmoteList = ConfigBinder.BindSynced(config, configSync, sEmote, "List", "wave",
                "Emote that toasts the full destination directory.");

            EquipSelectorEnabled = ConfigBinder.BindLocal(config, sItems, "EquipSelectorEnabled", true,
                "#160 equipped item as destination selector.");
            EquipBindingsFile = ConfigBinder.BindLocal(config, sItems, "EquipBindingsFile", "ux_equip_bindings.json",
                "Path (relative to the plugin folder) to the item-prefab-name -> destination-alias binding table.");
            EquipPollSeconds = ConfigBinder.BindSynced(config, configSync, sItems, "EquipPollSeconds", 0.5f,
                "How often each connected character's visible equipment slots are polled for a change.", 0.1f, 5f);

            ItemStandBoardEnabled = ConfigBinder.BindLocal(config, sItems, "ItemStandBoardEnabled", true,
                "#161 item stand token board near a named portal.");
            ItemStandDiscoveryRadius = ConfigBinder.BindSynced(config, configSync, sItems, "ItemStandDiscoveryRadius", 8f,
                "Radius (metres) around a named portal within which ItemStands are adopted as board slots.", 1f, 30f);
            ItemStandRescanSeconds = ConfigBinder.BindSynced(config, configSync, sItems, "ItemStandRescanSeconds", 8f,
                "How often the item-stand board re-scans for newly built/removed stands.", 2f, 60f);

            ChestTokenEnabled = ConfigBinder.BindLocal(config, sItems, "ChestTokenEnabled", true,
                "#162 chest tokens - item type/stack count as a routing command, with an optional toll.");
            ChestTokenBindingsFile = ConfigBinder.BindLocal(config, sItems, "ChestTokenBindingsFile", "ux_chest_bindings.json",
                "Path (relative to the plugin folder) to the item-name -> destination-alias token table.");
            ChestDiscoveryRadius = ConfigBinder.BindSynced(config, configSync, sItems, "ChestDiscoveryRadius", 8f,
                "Radius (metres) around a named portal within which Containers are adopted as token drop boxes.", 1f, 30f);
            ChestRescanSeconds = ConfigBinder.BindSynced(config, configSync, sItems, "ChestRescanSeconds", 8f,
                "How often the chest-token adoption scan re-runs.", 2f, 60f);

            PositionalInputEnabled = ConfigBinder.BindLocal(config, sPositional, "PositionalInputEnabled", true,
                "#164 positional input - admin-declared plates/sequences/approach vectors.");
            PlatesFile = ConfigBinder.BindLocal(config, sPositional, "PlatesFile", "ux_plates.json",
                "Path (relative to the plugin folder) to the hot-reloaded plate declaration file. Empty/missing file = feature inert.");
            PositionalPollSeconds = ConfigBinder.BindSynced(config, configSync, sPositional, "PollSeconds", 0.15f,
                "How often connected-character positions are sampled against declared plates.", 0.05f, 1f);
            PlateDwellSeconds = ConfigBinder.BindSynced(config, configSync, sPositional, "DwellSeconds", 1.5f,
                "Seconds a player must stay inside a plate's radius before it fires.", 0.2f, 10f);

            BuildShapeEnabled = ConfigBinder.BindLocal(config, sPositional, "BuildShapeEnabled", true,
                "#165 build-shape input - placing/removing a declared piece in a declared socket near a portal.");
            SocketsFile = ConfigBinder.BindLocal(config, sPositional, "SocketsFile", "ux_sockets.json",
                "Path (relative to the plugin folder) to the hot-reloaded socket declaration file. Empty/missing file = feature inert.");
            SocketMatchRadius = ConfigBinder.BindSynced(config, configSync, sPositional, "SocketMatchRadius", 0.75f,
                "Radius (metres) around a socket's declared relative offset within which a matching piece counts as plugged in.", 0.1f, 3f);
            EnforcePortalCap = ConfigBinder.BindSynced(config, configSync, sPositional, "EnforcePortalCap", false,
                "#165's own noted 'same hook' server-enforced per-player portal cap. Off by default - portal-count policy is arguably the access domain's call; this is an optional convenience, not this mod's primary cap mechanism.");
            PortalCapPerPlayer = ConfigBinder.BindSyncedInt(config, configSync, sPositional, "PortalCapPerPlayer", 25,
                "Only used when EnforcePortalCap is on.", 1, 5000);

            WardIndexEnabled = ConfigBinder.BindLocal(config, sWard, "Enabled", true,
                "#167 ward index - discovers PrivateArea (guard stone) ZDOs, exposes ward-derived dial permissions, and treats a double-toggle as a temporary unlock command.");
            WardPollSeconds = ConfigBinder.BindSynced(config, configSync, sWard, "PollSeconds", 0.5f,
                "How often each known ward's s_enabled bit is polled for a double-toggle.", 0.1f, 5f);
            WardDoubleToggleWindowSeconds = ConfigBinder.BindSynced(config, configSync, sWard, "DoubleToggleWindowSeconds", 5f,
                "Two enable/disable flips inside this window count as the 'temporary unlock' command.", 1f, 20f);
            WardUnlockSeconds = ConfigBinder.BindSynced(config, configSync, sWard, "UnlockSeconds", 60f,
                "How long a ward's temporary unlock (via double-toggle) lasts.", 5f, 600f);
            WardDefaultRadius = ConfigBinder.BindSynced(config, configSync, sWard, "DefaultRadius", 10f,
                "PrivateArea.m_radius is Inspector data this mod cannot read remotely - fallback radius used when checking 'is this portal inside a ward'.", 1f, 50f);

            WorldTextEnabled = ConfigBinder.BindLocal(config, sFeedback, "WorldTextEnabled", true,
                "#172 floating world text (RPC_DamageText) feedback layer.");
            VfxEnabled = ConfigBinder.BindLocal(config, sFeedback, "VfxEnabled", true,
                "#176 server-driven VFX/SFX (SpawnObject) feedback layer.");
            MapPinFeedbackEnabled = ConfigBinder.BindLocal(config, sFeedback, "MapPinFeedbackEnabled", true,
                "#170 push a saved, named map pin the first time a player names/dials a portal.");
            VoiceEnabled = ConfigBinder.BindLocal(config, sFeedback, "VoiceEnabled", false,
                "#171 the server 'speaks' consequential events as a fabricated ChatMessage. Off by default - tone risk, opt-in.");
            VoiceSpeakerName = ConfigBinder.BindSynced(config, configSync, sFeedback, "VoiceSpeakerName", "Portal Network",
                "Forged sender name used for #171's voice channel.");
            LightsOutEnabled = ConfigBinder.BindLocal(config, sFeedback, "LightsOutEnabled", true,
                "#260 SleepStart/SleepStop helper, used as an optional 'swap cover' by forced-teleport-driven options.");
            DreamReelVideoName = ConfigBinder.BindLocal(config, sFeedback, "DreamReelVideoName", "",
                "#261 vanilla cinematic name to queue via RPC_SetDreamCinematic. Blank disables the feature (no vanilla video name has been verified against this build's CinematicsManager.m_videos).");

            StatusIndicatorEnabled = ConfigBinder.BindLocal(config, sFeedback, "StatusIndicatorEnabled", false,
                "#173 grant a vanilla status effect as a persistent 'armed' indicator. Off by default - the effect's real gameplay side-effect (see ArmedStatusEffectName) is a genuine trade-off, admin's call.");
            ArmedStatusEffectName = ConfigBinder.BindLocal(config, sFeedback, "ArmedStatusEffectName", "Rested",
                "Vanilla status effect name granted while a player is 'armed'. Must be a real effect name already in ObjectDB.m_StatusEffects on this build - the mod cannot invent a new icon.");
            PuppetStringsEnabled = ConfigBinder.BindLocal(config, sFeedback, "PuppetStringsEnabled", true,
                "#262 server-invoked animator triggers (interact/stagger nod) on a player's own body as a non-textual acknowledgement.");

            ForcedTeleportCooldownSeconds = ConfigBinder.BindSynced(config, configSync, sFeedback, "ForcedTeleportCooldownSeconds", 12f,
                "#174 minimum spacing between two server-forced teleports of the same player - covers the whole ~8s swirl plus vanilla's own 2s cooldown.", 10f, 60f);

            GlobalKeyFeedbackEnabled = ConfigBinder.BindLocal(config, sFeedback, "GlobalKeyFeedbackEnabled", true,
                "#175 toast a player who is near/using a portal while NoPortals/NoBossPortals silently blocks it.");
            GlobalKeyPollSeconds = ConfigBinder.BindSynced(config, configSync, sFeedback, "GlobalKeyPollSeconds", 2f,
                "How often global portal-blocking keys are polled.", 0.5f, 30f);
            GlobalKeyToastRadius = ConfigBinder.BindSynced(config, configSync, sFeedback, "GlobalKeyToastRadius", 8f,
                "Radius (metres) around a portal within which a connected player gets the blocked-key toast.", 1f, 30f);

            LiveMarkersEnabled = ConfigBinder.BindLocal(config, sAdvanced, "LiveMarkersEnabled", false,
                "#266 removable map markers via PersistentEventSystem. Off by default - shares world state with vanilla's own event system; enable deliberately.");
            LiveMarkersRadius = ConfigBinder.BindSynced(config, configSync, sAdvanced, "LiveMarkersRadius", 6f,
                "Marker circle radius (metres) - kept small to minimise side effects on nearby spawners/pieces that react to persistent events.", 1f, 8f);
            LiveMarkersReassertSeconds = ConfigBinder.BindSynced(config, configSync, sAdvanced, "LiveMarkersReassertSeconds", 30f,
                "How often the marker list is re-broadcast, overwriting any client-requested (spoofed) removal.", 5f, 120f);

            SummoningGazeEnabled = ConfigBinder.BindLocal(config, sAdvanced, "SummoningGazeEnabled", true,
                "#268 consent-handshake player-to-player teleport (look + emote request/accept).");
            SummonRequestEmote = ConfigBinder.BindSynced(config, configSync, sAdvanced, "SummonRequestEmote", "wave",
                "Emote that requests a summon toward whoever the requester is looking at.");
            SummonAcceptEmote = ConfigBinder.BindSynced(config, configSync, sAdvanced, "SummonAcceptEmote", "thumbsup",
                "Emote the target performs to accept a pending summon request.");
            SummonExpirySeconds = ConfigBinder.BindSynced(config, configSync, sAdvanced, "SummonExpirySeconds", 30f,
                "How long a summon request waits for acceptance before expiring.", 5f, 120f);
            SummonMaxLookRangeMeters = ConfigBinder.BindSynced(config, configSync, sAdvanced, "SummonMaxLookRangeMeters", 40f,
                "Max distance for a look-ray to resolve a summon target.", 5f, 100f);
            SummonConeDegrees = ConfigBinder.BindSynced(config, configSync, sAdvanced, "SummonConeDegrees", 8f,
                "Angular half-width of the look-ray cone used to resolve a summon target.", 1f, 45f);

            WarpVerbsEnabled = ConfigBinder.BindLocal(config, sAdvanced, "WarpVerbsEnabled", true,
                "#269 /home, /back, /spawn via emotes, no portal required.");
            HomeEmote = ConfigBinder.BindSynced(config, configSync, sAdvanced, "HomeEmote", "bow",
                "Emote that warps the player to their most recently claimed bed.");
            BackEmote = ConfigBinder.BindSynced(config, configSync, sAdvanced, "BackEmote", "shrug",
                "Emote that warps the player back to their position before the last mod-issued warp.");
            SpawnEmote = ConfigBinder.BindSynced(config, configSync, sAdvanced, "SpawnEmote", "cower",
                "Emote that warps the player to the world start location.");

            ChatCommandEnabled = ConfigBinder.BindLocal(config, sAdvanced, "ChatCommandEnabled", false,
                "#271 plain-chat command line. Feasibility is UNVERIFIED (needs-ingame-check per the catalog: a solo vanilla Steam client likely never puts chat text on the wire at all). Off by default; capture is also stubbed pending a routed-RPC payload hook (see UxChatCommandEngine).");
            ChatCommandPrefix = ConfigBinder.BindSynced(config, configSync, sAdvanced, "ChatCommandPrefix", "tp:",
                "Prefix a plain chat message must start with to be treated as a command, once/if the capture hook exists.");

            CartographersTableEnabled = ConfigBinder.BindLocal(config, sAdvanced, "CartographersTableEnabled", true,
                "#272 adopt MapTable pieces near a hub and keep their shared-map-data blob listing every named portal.");
            CartographersTableRewriteSeconds = ConfigBinder.BindSynced(config, configSync, sAdvanced, "CartographersTableRewriteSeconds", 15f,
                "How often an adopted MapTable's s_data blob is recomputed and rewritten.", 5f, 120f);
        }
    }
}
