---
type: Convention
title: Operation guards across UI, MCP and server
description: The rule that an operation's allow/block decision is defined once server-side and applied on every surface — client UI, MCP and REST — in the same change.
tags: [convention, guards, permissions, mcp, ui, api]
status: stable
generated: { by: opencode/deepseek-v4.1-flash, at: 2026-11-14T12:41:24Z }
sources:
  - id: program-cs
    resource: ../../src/eh.DockManager.Server/Program.cs
    title: Program.cs — the REST endpoints and the shared server-side predicates
  - id: mcp
    resource: ../../src/eh.DockManager.Server/McpAndWebhooks/McpAndWebhookEngine.cs
    title: McpAndWebhookEngine.cs — the MCP tools/call allow/deny path (JSON-RPC -32003)
  - id: client-src
    resource: ../../src/eh.DockManager.Client/src
    title: React client sources — where a blocked operation is hidden or disabled
  - id: agents-md
    resource: ../../AGENTS.md
    title: AGENTS.md — binding knowledge-workflow rules for every agent
---

# Rule

Every operation that more than one surface can trigger — the React client, the REST API, the MCP
tool surface — has a **single allow/block decision**. Define it once on the server (a shared helper
or the endpoint predicate) and make **every** surface that can trigger the operation reflect the same
decision **in the same change**. A guard that exists on only one surface is a bug: the other
surfaces bypass it.

Corollary: the client is presentation, not enforcement. Hiding a control is a hint; the REST
endpoint and MCP must still refuse the call.

# Surfaces

| Surface | Where the decision lives | How a block is expressed |
|---------|---------------------------|---------------------------|
| Server rule | a shared helper / endpoint predicate (e.g. `PortalSelf.IsSelf`) | the authority — it computes the answer |
| REST | the `Map*` endpoint in `Program.cs` | `403`/`409` (or `400`) with a clear `{ "error": … }` body |
| MCP | the `tools/call` handler in `McpAndWebhookEngine.cs` | JSON-RPC `-32003` with a message, and the `tools/list` description updated |
| Client (UI) | the React view | the control is hidden or disabled, with a short inline explanation |

Deliberate per-surface differences (for example MCP is intentionally more restrictive than the web UI
for user administration) are allowed — but they must be **chosen once and documented**, never an
accident of where the guard happened to be written.

# Worked example — deleting DockManager's own portal app

1. **Server:** `PortalSelf.IsSelf(app)` decides whether a portal app is DockManager itself (its own
   container). [^program-cs]
2. **REST:** `DELETE /api/portal/apps/{subdomain}` returns `409`
   `{"error":"DockManager's own portal app cannot be deleted."}` for that entry.
3. **MCP:** `delete_portal_app` returns `-32003` for that entry, and the tool description says the
   entry is protected. [^mcp]
4. **Client:** the Portal card and the editor omit the Delete button and the editor explains why.
   [^client-src]

All four landed in the same change — see [Portal visibility & ordering](/project/portal.md) and
[MCP server permissions & audit](/project/mcp.md).

# Checklist for a new guard

- [ ] The decision is computed **once** (a shared helper or a single predicate), not re-implemented
      per surface.
- [ ] The REST endpoint refuses with a specific status and message.
- [ ] The MCP tool refuses with `-32003`, and its `tools/list` description mentions the protection.
- [ ] The client hides or disables the control and explains it.
- [ ] The rationale is recorded in this bundle (a concept update plus a `log.md` entry).

# Anti-patterns

- ❌ A **UI-only** guard — the REST endpoint and every MCP key still bypass it.
- ❌ An **MCP-only** guard — the web UI still lets it through.
- ❌ Re-deriving the rule in each surface, so the surfaces drift apart.
- ❌ Blocking on one surface without recording the deliberate difference.

# Related

* [Portal visibility & ordering](/project/portal.md) — the self-delete guard (the worked example).
* [MCP server permissions & audit](/project/mcp.md) — scope enforcement and the `-32003` convention.
* [UI & UX conventions](/project/ui-ux.md) — how the client presents an omitted/disabled control.
* [Delivered implementation](/project/implementation-status.md) — where each guard was built.

[^program-cs]: Program.cs — the REST endpoints and the shared server-side predicates
[^mcp]: McpAndWebhookEngine.cs — the MCP tools/call allow/deny path (JSON-RPC -32003)
[^client-src]: React client sources — where a blocked operation is hidden or disabled
[^agents-md]: AGENTS.md — binding knowledge-workflow rules for every agent
