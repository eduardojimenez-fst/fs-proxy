# Provider File Import Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let an admin sync a `ProviderAccount`'s proxies by uploading a CSV file (for providers with no working live API, e.g. Oxylabs — or as a manual top-up for any provider), reusing the exact reconciliation semantics the live-adapter sync already has.

**Architecture:** A new `ProxyKind` enum + `Proxy.Kind` field (mirroring the existing `Geolocation`/`ProviderGrouping` pattern). The existing `ProviderAccountSyncService.SyncAsync`'s create/update/retire loop is extracted into a shared `ReconcileAsync` method on `IProviderAccountSyncService`, called by both the live-adapter path (unchanged behavior) and a new file-import path. A pure `ProviderFileParser` turns canonical-format CSV text into `ProviderProxyRecord`s + per-row errors; a new command handler resolves per-row credential/geolocation/kind fallbacks against the account's stored defaults, then calls the shared reconciler. A new multipart upload endpoint and admin-UI dialog drive it.

**Tech Stack:** .NET 10, EF Core 10 / PostgreSQL, Mediator, FluentValidation (backend) — React 19, TanStack Query, Radix/Tailwind (frontend, `clients/admin`).

**Spec:** `docs/superpowers/specs/2026-09-04-provider-file-import-design.md`

## Global Constraints

- Module boundary: this module's runtime project (`Modules.Proxies`) may reference its own `.Contracts` project freely; nothing outside `Modules.Proxies` may be touched.
- Mediator handlers: `public sealed`, return `ValueTask<T>`, `.ConfigureAwait(false)` on every await.
- Structured logging only — message templates, no string interpolation in log messages.
- Every command handler needs a validator (Golden rule 8).
- `CancellationToken` propagates into every EF/IO call.
- No new CSV parsing package — hand-rolled parsing, matching every other parsing point in this module.
- The raw uploaded file is never persisted (no `Modules.Files` integration) — parse-and-discard.
- Build with `dotnet build src/FS.Proxy.slnx` and run `dotnet test src/Tests/Proxies.Tests` after every backend task; run `npm run build && npm run lint` (in `clients/admin`) and the relevant Playwright spec after every frontend task.

---

### Task 1: `ProxyKind` enum + `Proxy.Kind` domain field + migration

**Files:**
- Create: `src/Modules/Proxies/Modules.Proxies.Contracts/ProxyKind.cs`
- Modify: `src/Modules/Proxies/Modules.Proxies/Domain/Proxy.cs`
- Test: `src/Tests/Proxies.Tests/Domain/ProxyTests.cs`
- Migration: generated via `dotnet ef migrations add`, lands in `src/Host/FS.Proxy.Migrations.PostgreSQL/Proxies/`

**Interfaces:**
- Produces: `ProxyKind` enum (`DataCenter`, `Residential`, `Mobile`, `Dedicated`); `Proxy.Kind` (`ProxyKind?`, public, private setter); `Proxy.Create(..., ProxyKind? kind = null)` and `Proxy.UpdateConnection(..., ProxyKind? kind = null)` — both gain `kind` as the new final trailing optional parameter, after the existing trailing `providerGrouping` parameter.

- [ ] **Step 1: Write the failing test**

```csharp
// Add to src/Tests/Proxies.Tests/Domain/ProxyTests.cs
[Fact]
public void Create_Should_SetKind_When_Provided()
{
    var proxy = Proxy.Create(Guid.NewGuid(), "1.2.3.4", 8080, ProxyProtocol.Http, null, null, "ext-1",
        geolocation: "cl", providerGrouping: "zone1", kind: ProxyKind.DataCenter);

    proxy.Kind.ShouldBe(ProxyKind.DataCenter);
}

[Fact]
public void UpdateConnection_Should_UpdateKind()
{
    var proxy = Proxy.Create(Guid.NewGuid(), "1.2.3.4", 8080, ProxyProtocol.Http, null, null, "ext-1");

    proxy.UpdateConnection("1.2.3.4", 8080, ProxyProtocol.Http, null, null, kind: ProxyKind.Residential);

    proxy.Kind.ShouldBe(ProxyKind.Residential);
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test src/Tests/Proxies.Tests --filter "FullyQualifiedName~ProxyTests"`
Expected: FAIL — `CS1739`/`CS7036` (`Proxy.Create`/`UpdateConnection` don't accept `kind`) or `Kind` doesn't exist.

- [ ] **Step 3: Write the minimal implementation**

`src/Modules/Proxies/Modules.Proxies.Contracts/ProxyKind.cs`:

```csharp
namespace FSH.Modules.Proxies.Contracts;

public enum ProxyKind
{
    DataCenter,
    Residential,
    Mobile,
    Dedicated,
}
```

`src/Modules/Proxies/Modules.Proxies/Domain/Proxy.cs` — add the property and extend both methods:

```csharp
public ProxyKind? Kind { get; private set; }
```

(declare right after the existing `public string? ProviderGrouping { get; private set; }` line)

```csharp
public static Proxy Create(
    Guid providerAccountId, string host, int port, ProxyProtocol protocol,
    string? username, string? protectedPassword, string? externalId,
    string? geolocation = null, string? providerGrouping = null, ProxyKind? kind = null)
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
        Geolocation = geolocation,
        ProviderGrouping = providerGrouping,
        Kind = kind,
        Status = ProxyStatus.Testing,
        CreatedAtUtc = DateTime.UtcNow
    };
}
```

```csharp
public void UpdateConnection(
    string host, int port, ProxyProtocol protocol, string? username, string? protectedPassword,
    string? geolocation = null, string? providerGrouping = null, ProxyKind? kind = null)
{
    ArgumentException.ThrowIfNullOrWhiteSpace(host);
    Host = host.Trim();
    Port = port;
    Protocol = protocol;
    Username = username;
    ProtectedPassword = protectedPassword;
    Geolocation = geolocation;
    ProviderGrouping = providerGrouping;
    Kind = kind;
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test src/Tests/Proxies.Tests --filter "FullyQualifiedName~ProxyTests"`
Expected: PASS (all `ProxyTests`, including the two new ones)

- [ ] **Step 5: Build, then generate and review the migration**

```bash
dotnet build src/FS.Proxy.slnx

dotnet ef migrations add AddProxyKind \
  --project src/Host/FS.Proxy.Migrations.PostgreSQL \
  --startup-project src/Host/FS.Proxy.Api \
  --context ProxiesDbContext \
  --output-dir Proxies

dotnet ef migrations script --idempotent \
  --project src/Host/FS.Proxy.Migrations.PostgreSQL \
  --startup-project src/Host/FS.Proxy.Api \
  --context ProxiesDbContext
```

Expected generated migration: a single `AddColumn<int>(name: "Kind", schema: "proxies", table: "Proxies", nullable: true)` (enums map to `integer` by EF's default convention — same as the existing, unconfigured `Protocol`/`Status` columns). Confirm the reviewed SQL script shows only this one `ADD COLUMN`, no drops.

- [ ] **Step 6: Commit**

```bash
git add src/Modules/Proxies/Modules.Proxies.Contracts/ProxyKind.cs \
        src/Modules/Proxies/Modules.Proxies/Domain/Proxy.cs \
        src/Tests/Proxies.Tests/Domain/ProxyTests.cs \
        src/Host/FS.Proxy.Migrations.PostgreSQL/Proxies/
git commit -m "feat(proxies): add ProxyKind enum and Proxy.Kind field"
```

---

### Task 2: `ProviderProxyRecord.Kind` + wire through the live-adapter sync path

**Files:**
- Modify: `src/Modules/Proxies/Modules.Proxies/Providers/ProviderProxyRecord.cs`
- Modify: `src/Modules/Proxies/Modules.Proxies/Services/ProviderAccountSyncService.cs`
- Test: `src/Tests/Proxies.Tests/Services/ProviderAccountSyncServiceTests.cs`

**Interfaces:**
- Consumes: `Proxy.Create`/`UpdateConnection`'s new `kind` parameter (Task 1).
- Produces: `ProviderProxyRecord.Kind` (`ProxyKind?`, trailing optional, default `null`) — every later task that constructs a `ProviderProxyRecord` (the file parser in Task 4) uses this name.

- [ ] **Step 1: Write the failing test**

Extend the existing test in `ProviderAccountSyncServiceTests.cs` (rename it and add `Kind` to both the seed proxy and the incoming records, plus assertions):

```csharp
[Fact]
public async Task SyncAsync_Should_PropagateGeolocationProviderGroupingAndKind_OnCreateAndUpdate()
{
    await using var db = CreateDb();
    var account = ProviderAccount.Create("BrightData", ProxyProviderType.BrightData, "{}");
    var updatingProxy = Proxy.Create(account.Id, "old-host", 1111, ProxyProtocol.Http, null, null, "ext-existing",
        "us", "old-zone", ProxyKind.Residential);
    db.ProviderAccounts.Add(account);
    db.Proxies.Add(updatingProxy);
    await db.SaveChangesAsync();

    var adapter = Substitute.For<IProxyProviderAdapter>();
    adapter.ProviderType.Returns(ProxyProviderType.BrightData);
    adapter.SupportsSync.Returns(true);
    adapter.SyncProxiesAsync(Arg.Any<ProviderAccount>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
        .Returns(ProviderSyncResult.Ok([
            new ProviderProxyRecord("ext-existing", "new-ip", 2222, ProxyProtocol.Http, "u", "p", true, "ar", "zone1new", ProxyKind.DataCenter),
            new ProviderProxyRecord("ext-new", "9.9.9.9", 4444, ProxyProtocol.Http, "u2", "p2", true, "cl", "zone2", ProxyKind.Mobile)]));
    var factory = Substitute.For<IProxyProviderAdapterFactory>();
    factory.GetAdapter(ProxyProviderType.BrightData).Returns(adapter);

    var sut = new ProviderAccountSyncService(db, factory, new FakeProtector(), Substitute.For<IOutboxWriter>());

    await sut.SyncAsync(account.Id, CancellationToken.None);

    var updated = await db.Proxies.SingleAsync(p => p.ExternalId == "ext-existing");
    updated.Geolocation.ShouldBe("ar");
    updated.ProviderGrouping.ShouldBe("zone1new");
    updated.Kind.ShouldBe(ProxyKind.DataCenter);
    var created = await db.Proxies.SingleAsync(p => p.ExternalId == "ext-new");
    created.Geolocation.ShouldBe("cl");
    created.ProviderGrouping.ShouldBe("zone2");
    created.Kind.ShouldBe(ProxyKind.Mobile);
}
```

Delete the old `SyncAsync_Should_PropagateGeolocationAndProviderGrouping_OnCreateAndUpdate` test this replaces (same scenario, now with `Kind` folded in — keeping both would be duplicate coverage of the same code path).

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test src/Tests/Proxies.Tests --filter "FullyQualifiedName~ProviderAccountSyncServiceTests"`
Expected: FAIL — `ProviderProxyRecord` doesn't accept a 10th positional argument (`ProxyKind.DataCenter`), and `Proxy.Kind` assertions fail (property not populated by the sync path yet).

- [ ] **Step 3: Write the minimal implementation**

`src/Modules/Proxies/Modules.Proxies/Providers/ProviderProxyRecord.cs`:

```csharp
using FSH.Modules.Proxies.Contracts;

namespace FSH.Modules.Proxies.Providers;

public sealed record ProviderProxyRecord(
    string ExternalId, string Host, int Port, ProxyProtocol Protocol,
    string? Username, string? Password, bool IsActive,
    string? Geolocation = null, string? ProviderGrouping = null, ProxyKind? Kind = null);
```

`src/Modules/Proxies/Modules.Proxies/Services/ProviderAccountSyncService.cs` — pass `record.Kind` through both call sites in the reconciliation loop:

```csharp
existing.UpdateConnection(record.Host, record.Port, record.Protocol, record.Username,
    record.Password is null ? null : protector.Protect(record.Password),
    record.Geolocation, record.ProviderGrouping, record.Kind);
```

```csharp
var created = Proxy.Create(providerAccountId, record.Host, record.Port, record.Protocol, record.Username,
    record.Password is null ? null : protector.Protect(record.Password), record.ExternalId,
    record.Geolocation, record.ProviderGrouping, record.Kind);
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test src/Tests/Proxies.Tests --filter "FullyQualifiedName~ProviderAccountSyncServiceTests"`
Expected: PASS (all tests in this file)

- [ ] **Step 5: Full module regression check**

Run: `dotnet test src/Tests/Proxies.Tests`
Expected: PASS (every test, including everything from Task 1)

- [ ] **Step 6: Commit**

```bash
git add src/Modules/Proxies/Modules.Proxies/Providers/ProviderProxyRecord.cs \
        src/Modules/Proxies/Modules.Proxies/Services/ProviderAccountSyncService.cs \
        src/Tests/Proxies.Tests/Services/ProviderAccountSyncServiceTests.cs
git commit -m "feat(proxies): propagate ProxyKind through provider sync"
```

---

### Task 3: Extract shared `ReconcileAsync` onto `IProviderAccountSyncService`

**Files:**
- Modify: `src/Modules/Proxies/Modules.Proxies/Services/IProviderAccountSyncService.cs`
- Modify: `src/Modules/Proxies/Modules.Proxies/Services/ProviderAccountSyncService.cs`
- Test: `src/Tests/Proxies.Tests/Services/ProviderAccountSyncServiceTests.cs`

**Interfaces:**
- Produces: `IProviderAccountSyncService.ReconcileAsync(ProviderAccount account, IReadOnlyList<ProviderProxyRecord> records, CancellationToken cancellationToken) : Task<(int Created, int Updated, int Retired)>` — Task 5's file-import handler calls this directly. Does **not** call `SaveChangesAsync` or `account.RecordSyncResult` — the caller does both, exactly once, after reconciling (this is a pure refactor of `SyncAsync`'s existing tail, not a behavior change).

This is a pure refactor: no new test asserts new behavior, but the existing suite is the safety net proving the extraction changed nothing observable for the live-adapter path.

- [ ] **Step 1: Confirm the safety net is green before refactoring**

Run: `dotnet test src/Tests/Proxies.Tests --filter "FullyQualifiedName~ProviderAccountSyncServiceTests"`
Expected: PASS (4 tests, from Task 2)

- [ ] **Step 2: Extract the method**

`IProviderAccountSyncService.cs`:

```csharp
using FSH.Modules.Proxies.Domain;
using FSH.Modules.Proxies.Providers;

namespace FSH.Modules.Proxies.Services;

public interface IProviderAccountSyncService
{
    Task<int> SyncAsync(Guid providerAccountId, CancellationToken cancellationToken);

    /// <summary>
    /// Upserts <paramref name="records"/> against <paramref name="account"/>'s existing proxies
    /// (matched by <see cref="Proxy.ExternalId"/>) and retires rows missing from
    /// <paramref name="records"/> — the single reconciliation algorithm shared by the live-adapter
    /// sync path (<see cref="SyncAsync"/>) and file-based import. Adds/updates entities on the
    /// tracked <c>ProxiesDbContext</c> but does not save — the caller calls
    /// <c>account.RecordSyncResult(...)</c> and <c>SaveChangesAsync</c> exactly once afterward, so
    /// both land in a single database round-trip.
    /// </summary>
    Task<(int Created, int Updated, int Retired)> ReconcileAsync(
        ProviderAccount account, IReadOnlyList<ProviderProxyRecord> records, CancellationToken cancellationToken);
}
```

`ProviderAccountSyncService.cs` — replace the body from `var existingProxies = ...` through the `foreach (var stale in ...)` block with the extracted method, and rewrite `SyncAsync`'s tail to call it:

```csharp
public async Task<int> SyncAsync(Guid providerAccountId, CancellationToken cancellationToken)
{
    var account = await dbContext.ProviderAccounts.FirstOrDefaultAsync(x => x.Id == providerAccountId, cancellationToken).ConfigureAwait(false)
        ?? throw new NotFoundException($"Provider account {providerAccountId} not found.");

    var adapter = adapterFactory.GetAdapter(account.ProviderType);
    if (!adapter.SupportsSync)
    {
        return 0;
    }

    var decrypted = protector.Unprotect(account.ProtectedCredentials);
    var result = await adapter.SyncProxiesAsync(account, decrypted, cancellationToken).ConfigureAwait(false);

    if (!result.Success)
    {
        account.RecordSyncResult(success: false, statusMessage: result.ErrorMessage);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        if (account.ConsecutiveSyncFailures >= SyncFailureNotificationThreshold)
        {
            await outboxWriter.AddAsync(
                new ProviderAccountSyncFailedIntegrationEvent(
                    Guid.CreateVersion7(), DateTime.UtcNow, TenantId: null, Guid.NewGuid().ToString(), "Proxies",
                    account.Id, account.Name, account.ConsecutiveSyncFailures, result.ErrorMessage),
                cancellationToken).ConfigureAwait(false);
        }

        return 0;
    }

    var (created, updated, retired) = await ReconcileAsync(account, result.Proxies, cancellationToken).ConfigureAwait(false);

    account.RecordSyncResult(success: true, statusMessage: $"Synced {result.Proxies.Count} proxies.");
    await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    return created + updated + retired;
}

public async Task<(int Created, int Updated, int Retired)> ReconcileAsync(
    ProviderAccount account, IReadOnlyList<ProviderProxyRecord> records, CancellationToken cancellationToken)
{
    ArgumentNullException.ThrowIfNull(account);
    ArgumentNullException.ThrowIfNull(records);

    var existingProxies = await dbContext.Proxies
        .Where(p => p.ProviderAccountId == account.Id && p.ExternalId != null)
        .ToListAsync(cancellationToken).ConfigureAwait(false);
    var byExternalId = existingProxies.ToDictionary(p => p.ExternalId!);
    var incomingExternalIds = records.Select(p => p.ExternalId).ToHashSet();

    int created = 0, updated = 0;
    foreach (var record in records)
    {
        if (byExternalId.TryGetValue(record.ExternalId, out var existing))
        {
            existing.UpdateConnection(record.Host, record.Port, record.Protocol, record.Username,
                record.Password is null ? null : protector.Protect(record.Password),
                record.Geolocation, record.ProviderGrouping, record.Kind);
            updated++;
        }
        else
        {
            var newProxy = Proxy.Create(account.Id, record.Host, record.Port, record.Protocol, record.Username,
                record.Password is null ? null : protector.Protect(record.Password), record.ExternalId,
                record.Geolocation, record.ProviderGrouping, record.Kind);
            dbContext.Proxies.Add(newProxy);
            created++;
        }
    }

    int retired = 0;
    foreach (var stale in existingProxies.Where(p => !incomingExternalIds.Contains(p.ExternalId!) && p.Status != ProxyStatus.Retired))
    {
        stale.SetStatus(ProxyStatus.Retired);
        retired++;
    }

    return (created, updated, retired);
}
```

(Note: `account.Id` replaces the old `providerAccountId` parameter name inside the extracted block — `SyncAsync` already has `account` in scope at the call site, so `ReconcileAsync(account, result.Proxies, cancellationToken)` is the only call-site change needed there.)

- [ ] **Step 3: Run the existing tests to confirm zero behavior change**

Run: `dotnet test src/Tests/Proxies.Tests --filter "FullyQualifiedName~ProviderAccountSyncServiceTests"`
Expected: PASS — all 4 tests, unmodified, still green (proves the refactor is behavior-preserving).

- [ ] **Step 4: Full module regression check**

Run: `dotnet test src/Tests/Proxies.Tests`
Expected: PASS (every test)

- [ ] **Step 5: Commit**

```bash
git add src/Modules/Proxies/Modules.Proxies/Services/IProviderAccountSyncService.cs \
        src/Modules/Proxies/Modules.Proxies/Services/ProviderAccountSyncService.cs
git commit -m "refactor(proxies): extract shared ReconcileAsync from ProviderAccountSyncService"
```

---

### Task 4: `ProviderFileParser` — canonical CSV parsing

**Files:**
- Create: `src/Modules/Proxies/Modules.Proxies.Contracts/Dtos/FileImportResult.cs`
- Create: `src/Modules/Proxies/Modules.Proxies/Providers/FileImport/FileImportDefaultCredentials.cs`
- Create: `src/Modules/Proxies/Modules.Proxies/Providers/FileImport/ProviderFileParser.cs`
- Test: `src/Tests/Proxies.Tests/Providers/FileImport/ProviderFileParserTests.cs`

**Interfaces:**
- Consumes: `ProviderProxyRecord` (Task 2), `ProxyProtocol`, `ProxyKind`.
- Produces: `FileImportRowError(int LineNumber, string Message)` and `FileImportResult(int Created, int Updated, int Retired, IReadOnlyList<FileImportRowError> Errors)` (both in `Contracts.Dtos`, since `FileImportResult` is the command's public return shape — Task 5 depends on both); `FileImportDefaultCredentials(string? Username, string? Password)`; `ProviderFileParser.Parse(string csvContent) : ProviderFileParseResult`; `ProviderFileParseResult(IReadOnlyList<ProviderProxyRecord> Records, IReadOnlyList<FileImportRowError> Errors)`. `Parse` throws `FormatException` for a structurally unreadable file (no rows, or a header that doesn't match); everything else is a per-row `FileImportRowError`, never fatal.

- [ ] **Step 1: Write the failing tests**

`src/Tests/Proxies.Tests/Providers/FileImport/ProviderFileParserTests.cs`:

```csharp
using FSH.Modules.Proxies.Contracts;
using FSH.Modules.Proxies.Providers.FileImport;
using Shouldly;
using Xunit;

namespace Proxies.Tests.Providers.FileImport;

public sealed class ProviderFileParserTests
{
    private const string Header = "Host,Port,Protocol,Username,Password,Geolocation,ProxyKind";

    [Fact]
    public void Parse_Should_ParseFullyPopulatedRow()
    {
        var csv = $"{Header}\n89.249.195.245,7000,Http,jgwcycpg,ytz1gdtc8ymc,CL,Residential";

        var result = ProviderFileParser.Parse(csv);

        result.Errors.ShouldBeEmpty();
        var record = result.Records.ShouldHaveSingleItem();
        record.ExternalId.ShouldBe("file:89.249.195.245:7000");
        record.Host.ShouldBe("89.249.195.245");
        record.Port.ShouldBe(7000);
        record.Protocol.ShouldBe(ProxyProtocol.Http);
        record.Username.ShouldBe("jgwcycpg");
        record.Password.ShouldBe("ytz1gdtc8ymc");
        record.Geolocation.ShouldBe("CL");
        record.Kind.ShouldBe(ProxyKind.Residential);
        record.IsActive.ShouldBeTrue();
    }

    [Fact]
    public void Parse_Should_TreatBlankOptionalColumns_AsNull_And_DefaultProtocolToHttp()
    {
        var csv = $"{Header}\ndc.oxylabs.io,8007,,,,CL,DataCenter";

        var result = ProviderFileParser.Parse(csv);

        var record = result.Records.ShouldHaveSingleItem();
        record.Protocol.ShouldBe(ProxyProtocol.Http);
        record.Username.ShouldBeNull();
        record.Password.ShouldBeNull();
    }

    [Fact]
    public void Parse_Should_ReportRowError_When_HostIsBlank()
    {
        var csv = $"{Header}\n,8007,Http,u,p,CL,DataCenter";

        var result = ProviderFileParser.Parse(csv);

        result.Records.ShouldBeEmpty();
        var error = result.Errors.ShouldHaveSingleItem();
        error.LineNumber.ShouldBe(2);
        error.Message.ShouldContain("Host");
    }

    [Fact]
    public void Parse_Should_ReportRowError_When_PortIsNotAnInteger()
    {
        var csv = $"{Header}\ndc.oxylabs.io,notaport,Http,u,p,CL,DataCenter";

        var result = ProviderFileParser.Parse(csv);

        result.Records.ShouldBeEmpty();
        result.Errors.ShouldHaveSingleItem().Message.ShouldContain("port");
    }

    [Fact]
    public void Parse_Should_ReportRowError_When_ProtocolIsUnrecognized()
    {
        var csv = $"{Header}\ndc.oxylabs.io,8007,Ftp,u,p,CL,DataCenter";

        var result = ProviderFileParser.Parse(csv);

        result.Records.ShouldBeEmpty();
        result.Errors.ShouldHaveSingleItem().Message.ShouldContain("protocol");
    }

    [Fact]
    public void Parse_Should_ReportRowError_When_ProxyKindIsUnrecognized()
    {
        var csv = $"{Header}\ndc.oxylabs.io,8007,Http,u,p,CL,Satellite";

        var result = ProviderFileParser.Parse(csv);

        result.Records.ShouldBeEmpty();
        result.Errors.ShouldHaveSingleItem().Message.ShouldContain("proxy kind");
    }

    [Fact]
    public void Parse_Should_ContinuePastBadRows_And_KeepValidOnes()
    {
        var csv = $"{Header}\n,8007,Http,u,p,CL,DataCenter\ndc.oxylabs.io,8008,Http,u,p,CL,DataCenter";

        var result = ProviderFileParser.Parse(csv);

        result.Records.ShouldHaveSingleItem().Host.ShouldBe("dc.oxylabs.io");
        result.Errors.ShouldHaveSingleItem().LineNumber.ShouldBe(2);
    }

    [Fact]
    public void Parse_Should_Throw_When_FileIsEmpty() =>
        Should.Throw<FormatException>(() => ProviderFileParser.Parse(""));

    [Fact]
    public void Parse_Should_Throw_When_HeaderDoesNotMatch() =>
        Should.Throw<FormatException>(() => ProviderFileParser.Parse("Wrong,Header\n1.2.3.4,80"));
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test src/Tests/Proxies.Tests --filter "FullyQualifiedName~ProviderFileParserTests"`
Expected: FAIL — `ProviderFileParser` doesn't exist yet.

- [ ] **Step 3: Write the minimal implementation**

`src/Modules/Proxies/Modules.Proxies.Contracts/Dtos/FileImportResult.cs`:

```csharp
namespace FSH.Modules.Proxies.Contracts.Dtos;

public sealed record FileImportRowError(int LineNumber, string Message);

public sealed record FileImportResult(int Created, int Updated, int Retired, IReadOnlyList<FileImportRowError> Errors);
```

`src/Modules/Proxies/Modules.Proxies/Providers/FileImport/FileImportDefaultCredentials.cs`:

```csharp
namespace FSH.Modules.Proxies.Providers.FileImport;

/// <summary>
/// Fallback Username/Password for canonical-format rows that leave those columns blank (e.g. an
/// Oxylabs export, where every proxy shares one account-wide credential entered once). Stored,
/// protected, in the same <c>ProviderAccount.ProtectedCredentials</c> field the live adapters use
/// for their own (differently-shaped) API credentials — see the design spec's "Credential fallback
/// mechanism" section for why sharing the field is safe.
/// </summary>
public sealed record FileImportDefaultCredentials(string? Username, string? Password);
```

`src/Modules/Proxies/Modules.Proxies/Providers/FileImport/ProviderFileParser.cs`:

```csharp
using FSH.Modules.Proxies.Contracts;
using FSH.Modules.Proxies.Contracts.Dtos;

namespace FSH.Modules.Proxies.Providers.FileImport;

public sealed record ProviderFileParseResult(
    IReadOnlyList<ProviderProxyRecord> Records, IReadOnlyList<FileImportRowError> Errors);

/// <summary>
/// Parses the platform's canonical proxy-list CSV format (see the design spec) into
/// <see cref="ProviderProxyRecord"/>s. A pure function of the file's text: no DB/crypto dependency,
/// and blank optional columns stay <c>null</c> here — default-credential/geolocation/kind
/// substitution is the file-import command handler's job (Task 5), not this parser's.
/// </summary>
public static class ProviderFileParser
{
    private static readonly string[] ExpectedHeader =
        ["Host", "Port", "Protocol", "Username", "Password", "Geolocation", "ProxyKind"];

    public static ProviderFileParseResult Parse(string csvContent)
    {
        ArgumentNullException.ThrowIfNull(csvContent);

        var lines = csvContent.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0)
        {
            throw new FormatException("The file is empty.");
        }

        var header = lines[0].Split(',').Select(h => h.Trim()).ToArray();
        if (!header.SequenceEqual(ExpectedHeader, StringComparer.OrdinalIgnoreCase))
        {
            throw new FormatException(
                $"Expected header \"{string.Join(',', ExpectedHeader)}\", got \"{lines[0]}\".");
        }

        var records = new List<ProviderProxyRecord>();
        var errors = new List<FileImportRowError>();

        for (int i = 1; i < lines.Length; i++)
        {
            int lineNumber = i + 1; // 1-based; the header occupies line 1
            var columns = lines[i].Split(',');
            if (columns.Length != ExpectedHeader.Length)
            {
                errors.Add(new FileImportRowError(lineNumber,
                    $"Expected {ExpectedHeader.Length} columns, got {columns.Length}."));
                continue;
            }

            var host = columns[0].Trim();
            if (string.IsNullOrWhiteSpace(host))
            {
                errors.Add(new FileImportRowError(lineNumber, "Host is required."));
                continue;
            }

            var portText = columns[1].Trim();
            if (!int.TryParse(portText, out var port) || port is <= 0 or > 65535)
            {
                errors.Add(new FileImportRowError(lineNumber, $"\"{portText}\" is not a valid port."));
                continue;
            }

            var protocolText = columns[2].Trim();
            var protocol = ProxyProtocol.Http;
            if (protocolText.Length > 0 && !Enum.TryParse(protocolText, ignoreCase: true, out protocol))
            {
                errors.Add(new FileImportRowError(lineNumber,
                    $"\"{protocolText}\" is not a recognized protocol (Http, Https, Socks5)."));
                continue;
            }

            var kindText = columns[6].Trim();
            ProxyKind? kind = null;
            if (kindText.Length > 0)
            {
                if (!Enum.TryParse<ProxyKind>(kindText, ignoreCase: true, out var parsedKind))
                {
                    errors.Add(new FileImportRowError(lineNumber,
                        $"\"{kindText}\" is not a recognized proxy kind (DataCenter, Residential, Mobile, Dedicated)."));
                    continue;
                }
                kind = parsedKind;
            }

            records.Add(new ProviderProxyRecord(
                ExternalId: $"file:{host}:{port}", Host: host, Port: port, Protocol: protocol,
                Username: NullIfBlank(columns[3]), Password: NullIfBlank(columns[4]), IsActive: true,
                Geolocation: NullIfBlank(columns[5]), ProviderGrouping: null, Kind: kind));
        }

        return new ProviderFileParseResult(records, errors);
    }

    private static string? NullIfBlank(string value)
    {
        var trimmed = value.Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test src/Tests/Proxies.Tests --filter "FullyQualifiedName~ProviderFileParserTests"`
Expected: PASS (all 9 tests)

- [ ] **Step 5: Full module regression check**

Run: `dotnet test src/Tests/Proxies.Tests`
Expected: PASS

- [ ] **Step 6: Commit**

```bash
git add src/Modules/Proxies/Modules.Proxies.Contracts/Dtos/FileImportResult.cs \
        src/Modules/Proxies/Modules.Proxies/Providers/FileImport/ \
        src/Tests/Proxies.Tests/Providers/FileImport/
git commit -m "feat(proxies): add canonical CSV parser for provider file import"
```

---

### Task 5: `SyncProviderAccountFromFile` — command, handler, endpoint

**Files:**
- Create: `src/Modules/Proxies/Modules.Proxies.Contracts/v1/ProviderAccounts/SyncProviderAccountFromFileCommand.cs`
- Create: `src/Modules/Proxies/Modules.Proxies/Features/v1/ProviderAccounts/SyncProviderAccountFromFile/SyncProviderAccountFromFileCommandValidator.cs`
- Create: `src/Modules/Proxies/Modules.Proxies/Features/v1/ProviderAccounts/SyncProviderAccountFromFile/SyncProviderAccountFromFileCommandHandler.cs`
- Create: `src/Modules/Proxies/Modules.Proxies/Features/v1/ProviderAccounts/SyncProviderAccountFromFile/SyncProviderAccountFromFileEndpoint.cs`
- Modify: `src/Modules/Proxies/Modules.Proxies/ProxiesModule.cs`
- Test: `src/Tests/Proxies.Tests/Handlers/SyncProviderAccountFromFileHandlerTests.cs`

**Interfaces:**
- Consumes: `ProviderFileParser.Parse` (Task 4), `IProviderAccountSyncService.ReconcileAsync` (Task 3), `FileImportDefaultCredentials` (Task 4), `FileImportResult` (Task 4).
- Produces: `POST /api/v1/proxies/provider-accounts/{id}/sync-from-file` — Task 7's frontend API client calls this exact route and form-field names (`file`, `defaultUsername`, `defaultPassword`, `defaultGeolocation`, `defaultProxyKind`).

- [ ] **Step 1: Write the failing test**

`src/Tests/Proxies.Tests/Handlers/SyncProviderAccountFromFileHandlerTests.cs`:

```csharp
using System.Text.Json;
using FSH.Framework.Core.Exceptions;
using FSH.Modules.Proxies.Contracts;
using FSH.Modules.Proxies.Contracts.v1.ProviderAccounts;
using FSH.Modules.Proxies.Data;
using FSH.Modules.Proxies.Domain;
using FSH.Modules.Proxies.Features.v1.ProviderAccounts.SyncProviderAccountFromFile;
using FSH.Modules.Proxies.Providers.FileImport;
using FSH.Modules.Proxies.Services;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Proxies.Tests.Handlers;

public sealed class SyncProviderAccountFromFileHandlerTests
{
    private const string Header = "Host,Port,Protocol,Username,Password,Geolocation,ProxyKind";

    private static FSH.Modules.Proxies.Data.ProxiesDbContext CreateDb() =>
        Proxies.Tests.TestProxiesDbContext.Create(new DbContextOptionsBuilder<FSH.Modules.Proxies.Data.ProxiesDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private sealed class FakeProtector : IProxySecretProtector
    {
        public string Protect(string plaintext) => plaintext;
        public string Unprotect(string ciphertext) => ciphertext;
    }

    private static SyncProviderAccountFromFileCommandHandler CreateSut(ProxiesDbContext db, IProxySecretProtector protector) =>
        new(db, protector, new ProviderAccountSyncService(db, Substitute.For<FSH.Modules.Proxies.Providers.IProxyProviderAdapterFactory>(),
            protector, Substitute.For<FSH.Framework.Eventing.Abstractions.IOutboxWriter>()));

    [Fact]
    public async Task Handle_Should_ImportRowsWithTheirOwnCredentials()
    {
        await using var db = CreateDb();
        var account = ProviderAccount.Create("WebShare - file", ProxyProviderType.WebShare, "{}");
        db.ProviderAccounts.Add(account);
        await db.SaveChangesAsync();
        var csv = $"{Header}\n89.249.195.245,7000,Http,jgwcycpg,ytz1gdtc8ymc,,";
        var sut = CreateSut(db, new FakeProtector());

        var result = await sut.Handle(
            new SyncProviderAccountFromFileCommand(account.Id, csv, null, null, null, null), CancellationToken.None);

        result.Created.ShouldBe(1);
        result.Errors.ShouldBeEmpty();
        var proxy = await db.Proxies.SingleAsync();
        proxy.Username.ShouldBe("jgwcycpg");
    }

    [Fact]
    public async Task Handle_Should_PersistAndApplyDefaultCredentials_When_RowsOmitThem()
    {
        await using var db = CreateDb();
        var account = ProviderAccount.Create("Oxylabs - file", ProxyProviderType.Oxylabs, "{}");
        db.ProviderAccounts.Add(account);
        await db.SaveChangesAsync();
        var csv = $"{Header}\ndc.oxylabs.io,8007,Http,,,CL,DataCenter";
        var sut = CreateSut(db, new FakeProtector());

        var result = await sut.Handle(
            new SyncProviderAccountFromFileCommand(account.Id, csv, "acct-user", "acct-pass", null, null), CancellationToken.None);

        result.Created.ShouldBe(1);
        var proxy = await db.Proxies.SingleAsync();
        proxy.Username.ShouldBe("acct-user");
        proxy.ProtectedPassword.ShouldBe("acct-pass");
        var stored = await db.ProviderAccounts.SingleAsync();
        var storedDefaults = JsonSerializer.Deserialize<FileImportDefaultCredentials>(stored.ProtectedCredentials)!;
        storedDefaults.Username.ShouldBe("acct-user");
    }

    [Fact]
    public async Task Handle_Should_ApplyDefaultGeolocationAndKind_When_RowsOmitThem()
    {
        await using var db = CreateDb();
        var account = ProviderAccount.Create("BrightData - file", ProxyProviderType.BrightData, "{}");
        db.ProviderAccounts.Add(account);
        await db.SaveChangesAsync();
        var csv = $"{Header}\nbrd.superproxy.io,44445,Http,u,p,,";
        var sut = CreateSut(db, new FakeProtector());

        await sut.Handle(
            new SyncProviderAccountFromFileCommand(account.Id, csv, null, null, "CL", ProxyKind.DataCenter), CancellationToken.None);

        var proxy = await db.Proxies.SingleAsync();
        proxy.Geolocation.ShouldBe("CL");
        proxy.Kind.ShouldBe(ProxyKind.DataCenter);
    }

    [Fact]
    public async Task Handle_Should_Throw_When_RowOmitsCredentials_And_NoDefaultConfigured()
    {
        await using var db = CreateDb();
        var account = ProviderAccount.Create("Oxylabs - file", ProxyProviderType.Oxylabs, "{}");
        db.ProviderAccounts.Add(account);
        await db.SaveChangesAsync();
        var csv = $"{Header}\ndc.oxylabs.io,8007,Http,,,CL,DataCenter";
        var sut = CreateSut(db, new FakeProtector());

        await Should.ThrowAsync<CustomException>(
            () => sut.Handle(new SyncProviderAccountFromFileCommand(account.Id, csv, null, null, null, null), CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task Handle_Should_ReportRowErrors_Without_FailingTheWholeImport()
    {
        await using var db = CreateDb();
        var account = ProviderAccount.Create("WebShare - file", ProxyProviderType.WebShare, "{}");
        db.ProviderAccounts.Add(account);
        await db.SaveChangesAsync();
        var csv = $"{Header}\n,7000,Http,u,p,,\n89.249.195.245,7000,Http,u,p,,";
        var sut = CreateSut(db, new FakeProtector());

        var result = await sut.Handle(
            new SyncProviderAccountFromFileCommand(account.Id, csv, null, null, null, null), CancellationToken.None);

        result.Created.ShouldBe(1);
        result.Errors.ShouldHaveSingleItem().LineNumber.ShouldBe(2);
    }

    [Fact]
    public async Task Handle_Should_RetirePreviouslyImportedProxies_Missing_FromANewUpload()
    {
        await using var db = CreateDb();
        var account = ProviderAccount.Create("WebShare - file", ProxyProviderType.WebShare, "{}");
        db.ProviderAccounts.Add(account);
        await db.SaveChangesAsync();
        var sut = CreateSut(db, new FakeProtector());
        await sut.Handle(new SyncProviderAccountFromFileCommand(
            account.Id, $"{Header}\n89.249.195.245,7000,Http,u,p,,", null, null, null, null), CancellationToken.None);

        var result = await sut.Handle(new SyncProviderAccountFromFileCommand(
            account.Id, $"{Header}\n1.2.3.4,8000,Http,u,p,,", null, null, null, null), CancellationToken.None);

        result.Created.ShouldBe(1);
        result.Retired.ShouldBe(1);
        (await db.Proxies.SingleAsync(p => p.Host == "89.249.195.245")).Status.ShouldBe(ProxyStatus.Retired);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test src/Tests/Proxies.Tests --filter "FullyQualifiedName~SyncProviderAccountFromFileHandlerTests"`
Expected: FAIL — `SyncProviderAccountFromFileCommand`/`SyncProviderAccountFromFileCommandHandler` don't exist yet.

- [ ] **Step 3: Write the minimal implementation**

`src/Modules/Proxies/Modules.Proxies.Contracts/v1/ProviderAccounts/SyncProviderAccountFromFileCommand.cs`:

```csharp
using FSH.Modules.Proxies.Contracts.Dtos;
using Mediator;

namespace FSH.Modules.Proxies.Contracts.v1.ProviderAccounts;

public sealed record SyncProviderAccountFromFileCommand(
    Guid ProviderAccountId, string FileContent,
    string? DefaultUsername, string? DefaultPassword,
    string? DefaultGeolocation, ProxyKind? DefaultProxyKind) : ICommand<FileImportResult>;
```

`src/Modules/Proxies/Modules.Proxies/Features/v1/ProviderAccounts/SyncProviderAccountFromFile/SyncProviderAccountFromFileCommandValidator.cs`:

```csharp
using FluentValidation;
using FSH.Modules.Proxies.Contracts.v1.ProviderAccounts;

namespace FSH.Modules.Proxies.Features.v1.ProviderAccounts.SyncProviderAccountFromFile;

public sealed class SyncProviderAccountFromFileCommandValidator : AbstractValidator<SyncProviderAccountFromFileCommand>
{
    public SyncProviderAccountFromFileCommandValidator()
    {
        RuleFor(x => x.ProviderAccountId).NotEmpty();
        RuleFor(x => x.FileContent).NotEmpty();
    }
}
```

`src/Modules/Proxies/Modules.Proxies/Features/v1/ProviderAccounts/SyncProviderAccountFromFile/SyncProviderAccountFromFileCommandHandler.cs`:

```csharp
using System.Net;
using System.Text.Json;
using FSH.Framework.Core.Exceptions;
using FSH.Modules.Proxies.Contracts.Dtos;
using FSH.Modules.Proxies.Contracts.v1.ProviderAccounts;
using FSH.Modules.Proxies.Data;
using FSH.Modules.Proxies.Providers;
using FSH.Modules.Proxies.Providers.FileImport;
using FSH.Modules.Proxies.Services;
using Mediator;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.Proxies.Features.v1.ProviderAccounts.SyncProviderAccountFromFile;

public sealed class SyncProviderAccountFromFileCommandHandler(
    ProxiesDbContext dbContext, IProxySecretProtector protector, IProviderAccountSyncService syncService)
    : ICommandHandler<SyncProviderAccountFromFileCommand, FileImportResult>
{
    public async ValueTask<FileImportResult> Handle(SyncProviderAccountFromFileCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var account = await dbContext.ProviderAccounts.FirstOrDefaultAsync(x => x.Id == command.ProviderAccountId, cancellationToken).ConfigureAwait(false)
            ?? throw new NotFoundException($"Provider account {command.ProviderAccountId} not found.");

        if (command.DefaultUsername is not null || command.DefaultPassword is not null)
        {
            var defaults = new FileImportDefaultCredentials(command.DefaultUsername, command.DefaultPassword);
            account.UpdateCredentials(protector.Protect(JsonSerializer.Serialize(defaults)));
        }

        ProviderFileParseResult parsed;
        try
        {
            parsed = ProviderFileParser.Parse(command.FileContent);
        }
        catch (FormatException ex)
        {
            throw new CustomException(ex.Message, (IEnumerable<string>?)null, HttpStatusCode.BadRequest);
        }

        FileImportDefaultCredentials? storedDefaults = null;
        if (parsed.Records.Any(r => r.Username is null || r.Password is null))
        {
            storedDefaults = JsonSerializer.Deserialize<FileImportDefaultCredentials>(protector.Unprotect(account.ProtectedCredentials));
            if (storedDefaults?.Username is null || storedDefaults.Password is null)
            {
                throw new CustomException(
                    "One or more rows omit Username/Password and no default credentials are configured for this account. "
                    + "Pass defaultUsername/defaultPassword on this upload once to set them.",
                    (IEnumerable<string>?)null, HttpStatusCode.BadRequest);
            }
        }

        var resolved = parsed.Records.Select(r => r with
        {
            Username = r.Username ?? storedDefaults!.Username,
            Password = r.Password ?? storedDefaults!.Password,
            Geolocation = r.Geolocation ?? command.DefaultGeolocation,
            Kind = r.Kind ?? command.DefaultProxyKind,
        }).ToList();

        // ReconcileAsync tracks changes on `dbContext` via the same scoped instance this handler
        // holds — both resolve from the same DI scope, so the single SaveChangesAsync below
        // flushes the reconciled Proxy rows and the RecordSyncResult update together.
        var (created, updated, retired) = await syncService.ReconcileAsync(account, resolved, cancellationToken).ConfigureAwait(false);

        account.RecordSyncResult(success: true, statusMessage: $"Imported {resolved.Count} proxies from file.");
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return new FileImportResult(created, updated, retired, parsed.Errors);
    }
}
```

`src/Modules/Proxies/Modules.Proxies/Features/v1/ProviderAccounts/SyncProviderAccountFromFile/SyncProviderAccountFromFileEndpoint.cs`:

```csharp
using FSH.Framework.Shared.Identity.Authorization;
using FSH.Modules.Proxies.Contracts;
using FSH.Modules.Proxies.Contracts.Authorization;
using FSH.Modules.Proxies.Contracts.v1.ProviderAccounts;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace FSH.Modules.Proxies.Features.v1.ProviderAccounts.SyncProviderAccountFromFile;

public static class SyncProviderAccountFromFileEndpoint
{
    internal static RouteHandlerBuilder MapSyncProviderAccountFromFileEndpoint(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapPost("/provider-accounts/{id:guid}/sync-from-file",
                async (Guid id, [FromForm] IFormFile file, [FromForm] string? defaultUsername,
                    [FromForm] string? defaultPassword, [FromForm] string? defaultGeolocation,
                    [FromForm] string? defaultProxyKind, IMediator mediator, CancellationToken ct) =>
                {
                    ProxyKind? kind = null;
                    if (!string.IsNullOrWhiteSpace(defaultProxyKind))
                    {
                        if (!Enum.TryParse(defaultProxyKind, ignoreCase: true, out ProxyKind parsedKind))
                        {
                            return Results.BadRequest(new
                            {
                                title = "Invalid defaultProxyKind",
                                detail = $"\"{defaultProxyKind}\" is not a recognized proxy kind (DataCenter, Residential, Mobile, Dedicated).",
                            });
                        }
                        kind = parsedKind;
                    }

                    using var reader = new StreamReader(file.OpenReadStream());
                    var content = await reader.ReadToEndAsync(ct).ConfigureAwait(false);

                    var result = await mediator.Send(
                        new SyncProviderAccountFromFileCommand(id, content, defaultUsername, defaultPassword, defaultGeolocation, kind),
                        ct).ConfigureAwait(false);
                    return Results.Ok(result);
                })
            .DisableAntiforgery()
            .WithName("SyncProviderAccountFromFile")
            .WithSummary("Sync a provider account's proxies from an uploaded canonical-format CSV file")
            .RequirePermission(ProxiesPermissions.ProviderAccounts.Update);
}
```

Wire it up in `src/Modules/Proxies/Modules.Proxies/ProxiesModule.cs`:
1. Add `using FSH.Modules.Proxies.Features.v1.ProviderAccounts.SyncProviderAccountFromFile;` alongside the existing `using ...SyncProviderAccountNow;` line.
2. Add `group.MapSyncProviderAccountFromFileEndpoint();` directly below the existing `group.MapSyncProviderAccountNowEndpoint();` line.

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test src/Tests/Proxies.Tests --filter "FullyQualifiedName~SyncProviderAccountFromFileHandlerTests"`
Expected: PASS (all 6 tests)

- [ ] **Step 5: Full solution build + module regression check**

```bash
dotnet build src/FS.Proxy.slnx
dotnet test src/Tests/Proxies.Tests
```

Expected: PASS — the build step in particular confirms the minimal-API multipart binding (`[FromForm] IFormFile`, `.DisableAntiforgery()`) compiles against this project's actual ASP.NET Core version; this endpoint is the first `IFormFile`-binding endpoint in the codebase, so there's no existing precedent to diff against.

- [ ] **Step 6: Commit**

```bash
git add src/Modules/Proxies/Modules.Proxies.Contracts/v1/ProviderAccounts/SyncProviderAccountFromFileCommand.cs \
        src/Modules/Proxies/Modules.Proxies/Features/v1/ProviderAccounts/SyncProviderAccountFromFile/ \
        src/Modules/Proxies/Modules.Proxies/ProxiesModule.cs \
        src/Tests/Proxies.Tests/Handlers/SyncProviderAccountFromFileHandlerTests.cs
git commit -m "feat(proxies): add sync-from-file endpoint for provider accounts"
```

---

### Task 6: `ProxyKind` filter on `ListProxies`

**Files:**
- Modify: `src/Modules/Proxies/Modules.Proxies.Contracts/Dtos/ProxyDto.cs`
- Modify: `src/Modules/Proxies/Modules.Proxies.Contracts/v1/Proxies/ListProxiesQuery.cs`
- Modify: `src/Modules/Proxies/Modules.Proxies/Features/v1/Proxies/ListProxies/ListProxiesQueryHandler.cs`
- Modify: `src/Modules/Proxies/Modules.Proxies/Features/v1/Proxies/ListProxies/ListProxiesEndpoint.cs`
- Test: `src/Tests/Proxies.Tests/Handlers/ListProxiesHandlerTests.cs`

**Interfaces:**
- Produces: `ProxyDto` gains a trailing `ProxyKind? Kind` field; `ListProxiesQuery` gains an optional `ProxyKind? Kind = null` filter — Task 7's frontend `ProxyDto`/`ListProxiesParams` types mirror this exact field name (`kind`, lowercase on the wire per this API's existing camelCase JSON convention).

- [ ] **Step 1: Write the failing test**

Add to `src/Tests/Proxies.Tests/Handlers/ListProxiesHandlerTests.cs`:

```csharp
[Fact]
public async Task Handle_Should_FilterByKind()
{
    await using var db = CreateDb();
    var account = ProviderAccount.Create("Manual", ProxyProviderType.Manual, "protected:x");
    var dataCenter = Proxy.Create(account.Id, "1.1.1.1", 80, ProxyProtocol.Http, null, null, null,
        geolocation: null, providerGrouping: null, kind: ProxyKind.DataCenter);
    var residential = Proxy.Create(account.Id, "2.2.2.2", 80, ProxyProtocol.Http, null, null, null,
        geolocation: null, providerGrouping: null, kind: ProxyKind.Residential);
    db.ProviderAccounts.Add(account);
    db.Proxies.AddRange(dataCenter, residential);
    await db.SaveChangesAsync();
    var sut = new ListProxiesQueryHandler(db);

    var result = await sut.Handle(new ListProxiesQuery(null, null, null, Kind: ProxyKind.DataCenter), CancellationToken.None);

    result.Items.Select(x => x.Id).ShouldBe([dataCenter.Id]);
    result.Items.Single().Kind.ShouldBe(ProxyKind.DataCenter);
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test src/Tests/Proxies.Tests --filter "FullyQualifiedName~ListProxiesHandlerTests"`
Expected: FAIL — `ListProxiesQuery` has no `Kind` parameter, `ProxyDto` has no `Kind` member.

- [ ] **Step 3: Write the minimal implementation**

`ProxyDto.cs` — add `ProxyKind? Kind` as the new trailing member:

```csharp
public sealed record ProxyDto(
    Guid Id, string Host, int Port, ProxyProtocol Protocol, ProxyStatus Status,
    Guid ProviderAccountId, string ProviderAccountName, ProxyProviderType ProviderType,
    IReadOnlyList<string> Tags, DateTime CreatedAtUtc, DateTime? LastRenewedAtUtc,
    string? Geolocation, string? ProviderGrouping, ProxyKind? Kind);
```

`ListProxiesQuery.cs` — add `Kind` as the new trailing optional parameter:

```csharp
public sealed record ListProxiesQuery(
    IReadOnlyList<string>? Tags, ProxyStatus? Status, Guid? ProviderAccountId,
    string? Geolocation = null, ProxyKind? Kind = null, int PageNumber = 1, int PageSize = 20) : IQuery<PagedResponse<ProxyDto>>;
```

`ListProxiesQueryHandler.cs` — add the filter (directly after the existing `Geolocation` filter block) and pass `p.Kind` into the final `ProxyDto` projection:

```csharp
if (query.Kind is { } kind) q = q.Where(p => p.Kind == kind);
```

```csharp
var items = page.Select(p => new ProxyDto(
    p.Id, p.Host, p.Port, p.Protocol, p.Status,
    p.ProviderAccountId, accountNames[p.ProviderAccountId].Name, accountNames[p.ProviderAccountId].ProviderType,
    tagsByProxy.Where(t => t.ProxyId == p.Id).Select(t => t.Name).ToList(),
    p.CreatedAtUtc, p.LastRenewedAtUtc, p.Geolocation, p.ProviderGrouping, p.Kind)).ToList();
```

`ListProxiesEndpoint.cs` — add the `kind` query parameter and pass it through:

```csharp
return endpoints.MapGet("/",
        (string[]? tags, ProxyStatus? status, Guid? providerAccountId, string? geolocation, ProxyKind? kind,
            int pageNumber, int pageSize, IMediator mediator, CancellationToken ct) =>
            mediator.Send(new ListProxiesQuery(tags, status, providerAccountId, geolocation, kind, pageNumber == 0 ? 1 : pageNumber, pageSize == 0 ? 20 : pageSize), ct))
    .WithName("ListProxies")
    .WithSummary("List proxies (paged, filterable by tags/status/provider account/geolocation/kind)")
    .RequirePermission(ProxiesPermissions.ProviderAccounts.View);
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test src/Tests/Proxies.Tests --filter "FullyQualifiedName~ListProxiesHandlerTests"`
Expected: PASS (all tests in this file)

- [ ] **Step 5: Full solution build + regression check**

```bash
dotnet build src/FS.Proxy.slnx
dotnet test src/Tests/Proxies.Tests
```

Expected: PASS

- [ ] **Step 6: Commit**

```bash
git add src/Modules/Proxies/Modules.Proxies.Contracts/Dtos/ProxyDto.cs \
        src/Modules/Proxies/Modules.Proxies.Contracts/v1/Proxies/ListProxiesQuery.cs \
        src/Modules/Proxies/Modules.Proxies/Features/v1/Proxies/ListProxies/ \
        src/Tests/Proxies.Tests/Handlers/ListProxiesHandlerTests.cs
git commit -m "feat(proxies): add ProxyKind filter to ListProxies"
```

---

### Task 7: Frontend API client — `ProxyKind` + `syncProviderAccountFromFile`

**Files:**
- Modify: `clients/admin/src/api/proxies.ts`
- Modify: `clients/admin/src/api/provider-accounts.ts`

**Interfaces:**
- Consumes: `POST /provider-accounts/{id}/sync-from-file` (Task 5, multipart form fields `file`/`defaultUsername`/`defaultPassword`/`defaultGeolocation`/`defaultProxyKind`), `GET /?...&kind=...` (Task 6).
- Produces: `ProxyKind` TS union type; `ProxyDto.kind: ProxyKind | null`; `ListProxiesParams.kind?: ProxyKind`; `FileImportResult` type; `syncProviderAccountFromFile(id, input): Promise<FileImportResult>` — Task 8's dialog calls this exact function.

- [ ] **Step 1: Add the types and filter param to `proxies.ts`**

```typescript
export type ProxyKind = "DataCenter" | "Residential" | "Mobile" | "Dedicated";

export type ProxyDto = {
  // ...existing fields unchanged...
  kind: ProxyKind | null;
};

export type ListProxiesParams = {
  // ...existing fields unchanged...
  kind?: ProxyKind;
};
```

In `listProxies`, add: `if (params.kind) query.set("kind", params.kind);` alongside the existing `geolocation` line.

- [ ] **Step 2: Add `FileImportResult` type and `syncProviderAccountFromFile` to `provider-accounts.ts`**

```typescript
import type { ProxyKind } from "./proxies";

export type FileImportRowError = { lineNumber: number; message: string };
export type FileImportResult = { created: number; updated: number; retired: number; errors: FileImportRowError[] };

export type SyncProviderAccountFromFileInput = {
  file: File;
  defaultUsername?: string;
  defaultPassword?: string;
  defaultGeolocation?: string;
  defaultProxyKind?: ProxyKind;
};

export async function syncProviderAccountFromFile(
  id: string,
  input: SyncProviderAccountFromFileInput,
): Promise<FileImportResult> {
  const formData = new FormData();
  formData.set("file", input.file);
  if (input.defaultUsername) formData.set("defaultUsername", input.defaultUsername);
  if (input.defaultPassword) formData.set("defaultPassword", input.defaultPassword);
  if (input.defaultGeolocation) formData.set("defaultGeolocation", input.defaultGeolocation);
  if (input.defaultProxyKind) formData.set("defaultProxyKind", input.defaultProxyKind);
  return apiFetch<FileImportResult>(`${BASE}/${id}/sync-from-file`, { method: "POST", body: formData });
}
```

(`apiFetch` only stamps a `Content-Type: application/json` header when the request body is a `string` — passing a `FormData` body here leaves the header unset, so the browser sets the correct `multipart/form-data; boundary=...` value itself. No change needed in `api-client.ts`.)

- [ ] **Step 3: Type-check**

Run: `cd clients/admin && npm run build`
Expected: PASS (no TypeScript errors)

- [ ] **Step 4: Commit**

```bash
git add clients/admin/src/api/proxies.ts clients/admin/src/api/provider-accounts.ts
git commit -m "feat(admin): add ProxyKind and file-import API client functions"
```

---

### Task 8: Admin UI — upload-file dialog on the Provider Accounts page

**Files:**
- Create: `clients/admin/src/components/proxies/upload-provider-file-dialog.tsx`
- Modify: `clients/admin/src/pages/proxies/provider-accounts.tsx`
- Test: `clients/admin/tests/proxies/upload-provider-file-dialog.spec.ts`

**Interfaces:**
- Consumes: `syncProviderAccountFromFile` (Task 7), `ConfirmDialog`-adjacent visual language (`Dialog`/`DialogBody`/`DialogFooter` from `@/components/ui/dialog`, `Field`/`ErrorBand` from `@/components/list`), `ProxyKind` (Task 7).

- [ ] **Step 1: Write the failing Playwright test**

`clients/admin/tests/proxies/upload-provider-file-dialog.spec.ts`:

```typescript
import { expect, test } from "@playwright/test";
import { seedAuthedSession, TEST_USER } from "../helpers/auth-seed";
import { installAdminShellMocks, ADMIN_PERMS, paged } from "../helpers/shell-mocks";

const ACCOUNT = {
  id: "acc-1",
  name: "Oxylabs - CL",
  providerType: "Oxylabs",
  isEnabled: true,
  lastSyncedAtUtc: null,
  lastSyncStatus: null,
  consecutiveSyncFailures: 0,
};

test.beforeEach(async ({ page }) => {
  await seedAuthedSession(page, { ...TEST_USER, permissions: [...ADMIN_PERMS] });
  await installAdminShellMocks(page);
  await page.route("**/api/v1/proxies/provider-accounts*", async (route) => {
    if (route.request().method() === "GET") {
      await route.fulfill({ status: 200, headers: { "Content-Type": "application/json" }, body: JSON.stringify(paged([ACCOUNT])) });
    } else {
      await route.fallback();
    }
  });
});

test.describe("upload provider file dialog", () => {
  test("uploads a file with default credentials and shows the result summary", async ({ page }) => {
    let capturedForm: { fileName?: string; defaultUsername?: string } = {};
    await page.route("**/api/v1/proxies/provider-accounts/acc-1/sync-from-file", async (route) => {
      const request = route.request();
      const body = request.postDataBuffer()?.toString("utf-8") ?? "";
      capturedForm = {
        fileName: /filename="([^"]+)"/.exec(body)?.[1],
        defaultUsername: /name="defaultUsername"\r\n\r\n([^\r\n]+)/.exec(body)?.[1],
      };
      await route.fulfill({
        status: 200,
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ created: 10, updated: 0, retired: 0, errors: [] }),
      });
    });

    await page.goto("/proxies/provider-accounts");
    await expect(page.getByRole("heading", { name: "Provider accounts", exact: true })).toBeVisible({ timeout: 10_000 });

    await page.getByRole("button", { name: "Upload file for Oxylabs - CL" }).click();
    await page.setInputFiles('input[type="file"]', {
      name: "oxylabs.csv",
      mimeType: "text/csv",
      buffer: Buffer.from("Host,Port,Protocol,Username,Password,Geolocation,ProxyKind\ndc.oxylabs.io,8007,Http,,,CL,DataCenter"),
    });
    await page.getByLabel("Default username").fill("acct-user");
    await page.getByLabel("Default password").fill("acct-pass");
    await page.getByRole("button", { name: "Upload", exact: true }).click();

    await expect(page.getByText("10 created, 0 updated, 0 retired", { exact: true })).toBeVisible({ timeout: 10_000 });
    expect(capturedForm.fileName).toBe("oxylabs.csv");
    expect(capturedForm.defaultUsername).toBe("acct-user");
  });

  test("shows per-row errors when the response includes them", async ({ page }) => {
    await page.route("**/api/v1/proxies/provider-accounts/acc-1/sync-from-file", async (route) => {
      await route.fulfill({
        status: 200,
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ created: 1, updated: 0, retired: 0, errors: [{ lineNumber: 3, message: "Host is required." }] }),
      });
    });

    await page.goto("/proxies/provider-accounts");
    await expect(page.getByRole("heading", { name: "Provider accounts", exact: true })).toBeVisible({ timeout: 10_000 });
    await page.getByRole("button", { name: "Upload file for Oxylabs - CL" }).click();
    await page.setInputFiles('input[type="file"]', {
      name: "oxylabs.csv",
      mimeType: "text/csv",
      buffer: Buffer.from("Host,Port,Protocol,Username,Password,Geolocation,ProxyKind\n"),
    });
    await page.getByRole("button", { name: "Upload", exact: true }).click();

    await expect(page.getByText("line 3: Host is required.", { exact: true })).toBeVisible({ timeout: 10_000 });
  });
});
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `cd clients/admin && npx playwright test tests/proxies/upload-provider-file-dialog.spec.ts`
Expected: FAIL — the "Upload file for Oxylabs - CL" button doesn't exist yet.

- [ ] **Step 3: Write the minimal implementation**

`clients/admin/src/components/proxies/upload-provider-file-dialog.tsx`:

```tsx
import { useState } from "react";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { Loader2 } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Select } from "@/components/ui/select";
import { Dialog, DialogBody, DialogContent, DialogFooter, DialogHeader, DialogTitle } from "@/components/ui/dialog";
import { Field } from "@/components/list";
import { ApiRequestError } from "@/lib/api-client";
import { syncProviderAccountFromFile, type FileImportResult, type ProviderAccountDto } from "@/api/provider-accounts";
import type { ProxyKind } from "@/api/proxies";

const PROXY_KIND_OPTIONS: { value: ProxyKind; label: string }[] = [
  { value: "DataCenter", label: "DataCenter" },
  { value: "Residential", label: "Residential" },
  { value: "Mobile", label: "Mobile" },
  { value: "Dedicated", label: "Dedicated" },
];

function describeError(err: unknown): string {
  if (err instanceof ApiRequestError) return err.problem?.detail ?? err.problem?.title ?? err.message;
  if (err instanceof Error) return err.message;
  return "Something went wrong.";
}

export function UploadProviderFileDialog({
  open,
  account,
  onClose,
}: {
  open: boolean;
  account: ProviderAccountDto | null;
  onClose: () => void;
}) {
  const queryClient = useQueryClient();
  const [file, setFile] = useState<File | null>(null);
  const [defaultUsername, setDefaultUsername] = useState("");
  const [defaultPassword, setDefaultPassword] = useState("");
  const [defaultGeolocation, setDefaultGeolocation] = useState("");
  const [defaultProxyKind, setDefaultProxyKind] = useState<ProxyKind | "">("");
  const [result, setResult] = useState<FileImportResult | null>(null);

  const mutation = useMutation({
    mutationFn: () =>
      syncProviderAccountFromFile(account!.id, {
        file: file!,
        defaultUsername: defaultUsername || undefined,
        defaultPassword: defaultPassword || undefined,
        defaultGeolocation: defaultGeolocation || undefined,
        defaultProxyKind: defaultProxyKind || undefined,
      }),
    onSuccess: (r) => {
      setResult(r);
      void queryClient.invalidateQueries({ queryKey: ["proxies", "provider-accounts"] });
      void queryClient.invalidateQueries({ queryKey: ["proxies", "list"] });
    },
    onError: (err) => toast.error("Upload failed", { description: describeError(err) }),
  });

  function handleClose() {
    setFile(null);
    setDefaultUsername("");
    setDefaultPassword("");
    setDefaultGeolocation("");
    setDefaultProxyKind("");
    setResult(null);
    onClose();
  }

  if (!account) return null;

  return (
    <Dialog open={open} onOpenChange={(o) => !o && handleClose()}>
      <DialogContent>
        <DialogHeader>
          <DialogTitle>Upload proxy list for {account.name}</DialogTitle>
        </DialogHeader>
        <DialogBody className="space-y-4">
          <Field id="upload-file" label="CSV file" required>
            <input
              type="file"
              accept=".csv,text/csv"
              onChange={(e) => setFile(e.target.files?.[0] ?? null)}
            />
          </Field>
          <Field id="upload-default-username" label="Default username" hint="Used for any row that leaves Username blank.">
            <Input
              id="upload-default-username"
              aria-label="Default username"
              value={defaultUsername}
              onChange={(e) => setDefaultUsername(e.target.value)}
            />
          </Field>
          <Field id="upload-default-password" label="Default password">
            <Input
              id="upload-default-password"
              aria-label="Default password"
              type="password"
              value={defaultPassword}
              onChange={(e) => setDefaultPassword(e.target.value)}
            />
          </Field>
          <Field id="upload-default-geolocation" label="Default geolocation">
            <Input
              id="upload-default-geolocation"
              value={defaultGeolocation}
              onChange={(e) => setDefaultGeolocation(e.target.value)}
              placeholder="CL"
            />
          </Field>
          <Field id="upload-default-kind" label="Default proxy kind">
            <Select
              value={defaultProxyKind}
              onChange={(v) => setDefaultProxyKind(v as ProxyKind | "")}
              options={PROXY_KIND_OPTIONS}
              placeholder="— none —"
              className="w-full"
              minWidth="100%"
            />
          </Field>

          {result && (
            <div className="rounded-lg border border-[var(--color-border)] p-3 text-[13px]">
              <p>
                {result.created} created, {result.updated} updated, {result.retired} retired
              </p>
              {result.errors.length > 0 && (
                <ul className="mt-2 space-y-1 text-[var(--color-destructive)]">
                  {result.errors.map((e) => (
                    <li key={e.lineNumber}>
                      line {e.lineNumber}: {e.message}
                    </li>
                  ))}
                </ul>
              )}
            </div>
          )}
        </DialogBody>
        <DialogFooter>
          <Button type="button" variant="outline" onClick={handleClose}>
            Close
          </Button>
          <Button
            type="button"
            onClick={() => mutation.mutate()}
            disabled={!file || mutation.isPending}
            className="min-w-[8.5rem]"
          >
            {mutation.isPending ? (
              <>
                <Loader2 className="size-4 animate-spin" aria-hidden />
                <span>Uploading…</span>
              </>
            ) : (
              "Upload"
            )}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}
```

In `clients/admin/src/pages/proxies/provider-accounts.tsx`:
1. Import `UploadProviderFileDialog` and add `const [uploadState, setUploadState] = useState<{ open: boolean; account?: ProviderAccountDto }>({ open: false });` next to the existing `dialogState`.
2. In the `Row` component's action buttons (next to the existing "Sync now" button), add:

```tsx
<Button variant="outline" size="sm" onClick={onUploadFile} aria-label={`Upload file for ${account.name}`}>
  Upload file
</Button>
```

(add `onUploadFile: () => void` to `Row`'s props, passed from the parent as `onUploadFile={() => setUploadState({ open: true, account })}`).

3. Render the dialog next to the existing `<ProviderAccountDialog .../>`:

```tsx
<UploadProviderFileDialog
  open={uploadState.open}
  account={uploadState.account ?? null}
  onClose={() => setUploadState({ open: false })}
/>
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `cd clients/admin && npx playwright test tests/proxies/upload-provider-file-dialog.spec.ts`
Expected: PASS (both tests)

- [ ] **Step 5: Full frontend regression check**

```bash
cd clients/admin
npm run build
npm run lint
npx playwright test tests/proxies/
```

Expected: PASS (build/lint clean, every proxies spec green)

- [ ] **Step 6: Commit**

```bash
git add clients/admin/src/components/proxies/upload-provider-file-dialog.tsx \
        clients/admin/src/pages/proxies/provider-accounts.tsx \
        clients/admin/tests/proxies/upload-provider-file-dialog.spec.ts
git commit -m "feat(admin): add provider file upload dialog"
```

---

### Task 9: Admin UI — `ProxyKind` column and filter on the Proxies list

**Files:**
- Modify: `clients/admin/src/pages/proxies/list.tsx`
- Modify: `clients/admin/tests/proxies/proxies-list.spec.ts`

**Interfaces:**
- Consumes: `ProxyDto.kind`/`ListProxiesParams.kind` (Task 7).

- [ ] **Step 1: Extend the failing Playwright test**

Add to `clients/admin/tests/proxies/proxies-list.spec.ts` — a **separate** fixture rather than extending the shared `PROXY_CL` (which the existing "shows the provider-reported geolocation" test asserts an exact, `kind`-less string against; giving it a `kind` too would break that unrelated assertion):

```typescript
const PROXY_RESIDENTIAL = {
  ...PROXY_CL,
  id: "22222222-2222-2222-2222-222222222222",
  host: "10.0.0.6",
  kind: "Residential",
};

test("filters by ProxyKind", async ({ page }) => {
  let lastUrl = "";
  await page.route("**/api/v1/proxies/?*", async (route) => {
    lastUrl = route.request().url();
    await route.fulfill({ status: 200, headers: { "Content-Type": "application/json" }, body: JSON.stringify(paged([PROXY_RESIDENTIAL])) });
  });

  await page.goto("/proxies");
  await expect(page.getByRole("heading", { name: "Proxies", exact: true })).toBeVisible({ timeout: 10_000 });
  await expect(page.getByRole("listitem").getByText("Http · 🇨🇱 CL · Residential", { exact: true })).toBeVisible();

  await page.getByTestId("proxies-kind-select").getByRole("button").click();
  await page.getByRole("menuitem", { name: "Residential", exact: true }).click();

  await expect.poll(() => new URL(lastUrl).searchParams.get("kind")).toBe("Residential");
});
```

(`PROXY_CL` itself has no `kind` — its existing `null`-equivalent absence means `proxy.kind` is falsy and the appended `` ` · ${proxy.kind}` `` piece renders as `""`, exactly as today; the untouched "shows the provider-reported geolocation" test keeps passing unmodified.)

- [ ] **Step 2: Run the test to verify it fails**

Run: `cd clients/admin && npx playwright test tests/proxies/proxies-list.spec.ts`
Expected: FAIL — no `proxies-kind-select` testid, no "Residential" text rendered anywhere.

- [ ] **Step 3: Write the minimal implementation**

In `clients/admin/src/pages/proxies/list.tsx`:

1. Add `import type { ProxyKind } from "@/api/proxies";` (alongside the existing `type ProxyStatus` import from the same module).
2. Add state, right after the existing `const [geolocation, setGeolocation] = useState("");` line: `const [kind, setKind] = useState<ProxyKind | "">("");`.
3. Extend the page-reset effect to include it:

```tsx
// Reset to page 1 whenever a dropdown filter changes.
useEffect(() => {
  setPageNumber(1);
}, [status, providerAccountId, kind]);
```

4. Extend the query to include it:

```tsx
const proxiesQuery = useQuery({
  queryKey: ["proxies", "list", { pageNumber, tags, status, providerAccountId, geolocation, kind }],
  queryFn: () =>
    listProxies({
      pageNumber,
      pageSize: PAGE_SIZE,
      tags: tags.length > 0 ? tags : undefined,
      status: status || undefined,
      providerAccountId: providerAccountId || undefined,
      geolocation: geolocation || undefined,
      kind: kind || undefined,
    }),
  placeholderData: keepPreviousData,
});
```

5. Add a `Select` filter next to the existing "Provider account" one:

```tsx
<div data-testid="proxies-kind-select">
  <Select
    label="Kind"
    value={kind}
    onChange={(v) => setKind(v as ProxyKind | "")}
    options={[
      { value: "DataCenter", label: "DataCenter" },
      { value: "Residential", label: "Residential" },
      { value: "Mobile", label: "Mobile" },
      { value: "Dedicated", label: "Dedicated" },
    ]}
    placeholder="Any kind"
    minWidth="9rem"
  />
</div>
```

6. Update `filtersActive`:

```tsx
const filtersActive = tags.length > 0 || status !== "" || providerAccountId !== "" || geolocation !== "" || kind !== "";
```

7. Render `proxy.kind` next to the existing geolocation text — in `ProxyDesktopRow` (the line currently reading `{proxy.geolocation ? ... : proxy.protocol}`):

```tsx
<span className="block truncate font-mono text-[11px] text-[var(--color-muted-foreground)]">
  {proxy.geolocation ? `${proxy.protocol} · ${countryFlag(proxy.geolocation)} ${proxy.geolocation}` : proxy.protocol}
  {proxy.kind ? ` · ${proxy.kind}` : ""}
</span>
```

And in `ProxyMobileCard` (the line currently reading `{proxy.geolocation ? \`, ${countryFlag(...)} ...\` : ""})`):

```tsx
<p className="mt-0.5 truncate text-[11px] text-[var(--color-muted-foreground)]">
  {proxy.providerAccountName} (
  {proxy.providerGrouping ? `${proxy.providerType} · ${proxy.providerGrouping}` : proxy.providerType}
  {proxy.geolocation ? `, ${countryFlag(proxy.geolocation)} ${proxy.geolocation}` : ""}
  {proxy.kind ? `, ${proxy.kind}` : ""})
</p>
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `cd clients/admin && npx playwright test tests/proxies/proxies-list.spec.ts`
Expected: PASS (all tests in this file, including the new one)

- [ ] **Step 5: Full frontend regression check**

```bash
cd clients/admin
npm run build
npm run lint
npx playwright test tests/proxies/
```

Expected: PASS

- [ ] **Step 6: Commit**

```bash
git add clients/admin/src/pages/proxies/list.tsx clients/admin/tests/proxies/proxies-list.spec.ts
git commit -m "feat(admin): add ProxyKind column and filter to the proxies list"
```

---

## Final Verification

After all 9 tasks:

```bash
dotnet build src/FS.Proxy.slnx
dotnet test src/Tests/Proxies.Tests
cd clients/admin && npm run build && npm run lint && npx playwright test tests/proxies/
```

Expected: everything green. Then hand off to `superpowers:finishing-a-development-branch` for the merge/PR decision.

**Residual coverage gap worth a manual check:** this feature's endpoint is the first place in the codebase binding `IFormFile` from a minimal API (Task 5). The C# handler tests call the handler directly (bypassing HTTP entirely) and the Playwright tests mock the network layer (bypassing the real backend) — neither exercises ASP.NET Core's actual multipart/`[FromForm]` binding against a live server. Before considering this feature done, do one manual smoke test against a running `FS.Proxy.Api` (e.g. `curl -F file=@oxylabs.csv -F defaultUsername=x -F defaultPassword=y https://localhost:7030/api/v1/proxies/provider-accounts/{id}/sync-from-file -H "Authorization: Bearer ..."`) to confirm the real binding behaves as every other layer assumes.
