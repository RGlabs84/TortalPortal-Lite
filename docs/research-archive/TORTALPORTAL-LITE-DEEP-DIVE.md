# TortalPortal Lite — Deep Dive

**A strictly server-side Valheim 1.0.7 mod's complete capability audit against 100% vanilla clients.**

Produced by a 114-agent research/design/verify/critique/gap-fill/write pipeline (workflow `wf_426424f2-e93`): 12 mechanism-research agents read the decompile cold; 9 designers turned their findings into 197 fully-specified options; every option was adversarially attacked by two independent lenses (decompile accuracy, vanilla-client reality); three completeness critics hunted for missing mechanisms, missing options, and overclaimed honesty; 8 gap-fill designers and verifiers covered what the critics found (89 more options); 8 section writers produced this document from the resulting 286-option, fully-verified catalog.

Full per-option verifier detail, citations, and reasoning for all 286 options live in [OPTION-CATALOG-VERIFIED.md](OPTION-CATALOG-VERIFIED.md) — this document is the synthesized narrative; that file is the evidence backing every claim in it.

## Contents

1. [Foundations: what the server actually controls](#foundations-what-the-server-actually-controls)
2. [Network topologies](#network-topologies)
3. [Targeted portals: pointing at a specific thing](#targeted-portals-pointing-at-a-specific-thing)
4. [Dynamic routing: destinations that change](#dynamic-routing-destinations-that-change)
5. [Driving it from a vanilla client](#driving-it-from-a-vanilla-client)
6. [Access control, security and lockdown](#access-control-security-and-lockdown)
7. [Economy, progression, and operations](#economy-progression-and-operations)
8. [Limits, wildcards, and what to build first](#limits-wildcards-and-what-to-build-first)

---

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


---

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


---

## Targeted portals: pointing at a specific thing

### Target taxonomy

| Target | Server-locatable? | Source | Notes |
|---|---|---|---|
| Arbitrary X/Z coordinate | Yes, math-only | `WorldGenerator.GetHeight/GetBiome/GetNormal` | No colliders server-side; validate procedurally, never via `ZoneSystem` raycasts |
| World spawn (StartTemple) | Yes, exact | `ZoneSystem.GetLocationIcon(Game.m_StartLocation)` :115422 | Same call vanilla uses for `Game.FindSpawnPoint` |
| Boss altar | Yes, but pin needs a push | `m_locationInstances` / `FindLocations` | No vanilla map icon until a Vegvisir is read — corrected below |
| Trader camp | Yes, with a placement race | `FindLocations` + `m_placed` | Unique locations have N pre-placement candidates, not one |
| Dungeon/crypt entrance | Yes (surface only) | `FindClosestLocation` | Interior ruled out — geometry is asset data, doors have no ZDO |
| Biome region (edge/centroid/random) | Yes, O(1) grid | `ZNet.World.m_biomeData` | Ashlands/DeepNorth/Ocean are single global sectors — centroid modes degenerate |
| Any named world-gen location | Yes | `ZoneSystem.m_locations` + `LocationProxy` ZDOs | Registry route (always) vs. materialised-only route (`s_location` scan) |
| Player's bed | Yes, ambiguous | `Bed.RPC_SetOwner` → `s_owner` | Unlimited unowned-bed claims; disambiguate via `s_inBed` + `IsCurrent()` |
| Player's custom spawn point | **No direct read** | n/a | Local `.fch` only; inferable from `peer.m_refPos` during the ~10s respawn window |
| Player's death spot / tombstone | Yes, exact | `TombStone.Setup/Awake` → `s_owner`, `s_timeOfDeath` | Only server window into a player's actual inventory |
| Player's ship | Yes, helmsman only | `ShipControlls.RPC_RequestControl` → `s_user` | Passenger detection via `SyncTransform` connection is unverified |
| Nearest/named online player | Yes, live | Character ZDO position (`ConnectedCharacters`) | Never write `ConnectionType.Portal` onto a player ZDO — one slot, shared with ship/mount parenting |
| Player-placed anchor (sign/stand/ward/chest/tagged portal) | Yes | Plain ZDO fields, no RPC needed for signs | Reserved-prefix tagged portal needs no phantom at all |

Every "Yes" in this table still resolves to *server writes a connection field*; whether the traveller's client honours the four gates in `TeleportWorld.Teleport` (:143517-143537) is a separate question answered once, in the foundation section, and not re-litigated per target.

### The enabling mechanism: Phantom Destination Portal

Every targeted option below is a consumer of one primitive: mint a real, persistent, portal-prefab ZDO at a computed coordinate with no GameObject, pair it to the source, and let a vanilla client instantiate and honour it as an ordinary portal.

```
hash = Game.instance.PortalPrefabHash[i]                 // resolve at runtime, never hard-code
z    = ZDOMan.instance.CreateNewZDO(pos, hash)            // :76674 — owner=session, AddIfPortal files it
z.Persistent = true; z.Type = ZDO.ObjectType.Default; z.Distant = false
z.SetPrefab(hash); z.SetRotation(rot)                     // ZNetView.Awake's own fabrication order, :82613-82620
z.Set(ZDOVars.s_tag, tag); z.Set("TPL_phantom", 1)        // s_tagauthor left EMPTY — see identity note below

src.SetOwner(ZDOMan.GetSessionID()); src.SetConnection(Portal, z.m_uid); ZDOMan.instance.ForceSendZDO(src.m_uid)
z.SetConnection(Portal, src.m_uid); ZDOMan.instance.ForceSendZDO(z.m_uid)
```

Reciprocal connection + byte-identical `s_tag` on both ends is not optional: `Game.ConnectPortals` (:100589-100627) runs every 5s and its phase-1 predicate — `target==null || target.tag != mine || target.connection==None` (:100600-100604) — never checks reciprocity, so a symmetric pair is a fixed point of vanilla's own reconciler with no patch required, while an asymmetric one is torn down. `TeleportWorld.Teleport` reads the target's `GetPosition()/GetRotation()` fresh at the instant of transit (:143539-143548), so a server re-point takes effect on the very next walk-through with no client refresh.

Corrections load-bearing for every downstream option:

- **Exit offset is destination-owned.** `pos = targetPos + targetRot*forward*target.m_exitDistance + up` (:143542-143545) — the field read belongs to the portal being *entered*, so overriding `m_exitDistance` on a phantom via `LoadFields` only changes the *return* hop from it, never the arrival at it.
- **Ground support is the real killer.** The instantiated portal carries `WearNTear` with `m_noSupportWear=true` by default; `UpdateSupport` finds no terrain/static collider under a floating or buried phantom and the owning client applies ~100 dmg/tick until it's destroyed and drops full materials (client WearNTear support/damage paths). Mint at accurate ground height (`WorldGenerator.GetHeight`, never a `ZoneSystem` raycast — those are dead server-side because `Game.FixedUpdate` pins the reference position at (1e6,0,1e6), :100458-100466), or field-inject `HasFields`/`HasFieldsWearNTear`/`WearNTear.m_noSupportWear=false` via `ZNetView.LoadFields` (client :82898-82970, called unconditionally from `ZNetView.Awake` at client :82860). LoadFields itself is a pure ZDO-driven runtime check with no prefab-authoring prerequisite — it works on any prefab regardless of whether the Inspector shipped `HasFields`.
- **Ownership is transient, not persistent.** `ZDOMan.ReleaseNearbyZDOS` (:76901-76924) hands any Persistent ZDO within ~96-112m to the nearest peer every 2s. The written *value* survives (already `ForceSendZDO`'d); only re-claim before the *next* write, and never call `SetOwner` unconditionally on a maintenance tick — that alone bumps `OwnerRevision` and fights the reassignment for as long as a player stands nearby.
- **Never move a phantom.** `ZDO.SetSector` early-returns for any prefab in `PortalPrefabHash` (:73752-73756); a repositioned portal stays filed under its creation sector in `m_portalObjects` forever, `HandleDestroyedZDO` leaks the stale bucket, and clients duplicate the entry across sector boundaries. Destroy and recreate.
- **Tag-only writes are invisible to save.** `ZDO.Set(s_tag,...)` only marks the ordinary chunk dirty (`SetDirtySector`), and portals are explicitly excluded from that chunk (`AddObjectsPerChunk` :77572); the entire `ChunkPortal` file is written only when `DirtyPortalObjects` is set. Call `ZDOMan.instance.SetDirtyPortals()` (:76702) after every non-connection write or the rename reverts at the next save.
- **`s_tagauthor` is client-parsed as a `PlatformUserID`.** `GetTagInfo` does `new PlatformUserID(authorString)` on every hover for any non-empty value (:143557-143565); that type lives outside this decompile set. Leave it empty (`None` branch) rather than writing `"server"`.
- **Destructibility is real.** `Piece.m_canBeRemoved=false` via LoadFields blocks only the hammer path (`Player.RemovePiece` check); weapon damage still runs `WearNTear.Destroy → ZNetScene.Destroy → DestroyZDO`, and `ZDOMan.RPC_DestroyZDO` has zero sender/ward validation (:76953-76960) — any client can delete a phantom. The maintenance tick must detect a missing pair member and re-mint.
- **Never key persistent state by ZDOID** — `ZDO.Load` renumbers every id (`m_uid.SetID(++ZDOID.m_loadID)`, :74552). Key the mod's registry by rounded world position.

**Invisible Anchor variant.** Point the source at a *non*-portal-prefab ZDO instead — prefab hash **0** (silent: `ZNetScene.CreateObject` returns null with no log for hash 0; a custom hash logs `Missing prefab hash` every 1/30s pass while in range) — created the same way but `Persistent=true` with no `AddIfPortal` (skipped for hash 0). `TeleportWorld.Teleport` never checks the target's prefab (:143539-143548), so this is a legal one-way destination: no mesh, no return portal, and — unlike a phantom — it *can* be `SetPosition`'d safely (normal `SetSector` re-filing applies) provided you follow moving-target discipline (below) and call `ForceSendZDO` after every move, since `ZDOSectorInvalidated` drops the ZDO from any out-of-area peer's held set. Keep anchors inside ±16,352m — beyond that `SectorToIndex` clamps to sector 0, the one bucket the dedicated server's own pinned reference position scans, and the server's `IsServer()` reaper destroys uncreatable ZDOs there.

Uninstall residue for both variants: phantoms persist as ordinary vanilla portals (vanilla keeps pairing them by tag); anchors persist as harmless, warned-but-loaded ZDOs. Ship a `tpl_purge` command that walks `GetPortalList()` for `TPL_phantom` markers and the mod's anchor registry, claims ownership, and `DestroyZDO`s everything before removal.

### Fixed world-gen targets

**Admin coordinates** [feasible-with-caveats | server-enforced-route | medium]. Validate with pure `WorldGenerator` math — `GetHeight`, reject beyond `waterEdgeSqr` (:151122) or `h<30` unless water is explicitly allowed, `IsLavaPreHeightmap` (:115637), `GetNormal(...).y<0.7` for slope — then mint a phantom. The input channel is the weak link: the dedicated server reads no stdin (no `Console.In`/`ReadLine` anywhere in the assembly) and a vanilla admin's client cannot reach a *mod-registered* command through `RPC_RemoteCommand` — `Terminal.TryRunCommand` rejects unknown names locally before any forwarding branch (:45734-45758). Real channels: the mod's own config file (hot-polled), the mod's own stdin thread, or hijacking a vanilla `remoteCommand:true` command that accepts free text (`setkey <payload>`, :43509-43534) via a prefix on `ZNet.RPC_RemoteCommand`/`InternalCommand` (adminlist-gated, :81879-81889). Apply the LoadFields `WearNTear.m_noSupportWear=false` override — admin-picked coordinates are exactly the case with no location-flattening to rely on.

**World spawn** [feasible-with-caveats | server-enforced | small]. `ZoneSystem.GetLocationIcon(Game.instance.m_StartLocation)` (:115422-115447; `m_StartLocation="StartTemple"`, :99845) is the literal value `Game.FindSpawnPoint` uses client-side. Gate creation on `ZoneSystem.LocationsGenerated`/the `GenerateLocationsCompleted` event, not a bare "OnWorldReady" assumption — on a fresh world location generation is a time-sliced coroutine. `m_position.y` is the pre-flattening `WorldGenerator` height (`PlaceLocations` never writes the resolved value back), so expect a metre or two of error at the ring; the client's `FindFloor`/15s fallback absorbs it, but a floating phantom still needs the WearNTear override.

**Boss altars** [feasible-with-caveats | server-enforced | medium]. Discovery via `FindLocations`/`FindClosestLocation` over `m_locationInstances`, cached per-name at boot. **Correction to a common assumption:** altars carry **no vanilla map icon** — that only appears via Vegvisir → `Game.DiscoverClosestLocation` → `RPC_DiscoverLocationResponse`; push it yourself if wanted (note the side effect: it also forces `SetLookDir` toward the pin, :100826-100829). "Next undefeated boss" reads `GetGlobalKey(GlobalKeys.defeated_*)` — the enum order (eikthyr, dragon, goblinking, gdking, bonemass, :101314-101318) is **not** canonical progression order; hard-code the real sequence, and note Queen/Fader are free-form string keys outside the enum, readable only via `GetGlobalKey(string)`/`GetGlobalKeys()`. `NoBossPortals` also blocks on `RandEventSystem.GetBossEvent()!=null`, not just `activeBosses>0` (:143528). Re-targeting on boss defeat is destroy-and-recreate, which must complete and re-tag within the 5s reconcile window or the hub gets randomly re-paired.

**Traders** [feasible-with-caveats | server-enforced | small-medium]. Same registry route, filtered on `m_iconPlaced==true`. Non-obvious hazard: unique locations seed **multiple candidate instances** simultaneously; `PlaceLocations`/`RemoveUnplacedLocations` deletes every other candidate the instant *any one* is placed (:114990-115030) — including one triggered by the arriving traveller's own ghost-zone generation. A phantom targeting a not-yet-placed candidate is self-fulfilling only if nobody else generates a different candidate first.

**Dungeon/crypt entrances** [feasible-with-caveats | server-enforced (surface only) | medium]. Surface-level targeting via `FindClosestLocation` is sound. Interior targeting is ruled out: `class Teleport` (dungeon doors) has no ZNetView, no ZDO, no RPC — its destination is a compiled `m_targetPoint` reference (:143322) — but room *positions* ARE recoverable server-side from the `DungeonGenerator` ZDO's `s_roomData` byte blob (:152970-152985); what's actually missing is floor *geometry*, narrower than "interior is opaque." Correction: crypt doors do honour `NoBossPortals` for the outbound leg of an active boss dungeon (:143347-143351) even though they skip `NoPortals` and the ore check entirely.

**Biome targets** [feasible-with-caveats | server-enforced | medium]. `ZNet.World.m_biomeData` (`AltBiomeWorldData`, 2048×2048 @ 12m) gives O(1) height/biome and flood-filled `BiomeSector`s. Corrections: `PointBiomes` holds `Heightmap.BiomeIndex`, not `Biome` — convert before comparing; `BiomeSector.Min` is dead-zero garbage (not just `MinZone/MaxZone`) — only `Max`, `Center`, `EdgeCount`, `HeightMin/Max` (edge-cell-only, so `HeightAvg` is a perimeter average, not interior) are usable; Ashlands/DeepNorth/Ocean are each **one pre-seeded global sector**, so centroid/nearest-N modes are meaningless there — use the spiral edge-search or `GetRandomPointByBiomesAboveSeaLevel` (which itself silently falls back to below-sea-level points when a biome has none above, so re-check `GetHeight>=30`).

**Named world-gen locations (generic)** [feasible | server-enforced | small-medium]. `ZoneSystem.m_locations`/`GetLocationList` is the vocabulary; `m_locationIDCache`/`m_locationGroupCache` are already public. The "materialised-only" `GetAllZDOIDsWithHash(Int, s_location)` full-ZDO-field scan is unnecessary — `LocationInstance.m_placed` already answers "does this physically exist" with zero scanning.

### Player-relative targets

**Bed** [feasible-with-caveats | server-enforced (route) | medium]. Identity via `ZDOVars.s_playerID` on the character ZDO (never `peer.m_playerID`, permanently 0 — the client never invokes that RPC). `Bed.RPC_SetOwner` writes `s_owner`/`s_ownerName`, never cleared, so a player can own many beds. **Correction: `s_inBed` + nearest-bed match is not a heuristic, it's exact** — `Bed.Interact` only permits `AttachStart` when `IsMine() && IsCurrent()` (bed matches the profile's actual custom spawn point within 1m), so observing `s_inBed=true` near a bed ZDO *proves* that bed is the active spawn bed. Prefer this rule over "most recent claim." Arrival: `FindFloor` only gates *when* the swirl ends, not *where* — the player lands at exactly the frozen target position, so height must be right server-side (bed ZDO position, not `Bed.m_spawnPoint`, which is a client-only child Transform).

**Custom spawn point (direct read)** [reactive-detection-only | small-medium, RULED OUT as a direct read, feasible as inference]. Never networked — `PlayerProfile.WorldPlayerData` lives in the local `.fch`; `ZNet.SaveOtherPlayerProfiles` collects nothing back. Inference: `Game.FindSpawnPoint` drives `peer.m_refPos` to the spawn point for the ~8s `m_respawnLoadDuration` window between death and the new character existing (2s send cadence ⇒ 3-4 samples); the *new character's spawn position itself* is the exact value once it appears. Gate strictly on `peer.m_characterID.IsNone()`, not `s_dead` (the dying player's old character lingers ~10s before `_RequestRespawn` destroys it). Discard samples matching the StartTemple icon (no custom point exists) and treat login-time `refPos` as the *logout* point, not home — it is the wrong inference and must not be conflated with spawn.

**Death spot / tombstone** [feasible | server-enforced | small-medium]. `TombStone.Setup`/`Awake` write `s_owner`, `s_ownerName`, `s_timeOfDeath`, `s_spawnPoint` — but **`s_timeOfDeath` is not tombstone-exclusive**, `Corpse.Awake` writes the identical key for creature corpses, so bootstrap scans must additionally require `s_ownerName` or filter by the tombstone's prefab hash. No tombstone exists at all for an empty-handed death or under `DeathKeepInventory`. Use `zdo.GetPosition()`, not `s_spawnPoint` (also written by `BaseAI`/`Fish`/`RandomFlyingBird` and can drift via the tombstone's own Floater/Rigidbody). Cleanup fires from `ZDOMan.m_onZDODestroyed` when the owning client's `UpdateDespawn` empties the container — the server never runs that check itself.

**Ship** [feasible-with-caveats | server-enforced-route, one-way in practice | medium-large]. Confirmed signal: `ShipControlls.RPC_RequestControl` writes `ZDOVars.s_user` (playerID) on the *ship's own ZDO* (:141326-141342); helmsman-only, and it goes stale on disconnect since only `RPC_ReleaseControl` clears it — cross-check against `ConnectedCharacters` before trusting it. Passenger detection via `Player.GetRelativePosition`'s `ConnectionType.SyncTransform` write depends on the unverified Inspector bool `m_characterParentSync` (:87362). Directly linking a portal to the ship ZDO works mechanically (reciprocal tag+connection survives the reconciler and the save format) but is architecturally pointless for a moving ship: `Player.UpdateTeleport` freezes the destination at `TeleportTo` time and holds it ≥8s (:15337-15352), so the traveller always surfaces in open water behind wherever the ship *was*. Recommend: link only when the ship's position is stationary (near-zero delta across samples); for underway ships, route through the Two-Hop Relay instead, which re-resolves position at send time rather than at portal-entry time.

**Nearest/named player** [feasible-with-caveats | server-enforced-route only, not per-player-safe | medium]. Character ZDO positions arrive at the server every physics tick, distance-independent (`ZSyncTransform.OwnerSync → ClientChanged`), far fresher than `peer.m_refPos`. Direct link (`SetConnection(Portal, characterZdo.m_uid)`) requires a Harmony prefix on `Game.ConnectPortals` to suppress the reconcile teardown (no matching tag/reciprocal link exists on a player ZDO), **and must never actually write a Portal connection onto that ZDO** — it has exactly one connection slot, shared with ship/mount `SyncTransform` parenting. Use a re-positioned Invisible Anchor instead, following moving-target discipline in full, including the per-tick `ForceSendZDO` to peers standing at the source (a character ZDO several zones away is not in anyone's normal sync range).

### Player-authored anchors

Five ZDO channels players can write without any RPC roundtrip the server needs to intercept:

| Channel | Write | Cap | Notes |
|---|---|---|---|
| Sign | `Sign.SetText` → `s_text` | 50 chars (client widget) | No RPC exists — poll only |
| Item stand | `ItemStand.UpdateAttach` → `s_item` (prefab hash) | n/a | Physical radio button; stack is forced to 1 |
| Ward | `PrivateArea` → `s_permitted` + `pu_id{i}`/`pu_name{i}` | n/a | Gates *who may retarget*, never *who may pass* |
| Chest token | `Container.Save` → `s_items` blob | n/a | Skip while `s_inUse`; claim ownership before consuming |
| Tagged portal | Player's own portal, `s_tag` prefix `@name` | 10 chars client-side | Needs **no phantom** — both tags rewritten to one identical literal is a vanilla-legal pair |

Retargeting the tagged-portal variant is the cheapest of all fixed-destination mechanisms — it is exactly the phantom-pairing primitive with the player's own real portal standing in for the phantom. All five require `SetDirtyPortals()` after tag writes, and all writes to client-owned ZDOs race that client's next higher-revision send (`RPC_ZDOData` discards anything at or below its own revision) — claim ownership first.

### Selecting a destination: the map ping

`Chat.SendPing` (client `Minimap.OnMapMiddleClick`) invokes routed RPC `"ChatMessage"` at `targetPeerID=0` carrying `(pos, type=3/Ping, UserInfo, "")` (:42058-42068) — always on the wire, no player-count dependency, unlike chat text (per-other-listed-player, zero packets solo) or slash commands (dead — swallowed locally). Capture with a prefix on `ZRoutedRpc.RPC_RoutedRPC(ZRpc rpc, ZPackage pkg)` (:83632), **not** `HandleRoutedRPC`, which never sees the authenticating socket needed to cross-check `m_senderPeerID`. The ping's Y is the pinger's own Y — recompute height via `WorldGenerator`, never trust it. Feedback pins via `RPC_DiscoverLocationResponse` are genuinely saved but **have no removal RPC** and force `SetLookDir` toward the target; a fabricated Shout for a shared marker is dropped by `RelationsManager.CheckPermissionAsync` unless the `UserInfo.UserId` is the *recipient's own* platform id or the sender id equals the recipient's own peer uid — send per-recipient, not one shared broadcast.

### Moving targets (general engine)

Two hard rules govern every option above that tracks something that moves:

1. **Never move a portal-prefab ZDO** — always the Invisible Anchor, since `SetSector` no-ops for portals.
2. **Ownership + delivery, every tick:** `a.SetOwner(GetSessionID()); a.SetPosition(p); a.SetRotation(r);` — `InternalSetPosition` only bumps `DataRevision` when owned (:73734-73745), and `AddForceSendZdos` silently drops any id that fails `ShouldSend` (:77263-77285), so an un-owned write is invisible to force-send too. Follow with `ZDOMan.instance.ForceSendZDO(peer.m_uid, a.m_uid)` for every peer near the *source* portal — `TargetFound` requests the target ZDO exactly once and never again (:143640-143658), so a moving anchor goes stale for anyone who hasn't just arrived unless the server keeps pushing it. `SendZDOs` has no delta encoding (~80-120 bytes/peer/cycle); trivial at 0.5-2Hz for a handful of anchors.

`Player.UpdateTeleport` pins the traveller at a frozen snapshot from t>2s (:15340-15346) regardless of what the anchor does afterward — the arrival is always "where the target was when the portal accepted," never live-tracked mid-transit. This is the reason a moving-ship or moving-player destination cannot be solved by ZDO tricks alone; it needs the relay pattern below.

### Two-hop relay

For a genuinely per-player or live destination — one connection slot means a single portal can't fan out — route the source to a fixed phantom lobby, then force-teleport the traveller a second time from the server once they arrive: `ZRoutedRpc.InvokeRoutedRPC(peer.m_uid, "RPC_TeleportPlayer", pos, rot, true)` (`Chat.Awake` registration, :41627; handler has zero checks, :41962-41968) or the ZDO-scoped `RPC_TeleportTo` (:886, :4126-4132, owner-gated).

The timing is the whole difficulty and is exact, not approximate: `Player.TeleportTo` refuses while `IsTeleporting()` or `m_teleportCooldown<2f` (:15302-15326); the cooldown is held at zero for the *entire* duration of hop 1, which — because portals always pass `distantTeleport:true` — cannot finish before t>8s AND `IsAreaReady` (:15349). So the cooldown does not even start accruing until ≥8s after hop 1 began, meaning the earliest an accepted hop 2 can be sent is **≥10s after hop 1 accepted**, not "a couple of seconds after arrival is observed." Schedule hop 2 at (observed lobby-arrival + 8.5s) as a fixed hold, never earlier. Send hop 2 with `distantTeleport:false` (no 8s floor) but validate the floor first — a `FindFloor` miss with `distantTeleport:false` rubber-bands the player straight back to the lobby with a misleading `$msg_portal_blocked` (:15367-15374). Pre-check `s_dead` (dead players latch `m_teleporting` forever — `UpdateTeleport` only runs in the `!IsDead` branch) and `s_inBed` (attach fights position every tick) before sending either hop. Total wall-clock: ~8s swirl + ~2.5s standing in the lobby + ~2s plain black screen (no swirl, since `ShowTeleportAnimation = m_teleporting && m_distantTeleport`) ≈ 20s round trip — design the lobby as a deliberate beat (a dressed landing pad, a toast via `PlayerNotify`), not a bug to hide.

This pattern is also the only place a server-only mod can apply *its own* rules on top of vanilla's ore/boss/NoPortals gates (already enforced on hop 1 through a real portal) — tolls, cooldowns, group checks — because hop 2 is entirely server-initiated and needs no ZDO gate at all.

### World-gen augmentation (making the destination actually be there)

Two composable mechanisms turn "portal to X, if the zone happens to be generated" into "portal to X, always":

**Destination Zone Governor** [feasible | server-enforced | medium]. `ZoneSystem.CreateGhostZones` is server-exclusive and only ever runs for the server's own pinned refpos or a connected peer's `refPos` (:113971-113991); the mod can call the same private `SpawnZone(zone, SpawnMode.Ghost, out _)` (:114071-114107) itself for any target zone, one per tick, retrying while it returns false (terrain build queued or the location's async prefab still loading). A Harmony prefix on `ZoneSystem.PlaceVegetation` injecting a `ClearArea` (private nested class, :112757-112768) at every managed destination keeps vegetation from growing through a phantom; marking a zone into `m_generatedZones` without ever calling `SpawnZone` produces a permanently barren, spawn-free pad (no `_ZoneCtrl` ⇒ no `SpawnSystem`) — but only use this on zones with no un-placed location, or you erase it.

**Target Materialiser** [feasible-with-caveats | server-enforced | small-medium]. The same `SpawnZone(Ghost)` driver, applied specifically to boss-altar/trader/dungeon target zones before or independent of any traveller, so `PlaceLocations` runs, real ZNetView children (altar pieces, the Trader NPC, `DungeonGenerator`'s room data) materialise, `m_placed` flips true, and `SendLocationIcons` fires — turning "portal available once someone's been there" into "available from world boot," and giving safety-validation code real object positions instead of pre-flattening height guesses.

### Composite worked example: the corpse-run gate

A death-triggered ephemeral pair demonstrates every primitive together: detect the tombstone's arrival (`ZDOMan.CreateNewZDO`+`ZDO.Deserialize` postfix pair, filtered on the tombstone prefab and `s_ownerName` to exclude `Corpse`), resolve origin from the player's active spawn bed (`s_inBed`/`IsCurrent()` rule), build a phantom-to-anchor pair tagged with a per-player reserved literal (zero-width prefix, so `Game.ConnectPortals`'s two-member group is stable and never randomly re-paired), push a saved pin and a toast on respawn, and tear the pair down from `ZDOMan.m_onZDODestroyed` when the grave empties — plus a hard TTL, since first-item-pickup does not despawn a tombstone. It is one-way-safe (a portal stands at both ends), server-enforced for *whether the route exists*, and entirely reactive for *when it disappears*.


---

## Dynamic routing: destinations that change

### The write primitive, and the reconciler it must defeat or satisfy

Every option below rewrites `ZDO.SetConnection(ZDOExtraData.ConnectionType.Portal, targetId)` (`assembly_valheim_SERVER.decompiled.cs:73656-73666`) — one `ZDOConnection{type,target}` in `ZDOExtraData.s_connections` (`:74951-74961`), one `DataRevision` bump, `ZDOMan.instance.SetDirtyPortals()` fired automatically only for `ConnectionType.Portal` writes. `TeleportWorld.Teleport` reads that field fresh at the instant of transit (`:143539-143546`); nothing about the destination is cached client-side. Route selection is therefore a genuinely server-owned fact — no client can teleport anywhere the field doesn't point, and none can stop it pointing somewhere new.

The obstacle is `Game.ConnectPortals()` (public, `:100589-100627`), driven every 5s under `ZNet.instance.IsServer()` (`Game.Start :100069-100074`). Pass 1 (`:100594-100606`) nulls a portal whose partner is missing, whose `s_tag` differs ordinally, or whose partner's own connection is `ZDOID.None`; pass 2 (`:100607-100622`) re-pairs anything unconnected with a random same-tag partner (`FindRandomUnconnectedPortal :100664-100679`). Prefix `Game.ConnectPortals` to return `false`, then reimplement its two passes over the *unmanaged* subset of `ZDOMan.instance.GetPortalList()` (`:77648`) while applying policy to the *managed* subset. Reproduce the `IsCurrentlyConnectingPortal` guard (`:100700-100710`) or portals owned by online peers double-pair, since their deferred `RPC_SetConnection` leaves the server's own copy reading `None` within the same tick.

Two corrections govern everything downstream. **Hook timing**: `OnWorldReady` implemented as a `ZNetScene.Awake` postfix fires *before* `ZNet.Start → ServerLoadWorld → ZDOMan.LoadChunks` (`:76474-76531`) loads any portal ZDO — install the takeover prefix at plugin load, not there, or the load-time `ZDOMan.ConnectPortals()` (private, hash-based, `:77850-77893`) and the first `Game.ConnectPortals()` call from `LoadChunks:76523` run unopposed. **Ownership is never durable**: `ZDOMan.ReleaseNearbyZDOS` (`:76901-76927`) hands any persistent ZDO within ~100m of a peer to that peer every 2s, so the `SetOwner→SetConnection→ForceSendZDO` triad (`Game.SetConnection`'s own recipe, `:100637-100651`) must be *re-applied*, not claimed once. Because `SetConnection` is idempotent (`:74954-74957`), a 1-2Hz reassert loop over the managed registry costs nothing at steady state and doubles as self-heal against the one hole nothing closes: a player standing at their own portal owns its ZDO, so `RPC_SetTag`'s `UpdateConnection(Portal, None)` (`:143591-143596`) never crosses the wire (`InvokeRoutedRPC` skips `RouteRPC` when `targetPeerID==m_id`, `:83588-83594`) — the retag is invisible to any RPC hook and arrives as ordinary, unvalidated `ZDOMan.RPC_ZDOData` (`:77076-77139`). Detect by polling, not interception.

Registry keying: never ZDOID (`ZDO.Load` renumbers every id on every load, `m_uid.SetID(++ZDOID.m_loadID)`, `:74552`) and never `s_creator` (shared by every piece one player built, `Piece.SetCreator :136415-136424`, fires on *every* player-placed piece, not just post-mod ones). Key on rounded world position.

### Fabricated destinations

`TeleportWorld.Teleport` validates the target ZDO only for existence (`:143539-143546`) — any ZDO with position + rotation is a legal arrival point. `ZDOMan.instance.CreateNewZDO(pos, prefabHash)` (`:76674-76692`) sets neither prefab nor `Persistent`; replicate `ZNetView.Awake`'s fabrication block (`:82613-82620`). A real portal-prefab hash makes `AddIfPortal` (`:77732-77752`) file it in `m_portalObjects` and dirty the portal chunk — but it becomes a live `WearNTear` object on whichever client owns it, and `m_noSupportWear` applies 100 dmg/tick without a collider under it (client `UpdateWear :150402-150450`); the server cannot collider-test at all (reference position pinned, below), so height must come from world-gen math landing on real ground. A non-portal-prefab anchor is invisible to the pairer, needs `Persistent=true` set explicitly, and only logs "Missing prefab hash" per frame on clients within range (`CreateObject :82225-82236`) rather than being destroyed.

`TargetFound` (`:143640-143656`) requires the traveller to already hold the destination or it fires `RequestZDO` and returns `false` silently. Not purely one-shot: `UpdatePortal`'s 0.5s poll (`InvokeRepeating :143450`) re-issues `TargetFound`/`RequestZDO` on every tick a teleportable player sits within `m_activationRange` (5m, `:143508`), so anyone who lingers self-heals within ~0.5s+RTT — the dead-silent walk-in only bites someone who enters the trigger *before* that closes. `ForceSendZDO(peerUid, anchorId)` (`:77714-77719`) on approach removes even that gap (see the pre-warm infrastructure below).

### The race conditions, quantified

| Event | Latency | Citation |
|---|---|---|
| Connection write reaches a standing peer | ~50-200ms (one 0.05s `SendZDOToPeers2` cycle, one peer/frame) + RTT | `:76837-76862`, `AddForceSendZdos Insert(0) :77263-77286`, `ShouldSend :76003-76014` |
| `UpdatePortal` poll | 0.5s — cosmetic glow/VFX, but also the `RequestZDO` pre-fetch while a player is within 5m | `:143450`, `:143491-143509`, `:143511-143515` |
| Trigger re-entry | **enter-only** — a player already standing inside a swapped collider does not re-fire | `TeleportWorldTrigger.OnTriggerEnter :143664-143682` |
| Teleport accept → position pin | destination snapshotted into `m_teleportTargetPos` at accept; transform force-assigned only from t>2s | `TeleportTo :15302-15327`; `UpdateTeleport :15337-15343` |
| Distant completion | t>8s **and** `ZNetScene.IsAreaReady` | `:15349` |
| Distant floor-miss fallback | forced to `GetSolidHeight+0.5` at t=15s (non-distant instead reverts to origin, `$msg_portal_blocked`) | `:15353-15374` |
| Re-teleport refusal | any `TeleportTo` within 2s of the last silently returns `false` | `:15309-15316` |
| Ownership near a player | reassigned every 2s regardless of last writer | `ReleaseNearbyZDOS :76901-76927` |
| Vanilla reconciler | 5s cycle, two-phase commit via `ClearCurrentlyConnectingPortals` | `:100589-100627`, `:100690-100698` |
| Character position freshness server-side | ~50-150ms (client's own 0.05s send cadence, distance-independent) | `:77241-77258`, `:76837-76862` |

The connection write is atomic from the client's view — one struct, one byte + one `ZDOID` on the wire (`ZDO.Serialize :74169-74173`) — no torn reads. The two costs that survive are the **silent dead walk-in** (destination not yet held) and the **enter-only trigger** (a standing player misses a swap); both are UX problems, not correctness bugs.

### Clock- and event-driven destination changes

**Rotating Hub** [server-enforced]. Advance an index into `ZDOID[] destinations`, re-resolve the hub fresh via `GetZDO` each tick (never cache a `ZDO` reference — `ZDOPool` recycles them, `ZDO.Reset` blanks `m_uid`, `:73536-73545`), write the triad. Failure modes are exactly the race-condition table: pre-warm on rotation or eat a dead first walk-in; a standing player won't re-trigger.

**Blackout Swap Protocol** [signalling client-honoured, underlying state server-enforced]. Turns the invisible swap into a three-phase legible event with the only channels a vanilla client renders: T-30s write `s_tag` (no server-side cap — the 10-char limit is the client's `TextInput.RequestText(...,10)` `:143482`, and `TextInput.Show` pre-fills the box before clamping, `:61515-61522`, so a re-saved long tag truncates) and call `SetDirtyPortals()` (`:76702-76705`, a tag-only write never sets `DirtyPortalObjects` otherwise); T-0 `SetConnection(Portal, None)` — next `UpdatePortal` sample flips hover to `$piece_portal_unconnected`, emission fades at 1.0/s, any walk-in silently no-ops at gate 1 (`:143519-143522`); re-point fires the rising `HaveTarget` edge, `m_connected.Create(...)` (`:143496-143500`) — vanilla's own connect VFX, for free.

**One-Way Ring Rotation** [runtime server-enforced, does not survive a save]. Pass 1 never checks reciprocity (`:100598-100605` tests only the partner) — A→B→C→A holds under the unmodified reconciler if every member shares a tag and none reads `None`. It collapses at save time: `RegenerateConnectionHashData` (`:75461-75478`) gives each ZDO one hash slot; C's write overwrites A's slot, so only one pair survives a restart — and comes back *bidirectional* (`ZDOMan.ConnectPortals`'s load-time relink writes both directions, `:77879-77885`) — while self-loops are erased outright. Mandatory: a full re-assert from mod state in a `ZDOMan.LoadChunks`/`ZNet.LoadWorld` postfix (not `ZNetScene.Awake` — too early), landing before `Game.ConnectPortals`'s load-time call (`LoadChunks:76523`) can randomly re-pair the orphans.

**Scheduled Routing** [server-enforced]. Three live-headless clocks: `EnvMan.GetDayFraction()`/`GetDay()` (`:96290-96303`, `m_dayLengthSec` public, default 1200, `:95406`), recomputed every `FixedUpdate` with no local-player gate (proven live because `Game.UpdateSleeping` dereferences `EnvMan.instance` unguarded under `IsServer()`, `:100712-100733`); and wall-clock time. Gotcha: `GetDayFraction()` is `RescaleDayFraction`'d then `LerpAngle`-smoothed at 0.01/tick (`:95700-95705`) and `SkipToMorning()`'s 12-second ramp (`:96310-96341`) can compress a whole night — evaluate level-triggered ("what's the target now"), never edge-triggered, and derive exact boundaries from `ZNet.GetTimeSeconds() % m_dayLengthSec` directly.

**Seasonal / Event Network Swap** [server-enforced]. A batch topology rewrite. `ZDOMan.SendZDOs` has no delta encoding — full re-serialize per peer per send (`:77056-77064`) inside a 10240-byte queue cap (`:77011-77019`) — so budget K portals/tick (Wonderland's `StructureUpkeep` caps at 256/sweep after real thousands-at-once experience) rather than force-sending an entire network at once. Cheapest execution point: the pre-peer load window, where every write is local and free.

**Randomised Roguelike Re-Roll** [server-enforced, feasible-with-caveats]. Validation must be pure `WorldGenerator` math: `Game.FixedUpdate` pins `ZNet.SetReferencePosition(1e6,0,1e6)` every tick in the *server build only* (`:100458-100466`), so every `ZoneSystem` collider helper silently returns its no-hit fallback. Use `GetHeight`/`GetBiome`/`GetTerrainDelta` (`:151944`, `:151725`, `:152364-152390`), static `IsLavaPreHeightmap` (`:115637-115646`, the only headless-safe lava test), and `AltBiomeWorldData.GetRandomPointByBiomesAboveSeaLevel` (`:92683-92696`, the same pre-validated draw vanilla uses for location placement). `FindFloor` raycasts *down* from target+1m (`:115604-115612`) — a target above true ground completes normally at t>8s with a short fall; only a target below terrain reaches the 15s fallback. Always bias upward.

**Progression-Gated Sealed Gate** [server-enforced]. Hold `SetConnection(Portal, None)` behind a global key (a custom `NonServerOption` string persists in the .db, not the .fwl — `GetKeyValue :113560-113581`), write the real destination on satisfaction. Idempotent, so 1Hz blind reassert self-heals against `Game.RPC_SetConnection` (`:100653-100662`, zero sender check) and `RPC_SetGlobalKey`/`RemoveGlobalKey` (`:116022-116037`, also zero sender check) for free. Correction: `SetStartingGlobalKeys` (`:115904-115931`) replays only server-option keys (ordinal <41) at boot; `defeated_*` and custom keys replay via `ZoneSystem.Load` (`:113800-113806`) — snapshot after both.

**Event-Driven Retarget** [server-enforced, feasible-with-caveats]. `RandEventSystem`'s update path is genuinely `IsServer()`-gated (`:106788-106818`); `GetCurrentRandomEvent()`/`SetRandomEventByName` are public, no reflection (`:107373-107376`, `:107067`). `activeBosses` (`GetGlobalKey(..., out float) :115958`) is written from whichever client has the boss instantiated with *no* `IsOwner` gate around the call (`BaseAI.SetAlerted :27256-27260`) — two clients can race and double-increment, and a crash mid-fight leaves it stuck, permanently blacking out any `NoBossPortals` gate; a watchdog zeroing it when no boss ZDO exists is mandatory. `RandEventSystem.GetBossEvent()` (`:106939-106950`) reads the client-only `EnemyHud` and is *always null* server-side — the server observes only the `activeBosses` half of `TeleportWorld.Teleport`'s `NoBossPortals` check (`:143528`), never the other.

**World-State Conditional Routing** [population server-enforced; weather ruled out]. `ZNet.instance.GetPeers()` (`:81630`) plus character ZDOs give full population/occupancy conditioning. Weather is not conditionable as often assumed: `EnvMan.GetBiome()` resolves against `Utils.GetMainCamera()`, not the pinned reference position (`:96032-96045`), and `UpdateEnvironment` returns before selecting anything with no camera (`:95950-95955`) — the server doesn't have *wrong* weather, it selects *none at all*. A server-side recompute is theoretically possible (deterministic per `(period, biome)` via `Random.InitState`, `:95963-95980`) but duplicates private logic and `GetEnvironmentOverride`; treat as unshipped research.

**Moving Destination Anchor** [server-enforced link, structurally capped experience]. Pointing a connection at a Ship ZDO works and is safe *because* ships hold no `SyncTransform` parent connection of their own (one connection slot per ZDO, `s_connections`, `:74794`) — pointing at a player instead risks the reciprocal-write paths clobbering their ship-attachment record. The ceiling is arrival physics, milder than early framing suggested: `UpdateTeleport` pins the transform to a t>2s snapshot; `m_solidRayMask` *does* include terrain (`:113424`), so `FindFloor` hits the seabed under the stale point and the teleport completes normally at t>8s — the traveller lands in open water where the ship used to be, not force-landed at t=15s. `TargetFound` re-requests a missing ship ZDO every 0.5s while absent (`:143646-143650`), not once — staleness is bounded by re-`ForceSendZDO` cadence, not a single request.

**Deterministic Assignment Replacement** [server-enforced, lowest effort]. A postfix on the private `Game.FindRandomUnconnectedPortal` (`:100664-100679`) overwriting `__result` swaps vanilla's uniform-random pick for nearest/oldest/round-robin/load-balanced, leaving the two-phase commit intact. Fixes vanilla's own permanent-orphan bug for *odd*-count same-tag groups specifically (an even group of 4 pairs cleanly as 2+2) and its restart-unstable bucket order. Naive "nearest to skip" is insufficient — the first-iterated portal claims its nearest regardless of who else prefers it; true stability needs mutual-nearest or a per-pass global match.

### How close can this get to per-player routing? The honest ceiling

**Per-Peer Divergent ZDO Routing is ruled out as a naive mechanism.** One `ZDO`, one `ZDOConnection` field. `ZDOPeer.m_zdos` (`:75963-75981`) is a revision cache, not per-peer storage; `ShouldSend` (`:76003-76014`) fires whenever a peer's cache lags the latest write, and `FindObjects` merges `m_portalObjects` into every nearby sector query (`:77367-77382`) — writing X for A and Y for B reconverges everyone onto Y within ~50-100ms. This is real for write-and-forcesend. It is not proven impossible at the wire level: `SendZDOs` serializes each ZDO fresh per peer inside its own loop (`:77050-77066`), so a serialization-layer patch could in principle substitute per-outgoing-packet values — untested, revision-bookkeeping and owner-echo problems unsolved; treat as a research spike, not a design commitment.

Two mechanisms close most of the remaining gap.

**Approach-Triggered Just-In-Time Routing** [needs-ingame-check, server-enforced]. Poll character positions at 0.1s against managed-portal positions; on crossing an arming radius (12-20m), resolve that player's destination, `ForceSendZDO` it, then write the connection. Worst-case budget ~0.45s against a ~2.0-2.4s lead time at 12m/sprint — comfortable margin if the trigger collider is small relative to the ring (unverified — `TeleportWorldTrigger` has no serialized fields, `:143664-143682`; collider geometry is asset data). Contention is the real limit: two players in the ring share one field, resolved by first-claim-lock, blackout-on-contention, or the terminal architecture below. Supporting infrastructure, each closing one gap from the race table: **DestinationPrewarm** (event-triggered `ForceSendZDO` on every write/approach/anchor-move, not only at rotation); the **Pre-Stream Engine** (a `CreateSyncList` postfix, modeled on TortalPortal's shipped `SectorSubscription.cs:106-197`, appending the destination zone's ZDOs to an approaching peer's sync list so `IsAreaReady` is pre-satisfied); **Join-Time Portal Registry Broadcast** (one `ForceSendZDO` burst per portal in an `RPC_PeerInfo` postfix, covering every static destination for the session at ~100-160B each); the **Peer Link Probe** (vanilla's own `RPC_Ping`/`RPC_Pong`, registered outside the `IsServer` block `:100065-100066`, gives per-peer RTT — note `ISocket.GetConnectionQuality` calls the *client-side* Steam interface even server-side, `:86627-86648`, and is garbage headless; use the ping round-trip instead); the **Held-ZDO Ledger** (reading `ZDOPeer.m_zdos` answers "does this player already hold the destination" before opening a gate); **Revision-Bump Redelivery** (`DataRevision += 4096` is the correct "touch" primitive — `ZDO.Set` no-ops on an unchanged value, so re-sending requires an explicit bump); and **Destination Zone Ghost Pre-Generation** (`ZoneSystem.SpawnZone(zone, SpawnMode.Ghost, out _)` for the ring before routing anyone there — a client on a dedicated server only ghost-generates zones around its *own* position; an unvisited destination has no vegetation/location ZDOs at all until someone reaches it). One dead end: **Sync-Order Knobs** (`SetType(Prioritized)`/`SetDistant(true)`) only reorder the *ordinary* sector sync list and reset the moment a portal prefab's owning client re-instantiates it (`ZNetView.Awake :82583-82590`) — useless for anything force-sent or pre-streamed.

**Queue Dispatch** [feasible-with-caveats, server-enforced]. "Next arrival gets the next destination" is arm-and-lock, not a true queue — no pre-transit event exists to queue against (see Ruled Out). Detection-after-the-fact is slower than a naive read suggests: the character ZDO doesn't move until `m_teleportTimer > 2f` (`:15337-15343`), so confirmation lands no earlier than ~2.0s post-entry — the lock must cover the *approach* window, not the confirmation latency.

**Self-Disconnecting Portals** [cooldown-parked: server-enforced; true one-shot: reactive-detection-only]. A cooldown gate writing `None` after a detected transit and restoring on a timer is solid and needs no ownership claim (the write propagates through normal near-sector sync regardless of owner, `:73656-73666`, `:77376-77379`). True "exactly one use" is unreachable: the same ~2.0-2.2s detection floor means a second entrant within that window is never seen in time — honestly, "one guaranteed use plus anyone within ~2 seconds."

**Player-Requested Routing** [server-enforced]. Best channel: `Chat.SendPing` broadcasts `"ChatMessage"` at `targetPeerID=0` with the clicked world position (`:42058-42068`) — because it targets peer 0, it always routes (`:83577-83595`), reaching the server even solo, unlike ordinary chat text (`SendText` addresses each *other* listed player individually, `:41996-42043` — a solo player sends nothing). Emotes (`s_emote`/`s_emoteID`, `:15585-15598`) and item-stand tokens (`s_item`, `:78465`) are the reliable discrete fallback. `RPC_DiscoverLocationResponse` (`:100823-100831`) can drop a permanent named pin for feedback but has no removal RPC — use once per destination.

**Character-State Predicates for JIT Routing** [feasible-with-caveats, server-enforced]. Extends the approach watcher with server-readable ZDO bits — `s_pvp` (`Player.SetPVP :15558-15563`), `s_crowned`, `VisEquipment` slot hashes (`:20569-20708`, visible slots only, never backpack), `s_stealth` — to gate an arena/challenge portal on an all-in-ring rule. The only per-player-*feeling* mechanism here that is honestly enforced by the route rather than by a client self-report, because it still writes the one shared field.

### Per-player physical portals, worked through fully

The protocol ceiling forces a physical answer for true per-player routing, and there are two distinct designs.

**Per-Player Private Portal Terminals** [feasible-with-caveats, server-enforced outbound only]. One portal per player, identified free via `s_creator` (fires unconditionally on placement) equalling a player's own `s_playerID`. Outbound is fully solved — a one-way `T_p → D_i` link is stable under the reconciler if both share a tag and `D_i`'s own connection is non-`None` (`:100598-100605`). **Inbound is structurally unsolved**: `D_i` has one connection field, so it can't send different players home differently — fixed only by one anchor per (player × destination), cheap server-side since portals are always-resident regardless of `Persistent` (`GetSaveClonePerChunk :77621-77635`), or a forced teleport home. No access control exists: `PrivateArea.CheckAccess` returns `true` vacuously server-side (`m_allAreas` populated only by a live `PrivateArea.Awake`, never headless, `:137547-137593`) — anyone physically reaching another player's terminal uses their destination; the only enforcement is architectural.

**Parked Terminal — Server-Teleport Portal** [feasible-with-caveats, server-enforced] actually removes contention, by abandoning the portal mechanism for the transit decision. Keep the connection permanently `None` under a unique reserved tag (`FindRandomUnconnectedPortal` never finds it a partner, `:100664-100679`) — the cost is the whole point: `TeleportWorld.Teleport` never runs, so `NoPortals`/`NoBossPortals`/`IsTeleportable` (unreproducible anyway) are all skipped. A 0.1-0.25s watcher fires `ZRoutedRpc.instance.InvokeRoutedRPC(peer.m_uid, peer.m_characterID, "RPC_TeleportTo", exitPos, exitRot, distant)` — the owner-gated ZDO-scoped path (`Character.RPC_TeleportTo :4126-4132`, registered on every character, `:886`) — when a player's position enters a tight radius (~1.2m XZ). Coordinates are computed and sent per-player, so **zero contention**: two players entering the same tick get their own destinations. `distant:true` reproduces vanilla's exact feel; the portal shows `$piece_portal_unconnected` and never glows — the honest tell it works differently. Guard `s_dead` (a dead player's pending `TeleportTo` latches, `:10213-10226`) and `s_inBed` (attach re-snap fights the teleport, `:10217`). Rejected calls (2s cooldown, already teleporting) are silent no-ops, so the watcher can fire every tick without special-casing.

**Fast-Transit Mode** [feasible-with-caveats, server-enforced], the `distant:false` variant: `UpdateTeleport` waits only for `IsAreaReady` before `FindFloor`, but a *miss* reverts the player to origin with `$msg_portal_blocked` (`:15360-15374`) rather than force-landing — safe only once the destination zone is confirmed generated (Ghost Pre-Generation) and the exit height confirmed above `WorldGenerator.GetHeight`, never below (rays only go down). Pre-streaming the area first turns the ~8s vanilla floor into a genuine 2-4s hop.

Composite recommendation: public destinations behave as a Rotating Hub or Sealed Gate; ordinary player travel uses Per-Player Terminals for outbound convenience; anything needing contention-free per-player dispatch (a "go home" button, an instance entrance, a tiered arena) uses a Parked Terminal. All three share the same write primitive and reconciler takeover.

### Ruled out

**Transit-Instant Decision Hook** — evaluating who is walking through as they walk through — is unreachable, not merely hard. `Teleport`'s only caller is `OnTriggerEnter`, gated on `Player.m_localPlayer == component` (`:143664-143682`); no portal GameObject exists server-side (`Game.FixedUpdate` pins the reference position, `:100458-100466`), and `Player.s_players` is permanently empty (`Player.Awake :10032-10038`). Any patch on a `TeleportWorld` member compiles and never fires. Correction: the real gate is not "the active area is unloaded" but sector arithmetic — `ZoneSystem.SectorToIndex` clamps the server's own zone and anything beyond ±16.4km to Sector 0 (`:115760-115773`), so the server does instantiate real GameObjects, just never near an in-world portal. One reactive lever survives: `Character.RPC_TeleportTo` corrects a player's position *after* a transit is detected — a bounce, not a gate.

**Roaming Portal** — moving a portal's own ZDO position — is ruled out for a subtler reason than "it isn't saved." `ZNetScene.CreateObject` applies position only at `Instantiate` (`:82011-82013`); without `ZSyncTransform` the mesh and trigger freeze at the build site regardless of writes. `ZDO.SetSector` early-returns for portal prefabs (`:73752-73757`), so the *server's* key is frozen at creation — but a client that later receives the moved ZDO re-files it under the new sector via its own `AddIfPortal` (`:77738-77748`) without removing the old entry: the failure mode is **permanent duplicate portal entries**, not "reverts on restart," because a re-keyed, dirtied portal *does* get cloned into the saved `ChunkPortal` file under its new key while old references linger. Combined with the frozen mesh, the practical outcome is a portal that forks into ghost copies over a long session — worse than doing nothing. Fabricate a new anchor and destroy the old one instead.


---

## Driving it from a vanilla client

Every mechanism below assumes the Central Constraint holds: no `TeleportWorld`, no `Player`, no `PrivateArea` GameObject ever exists server-side (`Game.FixedUpdate` reference-position pin, `assembly_valheim_SERVER.decompiled.cs:100458-100466`). Consequently every ZDO-targeted routed RPC (`RPC_SetTag`, `RPC_ToggleEnabled`, `RPC_TogglePermitted`, `RPC_Damage`, `RPC_Remove`) is dead on the server — `ZRoutedRpc.HandleRoutedRPC` needs `ZNetScene.instance.FindInstance(zdo)` non-null (:83646-83665) and it never is. The only live server hooks are: (a) global (ZDO-less) routed RPCs, which DO execute server-side; (b) `ZDOMan.RPC_ZDOData` → `ZDO.Deserialize`, which sees every field a client writes regardless of what component reads it; and (c) plain ZDO polling. Every input/output channel below reduces to one of these three.

### Input channels — what a stock client can tell the server

| Channel | Hook | Citation | Latency | Reliability |
|---|---|---|---|---|
| **Map ping** | Prefix `ZRoutedRpc.RPC_RoutedRPC(ZRpc,ZPackage)`, match `"ChatMessage".GetStableHashCode()`, type==3 | `Chat.SendPing` :42058-42071; `RPC_RoutedRPC` :83632-83644 | ~1 frame, broadcast to 0L always reaches server | High — works solo, no player-count dependency |
| **Portal tag ("The Dial")** | Postfix `ZDO.Deserialize(ZPackage)`, filter `Game.PortalPrefabHash` | `TeleportWorld.RPC_SetTag` :143587-143602; `Deserialize` body starts :74206 | one 0.05s client flush + ½RTT | High, but see ownership race below |
| **Sign text (50 chars)** | Same `ZDO.Deserialize` postfix, filter Sign prefab | `Sign.SetText` (pure ZDO write, no RPC) :141915-141929; `Sign.Interact` widget `m_characterLimit=50` :141756, :141831 | same as above | High; needs sender resolved via `ZRoutedRpc.RPC_RoutedRPC`/`ZDOMan.RPC_ZDOData`, not the Deserialize postfix alone |
| **Emote** | Poll `s_emoteID`/`s_emote` every 0.25s (Wonderland `EmoteSignals`) | `Player.StartEmote` :15585-15599; `s_emoteID` :78419, `s_emote` :78417 | ~0.25s poll | High but gated — refuses while attached/attacking/drawing bow (:15587), and un-crouches on every emote (:15591) |
| **Equipped item / back slot** | Poll `characterZdo.GetInt(s_rightItem/s_leftItem/…)` | `VisEquipment` setters :20560-20711; `Humanoid.SetupVisEquipment` :8192-8215 | ~50-150ms (ZDO flush) | High for hand+back items; only `IsEquipable` types (tool/weapon/shield/torch/armor/utility/trinket) — raw materials and trophies are never equippable, so "hold a Fine Wood" is not a real token |
| **Item stand token** | Poll `s_item` int, or catch `"SetVisualItem"` routed RPC (targetPeerID 0) | `ItemStand.UpdateAttach` :131834-131854; `s_item` :78465 | push via SetVisualItem RPC, else 0.5s poll | High; `m_stack` forced to 1 (:131843) — no stack-count encoding here |
| **Chest token + stack count** | Decode `s_items` blob (Wonderland `ZdoInventoryIO`), act only on `s_inUse` 1→0 | `Container.Save/Load` :122293-122322; `s_inUse` :78673; `InvokeRepeating("CheckForChanges",0f,1f)` :121922 | up to 1s poll + close-the-lid | Medium — the only channel with stack counts, and the only one that can charge a fee, but the reply is invisible until the chest is closed |
| **Animator trigger (jump/dodge/gpower)** | Prefix `ZRoutedRpc.RPC_RoutedRPC`, match trigger method hash, verify `data.m_targetZDO == peer.m_characterID` | `ZSyncAnimation.SetTrigger`→`InvokeRPC(Everybody,…)` :87260-87263; `"jump"` :3483, `"dodge"` :15196, `"gpower"` :15717 | one hop, pushed | High as a pulse, but jump/dodge refuse while encumbered (:3406-3410, :15186) and `ForceJump` un-crouches (:3486-3487) |
| **Crouch / block posture bit** | Poll `zdo.GetInt(438569+Animator.StringToHash("crouching"))`; `s_isBlockingHash` | `ZSyncAnimation.SetBool` :87270-87281, `c_ZDOSalt` :87144; `s_isBlockingHash` :78463 | ~50ms (session hash, on the wire, off the disk) | Medium — crouch is forced off by jump, block, run, no-stamina, swim, sleep, place-mode (:15660-15663); crouch+block is unreachable (:15662) |
| **Position (plates / approach vector / look direction)** | Poll `ZNet.GetAllCharacterZDOS().GetPosition()`/`GetVec3(s_lookTarget)` | `ZSyncTransform.OwnerSync` :87527-87534; `CharacterAnimEvent.UpdateHeadRotation` (`s_lookTarget`, 0.2s, only-on-change) :5041-5081 | ~50-100ms | High for freshness, low for discoverability — nothing in the game hints a floor tile means anything |
| **Build-piece placement as a latch** | Postfix `ZDOMan.CreateNewZDO(ZDOID,Vector3,int)` (remember id, hash still 0) + postfix `ZDO.Deserialize` (hash now known) | Wonderland `SpawnGovernor.cs:43,58`; `Piece.SetCreator` :136415-136424 | one ZDO arrival | High; also the **only** genuinely server-enforced build cap (§ enforcement) |
| **Ward toggle (as button)** | Poll `s_enabled` bool; never call `PrivateArea.CheckAccess` | `s_enabled` :78423; `CheckAccess` is a **vacuous allow-all** server-side, `m_allAreas` always empty :137173,:137197,:137547 | 0.5s poll | Low as an input gesture (disables base protection to press it); high value as a **permission source** — `s_creator`/`s_permitted`/`pu_id<i>` :78393,:78529,:137338-137363 |
| **Chat / slash commands** | — | `Chat.InputText` :41947-41955; `Terminal.TryRunCommand` (unknown command, silent, never networked) | n/a | **Ruled out** for slash commands, unconditionally. Plain shout/say is addressed per-listed-player (:41996-42042) so a solo player emits **zero** packets — *unless* `PlatformManager.DistributionPlatform.RelationsProvider` is null on the Steam backend, in which case it collapses to a `targetPeerID 0` broadcast (:42002-42006) and becomes a real channel. Single highest-value untested fact in this document. |

Ruled out entirely and not worth re-deriving: raising the 10-char tag cap (`TextInput.RequestText(...,10)` :143482 is client-only, no server hook exists) and debiting/inspecting a living player's inventory (`Player.Save` → local `.fch` only, :14091-14170; `ZNet.SaveOtherPlayerProfiles` collects nothing, :79653-79675 — the sole exception is a tombstone's `s_items`).

### Output channels — what the server can push to a stock client

| Channel | Hook | Citation | Budget | Enforcement label |
|---|---|---|---|---|
| **Tag echo** | `zdo.Set(s_tag, reply)` + `SetDirtyPortals()` + `ForceSendZDO` | `GetHoverText`→`GetTagInfo` reads live, no cache :143453-143464,:143556-143570 | ≤10 ASCII chars, no rich text (`RemoveRichTextTags`, `assembly_utils_SERVER.decompiled.cs:6700-6703`) | Server-authored, client-rendered — persistent, in-context, fastest |
| **HUD toast (per-character `"Message"`)** | `InvokeRoutedRPC(peer.m_uid, characterZdoId, "Message", type, text, 0)` | `Player.Start` reg. :10072; `RPC_Message` :14820-14825 | **one slot**, 1 msg/sec drain, no stacking (`m_msgQeue`, `UpdateMessage` :55910-55953) — corrects the common "5 toasts stack" claim | Client-honoured |
| **HUD toast (global `"ShowMessage"`)** | `InvokeRoutedRPC(peer.m_uid\|0, "ShowMessage", type, text)` | `MessageHud.Start` :55799-55809; `RPC_ShowMessage` :55848-55860 | same single-slot cap; dropped entirely while `Hud.IsUserHidden()` | Client-honoured |
| **Saved map pin** | `InvokeRoutedRPC(peer.m_uid, "RPC_DiscoverLocationResponse", name, pinType, pos, showMap)` | `Game.Start` reg. :100068; handler :100823-100830; `Minimap.DiscoverLocation` :58334-58361 | **no removal RPC anywhere in the assembly** — one-shot only, `save:true` forever; also yanks the camera via `SetLookDir` when the map is closed | Client-honoured, permanent |
| **Fabricated chat line + world text + pin** | `InvokeRoutedRPC(peer.m_uid, "ChatMessage", pos, type, userInfo, text)` | `Chat.OnNewChatMessage` :41775-41816 | chat-log line requires a **real listed player's PlatformUserID** (`Terminal.AddString(PlatformUserID,...)` looks the sender up in `ZNet.m_players` and drops silently otherwise) — the forged speaker name works only for the world-text/pin half; one shared world-text slot per sender | Client-honoured |
| **Floating world text (`RPC_DamageText`)** | `InvokeRoutedRPC(peer.m_uid, "RPC_DamageText", ZPackage{type,pos,text,mySelf})` | `DamageText.Awake` :46352-46356; `RPC_DamageText`/`AddInworldText` :46494-46510,:46395-46445 | 30m cull from **camera**, `Bonus`=3s/1.5×font >10m else 8pt; other types 1.5s; per-call jitter ±0.5m | Client-honoured — best disambiguator ("which portal") |
| **Status effect grant** | `InvokeRoutedRPC(peer.m_uid, characterZdoId, "RPC_AddStatusEffect", nameHash, true, 0,0f,-1)` | `SEMan` reg. :28724-28729; `RPC_AddStatusEffect` :28823-28850 | **no removal RPC** — usable only for finite-`m_ttl` effects, else permanent until relog | Client-honoured, weakest — borrowed semantics |
| **Forced teleport** | `InvokeRoutedRPC(peer.m_uid, "RPC_TeleportPlayer", pos,rot,true)` or ZDO-scoped `"RPC_TeleportTo"` | `Chat.RPC_TeleportPlayer` :41962-41968; `Character.RPC_TeleportTo` :4126-4132; `Player.TeleportTo` :15302-15327 | ≥8s black screen, ≥2s cooldown held at 0 the whole trip | Client-honoured — bypasses NoPortals/NoBossPortals/ore-check entirely |
| **World-wide global key state** | `ZoneSystem.SetGlobalKey`/`GlobalKeyAdd`+`SendGlobalKeys(0L)` | `NoPortals`/`NoBossPortals` read live at transit :143523,:143528 | broadcasts whole key set, one RPC hop | Client-honoured, but **`RPC_SetGlobalKey`/`RPC_RemoveGlobalKey` have zero sender check** (:116022-116037) — must be Harmony-hardened for a real lockdown |
| **Vanilla VFX/SFX one-shot** | `InvokeRoutedRPC(peer.m_uid, "SpawnObject", pos,rot,vfxHash)` | `ZNetScene.Awake` reg. :81954; `RPC_SpawnObject` :82355-82366 | effect prefabs only — a ZNetView prefab here creates a duplicate ZDO per recipient | Client-honoured |
| **Free Burst (vanilla connect FX, no prefab guess needed)** | `zdo.SetConnection(Portal,None)` → wait ≥1s → `SetConnection(Portal,target)`, both `ForceSendZDO` | `TeleportWorld.UpdatePortal` rising-edge :143491-143509; `HaveTarget` :143631-143638 | costs a real ~1s "unconnected" window, races the 5s reconciler | Client-honoured, but genuinely free — reuses the portal's own effect |
| **Ward flash (`FlashShield`)** | `InvokeRoutedRPC(0L, wardZdoId, "FlashShield")` | `PrivateArea.Awake` reg. :137202; handler :137690-137693 | none — no owner/enable check at all | Client-honoured, location-anchored |
| **Forced sleep blackout** | `InvokeRoutedRPC(peer.m_uid\|0, "SleepStart")` / `"SleepStop"` | `Game.Start` reg. :100063-100064; `SleepStart/SleepStop` :100752-100777; `Hud.UpdateBlackScreen` sleep-panel priority :47853-47871 | mod must send its own matching `SleepStop` — vanilla never will | Client-honoured; the only full-screen, input-locking cover available |
| **Removable, labelled map markers** | Inject/remove `ActivePersistentEvent` entries in `PersistentEventSystem.m_activePersistentEvents`, broadcast `"UpdateClientEventsList"` (JSON) | `Minimap.UpdatePersistentEventPins` (client) add/remove per 1s diff :57505-57549; `UpdateClientEventsList`/RPC :105407-105418 | label fixed to the chosen event's `mapTokenString`; `duration=-1` exempts it from the 30s expiry sweep :105219-105225 | Client-honoured — **the only genuinely removable named map surface**, correcting the earlier "map pins can't be removed" conclusion |

### The enforcement ladder, restated

Four tiers, and conflating them is the single most damaging error a design in this space can make:

1. **Server-enforced** — the server owns the only copy of the deciding state, full stop. Exactly: the Portal connection field itself (`TeleportWorld.Teleport` gate 1, `if(!TargetFound())return`, :143519 — a `None` connection has nowhere to send a modified client either); ZDO existence (`ZDOMan.DestroyZDO`, owner-gated send, ungated receive :76929-76999); and the build-cap via `SpawnGovernor`'s `CreateNewZDO`+`Deserialize` pair, which is the **one** place a server-only mod is strictly stronger than the full client mod (TortalPortal's cap lives in a client-side `Player.TryPlacePiece` prefix and any unmodded client ignores it).
2. **Client-honoured** — everything in the output table above, plus `NoPortals`/`NoBossPortals`/`TeleportAll`/`m_allowAllItems`-via-`LoadFields`. An honest client obeys it exactly as it obeys vanilla's own boss gating; a modified client does not, and nothing detects that it didn't.
3. **Reactive-detection-only** — the default tier for anything touching a portal's own `s_tag`, because the player standing at their own portal already **owns** its ZDO (`ZDOMan.ReleaseNearbyZDOS` every 2s, :76901-76927), so `TeleportWorld.SetText`→`InvokeRPC`→`InvokeRoutedRPC(targetPeerID==m_id)` handles locally and **never puts a packet on the wire** (:83587-83594). There is no RPC to veto; the only lever is polling `ZDO.Deserialize` after the fact and reverting.
4. **Not enforceable at all** — any per-player transit decision (item checks, PINs, "only the builder may pass"). No hook exists at the moment of transit; `TeleportWorldTrigger.OnTriggerEnter` is the sole caller of `Teleport()` and is triple-gated on a live collider, a live `Player` component, and `Player.m_localPlayer` (:143664-143682) — none of which a dedicated server ever has.

### Scheme A (recommended) — "The Dial": tag CLI + sign terminal + map ping + gesture arming

This is the only scheme built to be a complete, self-teaching player-facing UI rather than an admin convenience. Layered, each layer independently useful:

```
 L0  Pairing takeover ── prefix Game.ConnectPortals (:100589) return false;
     │                   re-implement its two passes for UNMANAGED portals only,
     │                   preserving the m_currentlyConnectingPortals guard (:100609)
     │                   and phase-0 force-commit (:100690). Removes the 5s
     │                   tag-equality teardown (:100601) that otherwise makes
     │                   per-portal tags impossible.
 L1  State ── address book {name → rounded worldPos, ownerPlayerId, locked}
     │         persisted per-world. NEVER key by ZDOID (ZDO.Load renumbers
     │         every uid on load, :74552).
 L2  Input ── four channels into one dispatcher, 500ms debounce per player:
     │         (a) tag CLI  (b) sign terminal  (c) map ping  (d) gesture (secondary)
 L3  Action ── one atomic routine per command (below)
 L4  Output ── tag echo + toast + world text, once per naming a saved map pin
```

**L3 action routine**, exactly, in order:

```
zdo.SetConnection(ConnectionType.Portal, destId)      // :73656 — no ownership check on ANY ZDO setter
zdo.Set(ZDOVars.s_tag, echo)                          // ≤10 ASCII chars, never '<'
ZDOMan.instance.SetDirtyPortals()                     // :76702 — mandatory or the rename is lost at next save
ZDOMan.instance.ForceSendZDO(zdo.m_uid)                // :77706
ZDOMan.instance.ForceSendZDO(peer.m_uid, destId)       // :77714 — pre-warm so TargetFound() (:143640) succeeds on the FIRST walk-through
```

`SetOwner(ZDOMan.GetSessionID())` is deliberately **absent** from this list, correcting the naive version of the design: claiming ownership opens a ≤2s dead window (until `ReleaseNearbyZDOS` returns it, :76874-76880) during which the very next vanilla rename the player attempts is routed to the server and silently dropped (`FindInstance==null`, :83656-83664). Server-side `ZDO.Set`/`SetConnection` need no ownership at all; `ForceSendZDO` needs none either. Every vanilla `RPC_SetTag` ALSO clears this portal's own connection and routes `RPC_SetConnected` to the *old* partner (:143590-143625) — the L3 routine must therefore run on *every* observed tag change, not just ones the mod initiated, or a player's own manual rename silently orphans their link until the mod's next tick.

**Input grammar** (tag CLI, ≤10 chars, char 0 = opcode): `>NAME`/`>N` dial · `#NAME` name this portal · `-` unlink · `!` lock · `?` help+directory · `@spawn` world target. Sign terminal (50 chars, `Sign.SetText` :141915-141929, no RPC at all — the change arrives as ordinary `ZDO.Deserialize` traffic) gets full words: `dial Haldor and lock`. Map ping (`Chat.SendPing` broadcast to 0L, :42058-42071) binds to the player's *last-touched portal*, resolved to the nearest managed portal within 64m of the re-derived height — **never** trust the packet's y, which is overwritten with the *sender's own altitude* (:42063-42064); recompute with `WorldGenerator.instance.GetHeight(x,z)` (:151944), never `ZoneSystem.GetGroundHeight` (a dead raycast server-side, silently returns `p.y` unchanged because no terrain collider exists near real coordinates, :115502-115511,:100465). Identity for the ping must be resolved in a prefix on `ZRoutedRpc.RPC_RoutedRPC(ZRpc,ZPackage)` (:83632) via `ZNet.instance.GetPeers().First(p=>p.m_rpc==rpc)` — `HandleRoutedRPC` never sees the authentic socket, and `m_senderPeerID` is a raw client self-report (:83487-83495).

**Gesture layer is secondary and must be redesigned from its first draft.** Crouch cannot be a *held* modifier across a jump: `ForceJump` calls `SetCrouch(false)` (:3486-3487) and `UpdateCrouch` also drops it on running/no-stamina/swim/attach/place-mode (:15660-15663). The only sound design is a **latch**: crouch's rising edge arms a 15s window (server reads `zdo.GetInt(438569+Animator.StringToHash("crouching"))`, :87270-87281), jump advances a candidate index (`SetTrigger("jump")`→routed via `ZNetView.Everybody`, :87260-87263, caught in a `ZRoutedRpc.RPC_RoutedRPC` prefix filtered to that character's ZDOID), and a *second* crouch rising edge commits. Emote confirm is impossible in the same flow because `StartEmote` itself un-crouches (:15591) — pick jump/dodge for the whole sequence, or emote for arm+confirm and jump for cycling, never mix crouch with emote.

**Output, three layers, always together:** tag echo (instant — `GetHoverText` reads the ZDO fresh every frame it is on screen, no cache, :143453-143464); a toast via the per-character `"Message"` RPC for anything the 10-char tag can't hold (remember: one slot, 1/sec drain, no stacking — six lines of directory read as six one-second replacements, not a stacked list); and, only when disambiguating between adjacent portals, an orange `RPC_DamageText` `TextType.Bonus` line anchored 2.2m above the frame. A saved map pin (`RPC_DiscoverLocationResponse`) fires exactly once, at first-naming, never again — there is no removal RPC, so re-pushing on every dial would litter the player's map permanently.

**Discoverability**, the honest weak point of every option in this document: seed every unnamed portal's tag to `?help`; `?` dumps the grammar as a run of toasts; the first sign built within 8m of a managed portal is rewritten once into a help card; one login toast. Four independent, passive chances to stumble in — still weaker than a real in-game help screen, which this toolkit cannot build.

### Scheme B — physical hub (item stand board / chest tokens / sign ledger)

For servers that want routing to read as base architecture rather than a command line. `ItemStand.UpdateAttach` writes `s_item` = mounted prefab hash (:131834-131854, `m_stack` forced to 1 — no stack encoding here); poll it or catch the vanilla `"SetVisualItem"` broadcast (targetPeerID 0, so it also transits the server, :131847). A wall of stands next to a portal, each labelled by an adopted sign (`Sign.Awake`→`InvokeRepeating("UpdateText",2f,2f)`, redraw only on `DataRevision` change, :141772-141865), becomes a patch panel: mount the torch, portal dials to "home"; move the torch, it re-dials. Never write `s_author="host"` on a server-adopted sign — `UpdateViewPermission` maps that to a null author and throws every 2s on every viewer (:141867-141876); write `""`. Chests add the one thing nothing else can do — a stack-count field and a real fee — but the reply is invisible until the player closes the lid (`Container.Load` refuses while `m_inUse`, :122302-122322) and any write attempted while the chest is open races the client's own `Save()` and can silently void a fee; gate strictly on the `s_inUse` 1→0 transition, never on every `OnContainerChanged`.

### Scheme C — admin/ops-only channel (not a player UX)

Forced teleport (`RPC_TeleportPlayer`/`RPC_TeleportTo`), the sleep-fade blackout (`SleepStart`/`SleepStop`), and the persistent-event map markers are correctly scoped as operator tools, not player interaction: they bypass every vanilla gate, need a per-player cooldown ledger the server itself must track (client-side `m_teleporting` is a private bool, never in any ZDO, :9901 — the server can *never* observe the cooldown it is racing), and in the sleep case require the mod to send its own `SleepStop` because `Game.m_sleeping` stays false server-side and vanilla's own `UpdateSleeping` will never close it (:100712-100733). A `Blackout Swap` — `SleepStart` → rewrite the destination graph in the dark → `RPC_TeleportPlayer` → `SleepStop` — is the cleanest way to relocate a player without the 8-second portal swirl revealing that anything unusual happened, and it is the right primitive for "admin recall home" or "world-spawn escape hatch," never for routine dialing.

### Honest comparison against TortalPortal's real client UI

| Property | TortalPortal (client mod) | The Dial (server-only) |
|---|---|---|
| Destination picker | Scrollable window, live 3D preview, favourites | 10-char tag / 50-char sign / map click / jump count |
| Discovery | In-game UI, self-documenting | Seeded `?help` tag + one login toast + a help sign — passive |
| Feedback on failure | Immediate, precise UI state | Toast + tag glyph, 1-frame-to-1-second lag |
| Per-portal item policy | Real per-portal setting | Client-honoured only, via unverified `LoadFields` |
| Build cap | Client-side prefix, any unmodded client ignores it | **Server-enforced**, strictly stronger |
| Install requirement | Every player | Zero |

Jank, scored honestly against that baseline (0 = indistinguishable from a real feature, 10 = obviously a workaround): tag CLI **3/10** — it is the exact vanilla rename interaction with a repurposed meaning, the least alien input in this whole catalogue. Sign terminal **4/10** — genuinely charming, costs real building. Map ping **2/10** — the single fastest, most native-feeling input available, limited only by discoverability. Crouch-jump gesture **7/10** — fast and hands-free but looks like nothing to a bystander and must stay optional. Chest tokens **6/10** — a real economy lever but the "must close the lid to see the result" beat is unavoidably strange. Forced teleport as delivery **1/10** visually (byte-identical to a portal transit, `Hud.UpdateBlackScreen` keys purely on `m_teleporting && m_distantTeleport`, :47872,:15386) but **9/10** functionally, since it silently carries ore the vanilla portal would have refused — never expose it to players without saying so.

The plain answer to "is Lite usable by players or only by admins": with Scheme A built to spec, it is usable by players — the tag CLI and map ping in particular require no more mechanical skill than vanilla portal renaming and map pinging, which every player already knows. It is not, and cannot be, as *discoverable* or as *immediate* as a real client UI; every input here is a repurposed vanilla verb with a learned meaning layered on top, and the entire system rests on the load-bearing, currently-unverified assumption that `GuiInputField` clamps a programmatically-assigned `.text` to `characterLimit` (§ open questions elsewhere) — if it does not, the whole ≤10-char echo discipline needs a different mechanism. Absent a companion client, this remains the ceiling: excellent zero-install destination control, mediocre zero-install destination *discovery*.


---

## Access control, security and lockdown

### Enforcement taxonomy

| Rung | Test | Representative mechanisms |
|---|---|---|
| **Server-enforced** | Server owns the deciding state, or the client code path never runs server-side | Force-disconnect (`ZDOID.None`), Per-Peer ZDO Withholding, Destroy Veto at the relay, Managed-Portal Relay Shield, Authenticated Global-Key Relay Filter, Creator Attestation |
| **Client-honoured** | Server broadcasts state an unmodified client obeys, exactly as it obeys vanilla boss gating | `NoPortals`/`NoBossPortals`/`TeleportAll`/`DungeonBuild`, `ZNetView.LoadFields`, Owner-Delegated Retag, Real Ownership Pin (against vanilla clients) |
| **Reactive-detection-only** | The write lands, is real for up to one poll interval, then is reverted | Tag Watchdog, Retag Interception at the Relay (telemetry), Anti-Grief Tag Integrity, Arrival Bouncer, Retag Rate Limit |
| **Not-enforceable** | No server hook exists on the decision at any layer | Per-Player Transit Gate, moment-of-transit veto, dungeon/crypt doors |

The reason this taxonomy exists at all: `TeleportWorld.Teleport`'s sole caller is `TeleportWorldTrigger.OnTriggerEnter` (`assembly_valheim_SERVER.decompiled.cs:143664-143682`), gated on `Player.m_localPlayer == component` — permanently false server-side because `Game.FixedUpdate` pins `ZNet.SetReferencePosition((1e6,0,1e6))` every tick in the server build only (`:100458-100466`). There is no server hook on the act of teleporting. Everything server-enforced here works by controlling what the traveller's client *reads at transit*, principally the Portal connection field, never by intercepting the transit itself.

### Identity substrate: Authentic Sender Context

`RoutedRPCData.m_senderPeerID` is deserialized straight off the wire (`Deserialize`, `:83487-83495`) and never cross-checked; `s_creator`, `s_tagauthor`, `ZNetPeer.m_playerID` are all client self-reports. The one platform-verified value is `peer.m_socket.GetHostName()`, validated in `ZNet.RPC_PeerInfo` (`:79843-79870`).

```
[HarmonyPrefix]   ZRoutedRpc.RPC_RoutedRPC(ZRpc rpc, ZPackage pkg)   // :83632-83644
   peer = ZNet.instance.GetPeers().First(p => p.m_rpc == rpc)        // GetPeers :81630
   s_current = peer        // null here means "server-originated" (self/broadcast dispatch, :83588-83591), not "unauthenticated"
[HarmonyFinalizer] same method -> s_current = null                    // Finalizer, not Postfix — a throwing handler
                                                                       // would otherwise leak the previous peer
[HarmonyPostfix]  ZDOMan.RPC_ZDOData(ZRpc, ZPackage)  ->  ZDO.Deserialize postfix reads s_current   // :77076-77140, :74206-74241
```

Meaningless for ZDO-targeted RPCs (`RPC_SetTag`, `RPC_SetConnected`) — those never dispatch server-side at all (`ZNetScene.FindInstance` always null, `:82229`); it only ever tells you about the ZDOData channel for those.

### RPC veto surface

Six routed RPCs execute genuinely on the server (ZDO-less, dispatched via `m_functions` not `FindInstance`), none sender-checked:

| RPC | Handler | Hole |
|---|---|---|
| `DestroyZDO` | `ZDOMan.RPC_DestroyZDO` `:76953-76999` | deletes any ZDO |
| `Game.RPC_SetConnection` | `:100653-100662` | rewires any portal to any ZDOID |
| `SetGlobalKey`/`RemoveGlobalKey` | `ZoneSystem.RPC_SetGlobalKey`/`RPC_RemoveGlobalKey` `:116022-116037` | any client sets/clears `noportals` |
| `RequestZDO` | `ZDOMan.RPC_RequestZDO` `:77689-77692` | forces any ZDO to any peer |
| `SpawnObject` | `ZNetScene.RPC_SpawnObject` `:82355-82366` | bare server-side `Instantiate` |
| `RequestStartEvent`/`RequestStopEvent` | `PersistentEventSystem.RPC_*` `:105341-105418` | forge/kill world events; unchecked index throws in-dispatch |

`ZRoutedRpc.RPC_RoutedRPC` (`:83632-83643`) is the only chokepoint with the authentic socket in scope; deserialize a **copy**, veto by `m_methodHash`. Two corrections to a naive design: `SetGlobalKey`/`RemoveGlobalKey` must be **key-class-aware**, not blanket-admin — vanilla clients legitimately send this RPC for `defeated_*` (`Character.OnDeath` `:3153-3165`) and `activeBosses` (`BaseAI.SetAlerted` `:27256-27262`), both ordinal ≥ `GlobalKeys.NonServerOption`(41). Classify with public static `ZoneSystem.GetKeyValue(name, out value, out gk)` (`:113560-113581`): refuse from non-admins only when `gk < 41` (`NoPortals=32`, `NoBossPortals=33`, `DungeonBuild=34`, `TeleportAll=35`) or a mod-reserved prefix; pass everything else — this is the **Authenticated Global-Key Relay Filter**, superseding a blanket-admin design. `RPC_SetConnection` needs no vanilla carve-out: `Game.SetConnection`'s deferred path always targets the *owning client* (`≠m_id`), which `InvokeRoutedRPC` never self-dispatches (`:83576-83583`) — every inbound instance is client-originated; refuse unconditionally from non-admins. Extend the same treatment to `Ship.RPC_Forward/Backward/Stop/Rudder` (`:140538-140580`, no owner check) and admin-gate the event RPCs.

**Retag Interception at the Relay** is a dead end as a lock, real as telemetry. `TeleportWorld.SetText → InvokeRPC("RPC_SetTag")` targets `m_zdo.GetOwner()` (`:143579-143585`); when the retagger already owns the portal — the common case, since `ZDOMan.ReleaseNearbyZDOS` reassigns ownership every 2 s (`:76901-76926`) — `InvokeRoutedRPC` handles locally and never calls `RouteRPC` (`:83588-83594`): zero packets, nothing to veto pre-write. But the resulting edit still arrives as `RPC_ZDOData` (`:77076-77139`), and a `ZDO.Deserialize` postfix filtered by `Game.instance.PortalPrefabHash` sees the new `s_tag` event-driven, with the pushing socket identified — reactive-detection-only, not "invisible." Attribution identifies the ZDO's *owner at write time*, not necessarily the actor if retagger ≠ owner. If the **server** owns the portal, the vanilla RPC is dropped entirely (`FindInstance` null, `:83656-83664`) — a free hard lock on retagging as a side effect of the Ownership Pin, breaking vanilla renaming while it holds.

### Ward-derived authorization

`PrivateArea.CheckAccess` is a **vacuous allow-all** on a dedicated server: it iterates static `m_allAreas` (`:137547-137592`), populated only in `PrivateArea.Awake` on a live GameObject (`:137197`) — never headless. Rebuild from ZDO data instead: `s_enabled` (bool, `:78423`), `s_permitted` count + `"pu_id"+i`/`"pu_name"+i` (no `ZDOVars` constants, `:137338-137363`), `s_creator` (`:78393`, via `Piece.SetCreator` `:136415-136424`); `m_radius` is a plain compiled field (`:137132`, default 10f — real value is asset data, read from the server's own prefab table). Build the index with `ZDOMan.GetAllZDOsWithPrefabIterative` (`:77442-77496`) or Wonderland's `PrefabSetSweeper`.

Corrected access rule: vanilla is OR-over-covering-wards, not first-match-denies (`CheckAccess`, `wardCheck=false`, `:137565-137580`) — deny only when at least one enabled covering ward exists **and none** of them grants:

```
covering = wards.Where(w => w.enabled && DistanceXZ(w.pos, portalPos) < w.radius)
denied   = covering.Any() && !covering.Any(w => w.creator == playerId || w.permitted.Contains(playerId))
```

Identity is the **profile `playerID` long** (`Piece.IsCreator` vs `Game.instance.GetPlayerProfile().GetPlayerID()`, `:136436-136441`), not a platform id. `RPC_ToggleEnabled`/`RPC_TogglePermitted` (`:137414-137421`, `:137399-137412`) both act on the **payload** `playerID`, never the routed `uid` — sender hardening accomplishes nothing there; any client can self-add to an un-enabled ward by asserting the creator's `playerID`. The index is an audit/cross-check source (reactive against a modified client), correct and complete against a vanilla population.

### Tag as namespace, and its limits

Tag comparison in `Game.ConnectPortals` is bare ordinal `!=` on `ZDO.GetString(s_tag)`, default `""` (`:100601`, `:100669`). `RemoveRichTextTags` (`assembly_utils_SERVER.decompiled.cs:6700-6703`) strips markup from the **displayed** hover text only, never from routing.

**Reserved Tag Namespace** (confirmed): server-written reserved tags have no length cap (`RPC_SetTag` validates nothing, `:143587-143602`); the client's edit box is capped at 10 characters (`TextInput.RequestText(this,"$piece_portal_tag",10)`, `:143482`), so a >10-char reserved tag is self-enforcing against honest players. On console/Xbox backends `CensorShittyWords.FilterUGC` is actually live (gate is `RelationsManager.PlatformRequiresTextFiltering()`, not "non-PlayFab") — keep `s_tagauthor` a valid `PlatformUserID` or empty to avoid the tag being censored. `TextInput.OnEnter` converts `\n`/`\t` after the cap, so control-character injection needs its own normalisation.

**Anti-Grief Tag Integrity** (confirmed-with-correction): first-claim registry with normalisation for detection only, never for the stored value. The "3-portals-one-tag" attack is weaker than first modeled — phase 1 (`:100594-100606`) never tears down an already-connected pair, so an intruder can only win a slot at a **re-pairing moment**, never displace a valid pair; and the orphan is **stable across restarts** for connected pairs, since load-time `ZDOMan.ConnectPortals` (`:77850-77893`) relinks saved pairs by hash before the runtime pass runs (`:76515` vs `:76523`). Flag a tag group only when a member is `None` while ≥2 others are paired, not on odd count alone.

**Password Tags** — ruled out as secrecy. `GetHoverText` has no access check (`:143453-143464`); any player reads any tag. The only survivable variant moves network identity into a mod-private ZDO key (`tp_net`, invisible to vanilla UI, additive on `ZDOExtraData` so it survives an honest push, `:74718-74730`), leaving `s_tag` cosmetic — but this **requires** the Managed Network Governor to fully replace `Game.ConnectPortals`, which pairs strictly by `s_tag`.

**Per-Network Quarantine-Tag Lockdown** (confirmed-with-correction): retag every member of `mine` to a unique-per-portal string — phase 1 tears the pair down, phase 2 (`FindRandomUnconnectedPortal`, `:100664-100679`) can never re-match distinct strings, no `ConnectPortals` patch needed for the reconciler side. **Not self-reinforcing against players**: `ReleaseNearbyZDOS` hands ownership to any nearby peer within 2 s (`:76901-76925`), after which the owner's vanilla `Interact → RPC_SetTag` rewrites `s_tag` locally with zero packets. A level-triggered re-assert loop is mandatory (DataRevision ties are mutually ignored, `:77110-77135`). Quarantine strings must be **longer than 10 characters** — that, not shortness, is what stops a player retyping the visible string on a spare portal. `SetDirtyPortals()` after every tag write.

### Ownership as the enforcement primitive

Naive "prefix `ZDO.SetOwner`" is insufficient: `ZDOMan.RPC_ZDOData` writes ownership through `SetOwnerInternal` directly (`:77114`, `:77128`), bypassing the public setter — it stops in-process callers (`ReleaseNearbyZDOS`, `Game.SetConnection`) but nothing against an incoming client packet.

**Real Ownership Pin** is three parts: (1) prefix `ZDO.SetOwner` — for pinned ids allow only `ZDOMan.GetSessionID()` (and `0` when explicitly unpinning); (2) postfix `RPC_ZDOData` — whenever a pinned ZDO's owner isn't the session, `SetOwner(session)` again, bumping `OwnerRevision` past the client's claim so the client's own symmetric revision check accepts the re-claim; (3) count reclaims per account, escalate to revert-and-kick (`ZNet.instance.Kick`, `:81658`) beyond a threshold. Side effect that must be documented, not discovered: a pinned portal drops **every** owner-routed vanilla RPC — retag, hammer removal (`WearNTear.RPC_Remove`, `:150411-150425`), damage/repair (`:150315-150321`, `:149522-149548`) — because the server holds no `ZNetView` instance to receive them. Server-enforced **against vanilla clients**; a modified client's ZDOData push still lands tag/connection field edits even while ownership is contested — pair with the Tag Watchdog.

**Creator Attestation** (confirmed-with-correction): notarise `s_creatorIndex → PlatformUserID` at first arrival (`CreateNewZDO` + `ZDO.Deserialize` postfix pair, `:76684-76692`, `:77131`), using server-maintained `ZNet.World.m_playerHistory` (`:81270-81281`). `s_creator` itself has no server ground truth — bind it to the sending peer's character-ZDO `s_playerID` and record it rather than manufacture a "true" value. A first-join index mismatch is a normal transient, not forgery evidence; flag only when the claimed index resolves to a *different* known account. Re-validate on every `Deserialize`, not just birth, and call `SetDirtyPortals()` after every correction. **Creator Reassignment** (confirmed): rewriting `s_creator`/`s_creatorIndex` sticks because `Piece.SetCreator` only ever writes once client-side (`:136417`) — use it to transfer, adopt admin-spawned (`s_creator==0`) portals, or migrate on roster changes; a client's already-instantiated copy shows the old value until recreated.

### The global-key family

`NoPortals`(32), `NoBossPortals`(33), `DungeonBuild`(34), `TeleportAll`(35) sit below `NonServerOption`(41): `GlobalKeyAdd` mirrors them into `ZNet.World.m_startingGlobalKeys` (`:113483-113513`), written to **.fwl** on autosave, stripped from **.db** (`:113742-113760`). `SetStartingGlobalKeys` unconditionally wipes 0..40 every boot then re-adds from the .fwl (`:115904-115931`) — a mod-owned key must be reapplied in a **postfix** there.

`NoPortals`: `SetGlobalKey("noportals")` (lowercase literal — the enum overload stringifies PascalCase and defeats `RPC_SetGlobalKey`'s dedupe, `:115938-115941` vs `:116024`) broadcasts via `SendGlobalKeys(0L)` (`:113466-113471`); read live at transit (`:143523-143527`) with **no visual warning** — hover/glow never consult it. Does not touch dungeon doors at all. `TeleportAll`: `Inventory.IsTeleportable` rejects `m_toolTier>=1000` items **before** the key check (`:68860-68880`) — never "allow all cargo." `DungeonBuild` is global across every piece in every dungeon, not portal-specific (`:13353`).

**Boss lockdown, corrected model**: the gate (`:143528`) is `NoBossPortals && (GetBossEvent()!=null || activeBosses>0)`. `GetBossEvent()` (`:106939-106950`) is **not** world-observable — a per-client HUD lookup (`EnemyHud.GetActiveBoss`, `m_maxShowDistanceBoss`=100 m). Only `activeBosses` (ordinal 47, `.db`-only) is world-wide and server-influenceable: incremented once per boss (`BaseAI.SetAlerted`, `:27256-27262`, latching `s_bossCount=true` unconditionally, dead decrement branch), decremented only in `Character.OnDeath` (`:3161-3165`). A never-killed alerted boss permanently blocks every portal. A watchdog must zero `activeBosses` only when no boss ZDO with `s_bossCount=true` is both `s_alert=true` and in any peer's active area — never "no boss exists" (bosses persist) — and clear `s_bossCount` on them, or the reset is a one-way ratchet. Client-honoured throughout.

**Progression Gate**/**Key-Item Requirement**: gate the connection, never the transit — a chest's `s_items` (`Container.Save`/`Load`, `:122294-122324`, the only live writer) or a world key can condition whether the Managed Network Governor writes the Portal connection at all. Server-enforced against vanilla clients; requires the governor to suppress vanilla's own 5 s re-pairing for gated portals, and inherits the RPC-hardening prerequisite against hostile clients. **Team/Guild Networks**: world global keys are a poor roster store — `RPC_SetGlobalKey`/`RemoveGlobalKey` have no sender check and `GlobalKeyAdd` lowercases every key, mangling a `PlatformUserID` — the mod's own position/account-keyed file must be authoritative.

### Force-disconnect: the unbypassable tier

```
foreach portal in ZDOMan.instance.GetPortalList():        // :77648-77656, whole world, always resident
    vault.record(RoundPos(portal), portal.s_tag, RoundPos(partner))
    portal.SetOwner(ZDOMan.GetSessionID())                 // :77362
    portal.SetConnection(ConnectionType.Portal, None)      // :73656-73666, sets DirtyPortalObjects
    ZDOMan.instance.ForceSendZDO(portal.m_uid)             // :77706
```

`Teleport` gate 1, `if (!TargetFound()) return;` (`:143519-143522`), is silent and resolves `GetZDO(connectionId)==null` (`:76754-76765`) — no client modification invents a destination the server never gave. Three defences are mandatory: (1) prefix `Game.ConnectPortals` (`:100589`) returning false while locked — phase 0's `ClearCurrentlyConnectingPortals` force-commits staged proposals regardless of ownership (`:100690-100698`); (2) guard `ZDO.SetOwner` **and** postfix `RPC_ZDOData` against `ReleaseNearbyZDOS` (`:76901-76926`) and incoming ownership via `SetOwnerInternal`; (3) drop unauthenticated `Game.RPC_SetConnection` and re-assert drifted connections on a 1 s tick, forcing a real revision bump.

**Boot ordering is the load-bearing correction.** `ZDOMan.LoadChunks` calls the runtime `Game.instance.ConnectPortals()` synchronously at `:76523`, inside the load call — a `LoadChunks` postfix is always one tick too late. Arm the `Game.ConnectPortals` prefix from `Plugin.Awake`, reading persisted lock state before `LoadWorld` runs at all; restore the Connection Vault in a postfix on the **private `ZDOMan.ConnectPortals`** (the load-time hash-relink at `:76515`, before the runtime call at `:76523`), never a `LoadChunks` postfix. Relabel the Vault's own guarantee **admin-facing-only** (crash hygiene, not a player rule); its persistence file needs real `FileStream.Flush(true)`+rename, not delete-then-move.

**Save-boundary transparency**: bracket `ZDOMan.PrepareSave` (`:76197-76205`, public — calls `GetSaveClonePerChunk` `:77621-77632` then `RegenerateConnectionHashData` `:75461-75491`), not `WorldSaveFinished` (~0.5 s later, `:80566-80581`, real unlocked window). `DirtyPortalObjects` double-buffering means every locked save rewrites the portal chunk twice in a row — harmless. Only round-trips pairwise topologies.

**Destroy Veto**, corrected: a prefix on `RPC_DestroyZDO` (`:76953-76961`) stops the server's own registry deletion, but `RPC_RoutedRPC` still **relays the packet to every other peer independently** (`:83636-83646`) — the veto must live at the relay, not merely the handler. `WearNTear.Destroy` already dropped build materials as real `ItemDrop` ZDOs and zeroed `s_health`/`s_support` *before* the packet was sent — a vetoed destroy leaves 0-health, visually-broken, dupe-prone portals unless the veto resets those fields. Recreation after genuine removal must mint a **fresh** ZDOID (`m_deadZDOs` force-re-destroys reuse, `:76043`, `:77133-77137`).

**Managed-Portal Relay Shield**: same treatment for `RPC_SetConnection` and `RPC_SetConnected` (`:143622-143629`) at the `RPC_RoutedRPC` chokepoint; for batched `DestroyZDO`, filter the id list and re-serialise rather than dropping the whole batch.

**Per-Peer ZDO Withholding** is the strongest, least-explored tier: a client obtains a ZDO only through `CreateSyncList`/`AddForceSendZdos`/`SendZDOs` (`:77212-77286`). A postfix on private `CreateSyncList` stripping disallowed ids makes the portal **not exist** for that client — no mesh, no trigger; a withheld destination leaves `TargetFound()` false forever with the source still showing `[Connected]`. Genuine server enforcement approaching a per-player transit gate, requiring the withheld portal be Ownership-Pinned. **Withholding Revocation** (peer-targeted `DestroyZDO` + `ZDOPeer.m_zdos` purge) and **Destination Pre-Delivery** (inject destinations for approaching members before activation range) complete it. Where withholding is too heavy, **Arrival Bouncer** is the reactive fallback: detect an unauthorised arrival, wait past the 8 s teleport floor, force the traveller back — undoes, never prevents.

### Blackout scheduling and graceful degradation

A schedule must be **level-triggered**: compute desired state every tick and reconcile, never fire once on a boundary. In-game time is `ZNet.instance.GetTimeSeconds() % EnvMan.m_dayLengthSec` or `EnvMan.instance.GetDayFraction()` (`:96290`) — there is no `Game.m_dayTimeSpeed` field.

**Graceful Lockdown**: gate 1 is silent, so the mod supplies every word. Per-character toast (`"Message"` RPC, owner-only, `:10072`, `:14820-14825`) is safest. Broadcast HUD (`"ShowMessage"`, `:55799-55809`) for connect-time notices — **no character ZDO exists yet** at `m_onNewPeer`. Fabricated `"ChatMessage"` gives chat + world text + a pin in one packet but is filtered by `RelationsManager.CheckPermissionAsync` client-side — best-effort. `RPC_DamageText` (30 m, 1.5–3 s) must be re-sent on a loop. `RemotePrint` reaches only the admin console.

**Lockdown Legibility**: `GetHoverText`/`UpdatePortal` read neither key — a locked gate looks normal until refused. Pair with force-disconnect (free legibility) or an atomic per-tag-group suffix rewrite (all members rewritten in the same frame so phase 1's tag-equality test never sees a mismatch) plus a budgeted `RPC_DamageText` loop. Restore atomically, or a 3+-member group's random re-pairing reshuffles.

**Boss/Raid locks**: vault and restore both ends of any cross-radius pair; account for `RandomEvent.m_pauseIfNoPlayerInArea` (`:107449`) — `m_time` does not advance while nobody is in the area, so a raid lock can persist indefinitely, not for a fixed duration.

**Modifier Badge Honesty**, corrected: "no supported API to re-register" is false — `ZSteamMatchmaking.instance.RegisterServer(...)` is public (`:85571-85597`) and re-issues `SetGameTags`; call it with a synthetic modifiers array (including a force-disconnect lockdown, which is not a global key at all) to refresh the browser badge on demand.

### Failure modes and runtime invariants

Assert every tick (fast ones over live `ZDOMan.GetPortals()`, `:77643-77646`; slow ones over allocated `GetPortalList()`):

1. **Vault completeness** — endpoints resolve within tolerance; drop and log on failure.
2. **Lock completeness** — connection is `None`; repair with a forced revision bump (a no-bump idempotent write does not converge a tied client).
3. **No self-loop** — survives runtime (no reciprocity check, `:100601`) but is erased at save (`:75472-75475`) — silent data loss.
4. **No dangling target** — vanilla's phase 1 already handles this within 5 s; treat as consistency-only.
5. **No duplicate portal entry** — repositioning leaves a stale bucket entry (`AddIfPortal`/`HandleDestroyedZDO`, `:73752-73756`, `:76976-76986`); `m_portalObjects` is private but mutable (`:76039`) and *is* reflectively repairable. Rule: never `SetPosition` a portal ZDO.
6. **No stale ZDO references across ticks** — `ZDOPool.Release` resets and reissues (`:73536-73549`); vanilla itself violates this in `m_currentlyConnectingPortals`. Cache ZDOIDs only.
7. **Dirty-flag discipline** — `AddIfPortal` early-returns without dirtying for an already-registered portal (`:77738-77742`); a `Deserialize` postfix that dirties unconditionally for portal prefabs is timing-proof.
8. **No `Portal|Target` bit live** — exact type equality required (`:75094-75102`).
9. **Single writer per portal per tick** — a racing second write is how detect-and-revert becomes a live flap.
10. **No portal beyond ~±16.3 km** — clamps to Sector 0 (`:115755-115774`), the server's own reference-position bucket; can be genuinely instantiated headless there.

**Panic restore**: on repeated unrepairable violation, restore the vault, clear lock state, set a sticky `Disabled` flag every subsystem checks, log loudly. Fail open, never closed.

**Mass-Rewrite Spike Control**, reversed from its original conclusion: `ZDO.IncreaseDataRevision` unconditionally dirties the ZDO's regular spatial chunk (`:73832-73840`, `:76694-76722`) — `SetSector`'s early-return exempts portals only from re-bucketing, not chunk-dirtying. A world-wide lockdown across N sectors forces a full rewrite of up to N ordinary chunks *in addition to* the always-included `ChunkPortal` file — batching across save intervals bounds this, and minimising the count of portals actually rewritten (exploit `SetConnection`'s no-op-on-unchanged return, `:74951-74960`) matters for save cost, not only network cost. `SendZDOs` has no delta encoding (`:77053-77062`, 10240-byte cap `:77010-77016`) — cap broadcasts per sweep (256, Wonderland precedent).

### Ruled out, definitively

**Moment-of-transit veto / per-portal item policy** — not-enforceable; every `TeleportWorld` field is a plain Inspector value, no player inventory is ever networked. `ZNetView.LoadFields` (`:82669-82738`) is the one unverified lead that could make `m_allowAllItems` client-honoured per-portal — flag as the highest-value in-game experiment, do not ship on the assumption.

**Dungeon/crypt doors** — `class Teleport` has no ZNetView, ZDO, or RPC of any kind (`:143316-143390`); `Ladder.Interact` and `TeleportHome.OnTriggerEnter` are two further invisible relocation paths worth cataloguing so a position-delta detector does not misattribute them.

**Per-Player Transit Gate** — ruled out as a class; `TeleportWorldTrigger.OnTriggerEnter` is triple-gated and unreachable server-side. Ship the feature as "private *networks*" (governor refuses to wire outsiders in), never "private *portals*" — a client already holding the ZDO teleports regardless of who built it.


---

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


---

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
