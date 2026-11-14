---
type: Reference
title: Remote Docker hosts
description: How DockManager connects to a remote Docker engine — the Unix-socket, mTLS-daemon and single-binary-agent host kinds, the endpoint scheme rules, the certificate validation chain on each side, and the plain-TCP fallback when no certificate is usable.
tags: [docker, hosts, mtls, agent, tls, runbook]
status: stable
generated: { by: opencode/deepseek-v4.1-flash, at: 2026-11-14T06:12:24Z }
sources:
  - id: docker-client
    resource: ../../src/eh.DockManager.Server/Docker/DockerSocketClient.cs
    title: DockerSocketClient — remote host transport and certificate validation
  - id: program
    resource: ../../src/eh.DockManager.Server/Program.cs
    title: Program.cs — /api/hosts API, agent enrollment, and --agent mode
  - id: cert-manager
    resource: ../../src/eh.DockManager.Server/Security/CryptoAndCertManager.cs
    title: CryptoAndCertManager — internal PKI and mTLS certificate issuance
  - id: client-ui
    resource: ../../src/eh.DockManager.Client/src/App.tsx
    title: App.tsx — Hosts & webhooks view and host form
---

# Why

A Docker host is any machine running the Docker engine that DockManager controls. The local
socket is seeded on first run; remote engines are added from **Hosts & webhooks**. Because
the control plane runs in a hostile-network control position, remote transports are TLS by
default, with a plain-TCP fallback for a lab where no certificate is usable.[^docker-client]

# Schema

`DockerHost` (`Models/DomainModels.cs`) is the persisted record; `DockerHostKind` selects the
transport.[^docker-client]

| Kind | UI label | Transport |
|------|----------|-----------|
| `UnixSocket` | (local) | `unix:///var/run/docker.sock` via `UnixDomainSocketEndPoint` |
| `HttpsMutualTls` | "Remote over mTLS" | TCP + TLS 1.2/1.3, optional client certificate |
| `AgentMtls` | "DockManager agent" | TCP + TLS to the single-binary `--agent` front door |

Every non-`UnixSocket` host runs through the same TLS handler; the kind is a label, not a
separate code path. `ClientCertPem`/`ClientKeyPem` are `[JsonIgnore]` (never returned by
`GET /api/hosts`); `CaCertPem` is returned.

# Endpoint scheme rule

`DockerSocketClient.NormalizeEndpoint` is applied on save (`POST /api/hosts`, MCP
`save_host`) and again in the client constructor, so both the stored value and the live
connection are usable.

| Input | Result |
|-------|--------|
| `tcp://host:2376` | `https://host:2376` (the Docker CLI convention means TLS) |
| `host:2376` (no scheme) | `https://host:2376` |
| `https://…`, `http://…`, `unix://…` | unchanged |

`tcp://` **must not** reach `HttpClient`: it accepts `tcp://` as a `BaseAddress` but every
send throws `NotSupportedException: The 'tcp' scheme is not supported.` Plain unauthenticated
TCP is `http://host:2375`.[^docker-client]

# Certificate validation

For a TLS host the client accepts the server certificate when **any** of these holds; the
corresponding rule also decides whether a self-signed daemon can be pinned.[^docker-client]

1. the OS/container trust store trusts it (`SslPolicyErrors.None`);
2. it chains to DockManager's **internal root CA** (`CertificateManager.ValidateClientCertificate`);
3. its thumbprint **equals** the PEM in the **"CA certificate"** field — i.e. that field is a
   *server-certificate pin*, not a CA bundle.

The exec / attach hijack path (`OpenRawConnectionAsync`) uses a **narrower** callback: only
rule 2. Terminal and file-browser sessions over a host that is only trusted through the pin
(rule 3) therefore fail.

# Both sides — mTLS daemon (`HttpsMutualTls`)

* **Remote:** run the daemon with `--tlsverify --tlscacert --tlscert --tlskey -H tcp://0.0.0.0:2376`;
  open `2376` to the DockManager host and keep `2375` closed. The client certificate must carry
  the **clientAuth** EKU.
* **DockManager:** add a host with an `https://host:2376` endpoint and paste the client
  certificate, client key, and the server certificate (pin) or the CA the container trusts.

# Both sides — agent mode (`AgentMtls`)

The single-binary agent is the lowest-friction option: it exposes a remote socket over mTLS
without reconfiguring the remote `dockerd`.[^program][^cert-manager]

1. **Generate agent** (`POST /api/hosts/agent/enroll`) with a name and the remote host address.
   It issues a **client** certificate (DockManager → agent) and a **server** certificate (SAN =
   the remote host address + `localhost`), both signed by the internal root CA, and returns a
   compose file plus an `https://host:2376` endpoint.
2. **Remote:** save `server.crt`, `server.key` and `ca.crt` from the compose comments into
   `./agent-certs` (mounted at `/certs`), then run the compose service. The command is
   `--agent --port 2376 --tls-cert /certs/server.crt --tls-key /certs/server.key --tls-ca /certs/ca.crt`.
3. **DockManager:** click **Add as host** — the endpoint and client certificates are pre-filled.
   Then **Test**.

Both directions validate against the internal CA, so no host-level trust store change is
needed on either side. The agent fails closed: without `--tls-cert`/`--tls-key` it falls back
to an ephemeral **self-signed** certificate (which DockManager cannot validate), and
`ClientCertificateMode` is `RequireCertificate`.

# Fallback — plain TCP (no certificates)

When no certificate is usable, run the daemon with `-H tcp://0.0.0.0:2375` and add an
`http://host:2375` endpoint with no certificates. This is **unauthenticated, root-equivalent
access** — bind it to a trusted interface, firewall it, or tunnel it.[^docker-client]

# Editing a host

The UI never receives the certificate PEMs, so a blank certificate field on edit means "keep
the stored value": `POST /api/hosts` merges the existing `ClientCertPem`/`ClientKeyPem`/
`CaCertPem` when the incoming value is empty. The MCP `save_host` tool only overwrites
certificates that are supplied.

# Pitfalls

* In .NET 11 the cert-only overload `X509Certificate2.CreateFromPemFile(path)` throws
  `CryptographicException` ("key contents do not contain a PEM…"); load a certificate-only PEM
  with `X509CertificateLoader.LoadCertificateFromFile` or `X509Certificate2.CreateFromPem(text)`.
  The file-based cert+key overload works.
* `DockerHost.CaCertPem` is displayed as "CA certificate" but is matched by **thumbprint**
  (rule 3), not by chain building.

# Notes

Fixes applied 2026-11-14: enrollment emitted a `tcp://` endpoint that always threw; the agent
presented a per-start self-signed server certificate DockManager could not validate and never
loaded the CA for client-certificate validation. The code above is authoritative; no
independent `verified` entry is recorded (self-run checks are not independent verification).

[^docker-client]: DockerSocketClient — remote host transport and certificate validation
[^program]: Program.cs — /api/hosts API, agent enrollment, and --agent mode
[^cert-manager]: CryptoAndCertManager — internal PKI and mTLS certificate issuance
[^client-ui]: App.tsx — Hosts & webhooks view and host form
