---
type: Reference
title: OpenCode default model
description: The repository's opencode.json pins openrouter/deepseek/deepseek-v4.1-flash as the default model through the top-level model key.
tags: [opencode, config, model, environment]
status: stable
generated: { by: opencode/deepseek-v4.1-flash, at: 2026-11-13T19:07:08Z }
sources:
  - id: opencode-config
    resource: https://opencode.ai/docs/config
    title: OpenCode configuration guide — Model
  - id: opencode-json
    resource: ../../opencode.json
    title: Repository OpenCode configuration
  - id: operator-briefing
    resource: OpenCode agent session — operator instruction
    title: Operator instruction — set the current model as the default
    author: human:ehud
---

# Schema

The default model is the top-level `model` key in
[`opencode.json`](../../opencode.json), written in `provider/model` form.[^opencode-config]

```json
{
  "$schema": "https://opencode.ai/config.json",
  "model": "openrouter/deepseek/deepseek-v4.1-flash",
  "mcp": { "servers": { "…": {} } }
}
```

| Field | Value |
|-------|-------|
| Provider | `openrouter` |
| Model ID | `deepseek/deepseek-v4.1-flash` |
| `model` value | `openrouter/deepseek/deepseek-v4.1-flash` |

# Notes

* This is a **project** configuration: it applies to sessions started in this repository and
  overrides the global `~/.config/opencode/opencode.json` default, while any discovered
  `.opencode/opencode.json` or system config would override it.[^opencode-config]
* The value was chosen by the operator to make the session's current model the default, so
  new sessions in this repository start on `deepseek-v4.1-flash` instead of a globally
  configured model.[^operator-briefing]
* The root `model` key does **not** retain a `#variant`; a variant (e.g. `#high`) can only be
  selected per agent or command reference.[^opencode-config]
* The model was confirmed present and `active` in the OpenCode model catalog before the
  change was made. Re-check it with the `opencode` models tool if a session reports the
  model as unavailable.
* `opencode.json` also registers the `DebugMCP` remote server and the `chrome-devtools`
  local server; see [Chrome DevTools MCP over an SSH reverse tunnel](/environment/chrome-mcp-over-ssh.md).

[^opencode-config]: OpenCode configuration guide — Model
[^opencode-json]: Repository OpenCode configuration
[^operator-briefing]: Operator instruction — set the current model as the default
