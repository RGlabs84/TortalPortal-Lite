# TortalPortal Lite — deep-dive: COMPLETE

**Finished 2026-09-12.** The full pipeline ran to completion: 114 agents total, 0 errors on the final pass
(two rounds of section-writers hit transient server-side rate limiting and were simply retried). Workflow run
`wf_426424f2-e93`, across three sessions (`73b09bc9…`, `03a665ba…` twice more).

## Deliverables

- **[TORTALPORTAL-LITE-DEEP-DIVE.md](TORTALPORTAL-LITE-DEEP-DIVE.md)** — the actual answer. Eight written
  sections (foundations, topologies, targeted portals, dynamic routing, UX, access control, economy/ops,
  limits) assembled into one document, ~29,000 words, synthesized from the fully-verified 286-option catalog.
  Read this first.
- **[OPTION-CATALOG-VERIFIED.md](OPTION-CATALOG-VERIFIED.md)** / **`.json`** — every one of the 286 options
  with its full designer spec, both adversarial verifier passes, corrections, citations and reasoning. This is
  the evidence backing every claim in the deep-dive doc; the JSON also carries the 12 mechanism-research
  reports verbatim and the 3 completeness critics in full.
- **`OPTION-CATALOG-PREVERIFY.md`** — superseded. Pre-verification draft catalog, kept only for history.
- **`TORTALPORTAL-LITE.md`** — the original spec this whole effort was auditing. Corrections below.

## Headline numbers

286 options total (197 first-round + 89 from gap-fill triggered by the completeness critics).
By feasibility: 97 feasible, 154 feasible-with-caveats, 12 needs-ingame-check, 23 ruled-out.
By enforcement: 173 server-enforced, 54 client-honoured, 25 admin-facing-only, 17 reactive-detection-only,
17 not-enforceable. 1 option refuted outright in round one (an admin console-command channel — client-side
`Terminal.TryRunCommand` checks its own dictionary, so the server can't register into it; the working
replacement is gap-fill #198, RemoteCommand Piggyback). 208 options carry at least one verifier correction.

## One thing worth knowing about the catalog's provenance

Two gap-fill domains (`targeted`, `ops`) were verified twice: once in an earlier session, and again in this
session after a resume unexpectedly re-hashed those two verify calls (everything else — all research, design,
round-1 verify, critique, and all 8 gap-fill designers — replayed cleanly from cache; only those two verify
calls didn't). The numbers above and the catalog on disk reflect the **second, final pass**. The two passes
mostly agreed; where they didn't, it was minor (a couple of corrections resolved rather than restated, one
option's feasibility moved a notch in each direction, two enforcement labels went from client-honoured to
server-enforced on closer reading). Nothing was refuted that wasn't refuted before, and no option's identity
or position in the catalog changed — only fine-grained verdict details on those 11 options.

## Corrections to `TORTALPORTAL-LITE.md` (verified — act on these)

Confirmed against the decompile:

- `Game.SetConnection` is not a plain server write: when the portal is owned by an online player it delegates via
  `ZRoutedRpc.InvokeRoutedRPC(owner, "RPC_SetConnection", …)`; only `owner == 0`, a dead peer, or
  `forceImmediateConnection` takes the local path. All three members are `private` on `Game`.
- Reconciliation is a staged two-tick commit (`ClearCurrentlyConnectingPortals`, `IsCurrentlyConnectingPortal`), not a
  single 5 s pass.
- The reconcile predicate `target == null || target.tag != my.tag || target.connection == None` never tests
  reciprocity (why rings/fan-in are runtime-stable — but see the save-boundary rules in section 2 of the deep-dive).
- `TeleportWorld` registers `RPC_SetConnected` (ZDOID) alongside `RPC_SetTag`; `UpdatePortal` polls every 0.5 s;
  `m_activationRange = 5f`, `m_exitDistance = 1f`.
- `ZDOMan.RPC_DestroyZDO` has no sender check and no ward check.
- The "Ruled Out: per-portal allow-all-items" entry is wrong — `LoadFields` reaches `m_allowAllItems` per instance
  (client-honoured). The "admin console command" idea is wrong as written but works via the `removekey`-style
  remote-command piggyback (see gap-fill #198). `nomap` is as inert as `noportals` on a dedicated server.
- Reported by research, not independently re-verified: `TeleportAll` does not lift the `m_toolTier >= 1000`
  rejection; dungeon `Teleport` ignores `NoPortals` and the ore check; tag writes need `SetDirtyPortals()`;
  `ReleaseNearbyZDOS` (2 s) defeats a permanent ownership claim; ZDOIDs regenerate per load; `CreateNewZDO(pos, hash)`
  sets neither `Persistent` nor the prefab hash; every collider-based `ZoneSystem` query is dead server-side (reference
  position pinned to 1e6) — validate positions with pure `WorldGenerator` math; ward-gated retag veto is impossible
  because an owning client's `InvokeRoutedRPC` never leaves the client, and `PrivateArea.CheckAccess` is a vacuous
  allow-all server-side.

## Open questions needing an in-game check, not a code read

All carried into section 8 of the deep-dive doc as a formal table with the exact check to run for each; listed
here too for quick reference (option numbers refer to `OPTION-CATALOG-VERIFIED.md`):

- Is `m_characterParentSync` enabled on the Player prefab? (gates ship-rider detection; fallback is `ZDOVars.s_user`
  on the Ship ZDO = helmsman only)
- Does `portal_stone` ship a different `m_allowAllItems` default? (no code difference between portal variants — all
  Inspector data)
- Is `m_syncInitialScale` set on portal prefabs? (gates #146 scaling)
- Extent of the `TeleportWorldTrigger` collider volume vs `m_exitDistance` — is the destination trigger already
  entered on arrival? (affects every ring/chain "immediate bounce" claim)
- Does the headless server instantiate sector-0 objects at (1e6, 0, 1e6)? (#143/#244 — Heightmap readiness, prefab
  availability)
- Is `System.Net.HttpListener` functional under the game's Mono runtime on the dedicated server? (#76/#203)
- Does `RelationsProvider`/chat routing deliver plain chat to the server for parsing? (#271)
- Geometric-centre-of-base inference from build ZDOs — which pieces carry a creator id? (#195)
- Corpse-Run Gate: tombstone ZDO lifecycle vs death timing (#207)

The designed answer to all of these is gap-fill #202 **CapabilityProbe**: a boot-time census that evaluates each
predicate against live Inspector/singleton data and logs the result, so no option ships with an unresolved
`needs-ingame-check`.

## Suggested next steps (not done, no one's asked for them yet)

- Archive `OPTION-CATALOG-PREVERIFY.md` (e.g. move under an `archive/` folder) now that `OPTION-CATALOG-VERIFIED.md`
  fully supersedes it.
- Run the CapabilityProbe checks above against a live dedicated server before starting implementation.
- Section 8 of the deep-dive doc has a concrete phased build order — start there.
