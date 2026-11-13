---
type: Guide
title: Chrome DevTools MCP over an SSH reverse tunnel
description: How OpenCode on the SSH host drives a Chrome window on the operator's Windows laptop — a reverse SSH tunnel exposes the laptop's CDP port as remote 127.0.0.1:9222 and chrome-devtools-mcp attaches to it with --browser-url.
tags: [opencode, mcp, chrome, ssh, tunnel, remote-development, environment]
status: draft
generated: { by: opencode/deepseek-v4.1-flash, at: 2026-11-13T09:56:54Z }
stale_after: 2027-05-13T00:00:00Z
sources:
  - id: chrome-devtools-mcp
    resource: https://github.com/ChromeDevTools/chrome-devtools-mcp/blob/main/docs/advanced-usage.md
    title: chrome-devtools-mcp — connecting to a running Chrome instance (--browserUrl / --wsEndpoint)
  - id: opencode-config-schema
    resource: https://opencode.ai/config.json
    title: OpenCode configuration JSON schema — Mcp.LocalConfig (`command`, `environment`)
  - id: chrome-blog
    resource: https://developer.chrome.com/blog/chrome-devtools-mcp-debug-your-browser-session
    title: Let your coding agent debug your browser session with Chrome DevTools MCP
  - id: chromium-switches
    resource: https://peter.sh/experiments/chromium-command-line-switches/
    title: List of Chromium command-line switches (peter.sh)
  - id: operator-briefing
    resource: OpenCode agent session — operator briefing and observed tool output
    title: Operator briefing — OpenCode over SSH, Chrome on the Windows laptop
    author: human:ehud
---

# Topology

| Node | Runs |
|------|------|
| **Laptop** (Windows 11) | Chrome (the visible browser), the VS Code Remote SSH client, and the terminal SSH client |
| **Remote host** (`eh-xubuntu`, Ubuntu 24.04) | `opencode`, the OpenCode-hosted MCP servers, and — after this setup — `chrome-devtools-mcp` |

Both SSH connections originate on the laptop, so the laptop can reach the remote but not the
reverse.[^operator-briefing] The browser must render on the laptop, so the browser is the
Windows-side process and the agent must reach it from the remote.

# How it works

The MCP server is registered as `type: local` in [`opencode.json`](../../opencode.json), so
OpenCode **spawns it on the remote host** and talks to it over stdio.[^opencode-config-schema]
The server then reaches the browser over the Chrome DevTools Protocol (CDP) at
`http://127.0.0.1:9222`.[^chrome-devtools-mcp]

`127.0.0.1:9222` on the remote is a *reverse* SSH forward that the laptop's SSH client
installs. Because the laptop initiates the connection, no inbound access to the laptop is
needed — the tunnel rides the existing outbound session.

```
Windows laptop                          SSH host (opencode)
┌─────────────────────────┐            ┌──────────────────────────────┐
│ Chrome  ──CDP── 127.0.0.1:9222       │  opencode ──stdio── chrome-  │
│    ▲                     │            │             devtools-mcp     │
│    └── ssh client ───────┼───────────►│  127.0.0.1:9222 ◄────────────┘│
└─────────────────────────┘  -R 9222:127.0.0.1:9222                    │
                                        └──────────────────────────────┘
```

The attach is one-way: `--browser-url` connects to an existing browser and never launches
one, so the window the operator already sees on the laptop is the one the agent drives.[^chrome-devtools-mcp]

# Setup

## 1. Laptop — start Chrome with a debugging port

Use PowerShell (note the leading `&` and the quoted options — without the quotes PowerShell
fails with `Unexpected token 'remote-debugging-port=9222' in expression or statement`):

```powershell
& "C:\Program Files\Google\Chrome\Application\chrome.exe" `
  "--remote-debugging-port=9222" `
  "--remote-allow-origins=*" `
  "--ignore-certificate-errors" `
  "--user-data-dir=$env:LOCALAPPDATA\chrome-debug-mcp" `
  "--no-first-run" `
  "--disable-first-run-ui" `
  "--no-default-browser-check" `
  "--disable-default-browser-promo" `
  "--disable-signin-promo-on-avatar-pill-for-testing" `
  "--suppress-message-center-popups"
```

In `cmd.exe` the identical set works without the `&` and quotes, using `^` for line breaks.

| Flag | Why |
|------|-----|
| `--remote-debugging-port=9222` | opens the CDP endpoint; Chrome binds it to `127.0.0.1` only |
| `--user-data-dir=…` | **required** when Chrome is already running — otherwise the flag is ignored and the new window joins the existing (non-debuggable) instance |
| `--remote-allow-origins=*` | lets CDP WebSocket clients with an `Origin` header connect; harmless otherwise |
| `--ignore-certificate-errors` | the portal serves its own PKI certificate, so the debug profile skips the interstitial |
| `--no-first-run`, `--disable-first-run-ui` | skips the first-run experience, including the "Sign in to Chrome" step |
| `--no-default-browser-check`, `--disable-default-browser-promo` | suppresses the "set Chrome as your default" check and its recurring promo |
| `--disable-signin-promo-on-avatar-pill-for-testing` | suppresses the sign-in promo bubble on the profile avatar pill |
| `--suppress-message-center-popups` | hides message-center toasts |

Every flag name is a current entry in the Chromium switch list.[^chromium-switches] Because
the debug profile directory persists, the one-time prompts appear at most once regardless;
the flags make even the first launch quiet. `--disable-component-update` is an optional
extra that stops Chrome's component-fetch background chatter.

**Make it a command.** A PowerShell alias cannot carry arguments, so the launcher is a
function in `$PROFILE` (typically
`C:\Users\<you>\Documents\PowerShell\Microsoft.PowerShell_profile.ps1`):

```powershell
function chrome-mcp {
  & "C:\Program Files\Google\Chrome\Application\chrome.exe" `
    "--remote-debugging-port=9222" `
    "--remote-allow-origins=*" `
    "--ignore-certificate-errors" `
    "--user-data-dir=$env:LOCALAPPDATA\chrome-debug-mcp" `
    "--no-first-run" `
    "--disable-first-run-ui" `
    "--no-default-browser-check" `
    "--disable-default-browser-promo" `
    "--disable-signin-promo-on-avatar-pill-for-testing" `
    "--suppress-message-center-popups"
}
```

Append it once, then reload the profile:

```powershell
$line = @'
<the function block above>
'@
New-Item -ItemType Directory -Force -Path (Split-Path $PROFILE) | Out-Null
Add-Content -Path $PROFILE -Value $line
. $PROFILE
```

`chrome-mcp` then starts the debug browser in any new PowerShell window. If the profile is
refused, allow it with `Set-ExecutionPolicy -Scope CurrentUser RemoteSigned`.

Confirm on the laptop: <http://127.0.0.1:9222/json/version> returns a JSON blob with a
`webSocketDebuggerUrl`.

## 2. Laptop — forward the port to the remote

One-off, on the command line:

```powershell
ssh -R 9222:127.0.0.1:9222 ehud@<remote-host>
```

**Make it automatic** — put the forward in the laptop's SSH config (`C:\Users\<you>\.ssh\config`
on Windows) so every connection carries it:

```
Host eh-xubuntu
    HostName eh-xubuntu
    User ehud
    RemoteForward 9222 127.0.0.1:9222
    ExitOnForwardFailure no
```

Then a plain `ssh eh-xubuntu` — and VS Code Remote SSH, which reads the same file — brings up
the tunnel with no extra flags. The forward is installed as soon as SSH connects; it does not
need Chrome to be running yet, and it does not need any inbound access to the laptop.

Notes:

* `-R` listens on the remote's loopback only (the default), so the port is never exposed to
  the remote's network. Do not add `GatewayPorts yes` or `-R 0.0.0.0:…`.
* If a second connection (another terminal, or the editor) also asks for `9222`, it logs
  `remote port forwarding failed … address already in use` and then continues; the first
  connection keeps the tunnel. `ExitOnForwardFailure no` guarantees SSH does not drop the
  session over it.
* A PowerShell function in `$PROFILE` is an alternative to the config file:
  `function oc { ssh -R 9222:127.0.0.1:9222 ehud@eh-xubuntu }`.

## 3. Remote — register the MCP server

Already committed in this repository's [`opencode.json`](../../opencode.json):

```json
"chrome-devtools": {
  "type": "local",
  "command": [
    "npx", "-y", "chrome-devtools-mcp@latest",
    "--browser-url", "http://127.0.0.1:9222"
  ],
  "environment": { "CHROME_DEVTOOLS_MCP_NO_USAGE_STATISTICS": "1" }
}
```

`command` and `environment` (not `env`) are the OpenCode local-MCP keys.[^opencode-config-schema]

## 4. Verify from the remote

```bash
curl -s http://127.0.0.1:9222/json/version
```

A JSON response means the tunnel is up and the agent can attach.

# Evidence

Recorded while writing this concept: after the server was added, OpenCode spawned it and
registered its 33 `chrome-devtools` tools. With no tunnel running, a tool call fails — and
crucially it fails instead of launching a browser on the remote:

```
Could not connect to Chrome. Check if Chrome is running.
Cause: Failed to fetch browser webSocket URL from http://127.0.0.1:9222/json/version: fetch failed
Cause: connect ECONNREFUSED 127.0.0.1:9222
```

That is the expected signature until steps 1 and 2 are done.

# Driving Chrome when the MCP tools are absent

The MCP server can be running and connected while an **agent session still cannot call its
tools**. Observed in a Code Mode session: the agent's tool catalog contained only the
`opencode` namespace, and `tools.browser`, `tools["chrome-devtools"]`, and even a made-up
name all resolved to a non-callable stub (`Tool 'browser' is not callable.`) — so no
`chrome-devtools` tool could be invoked, even though `chrome-devtools-mcp` was running and
`curl http://127.0.0.1:9222/json/version` returned `Chrome/157`.

In that case drive the browser directly over CDP through the same tunnel — no MCP involved:

```bash
# does the tunnel reach a browser?
curl -s http://127.0.0.1:9222/json/version

# open a tab (PUT is required on modern Chrome); the new tab is focused
curl -s -X PUT "http://127.0.0.1:9222/json/new?https://10.10.1.94/%23portal"

# list / bring an existing tab to front
curl -s http://127.0.0.1:9222/json/list
curl -s "http://127.0.0.1:9222/json/activate/<targetId>"
```

`/json/new` reports the created target with its `id` and `webSocketDebuggerUrl`; a page that
loaded correctly shows its real title there (the DockManager portal reports `eh.DockManager`).
Anything beyond opening/activating tabs — screenshots, DOM reads, clicks — needs a CDP
WebSocket client (`Page.captureScreenshot`, `Runtime.evaluate`, …), which is what the MCP
server normally wraps.

# Security

CDP on port 9222 grants full control of the browser — cookies, logged-in sessions, page
content. Keep both ends on loopback:

* do **not** pass `--remote-debugging-address=0.0.0.0` (Chrome would accept connections from
  the LAN, and Chrome's Host-header check allows addresses and IPs);
* do **not** use `ssh -R 0.0.0.0:9222:…` (or `GatewayPorts yes`), which would bind the
  forward on the remote's public interfaces.

# Alternatives

| Option | Notes |
|--------|-------|
| `@playwright/mcp` with `--cdp-endpoint http://127.0.0.1:9222` | same tunnel; Playwright's MCP server over the same CDP endpoint |
| `--wsEndpoint ws://127.0.0.1:9222/devtools/browser/<id>` | pins the exact target instead of discovering it via `--browserUrl` |
| Chrome 144+ `--autoConnect` (`chrome://inspect/#remote-debugging`) | Chrome picks an ephemeral port (reported as 61545 on Chrome 146), which a fixed `-R` forward cannot target — poor fit for tunnelling[^chrome-blog] |
| `ssh -L` (local forward) | equivalent wiring, but requires reaching the laptop from the remote, which is the direction that is usually unavailable |

# Trust

**Partly exercised; not independently verified.** The tunnel and the CDP fallback were
exercised from this session: `http://127.0.0.1:9222/json/version` returned `Chrome/157`, and
`PUT /json/new` opened—and `/json/activate` focused—the DockManager portal. The MCP-server
path itself was not exercised through an agent tool call, because the `chrome-devtools` tools
were not exposed to the session (see above). The concept is therefore `status: draft` and
carries no `verified` entry. Machine-confirmed here: the config is accepted by OpenCode (the
server spawns and its tools register), the no-tunnel failure is `ECONNREFUSED` on
`127.0.0.1:9222`, and the direct-CDP route works. Re-check after a Chrome,
`chrome-devtools-mcp`, or OpenCode release, or if the SSH forward stops working.

[^chrome-devtools-mcp]: chrome-devtools-mcp — connecting to a running Chrome instance (--browserUrl / --wsEndpoint)
[^opencode-config-schema]: OpenCode configuration JSON schema — Mcp.LocalConfig (`command`, `environment`)
[^chrome-blog]: Let your coding agent debug your browser session with Chrome DevTools MCP
[^chromium-switches]: List of Chromium command-line switches (peter.sh)
[^operator-briefing]: Operator briefing — OpenCode over SSH, Chrome on the Windows laptop
