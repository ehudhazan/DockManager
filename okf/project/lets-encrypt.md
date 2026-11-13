---
type: Reference
title: Let's Encrypt (ACME v2)
description: How DockManager obtains and renews a publicly trusted TLS certificate from Let's Encrypt using a hand-built ACME v2 client with an http-01 challenge, and what the host and domain must provide.
tags: [tls, acme, lets-encrypt, certificate, security, http-01]
status: stable
generated: { by: opencode/deepseek-v4.1-flash, at: 2026-11-13T19:40:00Z }
sources:
  - id: acme-src
    resource: ../../src/eh.DockManager.Server/Security/AcmeClient.cs
    title: AcmeClient.cs — ACME v2 client and AcmeRenewalWorker
  - id: cert-src
    resource: ../../src/eh.DockManager.Server/Security/CryptoAndCertManager.cs
    title: CryptoAndCertManager.cs — InstallExternalServerCertificate and the ACME state
  - id: program-cs
    resource: ../../src/eh.DockManager.Server/Program.cs
    title: Program.cs — challenge endpoint, /api/system/acme and the client
---

# Why a hand-built client

`PROMPT.md` bans every NuGet package outside its audited allowlist, and no allowlisted package
does ACME. So `Security/AcmeClient.cs` implements RFC 8555 directly with `ECDsa` (ES256 JWS),
`CertificateRequest` (PKCS#10 CSR) and `HttpClient`. It is a supporting file added alongside
`QrCodeGenerator.cs`; see [Delivered implementation](/project/implementation-status.md).[^acme-src]

# Host requirements

* The domain must **resolve publicly** (a real public suffix — the CA rejects `.invalid`/internal names).
* **Inbound TCP/80** must reach this server: the `http-01` token is fetched over plain HTTP.
* **Inbound TCP/443** must reach it too, so the issued certificate is actually served.

# Configuration & endpoints

Persisted as the `acme` config object (`AcmeConfig`): `Enabled`, `Domain` (comma-separated; the
first is the CN and the rest are SANs), `Email`, `UseStaging`, plus `LastError`/`LastIssuedAt`
for status.

| Method | Path | Purpose |
|--------|------|---------|
| `GET`  | `/api/system/acme` | Config, effective directory URL, whether an ACME cert is active, and the current leaf's SANs/expiry |
| `PUT`  | `/api/system/acme` | Save the config (admin); audits nothing, cheap to call |
| `POST` | `/api/system/acme/issue` | Run an issuance/renewal now and hot-swap the result; audits as `acme.issue` |

All require the `eh-admin` policy. The Settings view drives them; see
[UI & UX conventions](/project/ui-ux.md).[^program-cs]

# Flow

1. `GET` the directory (production, or the staging directory when `UseStaging`).
2. `HEAD` `newNonce`; an ECDSA P-256 account key is persisted at `/data/certs/acme-account.pem`.
3. `POST newAccount` with `termsOfServiceAgreed` and the contact email; the account URL becomes the JWS `kid`.
4. `POST newOrder` with one `dns` identifier per domain; the **order URL comes from the `Location`
   header**, not the body.
5. For each authorization: `POST`-as-GET it, pick the `http-01` challenge, register
   `token + "." + base64url(SHA256(jwk-thumbprint))` in the in-memory `AcmeChallengeStore`, `POST` the
   challenge, then poll the authorization until `valid`/`invalid`.
6. Generate an ephemeral P-256 key + CSR (SANs = the domains), `POST finalize`, poll the order to `valid`.
7. `POST`-as-GET the certificate URL; the PEM chain's first block is combined with the private key and
   installed.

# Where the certificate lands

`CertificateManager.InstallExternalServerCertificate` writes the leaf to `/data/certs/tls.pfx`, writes
the chain to `/data/certs/acme-fullchain.pem`, rewrites the SAN manifest so a restart keeps the
external certificate, writes `/data/certs/acme-state.json` (`active: true`), and hot-swaps it into
Kestrel. When `acme-state.json` is active, the startup routine **does not** overwrite the leaf with an
internally signed certificate for the portal domain — the renewal worker owns its lifecycle.[^cert-src]

# Renewal

`AcmeRenewalWorker` runs every 6 hours; when ACME is enabled and (no ACME certificate is active yet,
or the leaf is within **30 days** of expiry) it re-issues and hot-swaps. Let's Encrypt leaf
certificates live 90 days, so this renews around day 60.

# Gotchas

* `StringContent` appends `; charset=utf-8`, and the CA rejects anything other than exactly
  `application/jose+json` — the client sets `Content-Type` explicitly. (Caught by the staging test.)
* ES256 signatures are raw `r||s`, produced with
  `SignData(..., DSASignatureFormat.IeeeP1363FixedFieldConcatenation)` — not DER.
* The `badNonce` error is retried once with a fresh nonce.
* The challenge responder runs **before** `UseHttpsRedirection`, so the CA gets the token over http.

# Tested

Against the **staging** directory the flow reached account creation, order creation, the registered
http-01 challenge and the authorization poll, then returned the expected
"Authorization for <domain> ended 'invalid'" because the test domain does not serve the token; the
challenge path over plain HTTP returned `404` (not the `307` redirect a normal path returns).

# Why a publicly trusted certificate matters

An untrusted certificate does not only show a warning: Chrome reports
`Page.getInstallabilityErrors → not-from-secure-origin` and **refuses to install the PWA**, and
WebAuthn/passkeys are blocked as well. Trusting `ca.crt` on the device fixes both, but a publicly
trusted certificate (this feature) removes the per-device install entirely.

# Related

* [Server identity & hostname settings](/project/server-identity.md) — the internal CA this replaces.
* [UI & UX conventions](/project/ui-ux.md) — the Settings section that drives it.
* [Delivered implementation](/project/implementation-status.md) — the file it adds.

[^acme-src]: AcmeClient.cs — ACME v2 client and AcmeRenewalWorker
[^cert-src]: CryptoAndCertManager.cs — InstallExternalServerCertificate and the ACME state
[^program-cs]: Program.cs — challenge endpoint, /api/system/acme and the client
