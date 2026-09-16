# Changelog

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
