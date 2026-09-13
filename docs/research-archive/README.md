# Research archive

Everything in this folder describes the pre-v1.0.0 research and exploration phase of TortalPortal
Lite, not the shipped product:

- `OPTION-CATALOG-PREVERIFY.md` / `OPTION-CATALOG-VERIFIED.md` / `OPTION-CATALOG-VERIFIED.json` — the
  full 286-option catalog a 114-agent research pipeline produced, plus its adversarial verification
  pass (263 buildable, 23 ruled out).
- `TORTALPORTAL-LITE-DEEP-DIVE.md` / `deep-dive/` — the synthesized architectural deep-dive written
  from that catalog.
- `TORTALPORTAL-LITE.md`, `HANDOFF.md`, `TLDR.md` — planning and handoff notes from the build-out of
  all 263 options.
- `FEATURE-STATUS.md`, `WORKING-FEATURES.md` — the working/needs-more status of all 263 built
  options, at the point the mod still implemented all of them.

None of this reflects what actually ships. The product owner picked exactly 14 of those 263 options
as the final feature set, and every other option's code was removed from the codebase entirely
(recoverable via `git log` on this repo if it's ever needed again). For the current, accurate
reference, see `../FEATURES.md`.
