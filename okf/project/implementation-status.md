---
type: Status
title: Delivered implementation
description: What was actually built and verified for eh.DockManager, the generated file set, runtime behaviours exercised, and the deviations from PROMPT.md discovered during the build.
tags: [status, implementation, build, deviations]
status: stable
generated: { by: opencode/deepseek-v4.1-flash, at: 2026-11-13T08:52:06Z }
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
| Compose & converters | `Stacks/ComposeAndConverters.cs` |
| Kubernetes | `Kubernetes/KubeClusterService.cs` |
| Portal & proxy | `Portal/YarpPortalProxy.cs` |
| MCP & webhooks | `McpAndWebhooks/McpAndWebhookEngine.cs` |
| Client | `src/eh.DockManager.Client/{index.html,package.json,vite.config.ts,tailwind.config.js,postcss.config.js,tsconfig.json,src/{main.tsx,App.tsx,api.ts,types.ts,index.css}}` |
| Container | `Dockerfile`, `docker-compose.yml`, `.dockerignore` |

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

Verification was performed by the building agent (deterministic build/run commands); no
independent `process:`/`human:` verifier has been recorded, so this concept remains in the
**unverified** trust tier.

# Deviations from PROMPT.md

| PROMPT.md rule | Reality | Resolution |
|----------------|---------|------------|
| Runtime base `mcr.microsoft.com/dotnet/runtime-deps:11.0-noble-chiseled` and SDK `…:11.0-noble` | MCR publishes no `11.0-noble` / `11.0-noble-chiseled` tags (Noble exists only through 10.x; .NET 11 ships on Azure Linux and Alpine) | `Dockerfile` exposes overridable `ARG SDK_IMAGE` / `ARG RUNTIME_IMAGE` defaulting to the available distroless images; see [Stack constraints](/project/stack-constraints.md) |
| Trimmed + reflection-based `System.Text.Json` | With `PublishTrimmed`, STJ reflection is disabled by default and the app throws at startup | Set `JsonSerializerIsReflectionEnabledByDefault=true` and added `TrimmerRootAssembly` entries; see [Stack constraints](/project/stack-constraints.md) |
| Fixed ports 80/443/8443 | Binding privileged ports blocks local, non-root runs and smoke tests | Ports are now read from `EHDM_HTTP_PORT` / `EHDM_PORTAL_PORT` / `EHDM_ADMIN_PORT` with the PROMPT.md values as defaults |

# Notes

`PROMPT.md` remains normative. Where the implemented system intentionally departs from a
literal reading, the departure is listed above and explained; a follow-up should either
revert it (once the noble images ship) or amend the requirement.[^prompt-md]

[^prompt-md]: PROMPT.md — build brief and requirements
