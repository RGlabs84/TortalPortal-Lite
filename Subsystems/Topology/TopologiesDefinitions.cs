using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using UnityEngine;
using TortalPortalLite.Core;

namespace TortalPortalLite.Subsystems.Topology
{
    public sealed class TopologyVec3
    {
        public float X;
        public float Y;
        public float Z;
        public Vector3 ToVector3() => new Vector3(X, Y, Z);

        public static TopologyVec3 Of(Vector3 v) => new TopologyVec3 { X = v.x, Y = v.y, Z = v.z };
    }

    /// <summary>
    /// One member of a declared shape. Exactly one of Target / TargetSelf / TargetAnchor / NoManagedEdge
    /// should describe what this node's Portal connection should be - this single per-node "what do I
    /// point at" field is what lets one engine (TopologiesShapeEngine) uniformly express Simple Pair,
    /// Fan-In Star, Directed Ring, Open Chain (+ every terminator variant), Self-Loop/Decoy, Directed
    /// In-Tree, Portal Bank, Black-Hole Sink and Asymmetric Round-Trip, instead of one bespoke algorithm
    /// per shape "kind".
    /// </summary>
    public sealed class TopologyNode
    {
        public TopologyVec3 Position = new TopologyVec3();

        /// <summary>Facing used only if this node is fabricated by AutoProvisionPortals (degrees, yaw about Y).</summary>
        public float FacingYaw;

        /// <summary>Point this node's connection at whatever portal currently resolves at this position.</summary>
        public TopologyVec3? Target;

        /// <summary>Point this node's connection at itself (Self-Loop Dead End, #7; a chain terminator, #6).</summary>
        public bool TargetSelf;

        /// <summary>Point this node's connection at the owning shape's Anchor (see TopologyShapeDefinition.Anchor).</summary>
        public bool TargetAnchor;

        /// <summary>This engine writes this node's tag but never touches its connection - vanilla's own pass 2, or a separate declared edge, governs it (Portal Bank slots that rely on vanilla auto-pairing).</summary>
        public bool NoManagedEdge;

        /// <summary>
        /// Black-Hole Sink (#18) reversibility: while true, this node's connection is asserted from
        /// TopologiesKeys.SinkPreRepointPos (the position stashed the moment this node was first pointed
        /// at the shape's Anchor - see TopologyShapeDefinition.StashPreAnchorTarget) instead of from
        /// Target/TargetSelf/TargetAnchor above, for as long as the stash resolves to a live portal.
        /// Un-sinks one portal without touching the rest of the sink's membership; the admin flips this
        /// back off (and typically removes the node) once satisfied.
        /// </summary>
        public bool RestoreFromStash;
    }

    /// <summary>
    /// A fabricated non-portal ZDO (Anchor-Terminated One-Way / Dead Drop, #8) a shape can terminate
    /// into. Never a member of ZDOMan's portal registry (TopologiesFabricationEngine deliberately uses a
    /// non-portal prefab hash), so it is invisible to Game.ConnectPortals and can never be torn down or
    /// randomly re-paired - the strongest primitive in the whole catalogue.
    /// </summary>
    public sealed class TopologyAnchorSpec
    {
        public TopologyVec3 Position = new TopologyVec3();
        public float FacingYaw;

        /// <summary>Empty -> TopologiesConfig.DefaultAnchorPrefab.Value.</summary>
        public string PrefabName = "";
    }

    public sealed class TopologyShapeDefinition
    {
        public string Name = "";
        public string Tag = "";
        public List<TopologyNode> Nodes = new List<TopologyNode>();
        public TopologyAnchorSpec? Anchor;

        /// <summary>Portal Bank / Departures Hall (#11) "provisioning": fabricate a real portal ZDO at any Node.Position with no resolvable portal yet, gated globally by TopologiesConfig.AutoProvisionPortals.</summary>
        public bool AutoProvisionPortals;

        /// <summary>Black-Hole Sink (#18) reversibility: before a node is FIRST pointed at Anchor (its previous connection target isn't already the anchor), stash that previous target's current world position into TopologiesKeys.SinkPreRepointPos so TopologyNode.RestoreFromStash can undo it later without needing the tag-scramble stash mechanism.</summary>
        public bool StashPreAnchorTarget;
    }

    public sealed class TopologyDestination
    {
        public string Name = "";
        public TopologyVec3 Position = new TopologyVec3();
    }

    /// <summary>Switchboard (#12): one hub, many candidate destinations, all wired as a Fan-In Star; the hub's own outbound edge is player-selected, runtime-mutable state (not a static declaration), so TopologiesSwitchboardEngine owns writing it directly.</summary>
    public sealed class TopologySwitchboardDefinition
    {
        public string Name = "";
        public string Tag = "";
        public TopologyVec3 Hub = new TopologyVec3();
        public List<TopologyDestination> Destinations = new List<TopologyDestination>();
    }

    /// <summary>Carousel (#13): identical inbound wiring to a Switchboard, but the outbound edge rotates on a server timer instead of on player input.</summary>
    public sealed class TopologyCarouselDefinition
    {
        public string Name = "";
        public string Tag = "";
        public TopologyVec3 Hub = new TopologyVec3();
        public List<TopologyDestination> Destinations = new List<TopologyDestination>();
        public float PeriodSeconds = 600f;

        /// <summary>Optional: a Sign ZDO position (ZDOVars.s_text) this engine keeps updated with the current destination's Name, so the advertised destination is honest for practically the whole period.</summary>
        public TopologyVec3? SignPosition;
    }

    /// <summary>Nested / Layered Airlock (#10): an outer Fan-In Star (many outer portals -> one vestibule arrival portal -> an Anchor) composed with an inner Portal Bank on its own, disjoint tag namespace per wing.</summary>
    public sealed class TopologyVestibuleWing
    {
        public string Tag = "";
        public TopologyVec3 InsideSlot = new TopologyVec3();
        public TopologyVec3 OutsideSlot = new TopologyVec3();
    }

    public sealed class TopologyVestibuleDefinition
    {
        public string Name = "";
        public string OuterTag = "";
        public List<TopologyVec3> OuterPortals = new List<TopologyVec3>();
        public TopologyVec3 VestibulePortal = new TopologyVec3();
        public TopologyVec3 VestibuleAnchor = new TopologyVec3();
        public List<TopologyVestibuleWing> Wings = new List<TopologyVestibuleWing>();

        /// <summary>Admin-declared closed wings, by TopologyVestibuleWing.Tag - edit this list and the file's mtime changes, same hot-reload poll as everything else.</summary>
        public List<string> ClosedWingTags = new List<string>();

        /// <summary>Where a closed wing's inside slot is repointed instead of its outside partner (a jail/holding cell inside the vestibule, distinct from VestibuleAnchor which anchors the OUTER star).</summary>
        public TopologyVec3? ClosedWingAnchor;
    }

    public sealed class TopologyRouletteGroup
    {
        public string Tag = "";
    }

    /// <summary>
    /// Displaced Routing (#20): one portal's connection, owned entirely by this admin-declared entry
    /// (and mirrored onto TopologiesKeys.DisplacedRoute on the ZDO itself, per the catalog's own
    /// mechanism) rather than by shared-tag equality. DisplayTag is a pure label - RPC_SetTag itself
    /// applies no length cap server-side (SERVER decompile :143587-143602; only the client's TextInput
    /// widget caps a PLAYER's own edit at 10 chars, :143482) - so this can be longer than
    /// TagCodec.MaxTagLength, unlike every tag this mod writes anywhere else.
    /// </summary>
    public sealed class TopologyDisplacedRoute
    {
        public TopologyVec3 From = new TopologyVec3();
        public string DisplayTag = "";
        public TopologyVec3? To;
        public bool ToSelf;
    }

    public sealed class TopologyLockdownConfig
    {
        /// <summary>Empty -> lock down every portal currently in PortalCensus. Non-empty -> only these positions.</summary>
        public List<TopologyVec3> Portals = new List<TopologyVec3>();
    }

    public sealed class TopologyFactionsConfig
    {
        public bool Enabled;

        /// <summary>faction id (short code, folded into the invisible tag suffix - keep it very short, see TagCodec-style 10-char budget math in TopologiesTagShardEngine) -> member player ids (ZDOVars.s_playerID).</summary>
        public Dictionary<string, List<long>> Rosters = new Dictionary<string, List<long>>();
    }

    public sealed class TopologyPrivateNetworksConfig
    {
        public bool Enabled;
    }

    public sealed class TopologiesFile
    {
        public int SchemaVersion = 1;
        public List<TopologyShapeDefinition> Shapes = new List<TopologyShapeDefinition>();
        public List<TopologySwitchboardDefinition> Switchboards = new List<TopologySwitchboardDefinition>();
        public List<TopologyCarouselDefinition> Carousels = new List<TopologyCarouselDefinition>();
        public List<TopologyVestibuleDefinition> Vestibules = new List<TopologyVestibuleDefinition>();
        public List<TopologyRouletteGroup> RouletteGroups = new List<TopologyRouletteGroup>();
        public List<TopologyDisplacedRoute> DisplacedRoutes = new List<TopologyDisplacedRoute>();
        public TopologyLockdownConfig Lockdown = new TopologyLockdownConfig();
        public TopologyFactionsConfig Factions = new TopologyFactionsConfig();
        public TopologyPrivateNetworksConfig PrivateNetworks = new TopologyPrivateNetworksConfig();
    }

    /// <summary>
    /// Hot-reloaded admin-facing declaration file for every Topologies engine, deliberately its OWN file
    /// (topologies.json, default) rather than reusing Foundations' networks.json - two reasons: (1) that
    /// file's schema (NetworkDefinition: name+tag+member positions, reassert-as-a-ring only) cannot
    /// express a star/tree/chain-terminator/switchboard/carousel/vestibule/lockdown/sink, all of which
    /// need either non-ring adjacency, a fabricated non-portal Anchor, a runtime-mutable edge, or admin
    /// control knobs networks.json's schema has no field for; and (2) three agents are editing this
    /// codebase concurrently and a shared runtime file is exactly the kind of collision surface the
    /// orchestrator's file-ownership split (see this wave's own task boundaries) is designed to avoid -
    /// this file lives entirely inside this domain's own config surface (TopologiesConfig.ShapesFile).
    ///
    /// Same poll-based hot-reload shape as NetworkModel.cs (BepInEx has no file watcher) but on its own
    /// independent timer, so the two files reload independently of each other.
    /// </summary>
    public static class TopologiesDefinitions
    {
        private const float PollInterval = 5f;
        private static float _timer;
        private static DateTime _fileStamp = DateTime.MinValue;
        private static TopologiesFile _current = new TopologiesFile();

        public static TopologiesFile Current => _current;

        private static string FilePath =>
            Path.Combine(Path.GetDirectoryName(TopologiesConfig.ShapesFile?.ConfigFile?.ConfigFilePath ?? "") ?? ".",
                TopologiesConfig.ShapesFile?.Value ?? "topologies.json");

        public static void OnUpdate(float dt)
        {
            _timer += dt;
            if (_timer < PollInterval)
            {
                return;
            }
            _timer = 0f;
            TryReload();
        }

        private static void TryReload()
        {
            try
            {
                string path = FilePath;
                if (!File.Exists(path))
                {
                    return;
                }
                DateTime stamp = File.GetLastWriteTimeUtc(path);
                if (stamp == _fileStamp)
                {
                    return;
                }

                string json = File.ReadAllText(path);
                var parsed = JsonConvert.DeserializeObject<TopologiesFile>(json);
                if (parsed == null)
                {
                    PortalDebug.LogWarning($"[TopologiesDefinitions] '{path}' parsed to null - ignoring.");
                    return;
                }

                _current = parsed;
                _fileStamp = stamp;
                PortalDebug.LogAlways($"[TopologiesDefinitions] loaded {_current.Shapes.Count} shape(s), {_current.Switchboards.Count} switchboard(s), {_current.Carousels.Count} carousel(s), {_current.Vestibules.Count} vestibule(s) from '{path}'.");
            }
            catch (Exception ex)
            {
                PortalDebug.LogError($"[TopologiesDefinitions] failed to load '{TopologiesConfig.ShapesFile?.Value}': {ex.GetType().Name}: {ex.Message}");
            }
        }
    }
}
