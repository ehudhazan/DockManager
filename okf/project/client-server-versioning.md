---
type: Reference
title: Client/server version negotiation & asset freshness
description: How the client detects a disconnected server, notices a redeploy through the health endpoint's appVersion/bootId, reloads the shell, refreshes the service worker, and why sw.js and index.html are never cached.
tags: [client, server, versioning, pwa, cache, reconnect, health]
status: stable
generated: { by: opencode/deepseek-v4.1-flash, at: 2026-11-13T19:45:00Z }
sources:
  - id: program-cs
    resource: ../../src/eh.DockManager.Server/Program.cs
    title: Program.cs — /api/system/health, ComputeAppVersion and the static middleware cache policy
  - id: client
    resource: ../../src/eh.DockManager.Client/src/App.tsx
    title: App.tsx — connection/version watcher, ConnectionBanner and refreshServiceWorker
  - id: sw
    resource: ../../src/eh.DockManager.Client/public/sw.js
    title: sw.js — pass-through service worker
---

# The problem

A browser tab can outlive a server restart and a redeploy. Without a check it keeps showing stale data,
silently failing requests, and — because Vite emits hashed asset names — can end up with a shell that
references asset files the new server no longer serves (or that are cached forever under the old hash).

# Server signals

`GET /api/system/health` is anonymous (the container healthcheck uses it) and now returns two extra fields:[^program-cs]

| Field | Meaning |
|-------|---------|
| `appVersion` | `1.0.0+<hash>` where `<hash>` is the first 12 hex chars of the SHA-256 of the built `index.html`. Any deploy that ships new static assets changes it. |
| `bootId` | a random token generated at process start, so a **restart** (even with identical assets) is distinguishable from a continued session. |

`ComputeAppVersion` reads `wwwroot/index.html`; if the UI is not built it falls back to `1.0.0`.

# Client reaction

`App.tsx` polls `/api/system/health` every 15 seconds:

1. A failed poll sets `serverDown`, which renders a **"Server disconnected. Retrying automatically"** banner
   above the content (with the last known version and a Reload button). Requests keep retrying; the banner
   clears on the next successful poll.
2. The first successful poll records `appVersion`. If a later poll reports a **different** `appVersion`,
   the client asks the service worker to update (`navigator.serviceWorker.getRegistration().update()`)
   and then reloads the page, so the shell and the pre-compressed static resources always match the server.

`bootId` is exposed for future use (e.g. distinguishing a restart from a redeploy in a notice); the reload
decision is driven by `appVersion` because that is what determines whether the cached assets are stale.[^client]

# Static asset freshness

`PrecompressedStaticMiddleware` sets `Cache-Control` per file:[^program-cs]

| File | Cache-Control | Why |
|------|---------------|-----|
| `index.html` | `no-cache` | the entry document must never be served stale |
| `sw.js` | `no-cache` | the worker script itself must be re-fetched so an update can take effect |
| `manifest.webmanifest` | `no-cache` | install metadata changes should apply immediately |
| everything else (hashed assets, icons, fonts) | `public, max-age=604800, immutable` | Vite's filenames are content-addressed, so they are safe to cache |

The service worker stays a deliberate pass-through (it caches nothing) so a deploy never serves a stale
shell; see [UI & UX conventions](/project/ui-ux.md).[^sw]

# Related

* [UI & UX conventions](/project/ui-ux.md) — the banner lives in the app shell.
* [Server identity & hostname settings](/project/server-identity.md) — the other `/api/system/*` endpoints.
* [Delivered implementation](/project/implementation-status.md) — the pass that added this.

[^program-cs]: Program.cs — /api/system/health, ComputeAppVersion and the static middleware cache policy
[^client]: App.tsx — connection/version watcher, ConnectionBanner and refreshServiceWorker
[^sw]: sw.js — pass-through service worker
