## Economy, progression, and operations

### The enforcement ladder, restated as a design rule

Every feature in this domain resolves to one of four tiers, and mislabeling one tier as another is the single most common defect in the option catalog reviewed here. The tiers, from strongest to weakest:

1. **Server-enforced (route).** The rule decides whether an edge exists in the topology, and the server owns the write (`ZDO.SetConnection(ZDOExtraData.ConnectionType.Portal, target)`, assembly_valheim_SERVER.decompiled.cs:73656). `TeleportWorld.Teleport`'s first gate — `if (!TargetFound()) return;` (:143519) — is the only client-side check that reads server-authoritative state with no override path. A modified client cannot invent a destination that is not in the ZDO.
2. **Server-enforced against unmodified clients only.** The route decision is server-owned, but the *condition* it reads is a client-authored ZDO field (`s_items`, `s_fuel`, `s_item`, `s_state`) that reaches the server through `ZDOMan.RPC_ZDOData` (:77076-77140), which authenticates only the socket (`FindPeer(rpc)` :77078) and then blind-applies any packet whose DataRevision is higher (:77110-77131) — no ownership test, no field allowlist, no plausibility check anywhere in the path. A modified client can forge the condition without touching the RPC layer at all. This is the tier almost every toll/turnstile/fuel mechanism actually occupies, and the catalog originally mislabeled several of them "server-enforced" outright — see **Trust-Tier Correction** below.
3. **Client-honoured.** The rule is evaluated entirely on the traveling or interacting client (`GlobalKeys.NoPortals`/`NoBossPortals`/`TeleportAll`, `Inventory.IsTeleportable` :68860, `ZNetView.LoadFields` overrides). Exactly as trustworthy as vanilla's own boss gating — which is to say, honest clients get honest behavior and nothing else is guaranteed.
4. **Reactive-detection-only.** The server learns something happened after the fact (a position jump inferred as a transit, a ZDO field that changed with no matching RPC) and can only react — debit, debuff, park the *next* use, kick. It can never refuse the event that already happened.

The generative principle for every enforceable design in this section: **"should this ROUTE be open right now?" is answerable and ownable by the server; "should THIS PLAYER pass right now?" is not, because no server-side hook exists on the moment of transit** (`TeleportWorld.Teleport`'s sole call site is `TeleportWorldTrigger.OnTriggerEnter`, gated on `Player.m_localPlayer`, :143664-143682, and no portal GameObject ever exists server-side because `Game.FixedUpdate` pins the reference position to (1e6,0,1e6) every tick, :100458-100466). Every design below is a transformation of a player-shaped rule into a route-shaped one, or it is reactive, or it is ruled out.

A meta-note on provenance: the decompile in `libs-Tools/1.0/DECOMPILED/` is textually **Valheim 1.0.12** (`Version.CurrentVersion = new GameVersion(1,0,12)`, :112109; inlined network version literal `40u` at :42875/:97033), not 1.0.7 as the surrounding project documentation assumes throughout. Every citation below is 1.0.12-accurate; treat any "1.0.7" label elsewhere in this deep-dive as stale.

### Prerequisite: the Routing Kernel

Almost everything in this section requires taking pairing authority away from `Game.ConnectPortals()` (:100589-100627), because its phase 1 tears down any link whose partner's `s_tag` differs (:100601) and phase 2 randomly re-pairs anything left `None` (:100664-100679, `UnityEngine.Random.Range`). Three levels of takeover, cheapest first:

- **L1 — selection override.** Postfix the private `Game.FindRandomUnconnectedPortal` and overwrite `__result`. Vanilla still owns teardown, ownership handling, and its own two-phase commit (`m_currentlyConnectingPortals` / `Game.ClearCurrentlyConnectingPortals` :100690). **Does not decouple the tag from routing** — pass 1's tag-equality test still runs, so every managed group still needs one byte-identical tag.
- **L2 — full replacement.** Prefix `Game.ConnectPortals` to return `false` and run your own pass. Must reproduce the `IsCurrentlyConnectingPortal` guard (:100609/:100671) and must ALSO run at load time — `ZDOMan.LoadChunks` calls `Game.instance.ConnectPortals()` internally (:76523) before any `OnWorldReady` hook fires, since Wonderland's `OnWorldReady` is a `ZNetScene.Awake` postfix that runs *before* `ZNet.Start → ServerLoadWorld` loads any ZDO. Hook `ZDOMan.LoadChunks`/`Game.ConnectPortals` directly, not `OnWorldReady`.
- **L3 — coroutine stop.** `Game.instance.StopCoroutine("ConnectPortalsCoroutine")` (started by the string overload at :100074, so the string-keyed stop is Unity-API-valid — unverified in-game).

Write primitive (mirrors `Game.SetConnection` :100637-100651): `zdo.SetOwner(ZDOMan.GetSessionID()); zdo.SetConnection(Portal, target); ZDOMan.instance.ForceSendZDO(zdo.m_uid);`. **Correction to the naive claim that this is "free every tick":** `ZDO.SetOwner` bumps `OwnerRevision` whenever the current owner differs, and `ZDOMan.ReleaseNearbyZDOS` (:76901-76926) hands ownership of any portal within ~96-112m of a player back to that player every 2s — an unconditional per-tick `SetOwner` therefore flaps ownership and emits a packet every tick near every player. Gate the write on `GetConnectionZDOID(Portal) != target` first, exactly as vanilla's own `ForceSetConnection` does (:100628-100634); only then is the idempotent path (`ZDOExtraData.SetConnection` early-returns on an unchanged value, :74951-74961) actually free.

Once the Kernel owns pairing, `ZDOVars.s_tag` (:78617) decouples from routing and becomes a pure display string — the basis for Naming Rights, per-network tags, and status suffixes. Without the Kernel, every route in a managed group **must** carry one identical tag or vanilla's phase 1 severs it within 5s.

**Two hazards that apply to every design below, stated once:** (a) tag/field writes do **not** dirty the portal save chunk — only `ZDO.SetConnection`/`UpdateConnection` of type Portal do (:73661-73664, :76702); a rename or status-suffix write with no accompanying connection change is silently absent from the next save unless you call `ZDOMan.instance.SetDirtyPortals()` explicitly (:76702-76705). (b) non-pairwise topologies (rings, one-way links, self-loops, fan-in) are **runtime-stable but persistence-lossy**: `ZDOExtraData.RegenerateConnectionHashData` (:75461-75478) gives each ZDO exactly one hash slot, so a save collapses a ring to a partial pairwise matching (order-dependent, not "loses exactly one link"), turns a one-way edge bidirectional, and erases a self-loop outright. Any managed topology must be rebuilt from a position-keyed table in the same hook that replaces `Game.ConnectPortals`, every boot, before the first vanilla pairing pass runs.

### Economy and progression mechanisms

| Option | Enforcement | Feasibility | Core mechanism | Key citation |
|---|---|---|---|---|
| **Park and Release** | server-enforced (route) | confirmed | Unique-tag or pairing-veto disconnect via `SetConnection(Portal, None)`; `TargetFound()` gate 1 refuses silently | `TeleportWorld.Teleport` :143517-143521 |
| **Prepaid Toll Escrow** | tier 2 (client-authored balance) | confirmed-with-correction | Standing-balance chest read gates park/release; no debit, no clawback race | `Container.Save` :122294; `ZdoInventoryIO.Load` |
| **Token Turnstile** | tier 2 | confirmed-with-correction | One `s_item` int on an ItemStand is the whole gate condition | `ItemStand.UpdateAttach` :131842-131846 |
| **Charge Cells** | tier 2, downgraded | feasible-with-caveats | Fireplace `s_fuel` float burn-down | `ZDOVars.s_fuel` :78433 |
| **Reactive Transit Toll** | reactive-detection-only | confirmed-with-correction | Position-straddle detector bills after the fact | Wonderland `PositionWatch.IsPortalTransit` :148 |
| **Debtor's Lien / Personal Gates** | server-enforced (route, via property) | confirmed-with-correction | Park every gate whose `s_creator` matches the debtor | `Piece.SetCreator` :136415-136424 |
| **Progression-Gated Route Tiers** | server-enforced (route) vs modded clients only w/ RPC hardening | confirmed-with-correction | Route membership gated on `defeated_*`/custom global keys | `ZoneSystem.GetGlobalKey(string)` :115968 |
| **The Ore Gate** | client-honoured | confirmed-with-correction | `ZNetView.LoadFields` writes `TeleportWorld.m_allowAllItems` per-portal | `ZNetView.LoadFields` :82669 |
| **Physical Tiers (wards)** | server-enforced (route) for tiering; reactive for retag protection | confirmed-with-correction | Prefab/ward census near the portal sets tier | `PrivateArea.GetPermittedPlayers` :137349 |
| **Server Treasury / Tax Skim** | client-honoured spend, chest balance NOT trustworthy | downgraded | World-modifier key purchases funded by a server ledger | `Game.UpdateWorldRates` :101128 |
| **One-Way Trade Routes** | server-enforced at runtime, lossy at save | confirmed-with-correction | No-reciprocity-check exploit in `Game.ConnectPortals` phase 1 | :100600-100604 |
| **Distance / Biome Pricing** | server-enforced (route) vs vanilla; not vs `Game.RPC_SetConnection` | confirmed-with-correction | Pure `WorldGenerator` math, zero colliders | `WorldGenerator.GetBiome` :151725 |
| **Leases, Rent, Repossession, Caps** | server-enforced (route + creation) | confirmed-with-correction | Lease clock on ZDO + `ZDOMan.CreateNewZDO`/`ZDO.Deserialize` cap hook | `ZDOMan.CreateNewZDO` :76684 (private) |
| **Naming Rights and Prestige** | server-enforced (tag rewrite) | confirmed-with-correction | Kernel-owned tag as sellable, saved-pin-backed asset | `RPC_DiscoverLocationResponse` :100823 |
| **Event Topologies** | server-enforced (route) | confirmed-with-correction | Rotating hub / treasure chain / world-tour ring | `ZoneSystem.m_locationInstances` :113295 |
| **Route Auctions** | route award: server-enforced; bid input: tier 2 | confirmed-with-correction | Standing-balance ranking, no debit race | — |
| Point-of-transit enforcement (PIN, cargo tariff, per-player progression) | not enforceable | **ruled out** | No transit hook, no networked inventory, 2s-late detection | `TeleportWorldTrigger.OnTriggerEnter` :143664 |

#### Trust-Tier Correction (read this before shipping any toll)

The catalog originally labeled Prepaid Toll Escrow, Token Turnstile, Charge Cells, and Key-Item Requirement "server-enforced" while labeling The Ore Gate "client-honoured" — an inconsistent cut. All of them read a field the *owning client* wrote (`Container.Save` :122294, `Fireplace.RPC_AddFuel` :125726-125741, `ItemStand.UpdateAttach` :131842-131846, `Door.RPC_UseDoor` :123930-123952), and `ZDOMan.RPC_ZDOData` applies any such field with no ownership check and no plausibility check (:77076-77140; `ZDO.Set(int,int,bool okForNotOwner)` even ignores its own parameter, :73648-73654). The honest cut is tier 1 vs tier 2 as above: **what makes escrow/turnstile/fuel different from the Ore Gate is where the decision executes (server vs client), not who authored the inputs.** A modified client can forge a full chest, a maxed fireplace, or a trophy on a stand from anywhere, at any rate, with one ZDO packet.

Practical mitigation, in increasing cost:
- **Sender–Owner Binding Rule** (small effort): drop any ZDOData entry for a watched ZDO whose packet-declared owner isn't the sending peer. Every vanilla write path claims ownership first (`Container.RPC_RequestOpen` :122150-122152, `Sign.SetText` ClaimOwnership :141913-141928, `Piece.SetCreator` IsOwner gate :136417); a forger who skips the claim is caught for free. A forger who claims first passes — this rule alone is a speed bump.
- **Inline ZDOData Validator** (medium-large effort): Harmony-postfix `ZDO.Deserialize` (the same hook Wonderland's `SpawnGovernor` already uses, `SpawnGovernor.cs:58`) for a watched set of ZDOIDs (escrow chests, turnstiles, managed portals). Compare the just-arrived fields against the server's last-approved snapshot; if they disagree and the sender fails proximity/`s_inUse`/bounded-delta/owner-equals-sender checks, revert by re-writing the approved fields and bumping `zdo.DataRevision` past the incoming value by a margin (the WaterBuoyancyEngine `+= 4096` pattern, `WaterBuoyancyEngine.cs:158-164`), then `ForceSendZDO`. Because the peer's cached revision was already advanced by `RPC_ZDOData` before `Deserialize` runs (:77130), the revert reaches the forger's own client and any further push at or below that revision is silently discarded (:77110). This collapses the window from "one tick of forged state visible" to zero, but **cannot prove provenance** — a forger who fully emulates a legitimate session (walk to chest, set `s_inUse`, claim ownership, add one stack per second) still passes. Escalate repeat offenders via `AuditLog.Flag` + `ZNet.instance.Kick(hostName)` (:81658).

Never use a physical world chest as the balance *of record* for anything the server can't re-derive — see **Server Treasury** below for why.

#### Refund and reward primitives (server → client, without dropping items on the ground)

Two ways to pay a player back without `ItemDrop.DropItem`, both server-enforced writes into the player's own owned objects:

- **Station Credit Primitives.** Every fuel/ore station exposes an owner-gated ZNetView RPC the server can invoke exactly as it invokes `Game.RPC_SetConnection`: `ZRoutedRpc.instance.InvokeRoutedRPC(zdo.GetOwner(), zdo.m_uid, method, args)` (`ZNetView.InvokeRPC` :82855-82858). Usable RPCs: `Fireplace.RPC_AddFuelAmount(float)` (signed delta, clamped 0..m_maxFuel, :125754-125764), `Smelter.RPC_AddOre(string, bool)` / `RPC_AddFuel()` (no cap in the handler — clamp yourself first by reading `s_fuel`/`s_queued`, :142264-142359), `Fermenter.RPC_AddItem`, `CookingStation.RPC_AddItem` (not owner-gated — always target `GetOwner()` explicitly, :123130). Two-path rule: owned by a connected peer → route the RPC (player sees the vanilla add-effect); unowned → write the ZDO field directly, since there's no owner to clobber it.
- **Smelter Output Queue Credit.** Write `ZDOVars.s_spawnOre`/`s_spawnAmount` (:78605/:78603) directly; the owning client's own `Smelter.UpdateSmelter` (1s `InvokeRepeating`, owner-gated, :142407-142461) spawns the finished stack at the output chute on its next tick. **Correction:** this is inert, not merely deferred, on an unowned or zone-unloaded smelter — nobody runs `UpdateSmelter` until some player becomes owner and loads that zone, which per `ReleaseNearbyZDOS` may not be the credited player.

#### Status-effect feedback (reactive, but the best available UX for per-player consequences)

`StatusEffectRpc.Grant` (`ZRoutedRpc.instance.InvokeRoutedRPC(peer.m_uid, charZdo.m_uid, "RPC_AddStatusEffect", ...)`, owner-gated at `SEMan.RPC_AddStatusEffect` :28823-28829) grants any vanilla status effect. Verified against `Player.UpdateEnvStatusEffects` (:11619-11691), which force-removes environment-conditional effects every tick: **Wet, Frost, Burning, Poison survive a remote grant and run to their Inspector TTL** (re-pingable via `StatusEffect.ResetTime`, :31182-31185); **Cold, Freezing, Resting, Shelter, CampFire, Smoked are force-removed** and cannot be sustained this way. No removal RPC exists anywhere in either assembly — duration control is re-ping-until-expire only.
- **Portal Sickness**: grant Wet/Frost/Puke on a detected transit as the felt cost of "cooldown," 2-5s after entry.
- **Arrival Blessing**: grant `s_statusEffectRested` on arrival at a hub; `SE_Rested.UpdateTTL` recomputes from the traveler's comfort at the *arrival* point (:29980-29989), so re-pings can't extend past what the destination's comfort supports.
- **Per-Player Cooldown Made Visible**: keep the route open for everyone, stamp the recent traveler with a marker debuff whose remaining-time bar is the only channel a vanilla client has for a per-player timer.

#### Physical I/O composed into the economy

Three primitives read/write ordinary building pieces as toll infrastructure with no new RPC surface:
- **Door Lever** (input): `Door.RPC_UseDoor` writes `ZDOVars.s_state` (:78611) to 0/+1/-1 based on which side the player opened it from (:123930-123952) — a three-position selector a vanilla player operates by walking around a door. Gated by the door's own `m_checkGuardStone` (:123741) for free ward-based access control.
- **Server Portcullis** (output): the server writes `s_state` directly (no ownership check on `ZDO.Set`, :73648-73654); every nearby client's `Door.UpdateState` (0.2s poll, :123773-123785) animates it. Combine with ownership-hold (WaterBuoyancyEngine's `ZdoSetOwnerPatch` pattern) to make the door unopenable by anyone but the server.
- **Vault Seal**: while the server holds ownership of a chest, `Container.RPC_RequestOpen` (:122134-122157) routes to a server-side `ZNetScene.FindInstance` that is always null (the dedicated server never instantiates real-world-positioned GameObjects, per the reference-position pin) — the request silently evaporates and the GUI never opens. Use to freeze a chest during settlement.

#### Presence and property, not identity

Because there is no server hook on the moment of transit, every "per-player" rule that actually works is really a rule about **property** (portals the player built, per `s_creator`) or **presence** (whether that player is currently online), never about the traveling player directly:

- **Presence-Wired Routes**: a route is wired only while a member of its owner network (`s_creator` set, optionally unioned with ward `pu_id<i>`) is online, via `ConnectedCharacters.All()` and `Game.SetConnection` semantics (:100637-100651). `Game.RPC_SetConnection` is a *global* routed RPC (registered via `ZRoutedRpc.instance.Register`, not per-ZNetView), so its delivery is immune to the `ZNetScene.FindInstance` limitation that blocks per-object RPCs on a dedicated server.
- **Offline-Raid Shield**: the inverse — routes *into* a base are unwired while its owners are all offline (raiders find gate 1 silently refusing, per `TeleportWorld.Teleport` :143519), re-wired the instant a member's character ZDO shows `s_playerID != 0`.
- **Escort Gate**: a route is wired only while an owner stands within N meters of either end, using `ZSyncTransform.OwnerSync`'s distance-independent position push (:87527-87534) at ~50-100ms freshness.

All three require the Routing Kernel (or a Customs House anchor topology vanilla's reconciler ignores) and inherit the standard warm-up caveat: `ForceSendZDO` the destination to the traveling peer on every re-open, or the first walk-through silently fails while `TargetFound()`'s `RequestZDO` round-trip completes (:143640-143655).

#### Downgraded/refuted from the original catalog

- **Server Treasury**: the "chest nobody can open" framing is **refuted**. `piece_chest` is `PrivacySetting.Public` (`Container.CheckAccess` :122090-122099), and `ReleaseNearbyZDOS` hands any nearby vanilla client ownership within 2s, after which `RPC_RequestOpen` grants it the GUI. The treasury's balance of record must be a server-side ledger (`ItemLedger`-style), never a chest's `s_items`; a world chest may *mirror* the ledger for flavor but the server must overwrite it every tick. Spend targets (`GlobalKeys.TeleportAll`, custom `mod_route_*` keys) remain client-honoured exactly like every other global key.
- **Point-of-transit enforcement** (PINs, cargo tariffs, per-player progression gates, refunds from a living inventory): **ruled out**, four independent reasons — no transit hook exists server-side; even a ZDO-targeted RPC never dispatches (`ZNetScene.instance.FindInstance` is permanently empty, :82229); a player's carried inventory is never networked (`Player.Save` → local `.fch` only, :14091, :105706; `ZNet.SaveOtherPlayerProfiles` collects nothing, :79653); and detection is ≥2s late, after the transit is irreversible. One caveat the original ruling underclaimed: *equipped* items (weapons, armor, back slots) **are** networked via `VisEquipment` (`s_rightItem`/`s_leftItem`/etc.), so a server can see what a player is wearing — never what's in the pack, and never enough to reproduce `Inventory.IsTeleportable`'s ore check.

### Operations: enumeration, health, repair, audit

#### PortalCensus — the one snapshot everything else reads

A 1Hz main-thread pass over `ZDOMan.instance.GetPortalList()` (:77648-77656, flattens the always-resident `m_portalObjects` dictionary — portals are never unloaded, `ReleaseZDOS` only reassigns ownership :76872-76885) builds an immutable DTO: `{position, tag, tagAuthor, connection, owner, creator, biome}` per portal. Rules that prevent every downstream bug in this section:
- **Key by rounded world position, never ZDOID** — `ZDO.Load` renumbers every id on every world load (:74552).
- **Never cache a `ZDO` object reference across ticks** — `ZDOPool.Release` resets `m_uid = ZDOID.None` (:73536-73545); cache the ZDOID and re-resolve with `ZDOMan.instance.GetZDO(id)` every tick.
- **`s_tagauthor` and `s_creator` are both client self-reports**, useful only as "probable," never "confirmed" attribution (`TeleportWorld.RPC_SetTag` never cross-checks `authorId` against the sender, :143587-143602; `Piece.SetCreator` runs on the placing client, :136415-136424).

#### Health scan: pathologies unique to a pairwise-matching persistence layer

| Finding | Cause | Correction to naive model |
|---|---|---|
| Odd-count strand | N≥3 same-tag group leaves ⌊N/2⌋ pairs + 1 permanent orphan (`FindRandomUnconnectedPortal` :100664-100679) | Orphan is chosen by `Random.Range`, **not** dictionary order, and is **stable across restarts** if the pairing was ever saved — `ZDOMan.ConnectPortals` (load-time, hash-based, :77850-77893) restores the saved pair before any random re-pick |
| Dangling connection | Partner destroyed; cleared within 5s unless partner is player-owned, then deferred through `RPC_SetConnection` | Report only after 2 consecutive scans |
| Cross-tag link | Vanilla tears down within 5s regardless | Informational only |
| Self-loop / one-way / ring | Runtime-stable, save-lossy | See "two hazards" above |
| Duplicate tag (case/whitespace) | `GetString` comparison is ordinal (:75079-75082) | `"Base"` ≠ `"Base "` ≠ `"base"` |
| Sector duplication | Only from a mod repositioning a portal ZDO (`ZDO.SetSector` no-ops for portals, :73752-73756) — never happens in pure vanilla | Mod/admin-induced only; filter stale bucket entries by `IsValid()` (the leaked entry references a `ZDOPool`-released object) |

Repair actions reuse the Kernel's write primitive; **never merge a 1-member group into an existing even-sized pair** (creates a fresh odd-count strand) — merge only into a size-1 or already-odd group.

#### AuditEngine — attribution, honestly labeled

Three sources, one complete, two partial: (1) census diff, the only complete source, since an owning client's `RPC_SetTag` never crosses the wire when `targetPeerID == m_id` (`ZRoutedRpc.InvokeRoutedRPC` local-dispatch branch, :83577-83595) — the change still reaches the server as ordinary ZDO data via `ZDOMan.ClientChanged` (:77727-77730); (2) a `ZRoutedRpc.RPC_RoutedRPC` prefix (:83632-83643), the only point where the authenticated `ZRpc` and the payload coexist — but this sees **attempts**, not confirmed outcomes, since the RPC can still be dropped server-side with no client applying it; (3) `ZDOMan.m_onZDODestroyed` (public `Action<ZDO>`, :76033, fired before removal at :76975) for deletions — `Delegate.Combine`, never assign, since `ZNetScene.Awake` already subscribes its own handler (:81953). Tag `actorConfidence: confirmed` only when a matching RPC crossed the wire; `probable` for a census-only diff resolved to the nearest connected character; never present "probable" as fact in a player-facing channel.

#### Config: network topology as a hot-reloaded JSON file

BepInEx's flat `key=value` `.cfg` cannot express nested member lists; keep scalars in the `.cfg` and topology in a separate polled file:

```jsonc
// BepInEx/config/TortalPortalLite.networks.json
{
  "schema": 1,
  "defaults": { "reassertSeconds": 2.0 },
  "networks": [
    { "id": "merchant-ring", "topology": "ring", "tag": "TRADE",
      "members": [
        { "name": "Haldor",   "at": [1240.5, 31.2, -3310.0], "radius": 6.0 },
        { "name": "Docks",    "at": [-210.0, 33.9, 180.0] },
        { "name": "Mistvale", "at": [4420.0, 95.4, 2180.0] }
      ]},
    { "id": "spawn-hub", "topology": "hub", "tag": "HUB",
      "hub": { "at": [0.0, 32.0, 4.0], "radius": 8.0 },
      "spokes": [ { "at": [-980.0, 30.5, 1420.0] }, { "at": [2600.0, 41.0, -700.0] } ] }
  ]
}
```

Members resolve by nearest-portal-within-`radius` at load, never by ZDOID. Validate before swap (unique id/tag, no member beyond the `SectorToIndex` clamp at ±16.3-16.4km per axis, :115755-115774) and keep the previous model on any error. **One-way and ring topologies require the L2/L3 Kernel** — displaying distinct per-member tags for readability defeats vanilla's tag-equality reconciler otherwise.

#### Command channel: what actually works

The catalog's original "server-registered ConsoleCommand" design is **refuted**: a vanilla client's `Terminal.TryRunCommand` looks the typed word up in its *own* static `commands` dictionary first (:45734-45757) — a mod-only name like `tplite.list` is rejected locally as "not a recognized command" and never reaches the wire. Worse, a vanilla dedicated server has **no stdin reader at all** (zero `Console.ReadLine`/`StandardInput` hits across all four decompiled assemblies) — `ZNet.RPC_RemoteCommand` (:81879-81895) is, and has always been, the *only* console channel a dedicated server has ever had.

The working design is a **piggyback**: an admin types `removekey tpl <verb> …` in their own F5 console or in ordinary chat (`Chat.InputText` strips a leading `/` and forwards with `silentFail:true`, :41947-41955 — `isAllowedCommand` only blocks cheat commands, :41938-41945). `removekey` is `isCheat:false, onlyServer:true, remoteCommand:true` (:43535-43546), so a vanilla client forwards it verbatim via `ZNet.instance.RemoteCommand(text)` → `RPC_RemoteCommand`, which is admin-list-gated on the socket-verified host name (:81879-81889, same check as `RPC_Kick`) before `Console.instance.TryRunCommand` runs. A Harmony prefix on `ZNet.RPC_RemoteCommand` intercepts the reserved carrier, dispatches the verb table, replies via `ZNet.RemotePrint` (:81635-81656 → client `RPC_RemotePrint` → `Console.instance.Print`), and returns `false` so the carrier never reaches `RemoveGlobalKey` (whose mod-absent fallout is a provable no-op: `GetKeyValue` splits on the first space, finds no key named `tpl`, returns `false` — :113516-113539). Never call `ZNet.instance.RemoteCommand` *from* the server — its `IsServer()` branch passes a null `rpc` into `InternalCommand`, which dereferences `rpc.GetSocket()` unconditionally and throws (:81869-81895).

A parallel **file-queue channel** (poll `File.GetLastWriteTimeUtc` on a watched `.cmd` file every 1s, exactly as `WonderlandPlugin.PollConfigFile` already does, `Plugin.cs:97`) gives cron/ssh scripting with zero in-game presence, executes on the main thread (ZDOMan has no locking — vanilla's own save thread only ever touches a main-thread-prepared clone, `ZDOMan.PrepareSave` :76197-76205), and writes results temp-then-`File.Replace` to a sibling `.cmd.out`.

#### Output surfaces

- **JSON/CSV export**: temp-then-`File.Replace` (not delete-then-move — that leaves a window with no file), triggered from `ZNet.WorldSaveStarted` (public static `Action`, :78989, fired on the main thread before `PrepareSave` at :80524) so the export always matches the world file. Key every row by rounded position, never ZDOID.
- **Discord webhook**: lift Wonderland's `DiscordWebhook` verbatim (one static `HttpClient`, fire-and-forget `Send`, `SendBlocking` for the shutdown message only). Subscribe to `ZDOMan.m_onZDODestroyed` with `+=`, never `=`.
- **HTTP endpoint**: **needs-ingame-check**, not ruled out. No `HttpListener` reference exists in any game assembly, but `System.dll` ships in the server's Managed folder and `ZNet.GetPublicIP` (:79390-79420) proves *outbound* `HttpClient`/TLS/thread-pool sockets work headless — inbound listening on Unity's Mono is simply unverified. Probe at boot with a self-connect; fall back to the file export if the probe fails. Serve only an immutable snapshot built on the main thread; never touch ZDOMan from the listener thread.
- **SVG realm map**: pure string-building over `WorldGenerator.GetBiome`/`GetHeight` (closed-form, no colliders — every `ZoneSystem` raycast helper is dead server-side because of the reference-position pin) or the precomputed `AltBiomeWorldData` grid (`assembly_valheim_SERVER.decompiled.cs:92332`, populated by `ZNet.ServerLoadWorld` before any peer connects, :79283). PNG via `Texture2D.EncodeToPNG` is CPU-side, not GPU-blocked (`ImageConversionModule.dll` ships in Managed) — worth a boot probe rather than ruling it out.

#### Save-footprint correction (the most likely shipped bug in this design)

Portals are cloned wholesale into the dedicated `ChunkPortal` file whenever `DirtyPortalObjects` is set, with **no** `Persistent` filter (`ZDOMan.GetSaveClonePerChunk` :77621-77636) — but that flag is set **only** by `ZDO.SetConnection`/`UpdateConnection` of type Portal, `AddIfPortal`, and `HandleDestroyedZDO` (:73661-73678, :77750, :76986). A plain tag or mod-key write goes `ZDO.Set → IncreaseDataRevision → SetDirtySector`, which dirties an *ordinary* chunk the portal is no longer filed in (portals are removed from the sector array by `AddIfPortal`, :77732-77752) — the write is live in memory and **silently absent from the next save**. Every non-connection portal write (renames, status suffixes, GC-tag removal, lease timestamps) must be followed by an explicit `ZDOMan.instance.SetDirtyPortals()` call.

#### Uninstall behavior

No corruption, no phantom portals — mod-created portal-prefab ZDOs load through `LoadChunks`'s portal branch with no `FilterZDO` call and behave as ordinary vanilla gates (:76474-76482). What is lost, silently, at the **first restart** after removal: any non-pairwise topology, per the hazard above — a ring's surviving pair is order-dependent, not "one pair guaranteed"; one-way links come back bidirectional; self-loops vanish. Unknown-prefab anchor ZDOs are kept (not reaped) on ordinary boots — the "unsupported prefab" warning only fires on a world-version-change load (`FilterZDO`'s `newVersion` gate, :76346-76361) — so a non-portal anchor with a fabricated hash persists silently rather than spamming logs every boot. Ship `tplite.uninstall`: snapshot, split every odd-sized managed group as evenly as possible (an odd count cannot be eliminated, only made explicit), strip mod keys, destroy mod anchors (claim ownership first — `DestroyZDO` is a silent no-op otherwise, :76929-76935).

#### Compatibility and performance at scale

- `ZDOMan.m_onZDODestroyed`, `ZDO.SetOwner` prefixes, `Game.ConnectPortals` patches, `Terminal.commands`, and `ZRoutedRpc.Register` are the five shared mutable surfaces every ZDO-touching mod fights over. Combine delegates, fail-fast on prefab hash before any owner-change veto logic, and never register a vanilla RPC name (`Register` uses `Dictionary.Add` and throws on collision).
- **Portal-specific queries are O(portal count), independent of world ZDO count** — `GetPortalList()` flattens only `m_portalObjects`. But **spatial queries do return portals**: `ZDOMan.FindObjects`/`FindSectorObjects` merges `m_portalObjects[sector]` into every result (:77367-77382), so `ReleaseNearbyZDOS`, `CreateSyncList`, and any `ZdoSpatialQuery.FindNear` call all see portals too — a common false assumption is that portals are invisible to sector-based code; they are not, only `GetPortalList()`/`GetPortals()` are the *portal-only* shortcut.
- `Game.instance.ServerLog`'s sent/recv counters are 1-second samples (`m_zdosSentLastSec`, reset every second, :76829-76838), not cumulative totals — useless as a before/after regression baseline across a 10-minute log interval; instrument engine tick cost directly instead.
- Non-portal prefab-wide scans (`GetAllZDOsWithPrefabIterative`, `ZDOExtraData.GetAllZDOIDsWithHash`) are boot-time-only: the former walks a 512×512 sector array at ~400 non-empty sectors per call, the latter is an unindexed nested walk over every ZDO's field dictionary.

#### Subsystem architecture (mirrors Wonderland's registry/heartbeat shape)

One plugin, one `PortalOpsSubsystem`, engines with disjoint write ownership in a fixed intra-tick order: `PortalCensus` (1.0s, read-only) → `NetworkModel` (file-change-driven) → `AuditEngine` (reads the change stream before this tick's writes) → `NetworkReassertEngine` (2.0s, the **only** writer of `s_tag`/`Portal` connection) → `HealthScanEngine` (30s, read-only) → `RepairEngine` (on-demand, dry-run default, writes only through the reassert primitive) → `MetricsEngine` (3s sample / 60s aggregate) → `ExportEngine` (300s + on `WorldSaveStarted`) → `CommandEngine` (piggyback + file queue, owns no state). Copy Wonderland's `Plugin.cs`/`SubsystemRegistry.cs`/`Heartbeat.cs` verbatim, wrap every Harmony patch set in `SubsystemRegistry.SafePatch` (degrades a signature break to a logged warning instead of killing `Awake`), and retry `ObjectDB`/`ZNetScene` prefab reads from `OnUpdate` until they resolve rather than trusting them at `OnWorldReady` (`BuffRosterEngine.cs:83-96` precedent).

Two supporting engines round out the lifecycle: **OnJoin Briefing** hooks `ZRoutedRpc.m_onNewPeer` (public field, fired from `ZRoutedRpc.AddPeer`, :83531-83533 — no Harmony needed, the same hook `ZoneSystem` uses to push global keys to new peers) to force-send managed anchors to a joining peer (guaranteed to transmit — a fresh `ZDOPeer.m_zdos` is empty so `ShouldSend` is unconditionally true, :76003-76015), eliminating the classic "first transit after reconnect silently fails" complaint, and to toast the player's own portal health once their character ZDO's `s_playerID` resolves. **CapabilityProbe** runs once at `OnWorldReady` (retried per-tick until singletons resolve) and logs every Inspector-data fact this whole domain depends on but the decompile cannot answer — `Game.m_portalPrefabs` contents, per-prefab `ZSyncTransform`/`ZNetView` flags, `guard_stone.PrivateArea.m_radius`, routed-RPC registration table, `ZoneSystem.IsActiveAreaLoaded()` at the pinned reference position — publishing a `Capabilities` record every other engine reads to self-disable with a logged reason rather than silently no-op.
