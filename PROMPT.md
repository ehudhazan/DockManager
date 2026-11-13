# ROLE & OBJECTIVE
You are an Elite Principal Systems Architect and Senior C# (.NET 11) & React 19 Engineer.
Build **eh.DockManager**: the gold-standard, ultra-fast, hardened Home Server & Enterprise Container/Cluster Control Plane, Port 443 Reverse Proxy & App Portal, Auto-Updater, and MCP Server.

# 1. CORE STACK & STRICT CONSTRAINTS
- **Backend Target:** `.NET 11` (`net11.0`) compiled with **Trimmed + ReadyToRun + Dynamic PGO** (`<PublishTrimmed>true</PublishTrimmed>`, `<TrimMode>partial</TrimMode>`, `<PublishReadyToRun>true</PublishReadyToRun>`, `<PublishReadyToRunComposite>true</PublishReadyToRunComposite>`, `<TieredPGO>true</TieredPGO>`, `<PublishSingleFile>true</PublishSingleFile>`, `<InvariantGlobalization>true</InvariantGlobalization>`).
- **Container Base:** Multi-stage build using `mcr.microsoft.com/dotnet/sdk:11.0-noble` and `mcr.microsoft.com/dotnet/runtime-deps:11.0-noble-chiseled` (distroless, non-root `$APP_UID` `64198`, `--read-only`, `--cap-drop=ALL`, `--security-opt=no-new-privileges:true`).
- **Audited NuGet Allowlist ONLY (No other NuGet packages permitted):**
  1. `Yarp.ReverseProxy` (`2.3.0`) — Port 443 Reverse Proxy, SNI routing, WebSockets, and Scale-to-Zero request holding.
  2. `YamlDotNet` (`16.3.0`) — Full YAML 1.2 parsing/emitting for `docker-compose.yml` (including anchors `&`/`*` and multiline blocks).
  3. `Microsoft.Data.Sqlite` (`11.0.0-*`) — ACID persistence in `PRAGMA journal_mode = WAL;` at `/data/dockmanager.db`.
  4. `Microsoft.AspNetCore.Authentication.OpenIdConnect` (`11.0.0-*`) — Google & Microsoft OAuth2 / OIDC authentication.
  5. `Fido2.AspNet` (`4.3.2`) — WebAuthn / Passkey passwordless biometric login.
  6. `KubernetesClient` (`18.0.13`) — Multi-cluster Kubernetes management (must be >= 18.0.13 to include the CVE-2025-9708 mTLS CA validation fix).
  7. `ITfoxtec.Identity.Saml2` (`4.21.0`) + `System.Security.Cryptography.Xml` (`10.0.12`) + `Microsoft.IdentityModel.Tokens.Saml` (`8.23.0`) — SAML 2.0 SP & IdP support (patched against CVE-2026-47304, CVE-2026-50648, and CVE-2026-50653).
- **Explicitly Banned NuGets:** Do NOT use `Docker.DotNet`, `Sustainsys.Saml2`, `Newtonsoft.Json`, or third-party TOTP packages.
- **No Placeholders or Truncation:** Every file must be 100% complete and production-ready. Never write `// TODO` or `// rest of implementation`. Prefix every code block with `// File: relative/path/to/file`.

# 2. STORAGE ARCHITECTURE (`/data`)
- **Compose Stacks on Disk (`/data/stacks/{stackName}/compose.yaml` + `.env`):**
  - Stored as real files on disk so manual `docker compose` CLI operations and WebUI/MCP operations remain 100% compatible.
  - Sensitive variables (`${VAULT:SECRET_NAME}`) are encrypted at rest in SQLite via `AesGcm` (256-bit key in `/data/.master.key`, `0600` permissions) and injected purely in memory when creating containers.
- **SQLite WAL Database (`/data/dockmanager.db`):**
  - Tables: `Users`, `Passkeys`, `SamlSeenAssertions` (for SAML replay deduplication), `Registries`, `DockerHosts`, `KubeClusters`, `StacksMeta`, `PortalApps`, `Webhooks`, `McpApiKeys`, and `AuditLogs`.

# 3. TRANSPORT ENCRYPTION, DYNAMIC SSL/PKI & COMPRESSION
- **Kestrel Dual-Port HTTPS + HSTS:**
  - Port `443` (Public/LAN App Portal + Reverse Proxy + Admin UI) and Port `8443` (Dedicated Admin & mTLS API port), plus Port `80` HTTP-to-HTTPS redirect.
  - Enforces TLS 1.3 / TLS 1.2 (`HttpProtocols.Http1AndHttp2AndHttp3`) and strict `HSTS` (`max-age=31536000; includeSubDomains`).
- **Built-In PKI (`CertificateManager`) + Optional ACME Let's Encrypt:**
  - Generates a persistent ECDSA P-384 Internal Root CA (`/data/certs/ca.pfx` and `ca.crt`).
  - Generates an ECDSA P-256 Server Certificate (`/data/certs/tls.pfx`) signed by the Internal CA with wildcard SANs (`localhost`, `*.dock.lan`, `127.0.0.1`, `10.10.1.1`, and user-configured hostnames/IPs).
  - Hot-swaps certificates in Kestrel with zero downtime via `HttpsConnectionAdapterOptions.ServerCertificateSelector` (`Interlocked.Exchange`).
  - Provides UI endpoints to regenerate SSL certs with custom SANs (`POST /api/system/ssl/regenerate`) and download `ca.crt` (`GET /api/system/ssl/download`).
- **Multi-Layer Compression:**
  - Build-time Vite pre-compression into `.zst` (Zstandard) and `.br` (Brotli) served via zero-CPU static file middleware.
  - Dynamic ASP.NET Core 11 `ResponseCompression` with `ZstandardCompressionProvider` (Priority 1), `BrotliCompressionProvider` (Priority 2), and `GzipCompressionProvider` (Priority 3).
  - Exclude `/api/auth/*` from HTTPS response compression to prevent BREACH side-channel attacks.
  - Use `.NET 11` `TypedResults.ServerSentEvents` for `/api/events/stream`, `/api/containers/{id}/logs`, and `/api/containers/{id}/stats`.
  - Use WebSockets with `WebSocketAcceptContext { DangerousEnableCompression = true }` (`permessage-deflate`) at `/ws/exec/{id}`.

# 4. ZERO-TRUST AUTHENTICATION & ENTERPRISE SSO (5 METHODS)
1. **Local Admin Password:** PBKDF2-HMAC-SHA512 (`600,000` iterations, 32-byte salt, `CryptographicOperations.FixedTimeEquals`).
2. **Passkeys / WebAuthn (`Fido2.AspNet`):** Passwordless biometric/hardware key registration and login (`/api/auth/passkey/*`).
3. **TOTP Authenticator (Google & Microsoft Authenticator):** Pure RFC 6238 `HMACSHA1` implementation (`6` digits, `30s` period, `otpauth://` URI + SVG QR generator) required as 2FA for password logins and step-up confirmation for destructive actions.
4. **Google & Microsoft OAuth2 / OIDC:**
   - Configurable Client ID / Secret in UI.
   - **Strict Registration Policy:** Unknown OAuth emails do NOT get automatic access; they are created with `Role = "Pending"` and trigger a Webhook notification so an Admin can approve them as `Viewer` (Portal-only) or `Admin` (Full Docker/K8s control).
5. **SAML 2.0 Enterprise SP & IdP (`ITfoxtec.Identity.Saml2`):**
   - **SP Mode (`/api/auth/saml2/metadata`, `/login`, `/acs`):** Supports Microsoft Entra ID, Google Workspace, Okta, and Authentik.
   - **Mandatory SAML Hardening:**
     - **Assertion Replay Deduplication:** Implement `ITokenReplayCache` backed by `ConcurrentDictionary` + SQLite `SamlSeenAssertions` (`INSERT OR IGNORE`) to reject any replayed `AssertionId` or `ResponseId` within its validity window.
     - **XXE / XML Bomb Guard:** Validate raw XML with `DtdProcessing.Prohibit`, `XmlResolver = null`, and `MaxCharactersInDocument = 524288` prior to unbinding.
     - **`InResponseTo` State Verification:** Verify the encrypted `__Host-eh_saml_req` state cookie.
   - **IdP Mode (`/api/saml/idp/metadata`, `/sso`):** Allows self-hosted apps on Port 443 (Nextcloud, Grafana, Proxmox) to use `eh.DockManager` as their SAML 2.0 Identity Provider.

# 5. PORT 443 AUTO-DISCOVERY REVERSE PROXY, LANDING PORTAL & SCALE-TO-ZERO
- **Auto-Discovery Engine:**
  - Listens to Docker `/events` and K8s Service watches.
  - Inspects container ports (`80`, `443`, `3000`, `5000`, `8000`, `8080`, `8096`, `8123`, `9000`, `32400`) and `eh.portal.*` labels (`eh.portal.enable`, `eh.portal.subdomain`, `eh.portal.name`, `eh.portal.icon`, `eh.portal.category`, `eh.portal.port`, `eh.portal.auth=public|sso|admin`, `eh.portal.autoscale=idle-15m`).
  - Dynamically updates `Yarp.ReverseProxy` routes (`IProxyConfigProvider`) in memory with zero restarts.
- **Port 443 Landing Index Page (`https://dock.lan/`):**
  - Displays categorized app cards with custom/auto icons, search bar (`/`), live status badges, and optional real-time CPU % / RAM MB sparklines on each card.
  - Fully editable in the UI: users can modify any auto-discovered app, hide internal containers, or add manual external links (e.g., UniFi Router `https://10.10.1.1`).
- **Serverless Scale-to-Zero & Wake-on-LAN (`ServerlessWakeMiddleware`):**
  - Background worker stops idle containers tagged with `eh.portal.autoscale=idle-15m`.
  - When a user requests `https://<stopped-app>.dock.lan` on Port 443, middleware starts the container via the Docker socket (or sends an IEEE 802.3 UDP Wake-on-LAN Magic Packet if the target Docker host is sleeping), polls TCP readiness for up to 8 seconds, and seamlessly forwards the request through YARP.

# 6. UNIFIED MULTI-HOST DOCKER & MULTI-CLUSTER KUBERNETES ENGINE
- **Pure Unix Socket + mTLS Docker Client (`DockerSocketClient`):**
  - Connects to local `/var/run/docker.sock` via `SocketsHttpHandler` + `UnixDomainSocketEndPoint`.
  - Connects to remote Docker hosts over mutually authenticated TLS 1.3 (`SocketsHttpHandler.SslOptions.ClientCertificates` validated against the Internal Root CA).
  - **Single-Binary Agent Mode (`--agent`):** When launched with `--agent`, runs Kestrel in strict mTLS client-cert validation mode on port `2376` and proxies requests directly to the remote host's `/var/run/docker.sock`.
  - Supports Public and Private Registries (Docker Hub, GHCR, GitLab, Quay, Custom) with encrypted credentials (`X-Registry-Auth`) and per-registry `IgnoreSslErrors`.
  - Supports container file browsing/editing/downloading via `/containers/{id}/archive` and `System.Formats.Tar` (works even on stopped or distroless containers).
  - Includes an Agentless CVE Scanner that reads `/var/lib/dpkg/status` or `/lib/apk/db/installed` via `/archive` tar stream and batch-queries `https://api.osv.dev/v1/querybatch`.
- **Multi-Cluster Kubernetes Client (`KubeClusterManager`):**
  - Imports `kubeconfig` YAML or raw token/certs into SQLite.
  - Lists/watches Pods, Deployments, Services, Ingresses/Gateway API `HTTPRoutes`, streams Pod logs, and executes Pod shell sessions.
- **3-Way Rosettastone Converter:**
  - `docker run ...` CLI command <-> `docker-compose.yml` <-> `Kubernetes Deployment + Service + Gateway API HTTPRoute` + `Export Running Container to Compose`.
- **Provenance & CLI Sync:**
  - Stamps every managed resource with `com.docker.compose.project`, `com.docker.compose.service`, `eh.dockmanager.actor` (`webui` | `mcp` | `updater`), `eh.dockmanager.actor_detail`, and `eh.dockmanager.config_hash`.
  - Cross-references `/events` with internal operation IDs so manual SSH `docker` CLI actions are automatically detected, synced, and badged as `CLI (External)`.
- **Per-Stack Auto-Updater with 25s Health Guard & Auto-Rollback:**
  - Configured per stack (`Enabled`, `IntervalMinutes`, `PruneOldImages`, `RollbackOnFailure`).
  - Pulls latest images using configured registry credentials, selectively recreates containers whose image SHA changed, monitors container state/healthcheck for 25 seconds, automatically rolls back to the previous image ID on crash/unhealthy state, and prunes unused images (`POST /images/prune?filters={"dangling":["false"]}`).

# 7. BUILT-IN MCP SERVER (`/mcp`) & MULTI-PROVIDER WEBHOOKS
- **MCP Server (`/mcp`):**
  - JSON-RPC 2.0 Streamable HTTP endpoint authenticated via scoped Bearer tokens (`ehdm_...`).
  - Tools: `list_stacks`, `deploy_stack`, `stack_action`, `convert_docker_run`, `convert_compose_to_k8s`, `list_containers`, `container_action`, `get_container_logs`, `exec_command`, `prune_unused_images`, `list_k8s_pods`.
  - Every MCP action stamps `eh.dockmanager.actor=mcp` so it looks and behaves identically to Web UI and CLI operations.
- **Webhook Notifier (`WebhookNotifier`):**
  - Native formatters for **Discord**, **Telegram**, **Slack**, **Ntfy**, **Gotify**, and **Generic HMAC-SHA256** (`X-Hub-Signature-256`).
  - Triggers on stack deploy, auto-update, auto-rollback, CVE alert, and pending OAuth user registration.

# 8. RESPONSIVE PC & MOBILE FRONTEND (`Vite + React 19 + TypeScript + Tailwind CSS`)
- **PC Experience:** Dark-mode workspace, Global Command Palette (`Ctrl+K`), Host/Cluster switcher header, split-pane YAML editor with live diff & 3-way converter, slide-over container inspector (live CPU/RAM SVG sparklines, ANSI log viewer with regex filter, Tar file browser), and multi-tab `xterm.js` drawer.
- **Mobile Experience:** Bottom navigation bar, swipeable container cards, and a **Mobile Terminal Touch Bar** above `xterm.js` (`ESC`, `TAB`, `CTRL+C`, `CTRL+D`, `CTRL+L`, `↑`, `↓`, `←`, `→`, `/`, `-`, `|`).
- **Views:**
  1. `PortalLandingView`: The Port 443 App Portal with live container CPU/RAM metrics, Scale-to-Zero status badge, and in-place tile editor.
  2. `DashboardView`: Host/Cluster health, disk usage, CVE summary, and live provenance event feed (`WebUI`, `MCP`, `Updater`, `CLI`).
  3. `StacksView`: Compose stack manager, YAML editor, URL importer, `docker run` converter, K8s converter, and per-stack Auto-Update config.
  4. `ContainersView`: Container table/cards, live stats, Logs SSE viewer, `xterm.js` (`exec -it`), and Tar File Browser.
  5. `KubernetesView`: Multi-cluster Pod/Deployment status, rollout restart, and manifest deployer.
  6. `ImagesAndRegistriesView`: Image list with `Unused` badge, 1-click Prune Unused Images, Pull image with progress, and Private Registry manager (with `Ignore SSL` toggle).
  7. `HostsAndWebhooksView`: mTLS Remote Docker Host enrollment generator, Wake-on-LAN button, Kubeconfig importer, and Webhook configuration/test UI.
  8. `SecurityAndSsoView`: Passkey manager, TOTP Authenticator QR setup, Google/Microsoft OIDC & SAML 2.0 SP/IdP settings, Pending User approval queue, MCP API keys, and live SSL SAN Certificate Regenerator.

# 9. REQUIRED FILE GENERATION ORDER
Generate all files completely and sequentially with `// File: <path>` headers:
1. `global.json` and `src/eh.DockManager.Server/eh.DockManager.Server.csproj`
2. `src/eh.DockManager.Server/Models/DomainModels.cs`
3. `src/eh.DockManager.Server/Data/SqliteWalStore.cs` (Full SQLite WAL schema & CRUD for Config, Users, Passkeys, SamlSeenAssertions, Hosts, Clusters, Stacks, PortalApps, Webhooks, AuditLogs)
4. `src/eh.DockManager.Server/Security/CryptoAndCertManager.cs` (AES-256-GCM Vault, ECDSA Root CA, Dynamic Server Cert SAN generator, mTLS cert issuer)
5. `src/eh.DockManager.Server/Security/AuthAndSsoEngine.cs` (PBKDF2, RFC 6238 TOTP + QR SVG, Fido2 Passkeys, Google/MS OIDC, Hardened SAML2 SP/IdP with `SamlAssertionReplayProtector`)
6. `src/eh.DockManager.Server/Docker/DockerSocketClient.cs` & `DockerStreamDemuxer.cs` (Unix socket + mTLS client, registries, ignore-SSL, stats, logs, exec, Tar file browser, OSV.dev CVE scanner)
7. `src/eh.DockManager.Server/Stacks/ComposeAndConverters.cs` (`YamlDotNet` stack parser/orchestrator, selective hash recreate, `docker run` converter, K8s converter, `StackAutoUpdateWorker` with 25s rollback)
8. `src/eh.DockManager.Server/Kubernetes/KubeClusterService.cs` (`KubernetesClient` multi-cluster manager)
9. `src/eh.DockManager.Server/Portal/YarpPortalProxy.cs` (Dynamic YARP `IProxyConfigProvider`, Port 443 app auto-discovery, `ServerlessWakeMiddleware` + Wake-on-LAN)
10. `src/eh.DockManager.Server/McpAndWebhooks/McpAndWebhookEngine.cs` (`/mcp` JSON-RPC 2.0 server and multi-provider `WebhookNotifier`)
11. `src/eh.DockManager.Server/Program.cs` (Complete Kestrel Port 443/8443 setup, `--agent` mode, Zstd/Brotli compression, Static `.zst`/`.br` middleware, YARP pipeline, SSE, WebSockets, and all REST API endpoints)
12. `src/eh.DockManager.Client/package.json`, `vite.config.ts`, `tailwind.config.js`, `index.html`, `src/main.tsx`, and `src/App.tsx` (Complete React 19 UI covering all 8 views + Mobile Touch Bar)
13. `Dockerfile` and `docker-compose.yml`