# Framework-Owned Transactional Outbox (Option C1) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the per-DbContext outbox/inbox with a single framework-owned `EventingDbContext` that follows the tenant's connection, so any number of modules can publish integration events without DI ambiguity, and every tenant's outbox actually gets dispatched.

**Architecture:** `OutboxMessage`/`InboxMessage` move out of `IdentityDbContext` into a new `EventingDbContext` (schema `framework`) that derives from `BaseDbContext`, inheriting per-tenant connection routing. Because there is exactly one context that owns these tables, `IOutboxStore`/`IInboxStore` become non-generic with a single DI registration — the last-registration-wins ambiguity is gone by construction, and `AddEventingForDbContext<T>` is deleted. The dispatcher stops being a single-database poller: it enumerates distinct *drain targets* (the shared DB plus one per distinct tenant connection string) and drains each under the right tenant context. Outbox rows gain a lease (`ClaimedUntilUtc`/`ClaimedBy`) claimed via `FOR UPDATE SKIP LOCKED` so multiple API instances can run safely. Finally, `EventingDbContext` shares the caller's `DbConnection` and enlists in its ambient transaction, making the outbox write genuinely atomic with the business write.

**Tech Stack:** .NET 10, EF Core 10, Npgsql, Finbuckle 10, xUnit + Shouldly + NSubstitute, Testcontainers (integration), NetArchTest (architecture).

## Global Constraints

- Target framework `net10.0`; C# latest. Build runs with `TreatWarningsAsErrors` — warnings fail the build.
- `src/BuildingBlocks` is protected (AGENTS.md golden rule #4). This plan is the explicit approval for the files it names; do not widen the blast radius beyond them without checking in.
- Mediator handlers must be `public sealed`, return `ValueTask<T>`, `.ConfigureAwait(false)` every await.
- Structured logging only — message templates or `[LoggerMessage]`, never string interpolation.
- Propagate `CancellationToken` into every EF/IO call; `= default` on public service methods.
- File-scoped namespaces, 4-space indent, explicit types (`var` only when RHS-obvious), `is null`/`is not null`, records for DTOs/events/value objects.
- Tenant isolation is default-ON via `BaseDbContext`; `OutboxMessage`/`InboxMessage` stay `IGlobalEntity`. Subclass DbContexts call `base.OnModelCreating` **last**.
- All EF migrations live in `src/Host/FS.Proxy.Migrations.PostgreSQL`, one folder per context.
- Branch: work directly on `main` (user directive, 2026-08-06). Commit per task; do not push without asking.
- Docs travel with the change (golden rule #10) — Phase 6 is not optional.
- Schema name for framework-owned tables: `framework`. Table names unchanged: `OutboxMessages`, `InboxMessages`.

## Phase map

Each phase is independently shippable and leaves the build green.

| Phase | Delivers | Fixes |
|---|---|---|
| 1 | `EventingDbContext` + single non-generic stores + migrations + data migration | Issue #1349 — the reported blocker |
| 2 | Drain-target enumeration + tenant-aware dispatcher | Latent bug: dedicated-DB tenants' outbox never dispatched |
| 3 | Lease-based row claiming (`FOR UPDATE SKIP LOCKED`) | Double-dispatch when scaled past one instance |
| 4 | Shared connection + ambient transaction enlistment | Makes "transactional outbox" actually transactional |
| 5 | Move the 8 direct `IEventBus.PublishAsync` call sites onto the outbox | The `eventing.md` promise that is currently false |
| 6 | Rules, skills, docs repo, changelog | Golden rule #10 |

## Progress

| Task | State | Commit |
|---|---|---|
| 1.1 arch guard test | done (now green) | `375a8e58`, `f06b466f` |
| 1.2 `EventingDbContext` | done | `0d95730f` |
| 1.3 non-generic `EfCoreOutboxStore` | done | `aaca3d2b`, `795e0f32`, `d64aef9b` |
| 1.4 non-generic `EfCoreInboxStore` | done | `72fa0b13` |
| 1.5 collapse into `AddEventingCore` | done | `72fa0b13` |
| 1.6 strip Identity + host wiring | done | `161b364c` |
| 1.7 migrations + data carry-over | done | `161b364c` |
| 1.8 multi-module integration test | done | `161b364c` |
| **Phase 1 complete** — #1349 fixed end to end | shipped | PR #1353 |
| 2.1 drain-target abstractions | done | |
| 2.2 dispatch every target | done | |
| 2.3 multitenancy provider + scope | done | |
| 3.1 lease columns and index | done | |
| 3.2 claim via FOR UPDATE SKIP LOCKED | done | |
| 4.1 scoped shared connection | done | |
| 4.2 enlist in the caller's transaction | done | |
| 5.1–5.8 call sites | 7 moved, 1 documented exception | |
| 6.1 rules and skills | done | |
| 6.2 docs site + changelog | done (committed in the docs repo, **not pushed**) | |
| 6.3 issue reply | done on #1349 | |

**Phases 2–6 completed 2026-08-07.** Full suite green: 1053 unit + 747 integration.

Deviations from the plan text, all deliberate:

- **Task 5 left Chat mentions on the bus.** The Notifications handler writes the row *and* pushes it over SignalR; a mention landing in the bell a dispatch cycle after the message appears reads as broken, and durability matters least there (the message itself is already persisted). The plan's Step 5 explicitly allows this; it is the only exception, and the reason is in a comment at the call site.
- **Modules inject `IOutboxWriter`, not `IOutboxStore`.** `IOutboxStore` lives in the eventing runtime, which modules don't reference (and shouldn't — it would drag EF Core into every module). Added `IOutboxWriter` to `Eventing.Abstractions` as the publish-side contract; `IOutboxStore` extends it.
- **Phase 4 needed a transaction registry, and the plan's fallback was the right call.** `DbConnection` exposes no way to ask whether a transaction is open on it, so `AmbientDbTransactionRegistry` (an `IDbTransactionInterceptor` on every Hero context) records them. Also: enlistment must *detach* a completed transaction, or the next write in the same scope fails on a disposed handle.
- **Phase 5 broke six integration tests that encoded synchronous delivery.** They now drain via a new `OutboxDrain.DrainAsync` helper, which loops until a pass makes no progress — one cycle claims at most `OutboxBatchSize`, the suite shares one database so other tests' rows sit ahead in `CreatedOnUtc` order, and `TenantSubscribed → InvoiceIssued` needs a cycle each. A single `DispatchAsync` passed in isolation and failed in the full suite.

Phase 4 landed without the feared fallout: the whole integration suite stayed green on the first run after connection sharing.

Phase 1 exit criteria met 2026-08-07: build 0 warnings, 1040 unit tests green,
737 integration tests green (1 pre-existing skip). Phase 6's `eventing.md` update
was pulled forward — the rule file documented `AddEventingForDbContext<T>` as
required module wiring, which no longer exists. Still outstanding for Phase 6:
the docs repo page and a changelog entry.

Ordering note for anyone touching host wiring: `AddEventingCore` must be called
**before** `AddModules` in both hosts. `IDbInitializer` runs in registration
order, and `DropIdentityOutbox` copies rows into `framework.*` — if the eventing
initializer ran second, the copy would silently no-op and pending events would be
dropped on upgrade.

Rebased onto `origin/main` 2026-08-07, after PR #1336 (outbox retry backoff +
dead-letter redrive) landed upstream. Consequences for the remaining tasks:

- `EfCoreOutboxStore`'s ctor also takes `IOptions<EventingOptions>`, and
  `OutboxMessage` has a `NextRetryAt` column — the task 1.7 `framework`-schema
  migration must include it, and the data carry-over `INSERT` must list it.
- `Integration.Tests/Tests/Eventing/OutboxRetryTests.cs` reaches into
  `IdentityDbContext` for the outbox tables; it has to move to `EventingDbContext`
  as part of task 1.6.

Tasks 1.4 and 1.5 shipped as one commit: dropping the inbox store's type
parameter leaves `AddEventingForDbContext<TDbContext>` with an unused one, which
`TreatWarningsAsErrors` rejects (S2326).

Two deviations from the text below, both deliberate:

- The initializer is registered with `TryAddEnumerable(ServiceDescriptor.Scoped<IDbInitializer, EventingDbInitializer>())`,
  **not** `TryAddScoped<IDbInitializer, EventingDbInitializer>()` as written in task 1.5.
  Modules already register their own `IDbInitializer`, and `TryAdd` matches on service
  type alone — the eventing initializer would have been silently dropped.
- `EventingDbInitializer` is `public`, not `internal`: CA1812 fails the build for an
  internal DI-only type.

---

# Phase 1 — Framework-owned EventingDbContext

## File Structure (Phase 1)

- Create: `src/BuildingBlocks/Eventing/EventingConstants.cs` — schema name constant.
- Create: `src/BuildingBlocks/Eventing/Persistence/EventingDbContext.cs` — owns both tables.
- Create: `src/BuildingBlocks/Eventing/Persistence/EventingDbInitializer.cs` — `IDbInitializer` for the migrator.
- Modify: `src/BuildingBlocks/Eventing/Eventing.csproj` — add `Persistence.csproj` reference.
- Modify: `src/BuildingBlocks/Eventing/Outbox/EfCoreOutboxStore.cs` — drop the generic parameter.
- Modify: `src/BuildingBlocks/Eventing/Inbox/EfCoreInboxStore.cs` — drop the generic parameter.
- Modify: `src/BuildingBlocks/Eventing/ServiceCollectionExtensions.cs` — delete `AddEventingForDbContext<T>`, register the context + stores in `AddEventingCore`.
- Modify: `src/Modules/Identity/Modules.Identity/Data/IdentityDbContext.cs` — remove the outbox/inbox DbSets + configurations.
- Modify: `src/Modules/Identity/Modules.Identity/IdentityModule.cs` — drop `AddEventingForDbContext<IdentityDbContext>()`.
- Modify: `src/Host/FS.Proxy.Api/Program.cs` — call `AddEventingCore` at the host level.
- Modify: `src/Host/FS.Proxy.DbMigrator/Program.cs` — same.
- Create: `src/Host/FS.Proxy.Migrations.PostgreSQL/Eventing/` — new migration folder.
- Create: `src/Tests/Architecture.Tests/EventingRegistrationTests.cs` — guards against the regression that caused #1349.

### Task 1.1: Architecture test that would have caught #1349

This test comes first because it is the regression guard for the entire issue. It fails today
(two registrations are possible), passes after Task 1.5.

**Files:**
- Create: `src/Tests/Architecture.Tests/EventingRegistrationTests.cs`

**Interfaces:**
- Consumes: `FSH.Framework.Eventing.ServiceCollectionExtensions.AddEventingCore(IServiceCollection, IConfiguration)`
- Produces: nothing — test-only.

- [ ] **Step 1: Write the failing test**

```csharp
using FSH.Framework.Eventing;
using FSH.Framework.Eventing.Inbox;
using FSH.Framework.Eventing.Outbox;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Architecture.Tests;

/// <summary>
/// Guards the defect behind issue #1349: IOutboxStore/IInboxStore were registered
/// once per module DbContext, non-keyed, so .NET DI silently resolved whichever
/// module registered last — for the whole application, including Identity.
/// </summary>
public class EventingRegistrationTests
{
    private static IServiceCollection BuildServices()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DatabaseOptions:Provider"] = "postgresql",
                ["DatabaseOptions:ConnectionString"] = "Host=arch;Database=arch;Username=arch;Password=arch",
                ["DatabaseOptions:MigrationsAssembly"] = "FS.Proxy.Migrations.PostgreSQL",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddEventingCore(configuration);
        return services;
    }

    [Fact]
    public void AddEventingCore_Registers_Exactly_One_OutboxStore()
    {
        BuildServices()
            .Count(d => d.ServiceType == typeof(IOutboxStore))
            .ShouldBe(1, "a second IOutboxStore registration silently hijacks every module's outbox (issue #1349)");
    }

    [Fact]
    public void AddEventingCore_Registers_Exactly_One_InboxStore()
    {
        BuildServices()
            .Count(d => d.ServiceType == typeof(IInboxStore))
            .ShouldBe(1, "a second IInboxStore registration silently redirects idempotency writes (issue #1349)");
    }

    [Fact]
    public void AddEventingCore_Is_Idempotent()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DatabaseOptions:Provider"] = "postgresql",
                ["DatabaseOptions:ConnectionString"] = "Host=arch;Database=arch;Username=arch;Password=arch",
                ["DatabaseOptions:MigrationsAssembly"] = "FS.Proxy.Migrations.PostgreSQL",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddEventingCore(configuration);
        services.AddEventingCore(configuration);

        services.Count(d => d.ServiceType == typeof(IOutboxStore)).ShouldBe(1);
        services.Count(d => d.ServiceType == typeof(IInboxStore)).ShouldBe(1);
    }

    [Fact]
    public void AddEventingForDbContext_No_Longer_Exists()
    {
        typeof(ServiceCollectionExtensions)
            .GetMethods()
            .Any(m => m.Name == "AddEventingForDbContext")
            .ShouldBeFalse("per-DbContext outbox registration is the #1349 footgun; the framework owns one EventingDbContext");
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test src/Tests/Architecture.Tests --filter FullyQualifiedName~EventingRegistrationTests`
Expected: FAIL — `AddEventingCore_Registers_Exactly_One_OutboxStore` returns 0 (stores are registered by `AddEventingForDbContext`, not `AddEventingCore`), and `AddEventingForDbContext_No_Longer_Exists` fails because the method is still there.

- [ ] **Step 3: Commit the failing test**

```bash
git add src/Tests/Architecture.Tests/EventingRegistrationTests.cs
git commit -m "test(eventing): add failing guard for single outbox/inbox registration (#1349)"
```

---

### Task 1.2: EventingDbContext owning both tables

**Files:**
- Create: `src/BuildingBlocks/Eventing/EventingConstants.cs`
- Create: `src/BuildingBlocks/Eventing/Persistence/EventingDbContext.cs`
- Modify: `src/BuildingBlocks/Eventing/Eventing.csproj`
- Test: `src/Tests/Framework.Tests/Eventing/EventingDbContextModelTests.cs`

**Interfaces:**
- Consumes: `FSH.Framework.Persistence.Context.BaseDbContext(IMultiTenantContextAccessor<AppTenantInfo>, DbContextOptions, IOptions<DatabaseOptions>, IHostEnvironment)`; `OutboxMessageConfiguration(string schema)`; `InboxMessageConfiguration(string schema)`.
- Produces: `FSH.Framework.Eventing.EventingConstants.SchemaName` (const string `"framework"`); `FSH.Framework.Eventing.Persistence.EventingDbContext` with `DbSet<OutboxMessage> OutboxMessages` and `DbSet<InboxMessage> InboxMessages`, ctor `(IMultiTenantContextAccessor<AppTenantInfo>, DbContextOptions<EventingDbContext>, IOptions<DatabaseOptions>, IHostEnvironment)`.

- [ ] **Step 1: Add the project reference**

Add to `src/BuildingBlocks/Eventing/Eventing.csproj` inside the existing `ProjectReference` `ItemGroup`:

```xml
    <ProjectReference Include="..\Persistence\Persistence.csproj" />
```

No cycle: `Persistence` references only `Core` and `Shared`.

- [ ] **Step 2: Write the failing model test**

```csharp
using FSH.Framework.Eventing;
using FSH.Framework.Eventing.Inbox;
using FSH.Framework.Eventing.Outbox;
using FSH.Framework.Eventing.Persistence;
using FSH.Framework.Shared.Multitenancy;
using FSH.Framework.Shared.Persistence;
using Finbuckle.MultiTenant;
using Finbuckle.MultiTenant.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Framework.Tests.Eventing;

public class EventingDbContextModelTests
{
    private static EventingDbContext CreateContext()
    {
        var accessor = Substitute.For<IMultiTenantContextAccessor<AppTenantInfo>>();
        accessor.MultiTenantContext.Returns(new MultiTenantContext<AppTenantInfo>());

        var options = new DbContextOptionsBuilder<EventingDbContext>()
            .UseNpgsql("Host=arch;Database=arch;Username=arch;Password=arch")
            .Options;

        var settings = Options.Create(new DatabaseOptions
        {
            Provider = "postgresql",
            ConnectionString = string.Empty,
            MigrationsAssembly = "FS.Proxy.Migrations.PostgreSQL",
        });

        var environment = Substitute.For<IHostEnvironment>();
        environment.EnvironmentName.Returns("Production");

        return new EventingDbContext(accessor, options, settings, environment);
    }

    [Fact]
    public void Maps_OutboxMessages_To_Framework_Schema()
    {
        using var context = CreateContext();
        var entity = context.Model.FindEntityType(typeof(OutboxMessage));

        entity.ShouldNotBeNull();
        entity!.GetSchema().ShouldBe(EventingConstants.SchemaName);
        entity.GetTableName().ShouldBe("OutboxMessages");
    }

    [Fact]
    public void Maps_InboxMessages_To_Framework_Schema()
    {
        using var context = CreateContext();
        var entity = context.Model.FindEntityType(typeof(InboxMessage));

        entity.ShouldNotBeNull();
        entity!.GetSchema().ShouldBe(EventingConstants.SchemaName);
        entity.GetTableName().ShouldBe("InboxMessages");
    }

    [Fact]
    public void Outbox_And_Inbox_Are_Not_Tenant_Filtered()
    {
        using var context = CreateContext();

        // Both are IGlobalEntity: the dispatcher scans across tenants within a database.
        context.Model.FindEntityType(typeof(OutboxMessage))!.GetQueryFilter().ShouldBeNull();
        context.Model.FindEntityType(typeof(InboxMessage))!.GetQueryFilter().ShouldBeNull();
    }
}
```

- [ ] **Step 3: Run to verify it fails**

Run: `dotnet test src/Tests/Framework.Tests --filter FullyQualifiedName~EventingDbContextModelTests`
Expected: FAIL to compile — `EventingDbContext` and `EventingConstants` do not exist.

- [ ] **Step 4: Write the constants file**

`src/BuildingBlocks/Eventing/EventingConstants.cs`:

```csharp
namespace FSH.Framework.Eventing;

/// <summary>
/// Constants for the framework-owned eventing store.
/// </summary>
public static class EventingConstants
{
    /// <summary>
    /// Database schema owning <c>OutboxMessages</c> and <c>InboxMessages</c>.
    /// These tables are framework infrastructure, not module data, so they live
    /// outside every module schema.
    /// </summary>
    public const string SchemaName = "framework";
}
```

- [ ] **Step 5: Write the context**

`src/BuildingBlocks/Eventing/Persistence/EventingDbContext.cs`:

```csharp
using Finbuckle.MultiTenant.Abstractions;
using FSH.Framework.Eventing.Inbox;
using FSH.Framework.Eventing.Outbox;
using FSH.Framework.Persistence.Context;
using FSH.Framework.Shared.Multitenancy;
using FSH.Framework.Shared.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace FSH.Framework.Eventing.Persistence;

/// <summary>
/// The single context owning the transactional outbox and inbox.
///
/// Derives from <see cref="BaseDbContext"/> so it inherits per-tenant connection
/// routing: for a tenant with a dedicated database, the outbox row lands in that
/// same database as the business data it accompanies. That is what makes the
/// outbox transactional on every supported deployment topology.
///
/// Owning these tables here — rather than once per module DbContext — is what
/// keeps <c>IOutboxStore</c>/<c>IInboxStore</c> to a single, unambiguous DI
/// registration (issue #1349).
/// </summary>
public class EventingDbContext : BaseDbContext
{
    public EventingDbContext(
        IMultiTenantContextAccessor<AppTenantInfo> multiTenantContextAccessor,
        DbContextOptions<EventingDbContext> options,
        IOptions<DatabaseOptions> settings,
        IHostEnvironment environment)
        : base(multiTenantContextAccessor, options, settings, environment)
    {
    }

    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    public DbSet<InboxMessage> InboxMessages => Set<InboxMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.ApplyConfiguration(new OutboxMessageConfiguration(EventingConstants.SchemaName));
        modelBuilder.ApplyConfiguration(new InboxMessageConfiguration(EventingConstants.SchemaName));

        // Must run last: ApplyTenantIsolationByDefault inspects the configured entities.
        base.OnModelCreating(modelBuilder);
    }
}
```

- [ ] **Step 6: Run tests to verify they pass**

Run: `dotnet test src/Tests/Framework.Tests --filter FullyQualifiedName~EventingDbContextModelTests`
Expected: PASS (3 tests)

- [ ] **Step 7: Commit**

```bash
git add src/BuildingBlocks/Eventing/EventingConstants.cs \
        src/BuildingBlocks/Eventing/Persistence/EventingDbContext.cs \
        src/BuildingBlocks/Eventing/Eventing.csproj \
        src/Tests/Framework.Tests/Eventing/EventingDbContextModelTests.cs
git commit -m "feat(eventing): add framework-owned EventingDbContext (#1349)"
```

---

### Task 1.3: Make the outbox store non-generic

**Files:**
- Modify: `src/BuildingBlocks/Eventing/Outbox/EfCoreOutboxStore.cs`
- Test: `src/Tests/Framework.Tests/Eventing/EfCoreOutboxStoreTests.cs` (create)

**Interfaces:**
- Consumes: `EventingDbContext`, `IEventSerializer`, `TimeProvider`.
- Produces: `EfCoreOutboxStore : IOutboxStore` — ctor `(EventingDbContext, IEventSerializer, ILogger<EfCoreOutboxStore>, TimeProvider)`. No generic parameter. Method signatures on `IOutboxStore` are unchanged in this phase.

- [ ] **Step 1: Write the failing test**

```csharp
using FSH.Framework.Eventing.Abstractions;
using FSH.Framework.Eventing.Outbox;
using FSH.Framework.Eventing.Serialization;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace Framework.Tests.Eventing;

public class EfCoreOutboxStoreTests
{
    private sealed record SampleEvent(
        Guid Id,
        DateTime OccurredOnUtc,
        string? TenantId,
        string CorrelationId,
        string Source) : IIntegrationEvent;

    [Fact]
    public async Task AddAsync_Persists_Message_With_Event_Identity()
    {
        await using var context = EventingTestContext.CreateSqlite();
        var store = new EfCoreOutboxStore(
            context,
            new JsonEventSerializer(),
            NullLogger<EfCoreOutboxStore>.Instance,
            TimeProvider.System);

        var evt = new SampleEvent(
            Guid.CreateVersion7(),
            new DateTime(2026, 8, 6, 12, 0, 0, DateTimeKind.Utc),
            "acme",
            "corr-1",
            "Tests");

        await store.AddAsync(evt, TestContext.Current.CancellationToken);

        var saved = context.OutboxMessages.Single();
        saved.Id.ShouldBe(evt.Id);
        saved.TenantId.ShouldBe("acme");
        saved.CorrelationId.ShouldBe("corr-1");
        saved.ProcessedOnUtc.ShouldBeNull();
        saved.IsDead.ShouldBeFalse();
        saved.Type.ShouldContain(nameof(SampleEvent));
    }
}
```

Add the shared SQLite harness `src/Tests/Framework.Tests/Eventing/EventingTestContext.cs`:

```csharp
using FSH.Framework.Eventing.Persistence;
using FSH.Framework.Shared.Multitenancy;
using FSH.Framework.Shared.Persistence;
using Finbuckle.MultiTenant;
using Finbuckle.MultiTenant.Abstractions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Framework.Tests.Eventing;

/// <summary>
/// In-memory SQLite EventingDbContext for store-level unit tests. SQLite has no
/// schemas, so EF folds "framework.OutboxMessages" into a single table name —
/// fine here; schema mapping is asserted separately against the Npgsql model.
/// </summary>
internal static class EventingTestContext
{
    public static EventingDbContext CreateSqlite()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        var accessor = Substitute.For<IMultiTenantContextAccessor<AppTenantInfo>>();
        accessor.MultiTenantContext.Returns(new MultiTenantContext<AppTenantInfo>());

        var options = new DbContextOptionsBuilder<EventingDbContext>()
            .UseSqlite(connection)
            .Options;

        var settings = Options.Create(new DatabaseOptions
        {
            Provider = "postgresql",
            ConnectionString = string.Empty,
            MigrationsAssembly = "FS.Proxy.Migrations.PostgreSQL",
        });

        var environment = Substitute.For<IHostEnvironment>();
        environment.EnvironmentName.Returns("Production");

        var context = new EventingDbContext(accessor, options, settings, environment);
        context.Database.EnsureCreated();
        return context;
    }
}
```

Add `Microsoft.EntityFrameworkCore.Sqlite` to `src/Tests/Framework.Tests/Framework.Tests.csproj` if absent (version from `Directory.Packages.props`; add a `PackageVersion` entry there if the package is new to the repo).

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test src/Tests/Framework.Tests --filter FullyQualifiedName~EfCoreOutboxStoreTests`
Expected: FAIL to compile — `EfCoreOutboxStore` still requires a generic type argument.

- [ ] **Step 3: Rewrite the store**

Replace the class declaration and constructor in `src/BuildingBlocks/Eventing/Outbox/EfCoreOutboxStore.cs`. The method bodies are unchanged except that `_dbContext` is now `EventingDbContext`:

```csharp
using FSH.Framework.Eventing.Abstractions;
using FSH.Framework.Eventing.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FSH.Framework.Eventing.Outbox;

/// <summary>
/// EF Core outbox store over the framework-owned <see cref="EventingDbContext"/>.
///
/// Non-generic on purpose: a per-DbContext generic store meant one
/// <c>IOutboxStore</c> registration per module, and .NET DI resolved the last
/// one registered for the entire application (issue #1349).
/// </summary>
public sealed class EfCoreOutboxStore : IOutboxStore
{
    private readonly EventingDbContext _dbContext;
    private readonly IEventSerializer _serializer;
    private readonly ILogger<EfCoreOutboxStore> _logger;
    private readonly TimeProvider _timeProvider;

    public EfCoreOutboxStore(
        EventingDbContext dbContext,
        IEventSerializer serializer,
        ILogger<EfCoreOutboxStore> logger,
        TimeProvider timeProvider)
    {
        _dbContext = dbContext;
        _serializer = serializer;
        _logger = logger;
        _timeProvider = timeProvider;
    }

    // AddAsync / GetPendingBatchAsync / MarkAsProcessedAsync / MarkAsFailedAsync
    // bodies are carried over verbatim from the generic version.
}
```

Carry over the four method bodies from the current file unchanged.

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test src/Tests/Framework.Tests --filter FullyQualifiedName~EfCoreOutboxStoreTests`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add src/BuildingBlocks/Eventing/Outbox/EfCoreOutboxStore.cs \
        src/Tests/Framework.Tests/Eventing/EfCoreOutboxStoreTests.cs \
        src/Tests/Framework.Tests/Eventing/EventingTestContext.cs \
        src/Tests/Framework.Tests/Framework.Tests.csproj
git commit -m "refactor(eventing): make EfCoreOutboxStore non-generic over EventingDbContext (#1349)"
```

---

### Task 1.4: Make the inbox store non-generic

**Files:**
- Modify: `src/BuildingBlocks/Eventing/Inbox/EfCoreInboxStore.cs`
- Test: `src/Tests/Framework.Tests/Eventing/EfCoreInboxStoreTests.cs` (create)

**Interfaces:**
- Consumes: `EventingDbContext`, `TimeProvider`, `EventingTestContext.CreateSqlite()` from Task 1.3.
- Produces: `EfCoreInboxStore : IInboxStore` — ctor `(EventingDbContext, TimeProvider)`. No generic parameter.

- [ ] **Step 1: Write the failing test**

```csharp
using FSH.Framework.Eventing.Inbox;
using Shouldly;
using Xunit;

namespace Framework.Tests.Eventing;

public class EfCoreInboxStoreTests
{
    [Fact]
    public async Task MarkProcessedAsync_Then_HasProcessedAsync_Returns_True()
    {
        await using var context = EventingTestContext.CreateSqlite();
        var store = new EfCoreInboxStore(context, TimeProvider.System);
        var eventId = Guid.CreateVersion7();

        (await store.HasProcessedAsync(eventId, "HandlerA", TestContext.Current.CancellationToken))
            .ShouldBeFalse();

        await store.MarkProcessedAsync(eventId, "HandlerA", "acme", "SomeEvent", TestContext.Current.CancellationToken);

        (await store.HasProcessedAsync(eventId, "HandlerA", TestContext.Current.CancellationToken))
            .ShouldBeTrue();
    }

    [Fact]
    public async Task Dedup_Key_Is_Per_Handler()
    {
        await using var context = EventingTestContext.CreateSqlite();
        var store = new EfCoreInboxStore(context, TimeProvider.System);
        var eventId = Guid.CreateVersion7();

        await store.MarkProcessedAsync(eventId, "HandlerA", "acme", "SomeEvent", TestContext.Current.CancellationToken);

        (await store.HasProcessedAsync(eventId, "HandlerB", TestContext.Current.CancellationToken))
            .ShouldBeFalse("the inbox key is {eventId, handlerName}; a second handler must still run");
    }

    [Fact]
    public async Task MarkProcessedAsync_Is_Idempotent()
    {
        await using var context = EventingTestContext.CreateSqlite();
        var store = new EfCoreInboxStore(context, TimeProvider.System);
        var eventId = Guid.CreateVersion7();

        await store.MarkProcessedAsync(eventId, "HandlerA", "acme", "SomeEvent", TestContext.Current.CancellationToken);
        await store.MarkProcessedAsync(eventId, "HandlerA", "acme", "SomeEvent", TestContext.Current.CancellationToken);

        context.InboxMessages.Count().ShouldBe(1);
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test src/Tests/Framework.Tests --filter FullyQualifiedName~EfCoreInboxStoreTests`
Expected: FAIL to compile — `EfCoreInboxStore` still requires a generic type argument.

- [ ] **Step 3: Rewrite the store**

In `src/BuildingBlocks/Eventing/Inbox/EfCoreInboxStore.cs`, change the declaration to:

```csharp
using FSH.Framework.Eventing.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FSH.Framework.Eventing.Inbox;

/// <summary>
/// EF Core inbox store over the framework-owned <see cref="EventingDbContext"/>.
/// Non-generic for the same reason as <c>EfCoreOutboxStore</c> — see issue #1349.
/// </summary>
public sealed class EfCoreInboxStore : IInboxStore
{
    private readonly EventingDbContext _dbContext;
    private readonly TimeProvider _timeProvider;

    public EfCoreInboxStore(EventingDbContext dbContext, TimeProvider timeProvider)
    {
        _dbContext = dbContext;
        _timeProvider = timeProvider;
    }

    // HasProcessedAsync / MarkProcessedAsync bodies carried over verbatim.
}
```

Carry over both method bodies from the current file unchanged.

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test src/Tests/Framework.Tests --filter FullyQualifiedName~EfCoreInboxStoreTests`
Expected: PASS (3 tests)

- [ ] **Step 5: Commit**

```bash
git add src/BuildingBlocks/Eventing/Inbox/EfCoreInboxStore.cs \
        src/Tests/Framework.Tests/Eventing/EfCoreInboxStoreTests.cs
git commit -m "refactor(eventing): make EfCoreInboxStore non-generic over EventingDbContext (#1349)"
```

---

### Task 1.5: Collapse registration into AddEventingCore

This is the task that turns Task 1.1 green.

**Files:**
- Modify: `src/BuildingBlocks/Eventing/ServiceCollectionExtensions.cs`
- Create: `src/BuildingBlocks/Eventing/Persistence/EventingDbInitializer.cs`

**Interfaces:**
- Consumes: `PersistenceExtensions.AddHeroDbContext<TContext>()`, `IDbInitializer`.
- Produces: `AddEventingCore` now additionally registers `EventingDbContext`, `IOutboxStore`→`EfCoreOutboxStore`, `IInboxStore`→`EfCoreInboxStore`, `OutboxDispatcher`, and `IDbInitializer`→`EventingDbInitializer`. `AddEventingForDbContext<T>` is deleted.

- [ ] **Step 1: Write the initializer**

`src/BuildingBlocks/Eventing/Persistence/EventingDbInitializer.cs`:

```csharp
using FSH.Framework.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FSH.Framework.Eventing.Persistence;

/// <summary>
/// Migrates the framework eventing schema. Runs per tenant like every other
/// <see cref="IDbInitializer"/>, so a tenant with a dedicated database gets its
/// own outbox/inbox tables.
/// </summary>
public sealed partial class EventingDbInitializer : IDbInitializer
{
    private readonly EventingDbContext _context;
    private readonly ILogger<EventingDbInitializer> _logger;

    public EventingDbInitializer(EventingDbContext context, ILogger<EventingDbInitializer> logger)
    {
        _context = context;
        _logger = logger;
    }

    public async Task MigrateAsync(CancellationToken cancellationToken)
    {
        if ((await _context.Database.GetPendingMigrationsAsync(cancellationToken).ConfigureAwait(false)).Any())
        {
            await _context.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
            LogMigrated();
        }
    }

    public Task SeedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(Level = LogLevel.Information, Message = "Applied eventing schema migrations.")]
    private partial void LogMigrated();
}
```

- [ ] **Step 2: Rewrite the registration**

In `src/BuildingBlocks/Eventing/ServiceCollectionExtensions.cs`, add these registrations to `AddEventingCore` immediately after the `IEventTenantScope` line, and **delete the whole `AddEventingForDbContext<TDbContext>` method**:

```csharp
        // One context owns the outbox/inbox tables, so each store has exactly one
        // registration. Registering per module DbContext (the old
        // AddEventingForDbContext<T>) made DI resolve whichever module registered
        // last, for the whole app — issue #1349.
        services.AddHeroDbContext<EventingDbContext>();
        services.TryAddScoped<IDbInitializer, EventingDbInitializer>();
        services.TryAddScoped<IOutboxStore, EfCoreOutboxStore>();
        services.TryAddScoped<IInboxStore, EfCoreInboxStore>();
        services.TryAddScoped<OutboxDispatcher>();
```

`AddHeroDbContext` is itself idempotent enough for the `AddEventingCore_Is_Idempotent` test because `AddDbContext` uses `TryAdd` semantics for the context registration; the `TryAddScoped` calls guarantee the store counts.

Add the required usings: `FSH.Framework.Eventing.Persistence;`, `FSH.Framework.Persistence;`.

- [ ] **Step 3: Run the architecture test from Task 1.1**

Run: `dotnet test src/Tests/Architecture.Tests --filter FullyQualifiedName~EventingRegistrationTests`
Expected: PASS (4 tests)

- [ ] **Step 4: Commit**

```bash
git add src/BuildingBlocks/Eventing/ServiceCollectionExtensions.cs \
        src/BuildingBlocks/Eventing/Persistence/EventingDbInitializer.cs
git commit -m "feat(eventing): register one outbox/inbox store in AddEventingCore, drop AddEventingForDbContext (#1349)"
```

---

### Task 1.6: Remove outbox/inbox from IdentityDbContext and rewire the hosts

**Files:**
- Modify: `src/Modules/Identity/Modules.Identity/Data/IdentityDbContext.cs:29-31,66-67`
- Modify: `src/Modules/Identity/Modules.Identity/IdentityModule.cs:116-117`
- Modify: `src/Host/FS.Proxy.Api/Program.cs`
- Modify: `src/Host/FS.Proxy.DbMigrator/Program.cs`

**Interfaces:**
- Consumes: `AddEventingCore(IConfiguration)` from Task 1.5.
- Produces: no new API. `IdentityDbContext` no longer exposes `OutboxMessages`/`InboxMessages`.

- [ ] **Step 1: Strip the tables from IdentityDbContext**

Delete lines 29 and 31 (the two `DbSet` properties) and lines 66-67 (the two `ApplyConfiguration` calls). Remove the now-unused `using FSH.Framework.Eventing.Inbox;` and `using FSH.Framework.Eventing.Outbox;`. Update the comment on line 69-70 to drop the "(Outbox/Inbox/ImpersonationGrant opt out)" parenthetical, leaving `ImpersonationGrant`.

- [ ] **Step 2: Move eventing bootstrap out of IdentityModule**

In `src/Modules/Identity/Modules.Identity/IdentityModule.cs`, delete line 117 (`services.AddEventingForDbContext<IdentityDbContext>();`) and line 116 (`services.AddEventingCore(builder.Configuration);`). Keep line 118 (`AddIntegrationEventHandlers`) — handler registration stays per module.

- [ ] **Step 3: Bootstrap eventing at the host level**

The outbox is framework infrastructure, not Identity's. In `src/Host/FS.Proxy.Api/Program.cs`, add `builder.Services.AddEventingCore(builder.Configuration);` alongside the other framework registrations (before `RegisterModules`). Add the same line to `src/Host/FS.Proxy.DbMigrator/Program.cs` before its module registration, so `EventingDbInitializer` is discovered by the per-tenant migrate loop.

- [ ] **Step 4: Build**

Run: `dotnet build src/FS.Proxy.slnx`
Expected: SUCCESS. If any module fails to resolve `IOutboxStore`, it is a missing `AddEventingCore` at the host — not a per-module registration to re-add.

- [ ] **Step 5: Run the full unit suite**

Run: `dotnet test src/FS.Proxy.slnx --filter "FullyQualifiedName!~Integration"`
Expected: PASS. Identity tests substitute `IOutboxStore`, so they are unaffected by the store's shape.

- [ ] **Step 6: Commit**

```bash
git add src/Modules/Identity/Modules.Identity/Data/IdentityDbContext.cs \
        src/Modules/Identity/Modules.Identity/IdentityModule.cs \
        src/Host/FS.Proxy.Api/Program.cs \
        src/Host/FS.Proxy.DbMigrator/Program.cs
git commit -m "refactor(identity): hand outbox/inbox ownership to the framework eventing context (#1349)"
```

---

### Task 1.7: Migrations — create framework schema, move data, drop identity tables

**Files:**
- Create: `src/Host/FS.Proxy.Migrations.PostgreSQL/Eventing/*` (generated)
- Create: `src/Host/FS.Proxy.Migrations.PostgreSQL/Identity/*_DropIdentityOutbox.cs` (generated, then hand-edited)
- Modify: `src/Host/FS.Proxy.Migrations.PostgreSQL/FS.Proxy.Migrations.PostgreSQL.csproj` — add `<Folder Include="Eventing\" />`

**Interfaces:**
- Consumes: `EventingDbContext` from Task 1.2.
- Produces: schema `framework` with `OutboxMessages` + `InboxMessages`; `identity.OutboxMessages`/`identity.InboxMessages` dropped after their rows are copied.

> **Footgun (memory: `project_ef_migrations_remove_footgun`):** run a full `dotnet build` before `migrations add`, and never `migrations remove` mid-sequence — it operates on the snapshot and can silently drop the previous migration.

- [ ] **Step 1: Full build first**

Run: `dotnet build src/FS.Proxy.slnx`
Expected: SUCCESS

- [ ] **Step 2: Create the eventing migration**

```bash
dotnet ef migrations add EventingSchema \
  --project src/Host/FS.Proxy.Migrations.PostgreSQL \
  --startup-project src/Host/FS.Proxy.DbMigrator \
  --context EventingDbContext \
  --output-dir Eventing
```

Expected: creates `framework.OutboxMessages` and `framework.InboxMessages` with `EnsureSchema("framework")`.

- [ ] **Step 3: Create the Identity drop migration**

```bash
dotnet ef migrations add DropIdentityOutbox \
  --project src/Host/FS.Proxy.Migrations.PostgreSQL \
  --startup-project src/Host/FS.Proxy.DbMigrator \
  --context IdentityDbContext \
  --output-dir Identity
```

Expected: a migration containing `DropTable("OutboxMessages", "identity")` and `DropTable("InboxMessages", "identity")`.

- [ ] **Step 4: Hand-edit the drop migration to copy rows first**

Prepend to `Up(MigrationBuilder migrationBuilder)`, **above** the generated `DropTable` calls. Ordering across contexts is not guaranteed by EF, so guard on the destination existing:

```csharp
        // Carry the existing outbox/inbox across to the framework schema before dropping
        // the identity-owned tables. Unprocessed outbox rows would otherwise be lost
        // (they are pending integration events); inbox rows must survive or already-handled
        // events would be reprocessed. No-ops when the framework schema has not been
        // created yet — the eventing migration then starts from empty tables, which is
        // correct for a fresh database.
        migrationBuilder.Sql("""
            DO $$
            BEGIN
                IF to_regclass('framework."OutboxMessages"') IS NOT NULL
                   AND to_regclass('identity."OutboxMessages"') IS NOT NULL THEN
                    INSERT INTO framework."OutboxMessages"
                        ("Id", "CreatedOnUtc", "Type", "Payload", "TenantId",
                         "CorrelationId", "ProcessedOnUtc", "RetryCount", "LastError", "IsDead")
                    SELECT "Id", "CreatedOnUtc", "Type", "Payload", "TenantId",
                           "CorrelationId", "ProcessedOnUtc", "RetryCount", "LastError", "IsDead"
                    FROM identity."OutboxMessages"
                    ON CONFLICT ("Id") DO NOTHING;
                END IF;

                IF to_regclass('framework."InboxMessages"') IS NOT NULL
                   AND to_regclass('identity."InboxMessages"') IS NOT NULL THEN
                    INSERT INTO framework."InboxMessages"
                        ("Id", "EventType", "HandlerName", "ProcessedOnUtc", "TenantId")
                    SELECT "Id", "EventType", "HandlerName", "ProcessedOnUtc", "TenantId"
                    FROM identity."InboxMessages"
                    ON CONFLICT ("Id", "HandlerName") DO NOTHING;
                END IF;
            END $$;
            """);
```

Leave `Down` as EF generated it (recreating the identity tables empty) and add a comment saying the rollback does not copy rows back.

- [ ] **Step 5: Add the folder to the csproj**

Add `<Folder Include="Eventing\" />` to the existing `Folder` `ItemGroup`.

- [ ] **Step 6: Apply against a real database**

```bash
dotnet run --project src/Host/FS.Proxy.DbMigrator -- apply
```

Expected: exit 0. Verify in psql that `framework."OutboxMessages"` exists and `identity."OutboxMessages"` is gone.

- [ ] **Step 7: Commit**

```bash
git add src/Host/FS.Proxy.Migrations.PostgreSQL/
git commit -m "feat(eventing): migrate outbox/inbox to framework schema with data carry-over (#1349)"
```

---

### Task 1.8: Integration test — a second module can publish

Proves the reported defect is gone end to end.

**Files:**
- Create: `src/Tests/Integration.Tests/Tests/Eventing/MultiModuleOutboxTests.cs`

**Interfaces:**
- Consumes: the existing Testcontainers harness in `src/Tests/Integration.Tests` (follow `.agents/rules/integration-testing.md`; do **not** substitute `IOutboxStore` in this test — the point is to exercise the real store).
- Produces: nothing.

- [ ] **Step 1: Write the test**

Read `.agents/rules/integration-testing.md` and mirror an existing test's fixture usage. The test must:

1. Resolve the real `IOutboxStore` from the app's root provider in a tenant scope.
2. Publish one integration event whose source module is **not** Identity (use a Billing contracts event, e.g. `InvoiceIssuedIntegrationEvent`).
3. Assert the row lands in `framework."OutboxMessages"` via a resolved `EventingDbContext`.
4. Publish a second event from Identity in the same test and assert both rows coexist in the same table.

```csharp
[Fact]
public async Task Events_From_Multiple_Modules_Land_In_One_Framework_Outbox()
{
    using var scope = Fixture.Services.CreateScope();
    SetTenantContext(scope, MultitenancyConstants.Root.Id);   // inline — AsyncLocal is lost across awaited helpers

    var store = scope.ServiceProvider.GetRequiredService<IOutboxStore>();
    var context = scope.ServiceProvider.GetRequiredService<EventingDbContext>();

    var billingEventId = Guid.CreateVersion7();
    var identityEventId = Guid.CreateVersion7();

    await store.AddAsync(BuildBillingEvent(billingEventId), TestContext.Current.CancellationToken);
    await store.AddAsync(BuildIdentityEvent(identityEventId), TestContext.Current.CancellationToken);

    var ids = await context.OutboxMessages
        .Where(m => m.Id == billingEventId || m.Id == identityEventId)
        .Select(m => m.Id)
        .ToListAsync(TestContext.Current.CancellationToken);

    ids.ShouldBe([billingEventId, identityEventId], ignoreOrder: true);
}
```

> **Gotcha (memory: `project_tenant_context_asynclocal_in_tests`):** set the Finbuckle tenant context *inline* in the test method, never in an awaited helper — `AsyncLocal` does not flow back out.

- [ ] **Step 2: Run it (Docker required)**

Run: `dotnet test src/Tests/Integration.Tests --filter FullyQualifiedName~MultiModuleOutboxTests`
Expected: PASS

- [ ] **Step 3: Commit**

```bash
git add src/Tests/Integration.Tests/Tests/Eventing/MultiModuleOutboxTests.cs
git commit -m "test(eventing): prove multiple modules share one framework outbox (#1349)"
```

**Phase 1 exit criteria:** `dotnet build src/FS.Proxy.slnx` clean; full test suite green; a second module can call `IOutboxStore.AddAsync` with no DI ambiguity and no missing-table error. Issue #1349's blocker is resolved at this point.

---

# Phase 2 — Tenant-aware dispatch

Fixes the latent bug: today a tenant with `TenantInfo.ConnectionString` set writes outbox rows into *its own* database, while `OutboxDispatcherHostedService` polls the default connection and never sees them.

## File Structure (Phase 2)

- Create: `src/BuildingBlocks/Eventing.Abstractions/EventingDrainTarget.cs`
- Create: `src/BuildingBlocks/Eventing.Abstractions/IEventingDrainTargetProvider.cs`
- Create: `src/BuildingBlocks/Eventing.Abstractions/IEventingDrainScope.cs`
- Create: `src/BuildingBlocks/Eventing/SingleDatabaseDrainTargetProvider.cs` — default impl.
- Create: `src/BuildingBlocks/Eventing/NullEventingDrainScope.cs` — default impl.
- Modify: `src/BuildingBlocks/Eventing/ServiceCollectionExtensions.cs` — `TryAddSingleton` both defaults.
- Modify: `src/BuildingBlocks/Eventing/Outbox/OutboxDispatcherHostedService.cs` — loop targets.
- Create: `src/Modules/Multitenancy/Modules.Multitenancy/Services/TenantStoreDrainTargetProvider.cs`
- Create: `src/Modules/Multitenancy/Modules.Multitenancy/Services/FinbuckleEventingDrainScope.cs`
- Modify: `src/Modules/Multitenancy/Modules.Multitenancy/MultitenancyModule.cs` — replace both defaults.

### Task 2.1: Drain-target abstractions

**Files:**
- Create: `src/BuildingBlocks/Eventing.Abstractions/EventingDrainTarget.cs`
- Create: `src/BuildingBlocks/Eventing.Abstractions/IEventingDrainTargetProvider.cs`
- Create: `src/BuildingBlocks/Eventing.Abstractions/IEventingDrainScope.cs`
- Create: `src/BuildingBlocks/Eventing/SingleDatabaseDrainTargetProvider.cs`
- Create: `src/BuildingBlocks/Eventing/NullEventingDrainScope.cs`
- Test: `src/Tests/Framework.Tests/Eventing/SingleDatabaseDrainTargetProviderTests.cs`

**Interfaces:**
- Produces:
  - `public sealed record EventingDrainTarget(string? TenantId, string? ConnectionString)` — `TenantId`/`ConnectionString` both null means "the default connection".
  - `public interface IEventingDrainTargetProvider { Task<IReadOnlyList<EventingDrainTarget>> GetTargetsAsync(CancellationToken ct = default); }`
  - `public interface IEventingDrainScope { IDisposable Begin(EventingDrainTarget target); }`
  - `SingleDatabaseDrainTargetProvider` returning `[new EventingDrainTarget(null, null)]`.
  - `NullEventingDrainScope` returning a no-op `IDisposable`.

- [ ] **Step 1: Write the failing test**

```csharp
using FSH.Framework.Eventing;
using FSH.Framework.Eventing.Abstractions;
using Shouldly;
using Xunit;

namespace Framework.Tests.Eventing;

public class SingleDatabaseDrainTargetProviderTests
{
    [Fact]
    public async Task Returns_One_Default_Target()
    {
        var provider = new SingleDatabaseDrainTargetProvider();

        var targets = await provider.GetTargetsAsync(TestContext.Current.CancellationToken);

        targets.Count.ShouldBe(1);
        targets[0].TenantId.ShouldBeNull();
        targets[0].ConnectionString.ShouldBeNull();
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test src/Tests/Framework.Tests --filter FullyQualifiedName~SingleDatabaseDrainTargetProviderTests`
Expected: FAIL to compile.

- [ ] **Step 3: Write the abstractions**

`EventingDrainTarget.cs`:

```csharp
namespace FSH.Framework.Eventing.Abstractions;

/// <summary>
/// One database the outbox dispatcher must drain.
///
/// A null <paramref name="ConnectionString"/> means the default configured
/// database, which holds the outbox for every tenant that does not have a
/// dedicated one. Tenants sharing the default database need no separate target:
/// outbox rows are <c>IGlobalEntity</c>, so a single pass sees all of them.
/// </summary>
/// <param name="TenantId">Tenant whose context should be installed while draining, or null for the default pass.</param>
/// <param name="ConnectionString">Dedicated connection string, or null for the default.</param>
public sealed record EventingDrainTarget(string? TenantId, string? ConnectionString);
```

`IEventingDrainTargetProvider.cs`:

```csharp
namespace FSH.Framework.Eventing.Abstractions;

/// <summary>
/// Enumerates the distinct databases holding outbox rows. Implemented in
/// BuildingBlocks as a single default target; the multitenancy module replaces it
/// with one that also returns each distinct per-tenant connection string.
/// </summary>
public interface IEventingDrainTargetProvider
{
    Task<IReadOnlyList<EventingDrainTarget>> GetTargetsAsync(CancellationToken ct = default);
}
```

`IEventingDrainScope.cs`:

```csharp
namespace FSH.Framework.Eventing.Abstractions;

/// <summary>
/// Installs the ambient tenant context — including the dedicated connection
/// string — for the duration of one drain pass, so the EventingDbContext
/// resolved inside that scope targets the right database.
///
/// Distinct from <see cref="IEventTenantScope"/>, which sets tenant identity only
/// and is deliberately connection-string-agnostic.
/// </summary>
public interface IEventingDrainScope
{
    IDisposable Begin(EventingDrainTarget target);
}
```

`SingleDatabaseDrainTargetProvider.cs`:

```csharp
using FSH.Framework.Eventing.Abstractions;

namespace FSH.Framework.Eventing;

/// <summary>
/// Default provider for single-database deployments: one pass over the
/// configured connection.
/// </summary>
public sealed class SingleDatabaseDrainTargetProvider : IEventingDrainTargetProvider
{
    private static readonly IReadOnlyList<EventingDrainTarget> DefaultOnly =
        [new EventingDrainTarget(null, null)];

    public Task<IReadOnlyList<EventingDrainTarget>> GetTargetsAsync(CancellationToken ct = default)
        => Task.FromResult(DefaultOnly);
}
```

`NullEventingDrainScope.cs`:

```csharp
using FSH.Framework.Eventing.Abstractions;

namespace FSH.Framework.Eventing;

/// <summary>
/// No-op drain scope used when no multitenancy provider is wired.
/// </summary>
public sealed class NullEventingDrainScope : IEventingDrainScope
{
    private static readonly IDisposable Noop = new NoopScope();

    public IDisposable Begin(EventingDrainTarget target) => Noop;

    private sealed class NoopScope : IDisposable
    {
        public void Dispose() { }
    }
}
```

- [ ] **Step 4: Register the defaults**

In `AddEventingCore`, next to the existing `TryAddSingleton<IEventTenantScope, NullEventTenantScope>()`:

```csharp
        services.TryAddSingleton<IEventingDrainTargetProvider, SingleDatabaseDrainTargetProvider>();
        services.TryAddSingleton<IEventingDrainScope, NullEventingDrainScope>();
```

- [ ] **Step 5: Run to verify it passes**

Run: `dotnet test src/Tests/Framework.Tests --filter FullyQualifiedName~SingleDatabaseDrainTargetProviderTests`
Expected: PASS

- [ ] **Step 6: Commit**

```bash
git add src/BuildingBlocks/Eventing.Abstractions/ src/BuildingBlocks/Eventing/ \
        src/Tests/Framework.Tests/Eventing/SingleDatabaseDrainTargetProviderTests.cs
git commit -m "feat(eventing): add drain-target abstractions for per-tenant databases"
```

---

### Task 2.2: Dispatch every target

**Files:**
- Modify: `src/BuildingBlocks/Eventing/Outbox/OutboxDispatcherHostedService.cs:66-71`
- Test: `src/Tests/Framework.Tests/Eventing/OutboxDispatcherHostedServiceTests.cs` (create)

**Interfaces:**
- Consumes: `IEventingDrainTargetProvider`, `IEventingDrainScope` from Task 2.1.
- Produces: no new public API; `DispatchOutboxAsync` becomes a loop over targets, one DI scope per target.

- [ ] **Step 1: Write the failing test**

Assert that with a provider returning two targets, the scope is entered twice with the expected targets, and one dispatch happens per target. Use a recording `IEventingDrainScope` in the spirit of `RecordingTenantScope` in the existing `InMemoryEventBusTenantScopeTests.cs`.

```csharp
[Fact]
public async Task Drains_Once_Per_Target()
{
    var targets = new List<EventingDrainTarget>
    {
        new(null, null),
        new("acme", "Host=acme;Database=acme;Username=u;Password=p"),
    };
    var recordingScope = new RecordingDrainScope();
    // ... build a ServiceProvider with a substituted IOutboxStore that returns
    // an empty batch, a stub IEventingDrainTargetProvider returning `targets`,
    // and `recordingScope`; then invoke one dispatch cycle.

    recordingScope.Begun.ShouldBe(targets);
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test src/Tests/Framework.Tests --filter FullyQualifiedName~OutboxDispatcherHostedServiceTests`
Expected: FAIL — only one drain occurs, `Begun` has a single entry.

- [ ] **Step 3: Rewrite DispatchOutboxAsync**

```csharp
    private async Task DispatchOutboxAsync(CancellationToken ct)
    {
        // Resolved per cycle: tenants (and therefore databases) can be added at runtime.
        using var providerScope = _scopeFactory.CreateScope();
        var targetProvider = providerScope.ServiceProvider.GetRequiredService<IEventingDrainTargetProvider>();
        var drainScope = providerScope.ServiceProvider.GetRequiredService<IEventingDrainScope>();

        var targets = await targetProvider.GetTargetsAsync(ct).ConfigureAwait(false);

        foreach (var target in targets)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                // Tenant context must be installed BEFORE the scope's EventingDbContext is
                // constructed — BaseDbContext captures TenantInfo (and its connection string)
                // at construction time.
                using (drainScope.Begin(target))
                {
                    using var scope = _scopeFactory.CreateScope();
                    var dispatcher = scope.ServiceProvider.GetRequiredService<OutboxDispatcher>();
                    await dispatcher.DispatchAsync(ct).ConfigureAwait(false);
                }
            }
            // Broad catch is intentional: one unreachable tenant database must not
            // stop the others from draining this cycle.
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                _logger.LogError(ex, "Failed to drain outbox for tenant {TenantId}", target.TenantId ?? "(default)");
            }
        }
    }
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test src/Tests/Framework.Tests --filter FullyQualifiedName~OutboxDispatcherHostedServiceTests`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add src/BuildingBlocks/Eventing/Outbox/OutboxDispatcherHostedService.cs \
        src/Tests/Framework.Tests/Eventing/OutboxDispatcherHostedServiceTests.cs
git commit -m "fix(eventing): drain the outbox of every tenant database, not just the default"
```

---

### Task 2.3: Multitenancy-backed provider and scope

**Files:**
- Create: `src/Modules/Multitenancy/Modules.Multitenancy/Services/TenantStoreDrainTargetProvider.cs`
- Create: `src/Modules/Multitenancy/Modules.Multitenancy/Services/FinbuckleEventingDrainScope.cs`
- Modify: `src/Modules/Multitenancy/Modules.Multitenancy/MultitenancyModule.cs:75`
- Test: `src/Tests/Multitenancy.Tests/TenantStoreDrainTargetProviderTests.cs`

**Interfaces:**
- Consumes: `IMultiTenantStore<AppTenantInfo>`, `IMultiTenantContextSetter`, `IMultiTenantContextAccessor<AppTenantInfo>`, `EventingDrainTarget`.
- Produces: `TenantStoreDrainTargetProvider : IEventingDrainTargetProvider`, `FinbuckleEventingDrainScope : IEventingDrainScope`.

- [ ] **Step 1: Write the failing test**

```csharp
[Fact]
public async Task Returns_Default_Plus_One_Target_Per_Distinct_Connection_String()
{
    var store = Substitute.For<IMultiTenantStore<AppTenantInfo>>();
    store.GetAllAsync().Returns(new List<AppTenantInfo>
    {
        new("shared-a", "Shared A") { IsActive = true },                                  // default DB
        new("shared-b", "Shared B", "", null, null) { IsActive = true },                  // default DB
        new("acme", "Acme", "Host=acme;Database=acme;Username=u;Password=p", null, null) { IsActive = true },
        new("acme-2", "Acme Two", "Host=acme;Database=acme;Username=u;Password=p", null, null) { IsActive = true },
        new("gone", "Inactive", "Host=gone;Database=gone;Username=u;Password=p", null, null) { IsActive = false },
    });

    var provider = new TenantStoreDrainTargetProvider(store);

    var targets = await provider.GetTargetsAsync(TestContext.Current.CancellationToken);

    targets.Count.ShouldBe(2, "the default pass plus one per distinct active connection string");
    targets.ShouldContain(t => t.ConnectionString is null);
    targets.Count(t => t.ConnectionString == "Host=acme;Database=acme;Username=u;Password=p")
        .ShouldBe(1, "two tenants sharing a database must be drained once, not twice");
    targets.ShouldNotContain(t => t.TenantId == "gone", "inactive tenants are not drained");
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test src/Tests/Multitenancy.Tests --filter FullyQualifiedName~TenantStoreDrainTargetProviderTests`
Expected: FAIL to compile.

- [ ] **Step 3: Write the provider**

```csharp
using Finbuckle.MultiTenant.Abstractions;
using FSH.Framework.Eventing.Abstractions;
using FSH.Framework.Shared.Multitenancy;

namespace FSH.Modules.Multitenancy.Services;

/// <summary>
/// Returns the default database plus one target per distinct active per-tenant
/// connection string. Tenants sharing a database collapse to a single target —
/// outbox rows are not tenant-filtered, so one pass drains all of them.
/// </summary>
public sealed class TenantStoreDrainTargetProvider : IEventingDrainTargetProvider
{
    private readonly IMultiTenantStore<AppTenantInfo> _tenantStore;

    public TenantStoreDrainTargetProvider(IMultiTenantStore<AppTenantInfo> tenantStore)
        => _tenantStore = tenantStore;

    public async Task<IReadOnlyList<EventingDrainTarget>> GetTargetsAsync(CancellationToken ct = default)
    {
        var tenants = await _tenantStore.GetAllAsync().ConfigureAwait(false);

        var dedicated = tenants
            .Where(t => t.IsActive && !string.IsNullOrWhiteSpace(t.ConnectionString))
            .GroupBy(t => t.ConnectionString!, StringComparer.Ordinal)
            .Select(g => new EventingDrainTarget(g.First().Id, g.Key));

        return [new EventingDrainTarget(null, null), .. dedicated];
    }
}
```

- [ ] **Step 4: Write the drain scope**

```csharp
using Finbuckle.MultiTenant;
using Finbuckle.MultiTenant.Abstractions;
using FSH.Framework.Eventing.Abstractions;
using FSH.Framework.Shared.Multitenancy;

namespace FSH.Modules.Multitenancy.Services;

/// <summary>
/// Installs a full <see cref="AppTenantInfo"/> — crucially including the
/// connection string — so an EventingDbContext built inside the scope routes to
/// that tenant's database. <see cref="FinbuckleEventTenantScope"/> deliberately
/// does not carry the connection string; drains do.
/// </summary>
public sealed class FinbuckleEventingDrainScope : IEventingDrainScope
{
    private readonly IMultiTenantContextAccessor<AppTenantInfo> _accessor;
    private readonly IMultiTenantContextSetter _setter;

    public FinbuckleEventingDrainScope(
        IMultiTenantContextAccessor<AppTenantInfo> accessor,
        IMultiTenantContextSetter setter)
    {
        _accessor = accessor;
        _setter = setter;
    }

    public IDisposable Begin(EventingDrainTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);

        if (target.ConnectionString is null && target.TenantId is null)
        {
            // Default pass: leave the ambient context alone so the context falls
            // through to the configured default connection.
            return NoopScope.Instance;
        }

        var previous = _accessor.MultiTenantContext;
        var info = new AppTenantInfo(target.TenantId!, target.TenantId!, target.ConnectionString, null, null);
        _setter.MultiTenantContext = new MultiTenantContext<AppTenantInfo> { TenantInfo = info };

        return new RestoreScope(_setter, previous);
    }

    private sealed class RestoreScope : IDisposable
    {
        private readonly IMultiTenantContextSetter _setter;
        private readonly IMultiTenantContext<AppTenantInfo> _previous;

        public RestoreScope(IMultiTenantContextSetter setter, IMultiTenantContext<AppTenantInfo> previous)
        {
            _setter = setter;
            _previous = previous;
        }

        public void Dispose() => _setter.MultiTenantContext = _previous;
    }

    private sealed class NoopScope : IDisposable
    {
        public static readonly NoopScope Instance = new();
        public void Dispose() { }
    }
}
```

Match the real `AppTenantInfo` constructor signature — check `src/BuildingBlocks/Shared/Multitenancy/AppTenantInfo.cs` and adjust the argument list if it differs.

- [ ] **Step 5: Register both**

In `MultitenancyModule.cs`, beside the existing `IEventTenantScope` replacement at line 75:

```csharp
        builder.Services.Replace(
            ServiceDescriptor.Singleton<IEventingDrainScope, FinbuckleEventingDrainScope>());
        builder.Services.Replace(
            ServiceDescriptor.Scoped<IEventingDrainTargetProvider, TenantStoreDrainTargetProvider>());
```

`TenantStoreDrainTargetProvider` is scoped because `IMultiTenantStore` is; `FinbuckleEventingDrainScope` matches the existing singleton `IEventTenantScope` lifetime. Verify `IMultiTenantContextSetter`'s registered lifetime allows singleton capture — if it is scoped, register the drain scope as scoped and resolve it inside the per-cycle scope in Task 2.2 (it already is).

- [ ] **Step 6: Run to verify it passes**

Run: `dotnet test src/Tests/Multitenancy.Tests --filter FullyQualifiedName~TenantStoreDrainTargetProviderTests`
Expected: PASS

- [ ] **Step 7: Full build + unit suite**

Run: `dotnet build src/FS.Proxy.slnx && dotnet test src/FS.Proxy.slnx --filter "FullyQualifiedName!~Integration"`
Expected: PASS

- [ ] **Step 8: Commit**

```bash
git add src/Modules/Multitenancy/ src/Tests/Multitenancy.Tests/TenantStoreDrainTargetProviderTests.cs
git commit -m "feat(multitenancy): drain per-tenant databases in the outbox dispatcher"
```

**Phase 2 exit criteria:** a tenant with a dedicated connection string has its outbox dispatched. Single-database deployments behave exactly as before (one target, one pass).

---

# Phase 3 — Lease-based row claiming

Today `IdentityModule.cs:189-190` documents that a second dispatcher would race the same rows because there is no row-level claim. That makes the API unsafe to scale past one instance.

## File Structure (Phase 3)

- Modify: `src/BuildingBlocks/Eventing/Outbox/OutboxMessage.cs` — add `ClaimedUntilUtc`, `ClaimedBy`, index.
- Modify: `src/BuildingBlocks/Eventing/Outbox/IOutboxStore.cs` — `GetPendingBatchAsync` → `ClaimBatchAsync`.
- Modify: `src/BuildingBlocks/Eventing/Outbox/EfCoreOutboxStore.cs` — Postgres `FOR UPDATE SKIP LOCKED` claim.
- Modify: `src/BuildingBlocks/Eventing/Outbox/OutboxDispatcher.cs` — call the claim.
- Modify: `src/BuildingBlocks/Eventing/EventingOptions.cs` — `OutboxClaimLeaseSeconds` (default 300).
- Create: migration in `src/Host/FS.Proxy.Migrations.PostgreSQL/Eventing/`.

### Task 3.1: Lease columns and index

**Files:**
- Modify: `src/BuildingBlocks/Eventing/Outbox/OutboxMessage.cs`
- Modify: `src/BuildingBlocks/Eventing/EventingOptions.cs`
- Test: `src/Tests/Framework.Tests/Eventing/EventingDbContextModelTests.cs` (extend)

**Interfaces:**
- Produces: `OutboxMessage.ClaimedUntilUtc` (`DateTime?`), `OutboxMessage.ClaimedBy` (`string?`, max 128); index `IX_OutboxMessages_Pending` over `(IsDead, ProcessedOnUtc, ClaimedUntilUtc, CreatedOnUtc)`; `EventingOptions.OutboxClaimLeaseSeconds` (`int`, default 300).

- [ ] **Step 1: Add a failing model assertion**

```csharp
[Fact]
public void Outbox_Has_Claim_Index_For_Pending_Scan()
{
    using var context = CreateContext();
    var entity = context.Model.FindEntityType(typeof(OutboxMessage))!;

    entity.GetIndexes()
        .Any(i => i.GetDatabaseName() == "IX_OutboxMessages_Pending")
        .ShouldBeTrue();
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test src/Tests/Framework.Tests --filter FullyQualifiedName~EventingDbContextModelTests`
Expected: FAIL — index not found.

- [ ] **Step 3: Add the properties and index**

In `OutboxMessage`:

```csharp
    /// <summary>
    /// Lease expiry. A dispatcher claims a row by stamping this into the future;
    /// another instance may re-claim only once it has passed, so a crashed
    /// dispatcher's rows are recovered rather than stranded.
    /// </summary>
    public DateTime? ClaimedUntilUtc { get; set; }

    /// <summary>Identifier of the dispatcher instance holding the lease. Diagnostic only.</summary>
    public string? ClaimedBy { get; set; }
```

In `OutboxMessageConfiguration.Configure`:

```csharp
        builder.Property(o => o.ClaimedBy)
            .HasMaxLength(128);

        builder.HasIndex(o => new { o.IsDead, o.ProcessedOnUtc, o.ClaimedUntilUtc, o.CreatedOnUtc })
            .HasDatabaseName("IX_OutboxMessages_Pending");
```

In `EventingOptions`:

```csharp
    /// <summary>
    /// Seconds a claimed outbox row stays leased to one dispatcher. Must exceed the
    /// worst-case time to publish a batch, or a second instance will re-claim rows
    /// still in flight and double-publish them.
    /// </summary>
    public int OutboxClaimLeaseSeconds { get; set; } = 300;
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test src/Tests/Framework.Tests --filter FullyQualifiedName~EventingDbContextModelTests`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add src/BuildingBlocks/Eventing/Outbox/OutboxMessage.cs src/BuildingBlocks/Eventing/EventingOptions.cs \
        src/Tests/Framework.Tests/Eventing/EventingDbContextModelTests.cs
git commit -m "feat(eventing): add outbox lease columns for multi-instance dispatch"
```

---

### Task 3.2: Atomic claim via FOR UPDATE SKIP LOCKED

**Files:**
- Modify: `src/BuildingBlocks/Eventing/Outbox/IOutboxStore.cs`
- Modify: `src/BuildingBlocks/Eventing/Outbox/EfCoreOutboxStore.cs`
- Modify: `src/BuildingBlocks/Eventing/Outbox/OutboxDispatcher.cs:40`
- Test: `src/Tests/Integration.Tests/Tests/Eventing/OutboxClaimTests.cs`

**Interfaces:**
- Produces: `IOutboxStore.ClaimBatchAsync(int batchSize, string claimedBy, TimeSpan lease, CancellationToken ct = default) → Task<IReadOnlyList<OutboxMessage>>`. `GetPendingBatchAsync` is removed. `MarkAsProcessedAsync`/`MarkAsFailedAsync` additionally clear `ClaimedUntilUtc` and `ClaimedBy`.

- [ ] **Step 1: Write the failing integration test**

Claiming is a concurrency behaviour — it needs real Postgres, so this is an integration test.

```csharp
[Fact]
public async Task Concurrent_Claims_Never_Return_The_Same_Row()
{
    // Seed 50 pending messages, then claim from two stores concurrently.
    var first = ClaimAsync(batchSize: 50);
    var second = ClaimAsync(batchSize: 50);
    var results = await Task.WhenAll(first, second);

    var allIds = results.SelectMany(r => r.Select(m => m.Id)).ToList();
    allIds.Distinct().Count().ShouldBe(allIds.Count, "SKIP LOCKED must hand each row to exactly one claimant");
    allIds.Count.ShouldBe(50);
}

[Fact]
public async Task Expired_Lease_Is_Reclaimable()
{
    // Claim with a lease already in the past, then claim again from a second store.
    var reclaimed = await SecondStore.ClaimBatchAsync(10, "instance-b", TimeSpan.FromMinutes(5), Ct);
    reclaimed.ShouldNotBeEmpty("a crashed dispatcher's rows must not be stranded");
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test src/Tests/Integration.Tests --filter FullyQualifiedName~OutboxClaimTests`
Expected: FAIL to compile — `ClaimBatchAsync` does not exist.

- [ ] **Step 3: Change the interface**

In `IOutboxStore.cs`, replace `GetPendingBatchAsync` with:

```csharp
    /// <summary>
    /// Atomically leases up to <paramref name="batchSize"/> pending messages to this
    /// dispatcher and returns them. Rows already leased by another instance are skipped
    /// rather than waited on, so instances never block each other and never both
    /// publish the same message.
    /// </summary>
    Task<IReadOnlyList<OutboxMessage>> ClaimBatchAsync(
        int batchSize,
        string claimedBy,
        TimeSpan lease,
        CancellationToken ct = default);
```

- [ ] **Step 4: Implement the Postgres claim**

In `EfCoreOutboxStore`:

```csharp
    public async Task<IReadOnlyList<OutboxMessage>> ClaimBatchAsync(
        int batchSize,
        string claimedBy,
        TimeSpan lease,
        CancellationToken ct = default)
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var until = now.Add(lease);

        if (!_dbContext.Database.IsNpgsql())
        {
            // Non-Postgres providers have no portable SKIP LOCKED. Fall back to an
            // unclaimed read; safe only for a single dispatcher instance.
            LogClaimUnsupported(_dbContext.Database.ProviderName);
            return await _dbContext.Set<OutboxMessage>()
                .Where(m => !m.IsDead && m.ProcessedOnUtc == null)
                .OrderBy(m => m.CreatedOnUtc)
                .Take(batchSize)
                .ToListAsync(ct)
                .ConfigureAwait(false);
        }

        var sql = $"""
            UPDATE "{EventingConstants.SchemaName}"."OutboxMessages" AS o
            SET "ClaimedUntilUtc" = {{1}}, "ClaimedBy" = {{2}}
            FROM (
                SELECT "Id"
                FROM "{EventingConstants.SchemaName}"."OutboxMessages"
                WHERE "IsDead" = FALSE
                  AND "ProcessedOnUtc" IS NULL
                  AND ("ClaimedUntilUtc" IS NULL OR "ClaimedUntilUtc" < {{0}})
                ORDER BY "CreatedOnUtc"
                LIMIT {{3}}
                FOR UPDATE SKIP LOCKED
            ) AS c
            WHERE o."Id" = c."Id"
            RETURNING o.*
            """;

        var claimed = await _dbContext.Set<OutboxMessage>()
            .FromSqlRaw(sql, now, until, claimedBy, batchSize)
            .AsNoTracking()
            .ToListAsync(ct)
            .ConfigureAwait(false);

        // Re-attach so MarkAsProcessed/MarkAsFailed can update these instances.
        foreach (var message in claimed)
        {
            _dbContext.Attach(message);
        }

        return claimed;
    }

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Provider {Provider} does not support SKIP LOCKED; outbox claiming is disabled. Run a single dispatcher instance.")]
    private partial void LogClaimUnsupported(string? provider);
```

The class must become `public sealed partial class EfCoreOutboxStore` for `[LoggerMessage]`. Verify the interpolated schema name is a compile-time constant (it is — `EventingConstants.SchemaName`), so no SQL injection surface; the four runtime values stay parameterised via `FromSqlRaw`'s `{0}`-style placeholders.

- [ ] **Step 5: Clear the lease on completion**

In `MarkAsProcessedAsync` and `MarkAsFailedAsync`, before `Update(message)`:

```csharp
        message.ClaimedUntilUtc = null;
        message.ClaimedBy = null;
```

- [ ] **Step 6: Call it from the dispatcher**

In `OutboxDispatcher.DispatchAsync`, replace line 40:

```csharp
        var lease = TimeSpan.FromSeconds(_options.OutboxClaimLeaseSeconds > 0 ? _options.OutboxClaimLeaseSeconds : 300);
        var messages = await _outbox.ClaimBatchAsync(batchSize, _instanceId, lease, ct).ConfigureAwait(false);
```

Add an `_instanceId` field set in the constructor to `$"{Environment.MachineName}:{Environment.ProcessId}"`.

- [ ] **Step 7: Generate the migration**

```bash
dotnet build src/FS.Proxy.slnx
dotnet ef migrations add OutboxClaimLease \
  --project src/Host/FS.Proxy.Migrations.PostgreSQL \
  --startup-project src/Host/FS.Proxy.DbMigrator \
  --context EventingDbContext \
  --output-dir Eventing
```

- [ ] **Step 8: Run the integration tests**

Run: `dotnet test src/Tests/Integration.Tests --filter FullyQualifiedName~OutboxClaimTests`
Expected: PASS

- [ ] **Step 9: Update the stale comment**

`IdentityModule.cs:189-190` says a second dispatcher would race "(no row-level claim)". Rewrite it to state that claiming now exists and the module still registers no dispatcher of its own.

- [ ] **Step 10: Commit**

```bash
git add src/BuildingBlocks/Eventing/Outbox/ src/Host/FS.Proxy.Migrations.PostgreSQL/Eventing/ \
        src/Modules/Identity/Modules.Identity/IdentityModule.cs \
        src/Tests/Integration.Tests/Tests/Eventing/OutboxClaimTests.cs
git commit -m "feat(eventing): lease outbox rows with FOR UPDATE SKIP LOCKED for multi-instance dispatch"
```

**Phase 3 exit criteria:** two dispatcher instances draining concurrently never publish the same message; a lease that expires is reclaimable.

---

# Phase 4 — Real atomicity

The highest-risk phase. Review it hardest. Until it lands, an outbox write is a separate transaction from the business write — a crash between them loses the event. Everything before this phase is still a strict improvement on today.

**The mechanism:** `EventingDbContext` must share the *same* `DbConnection` object as the module context in the scope, because Npgsql will not promote two connections to a distributed transaction. A scoped connection cache keyed by connection string gives every context in a scope one connection; the outbox store then enlists in whatever transaction the module context has open.

### Task 4.1: Scoped shared connection

**Files:**
- Create: `src/BuildingBlocks/Persistence/ScopedDbConnectionProvider.cs`
- Modify: `src/BuildingBlocks/Persistence/OptionsBuilderExtensions.cs` — `ConfigureHeroDatabase` overload taking a `DbConnection`.
- Modify: `src/BuildingBlocks/Persistence/PersistenceExtensions.cs` — resolve the shared connection in `AddHeroDbContext`.
- Test: `src/Tests/Integration.Tests/Tests/Eventing/SharedConnectionTests.cs`

**Interfaces:**
- Produces: `IScopedDbConnectionProvider { DbConnection GetConnection(string connectionString); }`, scoped, disposing every connection it created at scope end.

- [ ] **Step 1: Write the failing integration test**

```csharp
[Fact]
public async Task Module_Context_And_EventingContext_Share_One_Connection()
{
    using var scope = Fixture.Services.CreateScope();
    SetTenantContext(scope, MultitenancyConstants.Root.Id);

    var catalog = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
    var eventing = scope.ServiceProvider.GetRequiredService<EventingDbContext>();

    ReferenceEquals(catalog.Database.GetDbConnection(), eventing.Database.GetDbConnection())
        .ShouldBeTrue("a shared DbConnection is what lets the outbox write join the business transaction");
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test src/Tests/Integration.Tests --filter FullyQualifiedName~SharedConnectionTests`
Expected: FAIL — distinct connection instances.

- [ ] **Step 3: Implement the provider**

```csharp
using System.Data.Common;

namespace FSH.Framework.Persistence;

/// <summary>
/// One <see cref="DbConnection"/> per connection string per DI scope, shared by
/// every DbContext in that scope. Required for cross-context transactions: EF Core
/// can enlist a second context in an existing transaction only when both contexts
/// use the same connection instance, and Npgsql does not support promoting two
/// connections to a distributed transaction.
/// </summary>
public interface IScopedDbConnectionProvider
{
    DbConnection GetConnection(string connectionString);
}
```

Implement with a `Dictionary<string, DbConnection>` created via `NpgsqlDataSource`/provider factory, and `IAsyncDisposable`/`IDisposable` disposing all cached connections.

- [ ] **Step 4: Wire it into AddHeroDbContext**

`ConfigureHeroDatabase` gains an overload that takes the resolved `DbConnection` and calls `UseNpgsql(connection, …)`. `AddHeroDbContext<TContext>` resolves `IScopedDbConnectionProvider` from `sp` and passes the shared connection.

> **Caution:** `BaseDbContext.OnConfiguring` also calls `ConfigureHeroDatabase` for per-tenant connection strings. It must go through the same provider, or a tenant context will re-open a private connection and defeat the sharing. Update both call sites.

- [ ] **Step 5: Run to verify it passes**

Run: `dotnet test src/Tests/Integration.Tests --filter FullyQualifiedName~SharedConnectionTests`
Expected: PASS

- [ ] **Step 6: Run the whole integration suite**

Run: `dotnet test src/Tests/Integration.Tests`
Expected: PASS. Connection sharing changes lifetime behaviour across ~390 tests — treat any new failure as a real regression, not flake.

- [ ] **Step 7: Commit**

```bash
git add src/BuildingBlocks/Persistence/ src/Tests/Integration.Tests/Tests/Eventing/SharedConnectionTests.cs
git commit -m "feat(persistence): share one DbConnection per scope across DbContexts"
```

---

### Task 4.2: Enlist the outbox write in the caller's transaction

**Files:**
- Modify: `src/BuildingBlocks/Eventing/Outbox/EfCoreOutboxStore.cs`
- Create: `src/BuildingBlocks/Eventing/Persistence/EventingTransactionEnlister.cs`
- Test: `src/Tests/Integration.Tests/Tests/Eventing/OutboxAtomicityTests.cs`

**Interfaces:**
- Produces: `EfCoreOutboxStore.AddAsync` enlists `EventingDbContext` in the ambient transaction when one exists; when none exists it behaves as today (its own transaction).

- [ ] **Step 1: Write the failing test**

```csharp
[Fact]
public async Task Outbox_Write_Rolls_Back_With_The_Business_Transaction()
{
    using var scope = Fixture.Services.CreateScope();
    SetTenantContext(scope, MultitenancyConstants.Root.Id);

    var catalog = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
    var store = scope.ServiceProvider.GetRequiredService<IOutboxStore>();
    var eventing = scope.ServiceProvider.GetRequiredService<EventingDbContext>();
    var eventId = Guid.CreateVersion7();

    await using (var tx = await catalog.Database.BeginTransactionAsync(TestContext.Current.CancellationToken))
    {
        catalog.Products.Add(NewProduct());
        await catalog.SaveChangesAsync(TestContext.Current.CancellationToken);
        await store.AddAsync(BuildEvent(eventId), TestContext.Current.CancellationToken);
        await tx.RollbackAsync(TestContext.Current.CancellationToken);
    }

    (await eventing.OutboxMessages.AnyAsync(m => m.Id == eventId, TestContext.Current.CancellationToken))
        .ShouldBeFalse("the outbox row must not survive a rolled-back business transaction");
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test src/Tests/Integration.Tests --filter FullyQualifiedName~OutboxAtomicityTests`
Expected: FAIL — the outbox row is committed by its own `SaveChangesAsync` and survives the rollback.

- [ ] **Step 3: Implement enlistment**

At the top of `AddAsync`, before `SaveChangesAsync`:

```csharp
        // Join the caller's transaction when there is one, so the outbox row commits
        // or rolls back with the business data. Requires the shared-connection provider
        // from Task 4.1 — EF can only enlist contexts sharing a DbConnection.
        var connection = _dbContext.Database.GetDbConnection();
        var ambient = _enlister.FindAmbientTransaction(connection);
        if (ambient is not null && _dbContext.Database.CurrentTransaction is null)
        {
            await _dbContext.Database.UseTransactionAsync(ambient, ct).ConfigureAwait(false);
        }
```

`EventingTransactionEnlister` tracks open transactions per connection. The simplest correct implementation reads `DbConnection`-level state; if that proves unreliable, register a scoped `AmbientTransactionRegistry` that a `SaveChangesInterceptor` populates whenever any context begins a transaction.

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test src/Tests/Integration.Tests --filter FullyQualifiedName~OutboxAtomicityTests`
Expected: PASS

- [ ] **Step 5: Full suite**

Run: `dotnet test src/FS.Proxy.slnx`
Expected: PASS

- [ ] **Step 6: Commit**

```bash
git add src/BuildingBlocks/Eventing/ src/Tests/Integration.Tests/Tests/Eventing/OutboxAtomicityTests.cs
git commit -m "feat(eventing): enlist outbox writes in the caller's transaction"
```

**Phase 4 exit criteria:** an outbox row written inside a rolled-back business transaction does not survive. `eventing.md`'s "commits in the same transaction" claim becomes true.

---

# Phase 5 — Move direct bus publishes onto the outbox

`eventing.md:10-12` states the outbox is the only way to publish and `IEventBus` must never be called from a handler. Eight call sites violate it. Each becomes durable and crash-safe; each also becomes **asynchronous**, so any consumer relying on synchronous in-scope execution must be checked.

**Call sites:**

| File | Line | Event |
|---|---|---|
| `src/Modules/Billing/Modules.Billing/Services/BillingService.cs` | 217 | `InvoiceIssuedIntegrationEvent` |
| `src/Modules/Billing/Modules.Billing/Services/BillingService.cs` | 348 | `InvoiceIssuedIntegrationEvent` |
| `src/Modules/Files/Modules.Files/Features/v1/FinalizeUpload/FinalizeUploadCommandHandler.cs` | 86 | `FileFinalizedIntegrationEvent` |
| `src/Modules/Chat/Modules.Chat/Features/v1/Messages/SendMessage/SendMessageCommandHandler.cs` | 109 | chat message event |
| `src/Modules/Multitenancy/Modules.Multitenancy/Features/v1/CreateTenant/CreateTenantCommandHandler.cs` | 55 | `TenantSubscribedIntegrationEvent` |
| `src/Modules/Multitenancy/Modules.Multitenancy/Features/v1/RenewTenant/RenewTenantCommandHandler.cs` | 36 | `TenantRenewedIntegrationEvent` |
| `src/Modules/Multitenancy/Modules.Multitenancy/Services/TenantExpiryScanJob.cs` | 123 | tenant expiry notice |
| `src/Modules/Identity/Modules.Identity/Events/UserRegisteredEventHandler.cs` | 41 | `UserRegisteredIntegrationEvent` |

### Task 5.1 – 5.8: One task per call site

Each follows the identical shape. Do them one at a time, smallest blast radius first (Files, then Billing, then Multitenancy, then Chat, then Identity).

- [ ] **Step 1: Write a failing test** asserting the handler writes an outbox row rather than calling the bus. Substitute `IEventBus` and assert `PublishAsync` was **not** called; substitute `IOutboxStore` and assert `AddAsync` was called once with the expected event.
- [ ] **Step 2: Run it** — expect FAIL (the bus is called).
- [ ] **Step 3: Swap the dependency** — replace the `IEventBus` constructor parameter with `IOutboxStore`, and `await bus.PublishAsync(evt, ct)` with `await outbox.AddAsync(evt, ct)`.
- [ ] **Step 4: Run it** — expect PASS.
- [ ] **Step 5: Check the consumers** of that event for a synchronous-execution assumption. Chat → Notifications is the one to look at hardest (`add-integration-event/SKILL.md:86` documents the load-order coupling). If a consumer needs synchronous behaviour, note it in the plan and leave that call site on the bus with a comment explaining why.
- [ ] **Step 6: Commit** — `refactor({module}): publish {Event} via the outbox`.

> **Real risk:** `TenantExpiryScanJob` (`:123`) runs as a background job and already sets tenant context before publishing (memory: `project_background_event_publish_tenant_context`). Verify the outbox write happens *inside* that tenant scope, or the row lands in the wrong database once Phase 2 routes by tenant.

**Phase 5 exit criteria:** no `IEventBus.PublishAsync` calls remain in module handlers, or each remaining one carries a comment justifying it.

---

# Phase 6 — Documentation

Golden rule #10: a user-facing change isn't done until docs match.

### Task 6.1: Repo rules and skills

**Files:**
- Modify: `.agents/rules/eventing.md`
- Modify: `.agents/skills/add-module/SKILL.md:42-45`
- Modify: `.agents/skills/add-integration-event/SKILL.md:33,91`

- [ ] **Step 1: Rewrite `.agents/rules/eventing.md`**

The "Wiring (3 calls)" section becomes two calls — `AddEventingCore` is now a host-level concern and `AddEventingForDbContext` is gone:

```markdown
## Wiring (1 call in the module's `ConfigureServices`)

```csharp
services.AddIntegrationEventHandlers(typeof(MyModule).Assembly);   // scans IIntegrationEventHandler<>
```

The outbox itself is framework infrastructure — `AddEventingCore` is called once by the host
(`FS.Proxy.Api/Program.cs`), and `EventingDbContext` owns `framework.OutboxMessages` /
`framework.InboxMessages`. Modules never register an outbox store; inject `IOutboxStore` and publish.
```

Add a section documenting: the `framework` schema, per-tenant drain targets, lease-based claiming, and that atomicity depends on the shared-connection provider.

- [ ] **Step 2: Fix `add-module/SKILL.md`**

Replace lines 42-45 with:

```csharp
        // Only if the module handles integration events:
        // builder.Services.AddIntegrationEventHandlers(typeof({Name}Module).Assembly);
```

Publishing needs no registration at all. Add a one-line note: *the outbox is framework-owned; do not register a per-module store.*

- [ ] **Step 3: Fix `add-integration-event/SKILL.md`**

Line 33: drop the `AddEventingForDbContext<{Source}DbContext>` requirement — inject `IOutboxStore` directly. Line 91 checklist item: replace with "published via `IOutboxStore.AddAsync` (not the bus); no per-module eventing registration needed".

- [ ] **Step 4: Commit**

```bash
git add .agents/
git commit -m "docs(agents): update eventing rules and skills for the framework-owned outbox (#1349)"
```

### Task 6.2: Docs site and changelog

**Files:** the separate docs repo at `C:\Users\mukesh\repos\fullstackhero\docs`.

- [ ] **Step 1** — update the eventing/architecture pages to match the new model.
- [ ] **Step 2** — add a changelog entry under `src/content/docs/changelog/` covering: the `framework` schema and its migration, `AddEventingForDbContext` removal (breaking for anyone who called it), per-tenant outbox dispatch, and multi-instance claiming.
- [ ] **Step 3** — commit in the docs repo. **Do not push** without asking.

### Task 6.3: Close the loop on the issue

- [ ] **Step 1** — draft a reply to #1349 summarising what shipped and what changed for module authors. **Get approval before posting** (global rule: never publish externally without explicit approval).

---

## Self-review notes

**Spec coverage.** Every element of the agreed C1 design maps to a phase: framework-owned context (1), tenant-following connection (1.2, inherited from `BaseDbContext`), single unambiguous store (1.3–1.5, guarded by 1.1), tenant-enumerating dispatcher (2), row claiming (3), true atomicity (4). Phases 5–6 close the docs/code gap that made the issue reportable in the first place.

**Known soft spots — review these hardest:**

1. **Task 4.1 has the widest blast radius in the plan.** Sharing one `DbConnection` per scope changes connection lifetime for every context in the app, and `BaseDbContext.OnConfiguring` opens a second path that must be routed through the same provider. If the integration suite goes red here in ways that aren't obviously the new behaviour, stop and reconsider rather than patching through.
2. **Task 4.2's ambient-transaction discovery is specified with a fallback** because I could not confirm from the codebase that `DbConnection`-level state is a reliable source. If the direct approach is flaky, use the registry variant described in the step.
3. **Task 1.7's cross-context migration ordering** is guarded by `to_regclass` checks rather than assumed ordering, because EF gives no ordering guarantee between two contexts' migrations. On a fresh database the copy is a no-op, which is correct.
4. **Phase 5 changes event delivery from synchronous to asynchronous.** Step 5 of each task exists specifically to catch consumers that depend on the old timing; Chat → Notifications is the likeliest to break.
5. **`EventingOptions.OutboxClaimLeaseSeconds` default of 300s** is a guess. If a batch of 100 events can take longer than five minutes to publish, raise it — an expired lease mid-batch means double-publish.
