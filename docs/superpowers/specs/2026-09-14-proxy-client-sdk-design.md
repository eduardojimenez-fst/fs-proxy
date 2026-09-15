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
"Tender-Attachments": { "ProxyTags": [ "entitytype:attachments", "country:cl" ] },
"PurchaseOrder":      { "ProxyTags": [ "entitytype:purchaseorder", "country:cl" ] }
```

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

- **Redis** for TAG (`FS.Proxy.Client.Redis`, net10 only).
- **JSON file** for legacy (core package, zero dependencies).
- **Plaintext, unencrypted, by explicit decision.** These are internal systems, and the status quo — plaintext provider credentials committed to source control — is strictly worse than a runtime-generated cache. Simplicity was chosen over DPAPI/encryption for this phase.
- The file lives in a runtime path (`%PROGRAMDATA%` or equivalent), **never** alongside the config JSON, and is added to `.gitignore`. The realistic risk is not disk access, it is the cache file being committed.
- TTL 24 h.

**Redis doubles as an L2 in front of `/request`**: memory → Redis → API. Ten TAG workers collapse from one refresh each every 90 s to roughly one refresh per tag-set. Last-writer-wins is sufficient; the snapshot is idempotent.

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
4. **Idempotency deferred.** `.WithIdempotency()` exists in BuildingBlocks, but its cache key is tenant-scoped while this consumer authenticates by API key against global entities — it would need verification that a tenant resolves. The damage from a duplicated batch is bounded: it inflates the failure count of proxies that were already failing. Tracked as follow-up.
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
    ProxyTags.EntityType.Attachments,
    "experimento:nuevo-portal");        // custom, no ceremony
```

An enum or a `ProxyTag` struct is explicitly rejected: any closed type demotes custom tags to second-class citizens (`ProxyTag.Custom("…")`), which defeats the purpose.

Three details:

1. **Constants encode the post-normalization form.** `TagCategory.Create` only trims the category name, but `Tag.Normalize` lowercases the whole composed string — so `entityType` + `Tender` becomes `entitytype:tender`. `GetProxies` applies the same normalization to whatever is passed, so `"entityType:Tender"` and `"entitytype:tender"` resolve identically.
2. **Drift test against the seed.** The SDK targets `netstandard2.0` and cannot reference `Modules.Proxies`, so the constants are hand-maintained. A test in `Proxies.Tests` (which sees both) compares the SDK's constant set against `TagCategorySeedData.Categories` and fails when a seed value is added without its constant.
3. **`Attachments` is seeded under two categories** — both `entityType` and `operationType` carry
   the value. Whichever is chosen must be used consistently by the tagging work in phase 3 and by
   the SDK constants, or a pool will silently resolve to zero proxies. This spec assumes
   `entitytype:attachments`; see open questions.
4. **`source` values contain spaces and dashes** — the real tag is `source:chile - mercado publico`. Left as-is: already seeded in QA and Production, and changing them forces a full retag. Slugifying is a separate task with migration cost.

Two observations from the seed:

- **`application` already anticipates this work**, carrying `TAG`, `PO-Legacy`, `QB-Legacy`, `QR-Legacy`, `AG-Legacy`, `SGL`, `TaskManager`, `POM`. This aligns with one API key per scraper: the key and the `application:*` tag identify the same actor.
- **`"QuoteAgreementHardwareStorare"` is a typo** for `Storage`. The SDK constants mirror the real value, typo included, or they stop matching. Fixing it is another retag — **open question, listed below.**

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
- **Two packages.** `FS.Proxy.Client` (core) carries zero non-BCL dependencies and includes the file cache. `FS.Proxy.Client.Redis` (net10 only) is the sole package pulling `StackExchange.Redis`. Legacy scrapers do not inherit a dependency graph they never asked for.
- Add a `/Clients/` folder to `src/FS.Proxy.slnx`.
- Published to the internal private feed, SemVer.

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

---

## 10. Delivery phases

| # | Deliverable | Depends on |
|---|---|---|
| 1 | `/feedback/batch` endpoint + contracts + tests | — |
| 2 | SDK core: level 0, pool, feedback, file cache, `ProxyTags`. Publish preview to the feed | 1 |
| 3 | TAG integration: `ProxyInfo.ProxyId`, `LoadWebScraperConfiguration`, `RenewProxy` reports, tags per sub-section, Redis cache | 2 |
| 4 | One legacy pilot scraper, then the rest | 2 |
| 5 | `DelegatingHandler` + documentation | 3 |

Phase 1 is independent and mergeable on its own. Phases 3 and 4 run in parallel.

**Configuration work that blocks phase 3** (not code — start now):

1. **Create one API key per scraper** via `CreateApiClient`, in QA and Production.
2. **Tag the proxies** against the existing five-dimension catalog. Today's pools (`(root)`, `Tender-Attachments`, `PurchaseOrder`) must become explicit tag sets, and proxies must be labelled in the admin UI. Phase 3 has nothing to point at until this is done.

---

## Open questions

1. **Seed typo `"QuoteAgreementHardwareStorare"`** — fix it (costs a retag of any proxy already carrying it) or keep it and mirror the typo in the SDK constants?
2. **`Attachments` under two categories.** The seed carries it in both `entityType` and
   `operationType`. Pick one as canonical for proxy tagging (this spec assumes `entityType`), or
   remove the duplicate from the catalog.
3. **Documentation target.** `AGENTS.md` golden rule 10 points at the upstream Astro docs repo (`github.com/fullstackhero/docs`) plus a changelog entry. Confirm whether that applies to this fork or whether the SDK documentation lives in this repository.

## Follow-ups explicitly out of scope

- **Round-robin cursor race.** `RequestProxiesQueryHandler.ResolveRoundRobinAsync` performs a non-atomic `GetOrCreate` → `Remove` → `GetOrCreate`, which will lose increments under concurrency. Pre-existing; unaffected by this spec because the SDK fills pools with `Random`.
- **Idempotency on the batch endpoint** (see §4.4).
- **Slugifying `source` tag values** (see §5.3).
- **Merging local attachment reputation with the server-side policy engine** (see §6).
- A non-.NET consumer would revisit the forward-proxy sidecar approach.
