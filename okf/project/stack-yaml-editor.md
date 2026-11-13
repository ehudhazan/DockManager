---
type: Reference
title: Stack YAML editor
description: How the Stacks view YAML editor validates a compose document, offers keyboard-driven key and value completions, and indents lists and mappings intelligently, and the schema validator endpoint behind it.
tags: [stacks, compose, yaml, editor, validation, ux, api]
status: stable
generated: { by: opencode/deepseek-v4.1-flash, at: 2026-11-13T20:20:00Z }
sources:
  - id: validator
    resource: ../../src/eh.DockManager.Server/Stacks/ComposeSchemaValidator.cs
    title: ComposeSchemaValidator.cs — schema, enum and port validation with source locations
  - id: engine
    resource: ../../src/eh.DockManager.Client/src/yaml.ts
    title: yaml.ts — schema tree, completion engine, smart Enter and local enum lint
  - id: editor
    resource: ../../src/eh.DockManager.Client/src/ui.tsx
    title: ui.tsx — YamlEditor component
  - id: program-cs
    resource: ../../src/eh.DockManager.Server/Program.cs
    title: Program.cs — POST /api/stacks/validate
---

# Goal

Manual stack editing must be as safe and quick as an IDE: a wrong enum or a broken port mapping should be
flagged before saving, the known-good keys and values should be one keystroke away, and Enter should keep
the document's structure so nobody has to count spaces.[^editor]

# Validation — `POST /api/stacks/validate`

Admin-only. Body `{ "yaml": "…" }`; the response is
`{ valid, name, serviceCount, issues: [{ line, column, message, severity, path }] }`.[^validator]

| Check | Severity | Notes |
|-------|----------|-------|
| YAML parse error | error | the line/column come from YamlDotNet's `YamlException` |
| missing `services` / not a mapping / empty | error | |
| a service without `image` or `build` | warning | may be built by a `build:` sibling |
| unknown top-level key | warning | `x-*` extensions are ignored |
| unknown service / network / volume key | warning | warns without blocking forward-compatible fields |
| invalid `restart`, `network_mode`, `pull_policy` | error | e.g. `unless-stopped`, `on-failure:3` |
| invalid `depends_on.*.condition`, `logging.driver`, `networks.*.driver`, `deploy.*.condition/mode` | error | |
| invalid `volumes.*.type` / `consistency` | error | long syntax only |
| invalid `healthcheck.test` verb | error | `CMD`, `CMD-SHELL`, `NONE` |
| invalid port mapping | error | `[host:]container[/tcp\|udp\|sctp]` |
| obsolete top-level `version` | warning | ignored by the Compose Specification |

Because the YAML object model loses source positions, the validator **re-attaches a line** for any finding
that names a property or a literal: it searches the source for the property line, and for a literal it also
scans the list items under that property, so a bad `ports` entry points at the `- "…"` line, not the
`ports:` header. Findings without a path (parse errors) keep their own position.

`valid` is `false` only when at least one **error** exists; warnings never block a save.

# Completions — schema-driven, keyboard-first

`yaml.ts` holds a `SchemaNode` tree (`COMPOSE_SCHEMA`) covering the top level (`name`, `version`,
`services`, `networks`, `volumes`, `configs`, `secrets`) and the service properties DockManager supports.
Each node records its `kind` (`map`/`sequence`/`scalar`), a description, a set of **known valid values**
for enums, and an example for freeform scalars.[^engine]

* **Path analysis** — `pathAt` walks the lines above the caret and returns the enclosing key chain, so the
  editor knows whether it is inside `services.web.ports` or `volumes.data.driver`. Sequence items
  contribute their parent key, so list nesting does not confuse it.
* **Key completions** — when the caret is at a key position, the child keys of the resolved node are
  offered (filtered by the typed prefix), each with a description and its kind.
* **Value completions** — for a scalar with `values` (e.g. `restart`) the valid set is offered; for a
  sequence the list-item template is offered; for a freeform scalar an example is offered.
* **Keyboard** — `Ctrl/⌘+Space` opens the list (or it opens automatically once a prefix is typed);
  `↑`/`↓` move, `Enter`/`Tab` apply, `Esc` dismisses. Mouse users can click an entry. The panel shows the
  resolved path (e.g. `services → web → ports`) so context is never ambiguous.

# Smart Enter and Tab

`enterInsertion` returns the text to insert so structure is preserved:[^engine]

| Situation | Result |
|-----------|--------|
| `key:` with an empty value that names a sequence (`ports`, `volumes`, `environment`, `command`, …) | new line at `indent + 2` starting with `- ` |
| `key:` with an empty value that names a mapping (`healthcheck`, `logging`, …) | new line at `indent + 2` |
| `key: value` | new line at the same indent |
| `- value` (scalar list item) | new line with another `- ` at the same indent |
| `- key:` (map inside a list, e.g. long-syntax `volumes`) | new line at `indent + 2`, continuing properties |
| blank line | matches the previous non-empty line's indent |

`Tab` inserts two spaces (compose's convention); with a multi-line selection it indents every selected
line, and `Shift+Tab` outdents.

# Local lint

`lintEnums` runs on every keystroke and flags the same closed-set enums the server checks
(`restart`, `network_mode`, `pull_policy`, `condition`, `driver`, volume `type`/`consistency`,
`healthcheck.test`) with the **exact** offending line, so feedback is instant and the server round-trip
only adds the schema-level findings. The editor merges and de-duplicates the two sets.

# UI

The Stacks view renders `YamlEditor` in place of the old `<textarea>`: the source on the left, a status
line (`Ctrl/⌘ Space` hint, indent hint, error/warning count or "schema ok"), and a clickable issue list
where selecting a finding focuses the editor and selects that line. The toolbar keeps **Save** (blocked
while errors exist, naming the first one) and adds **Validate**; the live diff view is unchanged.[^editor]

# Related

* [UI & UX conventions](/project/ui-ux.md) — where the editor sits and the surrounding controls.
* [Delivered implementation](/project/implementation-status.md) — the pass that added it.

[^validator]: ComposeSchemaValidator.cs — schema, enum and port validation with source locations
[^engine]: yaml.ts — schema tree, completion engine, smart Enter and local enum lint
[^editor]: ui.tsx — YamlEditor component
[^program-cs]: Program.cs — POST /api/stacks/validate
