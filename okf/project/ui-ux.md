---
type: Guide
title: UI & UX conventions
description: The design system, navigation model, and mobile behaviour of the eh.DockManager React client, including the icon set, info tooltips, clickable surfaces, and role-based views.
tags: [ui, ux, frontend, react, design-system, mobile]
status: stable
generated: { by: opencode/deepseek-v4.1-flash, at: 2026-11-13T21:05:00Z }
sources:
  - id: client-src
    resource: ../../src/eh.DockManager.Client/src
    title: React client sources (App.tsx, ui.tsx, icons.tsx, types.ts, api.ts, index.css)
  - id: prompt-md
    resource: ../../PROMPT.md
    title: PROMPT.md — build brief and requirements
    author: human:ehud
---

# Purpose

The client was rewritten for a gold-standard, self-explanatory experience on both desktop and
mobile. This concept records the conventions so later changes stay consistent.[^client-src]

# Structure

| File | Role |
|------|------|
| `src/App.tsx` | App shell (sidebar, top bar, bottom nav, command palette) plus every view and the terminal drawer |
| `src/yaml.ts` | Compose schema tree, completion engine, smart Enter/Tab indentation and the local enum lint behind `YamlEditor` |
| `src/ui.tsx` | Shared atoms: `InfoTip`, `StatusPill`, `RoleBadge`, `PageHeader`, `Section`, `StatTile`, `EmptyState`, `Sparkline`, `TrafficChart`, `MiniMetric`, `Tabs`, `Toggle`, `Field`, `ConfirmButton`, `Modal`, `Callout`, `Skeleton`, `Expand`, `MetricRow`, `RefreshControl`, `counterRate`, `usePersistentState`, `classNames`, `formatBytes`, `formatNumber`, `formatPercent`, `formatRate`, `formatDuration`, `intervalLabel`, `roleBlurb`, and the `Notify`/`OpenTerminal` prop types |
| `src/icons.tsx` | Dependency-free 24x24 line-icon set (`Icon` + `IconName`), replacing the original emoji |
| `src/index.css` | Tailwind layers plus focus rings, background gradient, thin mobile scrollbars, and min touch heights |
| `src/api.ts`, `src/types.ts` | Typed REST client; `ViewKey` includes the new `users` and `settings` views |
| `src/main.tsx` | React entry point; registers the service worker for installability |
| `public/` | `manifest.webmanifest`, `sw.js`, `icon.svg`, `icon-maskable.svg` — copied verbatim into `wwwroot` by Vite |

# Conventions

* **No emoji in chrome.** All navigation and actions use `Icon`; a colour + icon conveys state.
* **Every page explains itself.** Each view starts with a `PageHeader` carrying a one-line
  subtitle and an `InfoTip`; complex sections use `Section` with `info`.
* **Info affordance (`i`).** `InfoTip` renders a small `i` button: it opens on hover (desktop)
  and on tap (touch), closes on outside-pointer or `Escape`, and is never nested inside another
  button (for tiles, the tip is an absolutely-positioned sibling of the clickable element).
* **Clickable surfaces.** Dashboard `StatTile`s navigate to the area they describe
  (Containers, Images, Proxy routes, Docker hosts, K8s clusters, Uptime/Settings); portal cards
  keep their Open / Terminal / Edit actions always visible instead of hover-only.
* **Role clarity.** `RoleBadge` + `roleBlurb` describe Admin/Viewer/Pending; the top-bar user
  menu and the sidebar footer show the current role. Non-admins see only Portal + Dashboard and
  are redirected away from admin-only views.
* **Human-readable values.** Use `formatBytes` for image sizes, disk totals, prune results and
  file listings, and `formatRelative` for created/last-seen timestamps; never show raw bytes.
* **Exec UX.** The terminal drawer always shows why a session ended; Inspect offers a shell
  picker (`/bin/sh`, `/bin/bash`, `/bin/ash`, `/bin/busybox sh`) and the server replaces
  distroless "no shell" OCI errors with guidance to try another interpreter or use Files.
* **Images clarity.** Show repository:tag or an explicit `untagged` badge, the short image ID,
  relative creation age, human size, and a used-by count derived from containers (Docker's
  `Containers` field is `-1`, so it is not trusted). Rows and cards are selectable (with a
  header select-all and a "Select unused" shortcut) and a bulk bar can delete the selection.
* **Run image.** Every image has a **Run** action that opens a dialog for a container name,
  command/args, `KEY=VALUE` environment lines, `host:container[/proto]` port mappings and a
  restart policy, then creates and starts it (`POST /api/containers`) and jumps to Containers.
* **Hover feedback uses outlines.** Cards use an always-present transparent outline that changes
  colour on hover, so nothing shifts or moves.
* **Explicit spaces in JSX prose.** JSX drops a line break that sits between prose and a tag, so
  `…<strong>Signed-in users</strong>` on one line followed by `requires …` on the next renders as
  “Signed-in usersrequires …”. Any prose that wraps around an inline element must carry an explicit
  `{' '}` at the break. (This shipped once; the tooltip text is now spot-checked in a browser.)
* **Dashboard density.** The Dashboard adds CPU, memory, disk, network-traffic and published-port
  tiles plus a sortable per-container table (CPU / memory / network / ports / size) and one panel
  per Docker host, all from a single `GET /api/system/dashboard` call; see
  [Dashboard metrics & aggregation](/project/dashboard.md).
* **Portal visibility & order.** Hidden apps show dimmed with a `hidden` badge, a **Show hidden**
  toggle reveals them, and each card has hide/unhide and move-earlier/move-later controls; see
  [Portal visibility & ordering](/project/portal.md).
* **Containers at a glance.** The container table and cards show published/exposed ports, relative
  creation age and writable-layer size (`size=1` on the Docker list) alongside state and actions.
* **Kubernetes namespaces.** The namespace field is a picker populated from
  `GET /api/clusters/{id}/namespaces` (with an **All namespaces** option), falling back to free text
  only when the list cannot be loaded.
* **Command palette, not help.** The top-bar button is labelled **Commands**; the palette header
  states it is a keyboard way to move around and act (`Ctrl/⌘+K`), not the help system.
* **MCP key editor.** Security → MCP API keys opens a dialog with per-service **list / read / write /
  delete** checkboxes (a searchable service list, an *All services* row with per-action toggles, presets and
  `Unrestricted (*:*)`), shows the resulting scope string as copyable text, and after creation
  reveals the token in a copy dialog that stays until the admin clicks OK. Each key in the list shows
  its scopes as chips plus an **Enabled** toggle, an **Edit** dialog (rename, re-scope, enable/disable —
  no secret shown), **Rotate** (new one-time secret) and **Revoke**, and a callout explains how to point an
  MCP client at `/mcp`, that users are manageable over MCP **except Admin accounts**, and that the key
  **name** is what appears in the audit log; see [MCP server permissions & audit](/project/mcp.md).
* **Refresh control everywhere it matters.** A compact **Refresh** dropdown (Off / 1s / 5s / 10s / 30s /
  1m / 5m / 10m / 30m) replaces the old Live checkbox on the Dashboard, Containers, Stacks and Images
  views; the chosen interval is persisted per view (`ehdm.refresh.*`) and the defaults are editable in
  Settings → **Display settings**. The legacy 5 s/12 s timers are gone.
* **Expandable operational info.** `Expand` is a disclosure: an always-visible summary line plus details
  hidden until expanded. It is used everywhere operational depth exists — portal cards ("More info"),
  the Dashboard per-container detail row, the container inspector, the network counters/TCP/interfaces,
  and every stack service — so panels stay calm but nothing is lost. `MetricRow` renders the dense
  label/value rows inside them.
* **Human-readable network labels.** Traffic is shown as In/Out rates and 1h/24h totals with named
  counters ("Receive drops", "Connection resets (established)", "Segments retransmitted"), never as bare
  rx/tx numbers; see [Network metrics & traffic history](/project/network-metrics.md).
* **Expand panels carry charts, not just rows.** Anything that runs gets a chart inside its `Expand`
  disclosure, so the panels stay calm until opened but are dense when they are: the Portal card's
  **More info** shows live Network in/out and Disk read/write sparklines (from the 2 s stats stream) plus
  process/thread, CPU-core, memory-peak and block-IO counters; the container **Inspect → Stats** tab adds a
  **Disk throughput** chart beside the network chart; the Dashboard **Network traffic** panel adds host
  load and process counters. Two shared atoms keep them consistent — `MiniMetric` (label + value +
  `Sparkline`) and `counterRate` (cumulative counter → per-second rate, 0 on a counter reset). The same
  pattern is the default for any new operational surface.
* **Log timestamps.** The container-inspector log view has a **Show timestamps** checkbox (persisted as
  `ehdm.logs.timestamps`, default on) that maps to Docker's `timestamps` flag; stack/pod logs honour the
  same preference.
* **Server disconnection & version skew.** A poll of `GET /api/system/health` raises a **"Server
  disconnected"** banner when the server is unreachable and reloads the shell (after refreshing the
  service worker) when the returned `appVersion` changes; see
  [Client/server version negotiation & asset freshness](/project/client-server-versioning.md).
* **Navigation always starts at the top.** Changing views scrolls the window and `main` back to the top,
  so a deep page such as Images never opens mid-scroll after a navigation.
* **Mobile ergonomics.** The terminal drawer keeps a reachable **Hide** and a **Close all** button; the
  TOTP setup QR lives in a scrollable modal (no sideways drift, and a real close button); host rows use
  icon-only actions with wrapping endpoints; the agent-enrollment dialog replaces `window.prompt` on
  mobile; and username/email fields set `autoCapitalize="none"`/`inputMode` so mobile keyboards do not
  capitalise them.
* **Stack YAML editor.** The Stacks editor is a compose-aware `YamlEditor`: schema/value validation
  (server `POST /api/stacks/validate` plus a local enum lint) with a clickable issue list and line-accurate
  findings, keyboard-driven key/value completions (`Ctrl/⌘+Space`, `↑`/`↓`, `Enter`/`Tab`, `Esc`) that show
  the resolved path, and smart `Enter` indentation that continues lists with `- ` and opens mappings at the
  next level; `Tab`/`Shift+Tab` indent selections. Details and the full check list:
  [Stack YAML editor](/project/stack-yaml-editor.md).
* **SAML labels explain themselves.** The Service Provider section carries a callout and per-field info
  tips describing the SP entity id and the IdP metadata URL.
* **Passkey guidance.** The Passkeys section carries a callout explaining that browsers refuse
  WebAuthn on a certificate-error origin and pointing at the `ca.crt` download.
* **Settings explains itself.** The TLS section lists concise per-platform steps for trusting
  `ca.crt` (Windows / macOS / Linux / iOS / Android) and warns to browse by hostname, not IP; a
  **Let's Encrypt** section configures automatic HTTPS — see
  [Let's Encrypt (ACME v2)](/project/lets-encrypt.md).
* **Installable app (PWA).** `public/manifest.webmanifest` (name, `standalone`, **raster PNG icons**
  at 192/512 plus maskable variants and an SVG extra), a pass-through `public/sw.js` registered from
  `main.tsx`, a PNG `apple-touch-icon`, and the `apple-*` meta tags make the client installable. A
  top-bar **Install** button appears once the browser fires `beforeinstallprompt`, and the **More**
  sheet always offers *Install app (PWA)*, falling back to per-browser instructions (Chrome/Edge
  “Install app”; iOS Safari Share → “Add to Home Screen”). The worker deliberately caches nothing, so
  a deploy never serves a stale shell.
  *Chrome’s install criteria require a **raster** icon of at least 192×192 with an explicit
  `sizes` value; an SVG-only manifest (or `sizes: "any"`) is not installable, and iOS ignores SVG
  `apple-touch-icon`s.*
  *Installation **also** requires a certificate the device trusts: with the untrusted internal CA,
  Chrome reports `Page.getInstallabilityErrors → not-from-secure-origin` and never fires
  `beforeinstallprompt`, even though the manifest, icons and service worker are all correct. Same
  root cause as WebAuthn — see [Let's Encrypt (ACME v2)](/project/lets-encrypt.md). The Install
  button is therefore always shown (unless already running standalone) and, when the prompt cannot
  fire, explains the browser-menu route **and** the certificate requirement.*

# Navigation

Desktop uses a grouped sidebar (`Overview`, `Manage`, `Configure`) built from a single `NAV`
array that also drives the command palette. Routing is hash-based with a `hashchange` listener so
back/forward work. `Ctrl/⌘+K` opens the command palette; its top-bar trigger is labelled
**Commands** and carries a tooltip plus a one-line explanation in the palette, because "command"
was being mistaken for a help button.

# Mobile

A fixed bottom bar shows four primary destinations plus **More**, which opens an **All views** bottom
sheet containing only the full view grid — it no longer repeats the top-bar command button or the
user-menu sign-out. The bar respects `env(safe-area-inset-bottom)` and keeps 56px tap targets.
Tables switch to stacked cards below the `md` breakpoint, and the terminal drawer gains the
`MobileTouchBar`.

Horizontal drift is prevented with `overflow-x: clip` on `body` (not `hidden`, which would create a
scroll container and break the sticky header), so a stray absolutely-positioned element such as an
info tooltip can no longer produce a blank side-scroll on a phone.

# Related

* [Project overview](/project/overview.md) — where the client sits in the system.
* [Server identity & hostname settings](/project/server-identity.md) — the Settings view's hostname and TLS controls.
* [Dashboard metrics & aggregation](/project/dashboard.md) — the numbers behind the Dashboard tiles and panels.
* [Portal visibility & ordering](/project/portal.md) — hiding, revealing and sorting portal apps.
* [MCP server permissions & audit](/project/mcp.md) — the Security view's MCP key editor.
* [Network metrics & traffic history](/project/network-metrics.md) — the charts behind the traffic panels.
* [Client/server version negotiation & asset freshness](/project/client-server-versioning.md) — the disconnect banner and redeploy reload.
* [Stack YAML editor](/project/stack-yaml-editor.md) — validation, completions and smart indentation in the Stacks view.
* [Let's Encrypt (ACME v2)](/project/lets-encrypt.md) — the Settings section that turns on automatic HTTPS.
* [Delivered implementation](/project/implementation-status.md) — the exact file set.

[^client-src]: React client sources (App.tsx, ui.tsx, icons.tsx, types.ts, api.ts, index.css)
[^prompt-md]: PROMPT.md — build brief and requirements
