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
