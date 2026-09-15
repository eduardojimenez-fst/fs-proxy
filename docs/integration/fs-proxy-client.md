# Integrating `FS.Proxy.Client`

This is the guide phases 3 (TAG) and 4 (legacy .NET Framework 4.8 scrapers) work from. Those
integrations happen in **other repositories** — this repo ships the package and this document, and
nothing else. You should be able to do the whole integration from this file without opening the SDK's
source.

Design background, if you want the "why": `docs/superpowers/specs/2026-09-14-proxy-client-sdk-design.md`.
This guide cross-references it for anything already fully written up there (the follow-ups list in
particular) rather than repeating it.

## 1. Install

| | |
|---|---|
| Package id | `FS.Proxy.Client` |
| Version | `0.1.0-preview.1` |
| Target frameworks | `netstandard2.0` (legacy .NET Framework 4.8 scrapers) and `net10.0` (TAG, new code) |

Published to the `fsh-local` NuGet feed, a **folder feed on the developer machine that built it**
(`/Users/eduardo/dev/nuget-local` at the time of writing — check this repo's own `NuGet.config` for
the current value). That is a real constraint, not a formality:

> **The configured feed path is a developer's home directory. It serves exactly one machine.**
> A consuming repository (TAG, or a legacy scraper) checked out on any other machine — another
> developer's laptop, a build agent, a production host — cannot `dotnet restore` against it. Before
> phase 3 or phase 4 can consume this package outside the machine that built it, the feed needs to
> move to something reachable from all of them: a network share, an internal NuGet server (e.g.
> BaGet, Azure Artifacts, a GitHub Packages feed), or committing the `.nupkg` into that consumer's own
> local feed folder as an interim step. Treat this as a prerequisite of phase 3/4, not a detail to
> discover partway through.

Once a reachable feed exists, add it to the consuming repository's own `NuGet.config`:

```xml
<configuration>
  <packageSources>
    <add key="fsh-local" value="\\path\to\shared\feed-or-url" />
  </packageSources>
</configuration>
```

and reference the package:

```xml
<ItemGroup>
  <PackageReference Include="FS.Proxy.Client" Version="0.1.0-preview.1" />
</ItemGroup>
```

`0.1.0-preview.1` is a prerelease version; either pass `--prerelease` to tooling that filters it out
by default, or pin the exact version as above (recommended while this package is pre-1.0).

## 2. Configuration — `ProxyClientOptions`

Every knob lives on one type, `FSH.Proxy.Client.ProxyClientOptions`. It is a plain, constructible-by-hand
class (not `IOptions<T>`) precisely so a DI-less .NET Framework 4.8 scraper can build one directly —
configuration binding (§5, Level 2) is a convenience on top, never a requirement.

| Property | Type | Default | Notes |
|---|---|---|---|
| `BaseAddress` | `Uri?` | `null` (required) | Root of the Proxy Management Service, e.g. `https://proxy-qa.falconsoft.cl`. |
| `ApiKey` | `string?` | `null` (required) | **From an environment variable. Never from a config file** — see below. |
| `Tags` | `string[]` | `[]` | Default tag set used when `GetProxies()`/`Lease()` is called with no tags. |
| `PoolSize` | `int` | `50` | Proxies held locally per tag set. Hard ceiling of 50 — the service's `RequestProxiesQueryValidator` caps `count` there and silently truncates above it. `Validate()` throws outside `[1, 50]`. |
| `RefreshInterval` | `TimeSpan` | `00:01:30` (90 s) | Background refresh cadence per pool. |
| `RefreshJitterPercent` | `int` | `20` | Spread applied to `RefreshInterval` so many scrapers starting together don't refresh in lockstep. `Validate()` throws outside `[0, 100]`. |
| `StaleCeiling` | `TimeSpan` | `00:10:00` (10 min) | How long a stale snapshot keeps being served while the service is unreachable, before the pool fails loudly (empty list / `null` lease). |
| `Quarantine` | `TimeSpan` | `00:02:00` (2 min) | How long a locally-failed proxy is set aside before being offered again. |
| `FeedbackFlushInterval` | `TimeSpan` | `00:00:10` (10 s) | How often buffered outcomes are flushed to the service. |
| `FeedbackBatchSize` | `int` | `50` | Events per flush. The service's batch endpoint caps a submission at 200. `Validate()` throws outside `[1, 200]`. |
| `FeedbackQueueCapacity` | `int` | `10,000` | Bound on the in-memory feedback queue; overflow drops the event rather than blocking the scrape. |
| `SuccessSampling` | `double` | `0` | Fraction of `Success` outcomes actually transmitted, `[0, 1]`. Defaults to 0 — see §7. `Validate()` throws outside `[0, 1]`. |
| `SnapshotCache` | `IProxySnapshotCache?` | `null` (no fallback) | Opt-in local fallback for the last known-good proxy set (`FileSnapshotCache`, shown in §3). Stores credentials in plaintext by deliberate decision — see the note below the code sample in §3 before wiring one up. |

Call `options.Validate()` before use if you build `ProxyClientOptions` by hand (Levels 0/1); it throws
`InvalidOperationException` naming the first violated constraint. `AddFsProxyClient` (Level 2) calls it
for you after binding.

### `ApiKey`: environment variable, never a config file

This is a hard rule, not a style preference: `webscraper.json` — the file this SDK replaces — held
BrightData/WebShare credentials in plaintext, in source control. Do not recreate that mistake one
level up by writing the proxy-service API key into `appsettings.json`, `webscraper.json`, or any other
file that gets committed.

- **Level 0 / Level 1** (hand-built `ProxyClientOptions`): read it directly —
  `Environment.GetEnvironmentVariable("FSPROXY_API_KEY")`.
- **Level 2** (`AddFsProxyClient(configuration.GetSection("FsProxy"))`): the section is bound with the
  ordinary ASP.NET Core configuration pipeline, which already layers environment variables over
  `appsettings.json` by default. Put every other `FsProxy:*` setting in `appsettings.json` and supply
  **only** `ApiKey` through the environment, using the double-underscore section syntax:
  `FsProxy__ApiKey=<key>`. Never add an `"ApiKey"` line to the JSON file itself, even a placeholder —
  it is exactly the kind of value someone copy-pastes a real one into later.

## 3. Level 0 — legacy .NET Framework 4.8 scrapers on `WebRequest`

The whole surface a Level-0 caller needs is four calls: `ProxySource.Initialize`, `GetProxies`,
`ToWebProxy()`, and `Report`. No DI container, no `HttpClient`, no async requirement beyond the
one-time startup warmup.

```csharp
using System;
using System.IO;
using System.Net;
using FSH.Proxy.Client;
using FSH.Proxy.Client.Caching;

public static class ProxyBootstrap
{
    /// <summary>Call once, at process startup, before any scraping code runs.</summary>
    public static void Initialize()
    {
        var options = new ProxyClientOptions
        {
            BaseAddress = new Uri("https://proxy-qa.falconsoft.cl"),
            ApiKey = Environment.GetEnvironmentVariable("FSPROXY_API_KEY"),
            Tags = new[] { ProxyTags.Country.Chile, ProxyTags.Source.ChileMercadoPublico },

            // Optional: survives a startup outage of the proxy service by serving the last known-good
            // set from disk. Point this at a runtime/data directory, never next to configuration —
            // see the note just below this sample for why.
            SnapshotCache = new FileSnapshotCache(
                Path.Combine(Environment.GetEnvironmentVariable("ProgramData") ?? Path.GetTempPath(), "fsproxy-cache"),
                TimeSpan.FromHours(24)),
        };

        // Throws with a specific message if a required setting is missing/out of range — fail at
        // startup, not on the first scrape.
        options.Validate();

        ProxySource.Initialize(options);

        // The ONE blocking call in this whole SDK, and it's deliberate: WarmupAsync fills the default
        // tag set's pool and starts its background refresh timer. Call it once, synchronously, at
        // startup — everything after this is synchronous and does no I/O.
        ProxySource.Instance.WarmupAsync().GetAwaiter().GetResult();
    }
}
```

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using FSH.Proxy.Client;

public static class LegacyScraper
{
    public static string DownloadPage(string url)
    {
        IReadOnlyList<ProxyEndpoint> proxies = ProxySource.Instance.GetProxies(ProxyTags.Country.Chile);
        if (proxies.Count == 0)
        {
            // No proxy currently available for these tags (never warmed, or past StaleCeiling — see §7).
            throw new InvalidOperationException("No proxies available for country:cl.");
        }

        ProxyEndpoint proxy = proxies[new Random().Next(proxies.Count)];

        var request = (HttpWebRequest)WebRequest.Create(url);
        request.Proxy = proxy.ToWebProxy();
        request.Timeout = 90_000;

        try
        {
            using HttpWebResponse response = (HttpWebResponse)request.GetResponse();
            using var reader = new StreamReader(response.GetResponseStream());
            string body = reader.ReadToEnd();

            // The destination answered — even a 4xx/5xx from a WebException below counts as the proxy
            // having done its job. Report Success only via FromStatusCode/FromException so the
            // Banned/Timeout/Failure distinction stays centralized; never hand-roll it here.
            ProxySource.Instance.Report(proxy.Id, ProxyOutcomeClassifier.FromStatusCode(response.StatusCode));
            return body;
        }
        catch (WebException ex)
        {
            // WebException is what WebRequest/HttpWebRequest throws — not HttpRequestException, which
            // is the HttpClient world. ProxyOutcomeClassifier.FromException already special-cases it
            // (timeout vs. protocol error vs. everything else).
            ProxySource.Instance.Report(proxy.Id, ProxyOutcomeClassifier.FromException(ex), ex.Message);
            throw;
        }
    }
}
```

**`SnapshotCache` stores proxy credentials in plaintext, by deliberate decision** (it replaces
`webscraper.json`, which did the same thing, in source control — a runtime-only plaintext file is
strictly better than that). What is required of the directory you point it at:

- It must be a runtime/data directory the scraper's own account can write to — never colocated with
  `appsettings.json`/`webscraper.json`, and never a path under source control.
- `FileSnapshotCache` names each file after a SHA-256 hash of its cache key (e.g. `3f2a…c1.json`), not
  a fixed suffix — so a single glob line in `.gitignore` cannot reliably target its output by filename.
  This repo's own `.gitignore` carries a `*.fsproxy-snapshot.json` pattern as a defense-in-depth
  backstop, but the real safety net is dedicating an **entire directory** to this cache (as in the
  sample above) and excluding that whole directory in the consuming repository's own `.gitignore` —
  do not rely on the filename pattern alone.

Notes:

- `GetProxies`/`Report` never block and never do I/O — safe to call from code that is itself
  synchronous or already inside a `.Result`/`.Wait()` chain, which the legacy scrapers are full of.
- `ProxyEndpoint.ToWebProxy()` carries the right scheme (`http`, `https`, or `socks5`) on the returned
  `WebProxy.Address` regardless of target framework — but see §8 for why a `socks5` scheme still will
  not actually tunnel when assigned to `WebRequest.Proxy` on this target.
- Report **something** for every attempt that went through a leased proxy, success or failure — even
  though `Success` is not transmitted to the server by default (§7), `Report` also drives the
  process-local quarantine, which only fires from a call you make.

## 4. Level 1 — TAG's `WebScraper`

Read directly from
`FST.TAG/src/Core/Application.Common/Common/Scraping/WebScraper/WebScraper.cs` and
`WebScraperSettings.cs` (branch `develop`, net10.0) on 2026-09-15. Line numbers below are a snapshot,
not a promise — that file is under active development and will have moved by the time you branch; use
them to find your bearings, then confirm against the current file (`grep -n "RenewProxy\(\)" WebScraper.cs`
finds all fourteen call sites regardless of drift). The spec
(`docs/superpowers/specs/2026-09-14-proxy-client-sdk-design.md`, §1 "Level 1") describes change 1
below; changes 2–4 were found only by reading the real file and are not written up anywhere else.

Everything here assumes `FSH.Proxy.Client.ProxySource.Initialize(options)` has already run once, at
TAG's own process startup, exactly as in §3 above — `WebScraper` does not take an `IProxySource`
constructor parameter in what follows, to avoid rippling a new required parameter through every one of
its four existing constructors and every call site across TAG. Reach it via `ProxySource.Instance`,
after adding `using FSH.Proxy.Client;` to `WebScraper.cs` (it has no dependency on that namespace
today). (If TAG's composition root already threads services through constructors cleanly, injecting
`IProxySource` explicitly is the better long-term shape — just know it is a wider change than the four
below.)

### Change 1 — `LoadWebScraperConfiguration()` (around line 673)

Today:

```csharp
private void LoadWebScraperConfiguration()
{
    if (_settings is null) throw new Exception("WebScraper Settings is not configured.");
    UseProxy = _settings.UseProxy;
    Proxies = _settings.Proxies;
}
```

Becomes a projection over `IProxySource.GetProxies(tags)` instead of the hardcoded list:

```csharp
private void LoadWebScraperConfiguration()
{
    if (_settings is null) throw new Exception("WebScraper Settings is not configured.");
    UseProxy = _settings.UseProxy;

    Proxies = _proxySource
        .GetProxies(_settings.ProxyTags)
        .Select(p => new ProxyInfo
        {
            ProxyId = p.Id,
            Ip = p.Host,
            Port = p.Port,
            User = p.Username,
            Password = p.Password,
        })
        .ToList();
}
```

with a private field `private readonly IProxySource _proxySource = ProxySource.Instance;`
and `WebScraperSettings.ProxyTags` (a new `string[]` property) replacing the per-section `Proxies`
list that `Resolve()` currently reads. An attachment-scrape sub-section becomes:

```json
"Tender-Attachments": { "ProxyTags": [ "entitytype:tender", "operationtype:attachments", "country:cl" ] },
"PurchaseOrder":      { "ProxyTags": [ "entitytype:purchaseorder", "country:cl" ] }
```

— see §6 for why `Tender-Attachments` is two tags ANDed together, not one.

Side effect worth calling out to whoever reviews this in TAG: `webscraper.json` stops carrying
BrightData/WebShare credentials in plaintext. The only secret left in that file's neighborhood is the
proxy-service API key, and per §2 that never goes in the file at all.

### Change 2 — `ProxyInfo` gains `Guid ProxyId` (`WebScraperSettings.cs`)

```csharp
public class ProxyInfo
{
    public int Id { get; set; }        // unchanged: an index into the JSON file, kept for ProxyKey/logs
    public Guid ProxyId { get; set; }  // NEW — the service's identifier; required to call Report(...)
    public string Ip { get; set; }
    public int Port { get; set; }
    public string User { get; set; }
    public string Password { get; set; }
}
```

`Id` cannot be repurposed for this: it is an `int` position in a JSON array, meaningless to the proxy
service, which addresses every proxy by `Guid` (`ProxyEndpoint.Id`). Keep both — `Id`/`ProxyKey`
(`ip-port-user`) stay exactly as useful for logs and the existing local quarantine as they always were.

### Change 3 — `GetProxy()` must retain the proxy it selected (around line 219)

**As of this reading, `WebScraper.cs` already declares a public `CurrentProxy` property** (set inside
`CreateHttpClient()`, immediately after it calls `GetProxy()`). That property happens to hold the right
value at the right moment for reporting — but only because of where it is set, not because `GetProxy()`
itself retains anything. That is fragile: `CreateHttpClient()`'s whole job is to build the *next*
`HttpClient`, and the moment `RenewProxy()` (change 4) calls it, `CurrentProxy` gets overwritten with
the *new* proxy — before you have a chance to report the one that just failed, unless you capture it
first.

Rather than depend on that ordering, retain the selection inside `GetProxy()` itself, next to `ProxyKey`:

```csharp
private ProxyInfo _currentProxy;

public ProxyInfo GetProxy()
{
    ProxyInfo result = null;
    if (!UseProxy || Proxies == null || Proxies.Count == 0)
    {
        return null;
    }

    // ... existing Random/Sequential/Other selection logic, unchanged ...

    ProxyKey = $"{result?.Ip}-{result?.Port}-{result?.User}" ?? "no-proxy";
    _currentProxy = result;   // NEW — retained so RenewProxy() has something to report against
    _logger.LogDebug("WebScraper selected proxy {ProxyId} ({ProxyKey}).", result?.Id, ProxyKey);
    return result;
}
```

`_currentProxy` and the existing `CurrentProxy` property must agree — either have `CurrentProxy` read
from `_currentProxy`, or leave `CreateHttpClient()`'s assignment as a (now redundant but harmless)
extra write. What matters is that `RenewProxy()` (change 4) has a field it owns and controls the timing
of, independent of `CreateHttpClient()`'s side effects.

### Change 4 — `RenewProxy()` must take the outcome (around line 283), and all fourteen call sites

Today, `RenewProxy()` is parameterless and reports nothing:

```csharp
private void RenewProxy()
{
    if (UseProxy && Proxies != null && Proxies.Count != 0)
        _httpClient = CreateHttpClient();
}
```

It becomes:

```csharp
private void RenewProxy(ProxyOutcome outcome, string detail)
{
    // Report BEFORE CreateHttpClient() runs — CreateHttpClient() calls GetProxy() again and
    // overwrites _currentProxy/CurrentProxy with the NEXT proxy (see change 3).
    if (_currentProxy is not null)
    {
        _proxySource.Report(_currentProxy.ProxyId, outcome, detail);
    }

    if (UseProxy && Proxies != null && Proxies.Count != 0)
        _httpClient = CreateHttpClient();
}
```

`RenewProxy()` is called from **fourteen** `catch` blocks, all following one of exactly two shapes.
The mapping is mechanical, because the call sites already split by exception family:

```csharp
catch (HttpRequestException ex)
{
    _logger.LogWarning($"Error al hacer la solicitud HTTP: RequestUrl {url}, Response StatusCode:{ex.StatusCode}, Message: {ex.Message}");

    RenewProxy(ProxyOutcomeClassifier.FromException(ex), ex.Message);
    throw ex;
}
catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
{
    _logger.LogWarning($"Timeout al hacer la solicitud HTTP: RequestUrl {url}, tras {_httpClient.Timeout.TotalSeconds:F0}s. Renovando proxy.");

    RenewProxy(ProxyOutcome.Timeout, "Request timed out.");
    throw;
}
```

As read on 2026-09-15, the fourteen `RenewProxy()` calls sit at (approximately) lines 202, 214, 760,
772, 855, 899, 911, 975, 1028, 1040, 1086, 1098, 1127, and 1139, across `CatchHttpExceptionFor<T>`,
`GetByteAsync`, `GetByteAsyncV2`, `GetStringAsync`, `PostByteAsync`, and both `PostStringAsync`
overloads. Most of these methods catch both exception shapes — `HttpRequestException` and the
timed-out flavor of `TaskCanceledException` — giving twelve of the fourteen sites as six matched
pairs; `GetByteAsyncV2` (line 855) and `PostByteAsync` (line 975) each catch only
`HttpRequestException`, with no separate timeout arm to touch. That asymmetry is fine — it means two
sites, not fourteen, get only the `HttpRequestException` half of the change below. Every
`HttpRequestException` catch gets the same one-line swap, and every `TaskCanceledException`-timeout
catch gets the other:

```csharp
// Every "catch (HttpRequestException ex)" arm:
RenewProxy(ProxyOutcomeClassifier.FromException(ex), ex.Message);

// Every "catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)" arm:
RenewProxy(ProxyOutcome.Timeout, "Request timed out.");
```

Nothing else in any of these `catch` blocks needs to change.

**Why not leave `RenewProxy()` parameterless and report a blanket `Failure` instead?** Because that
erases exactly the distinction the policy engine exists to act on. `ProxyOutcomeClassifier` (already
shipped in this SDK) tells `Banned` (the destination rejected this IP — a different proxy may work)
apart from `Timeout` (the proxy went silent — also usually a proxy problem) apart from `Failure`
(this proxy is broken or misauthenticated). Reporting every one of the fourteen sites as `Failure`
would still call `Report`, still quarantine locally, still show up on the dashboard — but every signal
the server-side `PolicyEvaluationService` uses to decide *why* a proxy is bad would collapse into one
bucket. A proxy that is merely IP-banned on one portal (which another portal might still accept) looks
identical, in that data, to one whose credentials are wrong everywhere. The four-changes version above
costs two call sites' worth of extra typing per catch block and keeps the signal the SDK exists to
produce.

### `PinProxy()` — a flag, not a fix

`PinProxy(ProxyInfo proxy)` (around line 265) replaces `Proxies` wholesale with a single-element list
containing the pinned proxy:

```csharp
public void PinProxy(ProxyInfo proxy)
{
    if (proxy is null) return;
    UseProxy = true;
    Proxies = new List<ProxyInfo> { proxy };
    _currentProxyIndex = 0;
    // ...
}
```

Once a `WebScraper` instance is pinned this way, it never sees another `GetProxies()`/pool refresh
result again — it is stuck on that one proxy (or whatever `RenewProxy()` cycles back to within a
one-element list, which is the same proxy) until the instance is discarded. Today's caller is
attachment sessions choosing a proxy by local reputation, and staying pinned for the life of that
session is exactly what they want. This is **not** something phase 3 needs to fix, but it must be a
conscious decision when phase 3 wires `PinProxy` up to the new tag-based pools — a caller reaching for
`PinProxy` without noticing this will quietly stop benefiting from pool refresh/rotation for the rest
of that scraper's life, which is a very different failure mode from "this proxy got quarantined and
another one took over" that the rest of the SDK provides everywhere else.

## 5. Level 2 — new code (`AddFsProxyClient` + `AddFsProxyRotation`)

For code written from here on that does not go through `WebScraper` — net10 only.

```csharp
// Program.cs / composition root
using FSH.Proxy.Client;                       // ProxyTags
using FSH.Proxy.Client.DependencyInjection;    // AddFsProxyClient / AddFsProxyRotation

builder.Services.AddFsProxyClient(builder.Configuration.GetSection("FsProxy"));

builder.Services.AddHttpClient("mercadopublico")
    .AddFsProxyRotation(ProxyTags.Country.Chile, ProxyTags.Source.ChileMercadoPublico);
```

```json
// appsettings.json — everything EXCEPT ApiKey; see §2 for where ApiKey comes from
{
  "FsProxy": {
    "BaseAddress": "https://proxy-qa.falconsoft.cl",
    "Tags": [ "country:cl" ],
    "PoolSize": 50,
    "RefreshInterval": "00:01:30",
    "StaleCeiling": "00:10:00"
  }
}
```

`AddFsProxyClient` binds a `ProxyClientOptions` from the given section, validates it, and registers a
singleton `IProxySource` — call `WarmupAsync()` yourself (e.g. from an `IHostedService`) if you want
the first request to hit a warm pool rather than an empty one that fills on first use.
`AddFsProxyRotation(params string[] tags)` adds `FsProxyRotationHandler` to a named/typed
`HttpClient`'s pipeline: it leases a proxy from the container's `IProxySource` per request, sends
through it, classifies the response/exception, and reports the outcome automatically — nothing else to
call.

**Read §8 before wiring this onto a client that also carries Polly, correlation-id propagation, or
relies on `IHttpClientFactory`'s own request logging** — `AddFsProxyRotation`'s handler has a real,
documented interaction with the rest of that pipeline.

## 6. The tag taxonomy

`FSH.Proxy.Client.ProxyTags` exposes the service's seeded catalog as `const string`, grouped into five
categories:

| Category | Example |
|---|---|
| `Country` | `ProxyTags.Country.Chile` → `"country:cl"` |
| `Source` | `ProxyTags.Source.ChileMercadoPublico` → `"source:chile - mercado publico"` |
| `EntityType` | `ProxyTags.EntityType.Tender` → `"entitytype:tender"` |
| `OperationType` | `ProxyTags.OperationType.Attachments` → `"operationtype:attachments"` |
| `Application` | `ProxyTags.Application.Tag` → `"application:tag"` |

Tags are **ANDed** by the service when resolving a pool — `GetProxies(tag1, tag2)` asks for proxies
matching *every* tag given, not any. An attachment scrape is therefore the combination of two tags, not
one dedicated tag of its own:

```csharp
_proxySource.GetProxies(ProxyTags.EntityType.Tender, ProxyTags.OperationType.Attachments)
```

Custom tags are **plain strings** — there is no closed enum or wrapper type to go through:

```csharp
_proxySource.GetProxies(ProxyTags.Country.Chile, "experimento:foo")
```

`ProxyTags.Normalize` (trim + lowercase) is applied to every tag on the way out, so it does not matter
whether you pass `"Country:CL"` or `"country:cl"` — both address the same pool.

## 7. What to expect when things break

- **Stale-snapshot serving.** If the service returns an empty result (its own 404, "no active proxy
  matches these tags") or is unreachable, a pool does **not** empty out — it keeps serving its last
  successful snapshot.
- **The stale ceiling.** That tolerance is not indefinite: past `StaleCeiling` (default 10 minutes)
  since the last successful fetch, the pool stops serving anything — `GetProxies` returns an empty
  list, `Lease` returns `null`. A 30-second network blip is absorbed; a service outage past 10 minutes
  is a hard failure, by design (silently serving hour-old dead proxies is worse than failing loudly).
- **Local quarantine.** A proxy reported as anything other than `Success` is set aside in-process for
  `Quarantine` (default 2 minutes) and not offered again until it lapses — this happens immediately,
  client-side, without waiting for a round trip to the server's policy engine. If *every* proxy in a
  pool is currently quarantined, the quarantine is ignored and the least-bad one is handed out anyway —
  a questionable proxy beats failing the scrape outright.
- **`Success` is not transmitted to the server by default.** `SuccessSampling` defaults to `0`; the
  service's `PolicyEvaluationService` only ever counts non-`Success` events when deciding whether to
  disable a proxy, so transmitting successes at the default setting would write rows no decision ever
  reads. `Report(proxyId, ProxyOutcome.Success)` is still cheap and safe to call on every successful
  attempt — it is simply dropped before it reaches the network unless `SuccessSampling` is raised above
  `0`.

## 8. What is NOT in this release

- **The Redis L2 cache is deferred.** Only the JSON file cache (`FileSnapshotCache`, `IProxySnapshotCache`)
  ships in this package. `IProxySnapshotCache` is the seam that lets a Redis-backed implementation land
  later, as a separate package, without a breaking change here — but there is no L2 cache to point ten
  TAG workers at yet; each will refresh its own pool independently.
- **No idempotency on `/feedback/batch`.** A long batch (up to 200 events, each doing several DB
  round-trips server-side) sitting behind one client-side request timeout, followed by a blind retry of
  the whole batch, can duplicate events server-side — inflating the failure count of proxies that were
  already failing. **Phase 3's retry policy must not be blind** for this endpoint: do not wrap
  `RequestFeedbackAsync`/the batch call in an automatic retry-on-timeout without first checking
  whether the batch actually landed, or accepting the (bounded, but real) risk of double-counting.
  See spec §4.4 and the "Long-batch timeout amplification" follow-up for the server-side half of this.
- **`AddFsProxyRotation`'s handler terminates the HTTP pipeline.** Whenever it actually leases a proxy,
  `FsProxyRotationHandler` sends through its own cached per-proxy `HttpMessageInvoker` directly — it
  does not call `base.SendAsync`. Any `DelegatingHandler` registered *after* `AddFsProxyRotation` on
  the same `HttpClient` is silently skipped for that request, and so is
  `IHttpClientFactory`'s own innermost `LoggingHttpMessageHandler`. Concretely: Polly retry/circuit-breaker
  handlers, correlation-id propagation handlers, and the framework's own request/response/elapsed logs
  all stop running the moment a proxy is in play. This is recorded as a follow-up in the spec (a
  pipeline-preserving redesign exists and is the recommended direction), not fixed in this release —
  know it before you stack this handler under anything that assumes the pipeline always runs.
- **SOCKS5 cannot work through `HttpWebRequest`/`WebRequest.Proxy` on `netstandard2.0`**, regardless of
  what `ProxyEndpoint.Protocol` says. `ToWebProxy()` still carries the correct `socks5://` scheme on the
  returned address on every target — a caller handing that off to its own SOCKS-capable client still
  gets the right value — but assigning it straight to `WebRequest.Proxy` in a .NET Framework 4.8
  scraper has nothing underneath that understands that scheme. If a legacy scraper is tagged into a
  pool that can return SOCKS5 proxies, either exclude that tag combination for Level-0 callers or
  filter `Protocol != ProxyProtocol.Socks5` before use.
