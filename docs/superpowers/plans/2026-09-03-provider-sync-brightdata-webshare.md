# Provider Sync: BrightData + WebShare Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Fix the Proxies module's BrightData and WebShare provider adapters to match real, verified API behavior — BrightData's zone-based model (validated against a real production zone) and WebShare's per-IP list (validated against a real production account) — and add a shared `Country`/`ProviderGrouping` data model so synced proxies carry provider-reported geolocation and category, independent of the existing free-form Tag system.

**Architecture:** No new modules or interfaces. `IProxyProviderAdapter` stays unchanged; `ProviderProxyRecord` gains two nullable fields threaded through `ProviderAccountSyncService` into the `Proxy` domain entity (new columns, one EF migration). `BrightDataAdapter` is rewritten around a 2-call algorithm (`GET /zone` for password/plan, then `GET /zone/ips` branching on 200 vs 400) instead of its old, unverified single-call guess. `WebShareAdapter` gains real pagination and country mapping. Backend DTO/query and the admin Proxies list page surface the two new fields; the Provider Account dialog's credentials placeholder becomes provider-aware.

**Tech Stack:** .NET 10, EF Core 10 / PostgreSQL, xUnit + Shouldly + NSubstitute, React 19 + TanStack Query v5 + Zod, Playwright.

**Spec:** `docs/superpowers/specs/2026-09-03-provider-sync-brightdata-webshare-design.md`

## Global Constraints

- Mediator handlers stay `public sealed`, return `ValueTask<T>`, `.ConfigureAwait(false)` every await.
- Structured logging only — no string interpolation in log messages (none of these tasks add logging, but don't introduce any).
- Propagate `CancellationToken` into every EF/IO call.
- Every paginated query keeps its validator (`ListProxiesQueryValidator` already exists — extend, don't remove).
- Frontend: pass per-call data through `mutate(arg)`/query params, never via state a callback closes over.
- `Country` values are provider-reported and case-inconsistent across providers (BrightData: lowercase ISO2 via MaxMind, e.g. `"cl"`; WebShare: uppercase ISO2, e.g. `"CL"`) — any comparison (filtering) must be case-insensitive.
- `Country`/`ProviderGrouping` are informational/sync-only fields. They must never be written to by anything except a provider sync — they are not user-editable and are independent of the Tag system (a proxy's Tags represent *usage*, e.g. "which market we use it for"; `Country` represents the *provider's own reported geolocation* — these can legitimately disagree).
- Test fixtures use realistic BrightData/WebShare response *shapes* (field names, nesting, the 400 "Wrong zone plan" status) but placeholder-style values (e.g. `"cust1"`, `"zone-pass"`) — never real account identifiers, tokens, or passwords, matching the existing test files' own convention.
- Build runs with `TreatWarningsAsErrors` — warnings fail the build.

---

### Task 1: Add `Country`/`ProviderGrouping` to the `Proxy` domain entity + EF migration

**Files:**
- Modify: `src/Modules/Proxies/Modules.Proxies/Domain/Proxy.cs`
- Modify: `src/Modules/Proxies/Modules.Proxies/Data/Configurations/ProxyConfiguration.cs`
- Test: `src/Tests/Proxies.Tests/Domain/ProxyTests.cs`
- Create (generated): `src/Host/FS.Proxy.Migrations.PostgreSQL/Proxies/{timestamp}_AddProxyCountryAndProviderGrouping.cs` + `.Designer.cs`, and regenerate `src/Host/FS.Proxy.Migrations.PostgreSQL/Proxies/ProxiesDbContextModelSnapshot.cs`

**Interfaces:**
- Produces: `Proxy.Create(Guid providerAccountId, string host, int port, ProxyProtocol protocol, string? username, string? protectedPassword, string? externalId, string? country = null, string? providerGrouping = null)` — two new **trailing optional** params, so every existing call site (including 7-arg calls in other test files) keeps compiling unchanged.
- Produces: `Proxy.UpdateConnection(string host, int port, ProxyProtocol protocol, string? username, string? protectedPassword, string? country = null, string? providerGrouping = null)` — same trailing-optional treatment.
- Produces: `Proxy.Country` (`string?`) and `Proxy.ProviderGrouping` (`string?`) public read-only properties.

- [ ] **Step 1: Write the failing tests**

Add to `src/Tests/Proxies.Tests/Domain/ProxyTests.cs`:

```csharp
    [Fact]
    public void Create_Should_SetCountryAndProviderGrouping_When_Provided()
    {
        var proxy = Proxy.Create(Guid.NewGuid(), "1.2.3.4", 8080, ProxyProtocol.Http, "user", "protected-pw", "ext-1", "cl", "zone1new");

        proxy.Country.ShouldBe("cl");
        proxy.ProviderGrouping.ShouldBe("zone1new");
    }

    [Fact]
    public void Create_Should_DefaultCountryAndProviderGrouping_ToNull_When_Omitted()
    {
        var proxy = Proxy.Create(Guid.NewGuid(), "1.2.3.4", 8080, ProxyProtocol.Http, null, null, null);

        proxy.Country.ShouldBeNull();
        proxy.ProviderGrouping.ShouldBeNull();
    }

    [Fact]
    public void UpdateConnection_Should_UpdateCountryAndProviderGrouping()
    {
        var proxy = Proxy.Create(Guid.NewGuid(), "1.2.3.4", 8080, ProxyProtocol.Http, null, null, null, "us", "old-zone");

        proxy.UpdateConnection("5.6.7.8", 9090, ProxyProtocol.Http, "u2", "p2", "ar", "new-zone");

        proxy.Country.ShouldBe("ar");
        proxy.ProviderGrouping.ShouldBe("new-zone");
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test src/Tests/Proxies.Tests --filter "FullyQualifiedName~ProxyTests"`
Expected: FAIL — `Proxy` has no `Country`/`ProviderGrouping` members and `Create`/`UpdateConnection` don't accept the extra arguments (compile error).

- [ ] **Step 3: Implement the domain changes**

In `src/Modules/Proxies/Modules.Proxies/Domain/Proxy.cs`, add two properties next to `ExternalId`:

```csharp
    public string? ExternalId { get; private set; }
    public string? Country { get; private set; }
    public string? ProviderGrouping { get; private set; }
```

Change `Create` to:

```csharp
    public static Proxy Create(
        Guid providerAccountId, string host, int port, ProxyProtocol protocol,
        string? username, string? protectedPassword, string? externalId,
        string? country = null, string? providerGrouping = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        return new Proxy
        {
            Id = Guid.CreateVersion7(),
            ProviderAccountId = providerAccountId,
            Host = host.Trim(),
            Port = port,
            Protocol = protocol,
            Username = username,
            ProtectedPassword = protectedPassword,
            ExternalId = externalId,
            Country = country,
            ProviderGrouping = providerGrouping,
            Status = ProxyStatus.Testing,
            CreatedAtUtc = DateTime.UtcNow
        };
    }
```

Change `UpdateConnection` to:

```csharp
    public void UpdateConnection(
        string host, int port, ProxyProtocol protocol, string? username, string? protectedPassword,
        string? country = null, string? providerGrouping = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        Host = host.Trim();
        Port = port;
        Protocol = protocol;
        Username = username;
        ProtectedPassword = protectedPassword;
        Country = country;
        ProviderGrouping = providerGrouping;
    }
```

In `src/Modules/Proxies/Modules.Proxies/Data/Configurations/ProxyConfiguration.cs`, add after the `ExternalId` mapping:

```csharp
        builder.Property(x => x.ExternalId).HasMaxLength(255);
        builder.Property(x => x.Country).HasMaxLength(10);
        builder.Property(x => x.ProviderGrouping).HasMaxLength(255);
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test src/Tests/Proxies.Tests --filter "FullyQualifiedName~ProxyTests"`
Expected: PASS (all `ProxyTests` facts, including the 3 new ones)

- [ ] **Step 5: Build, then generate and review the migration**

```bash
dotnet build src/FS.Proxy.slnx
dotnet ef migrations add AddProxyCountryAndProviderGrouping \
  --project src/Host/FS.Proxy.Migrations.PostgreSQL \
  --startup-project src/Host/FS.Proxy.Api \
  --context ProxiesDbContext \
  --output-dir Proxies
dotnet ef migrations script --idempotent \
  --project src/Host/FS.Proxy.Migrations.PostgreSQL \
  --startup-project src/Host/FS.Proxy.Api \
  --context ProxiesDbContext
```

Confirm the generated script only **adds** two nullable columns (`"Country" varchar(10) NULL`, `"ProviderGrouping" varchar(255) NULL`) to the `Proxies` table — no drops, no non-nullable additions without a default.

- [ ] **Step 6: Commit**

```bash
git add src/Modules/Proxies/Modules.Proxies/Domain/Proxy.cs \
        src/Modules/Proxies/Modules.Proxies/Data/Configurations/ProxyConfiguration.cs \
        src/Tests/Proxies.Tests/Domain/ProxyTests.cs \
        src/Host/FS.Proxy.Migrations.PostgreSQL/Proxies/
git commit -m "feat(proxies): add Country and ProviderGrouping to the Proxy entity

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>"
```

---

### Task 2: Extend `ProviderProxyRecord` and wire `Country`/`ProviderGrouping` through `ProviderAccountSyncService`

**Files:**
- Modify: `src/Modules/Proxies/Modules.Proxies/Providers/ProviderProxyRecord.cs`
- Modify: `src/Modules/Proxies/Modules.Proxies/Services/ProviderAccountSyncService.cs`
- Test: `src/Tests/Proxies.Tests/Services/ProviderAccountSyncServiceTests.cs`

**Interfaces:**
- Consumes: `Proxy.Create(..., string? country = null, string? providerGrouping = null)` and `Proxy.UpdateConnection(..., string? country = null, string? providerGrouping = null)` from Task 1.
- Produces: `ProviderProxyRecord` gains `string? Country = null, string? ProviderGrouping = null` as **trailing optional** positional parameters (so `ManualAdapter`, `OxylabsAdapter`, and the existing `WebShareAdapter`/`BrightDataAdapter` — before Tasks 3/4 rewrite them — keep compiling unchanged, since all their existing constructor calls use `IsActive: true` as a named argument for the last positional slot).

- [ ] **Step 1: Write the failing test**

Add to `src/Tests/Proxies.Tests/Services/ProviderAccountSyncServiceTests.cs`:

```csharp
    [Fact]
    public async Task SyncAsync_Should_PropagateCountryAndProviderGrouping_OnCreateAndUpdate()
    {
        await using var db = CreateDb();
        var account = ProviderAccount.Create("BrightData", ProxyProviderType.BrightData, "{}");
        var updatingProxy = Proxy.Create(account.Id, "old-host", 1111, ProxyProtocol.Http, null, null, "ext-existing", "us", "old-zone");
        db.ProviderAccounts.Add(account);
        db.Proxies.Add(updatingProxy);
        await db.SaveChangesAsync();

        var adapter = Substitute.For<IProxyProviderAdapter>();
        adapter.ProviderType.Returns(ProxyProviderType.BrightData);
        adapter.SupportsSync.Returns(true);
        adapter.SyncProxiesAsync(Arg.Any<ProviderAccount>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ProviderSyncResult.Ok([
                new ProviderProxyRecord("ext-existing", "new-ip", 2222, ProxyProtocol.Http, "u", "p", true, "ar", "zone1new"),
                new ProviderProxyRecord("ext-new", "9.9.9.9", 4444, ProxyProtocol.Http, "u2", "p2", true, "cl", "zone2")]));
        var factory = Substitute.For<IProxyProviderAdapterFactory>();
        factory.GetAdapter(ProxyProviderType.BrightData).Returns(adapter);

        var sut = new ProviderAccountSyncService(db, factory, new FakeProtector(), Substitute.For<IOutboxWriter>());

        await sut.SyncAsync(account.Id, CancellationToken.None);

        var updated = await db.Proxies.SingleAsync(p => p.ExternalId == "ext-existing");
        updated.Country.ShouldBe("ar");
        updated.ProviderGrouping.ShouldBe("zone1new");
        var created = await db.Proxies.SingleAsync(p => p.ExternalId == "ext-new");
        created.Country.ShouldBe("cl");
        created.ProviderGrouping.ShouldBe("zone2");
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test src/Tests/Proxies.Tests --filter "FullyQualifiedName~ProviderAccountSyncServiceTests"`
Expected: FAIL — `ProviderProxyRecord` doesn't accept 2 extra positional args (compile error), or (once that's stubbed) `updated.Country`/`created.Country` are `null` because the service doesn't pass them through yet.

- [ ] **Step 3: Implement**

In `src/Modules/Proxies/Modules.Proxies/Providers/ProviderProxyRecord.cs`:

```csharp
using FSH.Modules.Proxies.Contracts;

namespace FSH.Modules.Proxies.Providers;

public sealed record ProviderProxyRecord(
    string ExternalId, string Host, int Port, ProxyProtocol Protocol,
    string? Username, string? Password, bool IsActive,
    string? Country = null, string? ProviderGrouping = null);
```

In `src/Modules/Proxies/Modules.Proxies/Services/ProviderAccountSyncService.cs`, change the two lines inside the `foreach (var record in result.Proxies)` loop:

```csharp
            if (byExternalId.TryGetValue(record.ExternalId, out var existing))
            {
                existing.UpdateConnection(record.Host, record.Port, record.Protocol, record.Username,
                    record.Password is null ? null : protector.Protect(record.Password),
                    record.Country, record.ProviderGrouping);
            }
            else
            {
                var created = Proxy.Create(providerAccountId, record.Host, record.Port, record.Protocol, record.Username,
                    record.Password is null ? null : protector.Protect(record.Password), record.ExternalId,
                    record.Country, record.ProviderGrouping);
                dbContext.Proxies.Add(created);
            }
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test src/Tests/Proxies.Tests --filter "FullyQualifiedName~ProviderAccountSyncServiceTests|FullyQualifiedName~WebShareAdapterTests|FullyQualifiedName~BrightDataAdapterTests|FullyQualifiedName~OxylabsAdapterTests"`
Expected: PASS — the new fact passes, and every existing adapter test (which constructs `ProviderProxyRecord` with only 7 args) still compiles and passes since the two new fields are optional.

- [ ] **Step 5: Commit**

```bash
git add src/Modules/Proxies/Modules.Proxies/Providers/ProviderProxyRecord.cs \
        src/Modules/Proxies/Modules.Proxies/Services/ProviderAccountSyncService.cs \
        src/Tests/Proxies.Tests/Services/ProviderAccountSyncServiceTests.cs
git commit -m "feat(proxies): thread Country/ProviderGrouping through provider sync

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>"
```

---

### Task 3: WebShare adapter — real pagination + `Country`/`ProviderGrouping` mapping

**Files:**
- Modify: `src/Modules/Proxies/Modules.Proxies/Providers/WebShare/WebShareProxyListResponse.cs`
- Modify: `src/Modules/Proxies/Modules.Proxies/Providers/WebShare/WebShareAdapter.cs`
- Test: `src/Tests/Proxies.Tests/Providers/WebShareAdapterTests.cs`

**Interfaces:**
- Consumes: `ProviderProxyRecord(..., string? Country = null, string? ProviderGrouping = null)` from Task 2.
- Produces: no change to `IProxyProviderAdapter`'s public surface — internal behavior only. `CountryCode` on `WebShareProxyRecord` defaults to `null` so the file's pre-existing `SyncProxiesAsync_Should_MapResultsToProviderProxyRecords` fact — which constructs a `WebShareProxyRecord` with only 6 positional args and is not otherwise touched by this task — keeps compiling.

**Context (from the design spec):** `GET https://proxy.webshare.io/api/v2/proxy/list/?mode=direct&page={n}&page_size=100` returns `{"count": N, "next": "<url-or-null>", "results": [...]}`; each result has a `country_code` (ISO2, uppercase, e.g. `"CL"`) the adapter currently discards. The adapter must follow `next` until it's `null` — today it fetches exactly one page and silently drops everything past the first 100 proxies.

- [ ] **Step 1: Write the failing test**

Add to `src/Tests/Proxies.Tests/Providers/WebShareAdapterTests.cs` (needs a second private helper alongside the existing `StubHandler`, since this test must return two different responses in sequence):

```csharp
    private sealed class SequencedStubHandler(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        private int _index;
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(responses[_index++]);
        }
    }

    [Fact]
    public async Task SyncProxiesAsync_Should_FollowPagination_And_MapCountryAndProviderGrouping()
    {
        var page1 = new WebShareProxyListResponse(2, "https://proxy.webshare.io/api/v2/proxy/list/?mode=direct&page=2&page_size=100",
            [new WebShareProxyRecord("ext-1", "user", "pass", "1.2.3.4", 8080, true, "CL")]);
        var page2 = new WebShareProxyListResponse(2, null,
            [new WebShareProxyRecord("ext-2", "user", "pass", "5.6.7.8", 8081, true, "AR")]);
        var handler = new SequencedStubHandler(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(page1) },
            new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(page2) });
        var services = new ServiceCollection();
        services.AddHttpClient("ProxyProvider:WebShare").ConfigurePrimaryHttpMessageHandler(() => handler);
        var provider = services.BuildServiceProvider();
        var sut = new WebShareAdapter(provider.GetRequiredService<IHttpClientFactory>());
        var account = ProviderAccount.Create("WebShare", ProxyProviderType.WebShare, "n/a");

        var result = await sut.SyncProxiesAsync(account, "{\"ApiKey\":\"key-123\"}", CancellationToken.None);

        result.Success.ShouldBeTrue();
        result.Proxies.Count.ShouldBe(2);
        handler.Requests.Count.ShouldBe(2);
        var first = result.Proxies.Single(p => p.ExternalId == "ext-1");
        first.Country.ShouldBe("CL");
        first.ProviderGrouping.ShouldBe("Proxy List");
        result.Proxies.Single(p => p.ExternalId == "ext-2").Country.ShouldBe("AR");
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test src/Tests/Proxies.Tests --filter "FullyQualifiedName~WebShareAdapterTests"`
Expected: FAIL — `WebShareProxyRecord`'s constructor doesn't accept a `country_code` argument, and the adapter only issues one request.

- [ ] **Step 3: Implement**

In `src/Modules/Proxies/Modules.Proxies/Providers/WebShare/WebShareProxyListResponse.cs`, add the country field:

```csharp
public sealed record WebShareProxyRecord(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("username")] string Username,
    [property: JsonPropertyName("password")] string Password,
    [property: JsonPropertyName("proxy_address")] string ProxyAddress,
    [property: JsonPropertyName("port")] int Port,
    [property: JsonPropertyName("valid")] bool Valid,
    [property: JsonPropertyName("country_code")] string? CountryCode = null);
```

In `src/Modules/Proxies/Modules.Proxies/Providers/WebShare/WebShareAdapter.cs`, replace the single-request block (from `using var client = httpClientFactory.CreateClient(ClientName);` down to the `return ProviderSyncResult.Ok(proxies);` at the end) with:

```csharp
        using var client = httpClientFactory.CreateClient(ClientName);
        var proxies = new List<ProviderProxyRecord>();
        string? nextUrl = "https://proxy.webshare.io/api/v2/proxy/list/?mode=direct&page=1&page_size=100";

        while (nextUrl is not null)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, nextUrl);
            request.Headers.TryAddWithoutValidation("Authorization", $"Token {credentials.ApiKey}");

            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return ProviderSyncResult.Failed($"WebShare returned {(int)response.StatusCode} {response.ReasonPhrase}.");
            }

            var payload = await response.Content.ReadFromJsonAsync<WebShareProxyListResponse>(cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("WebShare returned an empty proxy list response.");

            proxies.AddRange(payload.Results
                .Where(r => r.Valid)
                .Select(r => new ProviderProxyRecord(r.Id, r.ProxyAddress, r.Port, ProxyProtocol.Http, r.Username, r.Password,
                    IsActive: true, Country: r.CountryCode, ProviderGrouping: "Proxy List")));

            nextUrl = payload.Next;
        }

        return ProviderSyncResult.Ok(proxies);
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test src/Tests/Proxies.Tests --filter "FullyQualifiedName~WebShareAdapterTests"`
Expected: PASS (all facts, including the existing malformed-credentials and failure-status ones — those construct `CreateSut` with a single `StubHandler`, still returns one response for the one request they expect).

- [ ] **Step 5: Commit**

```bash
git add src/Modules/Proxies/Modules.Proxies/Providers/WebShare/ \
        src/Tests/Proxies.Tests/Providers/WebShareAdapterTests.cs
git commit -m "fix(proxies): paginate WebShare proxy sync and map country/grouping

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>"
```

---

### Task 4: BrightData credentials + adapter rewrite (zone config + per-IP/rotating branch)

**Files:**
- Modify: `src/Modules/Proxies/Modules.Proxies/Providers/BrightData/BrightDataCredentials.cs`
- Delete and recreate: `src/Modules/Proxies/Modules.Proxies/Providers/BrightData/BrightDataZoneIpsResponse.cs`
- Create: `src/Modules/Proxies/Modules.Proxies/Providers/BrightData/BrightDataZoneConfigResponse.cs`
- Modify: `src/Modules/Proxies/Modules.Proxies/Providers/BrightData/BrightDataAdapter.cs`
- Test: `src/Tests/Proxies.Tests/Providers/BrightDataAdapterTests.cs` (full rewrite)

**Interfaces:**
- Consumes: `ProviderProxyRecord(..., string? Country = null, string? ProviderGrouping = null)` from Task 2.
- Produces: no change to `IProxyProviderAdapter`'s public surface — internal behavior and `BrightDataCredentials`'s shape only (an internal type, not referenced outside this provider folder and its tests).

**Context (from the design spec, verified against a real production BrightData zone):**
- `GET /zone?zone={zone}` → zone config: `{"password":["..."],"plan":{"country":"cl","default_country":null}}` (or vice versa depending on product — `default_country` on rotating zones, `country` on static ones; both possibly present).
- `GET /zone/ips?zone={zone}` → **200** `{"ips":[{"ip":"...","maxmind":"cc"}]}` for zones with an enumerable IP roster (static, whether single- or multi-country) — one `ProviderProxyRecord` per IP. **400** `"Wrong zone plan"` (plain text body, not JSON) for rotating zones with no enumerable roster — a single `ProviderProxyRecord` representing the whole zone.
- Connection: `Host = credentials.GatewayHost` (default `"brd.superproxy.io"`), `Port = credentials.GatewayPort`, `Username = "brd-customer-{CustomerId}-zone-{Zone}-ip-{ip}"` (per-IP case) or `"brd-customer-{CustomerId}-zone-{Zone}"` (pool case), `Password` = the zone's password (first element of the `password` array).

- [ ] **Step 1: Write the failing tests (full rewrite of the test file)**

Replace the entire contents of `src/Tests/Proxies.Tests/Providers/BrightDataAdapterTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using FSH.Modules.Proxies.Contracts;
using FSH.Modules.Proxies.Domain;
using FSH.Modules.Proxies.Providers.BrightData;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Proxies.Tests.Providers;

public sealed class BrightDataAdapterTests
{
    private const string ValidCredentials =
        "{\"ApiToken\":\"token-1\",\"Zone\":\"zone1\",\"CustomerId\":\"cust1\",\"GatewayPort\":44445,\"GatewayHost\":\"brd.superproxy.io\"}";

    private sealed class SequencedStubHandler(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        private int _index;
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(responses[_index++]);
        }
    }

    private static (BrightDataAdapter Adapter, SequencedStubHandler Handler) CreateSut(params HttpResponseMessage[] responses)
    {
        var handler = new SequencedStubHandler(responses);
        var services = new ServiceCollection();
        services.AddHttpClient("ProxyProvider:BrightData").ConfigurePrimaryHttpMessageHandler(() => handler);
        var provider = services.BuildServiceProvider();
        return (new BrightDataAdapter(provider.GetRequiredService<IHttpClientFactory>()), handler);
    }

    private static HttpResponseMessage ZoneConfigResponse(string password, string? country = null, string? defaultCountry = null) =>
        new(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new BrightDataZoneConfigResponse([password], new BrightDataZonePlan(country, defaultCountry)))
        };

    [Fact]
    public async Task SyncProxiesAsync_Should_MapOneRecordPerIp_When_ZoneHasEnumerableIps()
    {
        var ipsResponse = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new BrightDataZoneIpsResponse([
                new BrightDataZoneIpRecord("9.9.9.9", "cl"),
                new BrightDataZoneIpRecord("8.8.8.8", "ar")]))
        };
        var (sut, handler) = CreateSut(ZoneConfigResponse("zone-pass"), ipsResponse);
        var account = ProviderAccount.Create("BrightData", ProxyProviderType.BrightData, "n/a");

        var result = await sut.SyncProxiesAsync(account, ValidCredentials, CancellationToken.None);

        result.Success.ShouldBeTrue();
        result.Proxies.Count.ShouldBe(2);
        var first = result.Proxies.Single(p => p.Host == "brd.superproxy.io" && p.Port == 44445 && p.Country == "cl");
        first.Username.ShouldBe("brd-customer-cust1-zone-zone1-ip-9.9.9.9");
        first.Password.ShouldBe("zone-pass");
        first.ProviderGrouping.ShouldBe("zone1");
        result.Proxies.Single(p => p.Country == "ar").Username.ShouldBe("brd-customer-cust1-zone-zone1-ip-8.8.8.8");
        handler.Requests[0].RequestUri!.ToString().ShouldContain("/zone?zone=zone1");
        handler.Requests[1].RequestUri!.ToString().ShouldContain("/zone/ips?zone=zone1");
        handler.Requests[0].Headers.Authorization!.ToString().ShouldBe("Bearer token-1");
    }

    [Fact]
    public async Task SyncProxiesAsync_Should_MapSingleZoneRecord_When_ZoneIsRotating()
    {
        var rotatingResponse = new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("Wrong zone plan")
        };
        var (sut, _) = CreateSut(ZoneConfigResponse("zone-pass", defaultCountry: "cl"), rotatingResponse);
        var account = ProviderAccount.Create("BrightData", ProxyProviderType.BrightData, "n/a");

        var result = await sut.SyncProxiesAsync(account, ValidCredentials, CancellationToken.None);

        result.Success.ShouldBeTrue();
        var pool = result.Proxies.Single();
        pool.Username.ShouldBe("brd-customer-cust1-zone-zone1");
        pool.Password.ShouldBe("zone-pass");
        pool.Country.ShouldBe("cl");
        pool.ProviderGrouping.ShouldBe("zone1");
        pool.ExternalId.ShouldBe("zone1:pool");
    }

    [Fact]
    public async Task SyncProxiesAsync_Should_LeaveCountryNull_When_ZonePlanCountryIsMultiValued()
    {
        var rotatingResponse = new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("Wrong zone plan") };
        var (sut, _) = CreateSut(ZoneConfigResponse("zone-pass", country: "ar us"), rotatingResponse);
        var account = ProviderAccount.Create("BrightData", ProxyProviderType.BrightData, "n/a");

        var result = await sut.SyncProxiesAsync(account, ValidCredentials, CancellationToken.None);

        result.Proxies.Single().Country.ShouldBeNull();
    }

    [Fact]
    public async Task SyncProxiesAsync_Should_ReturnFailure_When_ZoneConfigRequestFails()
    {
        var (sut, _) = CreateSut(new HttpResponseMessage(HttpStatusCode.Forbidden));
        var account = ProviderAccount.Create("BrightData", ProxyProviderType.BrightData, "n/a");

        var result = await sut.SyncProxiesAsync(account, ValidCredentials, CancellationToken.None);

        result.Success.ShouldBeFalse();
    }

    [Fact]
    public async Task SyncProxiesAsync_Should_ReturnFailure_When_IpsRequestFailsWithNonBadRequestError()
    {
        var (sut, _) = CreateSut(ZoneConfigResponse("zone-pass"), new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var account = ProviderAccount.Create("BrightData", ProxyProviderType.BrightData, "n/a");

        var result = await sut.SyncProxiesAsync(account, ValidCredentials, CancellationToken.None);

        result.Success.ShouldBeFalse();
    }

    [Theory]
    [InlineData("raw-api-token-not-json")]
    [InlineData("{\"ApiToken\":")]
    [InlineData("")]
    [InlineData("null")]
    public async Task SyncProxiesAsync_Should_ReturnFailure_When_CredentialsAreNotValidJson(string credentials)
    {
        var (sut, _) = CreateSut(new HttpResponseMessage(HttpStatusCode.OK));
        var account = ProviderAccount.Create("BrightData", ProxyProviderType.BrightData, "n/a");

        var result = await sut.SyncProxiesAsync(account, credentials, CancellationToken.None);

        result.Success.ShouldBeFalse();
        result.ErrorMessage.ShouldStartWith("Invalid credentials JSON");
    }

    [Fact]
    public void SupportsRenew_Should_BeFalse() =>
        new BrightDataAdapter(Substitute.For<IHttpClientFactory>()).SupportsRenew.ShouldBeFalse();
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test src/Tests/Proxies.Tests --filter "FullyQualifiedName~BrightDataAdapterTests"`
Expected: FAIL — `BrightDataCredentials`/`BrightDataZoneConfigResponse`/`BrightDataZonePlan`/`BrightDataZoneIpRecord` don't exist yet in the shapes the test expects (compile errors), and the adapter doesn't implement the 2-call algorithm.

- [ ] **Step 3: Implement**

Replace `src/Modules/Proxies/Modules.Proxies/Providers/BrightData/BrightDataCredentials.cs`:

```csharp
namespace FSH.Modules.Proxies.Providers.BrightData;

public sealed record BrightDataCredentials(
    string ApiToken, string Zone, string CustomerId, int GatewayPort, string GatewayHost = "brd.superproxy.io");
```

Delete `src/Modules/Proxies/Modules.Proxies/Providers/BrightData/BrightDataZoneIpsResponse.cs` and recreate it:

```csharp
using System.Text.Json.Serialization;

namespace FSH.Modules.Proxies.Providers.BrightData;

public sealed record BrightDataZoneIpsResponse(
    [property: JsonPropertyName("ips")] IReadOnlyList<BrightDataZoneIpRecord> Ips);

public sealed record BrightDataZoneIpRecord(
    [property: JsonPropertyName("ip")] string Ip,
    [property: JsonPropertyName("maxmind")] string? Maxmind);
```

Create `src/Modules/Proxies/Modules.Proxies/Providers/BrightData/BrightDataZoneConfigResponse.cs`:

```csharp
using System.Text.Json.Serialization;

namespace FSH.Modules.Proxies.Providers.BrightData;

public sealed record BrightDataZoneConfigResponse(
    [property: JsonPropertyName("password")] IReadOnlyList<string> Password,
    [property: JsonPropertyName("plan")] BrightDataZonePlan Plan);

public sealed record BrightDataZonePlan(
    [property: JsonPropertyName("country")] string? Country,
    [property: JsonPropertyName("default_country")] string? DefaultCountry);
```

Replace `src/Modules/Proxies/Modules.Proxies/Providers/BrightData/BrightDataAdapter.cs`:

```csharp
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FSH.Modules.Proxies.Contracts;
using FSH.Modules.Proxies.Domain;

namespace FSH.Modules.Proxies.Providers.BrightData;

/// <summary>
/// BrightData organizes proxies into "zones", not individual per-IP accounts. A zone with an
/// enumerable IP roster (static, whether single- or multi-country) yields one record per IP,
/// each pinned via a "-ip-{ip}" username suffix; a zone with no enumerable roster (rotating)
/// yields a single record representing the whole zone/gateway, with no IP pin — BrightData
/// rotates internally. Verified against a real production zone export: connection is always
/// through the shared gateway host:port, never a literal per-IP socket. See
/// docs/superpowers/specs/2026-09-03-provider-sync-brightdata-webshare-design.md.
/// </summary>
public sealed class BrightDataAdapter(IHttpClientFactory httpClientFactory) : IProxyProviderAdapter
{
    private const string ClientName = "ProxyProvider:BrightData";

    public ProxyProviderType ProviderType => ProxyProviderType.BrightData;
    public bool SupportsSync => true;
    public bool SupportsRenew => false;

    public async Task<ProviderSyncResult> SyncProxiesAsync(ProviderAccount account, string decryptedCredentials, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(account);

        BrightDataCredentials? credentials;
        try
        {
            credentials = JsonSerializer.Deserialize<BrightDataCredentials>(decryptedCredentials);
        }
        catch (JsonException ex)
        {
            return ProviderSyncResult.Failed($"Invalid credentials JSON: {ex.Message}");
        }

        if (credentials is null)
        {
            return ProviderSyncResult.Failed("Invalid credentials JSON: BrightData credentials could not be parsed.");
        }

        using var client = httpClientFactory.CreateClient(ClientName);

        using var zoneRequest = new HttpRequestMessage(HttpMethod.Get, $"https://api.brightdata.com/zone?zone={Uri.EscapeDataString(credentials.Zone)}");
        zoneRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credentials.ApiToken);
        using var zoneResponse = await client.SendAsync(zoneRequest, cancellationToken).ConfigureAwait(false);
        if (!zoneResponse.IsSuccessStatusCode)
        {
            return ProviderSyncResult.Failed($"BrightData returned {(int)zoneResponse.StatusCode} {zoneResponse.ReasonPhrase} for zone config.");
        }

        var zoneConfig = await zoneResponse.Content.ReadFromJsonAsync<BrightDataZoneConfigResponse>(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("BrightData returned an empty zone config response.");
        if (zoneConfig.Password.Count == 0)
        {
            return ProviderSyncResult.Failed("BrightData zone config did not include a password.");
        }
        var password = zoneConfig.Password[0];

        using var ipsRequest = new HttpRequestMessage(HttpMethod.Get, $"https://api.brightdata.com/zone/ips?zone={Uri.EscapeDataString(credentials.Zone)}");
        ipsRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credentials.ApiToken);
        using var ipsResponse = await client.SendAsync(ipsRequest, cancellationToken).ConfigureAwait(false);

        if (ipsResponse.StatusCode == HttpStatusCode.BadRequest)
        {
            var poolCountry = SingleCountryOrNull(zoneConfig.Plan.DefaultCountry ?? zoneConfig.Plan.Country);
            var poolUsername = $"brd-customer-{credentials.CustomerId}-zone-{credentials.Zone}";
            return ProviderSyncResult.Ok([
                new ProviderProxyRecord($"{credentials.Zone}:pool", credentials.GatewayHost, credentials.GatewayPort, ProxyProtocol.Http,
                    poolUsername, password, IsActive: true, Country: poolCountry, ProviderGrouping: credentials.Zone)
            ]);
        }

        if (!ipsResponse.IsSuccessStatusCode)
        {
            return ProviderSyncResult.Failed($"BrightData returned {(int)ipsResponse.StatusCode} {ipsResponse.ReasonPhrase} for zone IPs.");
        }

        var ipsPayload = await ipsResponse.Content.ReadFromJsonAsync<BrightDataZoneIpsResponse>(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("BrightData returned an empty zone IPs response.");

        var proxies = ipsPayload.Ips
            .Select(ip => new ProviderProxyRecord(
                ExternalId: $"{credentials.Zone}:{ip.Ip}",
                Host: credentials.GatewayHost,
                Port: credentials.GatewayPort,
                Protocol: ProxyProtocol.Http,
                Username: $"brd-customer-{credentials.CustomerId}-zone-{credentials.Zone}-ip-{ip.Ip}",
                Password: password,
                IsActive: true,
                Country: ip.Maxmind,
                ProviderGrouping: credentials.Zone))
            .ToList();

        return ProviderSyncResult.Ok(proxies);
    }

    /// <summary>
    /// BrightData reports a multi-country zone's countries as a single space-separated string
    /// (e.g. "ar us") with no per-IP breakdown available in the rotating (pool) case — there is
    /// no single correct country to attribute, so this returns null rather than guessing.
    /// </summary>
    private static string? SingleCountryOrNull(string? country) =>
        string.IsNullOrWhiteSpace(country) || country.Contains(' ', StringComparison.Ordinal) ? null : country;

    public Task<ProviderRenewResult> RenewProxyAsync(ProviderAccount account, string decryptedCredentials, Proxy proxy, CancellationToken cancellationToken) =>
        Task.FromResult(ProviderRenewResult.Unsupported());
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test src/Tests/Proxies.Tests --filter "FullyQualifiedName~BrightDataAdapterTests"`
Expected: PASS (all facts)

- [ ] **Step 5: Full module test run**

Run: `dotnet test src/Tests/Proxies.Tests`
Expected: PASS — no other test references the old `BrightDataIpRecord`/`BrightDataZoneIpsResponse` shape or the old `BrightDataCredentials(ApiToken, Zone)` 2-arg shape (grep confirmed only this adapter and its own test file touch these types).

- [ ] **Step 6: Commit**

```bash
git add src/Modules/Proxies/Modules.Proxies/Providers/BrightData/ \
        src/Tests/Proxies.Tests/Providers/BrightDataAdapterTests.cs
git commit -m "fix(proxies): rewrite BrightDataAdapter around the real zone/ips algorithm

Corrects two wrong assumptions from the original implementation, found by
live read-only investigation against a real BrightData account: the
endpoint was /zone/ips (not /zone/ips's old guessed shape), and BrightData
never hands out individually-dialable proxy sockets — every IP, static or
rotating, is reached through one shared gateway host:port with the target
IP (or nothing, for rotating zones) encoded in the username.

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>"
```

---

### Task 5: Backend `Country` filter — `ListProxiesQuery`/Handler/Validator + `ProxyDto`

**Files:**
- Modify: `src/Modules/Proxies/Modules.Proxies.Contracts/v1/Proxies/ListProxiesQuery.cs`
- Modify: `src/Modules/Proxies/Modules.Proxies.Contracts/Dtos/ProxyDto.cs`
- Modify: `src/Modules/Proxies/Modules.Proxies/Features/v1/Proxies/ListProxies/ListProxiesQueryHandler.cs`
- Modify: `src/Modules/Proxies/Modules.Proxies/Features/v1/Proxies/ListProxies/ListProxiesQueryValidator.cs`
- Modify: `src/Modules/Proxies/Modules.Proxies/Features/v1/Proxies/ListProxies/ListProxiesEndpoint.cs`
- Test: `src/Tests/Proxies.Tests/Handlers/ListProxiesHandlerTests.cs`

**Interfaces:**
- Consumes: `Proxy.Country`/`Proxy.ProviderGrouping` from Task 1.
- Produces: `ProxyDto` gains trailing `string? Country, string? ProviderGrouping`. `ListProxiesQuery` gains a new `string? Country = null` parameter positioned **before** the existing `PageNumber`/`PageSize` defaults (all three keep defaults, so every existing 3-arg test call — e.g. `new ListProxiesQuery(["pais:cl"], null, null)` — keeps compiling).

- [ ] **Step 1: Write the failing test**

Add to `src/Tests/Proxies.Tests/Handlers/ListProxiesHandlerTests.cs`:

```csharp
    [Fact]
    public async Task Handle_Should_FilterByCountry_CaseInsensitively()
    {
        await using var db = CreateDb();
        var account = ProviderAccount.Create("Manual", ProxyProviderType.Manual, "protected:x");
        var chile = Proxy.Create(account.Id, "1.1.1.1", 80, ProxyProtocol.Http, null, null, null, "cl");
        var argentina = Proxy.Create(account.Id, "2.2.2.2", 80, ProxyProtocol.Http, null, null, null, "AR");
        db.ProviderAccounts.Add(account);
        db.Proxies.AddRange(chile, argentina);
        await db.SaveChangesAsync();
        var sut = new ListProxiesQueryHandler(db);

        var result = await sut.Handle(new ListProxiesQuery(null, null, null, "CL"), CancellationToken.None);

        result.Items.Select(x => x.Id).ShouldBe([chile.Id]);
        result.Items.Single().Country.ShouldBe("cl");
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test src/Tests/Proxies.Tests --filter "FullyQualifiedName~ListProxiesHandlerTests"`
Expected: FAIL — `ListProxiesQuery` doesn't accept a 4th positional arg, and `ProxyDto` has no `Country` member (compile error).

- [ ] **Step 3: Implement**

In `src/Modules/Proxies/Modules.Proxies.Contracts/v1/Proxies/ListProxiesQuery.cs`:

```csharp
public sealed record ListProxiesQuery(
    IReadOnlyList<string>? Tags, ProxyStatus? Status, Guid? ProviderAccountId,
    string? Country = null, int PageNumber = 1, int PageSize = 20) : IQuery<PagedResponse<ProxyDto>>;
```

In `src/Modules/Proxies/Modules.Proxies.Contracts/Dtos/ProxyDto.cs`:

```csharp
public sealed record ProxyDto(
    Guid Id, string Host, int Port, ProxyProtocol Protocol, ProxyStatus Status,
    Guid ProviderAccountId, string ProviderAccountName, ProxyProviderType ProviderType,
    IReadOnlyList<string> Tags, DateTime CreatedAtUtc, DateTime? LastRenewedAtUtc,
    string? Country, string? ProviderGrouping);
```

In `src/Modules/Proxies/Modules.Proxies/Features/v1/Proxies/ListProxies/ListProxiesQueryHandler.cs`, add the filter right after the existing `ProviderAccountId` filter:

```csharp
        if (query.ProviderAccountId is { } accountId) q = q.Where(p => p.ProviderAccountId == accountId);
        if (!string.IsNullOrWhiteSpace(query.Country))
        {
            var normalizedCountry = query.Country.ToUpperInvariant();
            q = q.Where(p => p.Country != null && p.Country.ToUpper() == normalizedCountry);
        }
```

and update the `ProxyDto` construction to include the two new trailing fields:

```csharp
        var items = page.Select(p => new ProxyDto(
            p.Id, p.Host, p.Port, p.Protocol, p.Status,
            p.ProviderAccountId, accountNames[p.ProviderAccountId].Name, accountNames[p.ProviderAccountId].ProviderType,
            tagsByProxy.Where(t => t.ProxyId == p.Id).Select(t => t.Name).ToList(),
            p.CreatedAtUtc, p.LastRenewedAtUtc, p.Country, p.ProviderGrouping)).ToList();
```

In `src/Modules/Proxies/Modules.Proxies/Features/v1/Proxies/ListProxies/ListProxiesQueryValidator.cs`, add:

```csharp
        RuleFor(x => x.Country).MaximumLength(10).When(x => x.Country is not null);
```

In `src/Modules/Proxies/Modules.Proxies/Features/v1/Proxies/ListProxies/ListProxiesEndpoint.cs`, add the `country` parameter and pass it through:

```csharp
        return endpoints.MapGet("/",
                (string[]? tags, ProxyStatus? status, Guid? providerAccountId, string? country, int pageNumber, int pageSize, IMediator mediator, CancellationToken ct) =>
                    mediator.Send(new ListProxiesQuery(tags, status, providerAccountId, country, pageNumber == 0 ? 1 : pageNumber, pageSize == 0 ? 20 : pageSize), ct))
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test src/Tests/Proxies.Tests --filter "FullyQualifiedName~ListProxiesHandlerTests"`
Expected: PASS (all facts, including the new one and the two pre-existing tag/status filter facts)

- [ ] **Step 5: Full backend build + test run**

```bash
dotnet build src/FS.Proxy.slnx
dotnet test src/Tests/Proxies.Tests
```

Expected: build clean (0 warnings — `TreatWarningsAsErrors`), all `Proxies.Tests` pass.

- [ ] **Step 6: Commit**

```bash
git add src/Modules/Proxies/Modules.Proxies.Contracts/v1/Proxies/ListProxiesQuery.cs \
        src/Modules/Proxies/Modules.Proxies.Contracts/Dtos/ProxyDto.cs \
        src/Modules/Proxies/Modules.Proxies/Features/v1/Proxies/ListProxies/ \
        src/Tests/Proxies.Tests/Handlers/ListProxiesHandlerTests.cs
git commit -m "feat(proxies): expose Country/ProviderGrouping and add a Country filter to ListProxies

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>"
```

---

### Task 6: Admin UI — display `Country`/`ProviderGrouping`, add a Country filter, provider-aware credentials placeholder

**Files:**
- Modify: `clients/admin/src/api/proxies.ts`
- Modify: `clients/admin/src/pages/proxies/list.tsx`
- Modify: `clients/admin/src/components/proxies/provider-account-dialog.tsx`
- Test: `clients/admin/tests/proxies/proxies-list.spec.ts`

**Interfaces:**
- Consumes: the backend `ProxyDto.country`/`ProxyDto.providerGrouping` fields and the `ListProxies` endpoint's new `country` query parameter, both from Task 5.

- [ ] **Step 1: Write the failing Playwright test**

In `clients/admin/tests/proxies/proxies-list.spec.ts`, add `country: "CL"` to the existing `PROXY_CL` mock object (right after `protocol: "Http",`):

```ts
const PROXY_CL = {
  id: "11111111-1111-1111-1111-111111111111",
  host: "10.0.0.5",
  port: 3128,
  protocol: "Http",
  country: "CL",
  status: "Active",
  providerAccountId: "acc-1",
  providerAccountName: "Manual",
  providerType: "Manual",
  providerGrouping: null,
  tags: ["pais:cl"],
  createdAtUtc: "2026-01-01T00:00:00Z",
  lastRenewedAtUtc: null,
};
```

Add a new test inside `test.describe("proxies list", ...)`:

```ts
  test("shows the provider-reported country next to the protocol", async ({ page }) => {
    await page.route("**/api/v1/proxies/?*", async (route) => {
      await route.fulfill({ status: 200, headers: { "Content-Type": "application/json" }, body: JSON.stringify(paged([PROXY_CL])) });
    });

    await page.goto("/proxies");

    await expect(page.getByRole("heading", { name: "Proxies", exact: true })).toBeVisible({ timeout: 10_000 });
    await expect(page.getByRole("listitem").getByText("Http · CL", { exact: true })).toBeVisible();
  });
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `cd clients/admin && npx playwright test tests/proxies/proxies-list.spec.ts`
Expected: FAIL — `ProxyDto` (TypeScript type) doesn't have `country`, and the row doesn't render "Http · CL" anywhere yet.

- [ ] **Step 3: Implement**

In `clients/admin/src/api/proxies.ts`:

```ts
export type ProxyDto = {
  id: string;
  host: string;
  port: number;
  protocol: ProxyProtocol;
  status: ProxyStatus;
  providerAccountId: string;
  providerAccountName: string;
  providerType: ProxyProviderType;
  tags: string[];
  createdAtUtc: string;
  lastRenewedAtUtc: string | null;
  country: string | null;
  providerGrouping: string | null;
};

export type ListProxiesParams = {
  tags?: string[];
  status?: ProxyStatus;
  providerAccountId?: string;
  country?: string;
  pageNumber?: number;
  pageSize?: number;
};

export async function listProxies(params: ListProxiesParams = {}): Promise<PagedResponse<ProxyDto>> {
  const query = new URLSearchParams();
  query.set("pageNumber", String(params.pageNumber ?? 1));
  query.set("pageSize", String(params.pageSize ?? 20));
  if (params.status) query.set("status", params.status);
  if (params.providerAccountId) query.set("providerAccountId", params.providerAccountId);
  if (params.country) query.set("country", params.country);
  for (const tag of params.tags ?? []) query.append("tags", tag);
  return apiFetch<PagedResponse<ProxyDto>>(`${BASE}/?${query.toString()}`);
}
```

In `clients/admin/src/pages/proxies/list.tsx`, add a debounced `country` filter mirroring the existing `tagsInput`/`tags` pair. Add state near the top of `ProxiesListPage` (right after the `tags` state):

```tsx
  const [countryInput, setCountryInput] = useState("");
  const [country, setCountry] = useState("");
```

Add its debounce effect right after the existing tags-debounce `useEffect`:

```tsx
  useEffect(() => {
    const t = setTimeout(() => {
      setCountry(countryInput.trim());
      setPageNumber(1);
    }, 300);
    return () => clearTimeout(t);
  }, [countryInput]);
```

Wire it into the query key and `listProxies` call:

```tsx
  const proxiesQuery = useQuery({
    queryKey: ["proxies", "list", { pageNumber, tags, status, providerAccountId, country }],
    queryFn: () =>
      listProxies({
        pageNumber,
        pageSize: PAGE_SIZE,
        tags: tags.length > 0 ? tags : undefined,
        status: status || undefined,
        providerAccountId: providerAccountId || undefined,
        country: country || undefined,
      }),
    placeholderData: keepPreviousData,
  });
```

Update `filtersActive` and `clearFilters`:

```tsx
  const filtersActive = tags.length > 0 || status !== "" || providerAccountId !== "" || country !== "";

  const clearFilters = () => {
    setTagsInput("");
    setStatus("");
    setProviderAccountId("");
    setCountryInput("");
  };
```

Add the input in the filter row, right after the Tags `<div>` block and before the Status `<Select>`:

```tsx
        <div className="flex flex-col gap-1">
          <label
            htmlFor="proxies-country"
            className="font-mono text-[0.6875rem] uppercase tracking-[0.18em] text-[var(--color-muted-foreground)]"
          >
            Country
          </label>
          <input
            id="proxies-country"
            type="search"
            placeholder="CL"
            value={countryInput}
            onChange={(e) => setCountryInput(e.target.value)}
            className="h-9 w-24 max-w-full rounded-md border border-[var(--color-input)] bg-transparent px-3 font-mono text-[12.5px] outline-none transition-colors placeholder:text-[oklch(from_var(--color-muted-foreground)_l_c_h_/_0.7)] focus-visible:border-[var(--color-ring)] focus-visible:ring-[3px] focus-visible:ring-[oklch(from_var(--color-ring)_l_c_h_/_0.5)]"
          />
        </div>
```

In `ProxyDesktopRow`, change the Host cell's protocol line to include `Country`:

```tsx
          <span className="block truncate font-mono text-[11px] text-[var(--color-muted-foreground)]">
            {proxy.country ? `${proxy.protocol} · ${proxy.country}` : proxy.protocol}
          </span>
```

and the Provider cell's subtitle line to include `ProviderGrouping`:

```tsx
          <span className="block truncate font-mono text-[11px] text-[var(--color-muted-foreground)]">
            {proxy.providerGrouping ? `${proxy.providerType} · ${proxy.providerGrouping}` : proxy.providerType}
          </span>
```

In `ProxyMobileCard`, change the subtitle line the same way:

```tsx
            <p className="mt-0.5 truncate text-[11px] text-[var(--color-muted-foreground)]">
              {proxy.providerAccountName} (
              {proxy.providerGrouping ? `${proxy.providerType} · ${proxy.providerGrouping}` : proxy.providerType}
              {proxy.country ? `, ${proxy.country}` : ""})
            </p>
```

In `clients/admin/src/components/proxies/provider-account-dialog.tsx`, make the credentials placeholder provider-aware. Add `watch` to the destructured `useForm` result:

```tsx
  const {
    register,
    handleSubmit,
    control,
    reset,
    watch,
    formState: { errors, isSubmitting },
  } = useForm<FormValues>({
```

Add a helper function above `ProviderAccountDialog` (after `buildSchema`):

```tsx
function credentialsPlaceholder(providerType: ProxyProviderType): string {
  switch (providerType) {
    case "BrightData":
      return '{"apiToken":"...","zone":"...","customerId":"...","gatewayPort":44445}';
    case "Oxylabs":
      return '{"username":"...","password":"..."}';
    case "WebShare":
    default:
      return '{"apiKey":"..."}';
  }
}
```

Inside `ProviderAccountDialog`, after the `submitting` line, resolve the placeholder — for edit mode there's no provider picker in the form, so fall back to `account.providerType`:

```tsx
  const submitting = isSubmitting || mutation.isPending;
  const watchedProviderType = watch("providerType");
  const effectiveProviderType = isEdit ? (account!.providerType === "Manual" ? "WebShare" : account!.providerType) : watchedProviderType;
```

Change the credentials `<Input>`'s `placeholder` prop:

```tsx
                  placeholder={credentialsPlaceholder(effectiveProviderType)}
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `cd clients/admin && npx playwright test tests/proxies/proxies-list.spec.ts`
Expected: PASS (all facts in the file, including the new one)

- [ ] **Step 5: Full frontend verification**

```bash
cd clients/admin
npm run build
npm run lint
npx playwright test tests/proxies/
```

Expected: build clean, lint clean (no new warnings/errors beyond the pre-existing fast-refresh warnings noted in the parent module's review), all Proxies Playwright specs pass.

- [ ] **Step 6: Commit**

```bash
git add clients/admin/src/api/proxies.ts \
        clients/admin/src/pages/proxies/list.tsx \
        clients/admin/src/components/proxies/provider-account-dialog.tsx \
        clients/admin/tests/proxies/proxies-list.spec.ts
git commit -m "feat(admin): show provider Country/ProviderGrouping and add a Country filter

Also makes the provider account credentials placeholder provider-aware,
since BrightData's credentials JSON shape changed (adds customerId and
gatewayPort, neither derivable from BrightData's API).

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>"
```
