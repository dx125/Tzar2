# 0182. Keep the core free of any engine and of the original's content, and move UI logic into engine-free models

* **Status:** Accepted
* **Date:** 2026-10-21
* **Deciders:** agent (maintainer); product owner (asked how ready the code is for a new game on the same core, 2026-10-21)
* **Related:** TDD §7, §11, REQ-ARCH-01..03; ADRs 0002, 0031, 0032, 0071, 0181

## Context

The owner plans, later, a new game built on Tzar2. It would have:

* a new balance;
* changed units and new civilizations;
* perhaps a 3D engine;
* the same logical core.

The question was how ready the code is, and what must be kept apart to allow it.

**Already in place:**

* **No engine in the core.** `Tzar2.Sim`, `Tzar2.Scripting` and `Tzar2.AI` reference no engine and do no I/O
  (`Tzar2.Architecture.Tests`). The simulation is deterministic integer code, so multiplayer and replays work with any
  client.
* **Rules are data.** YAML rulesets inherit and patch one another (ADR 0031); `remastered` is a patch of `classic`.
  The import of the original's tables is an optional bottom layer (ADR 0032), so units, buildings and civilizations
  can be written in YAML alone. Civilizations are strings of the rules, not code.
* **Godot stays in the client.** The presentation reads the game only through `GameFrame`, the commands and the
  events (REQ-ARCH-03). Packages, the VFS, the editor and the native map formats carry content of any game.

**What ties the code to the original and to Godot:**

* **About a thousand lines of game UI logic live in the Godot client** (`GameScreen.cs`):
  * the command card: which buttons, whether each works and why not, its price, its queued count, what a press
    sends;
  * the selection view and the event messages.

  A new client would have to write them again, and CI cannot test them (play-test findings came from there).
* **Original-specific code paths:**
  * The rules compiler holds the importer of the original's tables (`Tzar2.Content/Rules/Legacy*`).
  * A few paths name the original's classes (`LegacyClass == "castle"` in the victory check).
  * The legacy AI language knows only `european`, `asian` and `arabian`.
  * Card layouts, texts and pictures come from `MENU.INI`, `STRINGS.INI` and `IMAGES.INI`.
* **The frame is shaped for 2D sprites:** an 8-way facing, no height. A 3D client wants the full heading. Height that
  matters for play (high ground, sight) would be a change of the simulation, not of the client.
* **Some numbers of the rules come from the 2D art:** hit frames, walking strides, footprints from picture boxes.
  They are fine as long as they are turned into plain numbers when the rules are compiled.
* **Hosting the core in another engine:**
  * The libraries target .NET 8 with the latest C#. Godot 4 (2D or 3D) hosts them as they are.
  * Another C# engine must run modern .NET; check this when choosing one.
  * A C++ engine (Unreal) would host the core as a NativeAOT library with a C API. The projects are already
    AOT-compatible (REQ-ARCH-02).

## Decision

1. **Three layers.**
   * **The core:** Core, Sim, Scripting, the AI runtime, Net, Replay, Game (sessions, input semantics and the UI
     models of this ADR) and Content (packages, VFS, rules compiler). It is game-neutral and free of any engine.
   * **The original's content:** Formats, Import, the legacy table importer, the legacy AI language, the `classic` and
     `remastered` rulesets, and the 2D client's readers of the original's art.
   * **A game:** a package (rules, art, texts, AI) plus a client.
2. **Game UI logic lives in engine-free models in `Tzar2.Game.Ui`, tested there; a client draws what they give and
   carries out what they ask.**
   * The first is the command card: `CommandCard` builds the slots and turns a press into commands, an order waiting
     for a target, a building to place, a dialog or a message.
   * It reads its buttons through `ICardSource`. For the original that is the client's `PresentationCatalog`, from
     `MENU.INI`, `ORDERS.INI` and `STRINGS.INI`; a new game supplies its own.
   * Next, as they are touched: the selection view, the event messages, the minimap's model.
3. **New code in the core does not read the original's formats or name its classes;** what the original does by class
   becomes a trait of the rules. Numbers taken from the original's art are compiled into the ruleset at import; the
   simulation never reads art.
4. **`GameFrame` carries game state at full precision.** A field a 3D client needs (the full heading) is added when
   that client comes; nothing in the frame may only make sense for sprites.
5. **Deferred until a new game starts**, when its needs are known:
   * moving `Tzar2.Content/Rules/Legacy*` into a separate assembly behind the compiler's `legacy:` section;
   * a trait for the kingdom's seat instead of the castle's class in the victory check (a rules change, re-pinned
     hashes);
   * civilizations as data in the AI language;
   * the full heading in the frame;
   * the choice of host engine.

## Alternatives considered

* **Split the assemblies now.** Without a second game nobody can say where the seams must run. The cost (moving the
  importer, re-pinning hashes for the castle trait) brings nothing to the remake today.
* **Leave the UI logic in the client.** It is where the play-tests found most defects, and CI cannot test it there.
  A 3D client would have to rewrite it.
* **A separate assembly for UI models.** `Tzar2.Game` is already the engine-free application layer the client
  builds on (TDD §7.2), so a namespace is enough.

## Consequences

* The command card's rules (prices, requirements, queued counts, pages, what a press sends) are tested in
  `Tzar2.Game.Tests` (`CommandCardTests`), and the new card orders are built there.
* `CardAction`, `CardButton`, `CardSlot` and `PriceItem` move from the client to `Tzar2.Game.Ui`.
  `Tzar2.Client.Tests` references `Tzar2.Game`.
* Review checks that new UI logic goes into a model, and that the core gains no reads of original formats or
  original class names.
* Revisit when work on a new game begins: decision 5 then becomes its own plan.
