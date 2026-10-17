# M0 spikes

Throwaway prototypes that answer one question each before the design is fixed (TDD §25, M0). Each spike lives in its
own folder here, is not part of `Tzar2.sln`, and ends with an ADR in `docs/decisions/` that records the result.

| Spike | Question | Exit criterion | Status |
|---|---|---|---|
| S1 | Instanced sprite renderer: 10k y-sorted sprites from a texture array (DEC-10) | Windows >= 60 FPS on the desktop reference | Measured on macOS arm64 ([`S1-renderer/`](S1-renderer/README.md), [ADR 0010](../docs/decisions/0010-spike-s1-instanced-renderer.md)); Windows run pending |
| S2 | Determinism x64 vs ARM64 (fixed-point + Lua-CSharp arithmetic) | Identical hash streams; headless on macOS arm64 | Not started (Core golden hash in `tests/Tzar2.Determinism.Tests` is its seed) |
| S3 | LiteNetLib relay lockstep prototype | Windows <-> Windows plus a headless Linux client | Not started |
| S4 | Lua-CSharp sandbox and instruction budget (DEC-13b) | Budget enforced deterministically; sandbox escapes blocked | Not started |
| S6 | Sprite memory measurement and GPU format decision (REQ-GFX-03) | Measured volume; one of the layouts (a)/(b)/(c) chosen | Not started |

S5 (Godot .NET export to Android/iOS) moved to the start of the platform-expansion phase (DEC-14).
