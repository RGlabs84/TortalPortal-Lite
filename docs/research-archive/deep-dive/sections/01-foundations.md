## Foundations: what the server actually controls

### The central constraint, restated precisely

`TeleportWorld` and `TeleportWorldTrigger` are byte-identical between the dedicated-server and client decompiles — diffing `assembly_valheim_SERVER.decompiled.cs:143402-143690` against the client copy at `:144142-144430` produces zero output. There is no server-specific portal code path anywhere; the divergence is entirely in *whether the component ever exists*, not in what it does.

It never exists, for a provable mechanical reason, not merely because `Player.m_localPlayer` is null. `Game.FixedUpdate` in the SERVER build unconditionally calls `ZNet.instance.SetReferencePosition(new Vector3(1000000f, 0f, 1000000f))` every physics tick (`Game.FixedUpdate` :100458-100466) — a line with **no counterpart at all** in the client's `Game.FixedUpdate` (:100886). `ZNetScene.CreateDestroyObjects` (:82268-82276) and `CreateObjectsSorted` (:82105-82112) derive the zone to instantiate from `ZNet.GetReferencePosition()`, and `CreateObjectsSorted` early-returns unless `ZoneSystem.IsActiveAreaLoaded()` is true for that zone — which it never is for the pinned (1e6,0,1e6) location, because nothing generates it. Consequently: no `TeleportWorld` MonoBehaviour, no `TeleportWorldTrigger` collider, no `Player` GameObject (`Player.Awake` :10032-10035 is what populates the static `Player.s_players` list `GetClosestPlayer` reads) ever exists on a dedicated server. `Awake`, `Update`, `UpdatePortal`, `Interact`, `GetHoverText`, `SetText`, `RPC_SetTag` and `Teleport` are all unreachable code, not merely unused code — a Harmony patch on any of them compiles, applies, and never fires.

`TeleportWorld.Teleport(Player)` has **exactly one call site in the entire assembly**: `TeleportWorldTrigger.OnTriggerEnter(Collider colliderIn)` (:143664-143682), gated on `colliderIn.GetComponent<Player>() != null && Player.m_localPlayer == component` (:143672-143681). Three independent conditions must hold for this to fire — a live trigger collider, a live Player component on the colliding object, and that Player being the local one — and a dedicated server satisfies none of them. There is no other path to `Teleport()` anywhere; grepping for `.Teleport(` on `TeleportWorld` yields exactly this one hit.

What survives this collapse is the ZDO layer underneath the component, which is genuinely server-authoritative:

- `ZDOVars.s_tag` (:78617) / `s_tagauthor` (:78619) — two plain strings on the portal's own ZDO, no RPC required to read or write.
- The `ZDOExtraData.ConnectionType.Portal` connection — one `ZDOConnection{type, target}` slot, network-serialized (`ZDO.Serialize` :74112, :74169-74174; `ZDO.Deserialize` :74225-74232).
- `Game.ConnectPortals()` (:100589-100627), a `public` method run from a coroutine started only under `if (ZNet.instance.IsServer())` in `Game.Start` (:100073-100074), firing once synchronously and then every 5.0s forever, with **zero** `Player.m_localPlayer` dependency anywhere in its body.

This is the whole of it: the component is dead, the ZDO underneath it is alive, and `Game.ConnectPortals` is the one piece of vanilla logic that runs on a dedicated server and actually touches portals.

### The complete portal ZDO field map

Ten public compiled fields exist on `TeleportWorld` (:143411-143437) and **none of them are ZDO-backed or RPC'd** — they are plain Unity Inspector values that a server-side write mutates on a copy no client will ever instantiate:

| Field | Default | Networked? |
|---|---|---|
| `m_activationRange` | 5f | no — compiled |
| `m_exitDistance` | 1f | no — compiled (read from the *destination* portal, see below) |
| `m_proximityRoot` | Transform ref | no |
| `m_colorUnconnected` / `m_colorTargetfound` | Color | no |
| `m_target_found` (EffectFade) | ref | no |
| `m_model` (MeshRenderer) | ref | no |
| `m_connected` (EffectList) | ref | no |
| `m_allowAllItems` | bool | no — compiled |
| `m_hoverOffset` | float | no |

Private runtime state (`m_nview`, `m_hadTarget`, `m_colorAlpha`, :143433-143437) is likewise never touched by ZDO or RPC. `GetHoverName()` returns the hard-coded literal `"Teleport"` (:143466-143469) — not the tag, not a loc key; the hover *name* cannot be changed by any ZDO write, only the hover *text*.

Against that, the ZDO itself carries exactly what actually matters:

| ZDO field | Hash source | Type | Writer | Reader | Networked |
|---|---|---|---|---|---|
| `s_tag` | `"tag"` :78617 | string | `RPC_SetTag` :143587-143602 (no cap, no filter at write time) | `GetTagInfo` :143561-143573, `Game.ConnectPortals` tag compare :100597/:100601/:100611/:100669 | yes |
| `s_tagauthor` | `"tagauthor"` :78619 | string | `RPC_SetTag`, from client-supplied `authorId` — unverified self-report | `GetTagInfo` (feeds `CensorShittyWords.Filter`, display only) | yes |
| Portal connection | `ConnectionType.Portal` = 1 (:74747-74754) | one `ZDOConnection{type,target}` | `ZDO.SetConnection`/`UpdateConnection` (:73656, :73668) | `TeleportWorld.Teleport` :143539, `HaveTarget`/`TargetFound` :143631-143655, `Game.ConnectPortals` | yes (real ZDOID on wire, hash-pair on disk — see below) |

That's the complete surface. Everything else about a portal's *behaviour* — allow-all-items, exit distance, activation range, colours, effects — is Inspector data with no ZDO or RPC path at all, confirmed by exhaustive field enumeration of the class body.

One documented, untested escape from this: `ZNetView.LoadFields()` (:82669-82738 server copy; identical client copy :82898-82970), called from `ZNetView.Awake` (:82631) on the instantiating client, reflects over every **public instance field** of every component on the prefab and overrides it from ZDO keys `"HasFields"`, `"HasFields<TypeName>"`, `"<TypeName>.<FieldName>"`, for int/float/bool/Vector3/string/GameObject/ItemDrop. `TeleportWorld.m_allowAllItems` is exactly such a field. If confirmed in-game, `zdo.Set("HasFields", true); zdo.Set("HasFieldsTeleportWorld", true); zdo.Set("TeleportWorld.m_allowAllItems", true)` makes a specific portal's ore-gate genuinely per-portal on a 100% vanilla client — refuting the blanket claim that every TeleportWorld field is dead. It is strictly client-**honoured**, not enforced, and applies only at instantiation (a standing player does not pick it up until they leave and re-enter the zone).

### Vanilla's pairing algorithm, as pseudocode

There are three "ConnectPortals"-named routines; only one runs at steady-state runtime. `ZDOMan.ConnectPortals()` (:77850-77893, private) and `ZDOMan.ConvertPortals()` (:77812-77848, private) are load-time-only, operate on save-file **hash pairs**, and are irrelevant here except as the persistence boundary (below). The runtime one is `Game.ConnectPortals()` (public, :100589-100627), driven by `ConnectPortalsCoroutine` (:100580-100587: `while(true){ ConnectPortals(); yield return new WaitForSeconds(5f); }`), started once by string name in `Game.Start` under `IsServer()` (:100073-100074) — first call synchronous at Start, then exactly every 5.0s, unstoppable except by `Game.instance.StopCoroutine("ConnectPortalsCoroutine")`.

```
function Game.ConnectPortals():                                   // every 5.0s, dedicated server only
    // Phase 0 — force-commit last tick's proposals
    for each (A, B) in m_currentlyConnectingPortals:
        if A.connection(Portal) != B.uid: SetConnection(A, B.uid, force=true)
        if B.connection(Portal) != A.uid: SetConnection(B, A.uid, force=true)
    m_currentlyConnectingPortals.Clear()

    portals = ZDOMan.GetPortalList()                               // fresh List<ZDO>, world-global, no cache

    // Phase 1 — reconcile / self-heal  (:100594-100606)
    for each p in portals:
        c = p.GetConnectionZDOID(Portal)
        if c.IsNone(): continue
        q = ZDOMan.GetZDO(c)
        if q == null OR q.tag != p.tag OR q.GetConnectionZDOID(Portal) == None:
            SetConnection(p, ZDOID.None, force=false)               // NEVER checks q.connection == p.uid

    // Phase 2 — pair  (:100607-100622)
    for each p in portals:
        if IsCurrentlyConnectingPortal(p): continue
        if !p.GetConnectionZDOID(Portal).IsNone(): continue
        cands = [ q in portals : q != p (reference eq) AND q.tag == p.tag
                  AND q.connection(Portal) == None AND !IsCurrentlyConnectingPortal(q) ]
        if cands.empty: continue
        q = cands[UnityEngine.Random.Range(0, cands.Count)]         // shared global RNG, max-exclusive
        m_currentlyConnectingPortals.Add({p, q})
        SetConnection(p, q.uid, force=false); SetConnection(q, p.uid, force=false)

function SetConnection(portal, targetId, force):                   // :100637-100651
    owner = portal.GetOwner()
    if owner == 0 OR ZNet.instance.GetPeer(owner) == null OR force:
        portal.SetOwner(ZDOMan.GetSessionID())                      // steal ownership
        portal.SetConnection(Portal, targetId)
        ZDOMan.instance.ForceSendZDO(portal.uid)                    // applied immediately, locally
    else:
        ZRoutedRpc.InvokeRoutedRPC(owner, "RPC_SetConnection", portal.uid, targetId)  // DEFERRED
```

**The load-bearing omission**: phase 1's reconcile predicate never checks that `q` points back at `p`. Reciprocity is not tested anywhere in the runtime path. This is why one-way links, rings, chains, hub fan-in and self-loops are all stable fixed points of vanilla's own coroutine — they require zero Harmony patching to persist at runtime, only that every member shares one byte-identical tag string and every member's own connection stays non-None.

Edge-case table for N same-tag portals, all initially unconnected, steady state after ≥2 ticks:

| N | Outcome |
|---|---|
| 0 or 1 | stays `ZDOID.None` forever — candidate list is empty |
| 2 | mutual pair, first tick |
| 3 | one pair forms; the third is **permanently orphaned** — surviving pair keeps re-confirming, orphan never rotates in |
| 4 | two mutual pairs |
| odd N | floor(N/2) pairs + exactly one permanent orphan (order is dictionary-bucket order — not stable across restarts) |
| A→B, B→C (chain/ring) | survives indefinitely — reciprocity unchecked |
| A→A (self-loop) | survives indefinitely |
| tags differ by case/trailing space | different groups entirely — `string !=`, ordinal, no trim (:100601, :100669; default `""` via `ZDOExtraData.GetString`, :75079-75082) |

**Two-phase-commit trap.** Phase 1's disconnect at :100603 is **not forced** — if the owner is an online peer the clear is deferred over RPC and the server's own copy still reads the old value; phase 2, in the same tick, then sees `!IsNone()` and skips it. Retagging or re-pairing a portal near a standing player therefore takes **two or more ticks (≥10s)**, not one. `ClearCurrentlyConnectingPortals` (:100690-100698) force-commits the previous proposal at the top of every call regardless of what the client did — the server always wins ownership within one extra tick.

### ZDO ownership: the claim/write/release pattern

No ZDO setter of any kind checks ownership — `ZDO.Set(int hash, int value, bool okForNotOwner=false)` accepts and **ignores** `okForNotOwner` entirely (:73648-73654) — so a server write to an unowned or foreign-owned ZDO lands locally and bumps `DataRevision` regardless. Ownership matters for exactly two things: winning the revision race against the owning client's next higher-revision push (`ZDOMan.RPC_ZDOData` :77076-77139 applies incoming data wholesale on a higher revision, no field-level merge), and being allowed to call `ZDOMan.DestroyZDO` (owner-gated on send, :76929-76935 — receive side `HandleDestroyedZDO` is completely ungated, :76963-76999).

The canonical recipe, lifted verbatim from `Game.SetConnection`'s own body (:100643-100645):

```
zdo.SetOwner(ZDOMan.GetSessionID());                 // claim — no-op if already owner
zdo.SetConnection(ConnectionType.Portal, targetId);   // or zdo.Set(hash, value) for any field
ZDOMan.instance.ForceSendZDO(zdo.m_uid);              // push to every peer regardless of distance
```

Ownership is **transient by design**: `ZDOMan.ReleaseNearbyZDOS` (:76901-76926), driven by `ReleaseZDOS` every 2s (:76872-76885), hands any persistent ZDO inside a peer's active area (~96-112m, `ZNetScene.PointInsideActiveArea` :82297-82320) to that peer whenever the current owner is out of area or unowned — portals included, since `ZDOMan.FindObjects` merges `m_portalObjects[sector]` into every sector query (:77367-77382). A server claim near a player is stolen back within 2s; the *value* already written survives (it was force-sent), only the *next* write needs to reclaim. A high-frequency reassert loop should either tolerate the reclaim cost or install a Harmony prefix on `ZDO.SetOwner` that fails fast on prefab hash and never blocks `uid==0` or `uid==ZDOMan.GetSessionID()` (Wonderland's `WaterBuoyancyEngine.ZdoSetOwnerPatch`, `Subsystems/ItemFlow/WaterBuoyancyEngine.cs:301-313` — the exact reusable pattern). Note also that `ZDOMan.RPC_ZDOData` applies incoming ownership by calling `ZDO.SetOwnerInternal` directly (:77114, :77128), bypassing the public `SetOwner` setter entirely, so a Harmony prefix on `ZDO.SetOwner` alone does not stop a client's ZDO push from reclaiming ownership — a full guard needs the `Deserialize`/`RPC_ZDOData` path covered too.

Idempotence is free: `ZDOExtraData.SetConnection` no-ops on an unchanged value (:74954-74957) and `BinarySearchDictionary.SetValue` returns false on equality (`assembly_utils_SERVER.decompiled.cs:321-345`), so `ZDO.Set` skips `IncreaseDataRevision` — a reassert-every-tick loop over an already-correct topology generates **zero** DataRevision bumps and zero network traffic. This is what makes continuous server-side reconciliation viable rather than wasteful.

**Never cache a `ZDO` object reference across ticks.** `ZDOPool.Release`/`Get` recycle ZDO objects; `ZDO.Reset` sets `m_uid = ZDOID.None` (:73536-73545, :78286-78311). Vanilla itself violates this in `m_currentlyConnectingPortals`, which holds raw ZDO references for a full 5s window and can force-write through a stale/recycled reference. Cache `ZDOID` values only, re-resolve every tick via `ZDOMan.instance.GetZDO(id)`.

**Never persist a ZDOID as identity.** `ZDO.Load` does `m_uid.SetID(++ZDOID.m_loadID)` (:74552), and `ZDOID.SetID` forces `UserKey = UnknownFormerUserKey` (:75878-75882) — every ZDOID in the world is renumbered on every load. Key mod state by rounded world position, by `s_creator`, or by a mod-private ZDO field that travels with the object; TortalPortal's own `Favorites.cs` hit exactly this bug in production and switched to position keys.

**Tag-only writes are silently lost at save.** `ZDO.SetConnection`/`UpdateConnection` for `ConnectionType.Portal` call `ZDOMan.instance.SetDirtyPortals()` (:73661-73664, :76702) — a plain `zdo.Set(ZDOVars.s_tag, ...)` does not. `DirtyPortalObjects` is the *only* thing that causes the dedicated `ZoneSystem.ChunkPortal` file to be rewritten (`GetSaveClonePerChunk` :77621-77635; portals are explicitly excluded from ordinary chunk saves, :77572). Any tag-only mutation needs an explicit `SetDirtyPortals()` call or the rename silently reverts at the next world save with no error anywhere.

### Persistence collapses non-reciprocal topologies — this is the real ceiling, harder than the 5s reconciler

The wire format carries a real ZDOID (`ZDO.Serialize` :74169-74173); the disk format does not. `ZDOExtraData.RegenerateConnectionHashData` (:75461-75478, called from `PrepareSave`) converts each live `s_connections` entry into exactly one `ZDOConnectionHashData{type, int hash}` slot per ZDO — the source gets `{Portal, h}`, the target gets `{Portal|Target, h}`, and a later edge sharing either endpoint **overwrites** the earlier slot. A ring A→B→C→A: processing C overwrites A's source slot with a Target slot, so on reload only one pair re-links and the third portal is orphaned and randomly re-paired. A one-way link A→B, B→None: `ZDOMan.ConnectPortals()` (the load-time private one, :77850-77893) matches hashes and writes **both** directions — one-way becomes bidirectional on every restart. A self-loop A→A: the source slot is immediately overwritten by the Target slot (:75472-75475) and is erased outright. **Every managed topology except a simple pair or independent portal banks must be re-asserted in an `OnWorldReady` pass after every load** — this is a harder, more silent constraint than "vanilla re-pairs within 5s," because it only bites once per server restart and is easy to never notice in a dev session.

The cheapest window to do that reassertion is immediately after load and before any peer connects: `ZDOMan.LoadChunks` calls the load-time `ZDOMan.ConnectPortals()` then `Game.instance.ConnectPortals()` (:76515, :76523) while `m_peers` is empty, so every `Game.SetConnection` write takes the immediate local path (`GetPeer(owner) == null` is always true) with zero contention.

### The phantom-ZDO technique

A persistent, vanilla-instantiable portal with no GameObject anywhere until a client walks near it:

```
zdo = ZDOMan.instance.CreateNewZDO(pos, portalPrefabHash);   // :76674-76692 — auto-runs AddIfPortal(zdo, hash)
zdo.Persistent = true;                                        // NOT set by CreateNewZDO — mandatory
zdo.Type = ZDO.ObjectType.Default;                             // mirrors ZNetView.Awake :82613-82620
zdo.Distant = false;
zdo.SetPrefab(portalPrefabHash);                               // NOT set by CreateNewZDO — mandatory
zdo.SetRotation(rot);
zdo.Set(ZDOVars.s_tag, "MyNetwork");
ZDOMan.instance.SetDirtyPortals();
```

`CreateNewZDO` alone yields a non-persistent, prefab-less ZDO (`m_prefab` stays 0) that a client's `ZNetScene.CreateObject` cannot instantiate and that will not survive a save; passing the real portal hash into `CreateNewZDO` at least gets `AddIfPortal` to fire before `SetPrefab` runs, so portal registration is not lost even though persistence and prefab still need the explicit follow-up lines. Once fabricated, the phantom portal is invisible to `ZDOMan`'s bookkeeping distinctions — it lives in `m_portalObjects`, is enumerated by `GetPortalList()`, is subject to `Game.ConnectPortals()`'s tag/reciprocity rules exactly like a player-built portal, and is delivered to any client whose `FindSectorObjects` query covers its (frozen, creation-time) sector.

Two absolute rules follow directly from `ZDO.SetSector` (:73752-73757), which **early-returns for any prefab in `Game.PortalPrefabHash`**: never call `SetPosition` on a portal ZDO (it stays filed under its original sector forever in `m_portalObjects`, and if later destroyed, `HandleDestroyedZDO` looks it up by the *new* sector and fails to remove it — a permanent leak); destroy and recreate instead. And `Game.PortalPrefabHash` (public getter, private setter, :100021, populated in `Game.Awake` from the Inspector list `m_portalPrefabs` :99828-100032) must be read at runtime, never hard-coded as `portal_wood`/`portal_stone` — no portal prefab name string appears anywhere in either decompile; they are Unity asset data.

A cheaper variant — the **invisible anchor** — uses a non-portal prefab hash (or hash 0: the true uninitialized `m_prefab` default is -1, `c_PrefabInvalid` :73305-73317, so an explicit `SetPrefab(0)` is what produces a silent, log-free ZDO, since `ZNetScene.CreateObject` returns null before any lookup on exactly `prefab==0`, :81996-82002, and `ZNetScene.IsPrefabZDOValid` also treats 0 as invalid so `IsAreaReady` skips it cleanly, :81986). Such an anchor is never iterated by `Game.ConnectPortals()` (it is not in `m_portalObjects`), never torn down by tag mismatch, freely movable (its `SetSector` is not exempted), and still perfectly readable by `TeleportWorld.Teleport`, which only ever calls `GetPosition()`/`GetRotation()` on the connected ZDO with **no prefab check whatsoever** (:143539-143548). The cost is a one-way trip: nothing glows or reads as a portal at the anchor end, because there is no portal component there to glow.

### Execution-context inventory

Every claim in this document about a given piece of code resolves to exactly one of four contexts. Conflating them is the single most common error in naive portal-mod design:

| Context | What runs here | Portal-relevant examples |
|---|---|---|
| **dedicated-server** | Code gated on `ZNet.instance.IsServer()`, or public/static methods with no scene dependency | `Game.ConnectPortals()`, `ZDOMan.GetPortalList()`, `ZDO.SetConnection`, `ZoneSystem.SetGlobalKey`, `WorldGenerator.GetHeight/GetBiome` (collider-free) |
| **ZDO-owning-client** | Runs only on whichever peer currently owns a given ZDO | `TeleportWorld.RPC_SetTag` (only if a *client* owns the portal — never the server, since the server has no instance to dispatch to), `ZSyncTransform.OwnerSync`, `ShipControlls.RPC_RequestControl` |
| **local-player-client** | Runs only on the machine where `Player.m_localPlayer == this` | `TeleportWorldTrigger.OnTriggerEnter`, `TeleportWorld.Teleport`, `TeleportWorld.Interact`, `Teleport.OnTriggerEnter`, `Ladder.Interact` |
| **compiled-field-no-network** | Plain Inspector value, no ZDO, no RPC, exists only as compiled data on whichever process instantiated the prefab | `m_allowAllItems`, `m_exitDistance`, `m_activationRange`, all EffectList/colour refs, `PrivateArea.m_radius` |

The server is the *union* of dedicated-server-only code plus anything explicitly public/static and side-effect-safe to call from it (e.g. `ZDOMan.CreateNewZDO`, `ZoneSystem.GetGlobalKey`). Everything in the other three rows is either invisible to a Harmony patch placed on a dedicated server (rows 2 and 3 usually never execute there at all) or invisible to the network entirely (row 4). Before proposing any mechanism, classify it into this table first — most "obviously it should just work" ideas die here.

One structural correction worth internalising for the retag-interception family of ideas specifically: even on the rare occasion an RPC *does* cross the wire to the server, `ZRoutedRpc.HandleRoutedRPC` (:83646-83663) dispatches a ZDO-targeted RPC only via `ZNetScene.instance.FindInstance(zDO)` (:82229), which requires a live `m_instances` entry that a dedicated server never has. The only server-side interception point for such traffic is the relay layer itself — a Harmony prefix on `ZRoutedRpc.HandleRoutedRPC` or `RouteRPC` (:83597-83629) keyed on the method-hash — and even that catches nothing when the sender happens to already own the target ZDO, because `ZRoutedRpc.InvokeRoutedRPC` skips `RouteRPC` entirely whenever `targetPeerID == m_id` (:83588-83594): a player standing at their own portal retags it with **zero packets on the wire**, laundered instead through the ordinary `RPC_ZDOData` field push.

### The second teleport mechanism, and the full inventory of non-locomotive position changes

`class Teleport` (dungeon/crypt doors, :143316-143390) is architecturally the *opposite* of `TeleportWorld`: no `ZNetView`, no ZDO, no RPC, no `Awake()`, no networked state of any kind. Its destination is a plain serialized Inspector reference `public Teleport m_targetPoint` (:143322), resolved by `GetTeleportPoint() => transform.position + transform.forward - transform.up` (:143372-143375). Server and client copies are textually identical (client :144056-144130). There is **nothing to read, write, redirect, log or veto** — not a weaker version of the portal mechanism, a categorically different one with zero server surface. Its boss gate (`Teleport.Interact` :143355) tests `NoBossPortals && character.InInterior() && Location.IsInsideActiveBossDungeon(pos)` — the *outbound* leg only (`InInterior()` tests the player's *current* altitude, so entering is never blocked), and `IsInsideActiveBossDungeon` dereferences `EnemyHud.instance`, a pure client HUD singleton (:133631-133645) — doubly client-bound. Crucially, `Teleport.Interact` never reads `GlobalKeys.NoPortals` and never calls `Player.IsTeleportable` at all (:143345-143370) — a server-wide portal lockdown or ore restriction does not touch dungeon doors, full stop.

Beyond the two teleport components, the assembly contains a wider family of non-locomotive position writes, all executed client-side and none server-reachable except the two RPCs at the top:

| Mechanism | Location | Reachable from server? |
|---|---|---|
| `Chat.RPC_TeleportPlayer` | `Chat.Awake` reg :41627, body :41962-41968 (`if Player.m_localPlayer != null`) | **yes** — `ZRoutedRpc.InvokeRoutedRPC(peer.m_uid, "RPC_TeleportPlayer", pos, rot, distant)`, no Chat.instance needed |
| `Character.RPC_TeleportTo` | registered every Character with a ZDO, `Character.Awake` :886; body :4126-4132 (owner-gated) | **yes** — ZDO-scoped variant, `InvokeRoutedRPC(peer.m_uid, characterZdoId, "RPC_TeleportTo", ...)` |
| `Ladder.Interact` | :132064-132079 | no — instant snap, no cooldown, no fade, local-player only |
| `TeleportAbility.Setup` (projectile) | :25533-25550 | no — attacker's owning client only |
| `Valkyrie.SyncPlayer` | :147938-147955 | no — intro-only, local-player |
| `Trap.TriggerTrap` | :145917-145928 | no — freeze-in-place, ZDO owner |
| `Attack.UpdateAttach` | :24049-24085 | no — attacker's owning client |
| `Player.UpdateAttach`/`AttachStop` (sit/mount/helm) | :15818-15916 | indirectly observable — writes `ConnectionType.SyncTransform` + `s_relPosHash`/`s_attachJointHash` into the rider's own ZDO |
| World-edge/underworld rescue snaps | `Character.UnderWorldCheck` :1055-1073, `Player.EdgeOfWorldKill` :11132-11152 | no — owner-gated |

Both server-reachable RPCs share the same failure shape: `Player.TeleportTo` (:15302-15327) refuses with a silently-discarded `false` if `IsTeleporting()` or `m_teleportCooldown < 2f` — and the cooldown is zeroed every tick *while* teleporting, so the dead window for a `distantTeleport:true` call (which portals always use, and which any server-initiated teleport should also always use) spans the entire ~8-15s transit plus 2s, not "2 seconds after the RPC." Both bypass every vanilla gate: no `NoPortals`, no `NoBossPortals`, no `IsTeleportable` ore check, no `PrivateArea`. A server-driven warp is strictly more powerful and strictly less safe than a portal.

### The one organising principle

Collect every fact above into a single test, and the entire feasibility space of this project falls out of it:

> **The server controls whether a route is open. It never controls who passes through it.**

Restated mechanically: `TeleportWorld.Teleport`'s first gate — `if (!TargetFound()) return;` (:143519) — is the *only one* of its four gates that is genuinely server-**enforced**, because it depends on `GetConnectionZDOID(Portal)` (:143631-143638), a field the server owns outright and can force to `ZDOID.None` with no client cooperation. A portal with no connection has nowhere to send anyone, on a stock client or a fully re-compiled one — there is no code path, however hacked, that invents a destination ZDO that does not exist. Every other decision in the whole system — `NoPortals`, `NoBossPortals`, `m_allowAllItems`, a PIN, a per-player cap, "did this specific traveller pay" — requires either a networked global broadcast the *client* chooses to honour (client-honoured, defeated by any modified client, since e.g. `RPC_SetGlobalKey`/`RPC_RemoveGlobalKey` perform zero sender validation at :116022-116037), or a hook at the exact moment of transit that **structurally does not exist** on a dedicated server, because that moment is defined as "the frame `TeleportWorldTrigger.OnTriggerEnter` runs," and that frame only ever runs on the traveller's own machine.

Every option in this catalogue is therefore an exercise in converting a per-player policy into a per-route one. "Only the builder may retag this portal" becomes unenforceable the instant you ask it as a question about a *person*, and straightforward the instant you ask it as a question about a *ZDO write pinned to a server-held ownership claim*. "Charge this player a toll" is impossible; "hold this specific route disconnected until a chest beside it contains N coins" is a five-line `ZDO.GetInt` check that runs before anyone travels, not after — the same trick that makes progression gates, network membership, distance pricing and every dynamic-routing scheme in this document actually work. Read every subsequent section against this test first: does the mechanism decide *whether a coordinate exists at the far end of a connection field*, or does it try to decide *who is currently walking toward one*? Only the first kind survives contact with a modified client.
