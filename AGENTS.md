# AGENTS.md — eh.DockManager

> **Binding for every agent, on every task, in this repository.**
> `PROMPT.md` holds the functional requirements; this file holds the working rules. Where the
> two overlap, the stricter rule wins. The knowledge bundle under `okf/` holds the digested
> knowledge this repository has accumulated.

The `okf` skill (`.opencode/skills/okf/SKILL.md`) defines the knowledge format and the
full workflow. This file defines the **obligation**:

## 0. The two non-negotiable rules

1. **Consult OKF before any work.** Read the relevant knowledge before planning, editing,
   or running anything that changes state.
2. **Update OKF after every change.** Record what changed and why, and correct every concept
   your change made wrong, in the same task.

Both, every task. A change whose code is finished but whose OKF update is missing is **not
complete** — do not report it as done, and do not move on to the next task.

---

## 1. Before any work — consult the bundle

Bundle root: `okf/` (relative to this repository's root).

1. **Load the skill**: use the `skill` tool with id `okf`.
2. **Read the index**: `okf/index.md` — the map of what knowledge exists. It has no
   frontmatter except `okf_version: "0.2"`.
3. **Read the log**: `okf/log.md` — the history and the *why* behind the current state.
   **Read the tail of the file**: entries are appended at the end, so the last entry is the
   most recent change.
4. **Follow the links**: open every concept that touches the area you are about to change,
   starting from the matching index entries. Concept links are the navigation.
5. **Read the minimum set every time**:
   * [`okf/project/overview.md`](okf/project/overview.md) — what this system is and how it is
     laid out,
   * [`okf/project/stack-constraints.md`](okf/project/stack-constraints.md) — dependency,
     build, container, port, and storage rules.
6. **Read `PROMPT.md`** (repository root). It is the normative requirements document; the
   bundle summarizes and points at it but never replaces it.
7. **Check freshness and trust before relying on a fact**:
   * `status: draft` ⇒ possibly incomplete or wrong; verify the source.
   * `status: deprecated` ⇒ history only; find the replacement.
   * `now >= stale_after` ⇒ stale; re-verify against its `sources` (or the live system).
   * No `verified` key ⇒ **unverified**; only `process:`/agent verifiers ⇒
     **machine-confirmed**; a `human:<id>` verifier ⇒ **human-reviewed**.
8. Only then plan, edit, or run commands.

If the bundle contradicts the code, **the code wins** — and correcting the concept is part of
your OKF update (§2).

## 2. After every change — update the bundle

Applies to: source code, project files (`csproj`, `package.json`, `Dockerfile`,
`docker-compose.yml`, `global.json`, `opencode.json`), API endpoints, database schema,
configuration defaults, infrastructure, conventions, design decisions, and bug root causes.
If a future agent would benefit from knowing it, it belongs in the bundle.

1. **Append to `okf/log.md`** — one entry per change-set, at the **end** of the file (§3).
2. **Update every affected concept** in the same task. If your change makes a sentence
   false, incomplete, or misleading, fix it now — stale knowledge is worse than none.
3. **Create a concept for new knowledge.** One concept per unit of knowledge; give it a
   `type`, put it where a reader would look, and link it from the appropriate `index.md`
   and from related concepts.
4. **Refresh `generated`** on every concept you edit:

   ```yaml
   generated: { by: opencode/<model-id>, at: 2026-11-13T08:44:48Z }
   ```

   The timestamp is ISO 8601 UTC with a `Z` offset. Never write a relative time.
5. **Never self-verify.** Do not add or alter `verified` for work you produced. Verification
   means an independent check — a human sign-off (`human:<id>`) or a deterministic process
   (`process:<id>`). If your change invalidates an existing `verified` entry, a `stale_after`,
   or a previously stated fact, say so in the log entry and in the affected concept.
6. **Keep the indexes in sync** whenever concepts are added, renamed, moved, or removed; an
   index entry's description must match the target concept's `description` verbatim.
7. **Then report.** The OKF update is part of the change, not a follow-up.

## 3. `log.md` rule — append only, oldest first

`okf/log.md` (and any subdirectory `log.md`) is **append-only**:

* **Append new entries at the end of the file.** Never insert above existing entries and
  never add a "recent changes" block at the top.
* Rationale: the OKF spec's newest-first example rewrites the first lines of the file on
  every change, so any two branches — or two agents logging in parallel — conflict and every
  merge needs hand resolution. Appending only touches the tail and merges cleanly. This is a
  deliberate, repository-wide deviation from the spec example.
* If the **last** date heading is today's `## YYYY-MM-DD`, append the bullet under it;
  otherwise append a new date heading after all existing entries.
* Never reorder, reformat, re-date, or reword existing entries.
* One bullet per change-set, prefixed with a bold verb by convention:
  `**Creation**`, `**Update**`, `**Deprecation**`, `**Fix**`, `**Initialization**`.
* Keep a multi-line entry contiguous — append it as a single block, never interleaved with
  other entries.
* Link concepts and repo files so a reader can jump straight to the detail.

```markdown
## 2026-11-14
* **Fix**: Corrected the [TLS concept](/project/tls.md) after the SAN regeneration change;
  the certificate is now hot-swapped, not re-issued on restart. See `Program.cs`.
```

## 4. Concept rules

* Filename = concept ID: the path under the bundle minus `.md`. `index.md` and `log.md` are
  **reserved** and are never concepts.
* Frontmatter is required and must carry a non-empty `type`. Minimal shape:

  ```yaml
  ---
  type: <Type name>
  title: <Display name>
  description: <One sentence — copy this wording into the index entry.>
  tags: [<tag>, <tag>]
  status: stable
  generated: { by: opencode/<model-id>, at: 2026-11-13T08:44:48Z }
  sources:
    - id: <stable-key>
      resource: ../../PROMPT.md     # URL, or a path relative to the concept file
  ---
  ```

* **Links**: bundle-relative (`/project/overview.md`, recommended) for concepts; relative
  paths (`../../PROMPT.md`) for files outside the bundle. A leading `/` means **bundle
  root**, never the filesystem root.
* **Attribution**: record where a fact came from in `sources`; per-claim attribution uses a
  footnote whose label is the `sources[].id`.
* **Split concepts** when two facts change, verify, or go stale on different schedules.
* Prefer structured markdown (headings, tables, fenced code) over prose; conventional
  headings are `# Schema`, `# Examples`, `# Computation`.
* `okf/` is knowledge, not code: do not put implementation files there.

## 5. Conventions

| Item | Value |
|------|-------|
| Agent actor | `opencode/<model-id>` (e.g. `opencode/deepseek-v4.1-flash`) |
| Human actor | `human:<id>` |
| Automation actor | `process:<id>` |
| Timestamps | ISO 8601 with explicit UTC offset — `2026-11-13T08:44:48Z` |
| Log date headings | `## YYYY-MM-DD` |
| Bundle version | `okf_version: "0.2"` in the bundle-root `index.md` only |
| Change history | append to `okf/log.md` (oldest first), never prepend |
| Requirement source | `PROMPT.md` (normative; do not edit it unless asked) |

## 6. Definition of done

- [ ] OKF consulted: index, log tail, and every relevant concept read **before** the change.
- [ ] Change implemented and verified per project standards.
- [ ] `okf/log.md` entry appended at the end of the file.
- [ ] Every affected concept updated; `generated.at` refreshed.
- [ ] New knowledge captured as a concept and linked; indexes updated and consistent.
- [ ] No contradiction left between the bundle, the code, and `PROMPT.md`.

## 7. When something is off

* **No `okf/` bundle** ⇒ create it first (root `index.md` with `okf_version: "0.2"`, a
  `log.md`, and one concept for the project), log the initialization, then proceed.
* **Bundle contradicts the code** ⇒ the code wins; fix the concept in the same task.
* **Change feels too small to document** ⇒ it still gets a log entry. No exceptions; the log
  is what makes the next agent's work cheap.
* **Knowledge would go stale soon** ⇒ set `stale_after` instead of guessing.
* **Unsure where knowledge belongs** ⇒ closest existing group directory, and link it from
  that group's `index.md`.
* **Tempted to skip the update to save time** ⇒ the workflow is mandatory. Report the task as
  incomplete rather than skipping it.

## 8. Project quick reference

| What | Where |
|------|-------|
| Functional requirements (normative) | [`PROMPT.md`](PROMPT.md) — including the fixed file generation order in §9 |
| Knowledge bundle | [`okf/index.md`](okf/index.md), history in [`okf/log.md`](okf/log.md) |
| System overview | [`okf/project/overview.md`](okf/project/overview.md) |
| Dependency / build / port / storage rules | [`okf/project/stack-constraints.md`](okf/project/stack-constraints.md) |
| Knowledge workflow skill | [`.opencode/skills/okf/SKILL.md`](.opencode/skills/okf/SKILL.md) |
| Server sources | `src/eh.DockManager.Server/` |
| Client sources | `src/eh.DockManager.Client/` |
| Container build | `Dockerfile`, `docker-compose.yml`, `.env.example`, `build.sh` (stamps the date-based version) |

Verification commands:

```bash
dotnet build src/eh.DockManager.Server/eh.DockManager.Server.csproj
npm --prefix src/eh.DockManager.Client run typecheck
npm --prefix src/eh.DockManager.Client run build
./build.sh   # docker compose build with BUILD_NUMBER = git commit count (or: docker compose build)
```
