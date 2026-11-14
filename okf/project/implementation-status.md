---
type: Status
title: Delivered implementation
description: What was actually built and verified for eh.DockManager, the generated file set, runtime behaviours exercised, and the deviations from PROMPT.md discovered during the build.
tags: [status, implementation, build, deviations]
status: stable
generated: { by: opencode/deepseek-v4.1-flash, at: 2026-11-14T11:04:22Z }
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

# Seventh pass — editor fix, validate_stack MCP tool, richer charts

* **Stack editor bug fixed** — `suggest()` returned a **line-relative** `replaceFrom` while `YamlEditor`
  applied it as an **absolute** document offset, so choosing a completion on any line but the first wiped
  everything before it (the reported "keyboard menu deleted all"). `suggest()` now returns an absolute
  offset and the editor splices up to the caret. Also fixed: completing an empty value opens a mapping
  child or list item on the next line (`ports:` → `- "8080:80"`) and gives a scalar its separating space
  (`restart:` → `restart: always`); a bare `- ` completes the item schema instead of doubling the dash;
  `Enter` inside a `- key: value` list item continues its properties (not a new dash); `Enter` with a
  selection no longer duplicates the tail; and auto-open fires only at key positions and closed enums. The
  completion list is anchored to the caret line and the editor area is a bounded flex region, so the hint
  and issue labels below it are no longer overlapped. See [Stack YAML editor](/project/stack-yaml-editor.md).
* **`validate_stack` MCP tool** — the editor's validator is reachable over MCP (`stack:read`), taking `yaml`
  or a saved stack `name` and returning `{ valid, name, serviceCount, issues[] }` with located line/column
  findings. Tool surface: **60**. See [MCP server permissions & audit](/project/mcp.md).
* **Richer charts** — the stats SSE (`GET /api/containers/{id}/stats`) now emits the full
  `ParseExtendedMetric` sample (cores, memory peak, PIDs, block read/written, network bytes/packets); the
  Portal card's **More info** shows live network and disk sparklines plus process/thread, core, memory-peak
  and block-dotnet counters, the container inspector adds a **Disk throughput** chart, and the Dashboard's
  Network traffic panel adds host load/process counters and a **Host load & processes** disclosure
  (`HostSystemCounters` from `/proc/loadavg`, `/proc/stat`, `/proc/meminfo`). New `MiniMetric`/`counterRate`
  atoms; see [UI & UX conventions](/project/ui-ux.md) and
  [Network metrics & traffic history](/project/network-metrics.md).
* **Tests** — a dependency-free client suite (`src/eh.DockManager.Client/tests/yaml.test.mjs`, run with
  `npm test`) holds 38 checks over the engine and the editor's key handling. Verified live against the
  rebuilt container through the browser MCP: the completion applies without deleting text, the menu and
  labels render correctly, the portal and inspector charts populate, and an end-to-end MCP run showed the
  scope-filtered `tools/list` (`list_stacks`, `get_stack`, `validate_stack`), `validate_stack` returning
  `valid:false` with located issues, and `save_stack` denied with `-32003`. `dotnet build`,
  `npm run typecheck`, `npm run build`, `npm test` and `docker compose build` pass. No `verified` entry was
  added (self-run tests are not independent verification).

# Eighth pass — mobile UI audit (balloons, side scroll, squeezed titles)

Driven by the operator's request to walk every page, menu and info balloon on a phone and make sure
the UI is correct: no balloon outside the viewport, no side scroll. Checked on the live portal at a
390×844 mobile viewport (all ten views, every `Expand` disclosure open, thirteen dialogs, the bottom
sheet, the command palette, the user menu, the terminal drawer and the sign-in screen).

* **Info balloons all stayed inside the viewport** (was 38 of 51 outside). `InfoTip` now renders the
  balloon through a portal on `document.body` as a `position: fixed` box placed from the trigger's
  viewport rect — clamped horizontally, flipped above the trigger at the bottom edge, re-placed on
  resize/scroll, `z-[65]`, and hover-open only for `hover: hover` pointers. A balloon left in place
  enlarged `main`'s scroll area (the blank side-scroll) and, inside a `backdrop-blur` card, was
  positioned against the card because `backdrop-filter` makes it a containing block for fixed
  descendants.
* **No side scroll anywhere**: `main` gained `overflow-x: hidden` as a backstop (the `body`
  `overflow-x: clip` alone did not help, because the overflow lived inside `main`). The only
  containers that scroll sideways are deliberate ones (table wrappers, tab strips, the terminal log
  block and `MobileTouchBar`).
* **Two cards drew outside the phone viewport and were fixed**: the Dashboard **Provenance event
  feed** card (a grid item's `min-width: auto` let its 417 px `min-content` beat the 390 px track —
  `min-w-0` on the grid children plus a shrinkable actor-detail chip) and the Hosts card, where a
  wide actions row squeezed the title to ~119 px so the info tip landed on top of the kind badge
  (`Section`'s title column now keeps `min-w-[10rem]`, the title row is `min-w-0`, the `h2` is
  `min-w-0 break-words`, and the host name truncates).
* **Placeholders clipped mid-word on a phone were shortened**: Portal search → “Search apps…”,
  command palette → “Type a command…” (`portal` 292 px of text in 141 px of field).
* **Desktop regression found while verifying**: the Portal card's action row drew outside the card in
  a 3–4 column grid; the row now wraps. See [UI & UX conventions](/project/ui-ux.md) for the mobile
  invariants.

`npm run typecheck`, `npm run build`, `dotnet build` (0 errors) and `docker compose build` pass, and
the deployed bundle's asset hash matches the local build (`index-DYHSMK9s.js`). No `verified` entry
was added — a self-run audit is not independent verification.

# Ninth pass — remote Docker host transport fixes

Driven by the operator's report that adding a remote Docker host fails when the certificates are
not valid and there is no CA to trust. The mTLS agent path could not complete a handshake; see
[Remote Docker hosts](/project/remote-docker-hosts.md) for the model and the runbook.

* **The enrollment endpoint was unusable.** `POST /api/hosts/agent/enroll` returned
  `tcp://host:2376`; `HttpClient` accepts a `tcp://` `BaseAddress` but every send throws
  `NotSupportedException: The 'tcp' scheme is not supported.` `DockerSocketClient.NormalizeEndpoint`
  now maps `tcp://` (and a bare `host:port`) to `https://`, applied on save (`POST /api/hosts`, MCP
  `save_host`) and again in the client constructor, so legacy rows are repaired on connect.
* **The agent presented a certificate DockManager could not validate.** It served a per-start
  self-signed certificate. Enrollment now also issues a **server** certificate signed by the internal
  root CA (SANs = the remote host address + `localhost`) with `CertificateManager.IssueAgentServerCertificate`,
  and the generated compose mounts it as `/certs/server.crt` with `--tls-cert`/`--tls-key`; DockManager
  trusts it through the existing internal-CA chain (validation rule 2).
* **The agent never loaded the CA**, so it rejected the enrolled client certificate (its compose
  mounted `./agent-certs` but the code read only `--port`/`--socket`). `EhAgent` now loads
  `/certs/ca.crt` and validates client certificates against it with a custom-root-trust chain;
  without the files it still falls back to a self-signed certificate.
* **Editing a host wiped its certificates.** The UI cannot receive the PEMs (`[JsonIgnore]`), so
  `POST /api/hosts` now keeps the stored `ClientCertPem`/`ClientKeyPem`/`CaCertPem` when the incoming
  value is empty. The UI states this and the host form/runbook now describe the `https://` endpoint
  and the `./agent-certs` files.
* **`X509Certificate2.CreateFromPemFile(path)` (cert-only) throws in .NET 11**; the agent uses
  `X509CertificateLoader.LoadCertificateFromFile` instead.

Verified end-to-end against a local instance (temp `EHDM_DATA_DIR`, ports 18443/12376): enrollment
returns `https://127.0.0.1:12376`; `curl` with the enrolled client certificate against the running
agent returns `OK` (HTTP 200) and without a certificate the handshake fails; `POST /api/hosts` with a
`tcp://` endpoint stores `https://…` and `POST /api/hosts/{id}/test` returns `{"online":true}`; a
second save with blank certificate fields keeps the stored certs (lengths 724/226/785) and the host
stays online. `dotnet build` (0 errors), `npm run typecheck`, `npm run build` and `docker compose build`
pass. No `verified` entry was added — self-run checks are not independent verification.

# Tenth pass — date-based version with a git build number

The displayed version changed from `1.0.0+<index.html hash>` to a date-based `yyyy.M.d.<buildNumber>`
(e.g. `2026.11.14.8`), per the operator's request, and the client now shows it.

* `eh.DockManager.Server.csproj` gained a `ResolveBuildVersion` MSBuild target that sets the assembly
  `Version`/`InformationalVersion` to `yyyy.M.d.<buildNumber>`; `Program.cs` reads it back from the assembly
  version and exposes it as `version` on `GET /api/system/health` and `/api/system/info` (the hardcoded
  `1.0.0` in `/info` is gone). The asset hash stays as `appVersion` — the token the client watches for a
  redeploy.
* The build number is the git commit count, read by MSBuild when a checkout is present; otherwise the
  `BUILD_NUMBER` Docker build arg (`docker-compose.yml` passes `${BUILD_NUMBER:-}`, the `Dockerfile` forwards
  it as `-p:BuildNumber`), otherwise an `HHmm` clock fallback. The MSBuild git step is guarded on a `.git`
  checkout, which Docker's context excludes.
* New root `build.sh` (a supporting addition to the PROMPT.md §9 file list, like `QrCodeGenerator.cs` and
  `AcmeClient.cs`) exports `BUILD_NUMBER=git rev-list --count HEAD` and runs `docker compose build`, so
  `./build.sh` stamps the commit count while a plain `docker compose build` uses the fallback.
* Client: `HealthInfo.version` added; the sidebar footer and Settings → System information show the date
  version (plus a `Build token` row for `appVersion` and the `bootId`); the reload watcher still keys on
  `appVersion`.

Verified: `dotnet build` stamps `2026.11.14.8` (commit count 8) and `-p:BuildNumber=999` stamps
`2026.11.14.999`; `./build.sh` builds the image and the running container's `/api/system/health` returns
`{"version":"2026.11.14.8","appVersion":"1.0.0+fa1547e93fa9",…}`. `npm run typecheck` and `npm run build`
pass. No `verified` entry was added — self-run checks are not independent verification.

# Eleventh pass — toast polish

The PWA **Install app** toast was narrow, short-lived and static. Fixed in the client:

* **Width.** `Toast` used `left-1/2` with no `right`/width, so a `position: fixed` element's
  shrink-to-fit width was capped at ~50vw — very narrow on a phone. Phones now use `left-3 right-3`
  (full width between margins); `md`+ keeps the centred, content-sized (max 28rem) layout.
* **Lifetime.** The on-screen time moved into `Toast` and is overridable per
  `notify(message, tone, durationMs)`; the install instructions pass `0` for a **sticky** toast that
  stays until dismissed (defaults: 3.6 s, 5.2 s for errors). A new monotonic `id` remounts the toast
  so the entrance animation replays every time.
* **Animation.** `tailwind.config.js` gained `toastIn`/`toastOut` keyframes (opacity plus the
  independent `translate`/`scale` properties, so the centring `-translate-x-1/2` survives) and
  animations; `Toast` animates in, and on dismiss or timeout switches to the exit animation and
  unmounts when it ends (220 ms), with a `motion-reduce` opt-out.

Verified in the build: `npm run typecheck` and `npm run build` pass, and the emitted CSS contains
`@keyframes toastIn`/`toastOut`. No `verified` entry was added — self-run checks are not independent
verification.

# Twelfth pass — mobile version visibility

The date-based version was only visible in the desktop sidebar footer (which is `hidden md:flex`) and in
Settings → System information, which is admin-only — so a phone (or a Viewer) had nowhere to read it. The
bottom **More** sheet now carries a footer line `eh.DockManager · v2026.11.14.8`, fed by the same
`serverVersion` the health poll sets and shown for every role; `BottomNav`/`MoreSheet` take an `appVersion`
prop from `App`.

Verified in the build: `npm run typecheck` and `npm run build` pass. No `verified` entry was added —
self-run checks are not independent verification.

# Notes

`PROMPT.md` remains normative. Where the implemented system intentionally departs from a
literal reading, the departure is listed above and explained; a follow-up should either
revert it (once the noble images ship) or amend the requirement.[^prompt-md]

[^prompt-md]: PROMPT.md — build brief and requirements
