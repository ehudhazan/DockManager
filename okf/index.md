---
okf_version: "0.2"
---

# OKF Bundle — eh.DockManager

Knowledge bundle for the `eh.DockManager` repository: what the system is, the constraints
that bind it, and the decisions behind it. Agents **must** read this bundle before starting
work and update it after every change — see [`/AGENTS.md`](../AGENTS.md) and the `okf` skill
at `.opencode/skills/okf/`.

## Project

* [Project overview](project/overview.md) - Home-server and enterprise container/cluster control plane, port 443 reverse proxy and app portal, auto-updater, and MCP server built on .NET 11 and React 19.
* [Stack constraints](project/stack-constraints.md) - Binding dependency, build, container, port, and storage constraints for eh.DockManager, including the audited NuGet allowlist and banned packages.
* [Delivered implementation](project/implementation-status.md) - What was actually built and verified for eh.DockManager, the generated file set, runtime behaviours exercised, and the deviations from PROMPT.md discovered during the build.
* [UI & UX conventions](project/ui-ux.md) - The design system, navigation model, and mobile behaviour of the eh.DockManager React client, including the icon set, info tooltips, clickable surfaces, and role-based views.
* [Server identity & hostname settings](project/server-identity.md) - How the portal hostname is configured and how it drives the TLS certificate and portal app URLs, including the settings and user-update API endpoints.
* [Let's Encrypt (ACME v2)](project/lets-encrypt.md) - How DockManager obtains and renews a publicly trusted TLS certificate from Let's Encrypt using a hand-built ACME v2 client with an http-01 challenge, and what the host and domain must provide.
* [Portal visibility & ordering](project/portal.md) - How a published app is hidden from, revealed on, deleted from, or added in bulk to the portal catalogue, and how the manual SortOrder drives the order of the tiles within each category.
* [Dashboard metrics & aggregation](project/dashboard.md) - What the Dashboard shows, where each metric comes from, and how the server keeps the per-container stats fan-out cheap for the host and the browser.
* [MCP server permissions & audit](project/mcp.md) - How the built-in MCP server authenticates scoped bearer keys, the resource:action[:target] scope grammar it enforces, the tool surface it exposes, and how every MCP action is attributed in the audit log.
* [Operation guards across UI, MCP and server](project/operation-guards.md) - The rule that an operation's allow/block decision is defined once server-side and applied on every surface — client UI, MCP and REST — in the same change.
* [Network metrics & traffic history](project/network-metrics.md) - How DockManager samples container and host network traffic into a 24-hour ring buffer, the 1h/24h totals, drops, errors and TCP connection-reset counters it exposes, and the endpoints that serve them.
* [Client/server version negotiation & asset freshness](project/client-server-versioning.md) - How the client detects a disconnected server, notices a redeploy through the health endpoint's appVersion/bootId, reloads the shell, refreshes the service worker, and why sw.js and index.html are never cached.
* [Stack YAML editor](project/stack-yaml-editor.md) - How the Stacks view YAML editor validates a compose document, offers keyboard-driven key and value completions, and indents lists and mappings intelligently, and the schema validator endpoint behind it.
* [Host preparation](project/deployment.md) - Host-side steps required before the compose stack starts: bind-mount ownership, the host docker group id, and the empty-password bootstrap fallback.
* [Remote Docker hosts](project/remote-docker-hosts.md) - How DockManager connects to a remote Docker engine — the Unix-socket, mTLS-daemon and single-binary-agent host kinds, the endpoint scheme rules, the certificate validation chain on each side, and the plain-TCP fallback when no certificate is usable.

## Environment

* [Environment](environment/index.md) - Knowledge about the agent working environment: how tool output is sanitized by the PII and secret-key redaction guardrails, how agents must record knowledge safely, the OpenCode TUI quirk that pre-fills the prompt with a terminal reply, how OpenCode on the SSH host drives a Chrome window on the operator's laptop through a reverse SSH tunnel, and the repository's pinned default model in opencode.json.

## Meta

* [Update log](log.md) - Append-only, oldest-first history of knowledge changes in this bundle; new entries go at the end of the file.

> Structure note: add concepts under `project/`, or create a new group directory with its
> own `index.md` as the knowledge grows. Keep each entry above in sync with its concept's
> `description` field.
