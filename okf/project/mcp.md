---
type: Reference
title: MCP server permissions & audit
description: How the built-in MCP server authenticates scoped bearer keys, the resource:action[:target] scope grammar it enforces, the tool surface it exposes, and how every MCP action is attributed in the audit log.
tags: [mcp, security, permissions, audit, api, scopes]
status: stable
generated: { by: opencode/deepseek-v4.1-flash, at: 2026-11-14T12:41:24Z }
sources:
  - id: mcp-src
    resource: ../../src/eh.DockManager.Server/McpAndWebhooks/McpAndWebhookEngine.cs
    title: McpAndWebhookEngine.cs — McpApiKeyService, McpScopes and McpServer
  - id: program-cs
    resource: ../../src/eh.DockManager.Server/Program.cs
    title: Program.cs — /mcp endpoint and /api/mcp/keys management
  - id: prompt-md
    resource: ../../PROMPT.md
    title: PROMPT.md — build brief and requirements
    author: human:ehud
---

# Purpose

The MCP server (`POST /mcp`, JSON-RPC 2.0 Streamable HTTP) exposes the control plane to agents.
This concept records the **permission model** and the **audit attribution** that make a leaked
key safe and every action traceable.[^mcp-src]

# Authentication

* Bearer token `ehdm_…` in `Authorization`. Tokens are stored only as a SHA-256 hash; the
  plaintext is returned **once** when the key is created.
* `McpApiKeyService.ValidateAsync` rejects a token when the key is missing **or `Enabled` is
  false** — a disabled/revoked key never authenticates.
* Keys are managed through `GET`/`POST /api/mcp/keys` and `DELETE /api/mcp/keys/{id}`
  (admin only). The Security view drives these.

# Scope grammar

`Scopes` is a single string. Grants are separated by `;`; the actions inside a grant are joined
by `+`; an optional third part restricts the grant to named targets (globs allowed):

| Form | Meaning |
|------|---------|
| `*:*` | Every action on every resource. |
| `read` / `write` / `*` | Legacy single token ⇒ `*:read` / all read+write / everything. |
| `containers:read` | One action on one resource. |
| `stacks:list+read+write` | Several actions on one resource. |
| `containers:read; images:delete` | Several grants. |
| `stacks:write:nextcloud` | A grant limited to the target `nextcloud` (`*` globs allowed). |

**Action hierarchy:** `write` implies `read` implies `list`; `delete` is independent (a key with
`write` but not `delete` cannot remove anything). Resources: `stack`, `container`, `exec`,
`image`, `registry`, `host`, `cluster`, `webhook`, `portal`, `user`, `system`, `audit`,
`convert`.

# Enforcement

* `tools/list` returns only the tools the key's scopes allow, so an agent sees exactly what it
  may do. `initialize` and `tools/list` also return `_meta.key` (the key name) and the scopes.
* `tools/call` resolves the tool's `resource`+`action` (and, where applicable, the target
  argument) and, if not permitted, returns JSON-RPC error `-32003` naming the missing grant.
  `container_action` with `action=remove` requires the independent `delete` grant.
* A target-restricted grant also filters list results to the permitted names via
  `McpScopes.AllowsTarget`.

# Key lifecycle

| Method | Path | Purpose |
|--------|------|---------|
| `GET` | `/api/mcp/keys` | List keys (name, prefix, scopes, enabled, created/last-used) |
| `POST` | `/api/mcp/keys` | Create a key; the plaintext token is returned **once** |
| `PUT` | `/api/mcp/keys/{id}` | Change **name**, `enabled` and/or `scopes` without revealing the secret |
| `POST` | `/api/mcp/keys/{id}/rotate` | Issue a new secret for the same key; the old one stops working immediately |
| `DELETE` | `/api/mcp/keys/{id}` | Revoke a key |

All are admin-only and audited as `mcp.key.create` / `update` / `rotate` / `revoke`, so the
provenance feed shows who changed which key. Because the key **name** is what appears in the audit log
(`ActorDetail`), `PUT` can rename it so the log stays meaningful.

# Tool surface

**60 tools** cover the control plane, grouped by resource:

| Resource | Tools |
|----------|-------|
| `stack` | list, get, **`validate_stack`** (read-only schema check), save, delete, deploy, action, auto-update, `save_stack_secret`, convert (`docker run` ↔ Compose ↔ K8s) |
| `container` | list, get, action (start/stop/restart/pause/unpause/remove), `create_container`, logs, `list_container_files`, `read_container_file` |
| `exec` | `exec_command` |
| `image` | list, `get_image`, pull, remove, prune |
| `registry` | list, save, delete |
| `host` | list, test, wake, `save_host`, `delete_host` |
| `cluster` | list, `save_cluster`, `delete_cluster`, namespaces, pods, deployments, services, `list_k8s_httproutes`, `get_k8s_pod_logs`, restart, scale, apply manifest |
| `webhook` | list, save, delete, test |
| `portal` | list, save, delete, refresh |
| `user` | `list_users`, `get_user`, `create_user`, `update_user`, `delete_user` — **all non-admin users only** |
| `system` | `get_settings`, `update_settings` |
| `audit` | `list_audit` |

## Portal: DockManager's own entry protected

`delete_portal_app` refuses DockManager's own catalogue entry — the container that serves the
management UI — with JSON-RPC `-32003`. The same shared rule (`Portal/PortalSelf.cs`) guards
`DELETE /api/portal/apps/{subdomain}` with `409`. See
[Portal visibility & ordering](/project/portal.md) and
[Operation guards across UI, MCP and server](/project/operation-guards.md).

## Users: all non-admin users, Admin protected

MCP can list, inspect, create, update (role/approve/disable/email/password) and delete **any user whose
role is not `Admin`**, but an Admin account is untouchable through MCP:

* `list_users` marks Admin rows with `"protected": true`.
* `get_user`, `update_user` and `delete_user` return JSON-RPC `-32003` for an Admin account.
* `create_user` and `update_user` refuse to grant the `Admin` role, so a leaked key can never mint or
  promote an administrator.

`get_user`/`update_user`/`delete_user` accept either `username` (case-insensitive) or `id`.

# Audit attribution

Every MCP action is written to `AuditLogs` with `Actor = Mcp` and **`ActorDetail = <key name>`**
(stack deploys pass the key name through to `ComposeStackService.DeployAsync`). The Dashboard
provenance feed renders `actorDetail`, so a WebUI action shows the **username** and an MCP action
shows the **key name**.

`GET /api/events/stream` emits exactly the same JSON shape as `GET /api/system/audit`
(`id`, `actor`, `actorDetail`, `action`, `target`, `detail`, `success`, `timestamp`); before the
fix the live feed emitted a different envelope (`type`/`message`) and live rows lost their action
and key.[^program-cs]

# Related

* [Server identity & hostname settings](/project/server-identity.md) — the settings the MCP
  `system` resource reads and writes.
* [UI & UX conventions](/project/ui-ux.md) — the Security view's key list, enable toggle, rotate and scope chips.
* [UI & UX conventions](/project/ui-ux.md) — the Security view's MCP key editor.
* [Delivered implementation](/project/implementation-status.md) — the endpoints and tested
  behaviours.

[^mcp-src]: McpAndWebhookEngine.cs — McpApiKeyService, McpScopes and McpServer
[^program-cs]: Program.cs — /mcp endpoint and /api/mcp/keys management
[^prompt-md]: PROMPT.md — build brief and requirements
