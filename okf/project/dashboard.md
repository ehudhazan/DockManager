---
type: Reference
title: Dashboard metrics & aggregation
description: What the Dashboard shows, where each metric comes from, and how the server keeps the per-container stats fan-out cheap for the host and the browser.
tags: [dashboard, metrics, docker, stats, api, performance]
status: stable
generated: { by: opencode/deepseek-v4.1-flash, at: 2026-11-13T19:45:00Z }
sources:
  - id: program-cs
    resource: ../../src/eh.DockManager.Server/Program.cs
    title: Program.cs — DashboardAggregator and GET /api/system/dashboard
  - id: client
    resource: ../../src/eh.DockManager.Client/src/App.tsx
    title: App.tsx — DashboardView and the inline-SVG Sparkline
---

# Endpoint

`GET /api/system/dashboard?hostId=<id>&fresh=1` (admin). One call returns everything the view
needs:[^program-cs]

| Field | Contents |
|-------|----------|
| `overall` | containers total/running/paused/stopped, images, CPU %, CPU cores, memory used/total MB, network in/out bytes, published host-port count, and disk (images/layers/volumes/build cache/total). |
| `containers[]` | per container: name, image, state, CPU % (+ cores, throttled periods/time), memory used/limit/peak MB + allocation failures, PIDs, block read/written, network in/out + packets + errors + drops, published/exposed ports, writable-layer and root-fs size, created. |
| `hosts[]` | one panel per configured Docker host: online, container counts, images, CPU %, cores, memory used/total, disk used, or the last error. |
| `network` | the selected host's `NetworkSummary` from [Network metrics & traffic history](/project/network-metrics.md): 1h/24h in+out totals, packet/error/drop counters, TCP connection health, host interfaces and the sampled timeline. |

# Data sources

* Docker `/info` → `NCPU`, `MemTotal`, `Images`.
* Docker `/system/df` → disk, summed from `Images.Size`, `Containers.SizeRw`,
  `Volumes.UsageData.Size`, `BuildCache.Size`.
* Docker `/containers/json?all=1&size=1` → the container list including `SizeRw`/`SizeRootFs`,
  `Ports` and `Created`.
* Docker `/containers/{id}/stats?stream=false`, one call per **running** container, parsed by
  `NetworkMetricsTracker.ParseExtendedMetric` — the same routine the tracker and the per-container
  metrics endpoint use (see [Network metrics & traffic history](/project/network-metrics.md)).

# Efficiency

* The whole aggregate is cached server-side for 6 seconds, keyed by `hostId`; `fresh=1` bypasses
  the cache (the Refresh button). A poll or a second viewer does not multiply the Docker work.
* The stats calls fan out through a `SemaphoreSlim` capped at **8 concurrent** requests, so a
  busy or remote host is never flooded; a single container's failure is skipped, not fatal.
* Ports and sizes come from the single list call, not per-container requests; the port list is ordered by
  **container port** so the column never reshuffles between refreshes.
* The client polls on the interval chosen in the **Refresh** dropdown (Off / 1s / 5s / 10s / 30s / 1m / 5m / 10m / 30m; the default is persisted per view), and renders the charts
  with the inline-SVG `Sparkline` and `TrafficChart` components (no charting library, no canvas).

# Related

* [UI & UX conventions](/project/ui-ux.md) — where the tiles and panels sit in the client.
* [Network metrics & traffic history](/project/network-metrics.md) — the `network` block and its endpoints.
* [Delivered implementation](/project/implementation-status.md) — the behaviour exercised.

[^program-cs]: Program.cs — DashboardAggregator and GET /api/system/dashboard
[^client]: App.tsx — DashboardView and the inline-SVG Sparkline
