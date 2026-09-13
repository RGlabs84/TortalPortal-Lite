# TortalPortal Lite — Concept Spec (Valheim 1.0.7 Decompile-Verified)

**Status: not built. This is a decision document, not a roadmap commitment.**

This started as a portal deep-dive done for **Wonderland** (a separate, strictly-server-side-only Valheim mod)
and was pulled out here on 2026-09-11 because a full portal-network feature belongs with the portal mod, not
bolted onto a general-purpose one. It's landing in `TortalPortal/` rather than `Wonderland/` because full
**TortalPortal** already owns this problem space — a procedural client UI, a portal listing with a destination
photo/live view, favorites, per-gate locks and marks (see its own README) — and everything below is really
asking one question: *how much of that could exist as a "Lite" variant with **zero client install**, the same
philosophy Wonderland is built on?*

The answer, after a 13-agent audit of `assembly_valheim_SERVER.decompiled.cs`/`assembly_valheim.decompiled.cs`
(research pass + independent skeptic pass per topic, plus a completeness sweep): **less than it looks like at
first, because of one load-bearing fact, but the part that's real is genuinely real and needs no client mod.**

## The Central Constraint

**`TeleportWorld.Teleport()` — the method that actually moves a player through a portal — never executes on a
dedicated server, full stop.** Its only caller (`TeleportWorldTrigger.OnTriggerEnter`) is gated on
`Player.m_localPlayer`, which a dedicated server's `Game.FixedUpdate` never populates (no portal
GameObject/collider is ever instantiated server-side at all — `ZoneSystem`'s local-zone creation is gated on the
same permanently-pinned reference position). Consequence: the decision of *where a specific player ends up at the
moment they walk through a specific portal*, the ore/metal item-restriction check, and the `NoPortals`/boss-lockdown
block are **100% client-executed, per real player, with zero server hook** — not a Wonderland-style constraint to
work around, just how vanilla is built. This is exactly why full TortalPortal *has* to be a client mod to do what
it does (a live-rendered destination window, a real-time picker) — that class of feature is not reachable any
other way.

**What IS real and server-authoritative** is the layer underneath: portal *pairing*, *tags*, and each portal's
*destination* are all plain, directly-writable ZDO fields. `Game.ConnectPortals()` (public, runs from a coroutine
started whenever `ZNet.instance.IsServer()`) re-evaluates every same-tagged pair every 5 seconds using nothing but
`ZDOMan.instance.GetPortalList()` — no `Player.m_localPlayer` dependency anywhere. Vanilla itself already takes
ownership of an offline portal's ZDO to rewrite these fields (`SetConnection`, `portal.SetOwner(sessionID)`) — the
exact same pattern Wonderland's `WaterBuoyancyEngine.cs` uses for waterborne items. So a server-only mod can fully
rewrite **where a portal points and what it's tagged**, ahead of time, purely as ZDO writes — it just can never
intercept or veto the moment a specific player uses one, or see what they're carrying.

## What a "Lite" Mod Could Actually Ship (server-only, zero client install)

Tagged **[Feasible]** / **[Feasible, with caveats]** / **[Not feasible]** and an effort size. "Vanilla-respecting"
items are honored by any unmodified client the same way vanilla's own boss/no-portals gating already is — not a
harder guarantee than stock Valheim, but not a regression either.

### Group 1 — Portal network & destination rewriting (real ZDO writes, works on any vanilla client)
- **[Feasible, small]** Instant re-pairing after a tag change — a short server timer re-calling the public `Game.instance.ConnectPortals()` instead of waiting on vanilla's 5s coroutine.
- **[Feasible, medium]** Admin-defined fixed networks — rings, hub-and-spoke, one-way links — via direct `SetConnection` writes instead of trusting vanilla's random same-tag pairing. A ring is self-stable under vanilla's own self-heal check as long as members share a tag and stay non-`None`; a fan-in hub member can self-loop to stay "connected" without a real return route.
- **[Feasible, small-medium]** Deterministic/preferential pairing (nearest, oldest, admin-designated) replacing `FindRandomUnconnectedPortal`'s `UnityEngine.Random.Range` pick.
- **[Feasible-with-caveats, large]** Rotating multi-destination "hub" — the connection field only ever holds one ZDOID, so a true simultaneous multi-target hub is impossible; only time-sliced rotation on a timer works, and a player standing at the hub mid-rotation can have their destination change under them (`TargetFound`/`UpdatePortal` poll every 0.5s).
- **[Feasible, small]** Direct tag write/rename bypassing the RPC and the 10-character text-entry UI entirely — `ZDOVars.s_tag`/`s_tagauthor` are plain strings, writable any time. (This is "Every Gate Its Own Mark" from full TortalPortal, minus the picker UI — a server admin or automation writes the tag, the player just sees it take effect.)
- **[Feasible, small]** Reserved/admin tag namespace (e.g. an `ADMIN:` prefix only server code can set) — vanilla's tag matching is bare string equality with zero reserved names.
- **[Feasible, small]** Server-side tag length cap and profanity filtering at *write* time — vanilla only enforces the 10-char cap in the client UI widget and only profanity-filters at *display* time; the RPC itself stores anything verbatim.
- **[Feasible, small]** Portal to nearest/named boss altar — the real altar is `OfferingBowl`, an always-present world-gen ZDO (not the Vegvisir discovery-tracking rune stone); a plain spatial ZDO scan finds every altar with zero new bookkeeping.
- **[Feasible-with-caveats, medium]** Portal to a player's claimed bed — exact only when a player owns exactly one bed (`Bed.RPC_SetOwner`/`ZDOVars.s_owner` is server-readable); "currently active" bed with multiple beds lives only in that player's local, non-networked save data.
- **[Feasible-with-caveats, small]** Portal to a player's live-attached ship via the `ZSyncTransform`/`ZDOExtraData.ConnectionType.SyncTransform` link a rider's own client already writes — exact if confirmed live; whether `Player`'s prefab actually has `m_characterParentSync` enabled is Inspector data invisible to the decompile and needs an in-game check.
- **[Feasible-with-caveats, small]** Portal to nearest ship (cruder proximity fallback) — can't distinguish "on the ship" from "on the dock beside it."
- **[Feasible, small]** Portal to world spawn / start location, via `ZoneSystem.GetLocationIcon(m_StartLocation)`.
- **[Feasible, medium]** Portal to any computed coordinate — the general technique underlying several options above: `ZDOMan.CreateNewZDO(pos, prefabHash)` makes a real, persistent, correctly-owned ZDO with no live GameObject needed; the client never validates a portal's target prefab type.
- **[Feasible, medium]** Group-integrity safety net — a faster-than-5s tick that re-writes every managed portal's intended connection so vanilla's own reconciliation pass never randomly re-pairs a briefly-orphaned ring/hub member with an unrelated portal.
- **[Feasible-with-caveats, medium]** Distinct per-portal display tags within one managed group — vanilla's self-heal transitively forces every linked portal to share one literal tag string, so this needs patching/bypassing that equality check.
- **[Feasible, small]** "Every Portal in the Realm, Listed" — admin/Discord-facing only, not an in-game player UI (that part of full TortalPortal is unavoidably client work): `GetPortalList()`/`GetPortals()` gives a free enumeration (tag, position, connection, owner) for a log or a Discord channel. Same free read gives orphaned/broken-tag detection (3+ portals sharing a tag permanently strands one under vanilla's strictly-pairwise algorithm).
- **[Feasible, medium]** Proactive server ownership claim on placement — same pattern `Wonderland`'s `WaterBuoyancyEngine.cs` already uses, makes RPC interception more reliable.

### Group 2 — Global toggles ("Decide What Travels", vanilla-respecting)
- **[Feasible, small]** Allow-all cargo through portals — `GlobalKeys.TeleportAll`, via the same `GlobalKeyAdd`/`SendGlobalKeys` pair Wonderland's `WorldRatesEngine.cs` already uses for `carryweightrate`. This is *global*, not per-portal — `TeleportWorld.m_allowAllItems` is a plain, non-ZDO MonoBehaviour field with no networking, so a per-portal version is not achievable (see Ruled Out).
- **[Feasible, small]** Disable all portals server-wide — `GlobalKeys.NoPortals`.
- **[Feasible, small]** Auto-disable during boss fights — `GlobalKeys.NoBossPortals` (vanilla computes "is a boss active" itself).
- **[Feasible, small]** Scheduled portal blackout windows on top of the above two.
- **[Feasible, medium]** A genuinely *unbypassable* lockdown — instead of relying on the client-honored `NoPortals` key, force-disconnect every portal pair server-side (`SetConnection(..., ZDOID.None)`) so `TargetFound()` is false even for a hacked client.
- **[Feasible-with-caveats, small]** Allow portal placement inside dungeons — `GlobalKeys.DungeonBuild`; still client-honored only.
- **[Feasible-with-caveats, small]** Keep the public server-browser "modifiers" badge honest — Steam/PlayFab registration snapshots `ZNet.m_world.m_startingGlobalKeys` once and never re-registers on a later live toggle; mutate that list alongside any runtime key change. Cosmetic only.
- **[Ruled out]** The built-in `noportals` server console command — its body is wrapped in `if (Player.m_localPlayer != null)`, unlike the neighboring `nomap` command which correctly branches on `ZNet.instance.IsServer()`. Inert from a dedicated server's own console; use `SetGlobalKey` directly instead.

### Group 3 — Force-teleport a player without a portal (separate, genuine server→client RPC path)
- **[Feasible, small]** Admin summon/forced recall to arbitrary coordinates — reuse vanilla's `RPC_TeleportPlayer`/`Character.RPC_TeleportTo` via `ZRoutedRpc.InvokeRoutedRPC` directly, **bypassing `Chat.instance` entirely** (Chat-singleton assumptions keep turning out dead on a dedicated server elsewhere in this codebase; routing the RPC yourself sidesteps it). A forced teleport issued within vanilla's 2s post-teleport cooldown silently no-ops with no error either side.
- **[Feasible, medium]** Self-service named warps (`/home`, `/warp`, or emote-triggered) against a server-held name→coordinate registry. Chat-command parsing is unreliable server-side — an emote or item-interaction trigger is the safer entry point, not a slash command.

### Group 4 — "Lock Your Gates" (access-control hardening)
- **[Feasible, medium]** Harden RPC sender identity against spoofing — `RoutedRPCData.m_senderPeerID` and `RPC_SetTag`'s `authorId` are both raw, unverified client self-reports; nothing cross-checks them against the real `ZNetPeer` that owns the connection the packet arrived on. Prerequisite for trusting *any* player-identity argument on a portal-adjacent RPC.
- **[Feasible, small]** The same hardening should cover `PrivateArea.RPC_ToggleEnabled`/`RPC_TogglePermitted` (ward permission RPCs) too — identical trust gap, one hop upstream of portal tag protection.
- **[Feasible-with-caveats, large]** Ward-gated portal retag/relink protection — `PrivateArea`'s own access checks key off `Player.m_localPlayer`/local save data and can't be called as-is server-side; needs a Wonderland/TortalPortal-Lite-maintained shadow list of wards plus the hardened identity above to reject an `RPC_SetTag` from a non-permitted player. Caveat: this can only protect *changing* a portal's tag/link inside a ward, never block someone already using an existing linked portal (see the Central Constraint).

### Group 5 — Detection & economy (reactive-only, after the teleport already happened)
- **[Feasible-with-caveats, medium]** Portal-use detection/logging (Discord notice, stats) — reuses the exact position-straddle heuristic Wonderland's `PositionWatch.IsPortalTransit` already ships with; same false-positive risk class already known there.
- **[Feasible-with-caveats, medium]** Portal toll/usage fee via an adjacent chest — detect a completed transit, then debit a *nearby server-visible container*, never the traveling player's own inventory (which is never networked to the server at all). Reactive only — cannot block non-payment.

## Ruled Out — Confirmed Dead Ends (the honest limits of "Lite")
- **True per-visit, decided-in-the-moment routing** ("two players walking through seconds apart go to different places based on something chosen at that instant," or any live in-game picker UI) — impossible; no server-side collider/trigger for a portal ever exists to fire on, and rendering any picker at all is inherently client work. This is the feature gap full TortalPortal's client UI exists to fill and "Lite" fundamentally cannot.
- **Server-side item-restriction enforcement** (blocking ore/metal even against a modified client) — the only check lives inside the client-only `Teleport()`, and the server can't inspect a player's inventory anyway (never networked).
- **Per-portal "allow all items" toggle** — `TeleportWorld.m_allowAllItems` is a plain compiled field, never ZDO-backed or RPC'd; a server Harmony write only changes the server's own dead-end copy. Use the *global* `TeleportAll` key instead if all-or-nothing server-wide is acceptable.
- **Finer-than-all-or-nothing item allowlists** (e.g. copper but not iron) — only one check exists in the whole assembly and it's binary.
- **Server-enforced max teleport distance/cooldown override** — `Character.TeleportTo`/`UpdateTeleport` run only on the ZDO-owning client; a non-owner immediately forwards to the owner. No server execution point exists.
- **`TeleportHome`/`Game.RequestRespawn` as a force-respawn primitive** — unconditionally gated on `Player.m_localPlayer`, a confirmed no-op on a dedicated server.
- **`m_onlyInTeleportArea` placement restriction** — read only in the client-only ghost-placement preview; no `GlobalKeys` or other server lever exists for it.
- **Ashlands `portal_stone`'s baked-in `m_allowAllItems` default** — the value lives in Unity asset/AssetBundle binary data, not the C# decompile; needs an in-game check, not a code fix, to confirm.

## One More Mechanism To Track Separately
Vanilla ships a second, architecturally distinct teleport component — class `Teleport` (not `TeleportWorld`) — used
for crypt/dungeon interior doors. It sends a character to a fixed `Transform`-based exit point (no paired ZDO), has
its own narrower `NoBossPortals` check (blocks only the outbound trip from inside an *active* boss's own dungeon),
and tracks separate `PortalDungeonIn`/`PortalDungeonOut` stats instead of `PortalsUsed`. Any world-wide lockdown,
audit, or usage-tracking feature built around `TeleportWorld` should account for this second mechanism too, since
it shares the `NoBossPortals` key but gates a functionally different, non-portal teleport.

## Where This Came From
Produced 2026-09-11 by a 13-agent decompile audit (research + independent skeptic pass per topic, plus a
completeness sweep) against `libs-Tools/1.0/DECOMPILED/assembly_valheim_SERVER.decompiled.cs` and
`assembly_valheim.decompiled.cs`. Every claim above traces to a specific class/method/line number in that audit's
raw output; ask for the citations for any specific item before building against it rather than re-deriving from
scratch.
