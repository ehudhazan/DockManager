---
type: Reference
title: Portal visibility & ordering
description: How a published app is hidden from (or revealed on) the portal landing page, and how the manual SortOrder drives the order of the tiles within each category.
tags: [portal, landing, visibility, ordering, api]
status: stable
generated: { by: opencode/deepseek-v4.1-flash, at: 2026-11-13T15:44:00Z }
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

# Related

* [UI & UX conventions](/project/ui-ux.md) — the Portal card actions and editor.
* [Server identity & hostname settings](/project/server-identity.md) — the domain the tiles link to.
* [Delivered implementation](/project/implementation-status.md) — endpoints and tests.

[^program-cs]: Program.cs — /api/portal/landing and /api/portal/apps/reorder
[^yarp]: YarpPortalProxy.cs — route generation
[^client]: App.tsx — PortalLandingView, PortalCard and PortalAppEditor
