# Frontend — shared conventions

Applies to **both** `clients/admin` and `clients/dashboard`. Read this for any React work, then read
the app-specific file (`admin.md` / `dashboard.md`) for divergences.

Stack: React 19 · Vite 7 · TypeScript · TanStack Query v5 · React Router 7 · Radix UI · Tailwind v4 ·
class-variance-authority (shadcn-style) · `@microsoft/signalr`. Path alias `@` → `src` (`vite.config.ts`).

## API client (`src/lib/api-client.ts`, `src/api/*`)

- One fetch wrapper: `apiFetch<T>(path, init)`. No axios.
- **Types are hand-written**, not generated. (`openapi-typescript` is declared in admin's devDeps but unused — there is no codegen step.) Define DTO `type`s and a `const BASE = "/api/v1/..."` in each `src/api/{feature}.ts`, with thin async functions calling `apiFetch`.
- Auth header `Authorization: Bearer <access>` from `tokenStore.getAccessToken()` (unless `skipAuth:true`).
- Tenant header (lowercase `tenant`) = `tokenStore.getTenant() ?? env.defaultTenant`, unless overridden per request.
- Errors: non-OK parses RFC 9457 `application/problem+json` and throws `ApiRequestError(status, message, problem)`. 204/empty → `undefined`.
- **Single-flight refresh:** on 401 with a refresh token, `POST /api/v1/identity/token/refresh` with `{token, refreshToken}`; a module-level `refreshPromise` dedupes concurrent refreshes; rotated token returns on `token` (not `accessToken`); the original request retries once.
- Search endpoints: build `URLSearchParams` with **PascalCase** keys (`PageNumber`, `PageSize`, …) to match the API.

## Env (`src/env.ts`) — runtime, not build-time

`loadRuntimeConfig()` fetches `/config.json` once at boot (awaited in `main.tsx` before React mounts); `env` is a getter that throws if read too early. One built image promotes across environments (operator writes `config.json`). The only `VITE_*` var, `VITE_API_BASE_URL`, configures the **Vite dev proxy target only** — it is not the runtime apiBase (`config.json` ships `apiBase: ""`, relative).

⚠️ **`env` is NOT readable at module scope.** `main.tsx` awaits `loadRuntimeConfig()` in its module
*body*, but ESM evaluates the whole static-import graph (`main → App → routes`) first, so anything
reading `env` at import time throws `"Runtime config not loaded"` during boot, with no error boundary
above it. Read `env` inside functions / hooks / render only — that's why `routes.tsx` exports
`getRouter()` (a lazy singleton, built on `App`'s first render) instead of a `router` const.

Adding a key means four edits: the `RuntimeConfig` type + mapping in `src/env.ts`, `public/config.json`,
`docker/config.json.template` (+ the var in `docker/docker-entrypoint.sh`), and Terraform's
`runtime_config` in `deploy/terraform/apps/starter/app_stack/` (+ a variable). The explicit mapping in
`loadRuntimeConfig` drops unknown keys, so a key missing from the type is silently ignored.

## Hiding modules per deployment (`disabledModules`)

A deployment can hide whole modules from the UI — a non-multitenant app doesn't want Billing, Webhooks
or Health cluttering its nav. **Cosmetic only:** the backend module stays registered, migrated and
permission-guarded; nothing is removed, so this is about clutter, not security.

- **Config:** `"disabledModules": ["billing","webhooks"]` in `config.json`. Also accepts a
  comma-separated string (`"billing,webhooks"`) because `envsubst` can only interpolate a flat string —
  the Docker template uses `"${FSH_DISABLED_MODULES}"`, Terraform passes a real array.
- **Default is "hide nothing".** Absent, `""` and `[]` all mean everything is visible, and unknown keys
  are dropped (with a dev-only `console.warn`). Keep that direction: the failure mode worth engineering
  against is a config *without* the key quietly hiding a module in production.
- **Keys** live in `src/lib/modules.ts`, kept **byte-identical between the two apps** (mirrored the way
  `lib/permissions.ts` mirrors the server registry) so one `FSH_DISABLED_MODULES` configures both
  containers: `activity` `auditing` `billing` `catalog` `chat` `files` `health` `impersonation`
  `multitenancy` `notifications` `tickets` `webhooks`.
  There is deliberately **no `identity` key** — it underpins login, `/settings/*`, `RouteGuard` and the
  chat user picker, so offering it would invite a config that bricks the app. And `files` gates the
  *My Files* page, **not** the upload plumbing (the chat composer keeps working).
- **Two axes, mirroring `perm`/`anyPerm`:** `module?: ModuleKey` (AND) and
  `anyModule?: readonly ModuleKey[]` (OR, for a surface fronting several modules — e.g. Trash).
- **Nav:** tag the item with `module` — the check lives inside the one gating helper per app
  (`filterNavSpec` / `isNavItemVisible`), so desktop and the mobile drawer are both covered, and a
  section left with no items is already dropped.
- **Routes:** wrap the block in `...moduleRoutes("billing", [ … ])` (or `anyModuleRoutes([…], […])`).
  The routes are *absent*, not gated, so a deep link falls through to the catch-all `*` NotFound exactly
  like a URL that never existed — and the module's lazy chunk is never fetched.
- **Everything else that links into a module must be gated too**, or it becomes a dead link: cards,
  settings tabs, the command palette (dashboard ⌘K duplicates the whole nav graph), and `useQuery`
  calls whose only consumer is a hidden widget (`enabled: isModuleEnabled("billing")`).
- **Playwright suites must not read the deployment's `disabledModules`** — see the stubbing note under
  Testing. Otherwise setting the key in `public/config.json` fails every inherited spec for the hidden
  modules (routes 404 for tests that navigate to them).
- ⛔ **Never gate a provider** — `SseProvider` / `RealtimeProvider` in the dashboard's `AppShell`.
  `Topbar` calls `useSseStatus()` unconditionally and the hook throws without its provider, which
  white-screens the shell. Gate the *leaves* (`ChatUnreadBadge`, `ChatGlobalNotifier`).

Known limitation: the dashboard's `overview.tsx` is substantially a billing dashboard (hero stats,
first-run panel, "Valid for"). Hiding `billing` filters those widgets correctly but leaves a sparser
page; redesigning it for a billing-less deployment is out of scope for the flag.

## Data fetching (TanStack Query v5)

- Shared `queryClient` (`src/lib/query-client.ts`): `staleTime: 30_000`, `refetchOnWindowFocus:false`, no retry on 401/403 else `failureCount < 2`.
- **Query keys are inline literal arrays**, hierarchical, params object last: `["users", {pageNumber, searchTerm}]`, `["user", id]`, `["user", id, "roles"]`. No central key factory.
- `useQuery`/`useMutation` live inline in page components. Invalidate in `onSuccess`: `queryClient.invalidateQueries({ queryKey: ["users"] })`. Pagination: `placeholderData: keepPreviousData`.

### ⚠️ The `mutate(arg)` race-safe pattern (golden rule #9)

`useMutation` reads its options at execute time, so values produced at call time (e.g. a fresh
`crypto.randomUUID()` client id) must ride **through `mutate(arg)`** and be read from the `variables`
argument of `onMutate`/`onSuccess`/`onError` — never from component state the callbacks close over,
or two rapid calls collide.

```ts
mutation.mutate({ text, clientId: crypto.randomUUID() });
// onMutate: ({ clientId }) => insert optimistic `temp:${clientId}`
// onSuccess: (real, { clientId }) => swap temp → real
// onError:   (_e, { clientId }) => rollback
```

## Routing (`routes.tsx`, `App.tsx`)

- `createBrowserRouter`, flat config. Pages are **named exports** loaded via a `lazyNamed(importer, name)` helper (adapts named → `React.lazy`'s default contract). No default exports.
- Nesting: public auth routes → `<ProtectedRoute/>` → `<AppShell/>` → page children. `errorElement: <RouteError/>`.
- Provider tree: `ThemeProvider > QueryClientProvider > AuthProvider > … > RouterProvider` + `sonner` `<Toaster/>`.

## Auth (`src/auth/`)

`token-store.ts` (localStorage + pub/sub), `jwt.ts` (`decodeJwt`), `AuthProvider`/`useAuth()`, `ProtectedRoute`.
Login `POST /api/v1/identity/token/issue` with header `X-FSH-App: "admin"|"dashboard"`. **localStorage keys are namespaced per app** (`fsh.admin.*` / `fsh.dashboard.*`) so both run side-by-side. Permission *source* differs per app — see the app files.

## Design system (Tailwind v4, shadcn-style)

- **`cn()` is at `src/lib/cn.ts`** (`twMerge(clsx(...))`) — not `lib/utils.ts`. `components.json`: `style:new-york`, `baseColor:slate`, `cssVariables:true`, `iconLibrary:lucide`.
- UI primitives in `src/components/ui/` are cva-based: `cva(base, { variants, defaultVariants })` + Radix `Slot`/`asChild` + `cn(buttonVariants({...}))`. Layout primitives in `src/components/list/` (admin) / similar (dashboard), re-exported from `index.ts`.
- **Tailwind v4 is CSS-first — there is NO `tailwind.config`.** Configured via the `@tailwindcss/vite` plugin and one entrypoint `src/styles/globals.css` (imported in `main.tsx`). Tokens: `:root` oklch primitives → semantic vars → an `@theme inline { --color-*: var(--…) }` block exposing them as utilities. `@custom-variant dark (&:is(.dark *))`.
- Add a new token in `globals.css` (primitive → semantic → `@theme inline`), then use the utility. Don't hard-code colors in components.

### Design language (both apps differ on purpose)

| | admin (operator) | dashboard (tenant) |
|---|---|---|
| Neutrals | cool-cast, hue 240, small non-zero chroma | **chroma 0** (untinted — the warm tint was removed) |
| Accent | single fixed chartreuse "signal" (`--accent-signal`) | rose brand + **swappable** `.accent-{rose,indigo,violet,sky,emerald,amber}` |
| Font | Geist / Geist Mono | Figtree (+ saffron secondary) |

So "neutrals must be chroma 0" is a **dashboard** rule. Admin neutrals are intentionally cool — match the file you're editing.

## Realtime (SignalR)

`src/realtime/realtime-context.tsx`: one `HubConnection` to `/api/v1/realtime/hub`. `@microsoft/signalr` is **dynamically imported** (lazy ~37KB) only when an authed session opens the hub. Auth via `accessTokenFactory`; a `tokenEpoch` (bumped by `tokenStore.subscribe`) forces reconnect on login/refresh/impersonation. Consume with `useRealtimeEvent("EventName", handler, deps)` (handler kept in a ref). Pre-register new event names in the provider's event list.

## Testing (Playwright, route-mocked)

- `playwright.config.ts`: `testDir: ./tests`, chromium, auto-boots `npm run dev`, no real backend.
- Tests in `tests/{area}/{name}.spec.ts`; helpers in `tests/helpers/`.
- **JWT seeding:** `seedAuthedSession(page, TEST_USER)` builds a fake JWT and `addInitScript`-writes `fsh.{app}.*` to localStorage before React boots (server isn't called, so signature is junk).
- **Runtime config is stubbed, not served.** `installShellMocks` calls `mockRuntimeConfig(page)`, which fulfils `/config.json` from `TEST_RUNTIME_CONFIG` (all modules enabled). That keeps a suite hermetic: a project generated from this template can hide modules in its own `public/config.json` without the inherited specs failing on routes that no longer exist. A spec that hand-rolls its mocks instead of calling the installer must call `mockRuntimeConfig(page)` itself. To vary it, call `mockRuntimeConfig(page, {…})` / `withDisabledModules(page, […])` **after** the installer — most recently registered handler wins.
- **Route mocking:** `mockJsonResponse(page, urlGlob, body)` / `mockProblemDetails(...)`. `installShellMocks(page)` stubs every call `AppShell` fires and **aborts** SSE/SignalR. Playwright matches most-recently-registered first → broad shell mocks in `beforeEach`, page-specific mocks after (they win).
- `beforeEach`: `seedAuthedSession(page, TEST_USER)` → `installShellMocks(page)`.

## Add a page/feature (shared steps)

1. API: extend `src/api/{feature}.ts` — hand-written types + `apiFetch` calls.
2. Page: `src/pages/{area}/{name}.tsx`, **named** export. `useQuery` with hierarchical key; `useMutation` invalidating in `onSuccess`, passing per-call data via `mutate(arg)`.
3. Route: add `const X = lazyNamed(() => import("@/pages/area/name"), "XPage")` and a child route under `AppShell`.
4. Nav + module: register the sidebar item, and tag **both** the nav item and the route with the owning
   `module` key (`src/lib/modules.ts`) if the page belongs to a hidable module — plus any card, tab or
   palette entry that links to it.
5. Test: `tests/{area}/{name}.spec.ts` with seed + shell mocks + page mocks.

Then apply the app-specific steps in `admin.md` / `dashboard.md` (forms, permission gating, suspense, etc.).
