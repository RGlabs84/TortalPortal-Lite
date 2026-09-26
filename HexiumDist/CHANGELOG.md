# Changelog

## 1.0.8

Corpse-Run Gate (feature 11) hardened on both of its promises: both ends always appear, and neither
end can be destroyed.

### Fixed

- **The bed-side gate was being destroyed within seconds of every death, by this mod and vanilla
  working against each other.** Vanilla's own `Game.ConnectPortals` reconciler tears down any portal
  link whose partner points at `None` (`:100474-100481`), and a one-way gate's grave-side end points at
  `None` *by design* - so within 5 s of every raise vanilla cleared the origin's connection, and the
  phantom factory's generic "connection target is gone, reap the phantom" rule then deleted the
  bed-side portal outright. Both halves are fixed: the ends are minted `TPL_engineowned` so the generic
  sweep leaves them to the engine that owns them, and the one-way link is re-asserted from a
  `Game.ConnectPortals` **postfix** - synchronously, inside vanilla's own call, so the intermediate
  disconnected state is never sent to a client.
- **Only the grave-side end was protected.** The bed-side end was an ordinary phantom, so
  `ZDOMan.ReleaseNearbyZDOS` handed it to whichever player was nearby every 2 s and it took damage like
  any other piece. Both ends are now registered with the new `TargetedPortalProtection`.
- **Nothing stopped a client from deleting a gate.** `ZDOMan.RPC_DestroyZDO` applies whatever ZDOIDs a
  client sends it with no sender check, and `ZDOMan.DestroyZDO` broadcasts a removal to every client
  whenever the server itself owns the ZDO - either one deleted a corpse-run portal outright, with health
  and ownership irrelevant. The mod already had the right primitive (`Core/Hooks/HandleDestroyedZdoHook`)
  but only `AuditEngine` was registered against it, observe-only. Both removal paths are now vetoed for a
  protected portal, and a blocked removal force-sends the ZDO so the client that had already dropped its
  local copy gets the portal back.
- **A death could be dropped silently and never retried.** `RaiseGate` returned without a word if the
  tombstone had gone invalid in the 3 s settle window, and a failed mint logged an error and gave up -
  in both cases after the death had already been recorded as seen, so it was never reconsidered. Raises
  are now retried with backoff, and a death is only marked handled once a gate genuinely stands.
- **A death was only noticed while the player was connected.** Detection now runs off the tombstone
  table instead of the connected-player list, so dying and immediately disconnecting still produces a
  gate - standing and waiting, with the notification deferred until the player is back.
- **Nothing re-built an end that went missing.** The maintenance pass only ever looked at the
  destination, and only when its ZDO was still valid. It now checks both ends every pass and re-mints
  whichever one is gone, at the position it was placed at, re-linking and re-asserting afterwards.
- **`ResolveOrigin` could return `(0,0,0)`** - open ocean on most seeds - for a player with no claimed
  bed when the Start Temple could not be resolved. It now falls back to the player's last known
  position, and says so.
- **Gates expired mid-run.** `TtlMinutes` defaulted to 30, which is shorter than a deep Mistlands or
  Ashlands corpse run. Replaced by `GateTtlMinutes`, default `0` = no limit, so a gate lives exactly as
  long as the grave does. (A new key rather than a new default: BepInEx keeps whatever value an existing
  `.cfg` already holds, so a changed default never reaches a server that has run before.)

### Changed

- **Damage immunity is now argued from where damage is actually arbitrated, and enforced in four
  independent places.** Every damage source in the game - melee, arrows, a troll's AoE swing, an `Aoe`
  component's fire damage-over-time - ends at `WearNTear.Damage(HitData)`, whose entire body is
  `m_nview.InvokeRPC("RPC_Damage", hit)` (`:150275`), and `ZNetView.InvokeRPC` routes to
  `m_zdo.GetOwner()` (`:82901`). `ZRoutedRpc.HandleRoutedRPC` then drops the call outright when
  `ZNetScene.FindInstance` finds no object (`:83692-83711`). A dedicated server pins its reference
  position at `(1e6,0,1e6)` every physics tick (`:100340`) and so never instantiates anything at a real
  player coordinate: a portal ZDO the server owns receives every damage call aimed at it and discards
  100% of them, with no health threshold involved. So the mod (1) holds server ownership against
  `ReleaseNearbyZDOS`, (2) vetoes both removal paths, (3) no-ops every `WearNTear` entry point that can
  lower health or destroy the piece, for the case where a server does hold an instance, and (4) keeps the
  1e9 health and wear-immunity field overrides for the window where a client holds a copy. De-targeting
  (`Piece.m_randomTarget`/`m_primaryTarget`) is retained but is now documented as a courtesy, not a
  defence - it never stopped area damage.
- **A watchdog now counts what it had to fix**, and a status line every 5 minutes reports both ends
  standing plus every damage call, removal and ownership steal the protection refused. "Is anything
  getting through" is an answerable question instead of a guess.
- **`ClearanceMeters` is a preference, scanned in descending tiers, instead of all-or-nothing.** On the
  live server 40 of 40 logged placements came back `Compromise` and not one reached `Clear`: at
  `ClearanceMeters=15` (and at the 10 m default) nowhere in a Valheim forest has that much room from
  every tree, rock and building piece, so every gate fell through to a last-ditch "most open spot" that
  weighs a metre of room against thirty metres of walking - which is how gates ended up 26-40 m from the
  bed they were meant to stand beside, and why the warning it logged every single time carried no signal.
  The search now tries the configured clearance, then progressively smaller requirements, and takes the
  closest spot honouring the best one available, never accepting less than the new `MinRoomMeters` hard
  floor while any spot in range can meet it - widening the search radius (up to 4x, capped at 200 m)
  first. New `Reduced` quality for "met a smaller requirement in full", so a warning again means
  something. Log lines now read as room actually obtained versus room needed.
- **Death-detection latency cut from tens of seconds to a tick or two.** Respawning destroys the
  player's character ZDO and mints a new one (`Game._RequestRespawn` :100278-100293), so a character that
  is replaced or disappears opens a short watch that scans the player's last known position - which is
  where a fresh tombstone is - instead of waiting for the paced background sweep's cursor to come round.
  That sweep's own budget went from 20 to 48 sectors per tick.
- New config in section `41 - Targeted: Corpse Run`: `MinRoomMeters` (default 2), `MaxGraveAgeMinutes`
  (default 0 = no limit), `GateTtlMinutes` (default 0 = no limit, replaces `TtlMinutes`).

### Known gaps

- `Core/Hooks/RpcZdoDataHook`'s leading-ZDOID capture reads the `int` count field that
  `ZDOMan.RPC_ZDOData` writes first (`:77133`), not a ZDOID, so the value it hands its postfix handlers is
  garbage. Nothing in the corpse-run path depends on it; `AuditEngine` is the only consumer. Left alone
  in this release rather than changed blind.
- Older `Subsystems/Foundations/` doc comments still cite 1.0.12 decompile line numbers, now ~32-43
  lines low against 1.0.15/1.0.16.

## 1.0.7

### Changed
- **`1.0.16` added to the verified-build list.** An asmdiff of the 1.0.15 and 1.0.16 server assemblies
  shows every type this mod patches or calls (`Game`, `ZDOMan`, `ZDO`, `ZDOExtraData`, `ZDOVars`,
  `ZRoutedRpc`, `ZRpc`, `ZNetScene`, `ZNetView`, `TeleportWorld`, `ZDOID`, `ZPackage`, `ZNet`,
  `FejdStartup`) is unchanged; of the 19 types that did change, only `TerrainComp` is one this mod has
  any relationship with (`TargetedGroundProbe` reads the `_TerrainCompiler` wire format directly rather
  than calling the class), and the changed members (`Awake`, `ApplyOperation`, `PaintCleared`, `.cctor`)
  are not `Load`/`Save`/`ApplyToHeightmap`, the ones that format depends on. See
  `Subsystems/Foundations/VersionMigration.cs`.
- **Rebuilt against Valheim 1.0.16** (client build 25527674 / dedicated server build 25527701, network
  version 40, unchanged from 1.0.15) and re-verified: a static reference check against both the client
  and dedicated-server assemblies, and a headless 1.0.16 dedicated-server boot.

## 1.0.6

### Fixed
- **A modded client was kicked for not having TortalPortalLite installed — a server-only mod.**
  (`Plugin.cs` `ConfigSync` initializer.) The plugin set `DisplayName`, `CurrentVersion` and
  `MinimumRequiredVersion` on its `ConfigSync` but never `ModRequired`, which ServerSync defaults
  to `true` (`ServerSync.cs:1148`). `GetFailedServer` fails every check where
  `ModRequired && !ValidatedClients.Contains(rpc)` (`ServerSync.cs:1248`), and a client without
  the DLL never validates, so it was disconnected with `ConnectionStatus.ErrorVersion` naming this
  mod. A vanilla client has no ServerSync and sends no version list, so the check never ran and
  the bug stayed invisible on vanilla servers; it only bites a modded client whose pack correctly
  omits the server-side mods. `ModRequired = false` is now set explicitly. `MinimumRequiredVersion`
  stays: not required, but a client that does load it must match versions.

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
