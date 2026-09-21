# Changelog

## 1.0.5

### Fixed
- **Automatic repair never ran on Valheim 1.0.15 — and said so every five minutes.** `1.0.15` was
  outside the verified-build list, so with `AutoRepair = true` every scheduled `--apply` pass was
  refused by the Version Migration gate and the identical refusal line was logged each interval
  (230 times on one server). `1.0.15` is now verified: a per-class diff of the decompiled 1.0.12 and
  1.0.15 server assemblies shows the entire portal write path (`Game`, `ZDOMan`, `ZDO`,
  `ZDOExtraData`, `ZDOVars`, `ZRoutedRpc`, `ZNetScene`, `TeleportWorld`) is byte-identical, and the
  classes that did change (`Terminal`, `ZNet`, `TerrainComp`, `Inventory`, `Piece`, `Player`,
  `Character`) changed outside every member this mod patches or calls.

### Changed
- **The automatic repair pass now idles quietly while the destructive gate is closed.** It logs one
  warning explaining why (and what to do about it) the first time a scheduled `--apply` pass would
  be refused, skips silently after that, and logs a "resumed" line the moment the gate opens —
  whether by `AcceptUnverifiedBuild` hot-reloading, `DryRunDefault` flipping to report-only, or a
  future build being verified. The console/RemoteCommand `repair --apply` verb still refuses with
  its full message every time it's asked.

## 1.0.4

### Fixed
- **Corpse-run gates no longer spawn inside rocks, trees or buildings.** The 1.0.3 scanner measured
  clearance 1.3 m from each object's ZDO *pivot*, so a black-forest boulder whose pivot sat 4 m away
  but whose mesh spanned 8 m passed as "clear" and gates were planted inside rock walls. Every
  obstacle is now a disc sized from the prefab's real collider extents (read off the prefab asset
  and scaled by the object's stored scale), generated locations count with their exterior radius
  (their static geometry has no ZDOs), and clearance is measured edge-to-edge from the portal
  frame. Creatures, items, projectiles and the tombstone itself are not obstacles.
- **Gates sit on the real ground.** Height now comes from a headless re-run of the client's own
  heightmap build — `HeightmapBuilder.Build`'s corner-biome blending plus the zone's
  `_TerrainCompiler` terraforming deltas — instead of `WorldGenerator.GetHeight`, which returns the
  un-blended single-biome height and knows nothing about hoe/pickaxe work; at biome borders and on
  levelled bases that was metres off. The pivot, the frame's four base corners and a 2 m ring of
  surroundings must all be dry, lava-free, level and contiguous.
- **A death inside a dungeon** re-anchors the grave-side gate to the dungeon's exterior entrance
  (same 64 m zone as the interior) instead of inside a room wall; the player is told.
- **Stray gates after a restart** — gate bookkeeping lived only in memory while the portal ZDOs
  persist, so every restart with an unrecovered tombstone left the old pair standing and raised a
  second. Any corpse-run portal the running server did not raise is now reaped.
- **Both ends of one gate can no longer share a clearing** when a player dies beside their own bed.
- A new tombstone now gets 3 s to stop falling/sliding before its position is used.

### Added
- `ClearanceMeters` (section `41 - Targeted: Corpse Run`, default `10`, range `1`–`30`): edge-to-edge
  room every gate (both ends) must have from every tree, rock, building piece, portal and location.
  Sub-metre props (mushrooms, flowers, berry bushes) only need 2 m. When nothing inside the search
  radius honours it, the most open dry spot found is used and the server log says so — with
  distance, nearest obstacle and shortfall — so you can widen the search.
- `SearchRadiusMeters` (same section, default `40`, range `5`–`150`) **replaces** 1.0.3's
  `MaxOffsetMeters` (default 8, which could never satisfy a 10 m clearance; the rename stops a saved
  `MaxOffsetMeters = 8` from silently capping the new search). The scan is rings outward, so the gate
  still lands as close to the grave as the clearance allows; the search also auto-extends past any
  obstacle that covers the anchor itself (a death inside the start temple or a ruin).
- One `[CorpseRun]` log line per gate end on every death: quality (`Clear` / `Compromise` /
  `WaterSurface` / `LastResort`), position, distance, slope, margin vs nearest obstacle, the
  obstacle census, and the anchor's height above the modelled ground as a sanity readout.

### Changed
- `28 - Targeted: Bed` / `OffsetMeters` and `41` / `OffsetMeters` are now the *minimum* distance
  from the bed / tombstone (ranges widened to 1–20); the maximum is `SearchRadiusMeters`.
- Bed-side gates no longer accept underwater ground; an over-water bed gets a shoreline spot or a
  surface-floating gate like any ocean death.
- The death toast reads "near your bed" / "at the world spawn" and mentions when the grave gate
  exits at a dungeon entrance.

## 1.0.3

### Added
- **Configurable Corpse-Run Offset (`CorpseRunMaxOffsetMeters`).** Added `CorpseRunMaxOffsetMeters`
  (default `8.0`, range `3.0`–`25.0` meters) to config section `41 - Targeted: Corpse Run`, governing
  the outer radius searched when seeking unobstructed ground for the recovery gate.

### Improved
- **Intelligent Corpse-Run Gate Placement.** Replaced simple fixed-offset positioning with an
  adaptive concentric-ring clearance algorithm adapted from Wonderland's starting grant placement:
  - Concentric scanning rings (2.5m, 4.0m, 6.0m, up to `CorpseRunMaxOffsetMeters`) testing angular
    candidate points around the tombstone or player bed.
  - Multi-directional slope and clearance checks rejecting steep drop-offs, cliffs, and walls.
  - Proximity collision checks against trees, rocks, buildings, and existing structures via
    `ZdoSpatialQuery.FindNear` ensuring a 2.5m clear buffer.
  - Shoreline and water handling ensuring the portal never spawns submerged under water while
    gracefully accommodating coastal beach ground above the waterline.
  - Interior dungeon detection: preserves elevated interior elevations (`y > 1000m`) without
    snapping down to exterior world heightmap levels.
  - Automatic yaw alignment orienting the destination portal to face directly toward the tombstone
    upon arrival.

### Changed
- **Indestructible Tombstone Recovery Portal.** Prevented monsters and world damage from destroying
  the corpse-run destination portal at the grave before the player can retrieve their gear:
  - Mob AI de-targeting: sets Piece `m_randomTarget = false` and `m_primaryTarget = false` so mobs
    ignore the portal and do not agro or attack it.
  - Full client damage immunity: configures `WearNTear` fields on the portal ZDO with immune damage
    modifiers across all physical and elemental damage types, disables structural support wear, and
    sets health to 1,000,000,000 HP.
  - Server ownership pinning: patches `ZDO.SetOwner` and `ZDO.SetOwnerInternal` to keep the destination
    portal pinned to the dedicated server (ZDO owner 0L). Because Valheim clients only execute and
    broadcast damage to objects they own, unowned server pieces drop client-side monster attack damage.
  - Autonomous health watchdog: a 2-second background check ensures any transient damage is
    immediately healed until the grave is claimed or the TTL expires.

## 1.0.2

### Fixed
- **Version Migration falsely flagged verified builds as unverified.** The running-build check
  compared `Version.GetVersionString()` — which carries a platform prefix on some builds (`l-1.0.12`
  on Steam Linux, `dw-`/`dl-` on Deck, `ms-` on Microsoft Store) — against the bare `1.0.12` in the
  verified list, so it never matched. Now compares `Version.CurrentVersion` (bare major.minor.patch);
  the prefixed string is still logged for humans.
- **Version Migration always reported the portal-prefab registry as empty.** The
  `Game.instance.PortalPrefabHash` check ran during the plugin's own Awake, before the game's `Game`
  object exists — so it always failed, and `repair --apply` was permanently refused regardless of
  `AcceptUnverifiedBuild`. The check now runs once on the first tick `Game.instance` exists (which
  `Game.Awake` populates synchronously), logs the verified prefab count, and only errors on a
  genuinely empty registry.

## 1.0.1

### Added
- **BarrkBOT Portal Export.** Writes `barrkbot_portals.json` — per-player portal counts, cap/over-cap
  flags, and world totals — atomically to `BepInEx/config/TortalPortalLite/`, on a floor of once
  every 60 seconds, built directly against BarrkBOT's own published ingestion contract (no HTTP, no
  webhook, no auth — file-only; BarrkBOT reads, it never writes). Force an immediate rewrite with
  `removekey tpl barrkbot`. Config section `84 - Ops: BarrkBot Export` (`Enabled`, `IntervalSeconds`).

## 1.0.0

### Added
- **Phantom Anchor Fabrication.** An admin declares a named "phantom anchor" — a destination point
  with no real player-built portal behind it — in `routing.json`'s `phantomAnchors` section (fields:
  `Name`, `Position` `{X,Y,Z}`, `RotationY`, `Void`). `Void:true` makes it invisible and non-portal;
  `Void:false` makes it a real, visible portal anchor. The server creates and maintains it
  automatically, so Sealed Gates and Parked Terminals have somewhere to point that nobody has to
  physically build.
- **Progression-Gated Sealed Gate.** A portal that resolves to nothing until a named global key (a
  boss defeat, or a custom flag) becomes true, then opens to its declared destination. Declared in
  `routing.json`'s `sealedGates` section (`Name`, `Position`, `GlobalKey`, `Destination`,
  `SealedTag`, `OpenTag`). Config section `11 - Routing: Schedules and Conditions`, key
  `SealedGateEvalSeconds` controls how often the global key is re-checked (default 1s).
- **HealthScan.** A background pass (config section `4 - Foundations: Health`,
  `HealthIntervalSeconds`, default 30s) that flags odd-count same-tag portal groups — which always
  leave one permanently orphaned by vanilla's own pairwise-only pairing — and stale dangling
  connections. It deliberately does not flag a portal whose one-way connection is intentional, like
  the Corpse-Run Gate below; those are a designed feature, not a defect, and are excluded from this
  report.
- **Repair.** Turns HealthScan's findings into safe, bounded, reversible fixes — retag an orphaned
  portal, clear a dangling link. Dry-run by default (config section `5 - Foundations: Repair`,
  `RepairDryRunDefault`), reporting what it would do; run `removekey tpl repair --apply` to actually
  write the fixes. It automatically snapshots the world's portal state first, so `restore` can
  always undo the whole batch.
- **Metrics.** Portal counts (total/connected/managed), per-tag/per-biome/per-builder breakdowns,
  and a position-based heuristic for transit counts and "top routes," read via
  `removekey tpl metrics`. Config section `7 - Foundations: Metrics`.
- **Version Migration.** A boot-time safety check: is this a Valheim build the mod has actually been
  verified against, and does the game's own portal-prefab registry look intact. If either check
  fails, destructive passes (`repair --apply`) refuse to run rather than risk corrupting the world,
  unless an admin explicitly opts in via `GlobalConfig.AcceptUnverifiedBuild`.
- **Compat.** `removekey tpl compat` reports which Harmony patches exist on the handful of vanilla
  methods this mod also touches, so "my portals keep unpairing" support questions have one command's
  worth of answer. It also runs an explicit, named check against two specific sibling mods this
  server is expected to run alongside — Wonderland and GetOffMyLawn. Both were read directly: both
  patch `ZDO.SetOwner`, but both vetoes are scoped only to their own tracked objects (Wonderland's
  buoyant water items, GetOffMyLawn's ward-held building pieces) and never fire on a portal ZDO —
  confirmed no interference.
- **Architecture.** Not a player-facing feature — this is the subsystem/registry/tick-order design
  the whole mod is built on: Census first, then Audit, then HealthScan, and so on, each with disjoint
  write ownership over the portal data it touches.
- **The Ore Gate.** A per-portal upgrade, declared in `economy.json`'s `oreGates` section (`Portal`
  position, `AllowAllItems` bool, default `true`, optional `ExitDistance` float) that lets
  ore/metal actually pass through that specific gate and/or extends how far in front of it a
  traveller steps out. A real, vanilla-client-honoured gameplay perk written directly onto that one
  portal's data, not a global setting.
- **Server-Enforced Portal Caps.** An optional per-player portal limit (config section
  `155 - WildcardB: Portal Caps`, `PortalCapEnabled`, off by default, `PortalCapPerCreator`, default
  6). Once a player's live portal count exceeds the cap, their newest excess portals are destroyed
  automatically and they're toasted about it — genuinely unbypassable by an unmodified client,
  unlike a client-side placement limit.
- **Corpse-Run Gate.** On a tracked player's death, the server automatically raises a private,
  temporary portal near their claimed bed (or the world spawn if they have none) leading straight to
  their tombstone, so gear recovery doesn't mean a bare-handed run back into danger. This is a
  genuine one-way trip by design, not a round-trip pair — there is no automatic portal back from the
  grave to the bed. The gate self-destroys once the grave is emptied/despawned or after a
  configurable TTL (config section `41 - Targeted: Corpse Run`, `CorpseRunTtlMinutes`, default 30).
- **DestinationPrewarm.** An always-on safety net: the instant any managed portal's destination
  changes, nearby players' clients are immediately force-sent the new destination data, so a
  re-point is never followed by someone walking through into a "dead" transit because their client
  hadn't received the update yet. Config section `16 - Routing: Delivery Pipeline`, `PrewarmRadius`,
  default 30m.
- **Fast-Transit Mode (and Parked Terminal).** A managed portal kept permanently "unconnected" on
  purpose; instead of the normal portal-connection walk-through, the server itself detects a player
  stepping up to it and teleports them directly. Fast-Transit is the quicker variant (roughly 2-4
  second fade instead of vanilla's roughly 8 second swirl), used once the destination zone is
  confirmed already generated and safe to land in — otherwise it automatically falls back to the
  normal, slower transit rather than risking a bad landing. Declared in `routing.json`'s
  `parkedTerminals` section (`Distant:false` = Fast-Transit, `Distant:true` = the plain Parked
  Terminal base mechanism).
- **Live Portal Markers.** Any portal named with a leading `#` in vanilla's own rename box gets a
  removable, labelled marker on every connected player's map, via vanilla's own persistent-event
  system — the marker disappears the moment the portal is renamed away from it. Off by default
  (`LiveMarkersEnabled`, config section `40 - Ux: Advanced Schemes`).

### Notes
- This release is the result of narrowing a much larger internal research catalog — 263 candidate
  portal mechanisms explored during R&D — down to exactly the 14 features above for the shipped
  product. Everything else was removed from the codebase entirely, not merely disabled.
