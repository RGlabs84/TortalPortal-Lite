## Network topologies

### The one fact everything below depends on

`Game.ConnectPortals()` (assembly_valheim_SERVER.decompiled.cs:100589-100626) is the only reconciler that runs at play time. Pass 1's self-heal predicate, evaluated against the *target* only:

```
target == null || target.GetString(s_tag) != mine || target.GetConnectionZDOID(Portal) == ZDOID.None
```

(:100600-100604) — reciprocity is never checked. Pass 2 (:100607-100622) pairs only portals whose own connection `IsNone()`, via `Game.FindRandomUnconnectedPortal`'s `UnityEngine.Random.Range` pick from same-tag candidates (:100664-100679). This is a **staged two-tick commit**: pairs formed by pass 2 sit in the private `Game.m_currentlyConnectingPortals` (:99903) and are force-committed at the *start of the next* call by `ClearCurrentlyConnectingPortals`→`ForceSetConnection` (:100690-100698) regardless of what a mod wrote in between. A rebuild that doesn't clear or out-live this staging list gets vanilla-staged pairs reasserted one tick later.

No reciprocity check is what makes fan-in, rings, chains, self-loops and anchors all fixed points of vanilla's own coroutine with zero patches.

**The save-boundary wall.** None of it survives a save. `ZDOExtraData.RegenerateConnectionHashData` (:75461-75478) gives each ZDO exactly one `{type,hash}` slot — a pairwise perfect matching, full stop, last write wins. On load, the *private, load-time-only* `ZDOMan.ConnectPortals()` (:77850-77893 — unrelated to `Game.ConnectPortals`) reconnects only hash-matched pairs reciprocally, called synchronously inside `ZDOMan.Load`/`LoadChunks` (:76523, :76658) *before any mod tick runs*; survivors are then randomly re-paired by `Game.instance.ConnectPortals()` at that same call site. **Every shape except the plain pair and the portal bank must be rebuilt on every load**, early enough to beat this call — a `Game.ConnectPortals` postfix applied before world load, not an `OnWorldReady` hook.

ZDOIDs are renumbered on every load (`ZDO.Load`, :74552) — never key mod state by ZDOID; use position or a mod-private ZDO field (round-trips through save for free, `ZDO.Set`/`GetString` :73721/:73967).

Write recipe used throughout: `zdo.SetOwner(ZDOMan.GetSessionID()); zdo.SetConnection(Portal, target); ZDOMan.instance.ForceSendZDO(zdo.m_uid);` (`Game.SetConnection`'s local-write branch, :100637-100651). Ownership does **not** win DataRevision ties — `RPC_ZDOData` (:77076-77135) accepts any strictly-higher-revision packet regardless of owner; `SetOwner` only bumps `OwnerRevision` (:74627-74631). Its real effect is narrower: while the server owns a portal, a client's `RPC_SetTag` (routed to the owner) is dropped since no `ZNetView` instance exists server-side (:83646-83663) — but `ReleaseNearbyZDOS` (:76901-76926, every 2s) hands ownership back to any nearby peer, so this is transient, not a lock.

### Comparison table

| Shape | Feasible | Enforcement | Effort | Survives reload | Survives uninstall |
|---|---|---|---|---|---|
| Simple pair | yes | server-enforced (route) | small | yes (native) | yes |
| Fan-in star | yes, caveats | server-enforced (route) | small-med | no — rebuild | degrades to pairs |
| Fan-out (simultaneous) | **ruled out** | — | — | — | — |
| Directed ring | yes, caveats | server-enforced (route) | small-med | no — loses links | degrades to random pairs |
| Bidirectional ring | ruled out; twin rings substitute | server-enforced (route) | medium | no (inherits ring) | degrades |
| Chain + terminator | yes, caveats | server-enforced (route) | medium | no (terminator-dependent) | degrades |
| Self-loop dead end | yes, caveats | server-enforced (route) | small | **no** — erased | reverts |
| Anchor-terminated one-way | yes, caveats | server-enforced (route) | medium | conditional (mutual link) | anchor persists inert |
| Directed in-tree | yes, caveats | server-enforced (route) | medium | **no** — collapses | scrambles tree |
| Nested airlock | yes, caveats | server-enforced (route only) | medium | no | **not a security boundary** |
| Portal bank | yes | server-enforced (route) | small | yes (pairwise) | yes |
| Switchboard | yes, caveats | server-enforced (route) | medium | no | reverts to a pair |
| Carousel | yes, caveats | server-enforced (route) | small | no | reverts |
| Invisible tag sharding | yes, caveats | server-enforced (route) | small | yes | tags visible in edit box |
| Per-player networks | yes, caveats | route server-enforced, client-asserted identity | medium | yes | degrades |
| Faction networks | yes, caveats | route server-enforced, client-asserted identity | medium | yes | degrades |
| Tag-scramble lockdown | yes | server-enforced (route) — **not** vs. a client's own movement | small-med | yes (dangerous) | **must restore first** |
| Black-hole sink | yes, caveats | server-enforced (route) | small | no unless per-group anchors | scrambles if tag-merged |
| Roulette / shuffle | yes | server-enforced (route) | small | yes | yes |
| Displaced routing | yes | server-enforced (route) | med-large | mod is the source of truth | **total collapse** |
| Asymmetric round-trip | yes, caveats | server-enforced (route) | small-med | no | degrades |
| Per-player arbitration on a hub | **ruled out** as true per-player | — | — | — | — |

"Server-enforced (route)" means: the server owns the ZDO field `TeleportWorld.Teleport`'s gate 1 reads — `if (!TargetFound()) return;` (:143519-143522), no cache, silent failure on a null target. It never means the server controls *who* teleports or *when*: no such hook exists (`TeleportWorldTrigger.OnTriggerEnter`, gated on `Player.m_localPlayer`, is the sole caller, :143672-143681, unreachable server-side because `Game.FixedUpdate` pins the reference position to (1e6,0,1e6) every tick, :100458-100466). A modified client always moves itself with `Player.TeleportTo` or writes ZDO data through unchecked `RPC_ZDOData` (:77076-77139) — "server-enforced" is a claim about the *route*, never the *player*.

### Simple pair

```
[A] <== tag T ==> [B]
```

`A.SetConnection(Portal, B.uid)` + mirror, one tag. Pass 1 finds both non-null/tag-equal/non-None — zero-cost fixed point (`SetConnection` no-ops on an unchanged value, :74951-74960). The only shape needing no rebuild-on-load logic at all — it's what vanilla saves natively.

Tag comparison is bare `string !=` (:100601), ordinal, and default `""` (never null, :75079-75082) puts every untagged portal in one group. Three-or-more same-tag portals strand exactly one member permanently, chosen by bucket order not stable across restarts.

Hover reads `$piece_portal $piece_portal_tag:"forest" [$piece_portal_connected]` (client:144192-144203; **not** "Teleport tag:…" — `GetHoverName` is the separate literal "Teleport", client:144205). Arrival is a full 8s-minimum distant teleport (client:15331-15380); re-entry needs the 2s cooldown (client:15315) and, since `OnTriggerEnter` is enter-only, physically stepping out first.

### Fan-in star

```
S1,S2,S3,... --> [HUB] --> Sr (designated return / anchor)
```

Every spoke's target (the hub) is non-null/tag-matched/non-None regardless of what points at the hub — no reciprocity check means the hub never needs to know about any spoke.

**Does not survive a save.** The single hash slot means at most one spoke↔hub pair reconstructs; the rest come back `None` and are randomly re-paired by the load-time `Game.instance.ConnectPortals()` call before any mod code runs. A `Game.ConnectPortals` postfix must rebuild the star immediately **and clear `Game.m_currentlyConnectingPortals`**, or the staged commit reverts it one tick later.

A vanilla client owning the hub can retag it with **zero packets on the wire** (`InvokeRoutedRPC` dispatches locally when `targetPeerID == m_id`, :83588-83594) — poll `s_tag`, don't rely on RPC interception. Destroying the hub strands every spoke on the next pass, then pass 2 randomly re-pairs spokes with each other; subscribe to the public `ZDOMan.m_onZDODestroyed` (:76033, fires at :76975 before removal) to re-provision immediately.

### Fan-out star (one hub → many simultaneously) — RULED OUT

Structural, not missing. `ZDOExtraData.s_connections` is `Dictionary<ZDOID, ZDOConnection>`, and `ZDOConnection` has exactly one `readonly ZDOID m_target` (:75524-75534). `ConnectionType` is `{None=0, Portal=1, SyncTransform=2, Spawned=3, Target=0x10}` (:74747-74755) — the small values overlap, so you cannot OR two route types together, and writing `Portal|Target` makes `GetConnectionZDOID(Portal)` return `None` (:75094-75102), which vanilla then treats as unconnected. `TeleportWorld.Teleport` reads exactly one ZDOID with no fallback or picker (client:144282-144293). Every "one gate, many destinations" design here must be a **Portal Bank**, a **Switchboard**, or a **Carousel** — never a single ZDO.

### Directed ring / cycle

```
P0 --> P1 --> P2 --> ... --> P(n-1) --> P0
```

Every node's successor is non-null/tag-matched/non-None — stable for any N (N=2 degenerates to the plain pair; N=1 is the self-loop below).

Save collapse is severe: each node's single hash slot is overwritten across the ring's edges, so **at most one reciprocal pair survives** and the rest are randomly re-paired at load. A mandatory rebuild, keyed on position/string not ZDOID, must run before any player connects.

Never reposition a ring member: `ZDO.SetSector` early-returns for any portal-prefab hash (:73752-73756), so the ZDO stays filed under its creation-time sector in `m_portalObjects` forever, and `HandleDestroyedZDO` (which looks it up by the *current* sector) can never remove it — a permanent leak that can alias a `ZDOPool`-recycled object (:76987, :78286-78311). Destroy and recreate instead.

Five one-way Mistlands stages, tag `descent`: walking into stage 3 always advances to stage 4, never back. Each hop costs the full ~10s (8s hold + 2s cooldown before the next `TeleportTo` is even accepted, client:15315, :15351).

### Bidirectional ring — RULED OUT; twin counter-rotating rings substitute

A node can't hold clockwise and anticlockwise targets simultaneously (same single-slot limit). Substitute: two independent Directed Rings on distinct tags `T_cw`/`T_ccw`, a portal pair per station, `2N` portals total. Because `FindRandomUnconnectedPortal` filters on exact tag equality (:100664-100679), the rings never bridge unless a player retags one ring's portal to match the other. Inherits every Directed Ring caveat, independently per ring.

### Open chain / relay with a terminator

```
P0 --> P1 --> ... --> Pn --> [terminator]
```

An unterminated tail does **not** unravel "one hop per 5s" — pass 1 and pass 2 run in the *same call*, so when `Pn` is `None`, pass 1 nulls `P(n-1)` and pass 2 immediately pairs `P(n-1)` with the also-`None` `Pn` in the same tick — vanilla installs its own bounce terminator, bounded to 1-2 tail hops. Still undesirable; the terminator requirement stands for a milder reason than total collapse.

Three terminators, most robust last: **self-loop** (runtime-stable, erased at save — see below); **wrap to head** (this *is* the Directed Ring, inherits its save collapse); **anchor**, best choice — a non-portal-prefab ZDO is never enumerated by `m_portalObjects` (`AddIfPortal` filters on `Game.instance.PortalPrefabHash`, :77732-77752) so vanilla can never tear it down or re-pair it. All three still need rebuilding after load; write the anchor as a **mutual** partner (`Anchor.SetConnection(Portal, Pn.uid)`), not a self-loop, so it reconstructs order-independently like an ordinary pair.

Arrival lands at `target.pos + target.rot*forward*m_exitDistance + up*1.0`, where `m_exitDistance` belongs to the **entered** (source) portal, not the target (:143542-143545, field :143413) — a common mix-up. Because `OnTriggerEnter` is enter-only and the 2s cooldown gates re-entry, arrival inside the next trigger doesn't create an infinite relay.

### Self-loop dead end & paired decoy

`P.SetConnection(Portal, P.uid)`. Stable at runtime (all three pass-1 clauses resolve to itself). Client glows and hovers connected; `Teleport()` reads P's own position/rotation and moves the player roughly where they stood via a full 8s black screen for nothing (client:144282-144293, :15331-15380).

**Erased by every save.** `RegenerateConnectionHashData` writes the source slot then immediately overwrites it with the `Target` variant because `m_target == key` (:75461-75478), leaving no matching `Source`; `ZDOMan.ConnectPortals` on load requires an exact `Portal`-type slot (:75414-75425), so `P` reloads orphaned and is randomly re-paired inside its tag group before any mod tick. Must be re-asserted immediately every load, keyed on a mod-written string.

Use: a decoy room where N-1 identically-tagged portals are self-loops and one is the real exit — glow and hover are identical, though the real edge's first walk-through can silently fail if the client doesn't yet hold the far ZDO, a tell an attentive player could learn.

### Anchor-terminated one-way / dead drop

`ZDOMan.instance.CreateNewZDO(pos, nonPortalHash)` (:76674-76692) — the hash argument keeps it out of `Game.instance.PortalPrefabHash`, so `AddIfPortal` never files it in `m_portalObjects` (:77732-77752) and it is **permanently invisible** to `Game.ConnectPortals`. Replicate `ZNetView.Awake`'s five fabrication lines yourself, **`Persistent = true` first** — `AddToSector`/`SetDirtySector` both gate on it (:73496-73504, :76696-76702), and only dirty chunks save.

`Teleport()` performs zero prefab validation on its target, reading only position/rotation (:143539-143545); `TargetFound()` only needs the client to hold that ZDO, pulled on demand via `RequestZDO`→server `ForceSendZDO` if absent.

**Do not self-loop the anchor** — the single-hash-slot format can silently drop a self-loop's link depending on iteration order relative to whatever points at it. Write it as a **mutual pair** (`Anchor.SetConnection(Portal, P.uid)`) so it reconstructs order-independently. Never use hash 0 (spams "Missing prefab hash" on every nearby client's log, client:82225-82237) or a real destructible prefab without handling `m_onZDODestroyed`.

### Directed in-tree

```
leaf --> localHub --> regionalHub --> worldHub --> [terminator]
```

Recursive fan-in on one shared tag across every tier — every internal edge is stable; only the root needs a real (mutually-linked anchor) terminator. Because a ZDO holds one target, this is strictly an **in-tree**: travel only ratchets inward. Outward travel needs a Portal Bank at every hub level, doubling total portal count to `2N`.

Save collapse is the worst case in the catalogue for a naive build: one tag spanning the whole tree means a post-reload orphan at any level is a candidate for random pairing with *any other node anywhere in the tree*, not just its logical neighbor — the entire structure can scramble on one restart. Rebuild the complete tree from a string-keyed registry immediately after load, before any player connects, and run the re-assert as a *prefix* (not postfix) on `Game.ConnectPortals` so a mid-tick retag cascade (pass 1 reads live state while iterating) never gets a window.

### Nested / layered airlock (vestibule pattern)

```
O1..On (T_out) --> [V_in] --> Anchor
                       |
             B1..Bm (T_in_1..T_in_m, ordinary pairs)
```

Two tag namespaces `FindRandomUnconnectedPortal` can never bridge on exact string equality (:100669) — an orphaned outer portal can only re-pair with another outer portal.

This is **not an access-control boundary** in any real sense — it constrains vanilla's reconciler only. A player inside the vestibule can build their own portal there with any tag and vanilla pairs it within 5s, no server involvement; containment is scan-and-destroy (`GetPortalList()` filtered by position, `SetOwner`+`DestroyZDO`), purely reactive. A player owning an inner bank portal can retag it into `T_out`; `Game.RPC_SetConnection` (registered on every peer, zero sender validation, :100066/:100653-100662) lets any modded client rewire any node outright regardless.

"Closing a wing" is **not** a bare `Bk.SetConnection(None)` — pass 1 then nulls the twin too (its target's connection is None) and pass 2 re-stages the pair in the same tick. To show a dark, disconnected slot: null `Bk` **and** retag the remote twin out of `Bk`'s tag namespace so no candidate exists (`FindRandomUnconnectedPortal` returns `null` on an empty list, :100674-100678) — or accept the jail-anchor variant, which keeps the slot glowing/connected.

### Portal bank / departures hall

N ordinary vanilla pairs. Because each slot's tag is unique, every pair is a **perfect matching** and reconstructs identically across a save — alongside the plain pair, this is the one non-trivial shape needing no rebuild-on-load logic. Full mesh over S sites costs `S×(S−1)` portals (quadratic, all resident in RAM permanently — `ReleaseZDOS` only reassigns ownership, :76872-76883); the recommended real shape is the **hub+bank hybrid** — each site gets one outbound leg into a Fan-In Star at a hub, plus one hub bank slot per site for the return: `2S` portals, two hops, full any-to-any. Anti-retag protection is reactive-only (poll `s_tag`, revert, `SetDirtyPortals()`).

### Switchboard (player-repointed hub)

Fan-In Star with the hub's outbound leg rewritten per player request. Picked up on the very next transit — nothing caches the destination (:143539) — but the **first** transit after a repoint to a never-visited region silently fails at gate 1 (`TargetFound()` requires the client to already hold the target ZDO, :143646-143654). Always pair a repoint with a targeted `ForceSendZDO(peer.m_uid, Dk.uid)` before confirming success to the player.

Input channel: `Chat.SendPing` broadcasts `"ChatMessage"` at `targetPeerID=0` carrying the clicked world Vector3, from map middle-click/double-click/gamepad — reaches the server with no permission gate even solo (client:42058-42068; server dispatches `targetPeerID==0` locally, :83632-83644). Whether `Chat.Awake`'s `"ChatMessage"` registration is actually live on a dedicated server is **unverified**; if it is, `ZRoutedRpc.Register` throws on the duplicate hash (`Dictionary.Add`, :83666-83676) — the safe hook is a Harmony prefix on `HandleRoutedRPC` (:83646-83664), not a fresh Register. The ping's `y` is overwritten to the pinging player's own y (:42063) — match candidates in XZ only.

Single-writer hazard: the hub has one global connection; serialize concurrent requests with a short lock and toast the loser. Ownership is transient (`ReleaseNearbyZDOS` every 2s) — irrelevant to the write itself, which lands on any higher-DataRevision packet regardless of ownership.

### Carousel (timer-rotated hub)

Same mechanism, no player input — a timer writes the next destination every period `P`. Resolved at the instant of *transit*, not when the player commits to walking — there is no way to latch a destination to a specific player, since the connection is one global slot. Honest framings: a long period (≥5 min) with a server-written Sign (`ZDOVars.s_text`, :78629 — leave `s_author` empty, writing "host" null-derefs on the client, client:141866-141880) advertising the current destination accurately for nearly the whole period; or a short period as a deliberate randomizer, no promise attached.

Every rotation opens a dead window until the client's `RequestZDO` round-trip resolves — pre-broadcast `ForceSendZDO` on every rotation, not just to the nearest player. Idle re-assertion of the same index generates no traffic (no-op on unchanged value, :74954-74961).

### Invisible tag sharding

Routing compares the raw string (:100601, :100669); display strips rich-text first — `GetHoverText`'s `RemoveRichTextTags()` (:143459) is `Regex.Replace(text, "<[\/a-zA-Z0-9= \"''#;:()$_-]*?>", "")` (assembly_utils_SERVER.decompiled.cs:6700-6703). `base<f:red>` and `base<f:blue>` both hover as `base` and never pair with each other or with bare `base`.

**Correction:** the suffix is fully visible and editable in the **rename dialog** — `GetText()` returns the raw tag and `TextInput.Show` puts it straight in the field before applying the 10-character limit (:143551-143570, :61515-61521). "Invisible" applies to hover only; treat this as steganography for display, not secrecy. A player retag strips the suffix and clears the connection in one action — a normalization tick must reapply it within ≤1 tick, tolerating one staged-commit reversion. Whether `GuiInputField` clamps an over-limit programmatic assignment is unverified — needed before betting on long suffixed tags surviving a player's edit.

### Per-player private networks

`Piece.SetCreator` writes `ZDOVars.s_creator` (client-generated `PlayerProfile.m_playerID`, **per-character**) and `s_creatorIndex` (an index into the server-maintained, socket-verified `ZNet.World.m_playerHistory`, :112396) only when `IsOwner() && GetCreator()==0` (:136415-136424). A tick applies an Invisible-Tag-Sharding suffix keyed on this, then `SetDirtyPortals()` — a bare tag write never marks the portal chunk dirty on its own (portals excluded from ordinary chunk saves, :77572).

**Correction:** `s_creator` is per-character, not per-account — an alt gets a disjoint network unless the mod resolves through `s_creatorIndex → m_playerHistory[i].m_id` instead. Both are still client-asserted and accepted unvalidated by `RPC_ZDOData` — enforcement is server-owned at the route level over trusted (vanilla) identity, not authenticated identity. Portals with `s_creator==0` fall into a shared public namespace. Sharing is a one-line suffix rewrite plus an explicit `Game.instance.ConnectPortals()` call for immediate re-pairing.

### Team / faction networks

Same mechanism, roster-driven. Roster key must be `ZDOVars.s_playerID` (:10129-10136) or the socket-verified PlatformUserID — never `ZNetPeer.m_uid` (per-connection) or `m_playerID` (permanently 0; the `"PlayerID"` RPC is registered both sides but never invoked by any vanilla client, :80919-80927). Three resolution sources: a mod-private ZDO key; `s_creator` via roster; or a ward's `s_permitted` + raw `"pu_id"+i"`/`"pu_name"+i"` keys (`PrivateArea.SetPermittedPlayers`, :137338-137347 — not `ZDOVars` constants). All client-written, unvalidated; ward radius (`m_radius`, class default 10f, :137132) is unmeasured Inspector data. `RPC_ToggleEnabled`/`RPC_TogglePermitted` ignore the routed-RPC sender and act on a payload playerID (:137399-137421) — ward membership is a convenience input, not authorization. A cross-faction gateway inherits every Fan-In Star save/staging caveat; mass migration is a mass broadcast (budget per sweep — Wonderland's `StructureUpkeep` caps at 256).

### Tag-scramble lockdown (server-enforced portal shutdown)

Naive approaches fail: nulling every connection is undone by pass 2 within 5s; `NoPortals` is **client-honoured only** — read on the traveller's own machine (:143523-143527) — and `RPC_SetGlobalKey`/`RPC_RemoveGlobalKey` have zero sender validation (:116022-116037), so any client clears it with one packet.

Enforced version: stash each portal's original tag in a mod-private ZDO key, write a per-portal-unique suffix (a random code, **not** `m_uid.ID` — ZDOIDs renumber, :74552) so `FindRandomUnconnectedPortal`'s candidate list is empty for every portal (:100674-100678), then null every connection. Gate 1 fails unconditionally and *silently* (:143519-143522) — pair with a `PlayerNotify.Toast`, or it reads as a bug.

**Critical correction:** this does **not** stop a modified client from moving itself — `Teleport()` reads that client's own local ZDO copy; a hacked client calls `Player.TeleportTo` directly or writes ZDO data unchecked. The honest claim: cannot be lifted by any vanilla client, and cannot be cleared by the unauthenticated global-key RPCs.

Scrambled tags **persist through both a restart and a mod uninstall** — the most dangerous option in the catalogue for this reason. If removed mid-lockdown, every portal stays a singleton group forever, dead until someone hand-edits the (fully visible) suffix. Always restore in `Plugin.OnDestroy`, and stash *by group* — a bare `ConnectPortals()` call only produces *new random* pairs, not the originals.

### Black-hole sink (all portals → one destination)

Fan-In taken to its extreme — one anchor swallows unbounded portals at zero vanilla per-tick cost. **Do not rewrite every swallowed portal's tag to one shared value** — this is the worst save-boundary case in the catalogue: the anchor's self-loop is destroyed on save just like the plain self-loop, at most one pair reconstructs, and because every swallowed portal shares one tag the load-time random-pairing pass shuffles the *rest of the swallowed portals with each other* on the first restart. Prefer **one dedicated anchor per pre-existing tag group** (no tag rewrite) so a restart degrades to ordinary vanilla same-tag pairing instead of scrambling the network. The anchor must be genuinely `Persistent`; a real prefab must be non-destructible or handled via `m_onZDODestroyed`.

### Roulette / periodic shuffle

Zero-mod version: N portals on one tag, vanilla's own `UnityEngine.Random.Range` pairs them (:100664-100679), frozen once formed — this is the one dynamic-feeling shape that survives reload perfectly, since the resulting pairs *are* the native format. Forced re-roll: null every member, then call `Game.instance.ConnectPortals()` (public, :100589) for immediate re-pairing. `ConnectPortals()` opens by force-committing whatever vanilla staged on the *previous* pass — a re-roll fired within 5s of a genuine pairing event can partially restore old pairs first; schedule away from pairing bursts. Idempotent re-asserts generate no traffic; bump a mod nonce key if you need a guaranteed resend. For deterministic pairing, postfix the *private* `Game.FindRandomUnconnectedPortal` and overwrite `__result` — leaves the two-phase commit and vanilla's own logging intact.

### Displaced routing — own the pairing, free s_tag for display

```
Harmony prefix: Game.ConnectPortals() -> return false
Custom pass reads/writes a mod-private ZDO key; s_tag is now a pure label
```

The master enabler that dissolves nearly every constraint above. `Game.ConnectPortals` is `public` (:100589), started via a string-overload `StartCoroutine` (:100074, making `StopCoroutine("ConnectPortalsCoroutine")` a valid alternative). Note `ConnectPortals()` is *also* invoked directly from `ZDOMan.Load` (:76523, :76658) — the prefix must be installed before world load to cover that call.

Routing lives in a mod-private, persisted, arbitrary-length ZDO string; `RPC_SetTag` applies no cap at all (only the client widget does, :143482 vs :143587-143602), so display names can be long and distinct at every tier with zero routing consequence.

**Correction:** the *private* load-time `ZDOMan.ConnectPortals` still runs — the prefix only disables `Game.ConnectPortals` — so on every load the single-slot format still governs, and **the mod's persisted routing string is the actual source of truth that must be replayed after every load**, not a bonus. A vanilla client's `RPC_SetTag` still nulls the connection before writing the tag (:143587-143597), so a retag still tears down that portal's route for up to one mod tick even though the tag is now cosmetic — re-assert continuously, and remember tag-only writes never mark the portal chunk dirty on their own.

Mod uninstall is the **total collapse** case: vanilla resumes next boot, sees arbitrary directed edges with all-distinct tags, tears down nearly everything, randomly re-pairs the rest. Keep a sane fallback `s_tag` alongside the routing key and restore a vanilla-legal state in `Plugin.OnDestroy`.

### Asymmetric round-trip ("one-way door, long way home")

```
[A: home] --> [B: far] --> [C: home] --> [Anchor: home]
```

A three-edge chain closed by an anchor rather than ring structure, placed so `A`/`C` sit side by side at home while `B` is remote — `A` outbound, `B` return, `C` a dead arrival pad.

**Correction:** the claimed ability to "null or repoint the return leg independently, closing it while the outbound door stays lit" is **false under vanilla reconciliation** — nulling `B` makes pass 1 null `A` too (its target `B` has a `None` connection) in the same pass, then pass 2 re-pairs `A`↔`B` as an ordinary vanilla pair. Closing one leg of this shape requires pointing `B` at a dead end (self-loop, `[connected]`, 8s no-op hop) or Displaced Routing to disable pass 1 entirely.

Not save-stable: the single-hash-slot format either collapses to one bidirectional pair plus an orphan or loses all three edges outright, and `Game.instance.ConnectPortals()` at load re-pairs whatever's left at random before the mod's first tick. Effort is small-medium, not small — a position-keyed registry plus post-load rebuild plus retag-repair loop are required, not optional.

### Per-player destination arbitration on a shared hub — RULED OUT as true per-player routing

Two structural walls. **Storage**: one `ZDOConnection` per ZDO — no per-viewer state anywhere; `SendZDOs` serializes the identical blob to every peer (:77053-77062). **Timing**: `Teleport`'s sole caller is gated on `Player.m_localPlayer` and unreachable server-side, because `Game.FixedUpdate` pins the reference position to (1e6,0,1e6) every tick (server build only, :100458-100466) so an in-world portal is never instantiated there. The server cannot know who is about to walk in, and cannot set a destination "for them" first.

Legitimate approximations, each a different feature: a **proximity latch** (poll character ZDO positions via `ZNet.GetAllCharacterZDOS`, :81024-81044 — lock the hub while one player is alone within ~10m, toast the second); a **bank instead of a hub** (always correct when portal count allows); or a **forced teleport** via the routed RPC `RPC_TeleportPlayer` (registered in `Chat.Awake`, zero checks, :41627/:41962-41968) — genuinely per-player, but bypasses every vanilla portal rule (inventory check never reaches the server at all) and is forced movement, not a portal topology. `Player.TeleportTo` silently no-ops within 2s of a previous teleport with no feedback either side — any naive per-request hub rewrite without a lock has this as its steady-state failure: players randomly landing wherever the last request won the race.
