---
type: Reference
title: Network metrics & traffic history
description: How DockManager samples container and host network traffic into a 24-hour ring buffer, the 1h/24h totals, drops, errors and TCP connection-reset counters it exposes, and the endpoints that serve them.
tags: [network, metrics, docker, api, charts, monitoring]
status: stable
generated: { by: opencode/deepseek-v4.1-flash, at: 2026-11-13T19:45:00Z }
sources:
  - id: tracker
    resource: ../../src/eh.DockManager.Server/Docker/NetworkMetricsTracker.cs
    title: NetworkMetricsTracker.cs — background sampler, ring buffer and stats parsing
  - id: program-cs
    resource: ../../src/eh.DockManager.Server/Program.cs
    title: Program.cs — /api/system/network, /api/containers/{id}/metrics and the dashboard aggregate
  - id: client
    resource: ../../src/eh.DockManager.Client/src/App.tsx
    title: App.tsx — NetworkSection, TrafficChart and the container inspector
---

# Purpose

Operators need to see **traffic in and out**, how much moved in the **last hour and last 24 hours**, and
whether the network is **dropping packets, erroring or resetting connections** — not just a cumulative
byte counter. `NetworkMetricsTracker` samples Docker stats on a timer and keeps a rolling history that the
API and the UI chart.[^tracker]

# Sampling

| Property | Value |
|----------|-------|
| Cadence | every 30 seconds (first sample ~5 s after boot) |
| Retention | 24 hours, per host and per container |
| Ring buffer | at most one sample per 2 s is kept per key; older samples are trimmed past 24 h |
| Fan-out | at most 6 concurrent Docker `/containers/{id}/stats` calls per round |
| Scope | one history per enabled Docker host (`default` when no host row exists) plus one per container id |

The tracker is registered as a singleton **and** as a hosted service
(`AddSingleton<NetworkMetricsTracker>()` + `AddHostedService(sp => sp.GetRequiredService<NetworkMetricsTracker>())`),
so the dashboard can read the same instance the worker writes.[^program-cs]

# Parsed metrics

`NetworkMetricsTracker.ParseExtendedMetric` is the single parser for the full Docker `/stats` payload and is
shared by the dashboard aggregate, the per-container metrics endpoint and the tracker itself:

| Group | Fields |
|-------|--------|
| CPU | `cpuPercent`, `cpuCores`, `cpuThrottledPeriods`, `cpuThrottledMs` |
| Memory | `memoryMb` (cgroup usage minus inactive file/cache), `memoryLimitMb`, `memoryPeakMb`, `memoryFailCount` |
| Processes | `pidsCurrent`, `pidsLimit` |
| Disk | `blockReadBytes`, `blockWriteBytes` (blkio `io_service_bytes_recursive`) |
| Network | `networkRxBytes`/`networkTxBytes`, `networkRxPackets`/`networkTxPackets`, `networkRxErrors`/`networkTxErrors`, `networkRxDropped`/`networkTxDropped` |
| Interfaces | one `NetworkInterfaceMetric` per Docker network with the same fields |

# Host counters

Where the container can read the host's `/proc`, the summary also reports:

* `hostTcp` — from `/proc/net/snmp` (Tcp lines): `currEstab`, `activeOpens`, `passiveOpens`,
  `attemptFails`, `estabResets` (connection resets), `retransSegs` (retransmits), `inErrs`, `outRsts`.
* `hostInterfaces` — from `/proc/net/dev`, one entry per non-loopback interface.

Both are **best-effort**: the distroless container ships no shell but `/proc` is usually mounted, and a
restricted sandbox simply yields `null`/empty and the UI explains the absence rather than failing.

# API

| Method | Path | Returns |
|--------|------|---------|
| `GET` | `/api/system/network?hostId=` | `NetworkSummaryResponse`: 1h/24h in+out totals, packet/error/drop counters, `hostTcp`, `hostInterfaces`, and the sampled `history` |
| `GET` | `/api/containers/{id}/metrics` | one-shot `ExtendedContainerMetric` (`stats`) plus the container's `ContainerNetworkHistory` (`network`) |
| `GET` | `/api/containers/{id}/network` | the container's network timeline only |

The dashboard's `GET /api/system/dashboard` payload includes the same `network` summary for the selected
host, so a single poll feeds the tiles, the container table and the traffic chart. See
[Dashboard metrics & aggregation](/project/dashboard.md).[^program-cs]

# Client

* `NetworkSection` (Dashboard) renders four totals (in/out × 1h/24h), a dual-series `TrafficChart` of the
  per-second rates, and expandable panels for counters, TCP connection health, interfaces and raw samples.
* The container **Inspect → Stats** tab polls `/api/containers/{id}/metrics` every 5 s and charts the
  container's own traffic next to the CPU/memory sparklines, with throttle/PID/block-IO detail.
* Labels are human-readable ("Received", "Sent", "Connection resets (established)") and details stay
  hidden until expanded; see [UI & UX conventions](/project/ui-ux.md).[^client]

# Limitations

* History is **in memory**: a server restart clears it (the UI shows "collecting samples…" until the first
  minute of data exists).
* Container history is keyed by the container id, so recreating a container starts a fresh timeline.

# Related

* [Dashboard metrics & aggregation](/project/dashboard.md) — the aggregate endpoint that embeds the summary.
* [UI & UX conventions](/project/ui-ux.md) — where the charts and expandable panels sit.
* [Delivered implementation](/project/implementation-status.md) — the pass that added this.

[^tracker]: NetworkMetricsTracker.cs — background sampler, ring buffer and stats parsing
[^program-cs]: Program.cs — /api/system/network, /api/containers/{id}/metrics and the dashboard aggregate
[^client]: App.tsx — NetworkSection, TrafficChart and the container inspector
