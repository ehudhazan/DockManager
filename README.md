# eh.DockManager

> **The hardened home-server & enterprise container/cluster control plane.**
> A port-443 reverse proxy and app portal, multi-host Docker and multi-cluster Kubernetes
> manager, health-guarded Compose auto-updater, and a built-in MCP server for agents —
> shipped as one trimmed, single-file .NET 11 binary behind a distroless, read-only,
> non-root container.

`eh.DockManager` puts every app on your network behind `https://<name>.<your-domain>`, presents
them on a categorised landing portal, and gives you one place to run containers, Compose stacks
and Kubernetes workloads. Every managed resource is stamped with its provenance, so a change
made by the Web UI, the CLI, the auto-updater or an MCP agent looks and behaves the same.

---

## Table of contents

- [Features](#features)
- [Architecture](#architecture)
- [Quick start](#quick-start)
- [Configuration](#configuration)
- [HTTPS & certificate trust](#https--certificate-trust)
- [Authentication](#authentication)
- [MCP server](#mcp-server)
- [Development](#development)
- [Building & publishing](#building--publishing)
- [Project layout](#project-layout)
- [Documentation](#documentation)

---

## Features

### Reverse proxy & app portal
- **Port 443 entry point** for every app, with dynamic `Yarp.ReverseProxy` routes updated in
  memory (no restarts) from Docker `/events` and Kubernetes watches.
- **Auto-discovery** from container ports and `eh.portal.*` labels (`enable`, `subdomain`,
  `name`, `icon`, `category`, `port`, `auth=public|sso|admin`, `autoscale=idle-15m`).
- **Landing portal** of categorised app cards with search, live status, CPU/RAM sparklines, and
  full in-place editing — hide, reorder, add manual external links, or bulk-add discovered apps.
- **Scale-to-zero & Wake-on-LAN**: idle containers are stopped and woken (Docker start or an
  IEEE 802.3 magic packet) while the request is held until the app is ready.

### Docker & Kubernetes
- **Pure Unix-socket + mTLS Docker client** — local `/var/run/docker.sock`, or remote hosts over
  mutually authenticated TLS. Single-binary **`--agent`** mode turns a remote host into an mTLS
  endpoint.
- **Multi-cluster Kubernetes** — import `kubeconfig` or raw token/certs, then manage pods,
  deployments, services, ingresses/Gateway API `HTTPRoutes`, pod logs and pod exec.
- **Container file browser** over `/containers/{id}/archive` (works on stopped and distroless
  containers), and an **agentless CVE scanner** that reads the package database and queries
  [OSV.dev](https://osv.dev).
- **3-way converter**: `docker run` ↔ `docker-compose.yml` ↔ Kubernetes manifests.

### Compose stacks & auto-update
- Compose files stored as **real files on disk** (`/data/stacks/{name}/compose.yaml` + `.env`),
  so manual `docker compose` CLI use stays fully compatible.
- Secrets referenced as `${VAULT:NAME}` are encrypted at rest (AES-GCM) and injected in memory.
- **Per-stack auto-updater** with a 25-second health guard and automatic rollback on crash or
  unhealthy state, plus optional old-image pruning.
- A **schema-aware YAML editor** with keyboard completions and line-accurate validation.

### Security & operations
- Five authentication methods, a built-in PKI with hot-swapped certificates, and optional
  **Let's Encrypt (ACME v2)** for a publicly trusted certificate.
- A 24-hour in-memory **network metrics** history (rates, 1h/24h totals, drops, errors, TCP
  connection resets) and a dashboard that fans the per-container stats out cheaply.
- **Multi-provider webhooks** (Discord, Telegram, Slack, Ntfy, Gotify, generic HMAC-SHA256).
- **MCP server** at `/mcp` with scoped bearer keys and a 60-tool surface.
- **Installable PWA** client with a command palette, mobile touch terminal bar and role-aware views.

---

## Architecture

| Layer | Technology |
|-------|------------|
| Server | **.NET 11** (`net11.0`) — trimmed, ReadyToRun composite, single-file, dynamic PGO, invariant globalization |
| Client | **React 19 + TypeScript + Vite + Tailwind CSS** |
| Proxy | `Yarp.ReverseProxy` 2.3.0 (dynamic `IProxyConfigProvider`) |
| Persistence | **SQLite** (WAL) at `/data/dockmanager.db` via `Microsoft.Data.Sqlite` |
| YAML | `YamlDotNet` 16.3.0 |
| Auth | `Fido2.AspNet` 4.3.2, `Microsoft.AspNetCore.Authentication.OpenIdConnect`, `ITfoxtec.Identity.Saml2` 4.21.0 |
| Kubernetes | `KubernetesClient` 18.0.13+ |
| Container | Distroless, non-root (`uid 64198`), read-only rootfs, all capabilities dropped |

The dependency set is a **closed, audited allowlist** — see
[`PROMPT.md`](PROMPT.md) §1 and [`okf/project/stack-constraints.md`](okf/project/stack-constraints.md).
`Docker.DotNet`, `Sustainsys.Saml2`, `Newtonsoft.Json` and third-party TOTP packages are banned;
the TOTP implementation, the QR encoder and the ACME client are all vendored in-tree.

**Listen ports**

| Port | Purpose |
|------|---------|
| `80` | HTTP → HTTPS redirect |
| `443` | Public/LAN app portal, reverse proxy, admin UI (TLS 1.2/1.3, HSTS) |
| `8443` | Admin and mTLS API port |
| `2376` | `--agent` mode: mTLS client-cert-validated Docker endpoint |

---

## Quick start

### Prerequisites
- Docker Engine with the Compose plugin
- A Linux host with `/var/run/docker.sock`
- Ports `80`, `443` and `8443` free

### 1. Prepare the host

The image is distroless and runs as uid/gid **`64198`**. Docker creates a missing bind-mount
directory as `root:root`, so the container cannot write `/data` unless you pre-create it:

```bash
git clone https://github.com/ehudhazan/eh.DockManager.git
cd eh.DockManager

mkdir -p data
sudo chown -R 64198:64198 data
```

### 2. Configure the environment

```bash
cp .env.example .env
```

Set at least `DOCKER_GID` to the host's docker group id so the container can read the socket:

```bash
getent group docker | cut -d: -f3   # e.g. 988
```

### 3. Start the stack

```bash
docker compose up -d
docker compose ps                    # container is Up
curl -sk https://127.0.0.1/api/system/health   # 200
```

### 4. Sign in

Open `https://<host>/` in a browser. The first run bootstraps an administrator when the users
table is empty:

- If `EHDM_ADMIN_PASSWORD` is set, that is the password.
- If it is empty (the default), a random 16-byte password is generated and logged **once**:

```bash
docker logs eh-dockmanager | grep "Initial admin credentials"
```

> If the users table already exists, `EHDM_ADMIN_USER` / `EHDM_ADMIN_PASSWORD` have no effect —
> they only seed the **first** admin. See
> [`okf/project/deployment.md`](okf/project/deployment.md) for how to reset.

---

## Configuration

All values are optional except `DOCKER_GID`. They are read from `.env` (gitignored;
`.env.example` is the committed template).

| Variable | Default | Purpose |
|----------|---------|---------|
| `DOCKER_GID` | `999` | Host docker group id, so the container can read the socket |
| `EHDM_ADMIN_USER` | `admin` | First-run bootstrap administrator username |
| `EHDM_ADMIN_PASSWORD` | _(random)_ | First-run bootstrap password; empty ⇒ random, logged once |
| `EHDM_PORTAL_DOMAIN` | `dock.lan` | Portal hostname; drives the certificate SANs and app URLs |
| `EHDM_LAN_IP` | `10.10.1.1` | Host LAN IP, added to the certificate SANs |
| `EHDM_DATA_DIR` | `/data` | Data directory (database, stacks, certs, vault key) |
| `EHDM_HTTP_PORT` | `80` | HTTP listen port |
| `EHDM_PORTAL_PORT` | `443` | Portal/reverse-proxy listen port |
| `EHDM_ADMIN_PORT` | `8443` | Admin/mTLS listen port |

The binary also accepts two flags: `--health-probe` (exit 0/1 against `/api/system/health`,
used by the container healthcheck) and `--agent` (run in mTLS agent mode).

---

## HTTPS & certificate trust

On first start, `eh.DockManager` builds an internal PKI:

- An **ECDSA P-384 root CA** at `/data/certs/ca.pfx` / `ca.crt`
- An **ECDSA P-256 server certificate** signed by that CA, with SANs covering the portal domain,
  `localhost`, `*.dock.lan`, `127.0.0.1`, the configured LAN IP and any user-added hostnames

Because the CA is private, browsers show a certificate warning until you trust `ca.crt`
(downloadable from **Settings → Security**). **Browse by hostname, not IP**, or the certificate
will not match. Settings lists per-platform trust steps (Windows / macOS / Linux / iOS / Android).

For a publicly trusted certificate with no trust step, enable **Let's Encrypt** in Settings: a
hand-built ACME v2 client (http-01 challenge) issues and auto-renews the certificate. It requires
a public domain pointing at the host and inbound port 80.

Certificates are **hot-swapped with zero downtime** — no restart needed after a re-issue.

---

## Authentication

Five methods are supported, configured from the **Security & SSO** view:

1. **Local password** — PBKDF2-HMAC-SHA512, 600,000 iterations, constant-time comparison.
2. **Passkeys / WebAuthn** — passwordless biometric or hardware-key login.
3. **TOTP authenticator** — pure RFC 6238 (Google/Microsoft Authenticator), with an SVG QR code.
4. **Google & Microsoft OAuth2 / OIDC** — unknown emails are created as `Pending` and must be
   approved by an admin (as `Viewer` or `Admin`).
5. **SAML 2.0** — both **SP** (Entra ID, Google Workspace, Okta, …) and **IdP** mode, with
   assertion-replay deduplication, XXE/XML-bomb guards and `InResponseTo` state verification.

Roles are **Admin** (full control), **Viewer** (Portal + Dashboard only) and **Pending**
(awaiting approval).

---

## MCP server

`eh.DockManager` exposes a **JSON-RPC 2.0 Streamable HTTP** endpoint at `/mcp` for AI agents,
authenticated with scoped bearer keys (`ehdm_…`). Create and manage keys in
**Security → MCP API keys**.

```
POST https://<host>/mcp
Authorization: Bearer ehdm_...
Content-Type: application/json
```

- **60 tools** across stacks, containers, images, registries, hosts, clusters, webhooks, portal,
  users, system and audit.
- Scopes use a `resource:action[:target]` grammar (e.g. `stacks:read+write; containers:read`);
  `tools/list` returns only what the key allows, and a denied call returns JSON-RPC `-32003`.
- Keys can be enabled/disabled, renamed, re-scoped and **rotated**; every action is attributed to
  the **key name** in the audit log.

See [`okf/project/mcp.md`](okf/project/mcp.md) for the full scope grammar and tool surface.

---

## Development

### Server

```bash
dotnet build src/eh.DockManager.Server/eh.DockManager.Server.csproj
dotnet run  --project src/eh.DockManager.Server/eh.DockManager.Server.csproj
```

### Client

```bash
cd src/eh.DockManager.Client
npm install
npm run dev        # Vite dev server
npm run typecheck  # tsc --noEmit
npm run build      # emits into ../../eh.DockManager.Server/wwwroot with .br/.zst/.gz siblings
npm test           # dependency-free YAML engine suite
```

The client builds into the server's `wwwroot`, so `dotnet run` serves the freshly built SPA.

### Verification commands

```bash
dotnet build src/eh.DockManager.Server/eh.DockManager.Server.csproj
npm --prefix src/eh.DockManager.Client run typecheck
npm --prefix src/eh.DockManager.Client run build
./build.sh --no-push   # build + tag latest/version without pushing
```

---

## Building & publishing

The published image is [`ehudhazan/dockmanager`](https://hub.docker.com/r/ehudhazan/dockmanager) on
Docker Hub. `docker-compose.yml` sets `image: ehudhazan/dockmanager:latest` **and** keeps its
`build:` section, so you can either build locally or `docker compose pull` the published tag.

`./build.sh` is the release entry point. It:

1. exports `BUILD_NUMBER` = `git rev-list --count HEAD`,
2. runs `docker compose build`,
3. tags the image **both** `latest` and the date-based version (`yyyy.M.d.<buildNumber>`, e.g.
   `2026.11.14.10`),
4. pushes **both** tags to Docker Hub.

```bash
./build.sh              # build, tag latest + version, push both
./build.sh --no-push    # build and tag only
./build.sh --no-cache   # extra args forward to `docker compose build`
```

The running version is shown in the desktop sidebar footer, the mobile **More** sheet, and
**Settings → System information** (with the boot id and build token). A redeploy raises an
"App upgraded" banner and reloads the shell.

> Base-image note: MCR does not yet publish `.NET 11` "noble" tags, so the `Dockerfile` defaults
> to the available distroless Azure Linux images via `ARG SDK_IMAGE` / `ARG RUNTIME_IMAGE`, which
> you can override with `--build-arg` once the noble tags ship.

---

## Project layout

```
.
├── PROMPT.md                     # Normative build brief and requirements
├── README.md                     # This file
├── AGENTS.md                     # Binding knowledge-workflow rules for agents
├── build.sh                      # Release build + tag + push helper
├── docker-compose.yml            # The stack definition
├── Dockerfile                    # Hardened multi-stage build
├── global.json                   # .NET SDK pin (11.0.100)
├── opencode.json                 # OpenCode configuration
├── src/
│   ├── eh.DockManager.Server/    # ASP.NET Core server
│   │   ├── Program.cs            # Host: Kestrel, YARP, SSE, WebSockets, REST API, --agent mode
│   │   ├── Models/               # Domain models
│   │   ├── Data/                 # SQLite WAL store
│   │   ├── Security/             # PKI, vault, auth/SSO, QR encoder, ACME client
│   │   ├── Docker/               # Unix-socket/mTLS client, stream demuxer, network metrics
│   │   ├── Stacks/               # Compose parser, converters, auto-updater, schema validator
│   │   ├── Kubernetes/           # Multi-cluster manager
│   │   ├── Portal/               # Dynamic YARP provider, app discovery, candidates, wake middleware
│   │   └── McpAndWebhooks/       # /mcp server and multi-provider webhook notifier
│   └── eh.DockManager.Client/    # React 19 + Vite + Tailwind client
│       ├── src/                  # App.tsx, ui.tsx, icons.tsx, yaml.ts, api.ts, types.ts
│       ├── public/               # PWA manifest, service worker, icons
│       └── tests/                # YAML engine test suite
└── okf/                          # Knowledge bundle (consult before work, update after)
```

---

## Documentation

| Document | What it covers |
|----------|----------------|
| [`PROMPT.md`](PROMPT.md) | The normative requirements — the authority for every feature |
| [`okf/index.md`](okf/index.md) | The knowledge bundle index — start here for any subsystem |
| [`okf/project/overview.md`](okf/project/overview.md) | System overview and repository layout |
| [`okf/project/stack-constraints.md`](okf/project/stack-constraints.md) | Dependencies, build, ports and storage rules |
| [`okf/project/deployment.md`](okf/project/deployment.md) | Host-preparation runbook |
| [`okf/project/implementation-status.md`](okf/project/implementation-status.md) | What was built, tested and deviated from the brief |
| [`AGENTS.md`](AGENTS.md) | The knowledge-workflow rules binding every agent |

This repository keeps its engineering knowledge as an
[**OKF**](https://okf.md/) bundle under `okf/`: a directory of markdown concepts with YAML
frontmatter. Agents **must consult it before any work and update it after every change** — see
[`AGENTS.md`](AGENTS.md).
