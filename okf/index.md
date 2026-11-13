---
okf_version: "0.2"
---

# OKF Bundle — eh.DockManager

Knowledge bundle for the `eh.DockManager` repository: what the system is, the constraints
that bind it, and the decisions behind it. Agents **must** read this bundle before starting
work and update it after every change — see [`/AGENTS.md`](../AGENTS.md) and the `okf` skill
at `.opencode/skills/okf/`.

## Project

* [Project overview](project/overview.md) - What eh.DockManager is, its subsystems, repository layout, and where the requirements live.
* [Stack constraints](project/stack-constraints.md) - Audited NuGet allowlist, banned packages, publish/trim/AOT flags, and container baselines.
* [Delivered implementation](project/implementation-status.md) - What was actually built and verified for eh.DockManager, the generated file set, runtime behaviours exercised, and the deviations from PROMPT.md discovered during the build.
* [Host preparation](project/deployment.md) - Host-side steps required before the compose stack starts: bind-mount ownership, the host docker group id, and the empty-password bootstrap fallback.

## Environment

* [Environment](environment/index.md) - Knowledge about the agent working environment: how tool output is sanitized by the PII and secret-key redaction guardrails, how agents must record knowledge safely, the OpenCode TUI quirk that pre-fills the prompt with a terminal reply, and how OpenCode on the SSH host drives a Chrome window on the operator's laptop through a reverse SSH tunnel.

## Meta

* [Update log](log.md) - Append-only, oldest-first history of knowledge changes in this bundle; new entries go at the end of the file.

> Structure note: add concepts under `project/`, or create a new group directory with its
> own `index.md` as the knowledge grows. Keep each entry above in sync with its concept's
> `description` field.
