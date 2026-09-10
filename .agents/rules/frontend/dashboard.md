# Frontend — dashboard app (`clients/dashboard`)

Tenant-facing application. Read `frontend/shared.md` first; this file is only the divergences.

- **Port** 5174 · dev proxy target `https://localhost:7030` (HTTPS, with `ws: true` for the SignalR hub) · localStorage prefix `fsh.dashboard.*` · login header `X-FSH-App: dashboard`.
- **Env** (`src/env.ts`): `{ apiBase, defaultTenant, demoMode, inactivityIdleMs, inactivityWarningMs, disabledModules }`.
- Dev-proxy is HTTPS on purpose: routing the bearer token through an HTTP→HTTPS 307 redirect stripped the `Authorization` header.

## No RHF/zod — hand-rolled forms

The dashboard does **not** depend on react-hook-form or zod. Use controlled inputs + local state. Don't add those deps to match admin.

## Permissions — fetched, not in the JWT

The JWT carries **only role names**. `auth-context.tsx` fetches the effective permission list from `GET /api/v1/identity/permissions` (`getMyPermissions()` in `src/api/identity.ts`), caches it in `tokenStore` under `fsh.dashboard.permissions`, and exposes a `permissionsHydrated` flag so gated UI doesn't flash while the fetch is in flight (re-fetched on login/impersonation swaps; `refreshPermissions()` for role changes). Nav items are gated via `perm`/`anyPerm` in `src/components/layout/nav-data.ts`. `ProtectedRoute` is still **auth-only** (no per-route permission gating) — don't add `RouteGuard`-style gating here.

## Routing & realtime/SSE

- `routes.tsx` exports **`getRouter()`**, not a `router` const — see the load-order warning in
  `shared.md`. `App.tsx` and `lib/query-client.ts` (which navigates from outside React on the
  terminal-state 401/403s) both call it.
- Every route element is wrapped in `withSuspense(node)` (per-route skeleton fallback). No per-route permission guards.
- `RealtimeProvider` **and** `SseProvider` are mounted inside `AppShell` (authenticated routes only), under a `CommandPaletteProvider` (cmdk).
- SignalR provider pre-wires ~11 chat/notification events.
- **SSE** (`src/sse/`, dashboard-only): two-step token — `POST /api/v1/sse/token`, then `GET /api/v1/sse/stream?token=<guid>` consumed via fetch streaming (`parseSseStream` async generator; EventSource can't send auth headers). **Two split contexts:** `useSseStatus()` (stable, for status dots) vs `useSseEvents()` (mutates per event) to avoid cascading re-renders; `useSse()` is the composite.

## Module keys used here (`disabledModules`)

See `shared.md` for the mechanism. What each key hides in this app:

| Key | Nav / routes | Also |
|---|---|---|
| `chat` | Chat (`/chat*`) | `ChatUnreadBadge` (topbar), `ChatGlobalNotifier` — **not** the SignalR provider |
| `files` | My Files (`/files`) | the *My Files* page only; the chat composer and `components/file/*` keep uploading |
| `activity` | Operations → Live activity | the "View activity" header button, the Live feed card, the Live-events card's link |
| `billing` | Operations → Subscription / Wallet / Invoices | the hero's 3 stat cards, Subscription rail, Usage card, `FirstRunPanel`, Refresh button, and their queries |
| `catalog` | Catalog (3 items) | — |
| `tickets` | Helpdesk → Tickets | — |
| `auditing` | System → Audit trail | the "View audits" header button + Recent audits card |
| `health` | System → Health | — |
| `multitenancy` | `/settings/branding` tab | — |
| `notifications` | `/settings/notifications` tab | — |

`system/trash` uses `anyModule: ["catalog","tickets","files"]` — it fronts tabs from all three, so it
only disappears once every one is hidden. **Known gap:** its per-tab list isn't module-filtered yet, so
a hidden module's tab still shows for a user holding the permission.

⌘K (`command-palette-dialog.tsx`) duplicates the entire nav graph in its own `ActionGroup[]`, including
the **Create** group — every item needs the same key as its nav counterpart, or the palette keeps
offering routes that no longer exist.

## Impersonation

`token-store.ts` has `beginImpersonation` / `endImpersonationWithFreshTokens` / `restoreStashedActor` that stash the operator's tokens under `fsh.dashboard.impersonation.*`. `AuthProvider` exposes `beginImpersonation`/`stopImpersonation` and derives `ImpersonationInfo` from `act_sub` / `act_tenant` / `act_name` claims. Admin triggers the handoff one-way via its `dashboardUrl`.

## Performance

- `@tanstack/react-virtual` for long lists — use it for any large collection (chat history, big tables).
- `cmdk` powers the command palette.

## Theme

**Chroma-0 neutrals** (`--neutral-*: oklch(L 0 0)` — untinted; the warm-paper tint was deliberately removed). Rose default brand with **swappable accent themes** via `.accent-{rose,indigo,violet,sky,emerald,amber}` classes that override the `--brand-*` oklch stops; saffron secondary; Figtree font. Defined in `src/styles/globals.css`. Keep neutrals at chroma 0.

## Add-a-page deltas (on top of shared steps)

- Hand-roll forms (no RHF/zod).
- Wrap the route element in `withSuspense(<X/>)`; no permission guard.
- If the page belongs to a hidable module, tag its nav item, its route **and its ⌘K entries** with `module` (`src/lib/modules.ts`).
- If it consumes pushes: `useRealtimeEvent("EventName", handler)` (register the name in `realtime-context.tsx`) or `useSseEvents()` for SSE.
- Use `react-virtual` for long lists; keep neutrals chroma 0.
