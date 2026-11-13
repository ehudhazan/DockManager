---
name: OKF — Open Knowledge Format
description: Consult, author, and maintain OKF knowledge bundles — Google's Open Knowledge Format v0.2, a directory of markdown concepts with YAML frontmatter (type, sources, generated, verified, status, stale_after). Load this skill before starting any work in a repository that keeps an OKF bundle (read the bundle and its index first) and after every edit (append to log.md, update affected concepts, keep index.md in sync).
---

# OKF — Open Knowledge Format (Google, v0.2)

OKF is an open, human- and agent-friendly format for representing **knowledge**: the
metadata, context, and curated insight that surrounds data and systems. A **bundle** is
just a directory tree of markdown files with YAML frontmatter — no SDK, no registry, no
tooling required. If you can `cat` a file, you can read OKF.

Authoritative spec: <https://github.com/GoogleCloudPlatform/knowledge-catalog/blob/main/okf/SPEC.md>
(see also <https://okf.md/>).

This skill is installed at `<repo>/.opencode/skills/okf/`. Supporting files:

- `references/frontmatter-cheatsheet.md` — every field, allowed values, conformance rules.
- `references/concept-template.md` — copy-paste skeleton for a new concept.

---

## 🚨 MANDATORY in this repository

`AGENTS.md` at the repository root makes the OKF workflow binding for **every** agent and
**every** task. Do not skip it, and do not treat it as optional documentation.

1. **Consult OKF before any work.** Read the bundle index and every concept that touches
   the area you are about to change. Do this *before* planning or editing code.
2. **Update OKF after every change.** Every edit — code, config, infra, docs, or a decision
   — gets recorded in `log.md`, and every concept whose content is now wrong or incomplete
   gets updated in the same task.

A task is **not complete** until both halves are done. If the bundle is missing or
unreadable, repair it first (see *Bootstrapping a bundle* below), then proceed.

---

## 1. Consult the bundle (before any work)

1. **Read the index.** Open the bundle root's `index.md`. It is the progressive-disclosure
   map of what exists; it carries no frontmatter except an optional `okf_version`.
2. **Read the log.** Open `log.md` — recent entries tell you what changed lately and what
   was decided, which is often the real reason a file looks the way it does. Entries are
   appended at the **end** of the file, so read the tail for the most recent changes.
3. **Follow the links.** Open every concept relevant to your task, starting from the index
   entries matching your area. Concepts link with standard markdown links, so following
   them is how you traverse the knowledge graph.
4. **Read from the bundle root, not from a subdirectory**, when resolving links: paths
   beginning with `/` are **bundle-relative** (relative to the bundle root), e.g.
   `/project/overview.md`.
5. **Check freshness and trust before relying on a fact:**
   - `status: draft` ⇒ not reviewed, may be incomplete or wrong.
   - `status: deprecated` ⇒ kept for history only; find the replacement.
   - `now >= stale_after` ⇒ the content is stale; verify against its sources
     (or the live system) before acting on it.
   - No `verified` key ⇒ **unverified**; machine-only `verified` ⇒ **machine-confirmed**;
     a `human:<id>` entry ⇒ **human-reviewed**.
6. **Prefer the bundle over guessing.** If the bundle contradicts the code, the code wins —
   then fix the concept as part of your update.

## 2. Update the bundle (after every change)

1. **Append to `log.md`** (root, and any subdirectory-scoped `log.md` you touched).
   **Append at the end of the file, oldest first** — never insert above existing entries —
   grouped under an ISO 8601 `## YYYY-MM-DD` heading:

   ```markdown
   ## 2026-11-13
   * **Update**: Reworked X in [Y](/path/to/y.md) after changing Z.
   * **Creation**: Added [New Thing](/path/to/new-thing.md).
   ```

2. **Update every affected concept.** If your change makes a sentence false, incomplete,
   or misleading, edit that concept now. Never leave a concept describing the old world.
3. **Refresh the trust frontmatter of anything you wrote:**

   ```yaml
   generated: { by: opencode/<model-id>, at: 2026-11-13T08:44:48Z }
   ```

   - `generated.by` uses the actor convention: `opencode/<model-id>` for an agent
     (e.g. `opencode/deepseek-v4.1-flash`), `human:<id>` for a person,
     `process:<id>` for automation.
   - `generated.at` is an ISO 8601 datetime with an explicit UTC offset (`Z`). Refresh it on
     every meaningful content change.
   - Do **not** add or alter a `verified` entry for your own edit — verification means an
     independent check (a human sign-off or a deterministic process), not self-review.
     If your edit invalidates a previous verification, say so in `log.md`.
4. **Create a concept when the knowledge is new.** One concept = one unit of knowledge.
   Give it a `type`, put it where a reader would look for it, and link it from the parent
   `index.md` and from any concept that relates to it.
5. **Keep `index.md` in sync** when concepts are added, removed, renamed, or moved, and
   keep each entry's description identical to the target concept's `description`.
6. **Splitting vs. co-locating:** if two facts in one document verify, go stale, or change
   on different schedules, they are two concepts, not one.

## 3. Concept documents

A concept is a UTF-8 markdown file: a **YAML frontmatter block** (delimited by `---`) plus a
markdown **body**. The filename *is* the concept ID — the path within the bundle minus
`.md`.

```markdown
---
type: API Endpoint
title: Regenerate TLS certificate
description: Re-issues the server certificate from the internal PKI root with new SANs.
resource: https://localhost/api/system/ssl/regenerate
tags: [api, pki, tls]
status: stable
generated: { by: opencode/deepseek-v4.1-flash, at: 2026-11-13T08:44:48Z }
verified: { by: human:ehud, at: 2026-11-13T09:00:00Z }
stale_after: 2027-05-13T00:00:00Z
sources:
  - id: prompt-md
    resource: ../../PROMPT.md
    title: PROMPT.md — build brief and requirements
    author: human:ehud
---

# Schema

| Field | Type   | Notes                        |
|-------|--------|------------------------------|
| `sans`| string | Comma-separated hostnames/IPs.|

# Examples

Requests a new leaf certificate and hot-swaps it without dropping connections.[^prompt-md]

[^prompt-md]: PROMPT.md — build brief and requirements
```

- **`type` is the only required field.** A concept with just `type` is conformant.
- `title`, `description`, `resource`, `tags` are recommended. `description` is a single
  sentence — index files and search snippets reuse it.
- Body: favor structure (headings, lists, tables, fenced code) over prose. Conventional
  headings: `# Schema`, `# Examples`, `# Computation`.
- **Per-claim attribution** uses a markdown footnote whose label is a `sources[].id`. That
  is the join key into the frontmatter list — never a positional index.
- **Links between concepts**: bundle-relative (`/tables/orders.md`, recommended) or
  relative (`../computations/revenue.md`). The link kind (parent, depends-on, joins-with)
  lives in the prose, not in the link. Broken links are tolerated — they may be
  not-yet-written knowledge.
- **Paths outside the bundle** (repo files, wikis): use a path relative to the concept file
  (`../../PROMPT.md`) or an absolute URL. A leading `/` does **not** mean filesystem root —
  it means bundle root.
- **Do not touch** reserved filenames (`index.md`, `log.md`) as concepts.

## 4. Bundle structure

```text
okf/
├── index.md                  # Directory listing (reserved)
├── log.md                    # Update history (reserved)
├── index.md per subdirectory # Reserved
└── <group>/
    ├── index.md
    ├── <concept>.md          # Concept documents
    └── references/           # Conventionally mirrors external material / code
```

Distribute the bundle as a git repository (recommended) or as a subdirectory of a larger
repo. The directory layout is a producer choice — organize it for the reader, not for a
schema.

### Bootstrapping a bundle

If no bundle exists yet: create the root directory, an `index.md` carrying
`okf_version: "0.2"` in its frontmatter, an empty `log.md` with a `# Bundle Update Log`
heading and a first-dated entry, and one concept capturing what the project *is* and where
its requirements live. Announce the creation in `log.md`.

## 5. Trust, provenance, lifecycle (in short)

- **Provenance** — `sources: [{id, resource, title?, author?, usage_count?, last_modified?}]`
  plus an optional `usage_window: {from, to}`. `resource` is required per entry; record
  objective signals, never a credibility score.
- **Trust** — `generated: {by, at}` is how the content was produced; `verified:` (a single
  mapping or a list of `{by, at}`) is who confirmed it. Trust tier is derived from
  `verified`, not stored.
- **Lifecycle** — `status: draft | stable | deprecated` (absent ⇒ `stable`) and
  `stale_after: <ISO instant>` (stale when `now >= stale_after`).
- **Attested Computation** — a concept of `type: Attested Computation` carries a sanctioned
  way to compute a value (`runtime`, `parameters`, `computation`, `executor`, `attester`)
  so a consumer can confirm the value was produced the sanctioned way. Agents may supply
  *parameter values only* — never author or edit the computation.

Full field reference and conformance rules: `references/frontmatter-cheatsheet.md`.

## 6. Conformance (must-haves)

A bundle is conformant when:

1. Every non-reserved `.md` file has a parseable YAML frontmatter block.
2. Every frontmatter block has a non-empty `type`.
3. Every reserved filename, when present, follows the index/log structure.

Consumers must **not** reject a bundle for missing optional fields, unknown `type` values,
unknown extra keys, broken links, or a missing `index.md`. Producers should preserve
unknown keys when round-tripping a document.

## 7. Project conventions (deviations from the OKF spec)

### `log.md` is append-only, oldest first

The spec's §9 example lists log entries newest-first. This repository does the opposite **on
purpose**: new entries are appended to the **end** of the file, in chronological order.

Rationale: a prepend-at-top log rewrites the first lines of the file on every single change,
so any two branches — or two agents working in parallel — conflict on those lines and every
merge needs manual resolution. Appending only touches the tail, so independent log entries
merge cleanly.

Concretely:

1. Read the **tail** of `log.md`; the last entry is the most recent change.
2. If the last date heading is today's `## YYYY-MM-DD`, append a bullet under it; otherwise
   append a new date heading after the existing entries.
3. Never insert above existing entries, never reorder or reformat them, and never add a
   "recent changes" block at the top of the file.
4. Keep a multi-line entry contiguous — append it as one block, never split across bullets
   interleaved with other entries.

Everything else follows OKF v0.2 as written.

## 8. Anti-patterns

- ❌ Starting work without reading the bundle "because it's probably not relevant".
- ❌ Prepending to `log.md` or keeping a "newest first" log — it conflicts on every
  concurrent change. Append at the end instead (§7).
- ❌ Finishing a change without a `log.md` entry — the next agent loses the *why*.
- ❌ Leaving a concept that describes the pre-change state (stale documentation is worse
  than none).
- ❌ Self-verifying: adding `verified: { by: opencode/... }` to your own edit.
- ❌ Dumping everything into one mega-concept instead of one concept per unit of knowledge.
- ❌ Inventing provenance: a `sources[].resource` must point at something real.
- ❌ Relative timestamps ("recently", "last week") in `generated.at`, `verified.at`,
  `stale_after` — always ISO 8601 with an explicit offset.
- ❌ Using a leading `/` to mean the filesystem when linking a file outside the bundle.
