# TortalPortal Lite — Admin & Configuration Reference

**Mod:** TortalPortal Lite (GUID `wubarrk.tortalportallite`)
**Version:** 1.0.0
**Install footprint:** strictly server-side. Every feature below runs entirely on the dedicated server against raw ZDO data — there is nothing for players to install, ever, and Steam/Xbox/PlayFab/crossplay clients all see the identical result.

This document replaces the older `docs/FEATURE-STATUS.md` and `docs/WORKING-FEATURES.md`, which described a much larger, 263-option research catalog explored during the mod's R&D pass. That catalog has since been narrowed down to the 14 features actually shipped in this release (down from 296 files/~44.8k lines to 83 files/~10k lines); the old documents are archived under `docs/research-archive/` for history and no longer describe the shipped mod.

## Configuration basics

All settings live in `BepInEx/config/wubarrk.tortalportallite.cfg`, split into numbered sections (e.g. `4 - Foundations: Health`). Every entry carries its own description and valid range directly in the file. The config file is polled every 5 seconds — editing a value takes effect live, with **no server restart required**. Most settings are server-synced to connected clients (ServerSync) the moment they change; a small number are local-only (mainly file paths), since those are specific to the machine the server runs on and can't be meaningfully synced.

Declared game objects (anchors, gates, terminals, ore-gate perks) are not config settings — they live in the mod's own declaration files, `routing.json` and `economy.json`, described per-feature below.

---

## 1. Phantom Anchor Fabrication

**What it does:** Lets an admin declare a named "phantom anchor" — a destination point that has no real player-built portal standing behind it. The server creates and maintains this anchor automatically, giving other features (Sealed Gates, Parked Terminals) somewhere to point that nobody has to physically build.

**Config:** None — phantom anchors are declared directly in the declaration file, not the `.cfg`.

**Declaration file:** `routing.json`, section `phantomAnchors`. Fields per entry:
- `Name` — the anchor's identifier
- `Position` — `{X, Y, Z}` world coordinates
- `RotationY` — the anchor's yaw
- `Void` — `true` makes the anchor invisible and non-portal (a pure destination point); `false` makes it a real, visible portal anchor

---

## 2. Progression-Gated Sealed Gate

**What it does:** A portal that resolves to nothing until a named global key (a boss defeat, or a custom flag) becomes `true`. Once that key flips, the gate opens and resolves to its declared destination.

**Config:** Section `11 - Routing: Schedules and Conditions`
- `SealedGateEvalSeconds` — how often the gate re-checks its global key. Default: `1` (second)

**Declaration file:** `routing.json`, section `sealedGates`. Fields per entry:
- `Name`
- `Position`
- `GlobalKey` — the global key that must become true to open the gate
- `Destination` — where the gate resolves to once open
- `SealedTag` — the tag applied while the gate is closed
- `OpenTag` — the tag applied once the gate opens

---

## 3. HealthScan

**What it does:** A background pass that flags two kinds of portal problems: odd-count same-tag portal groups (vanilla only pairs portals two at a time, so any odd group always leaves one portal permanently orphaned), and stale dangling connections. HealthScan deliberately does **not** flag a portal whose one-way connection is intentional (e.g. the Corpse-Run Gate, feature 11) — those are a designed feature, not a defect, and are excluded from the report.

**Config:** Section `4 - Foundations: Health`
- `HealthIntervalSeconds` — how often the health pass runs. Default: `30` (seconds)

**Declaration file:** None — HealthScan inspects live portal ZDOs, it does not read a declaration file.

---

## 4. Repair

**What it does:** Turns HealthScan's findings into safe, bounded, reversible fixes — retagging an orphaned portal, clearing a dangling link. Repair is dry-run by default, meaning it only reports what it *would* do. Run `removekey tpl repair --apply` to actually write the fixes. Before applying anything, Repair automatically snapshots the world's portal state so `restore` can always undo the whole batch afterward.

**Config:** Section `5 - Foundations: Repair`
- `RepairDryRunDefault` — whether Repair defaults to dry-run (report-only) mode. Default: `true`

**Declaration file:** None.

---

## 5. Metrics

**What it does:** Tracks portal counts (total / connected / managed), breakdowns per tag, per biome, and per builder, and a position-based heuristic estimating transit counts and "top routes." Read live via `removekey tpl metrics`.

**Config:** Section `7 - Foundations: Metrics` (governs what Metrics tracks; see the `.cfg` file itself for the individual entries in this section).

**Declaration file:** None.

---

## 6. Version Migration

**What it does:** A boot-time safety check that verifies (a) this is a Valheim build the mod has actually been verified against, and (b) the game's own portal-prefab registry looks intact. If either check fails, destructive passes (`repair --apply`) refuse to run, to avoid risking world corruption — unless an admin explicitly opts in.

**Config:** `GlobalConfig`
- `AcceptUnverifiedBuild` — explicit admin opt-in to allow destructive passes to run even when the boot-time checks fail. Off by default; must be explicitly set to allow running against an unverified build.

**Declaration file:** None.

---

## 7. Compat

**What it does:** Reports which Harmony patches exist on the handful of vanilla methods this mod also touches (so "my portals keep unpairing"-type support questions have one command's worth of answer), via `removekey tpl compat`. It also runs an explicit, named check against two specific sibling mods this server is expected to run alongside: **Wonderland** and **GetOffMyLawn**. Both were read directly — both patch `ZDO.SetOwner`, but each veto is scoped only to that mod's own tracked objects (Wonderland's buoyant water items; GetOffMyLawn's ward-held building pieces) and neither ever fires on a portal ZDO. Confirmed: no interference.

**Config:** None.

**Declaration file:** None.

---

## 8. Architecture

Mostly not player-facing — this is the subsystem/registry/tick-order design the whole mod is built on: Census runs first, then Audit, then HealthScan, and so on, with each subsystem holding disjoint write ownership over portal data. It does own two real, admin-facing things, though: the mod's single master on/off switch, and a periodic alive-check.

**Config:** Section `1 - General`
- `Enabled` — the master switch for the entire mod. If `false`, every subsystem above still registers but no-ops. Default: `true`
- `ServerConfigLocked` — if `true` (default), a connecting client's own copy of synced settings is locked to the server's values and cannot diverge.
- `VerboseLogging` — extra diagnostic log detail. Default: `false`
- `AcceptUnverifiedBuild` — see feature 6, Version Migration.
- `HeartbeatEnabled` / `HeartbeatIntervalMinutes` — a periodic one-line server-log summary (uptime, who's online) so an admin tailing the log can confirm the mod is alive without full verbose logging. Default: enabled, every 15 minutes.

Section `15 - Discord`
- `DiscordWebhookUrl` — optional webhook URL. Blank (default) means no Discord activity at all.
- `DiscordNotifyHeartbeat` — if `true`, the same periodic summary above is also posted to the configured webhook. Default: `false`. A basic online/offline lifecycle notice is also posted (if a webhook URL is set) when the server starts and stops.

**Declaration file:** None.

---

## 9. The Ore Gate

**What it does:** A per-portal upgrade that lets ore/metal actually pass through that specific portal (vanilla normally blocks ore from being carried through portals) and/or extends how far in front of the portal a traveller steps out on arrival. This is a real, vanilla-client-honoured gameplay perk written directly onto that one portal's data — not a global setting, so it can be granted portal-by-portal.

**Config:** None — set per portal in the declaration file.

**Declaration file:** `economy.json`, section `oreGates`. Fields per entry:
- `Portal` — the position of the portal this upgrade applies to
- `AllowAllItems` — whether ore/metal is allowed through this gate. Default: `true`
- `ExitDistance` — optional float; extends how far in front of the portal an arriving traveller steps out

---

## 10. Server-Enforced Portal Caps

**What it does:** An optional per-player limit on live portal count. Once a player's portal count exceeds the cap, their newest excess portals are destroyed automatically and they're toasted about it. Because this runs server-side against raw ZDO data, it's genuinely unbypassable by an unmodified client — unlike a client-side placement limit.

**Config:** Section `155 - WildcardB: Portal Caps`
- `PortalCapEnabled` — turns the cap on or off. Default: `false` (off)
- `PortalCapPerCreator` — the maximum number of live portals allowed per player. Default: `6`

**Declaration file:** None.

---

## 11. Corpse-Run Gate

**What it does:** On a tracked player's death, the server automatically raises a private, temporary portal near the player's claimed bed (or world spawn if they have none), leading straight to their tombstone — so gear recovery doesn't require a bare-handed run back into danger. This is a genuine **one-way** trip by design: there is no automatic portal back from the grave to the bed. The gate self-destroys once the grave is emptied/despawned, or after a configurable TTL.

**Config:** Section `41 - Targeted: Corpse Run`
- `CorpseRunTtlMinutes` — how long the gate persists before self-destroying if the grave hasn't already been emptied/despawned. Default: `30`

**Declaration file:** None — these gates are created automatically by the server on death, not declared ahead of time.

---

## 12. DestinationPrewarm

**What it does:** An always-on safety net. The instant any managed portal's destination changes, nearby players' clients are immediately force-sent the new destination data, so a re-point of a portal is never followed by someone walking through into a "dead" transit because their client hadn't yet received the update.

**Config:** Section `16 - Routing: Delivery Pipeline`
- `PrewarmRadius` — the radius around a changed portal within which players are force-sent the new destination data. Default: `30` (meters)

**Declaration file:** None.

---

## 13. Fast-Transit Mode (and Parked Terminal)

**What it does:** A managed portal that is kept permanently "unconnected" on purpose. Instead of the normal portal-connection walk-through, the server itself detects a player stepping up to the portal and teleports them directly. **Fast-Transit** is the quicker variant (a roughly 2–4 second fade, versus vanilla's ~8 second swirl), used once the destination zone is confirmed already generated and safe to land in; otherwise it automatically falls back to the normal, slower transit rather than risk a bad landing. **Parked Terminal** is the base mechanism underlying both variants.

**Config:** None.

**Declaration file:** `routing.json`, section `parkedTerminals`. Field:
- `Distant` — `false` selects Fast-Transit (quick fade); `true` selects the plain Parked Terminal base mechanism (normal-speed transit)

---

## 14. Live Portal Markers

**What it does:** Gives named portals a removable, labelled marker on every connected player's map. Any portal whose tag starts with `#` (typed directly into that portal's own vanilla rename box — no client mod needed) gets one synthetic map-event entry injected via vanilla's own persistent-event system, pointing at the blandest available event type (no weather override, no graphical effect, nothing spawned) so it's visually inert beyond the marker itself. The marker is removed the moment the portal is renamed away from a `#`-prefixed tag. This is the only marker channel in the mod that's actually removable — feedback elsewhere in vanilla's own systems, once placed, stays until a player manually clears it.

**Config:** Section `30 - Ux: General`
- `Enabled` — master switch for this feature's subsystem. Default: `true`

Section `40 - Ux: Advanced Schemes`
- `LiveMarkersEnabled` — turns the feature itself on. Off by default — it shares world state with vanilla's own event system, so it's opt-in. Default: `false`
- `LiveMarkersRadius` — marker circle radius in meters. Kept small by default to limit side effects on nearby spawners/pieces that react to persistent events. Default: `6`
- `LiveMarkersReassertSeconds` — how often the marker list is re-broadcast (overwrites any client-requested/spoofed removal, since vanilla's own start/stop event RPCs have no sender check). Default: `30`

**Declaration file:** None — driven entirely by which portals currently carry a `#`-prefixed tag.

---

## Admin console

Every command is typed as `removekey tpl <verb> [args]` in an admin's own in-game console (F5) or chat — this piggybacks on vanilla's own remote-command channel, so no client mod is needed.

| Verb | Description |
|---|---|
| `list` | Lists the portals/anchors the mod currently manages. |
| `status` | Prints the mod's overall runtime status. |
| `health` | Runs/reports HealthScan's findings (orphaned same-tag groups, stale dangling connections). |
| `lock <x> <y> <z>` | Locks the portal at the given position, protecting it from automated changes (e.g. Repair, caps enforcement). |
| `unlock <x> <y> <z>` | Reverses `lock`, allowing automated changes to affect that portal again. |
| `uninstall --clean-keys` | Prepares the mod for removal, cleaning up its custom global keys/data as part of uninstalling. |
| `help` | Prints the list of available console verbs and their usage. |
| `repair [--apply]` | Runs Repair; without `--apply` it's a dry-run report of what would be fixed, with `--apply` it writes the fixes (after auto-snapshotting). |
| `snapshot [name]` | Takes a named (or auto-named) snapshot of the world's current portal state. |
| `snapshots` | Lists available snapshots. |
| `restore <name> [--recreate]` | Restores the world's portal state from a named snapshot, undoing a batch of changes. |
| `compat` | Reports Harmony patches on shared vanilla methods and the Wonderland/GetOffMyLawn compatibility check. |
| `metrics` | Prints portal counts and breakdowns (per-tag/per-biome/per-builder), plus transit/top-route heuristics. |
| `report <x> <y> <z>` | Prints a detailed diagnostic report for the specific portal/anchor at the given position. |

---

## Config file layout

Every section that actually appears in `wubarrk.tortalportallite.cfg`, in bind order (some, like
Foundations' Census/Audit sections, exist because a kept feature reads a value from them even
though they aren't a feature in their own right — noted below):

1. `1 - General` — feature 8, Architecture (master `Enabled` switch, `ServerConfigLocked`, `VerboseLogging`, `AcceptUnverifiedBuild`, Heartbeat).
2. `15 - Discord` — feature 8, Architecture (optional webhook).
3. `2 - Foundations: Census` — infrastructure: how often the portal census re-scans (no feature of its own; every other feature reads this snapshot).
4. `4 - Foundations: Health` — feature 3, HealthScan.
5. `5 - Foundations: Repair` — feature 4, Repair.
6. `6 - Foundations: Audit` — infrastructure: logs every tag/connection change with its attributed cause (backs Metrics' new/destroyed counts; no console verb of its own).
7. `7 - Foundations: Metrics` — feature 5, Metrics.
8. `82 - Ops: Repair (extra)` — feature 4, Repair (advanced/dangerous options).
9. `83 - Ops: Snapshot` — feature 4, Repair (the snapshot directory/retention Repair's auto-snapshot uses; also used by `snapshot`/`snapshots`/`restore`).
10. `9 - Routing: Core` — infrastructure shared by features 1, 2, and 13 (the reassertion loop every routing.json declaration relies on).
11. `11 - Routing: Schedules and Conditions` — feature 2, Sealed Gate.
12. `12 - Routing: Approach and JIT` — feature 13, Fast-Transit/Parked Terminal (approach-detection poll rate).
13. `16 - Routing: Delivery Pipeline` — feature 12, DestinationPrewarm (also backs feature 13's zone-readiness check).
14. `20 - Targeted: Foundation` — infrastructure: the phantom-portal factory feature 11 is built on.
15. `28 - Targeted: Bed` — infrastructure: feature 11's origin-lookup (a player's claimed bed).
16. `41 - Targeted: Corpse Run` — feature 11, Corpse-Run Gate.
17. `30 - Ux: General` — feature 14, Live Portal Markers (subsystem master switch).
18. `40 - Ux: Advanced Schemes` — feature 14, Live Portal Markers.
19. `90 - Economy: Core Kernel` — feature 9, The Ore Gate (master switch + declaration file path).
20. `150 - WildcardB: General` — infrastructure: master switch for feature 10's subsystem.
21. `155 - WildcardB: Portal Caps` — feature 10, Server-Enforced Portal Caps.
