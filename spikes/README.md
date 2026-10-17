# M0 spikes

Throwaway prototypes that answer one question each before the design is fixed (TDD §25, M0). Each spike lives in its
own folder here, is not part of `Tzar2.sln`, and ends with an ADR in `docs/decisions/` that records the result.
Spike projects build with `dotnet build spikes/Tzar2.Spikes.sln` (same `Directory.Build.props`, warnings as errors;
the determinism analyzers stay on Core only).

| Spike | Question | Exit criterion | Status |
|---|---|---|---|
| S1 | Instanced sprite renderer: 10k y-sorted sprites from a texture array (DEC-10) | Windows >= 60 FPS on the desktop reference | Measured on macOS arm64 ([`S1-renderer/`](S1-renderer/README.md), [ADR 0010](../docs/decisions/0010-spike-s1-instanced-renderer.md)); Windows run pending |
| S2 | Determinism x64 vs ARM64 (fixed-point + Lua-CSharp arithmetic) | Identical hash streams; headless on macOS arm64 | Done: [ADR 0007](../docs/decisions/0007-spike-s2-determinism.md), `S2-determinism/` (`run-matrix.sh`) |
| S3 | LiteNetLib relay lockstep prototype | Windows <-> Windows plus a headless Linux client | Done on macOS + Linux containers; Windows via nightly CI ([`S3-net/`](S3-net/README.md), [ADR 0009](../docs/decisions/0009-spike-s3-lockstep-relay.md)) |
| S4 | Lua-CSharp sandbox and instruction budget (DEC-13b) | Budget enforced deterministically; sandbox escapes blocked | Done: [ADR 0008](../docs/decisions/0008-spike-s4-lua-sandbox.md), `S4-lua/` (`Tzar2.Spikes.S4 all`) |
| S6 | Sprite memory measurement and GPU format decision (REQ-GFX-03) | Measured volume; one of the layouts (a)/(b)/(c) chosen | Done: measured by `t2 import` (112.2 MB for all 961 sheets in the chosen layout); layout (c) chosen, see [ADR 0013](../docs/decisions/0013-spike-s6-sprite-memory.md) (no spike folder: the measurement lives in the importer) |

S5 (Godot .NET export to Android/iOS) moved to the start of the platform-expansion phase (DEC-14).
