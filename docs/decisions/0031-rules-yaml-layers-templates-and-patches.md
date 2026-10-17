# 0031. Write rules as layered YAML entries with templates, trait patches and list operations

* **Status:** Accepted
* **Date:** 2026-10-17
* **Deciders:** implementation agent (M2 part C)
* **Related:** TDD §11.1-§11.3, REQ-DATA-02/04/05/06, DEC-06, ADR 0011, ADR 0012, ADR 0030

## Context

§11.2 asks for YAML rules with inheritance (`inherits: [^Infantry]`), field overrides, `-TraitName:` removal, list
operations (`+=`, `-=`) and compile-time conditionals, compiled into an immutable `Ruleset` with dense ids and a hash.
M2 needs enough of this to build `classic` from the original tables and to patch it (remastered, tests, mods). Content
is deterministic (ADR 0012); YamlDotNet is allowed there (ADR 0011) but not yet referenced, and its object mapper needs
reflection or a source generator for AOT.

## Decision

* **Parser.** `Tzar2.Content.Rules.RulesYaml`: the YAML subset of the capture harness (ADR 0030; block and flow
  collections, plain/quoted scalars, literal blocks, comments; no anchors, tags or multi-documents), moved into Content
  with integer-only accessors; the capture harness now uses it. No YamlDotNet for now.
* **Files.** `rules/<name>.yaml` in the VFS. Top-level keys are the sections `ruleset`, `resources`, `gameplay`,
  `terrain`, `legacy`, or entries. `ruleset.base` names the parent ruleset (`remastered` → `classic`); the chain is
  loaded base first. The engine defaults (`content/engine/rules/*.yaml`) are embedded in Tzar2.Content and served by
  `EngineDefaultsSource`, mounted first (REQ-DATA-02).
* **Layers.** Entries are patched in this order: the imported tables (ADR 0032), the ruleset chain, then the
  overrides of `RulesetCompileOptions` (VFS files, then texts) — the override mechanism for mods and tests. A later
  layer patches an existing entry; it does not replace it.
* **Entries.** `key: { inherits: [...], Trait: { field: value }, -Trait: }`. Keys starting with `^` are templates: they
  can be inherited (also by other templates) but are not compiled. An entry may only inherit templates.
  * `inherits:` replaces the parent list, `inherits+=:` appends to it;
  * `Trait: {...}` patches fields: `field: value` sets, `field: ~` unsets, `field+=: [..]` appends to the inherited list,
    `field-=: [..]` removes scalars from it; `-Trait:` removes the trait;
  * field operations are kept in order and applied after inheritance, so `+=` in a patch applies to the inherited
    value, not to whatever an earlier layer wrote.
* **Inheritance.** Parents apply in order (a later parent's fields win), then the entry's own changes. Cycles and
  unknown templates are errors.
* **Sections.** `ruleset`, `gameplay` and `legacy` merge field by field over the layers; `resources` and `terrain` are
  replaced as a whole.
* **Declarations.** A YAML layer may only patch entries that exist; a new non-template entry says `new: true` (and
  `new: true` on an existing entry is an error), so a typo in a patch fails instead of creating a type (PR #14 review
  S7). The imported table layer declares implicitly.
* **Bounded input.** Rules and mods are untrusted: collection nesting is limited to 64 levels, template inheritance to
  64 levels and the ruleset base chain to 16 (typed errors, never a stack overflow); every value that is scaled to
  world units is range-checked (PR #14 review B2, S5).
* **Compilation.** Non-template entries sorted by key (ordinal) get ids 0..n-1, so ids never depend on file order. An
  unknown trait, field, resource, locomotor or entity reference is a `RulesException` with file and line. The compiled
  `Ruleset.ContentHash` covers every compiled value (not comments or formatting).

## Alternatives considered

* **YamlDotNet with its static generator:** a full YAML 1.2 parser, but the generator and model classes are a large
  addition while the rules need a small subset; the RepresentationModel would work without reflection, so switching
  later only replaces the parser.
* **Eager list operations (apply `+=` when the layer is read):** simpler, but a mod's `+=` would then extend only the
  table layer's list and silently drop what templates contribute.
* **Ids in file order:** a mod adding an entry early would renumber everything.

## Consequences

* One YAML reader: `RulesYaml` and its node types are public in Content, and the capture harness (Tools.Cli) uses them
  through a small facade that adds its floating-point accessor.
* Not implemented yet: `@if:` conditionals, a JSON Schema generated from the trait catalogue (REQ-DATA-04), packages'
  own `rules/` folders discovered by the mod manager (they can be passed as override files today).
