# Provider Rotating Gateways — Design Spec

Status: Draft — pending review
Date: 2026-09-18
Author: Claude Opus 5 (with Eduardo Jimenez)
Related: `docs/superpowers/specs/2026-09-02-proxy-management-service-design.md` (parent — introduced `Proxy`, `/request`, `/feedback`, the policy engine), `docs/superpowers/specs/2026-09-03-provider-sync-brightdata-webshare-design.md` (the sync reconciliation this spec must survive)

## Context

Several proxy providers sell, alongside the individual proxies you contract, a **rotating gateway**: a
single virtual endpoint that rotates automatically across the very proxies you already own. WebShare
exposes it as:

```
host: p.webshare.io
port: 80
user: <account-username>-US-rotate
pass: <account-password>
```

BrightData exposes the same idea through its zone endpoint with parameters encoded in the username
(`brd-customer-<id>-zone-<zone>-country-us`). The rotation is performed by the provider, per request.
There is no per-IP listing to sync: the gateway is one durable endpoint whose exit IP changes
underneath it.

These gateways are useful for scrapers that want the provider to handle rotation instead of pulling a
pool and rotating client-side. Today the system has no way to represent one. The only workaround —
registering it as a manual proxy — severs the link to the provider account it actually belongs to,
which is the thing that makes it auditable and attributable.

This spec adds rotating gateways as a first-class endpoint type that stays attached to its real
provider account.

## Scope

**In scope:** modeling a rotating gateway, registering it by hand under a real provider account,
leasing it through the existing tag-based path, keeping it out of the auto-disable policy engine, and
giving it a health-check rule that matches what it actually is.

**Out of scope, deliberately:** structured connection parameters (sticky sessions, city selection,
country selection, session TTL). Providers support all of these by encoding them in the username, and
the operator can already express any of them by typing the username they want. No structured field is
introduced to hold them until there is a concrete need — adding one now would be guessing at a schema
across three providers with incompatible grammars.

## Constraints established during design

| Question | Decision |
|---|---|
| How does a gateway compete with individual proxies at lease time? | It doesn't. Control is 100% the operator's, via tags. The lease engine does not distinguish endpoint types at all. |
| How is a gateway registered? | Manually, under a real provider account. The operator pastes host, port, username and password. The system does not model any provider's username grammar. |
| Can the auto-disable policy engine disable a gateway? | Never. A gateway represents hundreds of exit IPs; disabling it over individual-IP bans is a false positive that costs the whole resource. |
| What happens when a gateway is genuinely down? | The active health check disables it — but only on endpoint-level failures, never on consumer-reported bans. |

## Architecture

A rotating gateway is a `Proxy` row, discriminated by a new `ProxyEndpointType`, attached to the real
provider account rather than to the well-known Manual account.

Three alternatives were considered:

- **Reusing `ProxyKind` by adding a `Rotating` member.** Rejected. `ProxyKind` describes the *nature of
  the IP* (DataCenter / Residential / Mobile / Dedicated) and is written by providers through
  `ProviderProxyRecord.Kind`. A residential rotating gateway is residential *and* rotating — orthogonal
  axes. Collapsing them forces a lossy choice and leaves a trap in `UpdateConnection`.
- **A separate `RotatingGateway` entity.** Rejected. It contradicts the tag-only lease decision:
  `RequestProxiesQueryHandler` would have to query and merge two tables, and so would feedback, usage
  events, tagging, the health-check job and the activity page. Half a dozen duplicated paths for an
  entity whose lifecycle is identical to a `Proxy`'s, and which — given the scope above — has no
  attributes of its own.
- **The chosen approach: a discriminator on `Proxy`.** One column, two guards, and every downstream
  path keeps working untouched.

### What attaching to the real provider account buys, for free

- `ProxyDto` already exposes `ProviderAccountName` and `ProviderType`, so a gateway is attributed to
  WebShare in the UI with no DTO change.
- `ProxyConfiguration` already sets `OnDelete(DeleteBehavior.Restrict)` on the provider-account FK, so
  an account with gateways cannot be deleted out from under them.
- `ProviderAccountSyncService.ReconcileAsync` filters on `ExternalId != null`. A gateway has
  `ExternalId == null`, so a sync neither updates nor retires it.

That last one is currently a fortunate side effect of the reconciliation query, not a stated contract.
This spec turns it into one with an integration test (see Testing).

### The password-protector hazard

`ProxyPasswordResolver` picks the protector **by `ProviderAccountId`**: `"proxy-password"` when the
proxy hangs off `ManualProviderAccount.Id`, `"provider-account"` for everything else.

A gateway hangs off a real provider account, so it will be *decrypted* with `"provider-account"`.
Its creation handler must therefore *encrypt* with `"provider-account"` too — **not** with the
`"proxy-password"` protector that `CreateManualProxyCommandHandler` uses. Copying that handler
verbatim is the obvious mistake here, and it produces a password that fails to decrypt at lease time
rather than a visible error at creation time.

## Domain and data model

New enum in Contracts:

```csharp
namespace FSH.Modules.Proxies.Contracts;

public enum ProxyEndpointType { Individual, RotatingGateway }
```

`Proxy` gains:

```csharp
public ProxyEndpointType EndpointType { get; private set; }
```

**`EndpointType` is set at creation and never modified.** It is deliberately absent from
`UpdateConnection`. That method's existing XML comment documents why its parameters are non-optional:
manual edits and renewals used to silently blank out provider metadata that a caller forgot existed
(fixed in `d00a8e7`). Keeping `EndpointType` out of it entirely means no edit or renewal path can
demote a gateway to an individual proxy by omission.

A dedicated factory, rather than an eleventh parameter on `Proxy.Create`:

```csharp
public static Proxy CreateRotatingGateway(
    Guid providerAccountId, string host, int port, ProxyProtocol protocol,
    string? username, string? protectedPassword,
    string? geolocation, string? providerGrouping, ProxyKind? kind)
```

It produces `ExternalId = null`, `Status = ProxyStatus.Testing` and
`EndpointType = RotatingGateway`. `Proxy.Create` is untouched and keeps producing `Individual`.

### Persistence

`ProxyConfiguration` gains the property mapping. One migration per provider project —
`FS.Proxy.Migrations.MSSQL/Proxies/` and `FS.Proxy.Migrations.PostgreSQL/Proxies/` — adding
`EndpointType int NOT NULL DEFAULT 0`. Existing rows become `Individual` with no backfill step.

### Uniqueness

There is no unique index on `(Host, Port)` today, and none is added. WebShare's US and DE gateways are
both `p.webshare.io:80` and differ only in the username; a unique index on host and port would make the
second one unregisterable.

Instead the create handler rejects a duplicate `(ProviderAccountId, Host, Port, Username)`. This is a
check-then-insert with a theoretical race, accepted knowingly: gateway registration is a manual
operator action measured in units per month, and the alternative — a filtered/partial unique index over
a nullable column — diverges between SQL Server and PostgreSQL for no practical gain.

### Provider account constraint

The create validator rejects `ProviderAccountId == ManualProviderAccount.Id`. A rotating gateway with
no provider behind it is not a thing that exists, and allowing it would reintroduce exactly the lost
provider link this spec is written to fix.

## Behavior

### Leasing — no change

`RequestProxiesQueryHandler` filters on `Status == Active` plus tags, and does not read `EndpointType`.
A gateway carrying the tags a scraper asks for is returned like any other candidate, under whichever
`ProxySelectionStrategy` was requested. `ProxyConnectionDto` already carries host, port, protocol,
username and password, so the scraper receives `p.webshare.io:80` with `<user>-US-rotate` and needs to
know nothing further.

Routing gateways to the scrapers that want them is the operator's job, done with tags.

### Auto-disable — exempt

`PolicyEvaluationService.EvaluateAsync` gains one guard, alongside the existing `Status != Active` one:

```csharp
if (proxy.EndpointType == ProxyEndpointType.RotatingGateway) return;
```

`ProxyUsageEvent` rows are still written, so `ProxyDto`'s rolling 24h success/failure counts and the
activity timeline keep working for gateways. What stops is any `PolicyProfile` acting on them.

Placing the guard in the service rather than in its callers covers all three entry points at once: the
single-event feedback endpoint, the batch feedback endpoint, and the health-check job.

### Health check — a rule of its own

The two signals this feature needs to separate are already separated by `UsageEventSource`:

- `ProxyHealthCheckOutcomeClassifier` produces only `Success`, `Failure` and `Timeout`. It **cannot**
  produce `Banned`. Every `SystemHealthCheck` event therefore answers "does the endpoint respond?" —
  the health of the gateway.
- `Banned` can only originate from a scraper calling the feedback endpoints. Every `ConsumerFeedback`
  event answers "did this request work?" — the health of the exit IP.

No new outcome taxonomy and no HTTP-status inspection (407 and friends) is needed.

The job keeps probing gateways exactly as it probes everything else — that is what promotes them from
`Testing` to `Active`, and `ShouldPromoteToActive` needs no change. `CheckOneProxyAsync` keeps calling
`IPolicyEvaluationService.EvaluateAsync` unconditionally (it is now a no-op for gateways, by the guard
above) and gains a sibling call, made only for gateways, that applies this rule:

> A rotating gateway is disabled when its most recent `RotatingGatewayFailureThreshold`
> `SystemHealthCheck` events are all non-`Success`.

Consumer feedback is excluded from that count. A gateway whose exit IPs are banned a thousand times
never turns itself off; a gateway whose credentials were revoked does.

Two consequences to keep visible:

- The job writes **one event per resolved health-check target**, not one per cycle. With three targets
  and a threshold of 5, a dead gateway is disabled after roughly two cycles, not five. The threshold
  counts events; this is documented rather than engineered around.
- A `Disabled` gateway falls outside the job's `Active || Testing` predicate and does not revive on its
  own. The operator re-enables it through the existing `SetProxiesStatusCommand`. This is the same
  behavior every proxy already has, not a new exception.

The threshold is configurable: `ProxiesOptions.RotatingGatewayFailureThreshold`, `[Range(1, 100)]`,
default `5`.

### Renewal — not applicable

`ProxyRenewalService` is reachable only from a `PolicyProfileType.AutoDisableAndRenew` decision, which
the auto-disable guard makes unreachable for gateways. This is also semantically right: rotation is
performed by the provider on every request, so there is nothing to renew.

### Provider sync — unchanged, but pinned down

No change to `ReconcileAsync`. Its `ExternalId != null` filter already excludes gateways. An
integration test converts that from an implementation detail into a contract.

### Consumer feedback — unchanged

The feedback endpoints do not distinguish endpoint types and must not. A scraper reporting on a gateway
is reporting on the request it just made, which is legitimate signal for the operator to read even
though it drives no automatic action.

## API

### Permissions

New resource `Proxies.RotatingGateways` with `View` / `Create` / `Update` / `Delete`, added to
`ProxiesPermissions.All`, with `View` marked `IsBasic: true` to match every other resource in the
module.

A separate resource rather than reusing `Proxies.ManualProxies`: registering a gateway operates against
a real provider account, which is a meaningfully different grant from registering a standalone proxy.

### Endpoints

New slice `Features/v1/RotatingGateways/` with `CreateRotatingGateway`, `UpdateRotatingGateway` and
`DeleteRotatingGateway` — each a handler, a validator and an endpoint, mirroring `ManualProxies`.
Contracts live in `Contracts/v1/RotatingGateways/`. Three `group.MapXxxEndpoint()` calls are added to
`ProxiesModule.MapEndpoints` beside the manual-proxy ones.

```csharp
public sealed record CreateRotatingGatewayCommand(
    Guid ProviderAccountId, string Host, int Port, ProxyProtocol Protocol,
    string? Username, string? PlaintextPassword,
    string? Geolocation, string? ProviderGrouping, ProxyKind? Kind,
    IReadOnlyList<string> TagNames) : ICommand<Guid>;
```

`UpdateRotatingGatewayCommand` carries the same fields plus `Id`, and — per the `UpdateConnection`
contract — every connection field is required, not optional, so an edit cannot blank out metadata the
caller forgot about.

Validation rules: `Host` required, ≤255; `Port` in 1–65535; `Username` ≤255; `Geolocation` ≤10;
`ProviderGrouping` ≤255; `ProviderAccountId` must resolve to an existing account and must not be
`ManualProviderAccount.Id`.

### Listing

No dedicated list endpoint. `ListProxiesQuery` already returns everything and gains a filter:

```csharp
ProxyEndpointType? EndpointType = null
```

appended as an optional parameter, so existing callers are unaffected.

`ProxyDto` gains a non-optional `EndpointType`, inserted immediately after `Kind` — inside the metadata
block and ahead of the defaulted trailing parameters, so those defaults stay intact and only the single
site that constructs the DTO needs touching.

### Endpoint scoping, both directions

`UpdateManualProxyCommandHandler` and `DeleteManualProxyCommandHandler` already scope their lookup with
`x.ProviderAccountId == ManualProviderAccount.Id`. A gateway hangs off a real provider account, so it is
excluded from both automatically. Nothing to change.

The reciprocal guard does **not** come for free and must be written. A gateway is identified only by its
`EndpointType`, so the gateway update and delete handlers must scope their lookup accordingly:

```csharp
.FirstOrDefaultAsync(x => x.Id == command.Id && x.EndpointType == ProxyEndpointType.RotatingGateway, ct)
```

Without it, `PUT /rotating-gateways` would happily rewrite the connection details of a provider-synced
individual proxy — which the next sync would then silently revert, making the bug intermittent.

The update handler also mirrors the manual-proxy password convention: a blank `PlaintextPassword` keeps
the stored `ProtectedPassword` rather than clearing it, so editing a gateway's tags does not require
re-typing its password.

## Admin UI

- `clients/admin/src/api/rotating-gateways.ts` — the API module.
- `clients/admin/src/pages/proxies/rotating-gateways.tsx` — the list/management page.
- `clients/admin/src/components/proxies/rotating-gateway-dialog.tsx` — create/edit dialog: provider
  account selector (excluding the Manual account), host, port, protocol, username, password,
  geolocation, kind, tags.
- `routes.tsx` — lazy import, a `proxies/rotating` route, a `RouteGuard` on
  `Proxies.RotatingGateways.View`, and the nav entry.
- `pages/proxies/list.tsx` — a badge distinguishing gateway rows from individual ones, and an
  endpoint-type filter. Without it an operator sees `p.webshare.io` repeated across several rows with
  no explanation.

Per-call data goes through `mutate(arg)`, never through state the mutation callbacks close over.

## Testing

**Domain** (`Proxies.Tests/Domain`)
- `CreateRotatingGateway` sets `EndpointType`, `ExternalId = null` and `Status = Testing`.
- `UpdateConnection` leaves `EndpointType` untouched.
- `Proxy.Create` still produces `Individual`.

**Handlers** (`Proxies.Tests/Handlers`)
- Create encrypts with the `"provider-account"` protector, not `"proxy-password"`.
- Create rejects `ManualProviderAccount.Id`.
- Create rejects a duplicate `(ProviderAccountId, Host, Port, Username)`.
- Update preserves `EndpointType`.
- Update with a blank password keeps the stored one.
- Update and delete reject an id belonging to an individual proxy (`NotFoundException`, not a silent edit).

**Validators** (`Proxies.Tests/Validators`) — one per new command. `Architecture.Tests` enforces their
existence independently.

**`PolicyEvaluationService`** (`Proxies.Tests/Services`)
- A gateway with more than enough negative events to cross any threshold does not change status.
- An individual proxy under identical conditions still does — the guard must not have widened.

**Health-check job** (`Proxies.Tests/Jobs`)
- N consecutive non-`Success` `SystemHealthCheck` events disable a gateway.
- `ConsumerFeedback` events — including `Banned` — do not count toward N.
- A `Success` inside the window resets the run.
- `Testing → Active` promotion still works for gateways.

**Integration** (`Integration.Tests`)
- Syncing a provider account does not retire or modify that account's gateways. This is the test that
  turns `ReconcileAsync`'s `ExternalId != null` filter into a contract.

## Documentation

`.agents/rules/modules/proxies.md` gains a rotating-gateways section covering: the endpoint-type
discriminator, the provider-account attachment and the protector it implies, the auto-disable
exemption, and the health-check rule.

(AGENTS.md golden rule #10 — the external docs repo — belongs to the upstream template and does not
apply to this fork.)
