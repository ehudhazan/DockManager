# Concept template (OKF v0.2)

Copy into `<bundle>/<group>/<concept-id>.md` and delete what does not apply. The filename
is the concept ID (path within the bundle, minus the `.md`).

```markdown
---
type: <Type name>
title: <Display name>
description: <One sentence — reused verbatim by index.md entries.>
resource: <https://... or omit for abstract concepts>
tags: [<tag>, <tag>]
status: stable
generated: { by: opencode/<model-id>, at: <YYYY-MM-DDTHH:MM:SSZ> }
verified: { by: human:<id>, at: <YYYY-MM-DDTHH:MM:SSZ> }   # omit unless independently verified
stale_after: <YYYY-MM-DDTHH:MM:SSZ>                        # omit when there is no expiry
sources:
  - id: <stable-key>
    resource: <../../relative/path.md | /bundle/path.md | https://...>
    title: <Human-readable label>
    author: <actor>
    last_modified: <YYYY-MM-DDTHH:MM:SSZ>
usage_window: { from: <ISO>, to: <ISO> }   # only when usage_count is used
---

# Definition

<What this concept is, in structured markdown. Link related concepts with
[display text](/bundle/path/to/related.md) or [relative](../other.md) links.>

# Schema

| Field | Type | Description |
|-------|------|-------------|
| `...` | ...  | ...         |

# Examples

```<lang>
<concrete, runnable example>
```

# Related

* [Related concept](/group/related.md)

# Notes

<Constraints, caveats, decisions, and the reason they were made. Attribute claims with
footnotes keyed to `sources[].id`.[^<stable-key>]>

[^<stable-key>]: <Human-readable source label>
```

## Checklist before saving

- [ ] `type` is present and descriptive.
- [ ] `description` is one sentence and matches the wording you will use in `index.md`.
- [ ] `generated.by` uses the actor convention and `generated.at` is refreshed (ISO 8601 UTC).
- [ ] No self-verification: you did not add `verified` for your own edit.
- [ ] Every `sources[].resource` points at something real.
- [ ] Internal links are bundle-relative (`/...`) or file-relative (`../...`).
- [ ] A `log.md` entry describes this change.
- [ ] Parent `index.md` lists this concept (same description) if the bundle structure changed.

## Attested Computation variant

Replace the `# Definition` block with the contract fields:

```yaml
---
type: Attested Computation
title: <Value name>
description: <What the value means, and who defines it.>
runtime: <bigquery | postgres | dbt | python | ...>
parameters:
  - { name: <name>, type: <string|integer|...>, required: true }
computation: references/computations/<name>.sql   # or an inline # Computation fence
executor:
  resource: references/skills/<run-instructions>.md
  receipt: [<field>, <field>]
attester:
  resource: references/attesters/<check>.py
generated: { by: opencode/<model-id>, at: <ISO> }
sources:
  - id: <key>
    resource: <...>
---

# Computation

```sql
<the sanctioned computation — agents must not edit this>
```

<Prose linking consumers of this value, with attributed claims.[^<key>]>

[^<key>]: <label>
```
