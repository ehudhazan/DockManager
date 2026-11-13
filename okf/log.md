# Bundle Update Log

Entries are **appended at the end of this file, oldest first**, so concurrent branches and
parallel agents merge cleanly instead of conflicting on the first lines of the file. This
is a deliberate deviation from the OKF spec's newest-first example — see
[`/AGENTS.md`](../AGENTS.md) and `.opencode/skills/okf/SKILL.md` §7.

## 2026-11-13
* **Initialization**: Created the OKF bundle for `eh.DockManager` (`index.md` declaring `okf_version: "0.2"`, `log.md`), and registered the project-local `okf` skill at `.opencode/skills/okf/`.
* **Creation**: Added [Project overview](project/overview.md) and [Stack constraints](project/stack-constraints.md), transcribed from [`/PROMPT.md`](../PROMPT.md) (human-authored build brief). Neither concept carries a `verified` entry yet — the source of truth remains `PROMPT.md` until a human records a verification.
* **Update**: Adopted append-at-the-end log ordering (oldest first) as a project convention in place of the spec's newest-first example, so log entries never conflict across branches. Updated the `okf` skill (workflow, new "Project conventions" section, anti-patterns) and the frontmatter cheatsheet to match, and documented the rule in `/AGENTS.md`.
* **Update**: Restated the append-at-the-end ordering in the `okf/index.md` update-log entry so a reader learns the rule before opening this file, and registered the project-local `okf` skill (`.opencode/skills/okf/`) plus `/AGENTS.md` as the repository's mandatory knowledge workflow.
* **Creation**: Added [Delivered implementation](project/implementation-status.md) capturing the built file set (all files from `PROMPT.md` §9 plus the supporting `Security/QrCodeGenerator.cs`), the runtime behaviours exercised during smoke tests, and the three deviations from `PROMPT.md` found during the build. Linked it from `okf/index.md`.
* **Update**: Refreshed [Project overview](project/overview.md) so its repository-layout table describes the files that actually exist (every server module, the client files, `Dockerfile`/`docker-compose.yml`/`.dockerignore`, and `AGENTS.md`) instead of treating `PROMPT.md` §9 as a target, and pointed it at [Delivered implementation](project/implementation-status.md).
* **Update**: Refreshed [Stack constraints](project/stack-constraints.md): documented the trimming prerequisites (`JsonSerializerIsReflectionEnabledByDefault` and the `TrimmerRootAssembly` set) that the publishing flags require, recorded the base-image deviation (MCR has no `11.0-noble`/`11.0-noble-chiseled` tags, so the `Dockerfile` uses overridable `ARG SDK_IMAGE`/`ARG RUNTIME_IMAGE` defaulting to the Azure Linux distroless images), and noted the environment-overridable listen ports plus `--health-probe`/`--agent`. `generated.at` refreshed on both concepts; no `verified` entry was added (self-review is not verification).
* **Fix**: Recorded that the trimming flags in the table above are not sufficient on their own — without `JsonSerializerIsReflectionEnabledByDefault=true` the app throws `Reflection-based serialization has been disabled` at startup; the code and `eh.DockManager.Server.csproj` are authoritative, and the concept now matches them.
