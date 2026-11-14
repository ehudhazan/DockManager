---
type: Reference
title: Stack constraints
description: Binding dependency, build, container, port, and storage constraints for eh.DockManager, including the audited NuGet allowlist and banned packages.
tags: [constraints, dependencies, build, security]
status: stable
generated: { by: opencode/deepseek-v4.1-flash, at: 2026-11-14T09:12:01Z }
sources:
  - id: prompt-md
    resource: ../../PROMPT.md
    title: PROMPT.md — build brief and requirements
    author: human:ehud
    last_modified: 2026-11-13T09:38:00Z
---

# Runtime and publish flags

Target `.NET 11` (`net11.0`), published with:

| Property | Value |
|----------|-------|
| `PublishTrimmed` | `true` |
| `TrimMode` | `partial` |
| `PublishReadyToRun` / `PublishReadyToRunComposite` | `true` |
| `TieredPGO` | `true` |
| `PublishSingleFile` | `true` |
| `InvariantGlobalization` | `true` |

`global.json` pins the SDK to `11.0.100` with `rollForward: latestMajor` and
`allowPrerelease: true`.[^prompt-md]

# Trimming prerequisites

Because `PublishTrimmed` is on, two settings in `eh.DockManager.Server.csproj` are load
bearing and must not be removed:

| Setting | Why |
|---------|-----|
| `<JsonSerializerIsReflectionEnabledByDefault>true</JsonSerializerIsReflectionEnabledByDefault>` | Trimming disables reflection-based `System.Text.Json` by default; the control plane serializes dynamic payloads and throws at startup without this |
| `<TrimmerRootAssembly Include="…" />` for `YamlDotNet`, `Fido2`, `Fido2.Models`, `Fido2.AspNet`, `ITfoxtec.Identity.Saml2`, `KubernetesClient`, `Microsoft.IdentityModel.Tokens.Saml`, `System.ServiceModel.Primitives`, `SQLitePCLRaw.batteries_v2`, `SQLitePCLRaw.core` | Keeps the reflection-driven libraries intact under `TrimMode=partial` |

Trim analysis still emits IL2026/IL2104 warnings, and NuGet audit still flags the
SDK-mandated `System.Security.Cryptography.Xml` `10.0.12`; both are warnings, not build
failures, and are accepted per the allowlist below.

# Container baselines

* Multi-stage build: Node 22 (UI), a .NET SDK stage (server publish), and a distroless
  runtime stage.
* Target base images per PROMPT.md are `mcr.microsoft.com/dotnet/sdk:11.0-noble` and
  `mcr.microsoft.com/dotnet/runtime-deps:11.0-noble-chiseled`.
* **As-built deviation:** MCR publishes no `11.0-noble` or `11.0-noble-chiseled` tags
  (Noble variants exist only through 10.x; .NET 11 ships on Azure Linux and Alpine). The
  `Dockerfile` therefore declares `ARG SDK_IMAGE` / `ARG RUNTIME_IMAGE` defaulting to the
  available distroless images
  (`mcr.microsoft.com/dotnet/sdk:11.0-azurelinux3.0`,
  `mcr.microsoft.com/dotnet/runtime-deps:11.0-azurelinux3.0-distroless`) and can be
  overridden at build time with `--build-arg`.
* Non-root user `$APP_UID` `64198` (`ENV APP_UID=64198`, `USER $APP_UID`).
* Hardened compose flags: `--read-only`, `--cap-drop=ALL`,
  `--security-opt=no-new-privileges:true`.[^prompt-md]

# Host bind mount and first-run permissions

The compose stack mounts `./data:/data`. Docker creates a missing bind-mount
directory as `root:root`, while the image runs as uid `64198`; the first
`docker compose up` therefore fails with `UnauthorizedAccessException` on
`/data/stacks`. Pre-create the host directory and give it to the container's uid:

```bash
mkdir -p data
sudo chown -R 64198:64198 data
```

The container must also join the host's docker group to read `/var/run/docker.sock`
(`660 root:docker`); set `DOCKER_GID` to the host's gid in `.env` (see `.env.example`).
See [Host preparation](/project/deployment.md) for the full runbook.

# First-run bootstrap admin

`EHDM_ADMIN_USER` and `EHDM_ADMIN_PASSWORD` are read from the environment. Empty
values are treated as unset (the `Program.cs` fix checks `is { Length: > 0 }`): the
admin password falls back to a random 16-byte value, logged once at startup as
`Initial admin credentials -> user: admin password: …`. The bootstrap only runs when
the users table is empty.

# NuGet allowlist (audited — nothing else is permitted)

| Package | Version | Role |
|---------|---------|------|
| `Yarp.ReverseProxy` | `2.3.0` | Port 443 reverse proxy, SNI routing, WebSockets, scale-to-zero request holding |
| `YamlDotNet` | `16.3.0` | YAML 1.2 parsing/emitting for `docker-compose.yml` (anchors, multiline blocks) |
| `Microsoft.Data.Sqlite` | `11.0.0-*` | ACID persistence, `PRAGMA journal_mode = WAL;` at `/data/dockmanager.db` |
| `Microsoft.AspNetCore.Authentication.OpenIdConnect` | `11.0.0-*` | Google & Microsoft OAuth2 / OIDC |
| `Fido2.AspNet` | `4.3.2` | WebAuthn / passkey passwordless login |
| `KubernetesClient` | `18.0.13` (or later) | Multi-cluster Kubernetes management; `>= 18.0.13` carries the CVE-2025-9708 mTLS CA validation fix |
| `ITfoxtec.Identity.Saml2` | `4.21.0` | SAML 2.0 SP & IdP |
| `System.Security.Cryptography.Xml` | `10.0.12` | SAML hardening (patched against CVE-2026-47304, CVE-2026-50648) |
| `Microsoft.IdentityModel.Tokens.Saml` | `8.23.0` | SAML token handling (patched against CVE-2026-50653) |

**Banned:** `Docker.DotNet`, `Sustainsys.Saml2`, `Newtonsoft.Json`, and any third-party TOTP
package (TOTP must be a pure RFC 6238 implementation).[^prompt-md]

# Code generation rules

* Every file must be complete and production-ready — no placeholders, no
  `// TODO`, no `// rest of implementation` truncation.
* Prefix every generated code block with `// File: relative/path/to/file`.
* `PROMPT.md` §9 fixes the file generation order and the intended module boundaries.[^prompt-md]

# Version & build number

The app version is date-based: `yyyy.M.d.<buildNumber>` (e.g. `2026.11.14.8`), baked into the assembly by the
`ResolveBuildVersion` MSBuild target in `eh.DockManager.Server.csproj` and surfaced by `GET /api/system/health`
and `/api/system/info`; see
[Client/server version negotiation & asset freshness](/project/client-server-versioning.md).

`<buildNumber>` resolves from, in order: the `BuildNumber` MSBuild property (`-p:BuildNumber`, fed by the
`BUILD_NUMBER` Docker build arg), the git commit count when a repository checkout is present, then a
build-time `HHmm` fallback. `docker-compose.yml` passes `BUILD_NUMBER: ${BUILD_NUMBER:-}`, and the root
`build.sh` exports it as `git rev-list --count HEAD`, so `./build.sh` stamps the commit count while a plain
`docker compose build` uses the fallback. Docker's build context excludes `.git` (`.dockerignore`).

# Ports, storage, and secrets

| Item | Value |
|------|-------|
| `443` | Public/LAN app portal, reverse proxy, and admin UI (TLS 1.2/1.3, HSTS) |
| `8443` | Admin and mTLS API port |
| `80` | HTTP-to-HTTPS redirect |
| `2376` | `--agent` mode: mTLS client-cert-validated Kestrel proxying to a remote Docker socket |
| Compose stacks | `/data/stacks/{stackName}/compose.yaml` and `.env` on disk (so manual `docker compose` stays compatible) |
| Database | `/data/dockmanager.db` (SQLite, WAL mode) |
| Vault key | `/data/.master.key` (AES-GCM 256-bit, `0600`); secrets referenced as `${VAULT:SECRET_NAME}` and injected in memory only |
| PKI | `/data/certs/ca.pfx`, `/data/certs/ca.crt`, `/data/certs/tls.pfx`; ECDSA P-384 root CA, ECDSA P-256 leaf with hot-swapped certificate selector |

Listen ports are read from the environment, defaulting to the values above:
`EHDM_HTTP_PORT` (80), `EHDM_PORTAL_PORT` (443), `EHDM_ADMIN_PORT` (8443); this lets the
server run unprivileged for local development and smoke tests. The binary also accepts
`--health-probe` (container healthcheck against `/api/system/health`, exit 0/1) and
`--agent` (mTLS agent mode).

# Notes

Transcribed from `PROMPT.md` §1, §2, §3, §6, and §9. No independent verification has been
recorded (unverified trust tier); `PROMPT.md` is authoritative, and any dependency change
must be reflected here in the same task.

[^prompt-md]: PROMPT.md — build brief and requirements
