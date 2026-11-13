# OKF v0.2 — Frontmatter Cheatsheet

Condensed reference. Normative source:
<https://github.com/GoogleCloudPlatform/knowledge-catalog/blob/main/okf/SPEC.md>

## Concept frontmatter

```yaml
---
type: <Type name>            # REQUIRED, non-empty, free-form, not centrally registered
title: <display name>        # recommended; consumers may fall back to the filename
description: <one sentence>  # recommended; reused by index.md and search snippets
resource: <canonical URI>    # recommended when the concept describes a real asset
tags: [<tag>, <tag>]         # recommended; list of short strings
# trust / lifecycle / provenance families below
---
```

## Trust

| Field | Shape | Notes |
|-------|-------|-------|
| `generated` | `{ by: <actor>, at: <ISO 8601> }` | `by` required within; `at` = last meaningful content change |
| `verified` | `{ by, at }` or a list of `{ by, at }` | Independent confirmations only. A bare mapping is a one-element list |

Trust tiers derived from `verified`:

| `verified` | Tier |
|------------|------|
| absent | unverified |
| non-`human:` actors only | machine-confirmed |
| includes a `human:<id>` actor | human-reviewed |

`generated.at` and `verified.at` are independent: content can change without re-confirmation,
and facts can be re-confirmed without regeneration.

## Lifecycle

| Field | Values | Default |
|-------|--------|---------|
| `status` | `draft` \| `stable` \| `deprecated` | `stable` when absent |
| `stale_after` | ISO 8601 instant | none; stale when `now >= stale_after` |

Use an absolute instant (not a relative TTL) so staleness is a plain comparison.

## Provenance

```yaml
sources:
  - id: prompt-md                  # optional; REQUIRED when the body cites the source
    resource: ../../PROMPT.md      # REQUIRED per entry: URL, bundle-relative /path, relative path, or scope descriptor
    title: PROMPT.md — build brief
    author: human:ehud             # credibility signal (actor convention)
    usage_count: 5000              # credibility signal; coarse liveness/trend signal
    last_modified: 2026-11-13T00:00:00Z   # credibility signal; distinct from generated.at
usage_window: { from: 2026-11-01T00:00:00Z, to: 2026-11-30T00:00:00Z }  # frames usage_count
```

- Credibility is **inferred** from signals; never store a score.
- Per-claim attribution: markdown footnote labelled with the `sources[].id`:

  ```markdown
  The queue is drained before shutdown.[^prompt-md]

  [^prompt-md]: PROMPT.md — build brief
  ```

  Labels are keyed, not positional, so reordering `sources` never misattributes.

## Actor convention

| Actor | Format | Example |
|-------|--------|---------|
| Agent / tool | `<producer>/<version>` | `opencode/deepseek-v4.1-flash` |
| Person | `human:<id>` | `human:ehud` |
| Automation | `process:<id>` | `process:nightly-sync` |

Trust classification keys off the `human:` prefix — use it for hand-authored or
human-confirmed content.

## Timestamps

ISO 8601 datetime with an explicit UTC offset, e.g. `2026-11-13T08:44:48Z`. Applies to
`generated.at`, `verified[].at`, `sources[].last_modified`, `usage_window.from/to`,
`stale_after`, and `log.md` headings (`YYYY-MM-DD`).

## Paths and links

| Form | Meaning |
|------|---------|
| `/tables/orders.md` | bundle-relative (relative to the bundle **root**) — recommended for links between concepts |
| `./other.md`, `../x.md` | relative to the current file |
| `https://...` | absolute URL |
| `../../PROMPT.md` | repo file outside the bundle — use a relative path, **not** a leading `/` |

Same rules apply to `resource`, `sources[].resource`, `computation`, `executor.resource`,
and `attester.resource`. A `sources[].resource` may instead be a scope descriptor (e.g.
`all containers on host X`), in which case it is not a path.

`references/` is a naming convention for mirroring external material, run instructions, or
code inside the bundle.

## Body headings (conventional)

| Heading | Purpose |
|---------|---------|
| `# Schema` | Structured description of an asset's columns/fields |
| `# Examples` | Concrete usage examples, usually fenced code |
| `# Computation` | The sanctioned computation of an Attested Computation |

No body section is required. Prefer structured markdown (headings, lists, tables, fences).

## Attested Computation

```yaml
type: Attested Computation
runtime: bigquery                       # REQUIRED for this type; defines parameter binding
parameters:
  - { name: year, type: integer, required: true }
computation: references/computations/revenue.sql   # optional; else the body # Computation fence
executor:
  resource: references/skills/run-on-bq.md
  receipt: [job_id, executed_sql, result]
attester:
  resource: references/attesters/sql-equality.py   # deterministic (no LLM), runs consumer-side
```

- `verified` confirms the **definition**; attestation confirms a single **run**. Both exist.
- The agent supplies parameter **values** only; it must not author or edit the computation.
- `executor.receipt` is a runtime artifact — not stored in the bundle.

## Reserve filenames (never concepts)

| Filename | Purpose |
|----------|---------|
| `index.md` | Directory listing. No frontmatter, except an optional `okf_version` in the bundle root. |
| `log.md` | Update history. Flat list of `## YYYY-MM-DD` entries, **oldest first** — new entries are appended at the end (project convention; the spec example shows newest first). |

Index body shape:

```markdown
# Section Heading

* [Title](relative-url) - short description
* [Subdirectory](subdir/) - short description
```

Log body shape — **append at the end of the file**; the last block is the most recent
change (oldest first, a deliberate deviation from the spec that keeps concurrent branches
from conflicting):

```markdown
# Directory Update Log

## 2026-11-10
* **Creation**: Added the container autoscaler playbook.

## 2026-11-13
* **Update**: Reworked the TLS concept after the SAN change.
```

## Conformance checklist

- [ ] Every non-reserved `.md` file has parseable YAML frontmatter.
- [ ] Every frontmatter block has a non-empty `type`.
- [ ] `index.md` / `log.md` follow the shapes above when present.
- [ ] Optional families, when used, follow §5–§10 of the spec.
- [ ] Unknown keys survive round-trips; unknown `type` values are tolerated.
- [ ] Missing optional fields, broken links, and a missing `index.md` never invalidate a bundle.

## Bundle root index template

```markdown
---
okf_version: "0.2"
---

# <Bundle name>

<One or two sentences on what this bundle covers.>

## <Section>

* [Concept](/group/concept.md) - short description
```

## Versioning

- `okf_version: "0.2"` may be declared only in the bundle-root `index.md`.
- Minor version bumps add optional fields; major bumps may rename or retire fields.
- v0.1 fallbacks a v0.2 consumer may apply: legacy `timestamp` → `generated.at`, legacy body
  `# Citations` list → `sources`.
