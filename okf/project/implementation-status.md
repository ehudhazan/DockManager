---
type: Status
title: Delivered implementation
description: What was actually built and verified for eh.DockManager, the generated file set, runtime behaviours exercised, and the deviations from PROMPT.md discovered during the build.
tags: [status, implementation, build, deviations]
status: stable
generated: { by: opencode/deepseek-v4.1-flash, at: 2026-11-13T19:45:00Z }
sources:
  - id: prompt-md
    resource: ../../PROMPT.md
    title: PROMPT.md — build brief and requirements
    author: human:ehud
    last_modified: 2026-11-13T09:38:00Z
---

# Purpose

This concept records the **implementation state** of `eh.DockManager` as of the first
complete build, distinct from the requirements summarized in
[Project overview](/project/overview.md). It exists so a later agent knows what already
exists before regenerating or changing it, and which PROMPT.md rules could not be followed
literally.[^prompt-md]

# Generated file set

The order in PROMPT.md §9 was followed. Every generated file carries a
`// File: relative/path/to/file` header.

| Module | File |
|--------|------|
| Server entry | `src/eh.DockManager.Server/Program.cs` |
| Domain models | `Models/DomainModels.cs` |
| Persistence | `Data/SqliteWalStore.cs` |
| PKI & vault | `Security/CryptoAndCertManager.cs` |
| Auth & SSO | `Security/AuthAndSsoEngine.cs` |
| QR encoder (supporting) | `Security/QrCodeGenerator.cs` |
| Docker client | `Docker/DockerSocketClient.cs`, `Docker/DockerStreamDemuxer.cs` |
| Network metrics | `Docker/NetworkMetricsTracker.cs` |
| Compose & converters | `Stacks/ComposeAndConverters.cs` |
| Kubernetes | `Kubernetes/KubeClusterService.cs` |
| Portal & proxy | `Portal/YarpPortalProxy.cs` |
| MCP & webhooks | `McpAndWebhooks/McpAndWebhookEngine.cs` |
| Client | `src/eh.DockManager.Client/{index.html,package.json,vite.config.ts,tailwind.config.js,postcss.config.js,tsconfig.json,src/{main.tsx,App.tsx,ui.tsx,icons.tsx,api.ts,types.ts,index.css}}` |
| Let's Encrypt | `Security/AcmeClient.cs` (`AcmeChallengeStore`, `AcmeClient`, `AcmeRenewalWorker`) |
| PWA assets | `src/eh.DockManager.Client/public/{manifest.webmanifest,sw.js,icon.svg,icon-maskable.svg}` |
| Container | `Dockerfile`, `docker-compose.yml`, `.dockerignore` |
| Host config | `.env.example` (host-local `.env` is gitignored) |

`Security/QrCodeGenerator.cs` is an intentional **addition** to the §9 list: PROMPT.md bans
third-party TOTP packages and requires an SVG QR generator, so a self-contained QR Code
Model 2 encoder (ported from the public-domain/MIT Nayuki reference algorithm) is vendored
rather than taken from NuGet.[^prompt-md]

# Behaviours exercised

End-to-end smoke tests were run against the built binary (framework-dependent `dotnet run`
and the trimmed single-file Release publish):

* Local login via PBKDF2-HMAC-SHA512; admin bootstrap from `EHDM_ADMIN_USER`/`EHDM_ADMIN_PASSWORD`.
* TOTP enrolment returning `otpauth://` URI plus SVG QR.
* Passkey registration/assertion option generation (Fido2 4.3.2 `RequestNewCredentialParams`
  / `GetAssertionOptionsParams` API).
* `POST /api/system/ssl/regenerate` hot-swapping the leaf certificate (SAN regeneration).
* `docker run` → Compose conversion via YamlDotNet; Compose → Kubernetes manifest generation.
* MCP `initialize` and `tools/list` over `POST /mcp` with a scoped `ehdm_…` bearer token
  (11 tools).
* SPA served from `wwwroot`, with `.zst` and `.br` precompressed siblings returned with the
  matching `Content-Encoding`; HTTP→HTTPS 307 redirect.
* `--health-probe` returning exit 0 while the app listens and exit 1 when it does not.
* The distroless image booting as uid `64198` with a read-only rootfs and all capabilities
  dropped.
* The compose stack starting on a host whose `./data` directory is owned by `64198:64198`
  and whose `DOCKER_GID` matches the host's docker group; `GET /api/system/health` returns
  200 and `GET /api/containers` reaches the Docker socket.

Verification was performed by the building agent (deterministic build/run commands); no
independent `process:`/`human:` verifier has been recorded, so this concept remains in the
**unverified** trust tier.

# Fixes applied during the first compose run

* **Bind-mount permission:** `docker compose up` failed with
  `UnauthorizedAccessException` at `/data/stacks` because Docker created the host
  `./data` directory as `root:root` and the image runs as uid `64198`. Fixed by
  chowning the host directory to `64198:64198`; the host preparation steps are in
  [Host preparation](/project/deployment.md).
* **Empty bootstrap password:** `Program.cs` read `EHDM_ADMIN_PASSWORD` with `??`, which
  only catches `null`, so the compose file's empty-string export bootstrapped the admin
  with an empty password. The fix treats empty values as unset and falls back to a
  random 16-byte password. The `docker-compose.yml` `DOCKER_GID` default was also
  documented as host-specific; `.env.example` records the host's gid.

# Deviations from PROMPT.md

| PROMPT.md rule | Reality | Resolution |
|----------------|---------|------------|
| Runtime base `mcr.microsoft.com/dotnet/runtime-deps:11.0-noble-chiseled` and SDK `…:11.0-noble` | MCR publishes no `11.0-noble` / `11.0-noble-chiseled` tags (Noble exists only through 10.x; .NET 11 ships on Azure Linux and Alpine) | `Dockerfile` exposes overridable `ARG SDK_IMAGE` / `ARG RUNTIME_IMAGE` defaulting to the available distroless images; see [Stack constraints](/project/stack-constraints.md) |
| Trimmed + reflection-based `System.Text.Json` | With `PublishTrimmed`, STJ reflection is disabled by default and the app throws at startup | Set `JsonSerializerIsReflectionEnabledByDefault=true` and added `TrimmerRootAssembly` entries; see [Stack constraints](/project/stack-constraints.md) |
| Fixed ports 80/443/8443 | Binding privileged ports blocks local, non-root runs and smoke tests | Ports are now read from `EHDM_HTTP_PORT` / `EHDM_PORTAL_PORT` / `EHDM_ADMIN_PORT` with the PROMPT.md values as defaults |

# Second pass — UX/UI and operational fixes

A follow-up pass reworked the client and closed several runtime gaps; see
[UI & UX conventions](/project/ui-ux.md) and
[Server identity & hostname settings](/project/server-identity.md).

* **Client** — new `ui.tsx` (InfoTip, StatusPill, RoleBadge, PageHeader, Section,
  StatTile, EmptyState, Tabs, Toggle, Field, ConfirmButton, Modal, Callout,
  `formatBytes`, `formatRelative`) and `icons.tsx` (SVG icon set). Ten views; grouped
  sidebar; mobile bottom nav + More sheet; role-aware navigation; clickable dashboard
  tiles; portal cards with always-visible actions and outline hover.
* **New endpoints** — `GET`/`PUT /api/system/settings` (portal domain, LAN IP, certificate
  SANs), `POST /api/users` (create a local user) and `POST /api/containers` (create and start a
  container from an image with command, env, ports and restart policy). `PUT /api/users/{id}`
  updates role and disabled state. API responses carry `Cache-Control: no-store`.
* **Hostname & certificate** — the leaf certificate CN/default SANs follow the configured
  portal domain, `PUT /api/system/settings` regenerates on request, and startup re-issues
  the leaf if it does not cover the portal domain. The self app's portal URL is the base
  domain (`https://<portalDomain>`), not a subdomain.
* **Exec** — the Docker exec WebSocket accepts before create/start and reports failures
  (both thrown and in-stream OCI errors) as an actionable terminal message; Inspect has a
  shell picker. Verified with Alpine and the distroless DockManager image.
* **Images & files** — clearer repository/untagged/unused labels, short ID, relative age and
  human sizes; image usage derived from containers; multi-select with a bulk delete bar and a
  **Run image** dialog (command, env, ports, restart policy); the file browser lists immediate
  children with human sizes.

# Third pass — portal, dashboard, MCP and audit

* **New endpoints** — `GET /api/system/dashboard` (overall + per-container + per-host aggregate),
  `POST /api/portal/apps/reorder`, `GET /api/clusters/{id}/namespaces`, and `?includeHidden=true`
  on `GET /api/portal/landing`.
* **Portal** — `Hidden` no longer drops the YARP route (it only leaves the landing list); admins can
  reveal hidden apps and reorder cards with persistent `SortOrder`. See
  [Portal visibility & ordering](/project/portal.md).
* **Dashboard** — CPU, memory, disk, traffic and port tiles, a sortable per-container table and one
  panel per Docker host, served by one cached aggregate. See
  [Dashboard metrics & aggregation](/project/dashboard.md).
* **MCP** — keys carry a `resource:action[:target]` scope set enforced on every `tools/call` (JSON-RPC
  `-32003`) and filtered in `tools/list`; the surface grew to **55 tools** (container create/files,
  image inspect, host and cluster CRUD, HTTPRoutes, pod logs, stack secrets) while **users stay
  read-only**; a disabled key no longer authenticates; every action records `ActorDetail = <key name>`.
  Keys can be enabled/disabled, re-scoped and **rotated** (`PUT /api/mcp/keys/{id}`,
  `POST /api/mcp/keys/{id}/rotate`), all audited as `mcp.key.*`. See
  [MCP server permissions & audit](/project/mcp.md).
* **Bugs fixed** — `PortalDiscoveryService` was registered only as a hosted service, so
  `GET /api/portal/refresh` (and the new MCP dependency) threw `Unable to resolve service`; the live
  SSE feed emitted a different envelope than `GET /api/system/audit`, so live rows lost their action
  and key; `McpApiKeyService` ignored `Enabled`; the Containers view omitted ports, size and age; the
  Kubernetes namespace field was free text.
* **Tested** — a scripted end-to-end MCP test (login → create scoped keys → `initialize` /
  `tools/list` / `tools/call`) verified scope-filtered tool lists, allow/deny per resource and per
  target, `-32003` on a denied call, the absence of any user-mutation tool, and
  `actorDetail = <key name>` in both the audit list and the SSE stream. A second suite (25 checks)
  covers the added tools, `host:read`/`container:write` gating, disable → `401`, scope change
  narrowing `tools/list`, and rotate invalidating the old token. `dotnet build`,
  `npm run typecheck`, `npm run build` and `docker compose build` all pass.

# Fourth pass — PWA, Let's Encrypt and on-page guidance

* **PWA** — added `public/manifest.webmanifest` (standalone, SVG + maskable icons), a pass-through
  `public/sw.js` registered from `main.tsx`, `apple-*` meta tags, and an **Install** affordance in
  the top bar (shown when the browser fires `beforeinstallprompt`) and the More sheet. The worker
  caches nothing on purpose, so a deploy never serves a stale shell. `.webmanifest` is served as
  `application/manifest+json` (the static middleware's content-type map gained the entry).
* **Let's Encrypt** — added `Security/AcmeClient.cs`: a hand-built ACME v2 client (ES256 JWS,
  PKCS#10 CSR, http-01) plus an in-memory `AcmeChallengeStore`, a challenge responder registered
  before the HTTPS redirect, `GET`/`PUT /api/system/acme` and `POST /api/system/acme/issue`, and a
  six-hourly `AcmeRenewalWorker` (renews within 30 days of expiry). No new NuGet package — the
  allowlist forbids ACME libraries. New concept
  [Let's Encrypt (ACME v2)](/project/lets-encrypt.md). `Security/AcmeClient.cs` is a supporting
  addition to the PROMPT.md §9 file list, like `QrCodeGenerator.cs`.
* **On-page guidance** — Settings now lists concise per-platform steps for trusting `ca.crt`
  (Windows / macOS / Linux / iOS / Android) with the “use the hostname, not the IP” warning, and a
  **Let's Encrypt** section with domain, email, staging toggle and status.
* **Bug fixed while testing** — `StringContent` appends `; charset=utf-8`, and the ACME CA rejects
  any `Content-Type` other than exactly `application/jose+json`; the client now sets the header
  explicitly.
* **Tested** — against the **staging** directory the flow reached directory/nonce/account/order, the
  registered http-01 challenge and the authorization poll, then returned the expected
  “authorization ended 'invalid'” (the test domain does not serve the token); the challenge path over
  plain HTTP returned `404` while a normal path returned the `307` redirect. `dotnet build`,
  `npm run typecheck`, `npm run build` and `docker compose build` all pass and the live container
  serves the PWA assets and the new endpoints.

# Fifth pass — operational depth, network metrics and client/server freshness

* **Network metrics** — new `Docker/NetworkMetricsTracker.cs`: a hosted+singleton background sampler
  (30 s cadence, 24 h in-memory ring buffer, bounded 6-way fan-out) parsing the full Docker `/stats`
  payload (CPU throttle, memory peak/failcnt, PIDs, block IO, per-direction packets/errors/drops and
  per-interface counters) plus host `/proc/net/snmp` (connection resets, retransmits) and `/proc/net/dev`.
  New endpoints `GET /api/system/network`, `GET /api/containers/{id}/metrics`,
  `GET /api/containers/{id}/network`; the dashboard aggregate now embeds a `network` summary. See
  [Network metrics & traffic history](/project/network-metrics.md).
* **Richer dashboard** — per-container rows gained cores, throttle, memory peak/failcnt, PIDs, block IO,
  packets/errors/drops; `overall` gained paused/stopped counts; the client shows a dense expandable
  `ContainerMetricGrid` and a dual-series traffic chart in a `NetworkSection`.
* **MCP users** — `list_users` now flags Admin rows as `protected`, and four tools were added:
  `get_user`, `create_user`, `update_user`, `delete_user`. They operate on **all non-admin users** and
  refuse to read, modify, delete or create an **Admin** account (JSON-RPC `-32003`). Tool surface: **59**.
  See [MCP server permissions & audit](/project/mcp.md).
* **MCP key rename** — `PUT /api/mcp/keys/{id}` now accepts `name`, and the Security view has a per-row
  **Edit** dialog (rename, re-scope, enable/disable) so the audit log's key name stays meaningful.
* **Client/server freshness** — `/api/system/health` returns `appVersion` (a hash of the built
  `index.html`) and `bootId`; the client polls it every 15 s, shows a "Server disconnected" banner when
  the server is unreachable, and reloads (after refreshing the service worker) when `appVersion` changes.
  `sw.js`, `manifest.webmanifest` and `index.html` are served `no-cache`. See
  [Client/server version negotiation & asset freshness](/project/client-server-versioning.md).
* **Stack stats** — `GET /api/stacks/{name}/stats` returns per-service container state/health/CPU/memory/
  PIDs/disk/network plus stack totals and the auto-update counters; the Stacks view renders it in an
  expandable panel and the auto-update interval is now a preset dropdown (15 m … month) with a custom
  minutes field.
* **UI fixes** — a reusable `RefreshControl` (Off/1s/5s/10s/30s/1m/5m/10m/30m, persisted) replaced the
  per-view timers; an `Expand` disclosure carries hidden-until-opened operational info on portal cards,
  dashboard rows, the inspector, network panels and stacks; ports are sorted by container port; navigation
  scrolls to the top; the container log view has a persisted **timestamps** checkbox; Settings gained a
  grouped **Display settings** section; and several mobile issues were fixed (terminal close button, TOTP
  modal, hosts layout, agent-enrollment dialog, username/email input types).
* **Bug fixed** — `Compose → K8s` rendered blank because `api.convertToK8s` parsed the YAML response as
  JSON; a raw-text request helper (`reqText`) now reads it correctly.
* **Tested** — server and client build, `npm run typecheck`, `npm run build` and `docker compose build`
  pass. Live checks against the running container: `/api/system/health` returns `appVersion`/`bootId`;
  the dashboard embeds `network` with 1h/24h totals, TCP counters (including `estabResets`) and host
  interfaces; `/api/stacks/smoketest/stats` returned the expected shape (stack created and deleted in the
  test); and a scripted MCP run with `user:list+read+write+delete` confirmed `create_user`/`update_user`/
  `delete_user` work while every Admin-targeted call is rejected with `-32003`. No `verified` entry was
  added (self-run tests are not independent verification).

# Sixth pass — schema-aware stack editor

* **Validator** — new `Stacks/ComposeSchemaValidator.cs` behind `POST /api/stacks/validate`: YAML parse
  errors carry the parser's line/column, and schema checks warn on unknown keys and error on bad enum
  values (`restart`, `network_mode`, `pull_policy`, `depends_on` conditions, logging/network drivers,
  volume `type`/`consistency`, `healthcheck.test`) and invalid port mappings. A locating pass re-attaches a
  **source line** to findings that name a property or literal (including list items), so the editor can jump
  to them. Warnings never block a save.
* **Editor** — new `src/yaml.ts` (compose schema tree, completion engine, smart Enter/Tab, local enum lint)
  and a `YamlEditor` component: schema-driven key/value completions with keyboard navigation and the
  resolved path, Enter that continues lists and opens mappings, `Tab`/`Shift+Tab` selection indentation, and
  a clickable, de-duplicated issue list. The Stacks view blocks **Save** while errors exist and adds a
  **Validate** action. See [Stack YAML editor](/project/stack-yaml-editor.md).
* **Tested** — the validator returned `valid: true` for a good compose file and located `restart: sometimes`
  (L4:14), `- "not-a-port"` (L6:10), `volumes[1].type: bork` (L8:15) and an unknown `portz` key (L7:1) on
  the expected lines, and reported a YAML syntax error at its parser position. `dotnet build`,
  `npm run typecheck`, `npm run build` and `docker compose build` pass. No `verified` entry was added.

# Notes

`PROMPT.md` remains normative. Where the implemented system intentionally departs from a
literal reading, the departure is listed above and explained; a follow-up should either
revert it (once the noble images ship) or amend the requirement.[^prompt-md]

[^prompt-md]: PROMPT.md — build brief and requirements
