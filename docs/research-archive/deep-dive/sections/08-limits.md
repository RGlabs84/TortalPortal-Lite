## Limits, wildcards, and what to build first

### Wildcards worth shipping

**Sector Zero, named** [needs-ingame-check | server-enforced]. `ZoneSystem.SectorToIndex` clamps any zone outside 0..511 to `Sector = 0` (`:115760-115773`); the dedicated server's own pinned reference position `(1e6,0,1e6)` (`Game.FixedUpdate` server-only line, `:100458-100466`) maps to zone `(15625,15625)`, which clamps into that same bucket. If `ZoneSystem.IsActiveAreaLoaded()` (`:114054-114069`) ever turns true there — gated on `HeightmapBuilder.IsTerrainReady` succeeding at that coordinate, unverified — the server-only invalid-prefab reaper in `ZNetScene.CreateObjectsSorted` (`:82105-82143`) claims and destroys any ZDO in bucket 0 whose `CreateObject` fails, and any *valid* portal prefab placed there is instead instantiated as a live server-side `TeleportWorld` with a running `UpdatePortal` invoke — the one place a server-side portal component can exist at all. Two use cases fall out: a Void Anchor mis-placed beyond ±16.4 km self-destructs (verified reaper mechanics, gate condition unverified); a mod-created hash-0 ZDO placed *inside* bounds never enters this bucket and is never reaped. Decisive one-line test: log `IsActiveAreaLoaded()` + `m_zones.ContainsKey(new Vector2s(15625,15625))` + `ZNetScene.instance.NrOfInstances()` 60 s after boot.

**Portal scaling via `s_scaleHash`** [needs-ingame-check | client-honoured]. `ZNetView.Awake`'s scale block (`:82591-82606`) applies `ZDOVars.s_scaleHash`/`s_scaleScalarHash` to `transform.localScale` at instantiation, gated on `ZNetView.m_syncInitialScale` (`:82543`) — a public bool whose value on `portal_wood`/`portal_stone` is unknown Inspector data. If set, a server write of `Vector3(3,3,3)` genuinely enlarges the portal *and* its child `TeleportWorldTrigger` collider (Unity hierarchy scaling), giving oversized hub arches and undersized secret gates with proportionally correct walk-in volumes. `m_activationRange`/`m_exitDistance` are NOT scaled by this (plain floats, read at `:143495`/`:143546`). Applies at instantiation only, same LoadFields-class latency. One-line test on the gating bool settles the whole option.

**Admin Command Channel via `RPC_RemoteCommand`** [confirmed feasible | server-enforced]. `Terminal.TryRunCommand` on the client forwards any `RemoteCommand`-flagged vanilla command that fails local `IsValid` (e.g. `setkey`/`removekey`/`listkeys`, registered `onlyServer:true,remoteCommand:true` at `:43509-43533`) to `ZNet.instance.RemoteCommand(text)`, which server-side hits `ZNet.RPC_RemoteCommand` (`:81879-81888`) — gated on `ListContainsId(m_adminList, rpc.GetSocket().GetHostName())`, the one socket-verified identity in the protocol — then `InternalCommand` runs it through the headless `Console.instance` (`Game.Awake` instantiates it, `:100038-100041`). A Harmony prefix on `ZNet.InternalCommand(ZRpc, string)` sniffing a sentinel riding `removekey tpl <verb>` gives a real, admin-only, authenticated text command channel to an unmodified client's own F5 console, with replies via `ZNet.RemotePrint`. This directly refutes the prior review's flag that ops/admin-console assumptions were never checked — they check out.

**Event Beacon (`PersistentEventSystem`)** [confirmed feasible-with-caveats | client-honoured]. `PersistentEventSystem.m_activePersistentEvents` is a server-persisted, JSON-broadcast list of `{sourceEventId, eventId, position, radius, startTime, duration}` (`:104922-104939`), delivered via the routed RPC `"UpdateClientEventsList"` (`:105093`, registered `:105284`) and re-pulled automatically by new peers (`RequestActiveEventsList` / `m_onNewPeer`, `:105289-105397`). Appending an entry directly (bypassing `RPC_RequestStartEvent`'s random placement) puts a removable, animated, **labelled** map pin at a managed portal (`Minimap.UpdatePersistentEventPins`, client `:57505-57549` — the only vanilla channel that can both add *and cleanly remove* a named marker), a client-side weather override for the radius (`EnvMan.GetEnvironmentOverride` → `PersistentEventSystem.GetEnvironmentOverride`, `:104830-104842`), and event-gated spawn/wear rules for pieces with `m_requiredPersistentEvent`. Label text and weather are locked to whatever `Game`'s `m_possibleEvents` Inspector list ships (undumped). Requires a publicizer on `UpdateClientEventsList`/`m_eventIdCounter`; a bad `sourceEventId` throws inside every vanilla client's map-pin loop, so validate against `m_possibleEvents.Count` before publishing.

**Landing Pad Engine (`TerrainComp`)** [confirmed feasible-with-caveats | server-enforced]. The only server-writable geometry in the game: each zone's terrain compiler ZDO carries `ZDOVars.s_TCData` (`:78353`), a compressed per-vertex height/paint array (`TerrainComp.Save`/`Load`, `:143793-143934`) indexed by `Heightmap.WorldToVertex` (`:129177-129183`) and clamped to `base ± 8 m` (`ApplyToHeightmap`, `:143950-143985`, `c_LevelMaxDelta` `:128249`). Writing a flattened, paved disc under a phantom exit means arrivals land on solid, level ground instead of whatever the procedural terrain happens to be — the correct fix for the "arrival physics" problem in *Skyfall, Seabed and Underworld Exits* rather than choosing altitudes that happen to work. ±8 m ceiling means it cannot rescue a destination over open water deeper than that, or a chasm.

**Phantom Survival** [confirmed feasible-with-caveats | client-honoured, with a simplification]. Unsupported server-fabricated portals get demolished by `WearNTear.UpdateWear`'s support pass (`:149662-149829`) on the first client that owns them, after a 30 s grace (`ShouldUpdate`, `:149653-149660`). Four independent mitigations: place the ZDO at real ground height (best, needs Landing Pad); LoadFields-override `WearNTear.m_noSupportWear=false`/`m_noRoofWear=false`; set `GlobalKeys.NoBuildingFall` (world-wide); or — the cheapest single lever — LoadFields-override `Piece.m_canBeRemoved=false`, which *also* zeroes every wear-damage path via the `if (num>0f && !CanBeRemoved()) num=0f;` guard that runs immediately before `ApplyDamage` — making the separate WearNTear overrides largely redundant whenever hammer-immunity is already wanted (which it usually is, per *The Adamant Gate*).

**The Ferry Route / corrected Player's Ship** [needs-ingame-check | client-honoured]. `Ship.Start` registers `"Forward"/"Backward"/"Stop"/"Rudder"` on the ship's ZNetView with **no owner check** (`:140482-140485`) — routing them to the ZDO owner via `ZRoutedRpc` genuinely steers a crewed vanilla ship. Two hard gates make it useless empty: `m_players.Count==0` forces `Stop` every physics tick (`:140606-140610`), and reverse/slow requires a real helmsman (`HaveValidUser`, `:141387-141394`) — so a ferry can only ever move WITH riders aboard, sailing (never reversing) between docks. For an *empty* ship, a server that pins ownership and writes position directly stays under `ZSyncTransform`'s 5 m snap threshold and the rider-correction ceiling in `Character.UpdateMotion` (`< 4 m` per tick, `:2118-2135`) — but loses all buoyancy/physics while server-owned, and arrival-onto-a-moving-deck via portal remains impossible (`Player.UpdateTeleport`'s frozen 8 s snapshot, `:15343-15349`, plus `ZoneSystem.m_solidRayMask` excluding the vehicle layer, `:113424`).

**Tame and Cart Follow-Through** [confirmed feasible-with-caveats | server-enforced]. Unlike portals, ordinary ZDOs *do* re-file on position change (`ZDO.SetSector`, `:73752-73768`, `ZDOSectorInvalidated`, `:75989-75996`), so relocating a tame's or unhitched cart's ZDO to the destination genuinely delivers it there once the source area is unowned. Selection: `ZDOVars.s_follow` string-matches the traveller's name (`Tameable.RPC_Command`, `:19444/:19457`); on arrival the destination client's `Tameable.UpdateSavedFollowTarget` (owner-only) resumes following with zero server involvement (`:19472-19497`). Name-matching is a heuristic (two same-named players collide); a cart mid-pull (`s_attachJointHash` true, `Vagon`, `:147610-147690`) must be skipped until detached.

**Per-Peer Location Icons** [confirmed feasible-with-caveats | client-honoured]. `ZoneSystem.SendLocationIcons(long peer)` is genuinely per-peer (`:113581-113593`) and the client does a wholesale dictionary replace on receipt (`RPC_LocationIcons`, `:113595-113607`). Because `Game.FindSpawnPoint` falls back to whatever position is keyed `"StartTemple"` in that dictionary when the player has no bed/logout point (client `:100774-100804`), a per-peer payload silently redirects where a faction's bed-less characters respawn and which altar/hub icons they see — entirely by rewriting a map channel, no character-ZDO writes at all. Vanilla re-broadcasts the real list to peer 0 whenever an `m_iconPlaced` location is placed (`:114999-115003`), so the mod's prefix must intercept that call too or policy resets silently.

**Ambush Gates (`RandEventSystem`)** [confirmed feasible-with-caveats | server-enforced]. `SetRandomEventByName(name, pos)` is public (`:107067-107071`) and bypasses `m_requiredGlobalKeys` entirely — the server can drop a raid at a portal's exit on arrival and lock the gate (`SetConnection(Portal, ZDOID.None)`) until `GetCurrentRandomEvent()==null`. One event slot exists globally (`:107085-107092`), so an ambush cancels any natural event and vice versa; if the spawn list contains a boss-flagged creature, `activeBosses` increments and every portal in the world blacks out via `NoBossPortals` — a real coupling to plan around, not a bug to fix.

### Complete ruled-out list

| Option | Precise reason | Citation |
|---|---|---|
| Cross-Server / Multi-World Travel | No redirect RPC anywhere — `ZNet.RPC_Disconnect` takes zero parameters; `SetServerHost`/`SetServer` are static setters called only from the main-menu `FejdStartup`, never from an RPC, ZDO, or global key. Character inventory never crosses the wire in either direction. | `assembly_valheim.decompiled.cs:80165-80177`, `:81288-81306`, `:97925/:98974-99019`; `Player.Save assembly_valheim_SERVER.decompiled.cs:14091-14170`; `ZNet.SaveOtherPlayerProfiles :79653-79675` |
| Relocating a base instead of the player | Position writes on a non-owned ZDO never bump revision (`ZDO.InternalSetPosition`); a vanilla client never re-applies a changed position to an existing instance (only at `Instantiate`); portal ZDOs are permanently sector-frozen; ownership near a player is reclaimed every 2 s. | `:73734-73745`, `:81996-82022`/`:82567-82633`, `:73752-73757`, `:76901-76924` |
| Ashlands stone portal as a distinct *class* of behaviour | `TeleportWorld` is byte-identical server/client with exactly one class definition; zero portal-prefab-name literals exist anywhere; every candidate difference is a plain Inspector field with no ZDO/RPC path. Per-instance override via LoadFields is possible, per-*prefab-class* behaviour is not. | `:143402-143682` vs `assembly_valheim.decompiled.cs:144142-144422` (diff empty); `:143411-143437` |
| Per-player transit gate (PIN / "only the builder") | No server-side moment-of-transit hook exists at all; `TeleportWorld.Teleport`'s sole caller is client-only and triple-gated (collider, `Player` component, `Player.m_localPlayer`). | `TeleportWorldTrigger.OnTriggerEnter :143664-143682`; single call site verified by grep |
| Per-peer divergent ZDO routing | A ZDO is one object with one revision-cached view per peer; there is no mechanism to serve two peers different connection values for the same ZDO. | `ZDOExtraData.s_connections :74794`, single `ZDOConnection` per ZDOID `:75524-75534` |
| Simultaneous multi-destination portal / true hub | `ZDOConnection` carries exactly one readonly type + one readonly target; structurally impossible, not merely unimplemented. | `:74794`, `:75524-75534` |
| Retargeting or gating dungeon/crypt doors (`class Teleport`) | No `ZNetView`, no ZDO, no RPC anywhere in the class; destination is a compiled `Teleport m_targetPoint` Inspector reference. Never reads `NoPortals`, never calls `IsTeleportable`. | `:143316-143390` (fields `:143320-143322`); `Interact :143345-143370` |
| Slash commands as player→server input | An unrecognised `/command` is swallowed entirely client-side (`Terminal.TryRunCommand(silentFail:true)`); nothing is ever sent. | `assembly_valheim.decompiled.cs:41947-41955`, `:45734-45758` |
| Ordinary chat text as player→server input (solo player) | `Chat.SendText`/`Talker.Say` address each *other* listed player individually; with one player online, zero packets are produced. Pending in-game test: collapses to a broadcast only if `RelationsProvider == null`. | `:41975-42043`, `:42002-42007` |
| Removing a status effect granted via `RPC_AddStatusEffect` | No `RemoveStatusEffect` RPC exists anywhere in either assembly. | grep of `StatusEffectRpc`-adjacent RPC names, none found |
| Removing a pushed `RPC_DiscoverLocationResponse` map pin | The pin is written `save:true`; there is no server-issued removal RPC, only the player's own local `Minimap.RemovePin`. | `Game.RPC_DiscoverLocationResponse :100823-100831`; `Minimap.DiscoverLocation/AddPin` client `:58394-58433` |
| Raising the 10-character portal tag cap for the *client's own edit box* | Cap is `TextInput.instance.RequestText(this,"$piece_portal_tag",10)` → `m_inputField.characterLimit`; a longer server-written tag risks silent truncation the moment a player re-saves it. | `:143482`; `TextInput.Show :61509-61522` |
| Reading or debiting a living player's inventory | Never networked in any form; `Player.Save` writes to the local `.fch` only, `ZNet.SaveOtherPlayerProfiles` collects nothing back. Sole exception: a dead player's tombstone `Container`. | `:14091-14170`, `:79653-79675`; `TombStone.Setup :19845-19853` |
| `TeleportHome` / `Game.RequestRespawn` as a network primitive | No RPC or routed call anywhere invokes `RequestRespawn`; `TeleportHome.OnTriggerEnter` is local-player-gated exactly like a portal trigger. | `:100396-100419`, `:143393-143400` |
| Per-portal colour / effect fields via LoadFields | `Color`, enums and arrays are not supported field types in `ZNetView.LoadFields`'s reflection (`int/float/bool/Vector3/string/GameObject/ItemDrop` only). | `:82669-82739` |
| Widening `m_activationRange` to enlarge the actual walk-in trigger | Field only feeds `Player.GetClosestPlayer` for the proximity glow effect; the transit volume is a separate child `TeleportWorldTrigger` collider untouched by any ZDO write. | `UpdatePortal :143491-143509`; `TeleportWorldTrigger :143664-143682` |
| `PrivateArea.CheckAccess` as a server-side authorization check | `m_allAreas` is populated only in `PrivateArea.Awake` on a live GameObject, which never exists server-side — the call returns `true` for every point in the world, a silent allow-all. | `:137547-137593`, `m_allAreas :137173`, `.Add` only at `:137197` |
| Any `Game.instance.GetPlayerProfile()`-based vanilla helper (`Piece.IsCreator`, `Bed.IsMine`, `TombStone.IsOwner`) | `Game.Awake` creates a throwaway profile named "Stranger" with a random UID server-side; these compare against garbage rather than throwing. | `Game.Awake :100047`; `Piece.IsCreator :136436-136441` |
| `noportals` / `nomap` console commands as a server lockdown lever | Both wrapped in `if (Player.m_localPlayer != null)`; `nomap`'s `IsServer()` branch is *nested inside* that same guard, not an alternative to it — the earlier "nomap is correct" framing is wrong. | `:45283-45298`, `:45261-45283` |

### TortalPortal feature parity

| Feature (full client mod) | Lite (server-only) status | Mechanism / gap |
|---|---|---|
| ScatterCommand (admin spawn/remove portals) | **Fully replicable — stronger** | `ZDOMan.CreateNewZDO`+`SetPrefab`+`Persistent`+ `DestroyZDO` after `SetOwner`; admin gate via `ZNet.IsAdmin`/socket host name, not `Player.IsAdmin` |
| Portal cap enforcement | **Fully replicable — strictly stronger than the original** | Client version is a `Player.TryPlacePiece` prefix, trivially bypassed by an unmodded client; Lite's `ZDOMan.CreateNewZDO`+`ZDO.Deserialize` postfix pair destroys the over-cap ZDO on arrival, genuinely unbypassable |
| PortalExport (world portal census/JSON) | **Fully replicable** | `ZDOMan.GetPortalList()` (`:77648-77656`) is a free, always-resident, world-wide enumeration; no client involvement ever existed in this half of the original mod |
| PlayerNames (creator identity harvesting) | **Fully replicable** | `ZDOVars.s_creator`/`s_creatorIndex` on every placed piece, joined through `ZNet.World.m_playerHistory` (socket-verified) |
| PortalRegistry (client-synced index) | **Degraded** | Delivery mechanism (`ServerSync.CustomSyncedValue<T>`) is invisible to a vanilla client with no `ConfigSync` RPC registered; the ENCODE half survives as a JSON/Discord export, the DELIVER half does not |
| PortalBehaviorPatch (ore check / exit offset / activation range) | **Degraded** | Per-ZDO override via `ZNetView.LoadFields` on `TeleportWorld.m_allowAllItems`/`m_exitDistance` works but only at instantiation, per-instance not per-prefab-class, and the SOURCE portal's fields govern (not the destination's — a common inversion error) |
| PortalMapPins | **Degraded** | `RPC_DiscoverLocationResponse` gives a real, saved pin with no live add/remove; original mod added/removed client-side pins every 2 s |
| SecurityPatch (PIN-gated retag) | **Degraded → mostly ruled out** | No per-transit prompt is possible at all; the closest substitute is an Ownership Pin that freezes retagging entirely for anyone, not a per-player challenge |
| MessageHudPatch (centre messages) | **Degraded** | `PlayerNotify`-style toasts via the per-character `"Message"` RPC substitute for feedback, but cannot intercept or suppress vanilla's own UI messages |
| MapInputPatch (destination picker via map) | **Degraded** | Map ping (`Chat.SendPing` → `"ChatMessage"` broadcast to peer 0) gives one-shot coordinate selection, not a live preview or list UI |
| TamesPatch | **Degraded** | Position-relocation heuristic (see Tame Follow-Through) substitutes for the original's direct `Player.TeleportTo` hook, name-matched not ID-matched |
| PortalUIManager (IMGUI destination window) | **Impossible** | Requires `Player.m_localPlayer`; no server-side UI surface exists at all |
| LivePreview (client GPU render of destination) | **Impossible** | Client rendering pipeline, no server equivalent |
| Favorites | **Impossible** | Pure local `.fch`/config-file state with no networked representation whatsoever |
| InputFocusPatch, MapInputPatch's non-ping half, SnapshotAnchor, AnchorPresence, AnchorEnvironment, SnapGuard, TraderRepair | **Impossible** | All patch client-only systems (`Chat.HasFocus`, `GameCamera`, `ZNetScene.CreateObjects`, `SpawnSystem` local instances, `SnapToGround`, `Trader.Start`) that never run on a dedicated server |
| TortalPinIcon / PinLibrary / UI / Theme / VFX (custom rendering assets) | **Impossible** | Client-side texture/particle generation with no server-writable equivalent |

Net count matches the digest's estimate: 4 fully replicable (2 of them *strictly stronger* than the original), 6 degraded, 12 impossible — the earlier "4/8/10" split undercounts the impossible column once `MapInputPatch`'s non-ping surfaces and the five environment/anchor patches are itemised separately.

### The minimal optional client mod counterfactual

Every hard ceiling in this report traces to one of four root causes, and each is dissolved by a small, clearly-labelled optional client plugin rather than a mandatory one:

1. **No moment-of-transit hook.** `TeleportWorld.Teleport(Player)` has exactly one caller, gated on `Player.m_localPlayer` (`:143673-143681`). A ~15-line client Harmony prefix on `Teleport` fires exactly once, at the exact instant, with the portal instance and the `Player` in scope — replacing the 0.2-0.4 s position-polling approximation everywhere in this report with an exact event. That single hook buys per-transit server authorization, an accurate destination picker, per-player routing, and tolls charged at the moment of use.
2. **No inventory visibility.** A client plugin can serialise an inventory digest into `ZNetPeer.m_serverSyncedPlayerData` (`RPC_ServerSyncedPlayerData`, `:80395-80410`, refreshed only every 2 s — stale for exact enforcement) or, better, attach it to the transit-request RPC the plugin sends in response to (1). Buys server-side ore enforcement and toll charging with an exact snapshot instead of a 2 s-old one.
3. **No UI.** `PortalUIManager`, `LivePreview`, `PortalMapPins`, `Favorites` all become reachable again; `ServerSync.CustomSyncedValue<T>` (already vendored identically in Wonderland and TortalPortal) is the ready-made delivery channel for a server-maintained portal index, a channel a vanilla client has no handler for and will never receive.
4. **No cross-server redirect.** A client plugin *can* call `ZNet.SetServerHost` + reconnect — the only way multi-server travel ever happens — but this still requires a full logout/rejoin through the main menu (`SetServerHost` only sets static join fields, `:81288-81306`; consumed by `FejdStartup.JoinServer`, `:98979-99019`), not a seamless hop, and character data still does not transfer unless the two servers share a save directory.

Design shape: gate the enhanced path on a non-enforcing `ServerSync.VersionCheck` announcement so unmodified clients keep the full server-only feature set described elsewhere in this catalog, degraded but complete, while modded clients get the additionally-precise version. Estimated cost: ~200-300 lines for the transit prefix, one request/response RPC pair, and the handshake — everything past that is UI polish.

### Open questions and their in-game checks

| Question | Check |
|---|---|
| Does `IsActiveAreaLoaded()` ever return true at the server's pinned `(1e6,0,1e6)` reference position? | Log `IsActiveAreaLoaded()`, `m_zones.ContainsKey(new Vector2s(15625,15625))`, `ZNetScene.instance.NrOfInstances()` 60 s after boot |
| If Sector Zero activates, does a real portal placed there instantiate a working, retaggable server-side `TeleportWorld`? | Place one at `x=20000`, retag from a client, watch server logs |
| Is `ZNetView.m_syncInitialScale` set on `portal_wood`/`portal_stone`? | On a client: `ZNetScene.instance.GetPrefab(name).GetComponent<ZNetView>().m_syncInitialScale` |
| Does the enlarged/shrunk `TeleportWorldTrigger` still fire correctly at 0.3x / 3x scale? | Write `s_scaleHash`, relog, walk through from several angles |
| Which prefabs are in `Game.m_portalPrefabs` / `PortalPrefabHash`? | Log both at first `OnWorldReady`/`Heartbeat` on the dedicated server |
| Does the portal prefab carry `Piece`/`WearNTear` at all, and what are the exact `<TypeName>` strings LoadFields needs? | `ZNetScene.instance.GetPrefab(name).GetComponents<MonoBehaviour>()` |
| Does a server-authored `TeleportWorld.m_allowAllItems` via LoadFields actually gate `IsTeleportable` on a live vanilla client? | Write the three ZDO ints, have a player carrying ore leave and re-enter the zone, walk through |
| What is `PersistentEventSystem.m_possibleEvents` (names, `environmentOverride`, radius bounds)? | Log the list at `OnWorldReady` |
| Does `PersistentEventSystem.instance` exist headless at all? | Log `PersistentEventSystem.instance != null` 30 s after world load |
| What is the zone prefab's `Heightmap.m_width`/`m_scale` (determines the required `s_TCData` vertex count)? | `ZoneSystem.instance.m_zonePrefab.GetComponentInChildren<Heightmap>().m_width` |
| What does a given paint `Color` in `s_TCData` visually render as? | Hoe-pave a spot on a client, read that zone's `s_TCData` server-side, inspect the modified `Color` |
| Does a relocated tame's saved-follow target actually re-attach after 500 m of teleport? | Relocate a wolf ZDO, watch it heel on the arriving client |
| Which sprite names exist in the compiled `Minimap.m_locationIcons` list (bounds every icon-only overlay)? | Enumerate on a client, log every `m_name` |
| Does overwriting `LocationIcons` per-peer actually move a vanilla client's respawn point? | Omit `"StartTemple"` for one peer, kill a bed-less test character |
| Does `RandEventSystem`'s spawn system honour a `SetRandomEventByName` call whose position is outside the event's normal biome? | Fire `army_goblin` in the Meadows, count spawns |
| Does `Chat.SendText` collapse to a solo broadcast only when `RelationsManager.PlatformRequiresTextFiltering()`/`RelationsProvider` is null? | Register a server-side `"ChatMessage"` handler, have one player type plain text and a shout, watch for a hit |
| Is `ZSyncTransform.m_characterParentSync` enabled on the `Player` prefab (gates every ship/mount-attachment read)? | Board a longship, read the character ZDO's `SyncTransform` connection and `s_relPosHash` server-side |

### Recommended build order

```
Phase 0  — Foundations (no dependencies)                          effort: S
  Portals As A Data Store · Portal Census · Game.PortalPrefabHash
  runtime discovery · Admin Command Channel

Phase 1  — Ownership & authority                                   effort: M
  ZDO.SetOwner Harmony guard (WaterBuoyancyEngine pattern)
  ↳ needed by: Ownership Pin, Managed Network Governor, Ferry Route

Phase 2  — Topology core                                           effort: M
  Pairing Authority Takeover / Managed Network Governor
  ↳ requires Phase 1 (ownership claim survives ReleaseNearbyZDOS)
  Phantom Anchor Fabrication (Void Anchor)
  ↳ requires Landing Pad Engine OR Skyfall/Seabed altitude table
    for a survivable arrival point

Phase 3  — Enforcement layer                                       effort: M
  Server-Enforced Portal Cap (SpawnGovernor hook pair)
  Ownership Pin + Destroy Veto + ungated-RPC hardening
  ↳ requires Phase 1

Phase 4  — Player-facing surfaces                                  effort: M
  Field Injection (corrected: SOURCE portal governs m_allowAllItems/
    m_exitDistance) · The Tag As A Public Broadcast Surface
  ↳ requires Phase 2 (tags are the pairing key — must own reconciliation
    before writing per-portal display text)
  Phantom Survival (m_canBeRemoved=false collapses most of this for free
    once Phase 3's Ownership Pin exists)

Phase 5  — World-reactive features                                 effort: M-L
  Ephemeral Event Gates · Ambush Gates · Event Beacon
  Per-Peer Location Icons
  ↳ each independently composes on top of Phases 2-3

Phase 6  — Motion & companions (optional, higher effort/lower ROI) effort: L
  Tame and Cart Follow-Through · Ferry Route · corrected Player's Ship
  ↳ requires reliable transit detection (Portal-As-Trigger, itself
    dependent on Phase 1/2) or accept PositionWatch's 2-3s latency

Phase 7  — Optional client companion                                effort: M
  ~200-300 line transit-prefix + RPC pair + non-enforcing VersionCheck
  ↳ strictly additive; every phase above degrades gracefully without it
```

Sector Zero, portal scaling, and the ruled-out entries above are not phases — they are either a one-line diagnostic to run once at boot (Sector Zero) or documented negatives to keep out of the design entirely.

### Verdict

TortalPortal Lite is worth building, but as a genuinely different product from the full client mod rather than a crippled version of it: the server owns the connection field, the tag string, existence and destruction of every portal, and — via `ZDOMan.CreateNewZDO`+`ZDO.Deserialize` — a cap enforcement the full client mod never achieved at all, which makes network topology, admin tooling, and world-reactive gating (event beacons, ambush gates, boss/global-key lockdowns) genuinely stronger built server-only than they were as Harmony patches on a client that any player could simply not run. What it cannot do — a real destination picker, live map pin sync, per-transit PIN prompts, inventory-aware tolls, and anything keyed on the exact moment of transit — is not a bug to work around but a hard wall traced to one call site (`TeleportWorldTrigger.OnTriggerEnter`, client-only, `Player.m_localPlayer`-gated) that no amount of ZDO cleverness crosses; every option in this catalog that claims otherwise degrades to a 0.2-3 s heuristic. Ship it as the topology/administration/world-governance layer it actually is, document the ruled-out list as loudly as the feature list, and treat the 200-300 line optional client companion in Phase 7 as the honest, labelled escape hatch for the handful of features that are structurally impossible any other way.
