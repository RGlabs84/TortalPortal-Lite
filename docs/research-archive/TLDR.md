# TortalPortal Lite — TL;DR

Full detail: [TORTALPORTAL-LITE-DEEP-DIVE.md](TORTALPORTAL-LITE-DEEP-DIVE.md) (29K words) ·
[OPTION-CATALOG-VERIFIED.md](OPTION-CATALOG-VERIFIED.md) (286 options, full verifier detail) · [HANDOFF.md](HANDOFF.md) (corrections + open questions)

## Bottom line

**Worth building**, but as a genuinely different product from the client mod, not a crippled copy of it. The
server owns the connection field, the tag string, and existence/destruction of every portal — that's real
power a client-side mod never had. What it can't do is anything keyed to the *exact instant* of transit (a
real destination picker, per-transit tolls, inventory checks) — that's one call site
(`TeleportWorldTrigger.OnTriggerEnter`, client-only) that no amount of ZDO cleverness crosses.

## Why any of this works at all

- The server can create a fully persistent, correctly-paired portal with **no GameObject and no player nearby**
  ("phantom ZDO") — this is the enabling trick behind most of the catalog.
- Vanilla's own reconciler (`Game.ConnectPortals`, every 5s) will pair and keep paired anything the server
  tags identically — fight it or ride it, your choice.
- The one organizing rule: **"should this ROUTE be open right now?" is enforceable server-side; "should THIS
  PLAYER pass right now?" is not.** Every option in the catalog is one or the other — design around the first.

## The numbers

286 options verified · 97 feasible · 154 feasible-with-caveats · 12 need an in-game check · 23 ruled out ·
173 server-enforced · only 1 refuted outright (with a working replacement found).

## Best options to build first (feasible + server-enforced, low effort)

- **Topologies:** Simple Pair (the baseline), Portal Bank (one room, many exits), Roulette (let vanilla's own
  RNG do sharding).
- **Routing:** Progression-Gated Sealed Gate (opens on a boss kill), Deterministic Assignment (nearest/LRU
  instead of random), Join-Time Registry Broadcast.
- **Access:** Extended RPC Veto Table, Untagged Auto-Pair Suppression, Client-Edit Dirty Guard.
- **Ops:** Portal Dirty-Flag Guardian (stop losing renames on save), RemoteCommand Piggyback (the actual admin
  console channel — see corrections below).
- **Economy:** Progression-Gated Route Tiers, Door Lever (a vanilla door as a 3-state switch), Vault Seal
  (server-owned chest = unopenable).
- **Lockdown:** Global-Key RPC Hardening, Event Slot Governor, Beacon Cleanup Sweep.
- **UX:** Emote signalling, equipped-item destination selector, tag-echo feedback (the fastest confirmation
  surface).
- **Targeted:** Named world-gen locations, player's tombstone, player-placed anchor (sign/item/ward/chest).
- **Wildcard:** Portals As A Data Store, Decoy Gate, Admin Command Channel.

## What's NOT possible — the important ruled-out ones

- Inspecting or vetoing a specific player at the moment of transit (no server hook exists on the act of
  teleporting) — the root cause behind most of this list.
- A server-registered admin console command — vanilla's console is a client-side dictionary. (The working
  substitute is the `RPC_RemoteCommand`/`removekey` piggyback, above.)
- Raising the 10-character tag cap; debiting a player's inventory directly; per-portal item policy at transit.
- True per-player routing on a *shared* hub, a roaming/moving portal, cross-server or multi-world travel,
  relocating a base instead of the player.

## Recommended build order

0. **Foundations** — Portals-as-data-store, census, admin command channel. (S)
1. **Ownership & authority** — the ZDO-ownership Harmony guard everything else needs. (M)
2. **Topology core** — pairing takeover + phantom anchors. (M)
3. **Enforcement layer** — portal cap, ownership pin, RPC hardening. (M)
4. **Player-facing surfaces** — field injection, tag-as-broadcast. (M)
5. **World-reactive features** — event gates, beacons, location icons. (M-L)
6. *(optional, lower ROI)* Motion & companions — tame/cart/ship follow-through. (L)
7. *(optional, additive)* A ~200-300 line client companion for the handful of things structurally impossible
   otherwise.

Full detail on every item above — mechanism, citations, failure modes — is in the deep-dive doc.
