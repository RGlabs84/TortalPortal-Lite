<div align="center">

# 🌀 TortalPortal Lite

![Valheim Mod](https://img.shields.io/badge/Valheim-Portal_Network-orange.svg)
[![Multiplayer Compatible](https://img.shields.io/badge/Multiplayer-Server--Synced-blue.svg)]()
[![Framework](https://img.shields.io/badge/Requires-BepInEx-red.svg)]()
[![Crossplay](https://img.shields.io/badge/Crossplay-PlayFab%2FXbox_Ready-purple.svg)]()
[![Valheim 1.0](https://img.shields.io/badge/Valheim-1.0.7--1.0.16_Server-green.svg)]()

*No client install, ever. Every gate, terminal, and health check runs entirely on the server. Built and verified against Valheim 1.0.7, 1.0.12, 1.0.15 and 1.0.16.*

</div>

TortalPortal Lite is a strictly server-side portal-network mod (GUID `wubarrk.tortalportallite`):
every feature below runs entirely on the dedicated server, operating directly on the world's raw
ZDO data instead of live game objects. Steam, Xbox, PlayFab, and full crossplay parties all get the
identical experience, with nothing to download. Forked from the sibling mod Wonderland's own
architecture, this is TortalPortal Lite's **v1.0.0** — the first packaged release. A large internal
R&D pass explored 263 possible portal mechanisms; the product owner picked exactly the 14 below to
actually ship. Everything else was cut from the codebase entirely, not just disabled — down from
296 files / ~44.8k lines to 83 files / ~10k lines. A **v1.0.1** follow-up added a 15th feature,
BarrkBOT Portal Export, for admins who want portal data fed into their own tooling.

---

<details>
<summary>📜 <b>Contents</b></summary>

- [🚪 Features](#-features)
  - [👻 Phantom Anchor Fabrication](#-phantom-anchor-fabrication)
  - [🔒 Progression-Gated Sealed Gate](#-progression-gated-sealed-gate)
  - [🩺 HealthScan](#-healthscan)
  - [🩹 Repair](#-repair)
  - [📊 Metrics](#-metrics)
  - [🚦 Version Migration](#-version-migration)
  - [🤝 Compat](#-compat)
  - [🧩 Architecture](#-architecture)
  - [🪨 The Ore Gate](#-the-ore-gate)
  - [🚧 Server-Enforced Portal Caps](#-server-enforced-portal-caps)
  - [💀 Corpse-Run Gate](#-corpse-run-gate)
  - [📡 DestinationPrewarm](#-destinationprewarm)
  - [🚄 Fast-Transit Mode & Parked Terminal](#-fast-transit-mode--parked-terminal)
  - [📍 Live Portal Markers](#-live-portal-markers)
  - [📤 BarrkBOT Portal Export](#-barrkbot-portal-export)
- [💻 Admin Console](#-admin-console)
- [🔧 Configuration](#-configuration)
- [📦 Dependencies](#-dependencies)
- [📥 Installation](#-installation)

</details>

---

## 🚪 Features

### 👻 Phantom Anchor Fabrication
**A destination that doesn't need a builder.** An admin declares a named phantom anchor — `Name`,
`Position` (`X`/`Y`/`Z`), `RotationY`, and a `Void` flag — directly in `routing.json`'s
`phantomAnchors` section, and the server creates and maintains it automatically from there.
`Void:true` keeps it invisible and non-portal, a pure coordinate for other features to point at;
`Void:false` turns it into a real, visible portal anchor players can walk up to. Either way, nobody
has to physically carry portal materials out and build the thing by hand — Sealed Gates and Parked
Terminals below both lean on phantom anchors as destinations that already exist the moment they're
declared.

### 🔒 Progression-Gated Sealed Gate
**A portal that only opens once it's earned.** Declared in `routing.json`'s `sealedGates` section
(`Name`, `Position`, `GlobalKey`, `Destination`, `SealedTag`, `OpenTag`), a Sealed Gate resolves to
nothing at all until a named global key — a boss defeat, or any custom flag the server sets —
becomes true, then opens straight to its declared destination. The server re-checks that key on its
own schedule rather than waiting on a player to interact with it: `SealedGateEvalSeconds`, in config
section `11 - Routing: Schedules and Conditions`, controls how often (default 1 second).

### 🩺 HealthScan
**A standing check for the portal pairs vanilla itself leaves broken.** Running on its own timer
(`HealthIntervalSeconds`, config section `4 - Foundations: Health`, default 30 seconds), HealthScan
walks the managed portal network looking for two specific problems: odd-count same-tag groups —
which vanilla's own pairwise-only pairing always leaves one member of permanently orphaned — and
stale, dangling connections. It's deliberately narrow about what counts as broken: a portal whose
one-way connection is intentional, like the Corpse-Run Gate below, is excluded from this report
entirely, because that's a designed feature, not a defect.

### 🩹 Repair
**Turns HealthScan's findings into fixes you can trust, and undo.** Repair only ever acts on what
HealthScan actually flagged: retagging an orphaned portal, clearing a dangling link. It's dry-run
by default (`RepairDryRunDefault`, config section `5 - Foundations: Repair`) — `removekey tpl
repair` on its own just reports what it would do — and only `removekey tpl repair --apply` writes
anything. Before it does, it automatically snapshots the world's portal state first, so `removekey
tpl restore <name>` can always undo the whole batch if the fix wasn't what you wanted. If
`AutoRepair` is on but Version Migration has the destructive gate closed, the scheduled `--apply`
pass logs that it's idle once and then waits quietly, resuming on its own when the gate opens.

### 📊 Metrics
**Know your portal network at a glance.** `removekey tpl metrics` (config section `7 - Foundations:
Metrics`) reports total, connected, and managed portal counts, breakdowns per tag, per biome, and
per builder, plus a position-based heuristic for transit counts and "top routes" across the world.
It's a read-only view straight off server-side ZDO data — nothing to install to see any of it.

### 🚦 Version Migration
**A boot-time seatbelt before anything destructive runs.** At startup the mod checks two things: is
this a Valheim build it has actually been verified against, and does the game's own portal-prefab
registry look intact. If either check fails, destructive passes — specifically `removekey tpl
repair --apply` — refuse to run at all rather than risk corrupting the world. An admin can
explicitly opt back in via `GlobalConfig.AcceptUnverifiedBuild`, but that's a deliberate override,
never a default.

### 🤝 Compat
**One command's worth of answer to "my portals keep unpairing."** `removekey tpl compat` reports
which Harmony patches exist on the handful of vanilla methods this mod also touches, plus an
explicit, named check against two specific sibling mods this server is expected to run alongside —
Wonderland and GetOffMyLawn. Both were read directly: both patch `ZDO.SetOwner`, but both vetoes are
scoped only to their own tracked objects (Wonderland's buoyant water items, GetOffMyLawn's
ward-held building pieces) and neither ever fires on a portal ZDO. Confirmed: no interference.

### 🧩 Architecture
**Not a player-facing feature — the frame everything above is built on.** Under the hood, the mod
runs a fixed subsystem tick order with disjoint write ownership at every stage — Census first (what
portals exist), then Audit, then HealthScan, and on down the line — each stage reading what came
before it and never fighting another stage for the same data. Nothing here has a console verb or a
config key of its own; it's simply the reason the other twelve features can coexist without
stepping on each other.

### 🪨 The Ore Gate
**A perk written onto one specific portal, not a global setting.** Declared in `economy.json`'s
`oreGates` section against a portal's own position, `AllowAllItems` (bool, default `true`) lets ore
and metal — normally barred from portal travel — actually pass through that one gate, and an
optional `ExitDistance` (float) extends how far in front of it a traveller steps out on arrival.
It's a real, vanilla-client-honoured gameplay effect: the client just sees ore go through and a
further step-out distance at that gate, because the data lives on that portal's own record, not in
a global rule.

### 🚧 Server-Enforced Portal Caps
**A per-player portal limit an unmodified client genuinely cannot bypass.** Off by default
(`PortalCapEnabled`, config section `155 - WildcardB: Portal Caps`), turning it on caps each player
at `PortalCapPerCreator` portals (default 6). Once a player's live portal count goes over the cap,
their newest excess portals are destroyed automatically and they're toasted about it — enforced
entirely server-side against raw ZDO ownership, unlike a client-side placement limit a modded
client could simply ignore.

### 💀 Corpse-Run Gate
**Gear recovery without a bare-handed run back into danger.** The instant a tracked player dies, the
server automatically raises a private, temporary portal near their claimed bed (or the world spawn,
if they have none) leading straight to their tombstone. This is a genuine **one-way** trip by
design, not a round-trip pair — there is no automatic portal back from the grave to the bed.

**Both ends always appear.** A death is picked up from the tombstone itself, not from the list of
connected players, so dying and immediately logging off still gets you a gate — it is standing when
you come back, and that is when you are told about it. If a raise can't complete on the first try it
is retried rather than dropped, and every maintenance pass afterwards checks **both** ends and
re-builds whichever one is missing, in the same spot it was placed.

**Both ends are placed as clear as the ground allows, and on the ground.** The server scans outward
from the tombstone (and from the bed) in 1 m rings for a spot with `ClearanceMeters` (default 10 m)
of room — measured **edge to edge** from every tree trunk, boulder, building piece, portal and
generated location, using each object's real collider extents rather than its pivot, so a boulder
that spans eight metres around its centre counts as eight metres of boulder. Tiny props (mushrooms,
flowers, berry bushes) only need 2 m. That figure is a *preference*: nowhere in a Valheim forest has
10 m of room from everything, so the search steps the requirement down and takes the **closest** spot
honouring the best figure that patch of map can offer — never below `MinRoomMeters` (default 2 m) of
real room, and widening its radius before it settles for less. The ground under the gate is the
client's own heightmap recipe re-run on the server — biome blending and hoe/pickaxe terraforming
included — and the frame's four corners and surroundings are checked so it neither floats off a slope
nor wedges against a cliff. A death inside a dungeon opens the grave-side gate at the dungeon's
entrance. A gate raised by a previous server run and forgotten is reaped automatically, so restarts
never leave stray portals.

**Both gates are immune to all damage** — not de-targeted, immune. Valheim never lets an attacker
apply damage itself: every source, a troll's AoE swing and a fire's damage-over-time included, ends
by sending an RPC to whichever machine *owns* the portal, and a routed RPC that finds no object on
the receiving machine is dropped on the floor. A dedicated server never builds objects where players
are, so a portal the server owns throws away every hit aimed at it. TortalPortal Lite holds that
ownership against the server's own 2-second hand-off to nearby players, refuses both ways a portal
can be deleted (including the one vanilla applies from any client without checking who sent it),
no-ops every `WearNTear` path that could hurt the piece, writes 1,000,000,000 HP and the full wear
immunity set for the moments a client holds a copy, blocks hammer removal, and keeps a watchdog that
puts health and ownership back and counts every time it had to. Every 5 minutes the log says both
ends are still standing and what the protection refused.

The gate self-destroys once the grave is emptied or despawned. It no longer expires on a timer while
the grave still stands: `GateTtlMinutes` (config section `41 - Targeted: Corpse Run`) defaults to `0`,
meaning no limit, because a deep Mistlands or Ashlands corpse run routinely takes longer than the old
30-minute cap allowed.

### 📡 DestinationPrewarm
**No walking through into a portal that just quietly stopped working.** The instant any managed
portal's destination changes, DestinationPrewarm force-sends the new destination data to nearby
players' clients immediately, so a re-point is never followed by someone walking through into a
"dead" transit because their client hadn't received the update yet. `PrewarmRadius` (config section
`16 - Routing: Delivery Pipeline`, default 30m) controls how far out that push reaches. It's
always-on — a safety net, not an opt-in feature.

### 🚄 Fast-Transit Mode & Parked Terminal
**A portal the server walks you through, no swirl required.** A Parked Terminal is a managed portal
kept permanently "unconnected" on purpose; instead of the normal portal-connection walk-through, the
server itself detects a player stepping up to it and teleports them directly. Fast-Transit is the
quicker variant of that same mechanism — a roughly 2-4 second fade instead of vanilla's roughly
8-second swirl — used once the destination zone is confirmed already generated and safe to land in;
otherwise it automatically falls back to the normal, slower transit rather than risk a bad landing.
Both are declared in `routing.json`'s `parkedTerminals` section: `Distant:false` is Fast-Transit,
`Distant:true` is the plain Parked Terminal base mechanism underneath it.

### 📍 Live Portal Markers
**A map pin that can actually disappear again.** Name any portal with a leading `#` in vanilla's own
rename box — no client mod, just the game's stock UI — and it gets a labelled marker on every
connected player's map, injected through vanilla's own persistent-event system. Rename the portal
away from that `#` prefix and the marker vanishes with it; every other feedback surface this mod
family uses is permanent once placed, which is exactly why this one exists. Off by default
(`LiveMarkersEnabled`, config section `40 - Ux: Advanced Schemes`) and kept to a small radius, since
a persistent event's radius also touches nearby spawners and weather.

### 📤 BarrkBOT Portal Export
**A live feed for your own tooling, not just your own eyes.** Writes
`BepInEx/config/TortalPortalLite/barrkbot_portals.json` — per-player portal counts, cap/over-cap
flags, and world totals — atomically (temp-file-then-swap, so nothing reading it ever catches a
half-written file), on a floor of once every 60 seconds. Built directly against BarrkBOT's own
published ingestion contract: no HTTP, no webhook, no auth — purely a file on this server's own disk
for BarrkBOT (or anything else you point at it) to read on its own schedule. Force an immediate
rewrite any time with `removekey tpl barrkbot`. Config section `84 - Ops: BarrkBot Export`
(`Enabled`, default on; `IntervalSeconds`, default 60, floored at 60).

---

## 💻 Admin Console

Every command is typed as `removekey tpl <verb> [args]` into an admin's own in-game console (`F5`)
or chat — this piggybacks entirely on vanilla's own remote-command channel, so driving TortalPortal
Lite from in-game needs no client mod either.

| Verb | What it does |
| :--- | :--- |
| `list` | Lists every portal the mod currently manages. |
| `status` | A one-line summary of the mod's own running state. |
| `health` | Runs an on-demand HealthScan pass immediately, instead of waiting for the next scheduled interval. |
| `lock <x> <y> <z>` | Locks the portal at that position out of automated passes. |
| `unlock <x> <y> <z>` | Reverses `lock`. |
| `uninstall --clean-keys` | Prepares for removal, clearing the mod's own tracked keys and data before the plugin is pulled. |
| `help` | Lists every verb with its arguments. |
| `repair [--apply]` | See [Repair](#-repair) above — dry-run without `--apply`, writes fixes with it. |
| `snapshot [name]` | Saves a snapshot of the world's current portal state, named or auto-named. |
| `snapshots` | Lists saved snapshots. |
| `restore <name> [--recreate]` | Restores a named snapshot; `--recreate` re-creates any portal the snapshot had that no longer exists in the world. |
| `compat` | See [Compat](#-compat) above. |
| `metrics` | See [Metrics](#-metrics) above. |
| `barrkbot` | Forces an immediate rewrite of `barrkbot_portals.json` — see [BarrkBOT Portal Export](#-barrkbot-portal-export) above. |
| `report <x> <y> <z>` | A detailed, single-portal readout for the portal at that position. |

---

## 🔧 Configuration

Settings live in `BepInEx/config/wubarrk.tortalportallite.cfg`, split into numbered sections, each
entry documented with its own description and range right in the file. **Edits are picked up
live**: the file is polled every 5 seconds and applied automatically — no server restart needed for
a config change to take effect.

### Server-Synced (Admin Controlled)
| Section | What it covers |
| :--- | :--- |
| `1 - General` | `Enabled` — the master switch for the entire mod (default on). `AcceptUnverifiedBuild` — admin opt-in to let destructive passes run against a Valheim build or portal-prefab registry the mod hasn't itself verified (see Version Migration). Also the periodic server-log "still alive" Heartbeat. |
| `40 - Ux: Advanced Schemes` | `LiveMarkersEnabled` (off by default), `LiveMarkersRadius`, `LiveMarkersReassertSeconds` — see Live Portal Markers above. |
| `4 - Foundations: Health` | `HealthIntervalSeconds` — how often the background HealthScan pass re-checks the portal network (default 30s). |
| `5 - Foundations: Repair` | `RepairDryRunDefault` — whether `removekey tpl repair` reports-only or writes fixes by default. |
| `7 - Foundations: Metrics` | Settings behind `removekey tpl metrics` — portal counts, per-tag/biome/builder breakdowns, and route heuristics. |
| `11 - Routing: Schedules and Conditions` | `SealedGateEvalSeconds` — how often a Sealed Gate's `GlobalKey` is re-checked (default 1s). |
| `16 - Routing: Delivery Pipeline` | `PrewarmRadius` — how far out DestinationPrewarm force-sends a changed destination to nearby clients (default 30m). |
| `41 - Targeted: Corpse Run` | `GateTtlMinutes` — how long a Corpse-Run Gate stands if the grave is never emptied; `0` (the default) means no limit, so it lives exactly as long as the grave. `ClearanceMeters` — edge-to-edge room every gate *prefers* from trees, rocks, pieces, portals and locations (default 10, range 1–30). `MinRoomMeters` — the hard floor of real room it will accept (default 2, range 0.5–10). `SearchRadiusMeters` — how far out from the tombstone / bed to look, widened automatically if nothing in it qualifies (default 40, range 5–150). `OffsetMeters` — minimum distance of the grave-side gate from the tombstone (default 2). `MaxGraveAgeMinutes` — skip graves older than this; `0` (default) means no limit. |
| `155 - WildcardB: Portal Caps` | `PortalCapEnabled` (off by default) and `PortalCapPerCreator` (default 6) — the per-player portal limit and its enforcement. |

### Local to Your Game
| Setting | Section | What it does |
| :--- | :--- | :--- |
| File-system paths | *(various)* | Where this server keeps `routing.json`, `economy.json`, and its own snapshot/report output. Read straight off this machine's disk, never synced to any client, since they're specific to this server. |
| `Enabled`, `IntervalSeconds` | `84 - Ops: BarrkBot Export` | See [BarrkBOT Portal Export](#-barrkbot-portal-export) above — local because it's writing a file to this server's own disk. |

### Declaration Files
Two of the features above aren't driven by the `.cfg` at all — they're declared directly in JSON
files sitting next to it:

- **`routing.json`** — `phantomAnchors` (Phantom Anchor Fabrication), `sealedGates`
  (Progression-Gated Sealed Gate), and `parkedTerminals` (Fast-Transit Mode & Parked Terminal).
- **`economy.json`** — `oreGates` (The Ore Gate), one entry per upgraded portal.

---

## 📦 Dependencies

> ⚠️ **Requires:** BepInEx (the Valheim pack, `denikson-BepInExPack_Valheim-5.4.2350` or newer) —
> and only BepInEx.

| Dependency | Why |
| :--- | :--- |
| **denikson-BepInExPack_Valheim** (5.4.2350) | The mod loader (also provides HarmonyX, which every patch in this mod runs on). |

## 📥 Installation

TortalPortal Lite is a **server-side-only** mod — install it once, on the server, and every
connected player benefits with nothing to download, on any platform.

1. Install **BepInExPack Valheim** (`denikson-BepInExPack_Valheim-5.4.2350` or newer) on the server.
2. Drop `TortalPortalLite.dll` into the server's `BepInEx/plugins` folder.
3. Restart the server. That's it — no client install, no client config, nothing for players to do.

<div align="center">

*A Valheim mod by Wubarrk.*

</div>
