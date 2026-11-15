---
type: Reference
title: SAML 2.0 SSO (SP & IdP)
description: How DockManager acts as a SAML 2.0 Service Provider and Identity Provider, the dedicated signing certificate each side persists, why AuthnRequests need an explicit flag, and the trimming prerequisite for XML-DSig.
tags: [security, saml, sso, certificates, trimming]
status: stable
generated: { by: opencode/deepseek-v4.1-flash, at: 2026-11-14T16:58:52Z }
sources:
  - id: auth-engine
    resource: ../../src/eh.DockManager.Server/Security/AuthAndSsoEngine.cs
    title: SamlSpService and SamlIdpService
  - id: csproj
    resource: ../../src/eh.DockManager.Server/eh.DockManager.Server.csproj
    title: TrimmerRootDescriptor for SignedXml
  - id: illink-descriptor
    resource: ../../src/eh.DockManager.Server/ILLink.Descriptors.xml
    title: ILLink descriptor preserving the XML-DSig hash algorithms
  - id: program-cs
    resource: ../../src/eh.DockManager.Server/Program.cs
    title: SAML SP/IdP endpoints and config stores
---

# Overview

DockManager ships a SAML 2.0 **Service Provider** (`SamlSpService`) so an operator can connect
Microsoft Entra ID, Google Workspace, Okta or JumpCloud, and a SAML 2.0 **Identity Provider**
(`SamlIdpService`) so DockManager can itself federate users into downstream apps. Both live in
`Security/AuthAndSsoEngine.cs` and are built on `ITfoxtec.Identity.Saml2`.[^auth-engine]

# Endpoints

| Endpoint | Auth | Purpose |
|----------|------|---------|
| `GET /api/auth/saml2/metadata` | anonymous | The signed SP metadata document the IdP imports |
| `GET /api/auth/saml2/login` | anonymous | SP-initiated login: builds and redirects a signed AuthnRequest |
| `POST /api/auth/saml2/acs` | anonymous | Assertion Consumer Service: validates the IdP response |
| `GET /api/saml/idp/metadata` | anonymous | The IdP metadata document downstream apps import |
| `GET`/`PUT /api/security/saml/sp` | admin | Read/save `SamlSpConfig` (stored under the `saml.sp` config key) |
| `GET`/`PUT /api/security/saml/idp` | admin | Read/save `SamlIdpConfig` (stored under the `saml.idp` config key) |

# Signing certificates

Each side persists a **dedicated** signing certificate under `/data/certs`, deliberately
separate from the portal TLS certificate (`tls.pfx`) and from each other, so rotating TLS
never invalidates SAML trust and vice versa.[^auth-engine]

| Side | File | Password | Fallback chain |
|------|------|----------|----------------|
| SP | `sp-signing.pfx` | `eh-dockmanager-sp` | operator `SpSigningCertPfxBase64`/`SpSigningCertPassword` → existing `sp-signing.pfx` → generate |
| IdP | `idp-signing.pfx` | `eh-dockmanager-idp` | existing `idp-signing.pfx` → generate |

`SamlSpService.LoadOrCreateSpSigningCertificate` reuses the persisted SP PFX when it is more
than seven days from expiry, otherwise issues a fresh 2048-bit RSA self-signed certificate
(`CN=eh.DockManager SAML SP, O=eh.DockManager`, five-year validity) and writes it with
`0600` permissions via `CryptoVault.TryRestrict`. The certificate is created lazily, the
first time the SP is enabled.

# AuthnRequest signing

Two settings must both be present for a redirect-bound AuthnRequest to be signed:

* `SigningCertificate` — the dedicated SP PFX, and
* `SignAuthnRequest = true`.

`Saml2RedirectBinding` signs only when `Config.SignAuthnRequest` is set for a
`Saml2AuthnRequest`; with the certificate but without the flag the SP would advertise
`AuthnRequestsSigned="true"` in its metadata yet send an **unsigned** request. DockManager
sets the flag whenever it has loaded the SP certificate, so SP-initiated login is signed by
default with the dedicated PFX. The signed redirect carries `SigAlg` and `Signature` query
parameters, and the metadata document itself is signed with the same certificate.[^auth-engine]

# Trimming prerequisite

`SignedXml` resolves its digest and signature algorithms through `CryptoConfig` at run time,
which the trimmer cannot see. Under `PublishTrimmed` the managed shims are removed and any
signing call fails with:

```
System.Security.Cryptography.CryptographicException: Could not create hash algorithm object.
If the application has been trimmed, ensure the required algorithm implementations are preserved.
```

`ILLink.Descriptors.xml` (wired via `<TrimmerRootDescriptor>` in
`eh.DockManager.Server.csproj`) preserves exactly the types `CryptoConfig` resolves for the
XML-DSig digest URIs — `SHA1Managed`, `SHA256Managed`, `SHA384Managed`, `SHA512Managed`,
`RSAPKCS1SignatureFormatter` and `RSAPKCS1SignatureDeformatter` — rather than rooting the
whole `System.Security.Cryptography` assembly (see dotnet/runtime#97274).[^csproj][^illink-descriptor]

# Related

* [Stack constraints](/project/stack-constraints.md) — the audited `ITfoxtec.Identity.Saml2` and
  `System.Security.Cryptography.Xml` dependencies and the trimming rules.
* [UI & UX conventions](/project/ui-ux.md) — the SAML Service Provider section labels.
* [Server identity & hostname settings](/project/server-identity.md) — the portal hostname that
  drives the SP entity id and the ACS URL.

[^auth-engine]: `src/eh.DockManager.Server/Security/AuthAndSsoEngine.cs`
[^csproj]: `src/eh.DockManager.Server/eh.DockManager.Server.csproj`
[^illink-descriptor]: `src/eh.DockManager.Server/ILLink.Descriptors.xml`
[^program-cs]: `src/eh.DockManager.Server/Program.cs`
