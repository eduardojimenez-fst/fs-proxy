# Proxy Client SDK + Batch Feedback — Design Spec

Status: Draft — pending review
Date: 2026-09-14
Author: Claude Opus 5 (with Eduardo Jimenez)
Related: `docs/superpowers/specs/2026-09-02-proxy-management-service-design.md` (parent — introduced `/request`, `/feedback`, policy engine), `docs/superpowers/specs/2026-09-04-proxy-tag-categories-design.md` (the `TagCategory` catalog this spec surfaces as typed constants)

## Context

The Proxy Management Service is deployed to QA and Production and synced with its providers. The remaining gap is consumption: the scrapers that should be pulling proxies from it are still reading hardcoded lists from configuration files.

Two families of consumers:

- **TAG (TenderActiveGrabber, .NET 10)** — scrapers do not build `HttpClient` directly. They go through a wrapper, `WebScraper` / `WebScraperV2`, which owns proxy rotation (`_sharedProxyCounter`, `GetProxy()`, `RenewProxy()`, `PinProxy()`) and every GET/POST to the target portals. Proxies come from `webscraper.json` via `WebScraperSettings.Resolve()`, which supports per-process pools through sub-sections (`Tender-Attachments`, `PurchaseOrder`) that override only the proxy list.
- **Legacy scrapers (.NET Framework 4.8)** — built on `WebRequest`/`HttpWebRequest`. They are not migrating to `HttpClient` in the foreseeable future. They need a flat list of proxies with credentials, nothing more.

`webscraper.json` also holds BrightData and WebShare credentials in plaintext, in source control.

This spec defines a client SDK that replaces the configuration-file proxy list, feeds outcome signal back to the policy engine, and does so without restructuring either consumer family.

## Constraints established during design

| Question | Answer |
|---|---|
| Usage pattern | Mixed — some scrapers rotate aggressively per request, others need sticky sessions |
| Volume | Medium: 10k–500k proxied HTTP requests/day across all scrapers |
| Distribution | Internal NuGet feed; multi-target `netstandard2.0` + `net10.0` |
| Propagation of admin-side changes (retag, disable) | TTL-based refresh, minutes is acceptable. No push (SignalR/SSE) needed |
| API key granularity | One per scraper, so the policy engine's `MinDistinctReporters` works as designed |

## Approach chosen

A shared client SDK distributed via NuGet, offering **three levels of adoption**, rather than a local forward-proxy sidecar or per-scraper ad-hoc HTTP calls.

The deciding argument is not DRY. It is that the SDK centralizes **outcome classification** — deciding whether a timeout, a 403, or a 503 means the proxy failed or the portal failed. The policy engine is only as good as the signal it receives; letting each scraper interpret that independently produces inconsistent signal, which means dead proxies nobody disables and healthy proxies disabled by mistake.

The sidecar approach is kept in reserve for the day a non-.NET scraper appears.

## Current backend surface (unchanged by this spec unless noted)

- `POST /api/v1/proxies/request` — `{ tags[], count, strategy, sessionId }` → `ProxyConnectionDto[]` with **decrypted** passwords. `count` capped at 50 by `RequestProxiesQueryValidator`. Auth: `X-Api-Key` header, or JWT with `ProxiesPermissions.Consumers.Request`.
- `POST /api/v1/proxies/{id}/feedback` — `{ outcome, detail }`. Runs `PolicyEvaluationService.EvaluateAsync` **inline**, per event.
- `PolicyEvaluationService` counts only `Outcome != Success` events inside the profile's window, and requires both `FailureThreshold` and `MinDistinctReporters` to be met before disabling.
- `Tag.Normalize` = trim + lowercase. `TagCategory`/`TagCategoryValue` is an advisory catalog used only to compose `"{category}:{value}"` strings.

---

## 1. SDK surface — three levels of adoption

The integration point is **not** `HttpClient`. It is `WebScraper.LoadWebScraperConfiguration()`, which today does `Proxies = _settings.Proxies`.

### Level 0 — flat list (legacy 4.8, and the foundation of everything above)

```csharp
public interface IProxySource
{
    IReadOnlyList<ProxyEndpoint> GetProxies(params string[] tags);   // synchronous, performs no I/O
    void Report(Guid proxyId, ProxyOutcome outcome, string? detail = null);
    Task WarmupAsync(CancellationToken ct = default);
}

public sealed class ProxyEndpoint
{
    public Guid Id { get; }
    public string Host { get; }  public int Port { get; }
    public string? User { get; } public string? Password { get; }
    public IWebProxy ToWebProxy();
    public NetworkCredential ToCredential();
    // ToString() masks credentials.
}
```

**`GetProxies` is synchronous and performs no I/O.** It reads an in-memory snapshot maintained by a background refresher. This is a hard requirement, not a convenience: the existing scrapers are full of `.Result` (`GetStringWithRetry`, `PostWithRetry`, `PostValues`). An async-only SDK would force `.Result` over an internal `HttpClient` — the classic deadlock. Only `WarmupAsync()` blocks, once per process at startup.

For consumers with no DI container (the 4.8 scrapers), the core package exposes a static
`ProxySource.Instance`, initialized once at process start. Under net10 the same object is
registered in DI; the static accessor is a convenience, not a second implementation.

Legacy usage, keeping `WebRequest`:

```csharp
var pool = ProxySource.Instance.GetProxies(ProxyTags.Country.Chile);
var ep = pool[i % pool.Count];
var req = (HttpWebRequest)WebRequest.Create(url);
req.Proxy = ep.ToWebProxy();
// ...
catch (WebException ex) { ProxySource.Instance.Report(ep.Id, ProxyOutcome.From(ex)); }
```

### Level 1 — TAG's `WebScraper` / `WebScraperV2`

```csharp
private void LoadWebScraperConfiguration()
{
    UseProxy = _settings.UseProxy;
    Proxies = _proxySource.GetProxies(_settings.ProxyTags)      // was: _settings.Proxies
                          .Select(ProxyInfo.From).ToList();
}
```

Supporting changes:

- **`ProxyInfo` gains `Guid ProxyId`.** Its current `Id` is an `int` index from the JSON file; the service uses `Guid`. Without this field there is no way to report. `ProxyKey` (`ip-port-user`) remains for logs and for the existing local quarantine.
- **`RenewProxy()` reports.** It is the exact hook: if it is called, the previous proxy failed. The existing `catch` blocks already distinguish `HttpRequestException` (carrying `StatusCode`) from `TaskCanceledException` with an inner `TimeoutException`.
- **Per-process sub-sections become tag sets.** `WebScraperSettings.Resolve(config, scrapElement)` stops returning a proxy list and returns tags:

```json
"Tender-Attachments": { "ProxyTags": [ "entitytype:tender", "operationtype:attachments", "country:cl" ] },
"PurchaseOrder":      { "ProxyTags": [ "entitytype:purchaseorder", "country:cl" ] }
```

Note that `Tender-Attachments` is not one dimension but a **combination**: entity type `tender`
*and* operation type `attachments`. This is exactly what `RequestProxiesQueryHandler` does — it ANDs
every tag in the request — so the existing per-process pools decompose naturally into the catalog's
dimensions instead of needing a tag of their own.

Side effect worth stating: **`webscraper.json` stops carrying provider credentials in plaintext.** The only remaining secret is the API key, supplied through an environment variable.

### Level 2 — `DelegatingHandler`

For code written from here on that does not go through `WebScraper`. Not a migration path.

```csharp
services.AddFsProxyClient(config.GetSection("FsProxy"));
services.AddHttpClient("mercadopublico").AddFsProxyRotation(ProxyTags.Country.Chile);
```

---

## 2. Pool lifecycle

- **Fill:** `POST /request { tags, count = PoolSize, strategy = Random }`.
  **`Random`, never `RoundRobin`.** The round-robin cursor is global per tag-set and mutated on every call; ten scrapers refreshing their pools through it would turn the cursor into noise. Rotation is local, over an immutable snapshot swapped wholesale on refresh.
- **Refresh:** every 60–120 s with 20% jitter, plus a reactive refresh when healthy proxies fall below 50% of `PoolSize`. The reactive path carries a ~15 s debounce so a fully-down tag set does not cause every scraper to hammer `/request`.
- **Local quarantine:** a proxy reported `Banned` or `Timeout` is set aside locally for ~2 min. The client protects itself immediately rather than waiting for the server to disable. If *every* proxy is quarantined, the quarantine is ignored and the least-bad one is served — a questionable proxy beats throwing.
- **Sticky:** bypasses the pool. Calls `/request` with `strategy=Sticky` + `sessionId`, cached locally for 25 min — deliberately below the server's 30 min pin, so the pin never expires underneath the client.
- **Degradation:** on a 404 (`"No active proxies match the requested tags"`) or an unreachable API, **the pool is not emptied**. The stale snapshot keeps being served up to a `StaleCeilingMinutes` (~10 min) ceiling, then hard-fails. Without this, a 30-second network blip strands every scraper at once.

### Snapshot cache

Scrapers are batch processes that start and die. If the service is unreachable at startup, the process would not start at all — a real availability regression versus `webscraper.json`. So the snapshot is cached:

- **JSON file**, in the core package, zero dependencies. This is the only cache shipped in phase 2.
- **Redis is deferred** (decision, 2026-09-15). It was to be a second package acting as an L2 in
  front of `/request` for TAG's many workers, but only phase 3 needs it, and shipping one package
  is materially simpler. The `IProxySnapshotCache` seam ships now — it is needed anyway to fake the
  cache in tests — so Redis can be added later as a separate package with no breaking change to
  the core.
- **Plaintext, unencrypted, by explicit decision.** These are internal systems, and the status quo — plaintext provider credentials committed to source control — is strictly worse than a runtime-generated cache. Simplicity was chosen over DPAPI/encryption for this phase.
- The file lives in a runtime path (`%PROGRAMDATA%` or equivalent), **never** alongside the config JSON, and is added to `.gitignore`. The realistic risk is not disk access, it is the cache file being committed.
- TTL 24 h.

When Redis is added it doubles as an L2 in front of `/request`: memory → Redis → API. Ten TAG
workers would collapse from one refresh each every 90 s to roughly one refresh per tag-set.
Last-writer-wins is sufficient; the snapshot is idempotent. Until then each worker refreshes on its
own, which at phase-3 scale is a handful of extra calls per minute against a cheap endpoint.

---

## 3. Outcome classification

The question the SDK exists to answer consistently: **did the proxy fail, or did the portal fail?**

| Situation | Outcome | Rationale |
|---|---|---|
| `TaskCanceledException` + inner `TimeoutException`; `WebException.Status == Timeout` | `Timeout` | Proxy accepted the connection and went silent |
| HTTP 407; `ProxyNameResolutionFailure`; `ConnectFailure` against the proxy | `Failure` | Proxy broken or misauthenticated — not banned |
| HTTP 403 / 429 from the destination | `Banned` | The portal recognized and rejected the IP |
| HTTP 500 / 502 / 503 from the destination | **`Success`** | The tunnel worked. The portal is what fell over |
| HTTP 404 / 400 from the destination | **`Success`** | Same. The proxy did its job |

The last two rows are the reason to centralize this. Reporting `Failure` when a portal returns 503 punishes healthy proxies and can disable an entire pool because a portal had a bad afternoon.

### The content-detection hook

A portal returning **HTTP 200 with a captcha or robot-check page** cannot be detected by any generic rule. Mercado Público's `ViewAttachment` does exactly this. Two escape hatches:

```csharp
// Explicit, from a scraper that knows how to recognize the page
lease.Report(ProxyOutcome.Banned, "mercadopublico:robot-check");

// Declarative, per tag set, for the DelegatingHandler
options.ClassifyResponse = resp => LooksLikeRobotCheck(resp) ? ProxyOutcome.Banned : null;
```

This hook carries the most valuable signal the team has — the kind that currently costs a captcha solve to learn.

---

## 4. Feedback buffering and the batch endpoint

### Client side

- **`Report()` never blocks.** It enqueues onto a bounded queue (10,000 events). On overflow it drops and counts the drop. Feedback must never throttle a scrape.
- **Flush every 10 s or 50 events**, whichever comes first.
- **`Success` events are not sent.** `PolicyEvaluationService` ignores them, so transmitting them writes rows no decision ever reads. Success rate is measured locally against the `IMetrics` abstraction TAG already has, where it is free. A `SuccessSampling` option defaults to 0 for anyone who later wants the server-side series.
- **Final flush on shutdown**, ~3 s timeout. These are batch processes; losing the last batch means losing precisely the events describing the failure that ended the run.

### `POST /api/v1/proxies/feedback/batch`

Same `ApiKeyAuthenticationDefaults.ConsumerPolicyName` as the existing consumer endpoints.

```json
{ "events": [ { "proxyId": "…", "outcome": "Banned", "detail": "mercadopublico:robot-check" } ] }
```

Design decisions:

1. **No client timestamp.** `ProxyUsageEvent.Create` stamps `OccurredAtUtc = DateTime.UtcNow` internally and accepts no external value. With policy windows in minutes and a 10 s flush, server time is sufficient. Accepting client timestamps invites clock skew and manipulation for no benefit.
2. **One policy evaluation per distinct `proxyId`, not per event.** This is the entire point of batching: 200 events across 20 proxies drop from 200 evaluations to 20. Inserts go in a single `SaveChangesAsync`.
3. **Partial acceptance.** An event whose `proxyId` no longer exists (retired between request and flush) is discarded and the batch proceeds. Returning 404 for the whole batch would make the client retry forever. Response: `200 { accepted, rejected[] }`.
4. **Idempotency deferred.** `.WithIdempotency()` exists in BuildingBlocks, but its cache key is tenant-scoped while this consumer authenticates by API key against global entities — it would need verification that a tenant resolves. The damage from a duplicated batch is bounded: it inflates the failure count of proxies that were already failing. Tracked as follow-up. See also the "Long-batch timeout amplification" follow-up below: a slow batch makes a client-side timeout-and-retry more likely, which turns this bounded, unlikely-accident duplication into a predictable one. Phase 2 must not encode a blind retry.
5. **Batch cap: 200 events**, enforced by validator, consistent with the cap of 50 on `RequestProxies`.

The existing per-event `/feedback` endpoint is **unchanged**, for simple consumers that do not want buffering.

**Backend deliverables:** `ReportProxyFeedbackBatchCommand` in Contracts, handler, validator, endpoint, wired into `ProxiesModule.MapEndpoints()`.

---

## 5. Tag catalog in the SDK

The taxonomy already exists, seeded by `TagCategorySeedData` across five dimensions: `country`, `source`, `entityType`, `operationType`, `application`. Nothing needs defining — the pending work is tagging the proxies.

Exposed as `const string`, so predefined and custom tags are indistinguishable at the call site:

```csharp
public static class ProxyTags
{
    public static class Country     { public const string Chile = "country:cl"; /* … */ }
    public static class Source      { public const string ChileMercadoPublico = "source:chile - mercado publico"; /* … */ }
    public static class EntityType  { public const string Tender = "entitytype:tender"; /* … */ }
    public static class OperationType { public const string Attachments = "operationtype:attachments"; }
    public static class Application { public const string Tag = "application:tag"; /* … */ }

    public static string Of(string category, string value);   // dynamic composition, same normalization
}

var proxies = source.GetProxies(
    ProxyTags.Country.Chile,
    ProxyTags.OperationType.Attachments,
    "experimento:nuevo-portal");        // custom, no ceremony
```

An enum or a `ProxyTag` struct is explicitly rejected: any closed type demotes custom tags to second-class citizens (`ProxyTag.Custom("…")`), which defeats the purpose.

Three details:

1. **Constants encode the post-normalization form.** `TagCategory.Create` only trims the category name, but `Tag.Normalize` lowercases the whole composed string — so `entityType` + `Tender` becomes `entitytype:tender`. `GetProxies` applies the same normalization to whatever is passed, so `"entityType:Tender"` and `"entitytype:tender"` resolve identically.
2. **Drift test against the seed.** The SDK targets `netstandard2.0` and cannot reference `Modules.Proxies`, so the constants are hand-maintained. A test in `Proxies.Tests` (which sees both) compares the SDK's constant set against `TagCategorySeedData.Categories` and fails when a seed value is added without its constant.
3. **`Attachments` belongs to `operationType` only.** The seed currently carries it under
   `entityType` as well; that duplicate is a mistake and is removed (see §5.1). An attachment run is
   the combination `entitytype:tender` + `operationtype:attachments`, not a single tag.
4. **`source` values contain spaces and dashes** — the real tag is `source:chile - mercado publico`. Left as-is: already seeded in QA and Production, and changing them forces a full retag. Slugifying is a separate task with migration cost.

Two observations from the seed:

- **`application` already anticipates this work**, carrying `TAG`, `PO-Legacy`, `QB-Legacy`, `QR-Legacy`, `AG-Legacy`, `SGL`, `TaskManager`, `POM`. This aligns with one API key per scraper: the key and the `application:*` tag identify the same actor.
- **`"QuoteAgreementHardwareStorare"` is a typo** for `Storage`, confirmed during review, and is
  corrected (see §5.1). The SDK constant carries the corrected value.

### 5.1 Correcting the seeded catalog

Two corrections were confirmed during review: remove `Attachments` from `entityType`, and fix
`QuoteAgreementHardwareStorare` to `QuoteAgreementHardwareStorage`.

**Editing `TagCategorySeedData.cs` alone does not fix QA or Production.**
`ProxiesDbInitializer.SeedTagCategoriesAsync` short-circuits on `if (await
dbContext.TagCategories.AnyAsync(...)) return;` — it seeds only into an empty catalog. The source
change therefore reaches fresh environments only; deployed ones need an explicit data correction.

That correction needs no migration: the `AddTagCategoryValue` / `RemoveTagCategoryValue` endpoints
already exist and are reachable from the admin UI. Three steps per environment (QA, Production):

1. Remove value `Attachments` from category `entityType`.
2. Remove value `QuoteAgreementHardwareStorare` from `entityType`; add `QuoteAgreementHardwareStorage`.
3. Verify no proxy is already tagged `entitytype:attachments` or
   `entitytype:quoteagreementhardwarestorare`. The catalog has **no foreign key** to `Tag` or
   `ProxyTagAssignment` by design, so removing a catalog value silently leaves any already-assigned
   tag in place — and such a tag would then match nothing anyone asks for. Re-tag those proxies if
   any exist.

Step 3 should be cheap right now, since proxy tagging has not started in earnest. It gets expensive
later, which is the argument for doing this before phase 3 rather than after.

---

## 6. Local reputation already in place

`Tender-Attachments` already persists per-proxy block rate to Redis with a 24 h TTL (`ProxyQuarantineConsecutiveBlocks`, `ProxyQuarantineMinutes`, `ProxyHealthTtlHours`), because each data point cost a captcha solve.

**It stays where it is.** The SDK ships generic in-memory quarantine; the attachments reputation keeps selecting via `PinProxy()` over the list the SDK now provides, and additionally reports to the service. Two layers with different purposes: the local one optimizes *this* run, the service one decides for the whole fleet. Merging them is a separate project.

---

## 7. Configuration

```json
"FsProxy": {
  "BaseAddress": "https://proxy-qa.falconsoft.cl",
  "Tags": [ "country:cl" ],
  "PoolSize": 50,                 // hard ceiling: RequestProxiesQueryValidator caps count at 50
  "RefreshSeconds": 90,
  "RefreshJitterPercent": 20,
  "StaleCeilingMinutes": 10,
  "QuarantineMinutes": 2,
  "FeedbackFlushSeconds": 10,
  "FeedbackBatchSize": 50,
  "SuccessSampling": 0,
  "Cache": { "Mode": "Redis", "TtlHours": 24 }
}
```

The **API key travels in an environment variable** (`FSPROXY_APIKEY`), never in the JSON — the only secret left once `webscraper.json` is emptied.

`ProxyClientOptions` must be constructible by hand (`new ProxyClientOptions { … }`), not only by binding: legacy processes may be on `app.config` with no `IConfiguration`. Binding and the DI extension are the net10 convenience path, not a requirement.

---

## 8. Packaging

- `src/Clients/FS.Proxy.Client/`, `TargetFrameworks=netstandard2.0;net10.0`.
- **`<TargetFramework></TargetFramework>` must be explicitly cleared in the csproj.** `src/Directory.Build.props` sets the singular property globally; with both set, MSBuild silently honours the singular and builds net10 only.
- **`<IsPackable>true</IsPackable>` explicitly** — the repo default is `false` under the template's source-ownership model.
- `AnalysisMode=AllEnabledByDefault` + `TreatWarningsAsErrors` + `Nullable=enable` apply to the `netstandard2.0` target too. Budget for it: missing nullable attributes, `System.Text.Json` needs a `PackageReference`.
- **One package in phase 2.** `FS.Proxy.Client` carries no dependency outside the BCL beyond
  `System.Text.Json` on the `netstandard2.0` target, and includes the file cache. A future
  `FS.Proxy.Client.Redis` (net10 only) would be the sole package pulling `StackExchange.Redis`, so
  legacy scrapers never inherit a dependency graph they did not ask for.
- Add a `/Clients/` folder to `src/FS.Proxy.slnx`.
- **Feed: the existing local folder feed** (decision, 2026-09-15). `NuGet.config` already declares
  `fsh-local` pointing at a directory, alongside `nuget.org`. Phase 2 packs and pushes there. Note
  the configured path is a developer's home directory, so it serves one machine — moving to a
  network share or a hosted feed (Azure Artifacts, GitHub Packages) is a prerequisite for anyone
  else consuming the package, and belongs to whoever sets up phase 3.
- SemVer, starting at `0.1.0-preview`.

---

## 9. Testing

**SDK** (`src/Tests/Proxy.Client.Tests/` — xUnit + Shouldly + NSubstitute, per repo convention):

- **The classification table, one `[Theory]` row per rule.** The highest-value test in the project: it is the rule that stops a Mercado Público 503 from taking down the pool.
- Rotation and quarantine: a `Banned` proxy is not served during cooldown; with *all* proxies quarantined, one is still returned rather than throwing.
- Degradation: `/request` 404 → stale snapshot keeps serving; past `StaleCeilingMinutes` → fails.
- Buffer: the bounded queue drops under pressure without blocking; flush triggers on both time and size.
- Cache: snapshot round-trip; an expired cache is not used.

**Backend** (`src/Tests/Proxies.Tests/`):

- Batch handler: with N events across M proxies, `IPolicyEvaluationService` is called exactly **M** times (`Received(M)`, not `Received(N)`).
- Validator: 200-event cap.
- Partial acceptance with a non-existent `proxyId`.
- Tag drift test against `TagCategorySeedData`.

**Integration** (`src/Tests/Integration.Tests/`): the batch endpoint under API-key auth.

Note: this fork has **no `.github/`** — the CI workflows described in `AGENTS.md` belong to the upstream template. Confirm `dotnet test src/FS.Proxy.slnx` picks up the multi-targeted project.

**Documentation lives in this repository.** `AGENTS.md` golden rule 10 points at the upstream Astro
docs repo (`github.com/fullstackhero/docs`); that rule belongs to the project this template was
derived from and does not apply here. SDK documentation and its changelog ship with the code.

---

## 10. Delivery phases

| # | Deliverable | Depends on |
|---|---|---|
| 0 | Catalog corrections (§5.1), source + QA + Production | — |
| 1 | `/feedback/batch` endpoint + contracts + tests | — |
| 2 | SDK core: level 0, pool, feedback, file cache, `ProxyTags`. Publish preview to the feed | 1 |
| 3 | TAG integration: `ProxyInfo.ProxyId`, `LoadWebScraperConfiguration`, `RenewProxy` reports, tags per sub-section | 2 |
| 4 | One legacy pilot scraper, then the rest | 2 |
| 5 | `DelegatingHandler` + documentation | 2 |

**Phases 3 and 4 are not work in this repository.** TAG and the legacy scrapers are separate
codebases; this repo holds only the backend and the two React apps. What ships from here is the
package plus an integration guide. Phase 5 was originally sequenced after phase 3 so the handler
could learn from a real integration; it is folded into phase 2 instead, because it is a few dozen
lines in the same package and splitting it would cost a second release cycle for no new
information.

Phase 1 is independent and mergeable on its own. Phases 3 and 4 run in parallel.

**Configuration work that blocks phase 3** (not code — start now):

1. **Create one API key per scraper** via `CreateApiClient`, in QA and Production.
2. **Tag the proxies** against the five-dimension catalog, *after* the phase 0 corrections land.
   Today's pools (`(root)`, `Tender-Attachments`, `PurchaseOrder`) become explicit tag combinations,
   and proxies are labelled in the admin UI. Phase 3 has nothing to point at until this is done.

---

## Resolved during review

1. **`Attachments` is `operationType` only.** The `entityType` duplicate is removed. An attachment
   run is the combination `entitytype:tender` + `operationtype:attachments` (§1 Level 1, §5.1).
2. **`QuoteAgreementHardwareStorare` is a typo** for `Storage` and is corrected (§5.1).
3. **Documentation lives in this repository.** Golden rule 10's external docs repo belongs to the
   upstream project, not this fork (§9).

## Follow-ups explicitly out of scope

- **Round-robin cursor race.** `RequestProxiesQueryHandler.ResolveRoundRobinAsync` performs a non-atomic `GetOrCreate` → `Remove` → `GetOrCreate`, which will lose increments under concurrency. Pre-existing; unaffected by this spec because the SDK fills pools with `Random`.
- **Idempotency on the batch endpoint** (see §4.4).
- **Slugifying `source` tag values** (see §5.3).
- **Merging local attachment reputation with the server-side policy engine** (see §6).
- A non-.NET consumer would revisit the forward-proxy sidecar approach.
- **Long-batch timeout amplification.** The batch handler loops `EvaluateAsync` over up to 200
  distinct proxies inline and sequentially; each is ~4 DB round-trips, and an
  `AutoDisableAndRenew` profile additionally makes a synchronous outbound HTTP call to the
  provider via `IProxyRenewalService.TriggerAsync`. Total work is no worse than the same events
  sent one at a time, but it is now concentrated behind a single request timeout. Combined with
  the deliberate absence of idempotency (§4 decision 4), a slow batch → client timeout → retry →
  duplicate inserts → inflated failure counts → more renewals → slower still. §4 decision 4
  reasons about duplication as an unlikely accident; this makes it a predictable consequence.
  Cheapest mitigations: cap *distinct proxies* per batch rather than only events; move policy
  evaluation to a Hangfire job; or bound the loop with a budget. Phase 2 must not encode a blind
  retry (cross-referenced from §4).
- **`ProxyUsageEvents` retention.** No pruning job exists anywhere in the module, and this branch
  exists specifically to raise that table's write rate by an order of magnitude. The policy query
  stays fast via its index, so this is a storage and backup-window concern, not correctness — but
  it is new, and a decision is needed before phase 3.
