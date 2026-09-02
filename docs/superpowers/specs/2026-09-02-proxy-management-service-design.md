# Proxy Management Service — Design Spec

- **Date:** 2026-09-02
- **Status:** Approved for implementation planning
- **Author:** Eduardo Jiménez (via brainstorming session)

## Context & problem

The company sells public-tender monitoring services across multiple Latin American
countries. Its core systems are a fleet of scrapers built on different tech stacks
(.NET Framework 4.8 legacy, .NET 5, and the new TAG — Tender Active Grabber — platform).
Every scraper needs outbound proxies (WebShare with multiple accounts, Oxylabs,
BrightData today; Decodo and Soax planned; plus manually provisioned proxies on
company-owned machines).

Proxy configuration is currently scattered across `web.config` files, hardcoded
values, config files, and database rows in different systems. When a proxy stops
working or a target site bans it, someone has to manually edit configuration in
multiple places. There is no central inventory, no automated health tracking, and
no easy way to fail over from one provider to another.

## Goal

Build a centralized proxy management service so that:
- Proxies are administered from one place instead of N scattered configs.
- Scrapers request proxies over a REST API instead of embedding provider details.
- Proxies can be grouped/filtered by arbitrary criteria (country, scraper type,
  functionality) via tags.
- Proxies can be enabled/disabled manually, and renewed manually or automatically
  per group.
- If a provider degrades or a proxy gets banned, consuming services can keep
  working by drawing from other proxies/providers in the same tag group, ideally
  without any change on the consumer side.

## Scope

**In scope (this spec / v1):**
- A new `Proxies` module inside this fs-proxy monolith.
- Provider adapters for WebShare, Oxylabs, BrightData, and manually-entered proxies.
- Tag-based grouping, REST API for proxy request + feedback, dual auth (API Key +
  JWT), active + passive health signals, configurable per-tag auto-disable/renew
  policy, and an admin UI (list/filter, enable/disable, provider account ABM,
  manual proxy ABM) inside `clients/admin`.

**Out of scope (future work, tracked separately):**
- Actually wiring TAG or the legacy .NET 4.8/.NET 5 scrapers to consume this
  service — this spec delivers the service only, ready to be consumed.
- Decodo and Soax adapters (add later using the same `IProxyProviderAdapter`
  pattern, once those accounts exist).
- Bulk "renew by group" button in the UI (the automated policy-driven renewal is
  in v1; the manual bulk-trigger UI is phase 2).
- Health/stats dashboard in the UI.
- Advanced, multi-step health checks (see "Phase 2" below) — v1 health checks are
  a single HTTP request per target.
- Multi-tenant data isolation — this is an internal single-tenant tool.

## Architecture

A new module `Proxies` (`src/Modules/Proxies` runtime project + `Proxies.Contracts`)
inside the existing monolith, following the same Vertical Slice Architecture as
every other module: Mediator commands/queries, a `{Name}Validator` per handler,
Minimal API endpoints registered via `MapEndpoints()`.

All entities implement `IGlobalEntity` (opt out of Finbuckle tenant isolation) —
this is an internal ops tool for a single organization, not a multi-tenant SaaS
feature.

Internal pieces:
- **Data/domain layer** — see "Data model" below.
- **Provider adapters** — `IProxyProviderAdapter` with one implementation per
  provider (`WebShareAdapter`, `OxylabsAdapter`, `BrightDataAdapter`,
  `ManualAdapter`), each declaring capability flags (`SupportsSync`,
  `SupportsRenew`). New providers (Decodo, Soax) are added later as a new
  adapter without touching the rest of the module.
- **Public REST API** — dual authentication (API Key for legacy scrapers, JWT for
  TAG and the admin UI) on the same endpoints.
- **Background jobs (Hangfire)** — periodic provider sync, periodic active health
  checks, and inline policy evaluation triggered by new usage events.
- **Cache (HybridCache/Redis)** — for the hot-path proxy-selection query, and for
  ephemeral sticky-session affinity data. Invalidated on proxy state changes.
- **UI** — a new "Proxies" section inside `clients/admin`, consuming the same API
  via JWT, following existing patterns (TanStack Query, Radix/Tailwind, permission
  mirroring in `RouteGuard`).

Scale assumption: hundreds of proxies, tens of requests/second — informs caching
and health-check frequency defaults, not a hard constraint.

## Data model

- **`ProviderAccount`** — a configured account against a provider (e.g. two
  separate WebShare accounts, one Oxylabs account, one BrightData account, and a
  `Manual` pseudo-account for self-hosted proxies). Fields: provider type, name,
  API credentials (encrypted at rest), enabled flag, last sync timestamp/status.
- **`Proxy`** — an individual proxy: host, port, protocol (HTTP/HTTPS/SOCKS5),
  credentials (encrypted), `ProviderAccountId`, `ExternalId` (the provider-side
  id, used to reconcile on sync), status (`Active` / `Disabled` / `Banned` /
  `Testing` / `Retired`), created-at, last-renewed-at.
- **`Tag`** — a reusable free-form label (backing autocomplete in the UI and
  avoiding duplicate near-identical tags), plus `ProxyTagAssignment` as the
  many-to-many join to `Proxy`.
- **`PolicyProfile`** — one of `Manual` / `AutoDisable` / `AutoDisableAndRenew`,
  with a failure-count threshold, a time window, and a minimum count of distinct
  reporters (to avoid one misbehaving client tanking a healthy proxy). Assigned
  to a `Tag` via `TagPolicyAssignment`.
- **`HealthCheckTarget`** — a named HTTP check: test URL, expected
  success criterion (status code and/or body keyword), timeout. Assigned to a
  `Tag` via `TagHealthCheckTargetAssignment` (e.g. tag `país=CL` → Mercado
  Público; tag `país=PE` → SEACE). A global default target is used for any proxy
  whose tags resolve to no specific target, so every proxy is still checked.
- **`ProxyUsageEvent`** — a single table for both active health-check results and
  passive consumer feedback, distinguished by `Source`
  (`SystemHealthCheck` / `ConsumerFeedback`) and `Outcome`
  (`Success` / `Failure` / `Banned` / `Timeout`). For `SystemHealthCheck` events,
  an optional `HealthCheckTargetId` records which target was tested. Unifying the
  two sources avoids duplicating the policy-evaluation logic.
- **`ApiClient`** — an API Key credential for legacy scrapers: name, hashed key,
  enabled flag.

**Conflict rule:** if a proxy's tags resolve to more than one `PolicyProfile`, the
most restrictive wins: `AutoDisableAndRenew` > `AutoDisable` > `Manual`.

**Manual-proxy renewal:** there is no provider API to call, so "renew" for a
`Manual` proxy means: disable it and raise a notification (via the existing
`Notifications` module) asking an admin to replace it by hand.

## Provider integration

`IProxyProviderAdapter` exposes:
- `SyncProxiesAsync(ProviderAccount)` — pulls the provider's current inventory and
  reconciles it against existing `Proxy` rows by `ExternalId` (adds, removes,
  updates status). Triggered by the periodic Hangfire job and by the manual
  "sync now" button in the UI.
- `RenewProxyAsync(Proxy)` — triggers rotation/renewal through the provider's API
  when the policy engine requires it. `ManualAdapter` does not implement this;
  the manual-proxy notification flow above applies instead.

Each adapter wraps the provider's SDK/HTTP client with Polly (retries + circuit
breaker, matching this repo's standard outbound-resilience conventions), so one
degraded provider does not block sync for the others or crash the shared job.

**Failover:** because proxy selection for scrapers is tag-based rather than
provider-based, cross-provider failover falls out of the design for free — if
every `BrightData` proxy tagged `país=CL` goes down, the same tag-filtered request
keeps being served from `Webshare`/`Oxylabs` proxies sharing that tag, with no
change needed on the consumer side. This assumes operators tag proxies so that a
given tag has redundancy across more than one provider where that matters — the
system doesn't enforce this, it's an operational recommendation.

## Public REST API

Dual authentication on the same endpoints via a combined `AuthenticationHandler`:
`X-Api-Key` header (resolved against `ApiClient`, for legacy scrapers) or
`Authorization: Bearer <JWT>` (TAG, admin UI). Admin endpoints additionally
require a JWT-backed permission, same as the rest of the system.

**Consumer endpoints:**
- `POST /api/proxies/request` — body: `tags[]`, `count` (default 1), `strategy`
  (`RoundRobin` default / `Random` / `Sequential` / `Sticky`), optional
  `sessionId`. `count > 1` returns a list of matching candidates so a scraper
  with its own rotation/session logic can manage it directly. `strategy=Sticky`
  with a `sessionId` pins the first-chosen proxy to that session (stored in Redis
  with a configurable TTL — ephemeral, not persisted to the database) so repeat
  calls with the same `sessionId` get the same proxy while it stays active.
- `POST /api/proxies/{id}/feedback` — body: `outcome`
  (`Success`/`Failure`/`Banned`/`Timeout`), optional detail. Creates a
  `ProxyUsageEvent(Source=ConsumerFeedback)` and triggers policy evaluation for
  that proxy.

**Admin endpoints (JWT + permission):** CRUD for `ProviderAccount`; CRUD for
manual proxies; paginated list/filter of proxies (by tag/status/provider);
enable/disable a single proxy or a bulk selection/tag; manual "sync now" trigger
per account; CRUD for `Tag`, `PolicyProfile`, and their tag assignments; CRUD for
`ApiClient` (issue/revoke legacy API keys).

## Health checks & policy engine

**Active check (Hangfire, configurable interval):** for each `Active` proxy, the
job resolves all distinct `HealthCheckTarget`s from its tags (falling back to the
global default target if none) and tests the proxy against each one with a single
HTTP request, recording one `ProxyUsageEvent(Source=SystemHealthCheck)` per
target tested — this gives per-portal visibility (e.g. "this proxy works against
Mercado Público but fails against SEACE").

**Policy evaluation:** runs inline, immediately after any `ProxyUsageEvent` is
created (from either source) — a cheap query counting `Failure`/`Banned` events
for that proxy within the resolved `PolicyProfile`'s time window, requiring the
configured minimum number of distinct reporters. It evaluates the proxy's
combined signal across all targets/sources — the per-target breakdown from health
checks is for diagnostics/dashboard use, not a separate threshold.

If the threshold is crossed:
- `AutoDisable` → the proxy moves to `Disabled`.
- `AutoDisableAndRenew` → moves to `Disabled`; if the provider adapter supports
  `RenewProxyAsync` it's invoked, otherwise (including always for `Manual`) an
  admin notification is raised via `Notifications`.
- After a successful renewal, the proxy moves to `Testing` (not directly back to
  `Active`) until the next active health check confirms it's good.

The same notification mechanism fires when a `ProviderAccount` sync fails
repeatedly (e.g. expired credentials), so operational problems surface without
having to read logs.

**Phase 2 (explicitly deferred, not built now):** v1 health checks are a single
HTTP request per target. A richer, multi-step check (simulating an actual
scraping sequence, headless-browser based, etc.) is valuable to catch proxies
that fail mid-flow even though a plain GET succeeds — noted for a future
iteration behind the same `HealthCheckTarget` abstraction, without redesigning
the rest of the system. Responsibility for detecting failures during an actual
scrape run stays with the consuming scraper in v1; this service's active checks
are a best-effort early warning, not a guarantee.

## Admin UI (`clients/admin`)

A new "Proxies" section with the v1 screens:
- **List + filter** — paginated table, filter by tag/status/provider-account,
  with a recent-health-signal column.
- **Enable/disable** — per-row action and bulk action over a selection or an
  entire tag.
- **Provider account ABM** — create/edit/delete `ProviderAccount` (provider type,
  name, credentials), "sync now" button, last-sync status.
- **Manual proxy ABM** — create/edit/delete `Manual` proxies (host, port,
  protocol, credentials, tags).

Deferred to phase 2: bulk "renew by group" button (the automated backend renewal
is in v1) and the health/stats dashboard.

Follows existing `clients/admin` conventions: TanStack Query for data-fetching,
permissions mirrored in `RouteGuard`, existing Radix/Tailwind components.

## Security

- `ProviderAccount` credentials and `Proxy` credentials are encrypted at rest
  (ASP.NET Core Data Protection API).
- `ApiClient` keys are generated once, shown once in the UI, and stored hashed —
  never in plaintext, same treatment as a password.
- Rate limiting on the public consumer endpoints, following this repo's existing
  `security.md` conventions.

## Error handling

Domain exceptions follow this repo's `api-conventions.md` (ProblemDetails):
`NoProxyAvailableException` (no active proxy matches the requested tags → 404),
`ProviderSyncException`, `RenewalNotSupportedException`. Every sync or renewal
failure is logged with structured logging and, where applicable, raises the admin
notification described above — nothing fails silently.

## Testing

- Handlers and validators: xUnit/Shouldly/NSubstitute, this repo's standard unit
  test pattern.
- `Architecture.Tests`: verifies the module doesn't violate boundary rules.
- `Integration.Tests` (Testcontainers): covers the full REST API surface and the
  policy-evaluation logic against real Postgres/Redis.
- Provider adapters are tested against mocked HTTP, not the real WebShare/Oxylabs/
  BrightData APIs — no external dependency in CI, no quota burned on real
  provider accounts.

## Open items for the implementation plan

None outstanding — all decisions in this document were confirmed during the
brainstorming session. The next step is `writing-plans` to turn this into a
step-by-step implementation plan.
