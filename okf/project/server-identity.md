---
type: Reference
title: Server identity & hostname settings
description: How the portal hostname is configured and how it drives the TLS certificate and portal app URLs, including the settings and user-update API endpoints.
tags: [settings, hostname, portal-domain, tls, certificate, users, api]
status: stable
generated: { by: opencode/deepseek-v4.1-flash, at: 2026-11-13T19:45:00Z }
sources:
  - id: program-cs
    resource: ../../src/eh.DockManager.Server/Program.cs
    title: Program.cs — API endpoints, settings and user updates
  - id: yarp
    resource: ../../src/eh.DockManager.Server/Portal/YarpPortalProxy.cs
    title: YarpPortalProxy.cs — portal domain and dynamic routes
  - id: cert
    resource: ../../src/eh.DockManager.Server/Security/CryptoAndCertManager.cs
    title: CryptoAndCertManager.cs — certificate SANs
  - id: prompt-md
    resource: ../../PROMPT.md
    title: PROMPT.md — build brief and requirements
    author: human:ehud
---

# Portal hostname

The portal domain is the base hostname for every published app: an app with subdomain
`nextcloud` is served at `https://nextcloud.<domain>`. Resolution order:[^yarp]

1. the value saved in the config store (`portal.domain`, key `database`),
2. otherwise `EHDM_PORTAL_DOMAIN` (default `dock.lan`),
3. otherwise the built-in default `dock.lan`.

`PortalProxyConfigurator.RebuildAsync` re-reads the persisted value and rebuilds the YARP routes,
so `PUT /api/system/settings` applies a new domain live (no restart). The old UI only exposed this
as an environment variable, which is why the Settings view now surfaces it explicitly.

# Certificate SANs

`CertificateManager` derives its default SANs from the portal domain
(`localhost`, `*.<domain>`, `<domain>`, `127.0.0.1`, `::1`) instead of the hard-coded
`dock.lan`.[^cert] Regenerating the leaf adds the domain, its wildcard, and the configured LAN IP,
and hot-swaps it into Kestrel. `GET /api/system/settings` reports the active certificate's subject,
thumbprint, expiry, DNS and IP SANs so the UI can warn when the domain is not covered.

`PUT /api/system/settings` calls `SetPortalDomain` before regenerating, so the CN and default
SANs follow the new name. At startup, after `RebuildAsync` loads the persisted domain, the server
sets it on the certificate manager and **re-issues the leaf if it does not cover that domain**;
a domain saved in the UI therefore survives restarts without a manual regeneration. The
regenerated certificate is hot-swapped into Kestrel, so only new TLS connections see it (an
existing browser connection can keep the previous certificate until it reconnects).

# Self app URL

The auto-discovered DockManager container is served at the root of the portal domain. The landing
endpoint detects that app (container ID starting with `Environment.MachineName`, matching container
name, or `EHDM_SELF_CONTAINER`) and returns `https://<portalDomain>` for it instead of
`https://<subdomain>.<portalDomain>`, so the portal "Open" button goes to the right address.[^program-cs]

# WebAuthn / passkeys

`PasskeyService` derives the FIDO2 `ServerDomain` (the WebAuthn RP ID) and the allowed `Origins`
from the **configured portal domain** — the persisted `portal.domain` value through
`PortalProxyConfigurator.PortalDomain`, falling back to `EHDM_PORTAL_DOMAIN` and then `dock.lan` —
plus any `EHDM_ORIGINS` override. It previously read only `EHDM_PORTAL_DOMAIN`, so a domain changed
in Settings would leave the RP ID stale and every ceremony would fail validation.[^cert]

WebAuthn additionally requires a **secure context with a valid certificate**: the W3C WebAuthn spec
exposes the API only to contexts "established without errors", and Chrome rejects it on "sites with
TLS certificate errors" (`SecurityError`/`NotAllowedError`). So a browser offers no passkeys while
the internal CA is untrusted, and an IP address can never work — the RP ID must be a domain. Devices
must install `ca.crt` (Settings → TLS certificate) and reach the portal by its hostname. This is
browser policy, not a DockManager bug.

# Public certificates (Let's Encrypt)

The internal CA is the default, but an administrator can enable Let's Encrypt instead; when an ACME
certificate is active it replaces the internal leaf and the startup re-issue step is skipped. See
[Let's Encrypt (ACME v2)](/project/lets-encrypt.md).

# API

| Method | Path | Purpose |
|--------|------|---------|
| `GET` | `/api/system/health` | Anonymous liveness probe; also returns `appVersion` and `bootId` for client reconnect/redeploy detection (see [Client/server version negotiation & asset freshness](/project/client-server-versioning.md)) |
| `GET` | `/api/system/settings` | Portal domain, its source, LAN IP, data directory, version, certificate SANs |
| `PUT` | `/api/system/settings` | Set `portalDomain` / `lanIp`; optionally `regenerateCertificate`; audits as `settings.update` |
| `PUT` | `/api/users/{id}` | Set `role` and/or `disabled`; refuses self-demotion and self-disable |
| `POST` | `/api/users` | Create a local user (username, password, optional email/role); PBKDF2-hashed, audited |
| `GET`/`PUT` | `/api/system/acme` | Read/save the Let's Encrypt config (domain, email, staging) |
| `POST` | `/api/system/acme/issue` | Issue or renew a Let's Encrypt certificate now and hot-swap it |

All require the `eh-admin` policy. API responses are sent with `Cache-Control: no-store`.[^program-cs]

# Related

* [UI & UX conventions](/project/ui-ux.md) — the Settings view that drives these endpoints.
* [MCP server permissions & audit](/project/mcp.md) — the `system` resource an MCP key can read or write.
* [Let's Encrypt (ACME v2)](/project/lets-encrypt.md) — the publicly trusted certificate alternative.
* [Stack constraints](/project/stack-constraints.md) — ports, storage and secrets.

[^program-cs]: Program.cs — API endpoints, settings and user updates
[^yarp]: YarpPortalProxy.cs — portal domain and dynamic routes
[^cert]: CryptoAndCertManager.cs — certificate SANs
[^prompt-md]: PROMPT.md — build brief and requirements
