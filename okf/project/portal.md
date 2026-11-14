---
type: Reference
title: Portal visibility & ordering
description: How a published app is hidden from, revealed on, deleted from, or added in bulk to the portal catalogue, and how the manual SortOrder drives the order of the tiles within each category.
tags: [portal, landing, visibility, ordering, delete, candidates, bulk, api]
status: stable
generated: { by: opencode/deepseek-v4.1-flash, at: 2026-11-14T12:41:24Z }
sources:
  - id: program-cs
    resource: ../../src/eh.DockManager.Server/Program.cs
    title: Program.cs — /api/portal/landing and /api/portal/apps/reorder
  - id: yarp
    resource: ../../src/eh.DockManager.Server/Portal/YarpPortalProxy.cs
    title: YarpPortalProxy.cs — route generation
  - id: client
    resource: ../../src/eh.DockManager.Client/src/App.tsx
    title: App.tsx — PortalLandingView, PortalCard and PortalAppEditor
  - id: mcp
    resource: ../../src/eh.DockManager.Server/McpAndWebhooks/McpAndWebhookEngine.cs
    title: McpAndWebhookEngine.cs — delete_portal_app tool and portal access entries
  - id: candidates
    resource: ../../src/eh.DockManager.Server/Portal/PortalAppCandidates.cs
    title: PortalAppCandidates.cs — /api/portal/available, /api/portal/apps/bulk and PortalCandidateService
  - id: self
    resource: ../../src/eh.DockManager.Server/Portal/PortalSelf.cs
    title: PortalSelf.cs — the shared DockManager-self test used by the landing flag and both delete paths
  - id: kube
    resource: ../../src/eh.DockManager.Server/Kubernetes/KubeClusterService.cs
    title: KubeClusterService.cs — ListIngressesAsync and the cluster listing helpers
---

# Hiding an app

`PortalApp.Hidden` removes the app **from the landing list only** — the YARP route is still
generated, so `https://<subdomain>.<domain>` keeps working. This matches the label "hide from
portal" and is the opposite of the first implementation, which also dropped the route.[^yarp]

* `GET /api/portal/landing` omits hidden apps. An administrator can add
  `?includeHidden=true` to get them back; every returned app carries its `hidden` flag.
* The Portal view has a **Show hidden** toggle (admins). Hidden cards render dimmed with a
  `hidden` badge, and each card has a hide/unhide (eye) button plus the editor toggle.
* The editor toggle reads "Hide from the portal" with an explanatory hint.
* Hiding is not a pure visibility toggle at the data layer: `PUT /api/portal/apps` forces
  `Manual = true`, so using the card's hide button on an auto-discovered app silently converts
  it to a manual row (after which discovery no longer overwrites it).

# Deleting an app

Deletion removes the `PortalApps` row and rebuilds the proxy config. It is exposed as the
admin-only `DELETE /api/portal/apps/{subdomain}` and the MCP tool `delete_portal_app`
(`portal:delete`).[^program-cs][^mcp]

* Each Portal card has a **Delete** button in its action row (admins, two-step confirm), and the
  Edit dialog's **Delete** button is offered for **every** app except DockManager itself — the
  previous `manual`-only guard is gone.[^client]
* **DockManager's own entry is protected everywhere.** One rule, `PortalSelf.IsSelf(app)`
  (`PortalSelf.cs`), matches DockManager's own container by `EHDM_SELF_CONTAINER`, the container id
  against `Environment.MachineName`, or the container name. `GET /api/portal/landing` flags that
  entry `self: true` and drives its base-domain URL, and the Portal omits the Delete button on the
  card and in the editor, which instead reads "DockManager's own entry — it can't be deleted from
  here." The protection is also enforced **server-side**: `DELETE /api/portal/apps/{subdomain}`
  returns **`409`** and the MCP `delete_portal_app` tool returns **`-32003`** when the target is the
  self entry, so no call can remove the management interface. (Auto-discovery would re-create the
  entry within 30 s anyway.)[^program-cs][^self][^mcp][^client]
* **A delete of an auto-discovered app is not durable.** `PortalDiscoveryService` reconciles
  every 30 seconds (and on Docker events), and `ReconcileAsync` re-creates any container that
  still exposes a portal unless a `Manual` row already exists, so a deleted auto-discovered app
  returns within ~30 s. To remove one permanently, stop exposing it (drop `eh.portal.enable` /
  `eh.portal.*` labels or stop the container) or **hide** it instead. Only manually-added apps
  (external links and API/MCP-created rows whose container is not exposing a portal) stay deleted.

# Ordering apps

Order is data-driven by `PortalApp.SortOrder`; `GetPortalAppsAsync` returns
`ORDER BY SortOrder, Name`.[^program-cs]

* `POST /api/portal/apps/reorder` takes `{ "order": ["sub1", "sub2", …] }` and assigns
  `SortOrder = index` to each, then rebuilds the proxy routes. It never flips an auto-discovered
  app to `manual` (unlike `PUT /api/portal/apps`, which does).
* Portal cards have **move earlier / move later** buttons (admins) that reorder within the card's
  category and persist through that endpoint.
* The client groups tiles by `category` and lists categories **alphabetically**; the order
  inside a category is the stored `SortOrder`.
* Because every app is created with `SortOrder = 0`, the pre-existing behaviour was alphabetical
  by name until an admin reorders. To move an app to another group, edit its `Category`.

# Adding apps in bulk

The Portal's **Add available** button opens a dialog that loads one connected host's apps and lets
an administrator search, multi-select and inline-edit them before creating them all at once as
manual apps.[^candidates][^client]

* `GET /api/portal/available?hostId=<id>` lists a Docker host's containers; `?clusterId=<id>` lists
  a Kubernetes cluster's **public** Services (LoadBalancer, or NodePort with an external address),
  Ingresses and Gateway API HTTPRoutes.[^kube] Each candidate carries a suggested name, subdomain,
  category and target (`targetPort` for containers, `externalUrl` for Kubernetes) plus a `present`
  flag when that container, URL or subdomain is already in the catalogue.
* `POST /api/portal/apps/bulk` creates a batch in one call: every entry is written as a `Manual`
  app (`AutoDiscovered = false`), the proxy config is rebuilt once, and the batch is audited as
  `portal.bulk_add`. If any subdomain is empty/invalid or already taken — by the catalogue or by an
  earlier item in the same batch — the whole batch is rejected with `409` and a `conflicts` list,
  so nothing is half-added.
* Kubernetes candidates are stored as **external-URL** apps (`ResolveAddress` returns `ExternalUrl`),
  so the portal still proxies `https://<subdomain>.<domain>` to the Service address or the
  Ingress/HTTPRoute host. Docker candidates keep `ContainerName` + `TargetPort` and proxy to
  `http://<container>:<port>`, the same shape auto-discovery produces.
* The candidate builder reuses the discovery helpers (`ResolveSubdomain`/`ResolvePort`/`Slug`, now
  `internal static` on `PortalDiscoveryService`), so a hand-picked candidate looks like a
  discovered one.

# Related

* [UI & UX conventions](/project/ui-ux.md) — the Portal card actions and editor.
* [Operation guards across UI, MCP and server](/project/operation-guards.md) — the self-delete guard is the worked example.
* [Server identity & hostname settings](/project/server-identity.md) — the domain the tiles link to.
* [Delivered implementation](/project/implementation-status.md) — endpoints and tests.

[^program-cs]: Program.cs — /api/portal/landing and /api/portal/apps/reorder
[^yarp]: YarpPortalProxy.cs — route generation
[^client]: App.tsx — PortalLandingView, PortalCard and PortalAppEditor
[^mcp]: McpAndWebhookEngine.cs — delete_portal_app tool and portal access entries
[^candidates]: PortalAppCandidates.cs — /api/portal/available, /api/portal/apps/bulk and PortalCandidateService
[^kube]: KubeClusterService.cs — ListIngressesAsync and the cluster listing helpers
[^self]: PortalSelf.cs — the shared DockManager-self test used by the landing flag and both delete paths
