---
type: Project Overview
title: eh.DockManager
description: Home-server and enterprise container/cluster control plane, port 443 reverse proxy and app portal, auto-updater, and MCP server built on .NET 11 and React 19.
tags: [project, dockmanager, architecture]
status: stable
generated: { by: opencode/deepseek-v4.1-flash, at: 2026-11-14T09:12:01Z }
sources:
  - id: prompt-md
    resource: ../../PROMPT.md
    title: PROMPT.md — build brief and requirements
    author: human:ehud
    last_modified: 2026-11-13T09:38:00Z
---

# Purpose

`eh.DockManager` is a control plane for home servers and enterprise container/cluster
hosts. It reverse-proxies every app behind port 443, presents a landing portal of
auto-discovered apps, manages Docker hosts and Kubernetes clusters, auto-updates Compose
stacks with health-guarded rollback, and exposes itself to agents through a built-in MCP
server.[^prompt-md]

# Requirements source of truth

All functional requirements live in [`PROMPT.md`](../../PROMPT.md) at the repository root.
That document is normative; this concept is a navigation aid, not a substitute. Read it
before planning work, and read
[Stack constraints](/project/stack-constraints.md) for the binding dependency and
build rules.[^prompt-md]

# Subsystems

| Subsystem | Responsibility |
|-----------|----------------|
| Reverse proxy & portal | Port 443 entry point, dynamic YARP routes, app auto-discovery from Docker events and K8s watches, landing index page |
| Scale-to-zero | `ServerlessWakeMiddleware`: stops idle containers, wakes them (Docker start or Wake-on-LAN) and holds the request until ready |
| Docker engine | Unix-socket and mTLS remote hosts, registries, container file browser over `/archive`, agentless CVE scanning via OSV.dev, single-binary `--agent` mode |
| Kubernetes engine | Multi-cluster kubeconfig/token import, pods, deployments, services, ingresses/`HTTPRoutes`, logs, pod exec |
| Compose stacks | Compose file storage on disk, selective recreate by image hash, `docker run` ↔ Compose ↔ Kubernetes conversion |
| Auto-updater | Per-stack image pulls, 25-second health guard, automatic rollback, image pruning |
| Auth & SSO | Local password, passkeys/WebAuthn, TOTP, Google/Microsoft OIDC, SAML 2.0 SP and IdP |
| MCP server & webhooks | `/mcp` JSON-RPC 2.0 over scoped bearer tokens, multi-provider webhook notifications; users are manageable over MCP except Admin accounts |
| Network metrics | 24-hour in-memory traffic history per host and container: rates, 1h/24h totals, drops/errors and TCP connection resets |
| Frontend | Vite + React 19 + TypeScript + Tailwind; ten views (adds Users and Settings), a line-icon set, info tooltips, expandable operational panels, persisted refresh intervals, role-aware navigation, clickable dashboard tiles, PC and mobile experiences |

# Repository layout

| Path | Contents |
|------|----------|
| `PROMPT.md` | Build brief and requirements (normative) |
| `global.json` | .NET SDK pin (`11.0.100`, `rollForward: latestMajor`) |
| `opencode.json` | OpenCode configuration (default model `openrouter/deepseek/deepseek-v4.1-flash`; DebugMCP and Chrome DevTools MCP servers) |
| `src/eh.DockManager.Server/` | ASP.NET Core server, implemented: `Program.cs` (hosts everything), `Models/DomainModels.cs`, `Data/SqliteWalStore.cs`, `Security/{CryptoAndCertManager,AuthAndSsoEngine,QrCodeGenerator,AcmeClient}.cs`, `Docker/{DockerSocketClient,DockerStreamDemuxer,NetworkMetricsTracker}.cs`, `Stacks/ComposeAndConverters.cs`, `Kubernetes/KubeClusterService.cs`, `Portal/YarpPortalProxy.cs`, `McpAndWebhooks/McpAndWebhookEngine.cs` |
| `src/eh.DockManager.Client/` | Vite + React 19 + Tailwind client, implemented: `index.html`, `package.json`, `vite.config.ts`, `tailwind.config.js`, `postcss.config.js`, `tsconfig.json`, and `src/{main.tsx,App.tsx,ui.tsx,icons.tsx,api.ts,types.ts,index.css}` (see [UI & UX conventions](/project/ui-ux.md)) |
| `Dockerfile`, `docker-compose.yml`, `.dockerignore`, `.env.example` | Multi-stage build and deployment; `.env.example` is the host-local template (the real `.env` is gitignored) |
| `build.sh` | Build helper: exports `BUILD_NUMBER` = `git rev-list --count HEAD` and runs `docker compose build`, stamping the date-based version |
| `AGENTS.md` | Binding knowledge-workflow rules for every agent |
| `okf/` | This knowledge bundle |
| `.opencode/skills/okf/` | The `okf` skill that defines the knowledge workflow |

`PROMPT.md` §9 fixes the file generation order; the delivered set follows it (see
[Delivered implementation](/project/implementation-status.md) for the exact files, the
runtime behaviours exercised, and the deviations discovered during the build).

# Knowledge and workflow

* Mandatory agent workflow (consult then update this bundle): [`/AGENTS.md`](../../AGENTS.md).
* Skill that implements it: `.opencode/skills/okf/SKILL.md`.
* Change history for this bundle: [Update log](/log.md).
* Binding dependency and build rules: [Stack constraints](/project/stack-constraints.md).
* Host-side compose prerequisites: [Host preparation](/project/deployment.md).
* Connecting a remote Docker engine: [Remote Docker hosts](/project/remote-docker-hosts.md).
* Working-environment guardrails (PII/secret redaction in tool output):
  [Agent environment guardrails](/environment/guardrails.md).
* Browser automation against a laptop Chrome from this host:
  [Chrome DevTools MCP over an SSH reverse tunnel](/environment/chrome-mcp-over-ssh.md).
* Default model for agent sessions in this repository:
  [OpenCode default model](/environment/opencode-default-model.md).

# Notes

Transcribed from `PROMPT.md` by an agent. No independent verification has been recorded
(the absence of a `verified` entry means the **unverified** trust tier, per OKF v0.2);
`PROMPT.md` remains the authority for every requirement stated here.

[^prompt-md]: PROMPT.md — build brief and requirements
