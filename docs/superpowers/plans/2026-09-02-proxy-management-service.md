# Proxy Management Service Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a new `Proxies` module inside the fs-proxy monolith that centralizes proxy inventory (WebShare, Oxylabs, BrightData, manual), exposes a dual-auth REST API for scrapers to request/report on proxies, runs automated health checks and per-tag disable/renew policies, and ships an admin UI in `clients/admin`.

**Architecture:** Vertical Slice module (`Modules.Proxies` + `Modules.Proxies.Contracts`) following this repo's existing Catalog/Webhooks patterns exactly: Mediator CQRS slices, EF Core with `IGlobalEntity` (single-tenant opt-out), Hangfire for sync/health-check/policy jobs, HybridCache for hot-path selection and sticky sessions, Data Protection for credential encryption, and a new dual authentication scheme (API Key + JWT) with no prior precedent in this repo.

**Tech Stack:** .NET 10, EF Core 10/PostgreSQL, Mediator 3.x, FluentValidation, Hangfire, HybridCache/Redis, Polly (via `AddHeroResilience`), ASP.NET Data Protection, React 19 + TanStack Query v5 + Radix/Tailwind (`clients/admin`).

**Spec:** `docs/superpowers/specs/2026-09-02-proxy-management-service-design.md`

## Global Constraints

- Module boundaries: reference other modules only through their `.Contracts` project (golden rule #1).
- Registering the module touches FOUR places: `Program.cs` Mediator `o.Assemblies` (two markers) + `moduleAssemblies` array, and the identical pair in `DbMigrator/Program.cs` (golden rule #2).
- All `Proxies` entities implement `IGlobalEntity` — this is a single-tenant internal tool, not multi-tenant (per approved spec, "Scope" section).
- `base.OnModelCreating` is called **last** in `ProxiesDbContext.OnModelCreating` (golden rule #3).
- Do **not** modify `src/BuildingBlocks`-equivalent framework source (it lives in the separate `fs-framework` repo and is consumed here as `FSH.Framework.*` NuGet packages from the local feed) — treat it as a package boundary, not editable source (golden rule #4).
- Mediator handlers are `public sealed`, return `ValueTask<T>`, `.ConfigureAwait(false)` every await (golden rule #5).
- Structured logging only, no string interpolation in log messages (golden rule #6).
- Propagate `CancellationToken` into every EF/IO call (golden rule #7).
- Every command handler and paginated query handler gets a `{Name}Validator` (golden rule #8).
- Frontend mutations pass per-call data through `mutate(arg)`, never via closed-over state (golden rule #9).
- `TreatWarningsAsErrors` is on — the build must be warning-free.
- No table library exists in `clients/admin` — lists are hand-rolled (see Milestone F).
- No prior precedent exists in this repo for API-key or dual-scheme authentication — Task 21 is genuinely new infrastructure, not a copy of an existing pattern.

---

## Milestone A — Module Foundation

### Task 1: Module skeleton, registration, and empty DbContext

**Files:**
- Create: `src/Modules/Proxies/Modules.Proxies.Contracts/Modules.Proxies.Contracts.csproj`
- Create: `src/Modules/Proxies/Modules.Proxies.Contracts/ProxiesContractsMarker.cs`
- Create: `src/Modules/Proxies/Modules.Proxies/Modules.Proxies.csproj`
- Create: `src/Modules/Proxies/Modules.Proxies/ProxiesModule.cs`
- Create: `src/Modules/Proxies/Modules.Proxies/Data/ProxiesDbContext.cs`
- Create: `src/Modules/Proxies/Modules.Proxies/Data/ProxiesDbInitializer.cs`
- Modify: `src/FS.Proxy.slnx`
- Modify: `src/Host/FS.Proxy.Api/Program.cs`
- Modify: `src/Host/FS.Proxy.DbMigrator/Program.cs`
- Modify: `src/Host/FS.Proxy.Migrations.PostgreSQL/FS.Proxy.Migrations.PostgreSQL.csproj`

**Interfaces:**
- Produces: `FSH.Modules.Proxies.Contracts.ProxiesContractsMarker` (Mediator scan anchor), `FSH.Modules.Proxies.ProxiesModule` (module entry point, order `650`), `FSH.Modules.Proxies.Data.ProxiesDbContext` (empty `DbSet`s added in Task 2).

This task has no unit test of its own — its deliverable is "the module loads and the host builds/runs." Verification is a build + boot, not TDD.

- [ ] **Step 1: Create the Contracts project**

```xml
<!-- src/Modules/Proxies/Modules.Proxies.Contracts/Modules.Proxies.Contracts.csproj -->
<Project Sdk="Microsoft.NET.Sdk">

	<PropertyGroup>
		<RootNamespace>FSH.Modules.Proxies.Contracts</RootNamespace>
		<AssemblyName>FSH.Modules.Proxies.Contracts</AssemblyName>
	</PropertyGroup>

	<ItemGroup>
		<PackageReference Include="Mediator.Abstractions" />
	</ItemGroup>

	<ItemGroup>
		<ProjectReference Include="..\..\..\BuildingBlocks\Shared\Shared.csproj" />
	</ItemGroup>

</Project>
```

```csharp
// src/Modules/Proxies/Modules.Proxies.Contracts/ProxiesContractsMarker.cs
namespace FSH.Modules.Proxies.Contracts;

public static class ProxiesContractsMarker;
```

Note: the `ProjectReference` to `..\..\..\BuildingBlocks\Shared\Shared.csproj` is intentional even though that directory doesn't exist in this checkout — `src/Directory.Build.targets` auto-detects the missing `BuildingBlocks` folder and rewrites it to `PackageReference Include="FSH.Framework.Shared"` at build time. This is confirmed working (Catalog uses the identical pattern).

- [ ] **Step 2: Create the runtime project**

```xml
<!-- src/Modules/Proxies/Modules.Proxies/Modules.Proxies.csproj -->
<Project Sdk="Microsoft.NET.Sdk">

	<PropertyGroup>
		<RootNamespace>FSH.Modules.Proxies</RootNamespace>
		<AssemblyName>FSH.Modules.Proxies</AssemblyName>
		<NoWarn>$(NoWarn);CA1031;CA1812;CA1859;S3267</NoWarn>
	</PropertyGroup>

	<ItemGroup>
		<ProjectReference Include="..\..\..\BuildingBlocks\Persistence\Persistence.csproj" />
		<ProjectReference Include="..\..\..\BuildingBlocks\Web\Web.csproj" />
		<ProjectReference Include="..\..\..\BuildingBlocks\Jobs\Jobs.csproj" />
		<ProjectReference Include="..\..\..\BuildingBlocks\Caching\Caching.csproj" />
		<ProjectReference Include="..\Modules.Proxies.Contracts\Modules.Proxies.Contracts.csproj" />
		<ProjectReference Include="..\..\Notifications\Modules.Notifications.Contracts\Modules.Notifications.Contracts.csproj" />
	</ItemGroup>

</Project>
```

- [ ] **Step 3: Create the module entry point**

```csharp
// src/Modules/Proxies/Modules.Proxies/ProxiesModule.cs
using Asp.Versioning;
using FSH.Framework.Persistence;
using FSH.Framework.Web.Modules;
using FSH.Modules.Proxies.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;

[assembly: FshModule(typeof(FSH.Modules.Proxies.ProxiesModule), 650)]

namespace FSH.Modules.Proxies;

public sealed class ProxiesModule : IModule
{
    public void ConfigureServices(IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.AddHeroDbContext<ProxiesDbContext>();
        builder.Services.AddScoped<IDbInitializer, ProxiesDbInitializer>();

        builder.Services.AddHealthChecks()
            .AddDbContextCheck<ProxiesDbContext>(name: "db:proxies", failureStatus: HealthStatus.Unhealthy);
    }

    public void ConfigureMiddleware(IApplicationBuilder app)
    {
        // No custom middleware needed yet.
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var versionSet = endpoints.NewApiVersionSet()
            .HasApiVersion(new ApiVersion(1))
            .ReportApiVersions()
            .Build();

        var group = endpoints
            .MapGroup("api/v{version:apiVersion}/proxies")
            .WithTags("Proxies")
            .WithApiVersionSet(versionSet)
            .RequireAuthorization();

        // Endpoint registrations added in later tasks.
    }
}
```

- [ ] **Step 4: Create the (initially empty) DbContext and initializer**

```csharp
// src/Modules/Proxies/Modules.Proxies/Data/ProxiesDbContext.cs
using Finbuckle.MultiTenant.Abstractions;
using FSH.Framework.Persistence.Context;
using FSH.Framework.Shared.Multitenancy;
using FSH.Framework.Shared.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace FSH.Modules.Proxies.Data;

public sealed class ProxiesDbContext : BaseDbContext
{
    public const string Schema = "proxies";

    public ProxiesDbContext(
        IMultiTenantContextAccessor<AppTenantInfo> multiTenantContextAccessor,
        DbContextOptions<ProxiesDbContext> options,
        IOptions<DatabaseOptions> settings,
        IHostEnvironment environment) : base(multiTenantContextAccessor, options, settings, environment) { }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.HasDefaultSchema(Schema);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(ProxiesDbContext).Assembly);
        base.OnModelCreating(modelBuilder);
    }
}
```

```csharp
// src/Modules/Proxies/Modules.Proxies/Data/ProxiesDbInitializer.cs
using FSH.Framework.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FSH.Modules.Proxies.Data;

public sealed class ProxiesDbInitializer(ProxiesDbContext dbContext, ILogger<ProxiesDbInitializer> logger)
    : IDbInitializer
{
    public async Task MigrateAsync(CancellationToken cancellationToken)
    {
        var pending = await dbContext.Database.GetPendingMigrationsAsync(cancellationToken).ConfigureAwait(false);
        if (pending.Any())
        {
            await dbContext.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
            logger.LogInformation("Applied {Count} pending Proxies migrations.", pending.Count());
        }
    }

    public Task SeedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
```

- [ ] **Step 5: Register the two new projects in the solution**

Add under the existing `Modules` folder in `src/FS.Proxy.slnx` (mirror the `Catalog` entries):

```xml
<Project Path="Modules/Proxies/Modules.Proxies/Modules.Proxies.csproj" />
<Project Path="Modules/Proxies/Modules.Proxies.Contracts/Modules.Proxies.Contracts.csproj" />
```

- [ ] **Step 6: Wire the FOUR registration points**

In `src/Host/FS.Proxy.Api/Program.cs`, add to `o.Assemblies` (Mediator) right after the Catalog entries:

```csharp
typeof(FSH.Modules.Proxies.Contracts.ProxiesContractsMarker),
typeof(FSH.Modules.Proxies.ProxiesModule),
```

and to `moduleAssemblies`:

```csharp
typeof(FSH.Modules.Proxies.ProxiesModule).Assembly,
```

Apply the identical two edits in `src/Host/FS.Proxy.DbMigrator/Program.cs`.

- [ ] **Step 7: Reference the new runtime project from the Migrations project**

```xml
<!-- add to src/Host/FS.Proxy.Migrations.PostgreSQL/FS.Proxy.Migrations.PostgreSQL.csproj, alongside the Catalog reference -->
<ProjectReference Include="..\..\Modules\Proxies\Modules.Proxies\Modules.Proxies.csproj" />
```

- [ ] **Step 8: Build the whole solution and fix any errors**

Run: `dotnet build src/FS.Proxy.slnx`
Expected: `Compilación correcta` / `Build succeeded`, 0 errors.

- [ ] **Step 9: Commit**

```bash
git add src/Modules/Proxies src/FS.Proxy.slnx src/Host/FS.Proxy.Api/Program.cs src/Host/FS.Proxy.DbMigrator/Program.cs src/Host/FS.Proxy.Migrations.PostgreSQL/FS.Proxy.Migrations.PostgreSQL.csproj
git commit -m "feat(proxies): scaffold Proxies module skeleton and wire the four registration points"
```

### Task 2: Domain entities and EF configurations

**Files:**
- Create: `src/Modules/Proxies/Modules.Proxies.Contracts/ProxyProviderType.cs`
- Create: `src/Modules/Proxies/Modules.Proxies/Domain/ProviderAccount.cs`
- Create: `src/Modules/Proxies/Modules.Proxies/Domain/Proxy.cs`
- Create: `src/Modules/Proxies/Modules.Proxies/Domain/ProxyTagAssignment.cs`
- Create: `src/Modules/Proxies/Modules.Proxies/Domain/Tag.cs`
- Create: `src/Modules/Proxies/Modules.Proxies/Domain/PolicyProfile.cs`
- Create: `src/Modules/Proxies/Modules.Proxies/Domain/TagPolicyAssignment.cs`
- Create: `src/Modules/Proxies/Modules.Proxies/Domain/HealthCheckTarget.cs`
- Create: `src/Modules/Proxies/Modules.Proxies/Domain/TagHealthCheckTargetAssignment.cs`
- Create: `src/Modules/Proxies/Modules.Proxies/Domain/ProxyUsageEvent.cs`
- Create: `src/Modules/Proxies/Modules.Proxies/Domain/ApiClient.cs`
- Create: `src/Modules/Proxies/Modules.Proxies/Data/Configurations/ProviderAccountConfiguration.cs`
- Create: `src/Modules/Proxies/Modules.Proxies/Data/Configurations/ProxyConfiguration.cs`
- Create: `src/Modules/Proxies/Modules.Proxies/Data/Configurations/ProxyTagAssignmentConfiguration.cs`
- Create: `src/Modules/Proxies/Modules.Proxies/Data/Configurations/TagConfiguration.cs`
- Create: `src/Modules/Proxies/Modules.Proxies/Data/Configurations/PolicyProfileConfiguration.cs`
- Create: `src/Modules/Proxies/Modules.Proxies/Data/Configurations/TagPolicyAssignmentConfiguration.cs`
- Create: `src/Modules/Proxies/Modules.Proxies/Data/Configurations/HealthCheckTargetConfiguration.cs`
- Create: `src/Modules/Proxies/Modules.Proxies/Data/Configurations/TagHealthCheckTargetAssignmentConfiguration.cs`
- Create: `src/Modules/Proxies/Modules.Proxies/Data/Configurations/ProxyUsageEventConfiguration.cs`
- Create: `src/Modules/Proxies/Modules.Proxies/Data/Configurations/ApiClientConfiguration.cs`
- Modify: `src/Modules/Proxies/Modules.Proxies/Data/ProxiesDbContext.cs`
- Test: `src/Tests/Proxies.Tests/Domain/ProxyTests.cs`
- Test: `src/Tests/Proxies.Tests/Domain/PolicyProfileTests.cs`

**Interfaces:**
- Produces: `ProxyProviderType { WebShare, Oxylabs, BrightData, Manual }` (Contracts — used by DTOs and adapters later); `ProxyStatus { Active, Disabled, Banned, Testing, Retired }`, `ProxyProtocol { Http, Https, Socks5 }`, `PolicyProfileType { Manual, AutoDisable, AutoDisableAndRenew }`, `UsageEventSource { SystemHealthCheck, ConsumerFeedback }`, `UsageEventOutcome { Success, Failure, Banned, Timeout }` (all runtime `Domain` enums); `ProviderAccount.Create(string name, ProxyProviderType providerType, string protectedCredentials)`; `Proxy.Create(Guid providerAccountId, string host, int port, ProxyProtocol protocol, string? username, string? protectedPassword, string? externalId)`; `PolicyProfile.RestrictivenessRank` (int, higher = more restrictive — consumed by the policy engine in Task 18).

This module's entities are simple enough that the highest-value unit tests are on the two with real behavior/invariants (`Proxy` status transitions, `PolicyProfile` restrictiveness ranking) rather than every trivial factory — the rest are exercised indirectly through the CRUD handler tests in later tasks.

- [ ] **Step 1: Create the `ProxyProviderType` enum in Contracts**

```csharp
// src/Modules/Proxies/Modules.Proxies.Contracts/ProxyProviderType.cs
namespace FSH.Modules.Proxies.Contracts;

public enum ProxyProviderType
{
    WebShare,
    Oxylabs,
    BrightData,
    Manual
}
```

- [ ] **Step 2: Write the failing domain tests**

```csharp
// src/Tests/Proxies.Tests/Domain/ProxyTests.cs
using FSH.Modules.Proxies.Domain;
using Shouldly;
using Xunit;

namespace Proxies.Tests.Domain;

public sealed class ProxyTests
{
    [Fact]
    public void Create_Should_DefaultTo_TestingStatus()
    {
        var proxy = Proxy.Create(Guid.NewGuid(), "1.2.3.4", 8080, ProxyProtocol.Http, "user", "protected-pw", "ext-1");

        proxy.Status.ShouldBe(ProxyStatus.Testing);
        proxy.Host.ShouldBe("1.2.3.4");
        proxy.Port.ShouldBe(8080);
    }

    [Fact]
    public void SetStatus_Should_UpdateStatus()
    {
        var proxy = Proxy.Create(Guid.NewGuid(), "1.2.3.4", 8080, ProxyProtocol.Http, null, null, null);

        proxy.SetStatus(ProxyStatus.Active);

        proxy.Status.ShouldBe(ProxyStatus.Active);
    }

    [Fact]
    public void MarkRenewed_Should_SetTestingStatus_And_Timestamp()
    {
        var proxy = Proxy.Create(Guid.NewGuid(), "1.2.3.4", 8080, ProxyProtocol.Http, null, null, null);
        proxy.SetStatus(ProxyStatus.Disabled);

        proxy.MarkRenewed();

        proxy.Status.ShouldBe(ProxyStatus.Testing);
        proxy.LastRenewedAtUtc.ShouldNotBeNull();
    }
}
```

```csharp
// src/Tests/Proxies.Tests/Domain/PolicyProfileTests.cs
using FSH.Modules.Proxies.Domain;
using Shouldly;
using Xunit;

namespace Proxies.Tests.Domain;

public sealed class PolicyProfileTests
{
    [Theory]
    [InlineData(PolicyProfileType.Manual, 0)]
    [InlineData(PolicyProfileType.AutoDisable, 1)]
    [InlineData(PolicyProfileType.AutoDisableAndRenew, 2)]
    public void RestrictivenessRank_Should_OrderByType(PolicyProfileType type, int expectedRank)
    {
        var profile = PolicyProfile.Create("test", type, failureThreshold: 3, windowMinutes: 30, minDistinctReporters: 2);

        profile.RestrictivenessRank.ShouldBe(expectedRank);
    }
}
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet test src/Tests/Proxies.Tests --filter "FullyQualifiedName~Domain"`
Expected: fails to compile — `Proxy`, `ProxyStatus`, `ProxyProtocol`, `PolicyProfile`, `PolicyProfileType` don't exist yet.

- [ ] **Step 4: Implement the domain enums and entities**

```csharp
// src/Modules/Proxies/Modules.Proxies/Domain/ProviderAccount.cs
using FSH.Framework.Core.Domain;
using FSH.Framework.Shared.Multitenancy;
using FSH.Modules.Proxies.Contracts;

namespace FSH.Modules.Proxies.Domain;

/// <see cref="IGlobalEntity"/>: this is an internal single-tenant ops tool — proxies are not
/// per-tenant data.
public sealed class ProviderAccount : AggregateRoot<Guid>, IGlobalEntity
{
    public string Name { get; private set; } = default!;
    public ProxyProviderType ProviderType { get; private set; }
    public string ProtectedCredentials { get; private set; } = default!;
    public bool IsEnabled { get; private set; }
    public DateTime? LastSyncedAtUtc { get; private set; }
    public string? LastSyncStatus { get; private set; }
    public int ConsecutiveSyncFailures { get; private set; }

    private ProviderAccount() { }

    public static ProviderAccount Create(string name, ProxyProviderType providerType, string protectedCredentials)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(protectedCredentials);
        return new ProviderAccount
        {
            Id = Guid.CreateVersion7(),
            Name = name.Trim(),
            ProviderType = providerType,
            ProtectedCredentials = protectedCredentials,
            IsEnabled = true
        };
    }

    public void UpdateCredentials(string protectedCredentials)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(protectedCredentials);
        ProtectedCredentials = protectedCredentials;
    }

    public void Rename(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name.Trim();
    }

    public void SetEnabled(bool enabled) => IsEnabled = enabled;

    public void RecordSyncResult(bool success, string? statusMessage)
    {
        LastSyncedAtUtc = DateTime.UtcNow;
        LastSyncStatus = statusMessage;
        ConsecutiveSyncFailures = success ? 0 : ConsecutiveSyncFailures + 1;
    }
}
```

```csharp
// src/Modules/Proxies/Modules.Proxies/Domain/Proxy.cs
using FSH.Framework.Core.Domain;
using FSH.Framework.Shared.Multitenancy;

namespace FSH.Modules.Proxies.Domain;

public enum ProxyStatus { Active, Disabled, Banned, Testing, Retired }
public enum ProxyProtocol { Http, Https, Socks5 }

public sealed class Proxy : AggregateRoot<Guid>, IGlobalEntity
{
    public Guid ProviderAccountId { get; private set; }
    public string Host { get; private set; } = default!;
    public int Port { get; private set; }
    public ProxyProtocol Protocol { get; private set; }
    public string? Username { get; private set; }
    public string? ProtectedPassword { get; private set; }
    public string? ExternalId { get; private set; }
    public ProxyStatus Status { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public DateTime? LastRenewedAtUtc { get; private set; }

    private readonly List<ProxyTagAssignment> _tagAssignments = [];
    public IReadOnlyCollection<ProxyTagAssignment> TagAssignments => _tagAssignments;

    private Proxy() { }

    public static Proxy Create(
        Guid providerAccountId, string host, int port, ProxyProtocol protocol,
        string? username, string? protectedPassword, string? externalId)
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
            Status = ProxyStatus.Testing,
            CreatedAtUtc = DateTime.UtcNow
        };
    }

    public void SetStatus(ProxyStatus status) => Status = status;

    public void MarkRenewed()
    {
        LastRenewedAtUtc = DateTime.UtcNow;
        Status = ProxyStatus.Testing;
    }

    public void AssignTag(Guid tagId)
    {
        if (_tagAssignments.Any(a => a.TagId == tagId)) return;
        _tagAssignments.Add(ProxyTagAssignment.Create(Id, tagId));
    }

    public void UnassignTag(Guid tagId) => _tagAssignments.RemoveAll(a => a.TagId == tagId);
}
```

```csharp
// src/Modules/Proxies/Modules.Proxies/Domain/ProxyTagAssignment.cs
using FSH.Framework.Shared.Multitenancy;

namespace FSH.Modules.Proxies.Domain;

public sealed class ProxyTagAssignment : IGlobalEntity
{
    public Guid ProxyId { get; private set; }
    public Guid TagId { get; private set; }

    private ProxyTagAssignment() { }

    public static ProxyTagAssignment Create(Guid proxyId, Guid tagId) => new() { ProxyId = proxyId, TagId = tagId };
}
```

```csharp
// src/Modules/Proxies/Modules.Proxies/Domain/Tag.cs
using FSH.Framework.Core.Domain;
using FSH.Framework.Shared.Multitenancy;

namespace FSH.Modules.Proxies.Domain;

public sealed class Tag : AggregateRoot<Guid>, IGlobalEntity
{
    public string Name { get; private set; } = default!;

    private Tag() { }

    public static Tag Create(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return new Tag { Id = Guid.CreateVersion7(), Name = Normalize(name) };
    }

    public static string Normalize(string name) => name.Trim().ToLowerInvariant();
}
```

```csharp
// src/Modules/Proxies/Modules.Proxies/Domain/PolicyProfile.cs
using FSH.Framework.Core.Domain;
using FSH.Framework.Shared.Multitenancy;

namespace FSH.Modules.Proxies.Domain;

public enum PolicyProfileType { Manual, AutoDisable, AutoDisableAndRenew }

public sealed class PolicyProfile : AggregateRoot<Guid>, IGlobalEntity
{
    public string Name { get; private set; } = default!;
    public PolicyProfileType Type { get; private set; }
    public int FailureThreshold { get; private set; }
    public int WindowMinutes { get; private set; }
    public int MinDistinctReporters { get; private set; }

    private PolicyProfile() { }

    public static PolicyProfile Create(
        string name, PolicyProfileType type, int failureThreshold, int windowMinutes, int minDistinctReporters)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return new PolicyProfile
        {
            Id = Guid.CreateVersion7(),
            Name = name.Trim(),
            Type = type,
            FailureThreshold = failureThreshold,
            WindowMinutes = windowMinutes,
            MinDistinctReporters = minDistinctReporters
        };
    }

    public void Update(string name, PolicyProfileType type, int failureThreshold, int windowMinutes, int minDistinctReporters)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name.Trim();
        Type = type;
        FailureThreshold = failureThreshold;
        WindowMinutes = windowMinutes;
        MinDistinctReporters = minDistinctReporters;
    }

    /// <summary>Higher rank wins when a proxy's tags resolve to more than one profile (spec conflict rule).</summary>
    public int RestrictivenessRank => Type switch
    {
        PolicyProfileType.AutoDisableAndRenew => 2,
        PolicyProfileType.AutoDisable => 1,
        _ => 0
    };
}
```

```csharp
// src/Modules/Proxies/Modules.Proxies/Domain/TagPolicyAssignment.cs
using FSH.Framework.Shared.Multitenancy;

namespace FSH.Modules.Proxies.Domain;

/// <summary>At most one policy profile per tag — enforced by a single-column PK on TagId.</summary>
public sealed class TagPolicyAssignment : IGlobalEntity
{
    public Guid TagId { get; private set; }
    public Guid PolicyProfileId { get; private set; }

    private TagPolicyAssignment() { }

    public static TagPolicyAssignment Create(Guid tagId, Guid policyProfileId) =>
        new() { TagId = tagId, PolicyProfileId = policyProfileId };
}
```

```csharp
// src/Modules/Proxies/Modules.Proxies/Domain/HealthCheckTarget.cs
using FSH.Framework.Core.Domain;
using FSH.Framework.Shared.Multitenancy;

namespace FSH.Modules.Proxies.Domain;

public sealed class HealthCheckTarget : AggregateRoot<Guid>, IGlobalEntity
{
    public string Name { get; private set; } = default!;
    public string TestUrl { get; private set; } = default!;
    public int? ExpectedStatusCode { get; private set; }
    public string? ExpectedBodyKeyword { get; private set; }
    public int TimeoutMs { get; private set; }

    private HealthCheckTarget() { }

    public static HealthCheckTarget Create(
        string name, string testUrl, int? expectedStatusCode, string? expectedBodyKeyword, int timeoutMs)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(testUrl);
        return new HealthCheckTarget
        {
            Id = Guid.CreateVersion7(),
            Name = name.Trim(),
            TestUrl = testUrl.Trim(),
            ExpectedStatusCode = expectedStatusCode,
            ExpectedBodyKeyword = expectedBodyKeyword,
            TimeoutMs = timeoutMs
        };
    }

    public void Update(string name, string testUrl, int? expectedStatusCode, string? expectedBodyKeyword, int timeoutMs)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(testUrl);
        Name = name.Trim();
        TestUrl = testUrl.Trim();
        ExpectedStatusCode = expectedStatusCode;
        ExpectedBodyKeyword = expectedBodyKeyword;
        TimeoutMs = timeoutMs;
    }
}
```

```csharp
// src/Modules/Proxies/Modules.Proxies/Domain/TagHealthCheckTargetAssignment.cs
using FSH.Framework.Shared.Multitenancy;

namespace FSH.Modules.Proxies.Domain;

/// <summary>At most one health-check target per tag — enforced by a single-column PK on TagId.</summary>
public sealed class TagHealthCheckTargetAssignment : IGlobalEntity
{
    public Guid TagId { get; private set; }
    public Guid HealthCheckTargetId { get; private set; }

    private TagHealthCheckTargetAssignment() { }

    public static TagHealthCheckTargetAssignment Create(Guid tagId, Guid healthCheckTargetId) =>
        new() { TagId = tagId, HealthCheckTargetId = healthCheckTargetId };
}
```

```csharp
// src/Modules/Proxies/Modules.Proxies/Domain/ProxyUsageEvent.cs
using FSH.Framework.Core.Domain;
using FSH.Framework.Shared.Multitenancy;

namespace FSH.Modules.Proxies.Domain;

public enum UsageEventSource { SystemHealthCheck, ConsumerFeedback }
public enum UsageEventOutcome { Success, Failure, Banned, Timeout }

public sealed class ProxyUsageEvent : BaseEntity<Guid>, IGlobalEntity
{
    public Guid ProxyId { get; private set; }
    public UsageEventSource Source { get; private set; }
    public UsageEventOutcome Outcome { get; private set; }
    public Guid? HealthCheckTargetId { get; private set; }
    public Guid? ReportedByApiClientId { get; private set; }
    public string? Detail { get; private set; }
    public DateTime OccurredAtUtc { get; private set; }

    private ProxyUsageEvent() { }

    public static ProxyUsageEvent Create(
        Guid proxyId, UsageEventSource source, UsageEventOutcome outcome,
        Guid? healthCheckTargetId, Guid? reportedByApiClientId, string? detail)
    {
        return new ProxyUsageEvent
        {
            Id = Guid.CreateVersion7(),
            ProxyId = proxyId,
            Source = source,
            Outcome = outcome,
            HealthCheckTargetId = healthCheckTargetId,
            ReportedByApiClientId = reportedByApiClientId,
            Detail = detail,
            OccurredAtUtc = DateTime.UtcNow
        };
    }
}
```

```csharp
// src/Modules/Proxies/Modules.Proxies/Domain/ApiClient.cs
using FSH.Framework.Core.Domain;
using FSH.Framework.Shared.Multitenancy;

namespace FSH.Modules.Proxies.Domain;

public sealed class ApiClient : AggregateRoot<Guid>, IGlobalEntity
{
    public string Name { get; private set; } = default!;
    public string ApiKeyHash { get; private set; } = default!;
    public bool IsEnabled { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public DateTime? LastUsedAtUtc { get; private set; }

    private ApiClient() { }

    public static ApiClient Create(string name, string apiKeyHash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKeyHash);
        return new ApiClient
        {
            Id = Guid.CreateVersion7(),
            Name = name.Trim(),
            ApiKeyHash = apiKeyHash,
            IsEnabled = true,
            CreatedAtUtc = DateTime.UtcNow
        };
    }

    public void SetEnabled(bool enabled) => IsEnabled = enabled;

    public void RecordUsage() => LastUsedAtUtc = DateTime.UtcNow;
}
```

- [ ] **Step 5: Run the domain tests again to verify they pass**

Run: `dotnet test src/Tests/Proxies.Tests --filter "FullyQualifiedName~Domain"`
Expected: PASS, 4 tests.

- [ ] **Step 6: Write the EF configurations**

```csharp
// src/Modules/Proxies/Modules.Proxies/Data/Configurations/ProviderAccountConfiguration.cs
using FSH.Modules.Proxies.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FSH.Modules.Proxies.Data.Configurations;

public sealed class ProviderAccountConfiguration : IEntityTypeConfiguration<ProviderAccount>
{
    public void Configure(EntityTypeBuilder<ProviderAccount> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.ToTable("ProviderAccounts");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Name).IsRequired().HasMaxLength(128);
        builder.Property(x => x.ProtectedCredentials).IsRequired();
        builder.Property(x => x.LastSyncStatus).HasMaxLength(1024);
        builder.HasIndex(x => x.ProviderType);
        builder.Ignore(x => x.DomainEvents);
    }
}
```

```csharp
// src/Modules/Proxies/Modules.Proxies/Data/Configurations/ProxyConfiguration.cs
using FSH.Modules.Proxies.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FSH.Modules.Proxies.Data.Configurations;

public sealed class ProxyConfiguration : IEntityTypeConfiguration<Proxy>
{
    public void Configure(EntityTypeBuilder<Proxy> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.ToTable("Proxies");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Host).IsRequired().HasMaxLength(255);
        builder.Property(x => x.Username).HasMaxLength(255);
        builder.Property(x => x.ProtectedPassword).HasMaxLength(1024);
        builder.Property(x => x.ExternalId).HasMaxLength(255);
        builder.HasIndex(x => new { x.ProviderAccountId, x.ExternalId });
        builder.HasIndex(x => x.Status);
        builder.HasOne<ProviderAccount>().WithMany().HasForeignKey(x => x.ProviderAccountId).OnDelete(DeleteBehavior.Restrict);
        builder.HasMany(x => x.TagAssignments).WithOne().HasForeignKey(x => x.ProxyId).OnDelete(DeleteBehavior.Cascade);
        builder.Metadata.FindNavigation(nameof(Proxy.TagAssignments))!.SetPropertyAccessMode(Microsoft.EntityFrameworkCore.ChangeTracking.PropertyAccessMode.Field);
        builder.Ignore(x => x.DomainEvents);
    }
}
```

```csharp
// src/Modules/Proxies/Modules.Proxies/Data/Configurations/ProxyTagAssignmentConfiguration.cs
using FSH.Modules.Proxies.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FSH.Modules.Proxies.Data.Configurations;

public sealed class ProxyTagAssignmentConfiguration : IEntityTypeConfiguration<ProxyTagAssignment>
{
    public void Configure(EntityTypeBuilder<ProxyTagAssignment> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.ToTable("ProxyTagAssignments");
        builder.HasKey(x => new { x.ProxyId, x.TagId });
        builder.HasOne<Tag>().WithMany().HasForeignKey(x => x.TagId).OnDelete(DeleteBehavior.Cascade);
    }
}
```

```csharp
// src/Modules/Proxies/Modules.Proxies/Data/Configurations/TagConfiguration.cs
using FSH.Modules.Proxies.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FSH.Modules.Proxies.Data.Configurations;

public sealed class TagConfiguration : IEntityTypeConfiguration<Tag>
{
    public void Configure(EntityTypeBuilder<Tag> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.ToTable("Tags");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Name).IsRequired().HasMaxLength(128);
        builder.HasIndex(x => x.Name).IsUnique();
        builder.Ignore(x => x.DomainEvents);
    }
}
```

```csharp
// src/Modules/Proxies/Modules.Proxies/Data/Configurations/PolicyProfileConfiguration.cs
using FSH.Modules.Proxies.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FSH.Modules.Proxies.Data.Configurations;

public sealed class PolicyProfileConfiguration : IEntityTypeConfiguration<PolicyProfile>
{
    public void Configure(EntityTypeBuilder<PolicyProfile> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.ToTable("PolicyProfiles");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Name).IsRequired().HasMaxLength(128);
        builder.Ignore(x => x.DomainEvents);
    }
}
```

```csharp
// src/Modules/Proxies/Modules.Proxies/Data/Configurations/TagPolicyAssignmentConfiguration.cs
using FSH.Modules.Proxies.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FSH.Modules.Proxies.Data.Configurations;

public sealed class TagPolicyAssignmentConfiguration : IEntityTypeConfiguration<TagPolicyAssignment>
{
    public void Configure(EntityTypeBuilder<TagPolicyAssignment> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.ToTable("TagPolicyAssignments");
        builder.HasKey(x => x.TagId);
        builder.HasOne<Tag>().WithMany().HasForeignKey(x => x.TagId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<PolicyProfile>().WithMany().HasForeignKey(x => x.PolicyProfileId).OnDelete(DeleteBehavior.Restrict);
    }
}
```

```csharp
// src/Modules/Proxies/Modules.Proxies/Data/Configurations/HealthCheckTargetConfiguration.cs
using FSH.Modules.Proxies.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FSH.Modules.Proxies.Data.Configurations;

public sealed class HealthCheckTargetConfiguration : IEntityTypeConfiguration<HealthCheckTarget>
{
    public void Configure(EntityTypeBuilder<HealthCheckTarget> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.ToTable("HealthCheckTargets");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Name).IsRequired().HasMaxLength(128);
        builder.Property(x => x.TestUrl).IsRequired().HasMaxLength(2048);
        builder.Property(x => x.ExpectedBodyKeyword).HasMaxLength(256);
        builder.Ignore(x => x.DomainEvents);
    }
}
```

```csharp
// src/Modules/Proxies/Modules.Proxies/Data/Configurations/TagHealthCheckTargetAssignmentConfiguration.cs
using FSH.Modules.Proxies.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FSH.Modules.Proxies.Data.Configurations;

public sealed class TagHealthCheckTargetAssignmentConfiguration : IEntityTypeConfiguration<TagHealthCheckTargetAssignment>
{
    public void Configure(EntityTypeBuilder<TagHealthCheckTargetAssignment> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.ToTable("TagHealthCheckTargetAssignments");
        builder.HasKey(x => x.TagId);
        builder.HasOne<Tag>().WithMany().HasForeignKey(x => x.TagId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<HealthCheckTarget>().WithMany().HasForeignKey(x => x.HealthCheckTargetId).OnDelete(DeleteBehavior.Restrict);
    }
}
```

```csharp
// src/Modules/Proxies/Modules.Proxies/Data/Configurations/ProxyUsageEventConfiguration.cs
using FSH.Modules.Proxies.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FSH.Modules.Proxies.Data.Configurations;

public sealed class ProxyUsageEventConfiguration : IEntityTypeConfiguration<ProxyUsageEvent>
{
    public void Configure(EntityTypeBuilder<ProxyUsageEvent> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.ToTable("ProxyUsageEvents");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Detail).HasMaxLength(2048);
        // Read pattern is always "events for proxy X in the last N minutes" (policy engine, Task 18).
        builder.HasIndex(x => new { x.ProxyId, x.OccurredAtUtc });
        builder.HasOne<Proxy>().WithMany().HasForeignKey(x => x.ProxyId).OnDelete(DeleteBehavior.Cascade);
    }
}
```

```csharp
// src/Modules/Proxies/Modules.Proxies/Data/Configurations/ApiClientConfiguration.cs
using FSH.Modules.Proxies.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FSH.Modules.Proxies.Data.Configurations;

public sealed class ApiClientConfiguration : IEntityTypeConfiguration<ApiClient>
{
    public void Configure(EntityTypeBuilder<ApiClient> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.ToTable("ApiClients");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Name).IsRequired().HasMaxLength(128);
        builder.Property(x => x.ApiKeyHash).IsRequired().HasMaxLength(512);
        builder.HasIndex(x => x.ApiKeyHash).IsUnique();
        builder.Ignore(x => x.DomainEvents);
    }
}
```

- [ ] **Step 7: Add the `DbSet`s to `ProxiesDbContext`**

```csharp
// add inside ProxiesDbContext, above OnModelCreating
public DbSet<ProviderAccount> ProviderAccounts => Set<ProviderAccount>();
public DbSet<Proxy> Proxies => Set<Proxy>();
public DbSet<Tag> Tags => Set<Tag>();
public DbSet<PolicyProfile> PolicyProfiles => Set<PolicyProfile>();
public DbSet<HealthCheckTarget> HealthCheckTargets => Set<HealthCheckTarget>();
public DbSet<ProxyUsageEvent> ProxyUsageEvents => Set<ProxyUsageEvent>();
public DbSet<ApiClient> ApiClients => Set<ApiClient>();
```

(add `using FSH.Modules.Proxies.Domain;` to the file's usings)

- [ ] **Step 8: Build and run the full Proxies.Tests project**

Run: `dotnet build src/FS.Proxy.slnx && dotnet test src/Tests/Proxies.Tests`
Expected: build succeeds, all tests pass.

- [ ] **Step 9: Commit**

```bash
git add src/Modules/Proxies src/Tests/Proxies.Tests
git commit -m "feat(proxies): add domain entities and EF configurations"
```

### Task 3: Initial EF migration

**Files:**
- Create: `src/Host/FS.Proxy.Migrations.PostgreSQL/Proxies/{Timestamp}_InitialProxies.cs` (+ `.Designer.cs`)
- Create: `src/Host/FS.Proxy.Migrations.PostgreSQL/Proxies/ProxiesDbContextModelSnapshot.cs`

**Interfaces:**
- Consumes: `ProxiesDbContext` from Task 2 (must already build cleanly).
- Produces: the `proxies` schema in Postgres, ready for `DbMigrator apply`.

No TDD here — an EF migration is generated, not hand-written; the "test" is applying it successfully.

- [ ] **Step 1: Build the solution first (stale-snapshot footgun)**

Run: `dotnet build src/FS.Proxy.slnx`
Expected: succeeds.

- [ ] **Step 2: Generate the migration**

Run:
```bash
dotnet ef migrations add InitialProxies \
  --project src/Host/FS.Proxy.Migrations.PostgreSQL \
  --startup-project src/Host/FS.Proxy.Api \
  --context ProxiesDbContext \
  --output-dir Proxies
```
Expected: creates `{Timestamp}_InitialProxies.cs`, `.Designer.cs`, and `ProxiesDbContextModelSnapshot.cs` under `src/Host/FS.Proxy.Migrations.PostgreSQL/Proxies/`.

- [ ] **Step 3: Review the generated SQL**

Run:
```bash
dotnet ef migrations script --idempotent \
  --project src/Host/FS.Proxy.Migrations.PostgreSQL \
  --startup-project src/Host/FS.Proxy.Api \
  --context ProxiesDbContext
```
Expected: a `CREATE SCHEMA "proxies"` plus `CREATE TABLE` statements for all 9 tables from Task 2, with the indexes/FKs as configured. Confirm there is no accidental Finbuckle `TenantId` column on any table (would indicate an `IGlobalEntity` misconfiguration).

- [ ] **Step 4: Apply the migration against the local dev database**

Run: `dotnet run --project src/Host/FS.Proxy.DbMigrator -- apply`
Expected: exits 0, logs `Applied 1 pending Proxies migrations.`

- [ ] **Step 5: Verify with `list-pending`**

Run: `dotnet run --project src/Host/FS.Proxy.DbMigrator -- list-pending`
Expected: Proxies shows no pending migrations.

- [ ] **Step 6: Commit**

```bash
git add src/Host/FS.Proxy.Migrations.PostgreSQL/Proxies
git commit -m "feat(proxies): add initial EF migration for the proxies schema"
```

### Task 4: Permissions and Architecture.Tests wiring

**Files:**
- Create: `src/Modules/Proxies/Modules.Proxies.Contracts/Authorization/ProxiesPermissions.cs`
- Modify: `src/Modules/Proxies/Modules.Proxies/ProxiesModule.cs`
- Modify: `src/Tests/Architecture.Tests/Architecture.Tests.csproj`
- Modify: `src/Tests/Architecture.Tests/ContractsPurityTests.cs`
- Modify: `src/Tests/Architecture.Tests/HostArchitectureTests.cs`
- Modify: `src/Tests/Architecture.Tests/EndpointConventionTests.cs`

**Interfaces:**
- Produces: `ProxiesPermissions.ProviderAccounts.{View,Create,Update,Delete}`, `.ManualProxies.{View,Create,Update,Delete}`, `.Tags.{View,Create,Update,Delete}`, `.Policies.{View,Create,Update,Delete}`, `.HealthCheckTargets.{View,Create,Update,Delete}`, `.ApiClients.{View,Create,Delete}` — all `Permissions.Proxies.{Resource}.{Action}` strings, consumed by every admin endpoint task from Task 7 onward.

This task has no application code to unit-test — its correctness is proven by `Architecture.Tests` actually running against the new module afterward, so the verification step doubles as the test.

- [ ] **Step 1: Define the permission catalogue**

```csharp
// src/Modules/Proxies/Modules.Proxies.Contracts/Authorization/ProxiesPermissions.cs
using FSH.Framework.Shared.Constants;

namespace FSH.Modules.Proxies.Contracts.Authorization;

public static class ProxiesPermissions
{
    public static class ProviderAccounts
    {
        public const string Resource = "Proxies.ProviderAccounts";
        public const string View = $"Permissions.{Resource}.View";
        public const string Create = $"Permissions.{Resource}.Create";
        public const string Update = $"Permissions.{Resource}.Update";
        public const string Delete = $"Permissions.{Resource}.Delete";
    }

    public static class ManualProxies
    {
        public const string Resource = "Proxies.ManualProxies";
        public const string View = $"Permissions.{Resource}.View";
        public const string Create = $"Permissions.{Resource}.Create";
        public const string Update = $"Permissions.{Resource}.Update";
        public const string Delete = $"Permissions.{Resource}.Delete";
    }

    public static class Tags
    {
        public const string Resource = "Proxies.Tags";
        public const string View = $"Permissions.{Resource}.View";
        public const string Create = $"Permissions.{Resource}.Create";
        public const string Update = $"Permissions.{Resource}.Update";
        public const string Delete = $"Permissions.{Resource}.Delete";
    }

    public static class Policies
    {
        public const string Resource = "Proxies.Policies";
        public const string View = $"Permissions.{Resource}.View";
        public const string Create = $"Permissions.{Resource}.Create";
        public const string Update = $"Permissions.{Resource}.Update";
        public const string Delete = $"Permissions.{Resource}.Delete";
    }

    public static class HealthCheckTargets
    {
        public const string Resource = "Proxies.HealthCheckTargets";
        public const string View = $"Permissions.{Resource}.View";
        public const string Create = $"Permissions.{Resource}.Create";
        public const string Update = $"Permissions.{Resource}.Update";
        public const string Delete = $"Permissions.{Resource}.Delete";
    }

    public static class ApiClients
    {
        public const string Resource = "Proxies.ApiClients";
        public const string View = $"Permissions.{Resource}.View";
        public const string Create = $"Permissions.{Resource}.Create";
        public const string Delete = $"Permissions.{Resource}.Delete";
    }

    public static IReadOnlyList<FshPermission> All { get; } =
    [
        new("View Provider Accounts", ActionConstants.View, ProviderAccounts.Resource, IsBasic: true),
        new("Create Provider Accounts", ActionConstants.Create, ProviderAccounts.Resource),
        new("Update Provider Accounts", ActionConstants.Update, ProviderAccounts.Resource),
        new("Delete Provider Accounts", ActionConstants.Delete, ProviderAccounts.Resource),

        new("View Manual Proxies", ActionConstants.View, ManualProxies.Resource, IsBasic: true),
        new("Create Manual Proxies", ActionConstants.Create, ManualProxies.Resource),
        new("Update Manual Proxies", ActionConstants.Update, ManualProxies.Resource),
        new("Delete Manual Proxies", ActionConstants.Delete, ManualProxies.Resource),

        new("View Tags", ActionConstants.View, Tags.Resource, IsBasic: true),
        new("Create Tags", ActionConstants.Create, Tags.Resource),
        new("Update Tags", ActionConstants.Update, Tags.Resource),
        new("Delete Tags", ActionConstants.Delete, Tags.Resource),

        new("View Policies", ActionConstants.View, Policies.Resource, IsBasic: true),
        new("Create Policies", ActionConstants.Create, Policies.Resource),
        new("Update Policies", ActionConstants.Update, Policies.Resource),
        new("Delete Policies", ActionConstants.Delete, Policies.Resource),

        new("View Health Check Targets", ActionConstants.View, HealthCheckTargets.Resource, IsBasic: true),
        new("Create Health Check Targets", ActionConstants.Create, HealthCheckTargets.Resource),
        new("Update Health Check Targets", ActionConstants.Update, HealthCheckTargets.Resource),
        new("Delete Health Check Targets", ActionConstants.Delete, HealthCheckTargets.Resource),

        new("View Api Clients", ActionConstants.View, ApiClients.Resource, IsBasic: true),
        new("Create Api Clients", ActionConstants.Create, ApiClients.Resource),
        new("Delete Api Clients", ActionConstants.Delete, ApiClients.Resource),
    ];
}
```

- [ ] **Step 2: Register the permissions in `ProxiesModule.ConfigureServices`**

```csharp
// add as the first line inside ConfigureServices, mirroring CatalogModule
PermissionConstants.Register(FSH.Modules.Proxies.Contracts.Authorization.ProxiesPermissions.All);
```

- [ ] **Step 3: Add the Proxies project references to `Architecture.Tests.csproj`**

```xml
<!-- add alongside the existing module ProjectReferences -->
<ProjectReference Include="..\..\Modules\Proxies\Modules.Proxies\Modules.Proxies.csproj" />
<ProjectReference Include="..\..\Modules\Proxies\Modules.Proxies.Contracts\Modules.Proxies.Contracts.csproj" />
```

- [ ] **Step 4: Add `Proxies.Contracts` to the `ContractsPurityTests` allow-list**

Open `src/Tests/Architecture.Tests/ContractsPurityTests.cs`, find the hardcoded list of Contracts assemblies (currently `Auditing`, `Chat`, `Identity`, `Multitenancy`), and add `FSH.Modules.Proxies.Contracts.ProxiesContractsMarker` to it so `Modules.Proxies.Contracts` gets checked for EF/FluentValidation/Hangfire/implementation-namespace leaks.

- [ ] **Step 5: Check `HostArchitectureTests.cs`'s forbidden-namespace list**

Open `src/Tests/Architecture.Tests/HostArchitectureTests.cs` and confirm its namespace list is pattern-based (e.g. `FSH.Modules.*.Features`, `FSH.Modules.*.Data`) rather than a hardcoded per-module list. If it is pattern-based, no edit is needed — `Proxies` is covered automatically. If it hardcodes module names, add `FSH.Modules.Proxies` alongside the others.

- [ ] **Step 6: Extend the endpoint verb allow-list in `EndpointConventionTests.cs`**

Run: `grep -n '"Get"\|"Create"\|"Update"\|"Delete"' src/Tests/Architecture.Tests/EndpointConventionTests.cs`

This locates the verb allow-list array. This module's endpoints (built across Tasks 7–24) introduce five verbs not currently in the list: `Enable`, `Disable`, `Sync`, `Renew`, `Report`, `Request`. Add all six to the array (`Enable*`, `Disable*`, `Sync*`, `Renew*`, `Report*`, `Request*` — matching whatever casing/wildcard convention the existing entries use).

- [ ] **Step 7: Build and run Architecture.Tests**

Run: `dotnet build src/FS.Proxy.slnx && dotnet test src/Tests/Architecture.Tests`
Expected: all pass. `Modules.Proxies`/`Modules.Proxies.Contracts` are now covered by `ModuleArchitectureTests`, `LayerDependencyTests`, `TenantIsolationTests`, `DomainEntityTests`, `ContractsPurityTests`, `CircularReferenceTests`. `HandlerValidatorPairingTests` and `EndpointConventionTests` will start applying once handlers/endpoints exist from Task 7 onward — expect them to be no-ops for now (no handlers/endpoints yet).

- [ ] **Step 8: Commit**

```bash
git add src/Modules/Proxies src/Tests/Architecture.Tests
git commit -m "feat(proxies): define permissions and wire the module into Architecture.Tests"
```

---

## Milestone B — Credential Protection and Provider Adapter Abstraction

### Task 5: Credential protectors and API key hashing

**Files:**
- Create: `src/Modules/Proxies/Modules.Proxies/Services/IProxySecretProtector.cs`
- Create: `src/Modules/Proxies/Modules.Proxies/Services/ProviderAccountCredentialProtector.cs`
- Create: `src/Modules/Proxies/Modules.Proxies/Services/ProxyPasswordProtector.cs`
- Create: `src/Modules/Proxies/Modules.Proxies/Services/IApiKeyHasher.cs`
- Create: `src/Modules/Proxies/Modules.Proxies/Services/ApiKeyHasher.cs`
- Modify: `src/Modules/Proxies/Modules.Proxies/ProxiesModule.cs`
- Test: `src/Tests/Proxies.Tests/Services/ApiKeyHasherTests.cs`

**Interfaces:**
- Produces: `IProxySecretProtector.Protect(string)`/`.Unprotect(string)`, registered as two distinct concrete singletons (`ProviderAccountCredentialProtector`, `ProxyPasswordProtector`) — each task that persists a provider credential or a proxy password injects the specific concrete type it needs, never the shared interface by itself (avoids DI ambiguity). `IApiKeyHasher.Hash(string plaintextKey) : string` and `.GenerateKey() : (string PlaintextKey, string Hash)` — consumed by Task 21 (`ApiClient` admin endpoints) and Task 22 (the API-key auth handler).

This mirrors `IWebhookSecretProtector`/`WebhookSecretProtector` exactly (Task research, §7), with a distinct Data-Protection purpose string per secret category as that pattern's own doc comment recommends.

- [ ] **Step 1: Write the failing hasher test**

```csharp
// src/Tests/Proxies.Tests/Services/ApiKeyHasherTests.cs
using FSH.Modules.Proxies.Services;
using Shouldly;
using Xunit;

namespace Proxies.Tests.Services;

public sealed class ApiKeyHasherTests
{
    private readonly ApiKeyHasher _sut = new();

    [Fact]
    public void GenerateKey_Should_ProduceKeyAndMatchingHash()
    {
        var (plaintextKey, hash) = _sut.GenerateKey();

        plaintextKey.ShouldNotBeNullOrWhiteSpace();
        hash.ShouldNotBeNullOrWhiteSpace();
        _sut.Hash(plaintextKey).ShouldBe(hash);
    }

    [Fact]
    public void Hash_Should_BeDeterministic()
    {
        const string key = "test-key-value";

        _sut.Hash(key).ShouldBe(_sut.Hash(key));
    }

    [Fact]
    public void GenerateKey_Should_ProduceUniqueKeysAcrossCalls()
    {
        var (first, _) = _sut.GenerateKey();
        var (second, _) = _sut.GenerateKey();

        first.ShouldNotBe(second);
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test src/Tests/Proxies.Tests --filter "FullyQualifiedName~ApiKeyHasherTests"`
Expected: fails to compile — `ApiKeyHasher` doesn't exist yet.

- [ ] **Step 3: Implement the API key hasher**

```csharp
// src/Modules/Proxies/Modules.Proxies/Services/IApiKeyHasher.cs
namespace FSH.Modules.Proxies.Services;

public interface IApiKeyHasher
{
    string Hash(string plaintextKey);
    (string PlaintextKey, string Hash) GenerateKey();
}
```

```csharp
// src/Modules/Proxies/Modules.Proxies/Services/ApiKeyHasher.cs
using System.Security.Cryptography;
using System.Text;

namespace FSH.Modules.Proxies.Services;

public sealed class ApiKeyHasher : IApiKeyHasher
{
    private const int KeyBytesLength = 32;

    public string Hash(string plaintextKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(plaintextKey);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(plaintextKey));
        return Convert.ToHexStringLower(bytes);
    }

    public (string PlaintextKey, string Hash) GenerateKey()
    {
        var randomBytes = RandomNumberGenerator.GetBytes(KeyBytesLength);
        var plaintextKey = $"fsh_proxies_{Convert.ToHexStringLower(randomBytes)}";
        return (plaintextKey, Hash(plaintextKey));
    }
}
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test src/Tests/Proxies.Tests --filter "FullyQualifiedName~ApiKeyHasherTests"`
Expected: PASS, 3 tests.

- [ ] **Step 5: Implement the credential protectors (mirrors `WebhookSecretProtector`, no separate test — it's a two-line wrapper over `IDataProtectionProvider`, already exercised indirectly by the handler tests in Tasks 7–8)**

```csharp
// src/Modules/Proxies/Modules.Proxies/Services/IProxySecretProtector.cs
namespace FSH.Modules.Proxies.Services;

public interface IProxySecretProtector
{
    string Protect(string plaintext);
    string Unprotect(string ciphertext);
}
```

```csharp
// src/Modules/Proxies/Modules.Proxies/Services/ProviderAccountCredentialProtector.cs
using Microsoft.AspNetCore.DataProtection;

namespace FSH.Modules.Proxies.Services;

/// <summary>
/// Encrypts/decrypts ProviderAccount API credentials at rest. Distinct purpose string from
/// ProxyPasswordProtector and from Webhooks' own protector — Data Protection purpose strings
/// are how different secret categories stay cryptographically isolated from each other.
/// </summary>
public sealed class ProviderAccountCredentialProtector : IProxySecretProtector
{
    private readonly IDataProtector _protector;

    public ProviderAccountCredentialProtector(IDataProtectionProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        _protector = provider.CreateProtector("FSH.Proxies.ProviderCredential.v1");
    }

    public string Protect(string plaintext) => _protector.Protect(plaintext);

    public string Unprotect(string ciphertext) => _protector.Unprotect(ciphertext);
}
```

```csharp
// src/Modules/Proxies/Modules.Proxies/Services/ProxyPasswordProtector.cs
using Microsoft.AspNetCore.DataProtection;

namespace FSH.Modules.Proxies.Services;

public sealed class ProxyPasswordProtector : IProxySecretProtector
{
    private readonly IDataProtector _protector;

    public ProxyPasswordProtector(IDataProtectionProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        _protector = provider.CreateProtector("FSH.Proxies.ProxyPassword.v1");
    }

    public string Protect(string plaintext) => _protector.Protect(plaintext);

    public string Unprotect(string ciphertext) => _protector.Unprotect(ciphertext);
}
```

- [ ] **Step 6: Register all three services in `ProxiesModule.ConfigureServices`**

```csharp
builder.Services.AddSingleton<ProviderAccountCredentialProtector>();
builder.Services.AddSingleton<ProxyPasswordProtector>();
builder.Services.AddSingleton<IApiKeyHasher, ApiKeyHasher>();
```

- [ ] **Step 7: Build and run**

Run: `dotnet build src/FS.Proxy.slnx && dotnet test src/Tests/Proxies.Tests`
Expected: PASS.

- [ ] **Step 8: Commit**

```bash
git add src/Modules/Proxies src/Tests/Proxies.Tests
git commit -m "feat(proxies): add credential protectors and API key hasher"
```

### Task 6: Provider adapter abstraction, factory, and the Manual adapter

**Files:**
- Create: `src/Modules/Proxies/Modules.Proxies/Providers/ProviderProxyRecord.cs`
- Create: `src/Modules/Proxies/Modules.Proxies/Providers/ProviderSyncResult.cs`
- Create: `src/Modules/Proxies/Modules.Proxies/Providers/ProviderRenewResult.cs`
- Create: `src/Modules/Proxies/Modules.Proxies/Providers/IProxyProviderAdapter.cs`
- Create: `src/Modules/Proxies/Modules.Proxies/Providers/IProxyProviderAdapterFactory.cs`
- Create: `src/Modules/Proxies/Modules.Proxies/Providers/ProxyProviderAdapterFactory.cs`
- Create: `src/Modules/Proxies/Modules.Proxies/Providers/ManualAdapter.cs`
- Modify: `src/Modules/Proxies/Modules.Proxies/ProxiesModule.cs`
- Test: `src/Tests/Proxies.Tests/Providers/ProxyProviderAdapterFactoryTests.cs`
- Test: `src/Tests/Proxies.Tests/Providers/ManualAdapterTests.cs`

**Interfaces:**
- Produces:
  ```csharp
  public interface IProxyProviderAdapter
  {
      ProxyProviderType ProviderType { get; }
      bool SupportsSync { get; }
      bool SupportsRenew { get; }
      Task<ProviderSyncResult> SyncProxiesAsync(ProviderAccount account, string decryptedCredentials, CancellationToken cancellationToken);
      Task<ProviderRenewResult> RenewProxyAsync(ProviderAccount account, string decryptedCredentials, Proxy proxy, CancellationToken cancellationToken);
  }
  public interface IProxyProviderAdapterFactory { IProxyProviderAdapter GetAdapter(ProxyProviderType providerType); }
  public sealed record ProviderProxyRecord(string ExternalId, string Host, int Port, ProxyProtocol Protocol, string? Username, string? Password, bool IsActive);
  public sealed record ProviderSyncResult(IReadOnlyList<ProviderProxyRecord> Proxies, bool Success, string? ErrorMessage);
  public sealed record ProviderRenewResult(bool Success, string? ErrorMessage, ProviderProxyRecord? UpdatedProxy);
  ```
  Consumed by Tasks 9 (sync job), 13–15 (WebShare/Oxylabs/BrightData adapters), 19 (renewal orchestration).

- [ ] **Step 1: Write the failing factory test**

```csharp
// src/Tests/Proxies.Tests/Providers/ProxyProviderAdapterFactoryTests.cs
using FSH.Modules.Proxies.Contracts;
using FSH.Modules.Proxies.Domain;
using FSH.Modules.Proxies.Providers;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Proxies.Tests.Providers;

public sealed class ProxyProviderAdapterFactoryTests
{
    [Fact]
    public void GetAdapter_Should_ReturnMatchingAdapter()
    {
        var manualAdapter = Substitute.For<IProxyProviderAdapter>();
        manualAdapter.ProviderType.Returns(ProxyProviderType.Manual);
        var webShareAdapter = Substitute.For<IProxyProviderAdapter>();
        webShareAdapter.ProviderType.Returns(ProxyProviderType.WebShare);
        var sut = new ProxyProviderAdapterFactory([manualAdapter, webShareAdapter]);

        var result = sut.GetAdapter(ProxyProviderType.WebShare);

        result.ShouldBeSameAs(webShareAdapter);
    }

    [Fact]
    public void GetAdapter_Should_Throw_When_NoAdapterRegistered()
    {
        var sut = new ProxyProviderAdapterFactory([]);

        Should.Throw<FSH.Framework.Core.Exceptions.NotFoundException>(() => sut.GetAdapter(ProxyProviderType.Oxylabs));
    }
}
```

```csharp
// src/Tests/Proxies.Tests/Providers/ManualAdapterTests.cs
using FSH.Modules.Proxies.Contracts;
using FSH.Modules.Proxies.Domain;
using FSH.Modules.Proxies.Providers;
using Shouldly;
using Xunit;

namespace Proxies.Tests.Providers;

public sealed class ManualAdapterTests
{
    private readonly ManualAdapter _sut = new();

    [Fact]
    public void ProviderType_Should_BeManual() => _sut.ProviderType.ShouldBe(ProxyProviderType.Manual);

    [Fact]
    public void SupportsSync_And_SupportsRenew_Should_BeFalse()
    {
        _sut.SupportsSync.ShouldBeFalse();
        _sut.SupportsRenew.ShouldBeFalse();
    }

    [Fact]
    public async Task SyncProxiesAsync_Should_ReturnEmptySuccess()
    {
        var account = ProviderAccount.Create("manual", ProxyProviderType.Manual, "n/a");

        var result = await _sut.SyncProxiesAsync(account, "n/a", CancellationToken.None);

        result.Success.ShouldBeTrue();
        result.Proxies.ShouldBeEmpty();
    }

    [Fact]
    public async Task RenewProxyAsync_Should_ReturnUnsuccessful()
    {
        var account = ProviderAccount.Create("manual", ProxyProviderType.Manual, "n/a");
        var proxy = Proxy.Create(account.Id, "1.2.3.4", 8080, ProxyProtocol.Http, null, null, null);

        var result = await _sut.RenewProxyAsync(account, "n/a", proxy, CancellationToken.None);

        result.Success.ShouldBeFalse();
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test src/Tests/Proxies.Tests --filter "FullyQualifiedName~Providers"`
Expected: fails to compile — none of these types exist yet.

- [ ] **Step 3: Implement the DTOs and interfaces**

```csharp
// src/Modules/Proxies/Modules.Proxies/Providers/ProviderProxyRecord.cs
using FSH.Modules.Proxies.Domain;

namespace FSH.Modules.Proxies.Providers;

public sealed record ProviderProxyRecord(
    string ExternalId, string Host, int Port, ProxyProtocol Protocol,
    string? Username, string? Password, bool IsActive);
```

```csharp
// src/Modules/Proxies/Modules.Proxies/Providers/ProviderSyncResult.cs
namespace FSH.Modules.Proxies.Providers;

public sealed record ProviderSyncResult(IReadOnlyList<ProviderProxyRecord> Proxies, bool Success, string? ErrorMessage)
{
    public static ProviderSyncResult Ok(IReadOnlyList<ProviderProxyRecord> proxies) => new(proxies, true, null);
    public static ProviderSyncResult Failed(string errorMessage) => new([], false, errorMessage);
}
```

```csharp
// src/Modules/Proxies/Modules.Proxies/Providers/ProviderRenewResult.cs
namespace FSH.Modules.Proxies.Providers;

public sealed record ProviderRenewResult(bool Success, string? ErrorMessage, ProviderProxyRecord? UpdatedProxy)
{
    public static ProviderRenewResult Ok(ProviderProxyRecord updatedProxy) => new(true, null, updatedProxy);
    public static ProviderRenewResult Unsupported() => new(false, "Renewal is not supported by this provider.", null);
    public static ProviderRenewResult Failed(string errorMessage) => new(false, errorMessage, null);
}
```

```csharp
// src/Modules/Proxies/Modules.Proxies/Providers/IProxyProviderAdapter.cs
using FSH.Modules.Proxies.Contracts;
using FSH.Modules.Proxies.Domain;

namespace FSH.Modules.Proxies.Providers;

public interface IProxyProviderAdapter
{
    ProxyProviderType ProviderType { get; }
    bool SupportsSync { get; }
    bool SupportsRenew { get; }

    Task<ProviderSyncResult> SyncProxiesAsync(ProviderAccount account, string decryptedCredentials, CancellationToken cancellationToken);

    Task<ProviderRenewResult> RenewProxyAsync(ProviderAccount account, string decryptedCredentials, Proxy proxy, CancellationToken cancellationToken);
}
```

```csharp
// src/Modules/Proxies/Modules.Proxies/Providers/IProxyProviderAdapterFactory.cs
using FSH.Modules.Proxies.Contracts;

namespace FSH.Modules.Proxies.Providers;

public interface IProxyProviderAdapterFactory
{
    IProxyProviderAdapter GetAdapter(ProxyProviderType providerType);
}
```

```csharp
// src/Modules/Proxies/Modules.Proxies/Providers/ProxyProviderAdapterFactory.cs
using FSH.Framework.Core.Exceptions;
using FSH.Modules.Proxies.Contracts;

namespace FSH.Modules.Proxies.Providers;

public sealed class ProxyProviderAdapterFactory(IEnumerable<IProxyProviderAdapter> adapters) : IProxyProviderAdapterFactory
{
    public IProxyProviderAdapter GetAdapter(ProxyProviderType providerType) =>
        adapters.FirstOrDefault(a => a.ProviderType == providerType)
        ?? throw new NotFoundException($"No provider adapter registered for '{providerType}'.");
}
```

```csharp
// src/Modules/Proxies/Modules.Proxies/Providers/ManualAdapter.cs
using FSH.Modules.Proxies.Contracts;
using FSH.Modules.Proxies.Domain;

namespace FSH.Modules.Proxies.Providers;

/// <summary>
/// Self-hosted proxies have no provider API — sync is a no-op (rows are managed directly
/// through the Manual Proxy admin CRUD, Task 8) and renewal always reports unsupported so
/// the caller falls back to the admin-notification flow (Task 19).
/// </summary>
public sealed class ManualAdapter : IProxyProviderAdapter
{
    public ProxyProviderType ProviderType => ProxyProviderType.Manual;
    public bool SupportsSync => false;
    public bool SupportsRenew => false;

    public Task<ProviderSyncResult> SyncProxiesAsync(ProviderAccount account, string decryptedCredentials, CancellationToken cancellationToken) =>
        Task.FromResult(ProviderSyncResult.Ok([]));

    public Task<ProviderRenewResult> RenewProxyAsync(ProviderAccount account, string decryptedCredentials, Proxy proxy, CancellationToken cancellationToken) =>
        Task.FromResult(ProviderRenewResult.Unsupported());
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test src/Tests/Proxies.Tests --filter "FullyQualifiedName~Providers"`
Expected: PASS, 6 tests.

- [ ] **Step 5: Register in `ProxiesModule.ConfigureServices`**

```csharp
builder.Services.AddScoped<IProxyProviderAdapter, ManualAdapter>();
builder.Services.AddScoped<IProxyProviderAdapterFactory, ProxyProviderAdapterFactory>();
```

- [ ] **Step 6: Build**

Run: `dotnet build src/FS.Proxy.slnx`
Expected: succeeds.

- [ ] **Step 7: Commit**

```bash
git add src/Modules/Proxies src/Tests/Proxies.Tests
git commit -m "feat(proxies): add provider adapter abstraction, factory, and Manual adapter"
```

---

## Milestone C — Admin CRUD Slices

### Task 7: ProviderAccount CRUD

**Files:**
- Create: `src/Modules/Proxies/Modules.Proxies.Contracts/Dtos/ProviderAccountDto.cs`
- Create: `src/Modules/Proxies/Modules.Proxies.Contracts/v1/ProviderAccounts/CreateProviderAccountCommand.cs`
- Create: `src/Modules/Proxies/Modules.Proxies.Contracts/v1/ProviderAccounts/UpdateProviderAccountCommand.cs`
- Create: `src/Modules/Proxies/Modules.Proxies.Contracts/v1/ProviderAccounts/DeleteProviderAccountCommand.cs`
- Create: `src/Modules/Proxies/Modules.Proxies.Contracts/v1/ProviderAccounts/GetProviderAccountByIdQuery.cs`
- Create: `src/Modules/Proxies/Modules.Proxies.Contracts/v1/ProviderAccounts/ListProviderAccountsQuery.cs`
- Create: `src/Modules/Proxies/Modules.Proxies/Features/v1/ProviderAccounts/CreateProviderAccount/CreateProviderAccountCommandValidator.cs`
- Create: `src/Modules/Proxies/Modules.Proxies/Features/v1/ProviderAccounts/CreateProviderAccount/CreateProviderAccountCommandHandler.cs`
- Create: `src/Modules/Proxies/Modules.Proxies/Features/v1/ProviderAccounts/CreateProviderAccount/CreateProviderAccountEndpoint.cs`
- Create: `src/Modules/Proxies/Modules.Proxies/Features/v1/ProviderAccounts/UpdateProviderAccount/UpdateProviderAccountCommandValidator.cs`
- Create: `src/Modules/Proxies/Modules.Proxies/Features/v1/ProviderAccounts/UpdateProviderAccount/UpdateProviderAccountCommandHandler.cs`
- Create: `src/Modules/Proxies/Modules.Proxies/Features/v1/ProviderAccounts/UpdateProviderAccount/UpdateProviderAccountEndpoint.cs`
- Create: `src/Modules/Proxies/Modules.Proxies/Features/v1/ProviderAccounts/DeleteProviderAccount/DeleteProviderAccountCommandValidator.cs`
- Create: `src/Modules/Proxies/Modules.Proxies/Features/v1/ProviderAccounts/DeleteProviderAccount/DeleteProviderAccountCommandHandler.cs`
- Create: `src/Modules/Proxies/Modules.Proxies/Features/v1/ProviderAccounts/DeleteProviderAccount/DeleteProviderAccountEndpoint.cs`
- Create: `src/Modules/Proxies/Modules.Proxies/Features/v1/ProviderAccounts/GetProviderAccountById/GetProviderAccountByIdQueryHandler.cs`
- Create: `src/Modules/Proxies/Modules.Proxies/Features/v1/ProviderAccounts/GetProviderAccountById/GetProviderAccountByIdEndpoint.cs`
- Create: `src/Modules/Proxies/Modules.Proxies/Features/v1/ProviderAccounts/ListProviderAccounts/ListProviderAccountsQueryValidator.cs`
- Create: `src/Modules/Proxies/Modules.Proxies/Features/v1/ProviderAccounts/ListProviderAccounts/ListProviderAccountsQueryHandler.cs`
- Create: `src/Modules/Proxies/Modules.Proxies/Features/v1/ProviderAccounts/ListProviderAccounts/ListProviderAccountsEndpoint.cs`
- Modify: `src/Modules/Proxies/Modules.Proxies/ProxiesModule.cs`
- Test: `src/Tests/Proxies.Tests/Handlers/ProviderAccountHandlerTests.cs`
- Test: `src/Tests/Proxies.Tests/Validators/ProviderAccountValidatorTests.cs`

**Interfaces:**
- Consumes: `ProviderAccountCredentialProtector` (Task 5), `ProxiesDbContext` (Task 2).
- Produces: `CreateProviderAccountCommand(string Name, ProxyProviderType ProviderType, string PlaintextCredentials) : ICommand<Guid>`, `UpdateProviderAccountCommand(Guid Id, string Name, string? PlaintextCredentials, bool IsEnabled) : ICommand`, `DeleteProviderAccountCommand(Guid Id) : ICommand`, `GetProviderAccountByIdQuery(Guid Id) : IQuery<ProviderAccountDto>`, `ListProviderAccountsQuery(int PageNumber, int PageSize) : IQuery<PagedResponse<ProviderAccountDto>>`, `ProviderAccountDto(Guid Id, string Name, ProxyProviderType ProviderType, bool IsEnabled, DateTime? LastSyncedAtUtc, string? LastSyncStatus, int ConsecutiveSyncFailures)` — the DTO's shape is reused verbatim by every later task that reads a `ProviderAccount` (Task 16's sync job status, the admin UI in Task 26).

- [ ] **Step 1: Define the DTO and command/query contracts**

```csharp
// src/Modules/Proxies/Modules.Proxies.Contracts/Dtos/ProviderAccountDto.cs
namespace FSH.Modules.Proxies.Contracts.Dtos;

public sealed record ProviderAccountDto(
    Guid Id, string Name, ProxyProviderType ProviderType, bool IsEnabled,
    DateTime? LastSyncedAtUtc, string? LastSyncStatus, int ConsecutiveSyncFailures);
```

```csharp
// src/Modules/Proxies/Modules.Proxies.Contracts/v1/ProviderAccounts/CreateProviderAccountCommand.cs
using Mediator;

namespace FSH.Modules.Proxies.Contracts.v1.ProviderAccounts;

public sealed record CreateProviderAccountCommand(
    string Name, ProxyProviderType ProviderType, string PlaintextCredentials) : ICommand<Guid>;
```

```csharp
// src/Modules/Proxies/Modules.Proxies.Contracts/v1/ProviderAccounts/UpdateProviderAccountCommand.cs
using Mediator;

namespace FSH.Modules.Proxies.Contracts.v1.ProviderAccounts;

public sealed record UpdateProviderAccountCommand(
    Guid Id, string Name, string? PlaintextCredentials, bool IsEnabled) : ICommand;
```

```csharp
// src/Modules/Proxies/Modules.Proxies.Contracts/v1/ProviderAccounts/DeleteProviderAccountCommand.cs
using Mediator;

namespace FSH.Modules.Proxies.Contracts.v1.ProviderAccounts;

public sealed record DeleteProviderAccountCommand(Guid Id) : ICommand;
```

```csharp
// src/Modules/Proxies/Modules.Proxies.Contracts/v1/ProviderAccounts/GetProviderAccountByIdQuery.cs
using FSH.Modules.Proxies.Contracts.Dtos;
using Mediator;

namespace FSH.Modules.Proxies.Contracts.v1.ProviderAccounts;

public sealed record GetProviderAccountByIdQuery(Guid Id) : IQuery<ProviderAccountDto>;
```

```csharp
// src/Modules/Proxies/Modules.Proxies.Contracts/v1/ProviderAccounts/ListProviderAccountsQuery.cs
using FSH.Framework.Shared.Persistence;
using FSH.Modules.Proxies.Contracts.Dtos;
using Mediator;

namespace FSH.Modules.Proxies.Contracts.v1.ProviderAccounts;

public sealed record ListProviderAccountsQuery(int PageNumber = 1, int PageSize = 20) : IQuery<PagedResponse<ProviderAccountDto>>;
```

- [ ] **Step 2: Write the failing validator and handler tests**

```csharp
// src/Tests/Proxies.Tests/Validators/ProviderAccountValidatorTests.cs
using FSH.Modules.Proxies.Contracts;
using FSH.Modules.Proxies.Contracts.v1.ProviderAccounts;
using FSH.Modules.Proxies.Features.v1.ProviderAccounts.CreateProviderAccount;
using Shouldly;
using Xunit;

namespace Proxies.Tests.Validators;

public sealed class ProviderAccountValidatorTests
{
    private readonly CreateProviderAccountCommandValidator _validator = new();

    [Fact]
    public void Should_Pass_When_Valid()
    {
        var command = new CreateProviderAccountCommand("WebShare - main", ProxyProviderType.WebShare, "api-key-123");

        _validator.Validate(command).IsValid.ShouldBeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void Should_Fail_When_NameEmpty(string name)
    {
        var command = new CreateProviderAccountCommand(name, ProxyProviderType.WebShare, "api-key-123");

        _validator.Validate(command).IsValid.ShouldBeFalse();
    }

    [Fact]
    public void Should_Fail_When_CredentialsEmpty()
    {
        var command = new CreateProviderAccountCommand("WebShare - main", ProxyProviderType.WebShare, "");

        _validator.Validate(command).IsValid.ShouldBeFalse();
    }
}
```

```csharp
// src/Tests/Proxies.Tests/Handlers/ProviderAccountHandlerTests.cs
using FSH.Modules.Proxies.Contracts;
using FSH.Modules.Proxies.Contracts.v1.ProviderAccounts;
using FSH.Modules.Proxies.Data;
using FSH.Modules.Proxies.Domain;
using FSH.Modules.Proxies.Features.v1.ProviderAccounts.CreateProviderAccount;
using FSH.Modules.Proxies.Features.v1.ProviderAccounts.DeleteProviderAccount;
using FSH.Modules.Proxies.Features.v1.ProviderAccounts.UpdateProviderAccount;
using FSH.Modules.Proxies.Services;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using Xunit;

namespace Proxies.Tests.Handlers;

public sealed class ProviderAccountHandlerTests
{
    private static ProxiesDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<ProxiesDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new TestProxiesDbContext(options);
    }

    [Fact]
    public async Task Create_Should_PersistWithEncryptedCredentials()
    {
        await using var db = CreateDb();
        var protector = new FakeSecretProtector();
        var sut = new CreateProviderAccountCommandHandler(db, protector);
        var command = new CreateProviderAccountCommand("WebShare - main", ProxyProviderType.WebShare, "plain-secret");

        var id = await sut.Handle(command, CancellationToken.None);

        var stored = await db.ProviderAccounts.SingleAsync(x => x.Id == id);
        stored.Name.ShouldBe("WebShare - main");
        stored.ProtectedCredentials.ShouldBe("protected:plain-secret");
        stored.IsEnabled.ShouldBeTrue();
    }

    [Fact]
    public async Task Update_Should_ReplaceCredentials_When_Provided()
    {
        await using var db = CreateDb();
        var protector = new FakeSecretProtector();
        var account = ProviderAccount.Create("Oxylabs", ProxyProviderType.Oxylabs, "protected:old");
        db.ProviderAccounts.Add(account);
        await db.SaveChangesAsync();
        var sut = new UpdateProviderAccountCommandHandler(db, protector);

        await sut.Handle(new UpdateProviderAccountCommand(account.Id, "Oxylabs - renamed", "new-secret", false), CancellationToken.None);

        var stored = await db.ProviderAccounts.SingleAsync(x => x.Id == account.Id);
        stored.Name.ShouldBe("Oxylabs - renamed");
        stored.ProtectedCredentials.ShouldBe("protected:new-secret");
        stored.IsEnabled.ShouldBeFalse();
    }

    [Fact]
    public async Task Delete_Should_RemoveAccount()
    {
        await using var db = CreateDb();
        var account = ProviderAccount.Create("BrightData", ProxyProviderType.BrightData, "protected:x");
        db.ProviderAccounts.Add(account);
        await db.SaveChangesAsync();
        var sut = new DeleteProviderAccountCommandHandler(db);

        await sut.Handle(new DeleteProviderAccountCommand(account.Id), CancellationToken.None);

        (await db.ProviderAccounts.AnyAsync(x => x.Id == account.Id)).ShouldBeFalse();
    }

    private sealed class FakeSecretProtector : IProxySecretProtector
    {
        public string Protect(string plaintext) => $"protected:{plaintext}";
        public string Unprotect(string ciphertext) => ciphertext.Replace("protected:", string.Empty, StringComparison.Ordinal);
    }
}
```

The handler tests reference `TestProxiesDbContext` — a minimal EF-InMemory-friendly subclass used across every handler test in this plan (avoids re-deriving `BaseDbContext`'s tenant/multitenancy constructor wiring in every test file). Create it once now:

```csharp
// src/Tests/Proxies.Tests/TestProxiesDbContext.cs
using FSH.Modules.Proxies.Data;
using Microsoft.EntityFrameworkCore;

namespace Proxies.Tests;

/// <summary>
/// EF-InMemory-friendly ProxiesDbContext for handler unit tests. IGlobalEntity means every
/// entity in this module is exempt from Finbuckle's tenant query filter, so no tenant context
/// setup is required here — a real DbContextOptions with UseInMemoryDatabase is enough.
/// </summary>
internal sealed class TestProxiesDbContext(DbContextOptions<ProxiesDbContext> options) : ProxiesDbContext(
    multiTenantContextAccessor: FSH.Framework.Shared.Multitenancy.NullMultiTenantContextAccessor.Instance,
    options: options,
    settings: Microsoft.Extensions.Options.Options.Create(new FSH.Framework.Persistence.DatabaseOptions()),
    environment: new TestHostEnvironment());
```

If `FSH.Framework.Shared.Multitenancy.NullMultiTenantContextAccessor` doesn't exist in the installed `FSH.Framework.Shared` package version, check the testing-guide's own handler-test template (`.agents/skills/testing-guide/SKILL.md`) for the exact no-tenant-context construction idiom used elsewhere in this repo's test suite and match it instead — the important part is only that `IGlobalEntity` entities never touch the tenant filter, so any accessor stub that returns no active tenant context works.

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet test src/Tests/Proxies.Tests --filter "FullyQualifiedName~ProviderAccount"`
Expected: fails to compile — none of the handler/validator types exist yet.

- [ ] **Step 4: Implement the validators**

```csharp
// src/Modules/Proxies/Modules.Proxies/Features/v1/ProviderAccounts/CreateProviderAccount/CreateProviderAccountCommandValidator.cs
using FluentValidation;
using FSH.Modules.Proxies.Contracts.v1.ProviderAccounts;

namespace FSH.Modules.Proxies.Features.v1.ProviderAccounts.CreateProviderAccount;

public sealed class CreateProviderAccountCommandValidator : AbstractValidator<CreateProviderAccountCommand>
{
    public CreateProviderAccountCommandValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(128);
        RuleFor(x => x.PlaintextCredentials).NotEmpty();
        RuleFor(x => x.ProviderType).IsInEnum();
    }
}
```

```csharp
// src/Modules/Proxies/Modules.Proxies/Features/v1/ProviderAccounts/UpdateProviderAccount/UpdateProviderAccountCommandValidator.cs
using FluentValidation;
using FSH.Modules.Proxies.Contracts.v1.ProviderAccounts;

namespace FSH.Modules.Proxies.Features.v1.ProviderAccounts.UpdateProviderAccount;

public sealed class UpdateProviderAccountCommandValidator : AbstractValidator<UpdateProviderAccountCommand>
{
    public UpdateProviderAccountCommandValidator()
    {
        RuleFor(x => x.Id).NotEmpty();
        RuleFor(x => x.Name).NotEmpty().MaximumLength(128);
        RuleFor(x => x.PlaintextCredentials).MaximumLength(4096);
    }
}
```

```csharp
// src/Modules/Proxies/Modules.Proxies/Features/v1/ProviderAccounts/DeleteProviderAccount/DeleteProviderAccountCommandValidator.cs
using FluentValidation;
using FSH.Modules.Proxies.Contracts.v1.ProviderAccounts;

namespace FSH.Modules.Proxies.Features.v1.ProviderAccounts.DeleteProviderAccount;

public sealed class DeleteProviderAccountCommandValidator : AbstractValidator<DeleteProviderAccountCommand>
{
    public DeleteProviderAccountCommandValidator() => RuleFor(x => x.Id).NotEmpty();
}
```

```csharp
// src/Modules/Proxies/Modules.Proxies/Features/v1/ProviderAccounts/ListProviderAccounts/ListProviderAccountsQueryValidator.cs
using FluentValidation;
using FSH.Modules.Proxies.Contracts.v1.ProviderAccounts;

namespace FSH.Modules.Proxies.Features.v1.ProviderAccounts.ListProviderAccounts;

public sealed class ListProviderAccountsQueryValidator : AbstractValidator<ListProviderAccountsQuery>
{
    public ListProviderAccountsQueryValidator()
    {
        RuleFor(x => x.PageNumber).GreaterThanOrEqualTo(1);
        RuleFor(x => x.PageSize).InclusiveBetween(1, 200);
    }
}
```

- [ ] **Step 5: Implement the handlers**

```csharp
// src/Modules/Proxies/Modules.Proxies/Features/v1/ProviderAccounts/CreateProviderAccount/CreateProviderAccountCommandHandler.cs
using FSH.Modules.Proxies.Contracts.v1.ProviderAccounts;
using FSH.Modules.Proxies.Data;
using FSH.Modules.Proxies.Domain;
using FSH.Modules.Proxies.Services;
using Mediator;

namespace FSH.Modules.Proxies.Features.v1.ProviderAccounts.CreateProviderAccount;

public sealed class CreateProviderAccountCommandHandler(
    ProxiesDbContext dbContext, ProviderAccountCredentialProtector protector)
    : ICommandHandler<CreateProviderAccountCommand, Guid>
{
    public async ValueTask<Guid> Handle(CreateProviderAccountCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var account = ProviderAccount.Create(command.Name, command.ProviderType, protector.Protect(command.PlaintextCredentials));
        dbContext.ProviderAccounts.Add(account);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return account.Id;
    }
}
```

```csharp
// src/Modules/Proxies/Modules.Proxies/Features/v1/ProviderAccounts/UpdateProviderAccount/UpdateProviderAccountCommandHandler.cs
using FSH.Framework.Core.Exceptions;
using FSH.Modules.Proxies.Contracts.v1.ProviderAccounts;
using FSH.Modules.Proxies.Data;
using FSH.Modules.Proxies.Services;
using Mediator;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.Proxies.Features.v1.ProviderAccounts.UpdateProviderAccount;

public sealed class UpdateProviderAccountCommandHandler(
    ProxiesDbContext dbContext, ProviderAccountCredentialProtector protector)
    : ICommandHandler<UpdateProviderAccountCommand>
{
    public async ValueTask<Unit> Handle(UpdateProviderAccountCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var account = await dbContext.ProviderAccounts.FirstOrDefaultAsync(x => x.Id == command.Id, cancellationToken).ConfigureAwait(false)
            ?? throw new NotFoundException($"Provider account {command.Id} not found.");

        account.Rename(command.Name);
        account.SetEnabled(command.IsEnabled);
        if (!string.IsNullOrWhiteSpace(command.PlaintextCredentials))
        {
            account.UpdateCredentials(protector.Protect(command.PlaintextCredentials));
        }

        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return Unit.Value;
    }
}
```

```csharp
// src/Modules/Proxies/Modules.Proxies/Features/v1/ProviderAccounts/DeleteProviderAccount/DeleteProviderAccountCommandHandler.cs
using FSH.Framework.Core.Exceptions;
using FSH.Modules.Proxies.Contracts.v1.ProviderAccounts;
using FSH.Modules.Proxies.Data;
using Mediator;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.Proxies.Features.v1.ProviderAccounts.DeleteProviderAccount;

public sealed class DeleteProviderAccountCommandHandler(ProxiesDbContext dbContext) : ICommandHandler<DeleteProviderAccountCommand>
{
    public async ValueTask<Unit> Handle(DeleteProviderAccountCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var account = await dbContext.ProviderAccounts.FirstOrDefaultAsync(x => x.Id == command.Id, cancellationToken).ConfigureAwait(false)
            ?? throw new NotFoundException($"Provider account {command.Id} not found.");

        dbContext.ProviderAccounts.Remove(account);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return Unit.Value;
    }
}
```

```csharp
// src/Modules/Proxies/Modules.Proxies/Features/v1/ProviderAccounts/GetProviderAccountById/GetProviderAccountByIdQueryHandler.cs
using FSH.Framework.Core.Exceptions;
using FSH.Modules.Proxies.Contracts.Dtos;
using FSH.Modules.Proxies.Contracts.v1.ProviderAccounts;
using FSH.Modules.Proxies.Data;
using Mediator;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.Proxies.Features.v1.ProviderAccounts.GetProviderAccountById;

public sealed class GetProviderAccountByIdQueryHandler(ProxiesDbContext dbContext) : IQueryHandler<GetProviderAccountByIdQuery, ProviderAccountDto>
{
    public async ValueTask<ProviderAccountDto> Handle(GetProviderAccountByIdQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        return await dbContext.ProviderAccounts.AsNoTracking()
            .Where(x => x.Id == query.Id)
            .Select(x => new ProviderAccountDto(x.Id, x.Name, x.ProviderType, x.IsEnabled, x.LastSyncedAtUtc, x.LastSyncStatus, x.ConsecutiveSyncFailures))
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new NotFoundException($"Provider account {query.Id} not found.");
    }
}
```

```csharp
// src/Modules/Proxies/Modules.Proxies/Features/v1/ProviderAccounts/ListProviderAccounts/ListProviderAccountsQueryHandler.cs
using FSH.Framework.Shared.Persistence;
using FSH.Modules.Proxies.Contracts.Dtos;
using FSH.Modules.Proxies.Contracts.v1.ProviderAccounts;
using FSH.Modules.Proxies.Data;
using Mediator;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.Proxies.Features.v1.ProviderAccounts.ListProviderAccounts;

public sealed class ListProviderAccountsQueryHandler(ProxiesDbContext dbContext)
    : IQueryHandler<ListProviderAccountsQuery, PagedResponse<ProviderAccountDto>>
{
    public async ValueTask<PagedResponse<ProviderAccountDto>> Handle(ListProviderAccountsQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var q = dbContext.ProviderAccounts.AsNoTracking().OrderBy(x => x.Name);
        long total = await q.LongCountAsync(cancellationToken).ConfigureAwait(false);
        var items = await q.Skip((query.PageNumber - 1) * query.PageSize).Take(query.PageSize)
            .Select(x => new ProviderAccountDto(x.Id, x.Name, x.ProviderType, x.IsEnabled, x.LastSyncedAtUtc, x.LastSyncStatus, x.ConsecutiveSyncFailures))
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        return new PagedResponse<ProviderAccountDto>
        {
            Items = items, PageNumber = query.PageNumber, PageSize = query.PageSize,
            TotalCount = total, TotalPages = (int)Math.Ceiling(total / (double)query.PageSize)
        };
    }
}
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test src/Tests/Proxies.Tests --filter "FullyQualifiedName~ProviderAccount"`
Expected: PASS, 7 tests (3 validator + 3 handler + the earlier passing ones untouched).

- [ ] **Step 7: Implement the endpoints**

```csharp
// src/Modules/Proxies/Modules.Proxies/Features/v1/ProviderAccounts/CreateProviderAccount/CreateProviderAccountEndpoint.cs
using FSH.Framework.Shared.Identity.Authorization;
using FSH.Modules.Proxies.Contracts.Authorization;
using FSH.Modules.Proxies.Contracts.v1.ProviderAccounts;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FSH.Modules.Proxies.Features.v1.ProviderAccounts.CreateProviderAccount;

public static class CreateProviderAccountEndpoint
{
    internal static RouteHandlerBuilder MapCreateProviderAccountEndpoint(this IEndpointRouteBuilder endpoints)
    {
        return endpoints.MapPost("/provider-accounts",
                async (CreateProviderAccountCommand command, IMediator mediator, CancellationToken ct) =>
                    Results.Ok(await mediator.Send(command, ct)))
            .WithName("CreateProviderAccount")
            .WithSummary("Create a proxy provider account")
            .RequirePermission(ProxiesPermissions.ProviderAccounts.Create);
    }
}
```

```csharp
// src/Modules/Proxies/Modules.Proxies/Features/v1/ProviderAccounts/UpdateProviderAccount/UpdateProviderAccountEndpoint.cs
using FSH.Framework.Shared.Identity.Authorization;
using FSH.Modules.Proxies.Contracts.Authorization;
using FSH.Modules.Proxies.Contracts.v1.ProviderAccounts;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FSH.Modules.Proxies.Features.v1.ProviderAccounts.UpdateProviderAccount;

public static class UpdateProviderAccountEndpoint
{
    internal static RouteHandlerBuilder MapUpdateProviderAccountEndpoint(this IEndpointRouteBuilder endpoints)
    {
        return endpoints.MapPut("/provider-accounts/{id:guid}",
                async (Guid id, UpdateProviderAccountBody body, IMediator mediator, CancellationToken ct) =>
                {
                    await mediator.Send(new UpdateProviderAccountCommand(id, body.Name, body.PlaintextCredentials, body.IsEnabled), ct);
                    return Results.NoContent();
                })
            .WithName("UpdateProviderAccount")
            .WithSummary("Update a proxy provider account")
            .RequirePermission(ProxiesPermissions.ProviderAccounts.Update);
    }

    internal sealed record UpdateProviderAccountBody(string Name, string? PlaintextCredentials, bool IsEnabled);
}
```

```csharp
// src/Modules/Proxies/Modules.Proxies/Features/v1/ProviderAccounts/DeleteProviderAccount/DeleteProviderAccountEndpoint.cs
using FSH.Framework.Shared.Identity.Authorization;
using FSH.Modules.Proxies.Contracts.Authorization;
using FSH.Modules.Proxies.Contracts.v1.ProviderAccounts;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FSH.Modules.Proxies.Features.v1.ProviderAccounts.DeleteProviderAccount;

public static class DeleteProviderAccountEndpoint
{
    internal static RouteHandlerBuilder MapDeleteProviderAccountEndpoint(this IEndpointRouteBuilder endpoints)
    {
        return endpoints.MapDelete("/provider-accounts/{id:guid}",
                async (Guid id, IMediator mediator, CancellationToken ct) =>
                {
                    await mediator.Send(new DeleteProviderAccountCommand(id), ct);
                    return Results.NoContent();
                })
            .WithName("DeleteProviderAccount")
            .WithSummary("Delete a proxy provider account")
            .RequirePermission(ProxiesPermissions.ProviderAccounts.Delete);
    }
}
```

```csharp
// src/Modules/Proxies/Modules.Proxies/Features/v1/ProviderAccounts/GetProviderAccountById/GetProviderAccountByIdEndpoint.cs
using FSH.Framework.Shared.Identity.Authorization;
using FSH.Modules.Proxies.Contracts.Authorization;
using FSH.Modules.Proxies.Contracts.v1.ProviderAccounts;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FSH.Modules.Proxies.Features.v1.ProviderAccounts.GetProviderAccountById;

public static class GetProviderAccountByIdEndpoint
{
    internal static RouteHandlerBuilder MapGetProviderAccountByIdEndpoint(this IEndpointRouteBuilder endpoints)
    {
        return endpoints.MapGet("/provider-accounts/{id:guid}",
                (Guid id, IMediator mediator, CancellationToken ct) => mediator.Send(new GetProviderAccountByIdQuery(id), ct))
            .WithName("GetProviderAccountById")
            .WithSummary("Get a proxy provider account by id")
            .RequirePermission(ProxiesPermissions.ProviderAccounts.View);
    }
}
```

```csharp
// src/Modules/Proxies/Modules.Proxies/Features/v1/ProviderAccounts/ListProviderAccounts/ListProviderAccountsEndpoint.cs
using FSH.Framework.Shared.Identity.Authorization;
using FSH.Modules.Proxies.Contracts.Authorization;
using FSH.Modules.Proxies.Contracts.v1.ProviderAccounts;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FSH.Modules.Proxies.Features.v1.ProviderAccounts.ListProviderAccounts;

public static class ListProviderAccountsEndpoint
{
    internal static RouteHandlerBuilder MapListProviderAccountsEndpoint(this IEndpointRouteBuilder endpoints)
    {
        return endpoints.MapGet("/provider-accounts",
                (int pageNumber, int pageSize, IMediator mediator, CancellationToken ct) =>
                    mediator.Send(new ListProviderAccountsQuery(pageNumber == 0 ? 1 : pageNumber, pageSize == 0 ? 20 : pageSize), ct))
            .WithName("ListProviderAccounts")
            .WithSummary("List proxy provider accounts (paged)")
            .RequirePermission(ProxiesPermissions.ProviderAccounts.View);
    }
}
```

- [ ] **Step 8: Wire the endpoints into `ProxiesModule.MapEndpoints` and register the credential protector for DI**

```csharp
// inside MapEndpoints, replacing the "Endpoint registrations added in later tasks." comment
group.MapCreateProviderAccountEndpoint();
group.MapUpdateProviderAccountEndpoint();
group.MapDeleteProviderAccountEndpoint();
group.MapGetProviderAccountByIdEndpoint();
group.MapListProviderAccountsEndpoint();
```

- [ ] **Step 9: Build and run the full Proxies test suite**

Run: `dotnet build src/FS.Proxy.slnx && dotnet test src/Tests/Proxies.Tests`
Expected: PASS.

- [ ] **Step 10: Commit**

```bash
git add src/Modules/Proxies src/Tests/Proxies.Tests
git commit -m "feat(proxies): add ProviderAccount CRUD slice"
```

### Task 8: Manual proxy CRUD (seeds the well-known "Manual" provider account)

Manually-entered proxies still hang off a `ProviderAccount` row (Task 2's FK), so a single well-known `Manual`-type account must exist before any manual proxy can be created. Rather than making the admin create it by hand, `ProxiesDbInitializer.SeedAsync` provisions it deterministically.

**Files:**
- Create: `src/Modules/Proxies/Modules.Proxies/Domain/ManualProviderAccount.cs`
- Modify: `src/Modules/Proxies/Modules.Proxies/Data/ProxiesDbInitializer.cs`
- Create: `src/Modules/Proxies/Modules.Proxies.Contracts/v1/ManualProxies/CreateManualProxyCommand.cs`
- Create: `src/Modules/Proxies/Modules.Proxies.Contracts/v1/ManualProxies/UpdateManualProxyCommand.cs`
- Create: `src/Modules/Proxies/Modules.Proxies.Contracts/v1/ManualProxies/DeleteManualProxyCommand.cs`
- Create: `src/Modules/Proxies/Modules.Proxies/Features/v1/ManualProxies/CreateManualProxy/CreateManualProxyCommandValidator.cs`
- Create: `src/Modules/Proxies/Modules.Proxies/Features/v1/ManualProxies/CreateManualProxy/CreateManualProxyCommandHandler.cs`
- Create: `src/Modules/Proxies/Modules.Proxies/Features/v1/ManualProxies/CreateManualProxy/CreateManualProxyEndpoint.cs`
- Create: `src/Modules/Proxies/Modules.Proxies/Features/v1/ManualProxies/UpdateManualProxy/UpdateManualProxyCommandValidator.cs`
- Create: `src/Modules/Proxies/Modules.Proxies/Features/v1/ManualProxies/UpdateManualProxy/UpdateManualProxyCommandHandler.cs`
- Create: `src/Modules/Proxies/Modules.Proxies/Features/v1/ManualProxies/UpdateManualProxy/UpdateManualProxyEndpoint.cs`
- Create: `src/Modules/Proxies/Modules.Proxies/Features/v1/ManualProxies/DeleteManualProxy/DeleteManualProxyCommandValidator.cs`
- Create: `src/Modules/Proxies/Modules.Proxies/Features/v1/ManualProxies/DeleteManualProxy/DeleteManualProxyCommandHandler.cs`
- Create: `src/Modules/Proxies/Modules.Proxies/Features/v1/ManualProxies/DeleteManualProxy/DeleteManualProxyEndpoint.cs`
- Modify: `src/Modules/Proxies/Modules.Proxies/ProxiesModule.cs`
- Test: `src/Tests/Proxies.Tests/Handlers/ManualProxyHandlerTests.cs`
- Test: `src/Tests/Proxies.Tests/Validators/ManualProxyValidatorTests.cs`

**Interfaces:**
- Consumes: `ProxyPasswordProtector` (Task 5), `Proxy.AssignTag`/`.UnassignTag` (Task 2), `ManualProviderAccount.Id`.
- Produces: `ManualProviderAccount.Id` (a fixed, deterministic `Guid`, consumed by Task 12's list/filter and Task 20's health-check job — any proxy whose `ProviderAccountId == ManualProviderAccount.Id` is a manual proxy); `CreateManualProxyCommand(string Host, int Port, ProxyProtocol Protocol, string? Username, string? PlaintextPassword, IReadOnlyList<string> TagNames) : ICommand<Guid>`; `UpdateManualProxyCommand(Guid Id, string Host, int Port, ProxyProtocol Protocol, string? Username, string? PlaintextPassword, IReadOnlyList<string> TagNames) : ICommand`; `DeleteManualProxyCommand(Guid Id) : ICommand`.

- [ ] **Step 1: Add the well-known Manual account id and seed it**

```csharp
// src/Modules/Proxies/Modules.Proxies/Domain/ManualProviderAccount.cs
namespace FSH.Modules.Proxies.Domain;

/// <summary>Every manually-entered Proxy row's ProviderAccountId points at this fixed account.</summary>
public static class ManualProviderAccount
{
    public static readonly Guid Id = Guid.Parse("00000000-0000-0000-0000-000000000001");
}
```

```csharp
// replace ProxiesDbInitializer.SeedAsync's body
public async Task SeedAsync(CancellationToken cancellationToken)
{
    bool exists = await dbContext.ProviderAccounts.AnyAsync(x => x.Id == ManualProviderAccount.Id, cancellationToken).ConfigureAwait(false);
    if (exists) return;

    var manualAccount = ProviderAccount.Create("Manual", FSH.Modules.Proxies.Contracts.ProxyProviderType.Manual, "n/a");
    typeof(FSH.Framework.Core.Domain.BaseEntity<Guid>).GetProperty(nameof(FSH.Framework.Core.Domain.BaseEntity<Guid>.Id))!
        .SetValue(manualAccount, ManualProviderAccount.Id);
    dbContext.ProviderAccounts.Add(manualAccount);
    await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    logger.LogInformation("Seeded the well-known Manual provider account.");
}
```

(add `using Microsoft.EntityFrameworkCore;` and `using FSH.Modules.Proxies.Domain;` to `ProxiesDbInitializer.cs`'s usings)

Reflection is used here only because `Id` is intentionally private-set everywhere else in this module (Task 2's `Create()` factories always call `Guid.CreateVersion7()`) — this is the one deliberate exception, for a single deterministic seed row. If `AggregateRoot<T>`'s `Id` setter turns out to be `protected` rather than `private` when you check the actual `FSH.Framework.Core` package (see `Directory.Build.props`'s note on embedded PDBs — you can step into it), prefer a `protected` constructor overload instead of reflection.

- [ ] **Step 2: Define the command contracts**

```csharp
// src/Modules/Proxies/Modules.Proxies.Contracts/v1/ManualProxies/CreateManualProxyCommand.cs
using Mediator;

namespace FSH.Modules.Proxies.Contracts.v1.ManualProxies;

public sealed record CreateManualProxyCommand(
    string Host, int Port, ProxyProtocol Protocol,
    string? Username, string? PlaintextPassword, IReadOnlyList<string> TagNames) : ICommand<Guid>;
```

```csharp
// src/Modules/Proxies/Modules.Proxies.Contracts/v1/ManualProxies/UpdateManualProxyCommand.cs
using Mediator;

namespace FSH.Modules.Proxies.Contracts.v1.ManualProxies;

public sealed record UpdateManualProxyCommand(
    Guid Id, string Host, int Port, ProxyProtocol Protocol,
    string? Username, string? PlaintextPassword, IReadOnlyList<string> TagNames) : ICommand;
```

```csharp
// src/Modules/Proxies/Modules.Proxies.Contracts/v1/ManualProxies/DeleteManualProxyCommand.cs
using Mediator;

namespace FSH.Modules.Proxies.Contracts.v1.ManualProxies;

public sealed record DeleteManualProxyCommand(Guid Id) : ICommand;
```

`ProxyProtocol` needs to move to `Modules.Proxies.Contracts` so command records (a Contracts-only concern) can reference it without depending on the runtime `Domain` namespace — update its declaration:

```csharp
// src/Modules/Proxies/Modules.Proxies.Contracts/ProxyProtocol.cs (new file)
namespace FSH.Modules.Proxies.Contracts;

public enum ProxyProtocol { Http, Https, Socks5 }
```

Then delete the `ProxyProtocol` enum out of `src/Modules/Proxies/Modules.Proxies/Domain/Proxy.cs` (leave `ProxyStatus` there — it's a runtime-only concept) and add `using FSH.Modules.Proxies.Contracts;` to every file in `Domain/`, `Data/Configurations/`, and `Providers/` that referenced the old in-`Domain` `ProxyProtocol`.

- [ ] **Step 3: Write the failing validator and handler tests**

```csharp
// src/Tests/Proxies.Tests/Validators/ManualProxyValidatorTests.cs
using FSH.Modules.Proxies.Contracts;
using FSH.Modules.Proxies.Contracts.v1.ManualProxies;
using FSH.Modules.Proxies.Features.v1.ManualProxies.CreateManualProxy;
using Shouldly;
using Xunit;

namespace Proxies.Tests.Validators;

public sealed class ManualProxyValidatorTests
{
    private readonly CreateManualProxyCommandValidator _validator = new();

    [Fact]
    public void Should_Pass_When_Valid()
    {
        var command = new CreateManualProxyCommand("10.0.0.5", 3128, ProxyProtocol.Http, "user", "pass", ["pais:cl"]);

        _validator.Validate(command).IsValid.ShouldBeTrue();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(65536)]
    public void Should_Fail_When_PortOutOfRange(int port)
    {
        var command = new CreateManualProxyCommand("10.0.0.5", port, ProxyProtocol.Http, null, null, []);

        _validator.Validate(command).IsValid.ShouldBeFalse();
    }

    [Fact]
    public void Should_Fail_When_HostEmpty()
    {
        var command = new CreateManualProxyCommand("", 3128, ProxyProtocol.Http, null, null, []);

        _validator.Validate(command).IsValid.ShouldBeFalse();
    }
}
```

```csharp
// src/Tests/Proxies.Tests/Handlers/ManualProxyHandlerTests.cs
using FSH.Modules.Proxies.Contracts;
using FSH.Modules.Proxies.Contracts.v1.ManualProxies;
using FSH.Modules.Proxies.Domain;
using FSH.Modules.Proxies.Features.v1.ManualProxies.CreateManualProxy;
using FSH.Modules.Proxies.Features.v1.ManualProxies.DeleteManualProxy;
using FSH.Modules.Proxies.Features.v1.ManualProxies.UpdateManualProxy;
using FSH.Modules.Proxies.Services;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using Xunit;

namespace Proxies.Tests.Handlers;

public sealed class ManualProxyHandlerTests
{
    private static Proxies.Tests.TestProxiesDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<FSH.Modules.Proxies.Data.ProxiesDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    [Fact]
    public async Task Create_Should_AttachToManualAccount_And_CreateNewTags()
    {
        await using var db = CreateDb();
        var sut = new CreateManualProxyCommandHandler(db, new FakePasswordProtector());
        var command = new CreateManualProxyCommand("10.0.0.5", 3128, ProxyProtocol.Http, "u", "p", ["pais:cl", "funcionalidad:licitaciones"]);

        var id = await sut.Handle(command, CancellationToken.None);

        var stored = await db.Proxies.Include(x => x.TagAssignments).SingleAsync(x => x.Id == id);
        stored.ProviderAccountId.ShouldBe(ManualProviderAccount.Id);
        stored.TagAssignments.Count.ShouldBe(2);
        (await db.Tags.CountAsync()).ShouldBe(2);
    }

    [Fact]
    public async Task Create_Should_ReuseExistingTag_When_NameAlreadyExists()
    {
        await using var db = CreateDb();
        db.Tags.Add(Tag.Create("pais:cl"));
        await db.SaveChangesAsync();
        var sut = new CreateManualProxyCommandHandler(db, new FakePasswordProtector());

        await sut.Handle(new CreateManualProxyCommand("10.0.0.6", 3128, ProxyProtocol.Http, null, null, ["PAIS:CL"]), CancellationToken.None);

        (await db.Tags.CountAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task Delete_Should_RemoveProxy()
    {
        await using var db = CreateDb();
        var proxy = Proxy.Create(ManualProviderAccount.Id, "10.0.0.7", 3128, ProxyProtocol.Http, null, null, null);
        db.Proxies.Add(proxy);
        await db.SaveChangesAsync();
        var sut = new DeleteManualProxyCommandHandler(db);

        await sut.Handle(new DeleteManualProxyCommand(proxy.Id), CancellationToken.None);

        (await db.Proxies.AnyAsync(x => x.Id == proxy.Id)).ShouldBeFalse();
    }

    private sealed class FakePasswordProtector : IProxySecretProtector
    {
        public string Protect(string plaintext) => $"protected:{plaintext}";
        public string Unprotect(string ciphertext) => ciphertext;
    }
}
```

- [ ] **Step 4: Run the tests to verify they fail, then implement**

Run: `dotnet test src/Tests/Proxies.Tests --filter "FullyQualifiedName~ManualProxy"` — expect compile failure first.

```csharp
// src/Modules/Proxies/Modules.Proxies/Features/v1/ManualProxies/CreateManualProxy/CreateManualProxyCommandValidator.cs
using FluentValidation;
using FSH.Modules.Proxies.Contracts.v1.ManualProxies;

namespace FSH.Modules.Proxies.Features.v1.ManualProxies.CreateManualProxy;

public sealed class CreateManualProxyCommandValidator : AbstractValidator<CreateManualProxyCommand>
{
    public CreateManualProxyCommandValidator()
    {
        RuleFor(x => x.Host).NotEmpty().MaximumLength(255);
        RuleFor(x => x.Port).InclusiveBetween(1, 65535);
        RuleFor(x => x.Protocol).IsInEnum();
        RuleForEach(x => x.TagNames).NotEmpty().MaximumLength(128);
    }
}
```

```csharp
// src/Modules/Proxies/Modules.Proxies/Features/v1/ManualProxies/CreateManualProxy/CreateManualProxyCommandHandler.cs
using FSH.Modules.Proxies.Contracts.v1.ManualProxies;
using FSH.Modules.Proxies.Data;
using FSH.Modules.Proxies.Domain;
using FSH.Modules.Proxies.Services;
using Mediator;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FSH.Modules.Proxies.Features.v1.ManualProxies.CreateManualProxy;

public sealed class CreateManualProxyCommandHandler(
    ProxiesDbContext dbContext, [FromKeyedServices("proxy-password")] IProxySecretProtector proxyPasswordProtector)
    : ICommandHandler<CreateManualProxyCommand, Guid>
{
    public async ValueTask<Guid> Handle(CreateManualProxyCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        string? protectedPassword = string.IsNullOrWhiteSpace(command.PlaintextPassword)
            ? null : proxyPasswordProtector.Protect(command.PlaintextPassword);

        var proxy = Proxy.Create(ManualProviderAccount.Id, command.Host, command.Port, command.Protocol, command.Username, protectedPassword, externalId: null);

        foreach (var tagId in await ResolveTagIdsAsync(dbContext, command.TagNames, cancellationToken).ConfigureAwait(false))
        {
            proxy.AssignTag(tagId);
        }

        dbContext.Proxies.Add(proxy);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return proxy.Id;
    }

    internal static async Task<List<Guid>> ResolveTagIdsAsync(ProxiesDbContext dbContext, IReadOnlyList<string> tagNames, CancellationToken cancellationToken)
    {
        var normalized = tagNames.Select(Tag.Normalize).Distinct().ToList();
        var existing = await dbContext.Tags.Where(t => normalized.Contains(t.Name)).ToListAsync(cancellationToken).ConfigureAwait(false);
        var toCreate = normalized.Except(existing.Select(t => t.Name)).Select(Tag.Create).ToList();
        if (toCreate.Count > 0)
        {
            dbContext.Tags.AddRange(toCreate);
        }
        return [.. existing.Select(t => t.Id), .. toCreate.Select(t => t.Id)];
    }
}
```

(register `IProxySecretProtector` for this handler's constructor by binding it to `ProxyPasswordProtector` explicitly — see Step 7 below; the two protectors from Task 5 are kept as distinct concrete types precisely so they can't be swapped by accident, so this handler must be constructed with `ProxyPasswordProtector`, not the shared interface resolved generically)

```csharp
// src/Modules/Proxies/Modules.Proxies/Features/v1/ManualProxies/UpdateManualProxy/UpdateManualProxyCommandValidator.cs
using FluentValidation;
using FSH.Modules.Proxies.Contracts.v1.ManualProxies;

namespace FSH.Modules.Proxies.Features.v1.ManualProxies.UpdateManualProxy;

public sealed class UpdateManualProxyCommandValidator : AbstractValidator<UpdateManualProxyCommand>
{
    public UpdateManualProxyCommandValidator()
    {
        RuleFor(x => x.Id).NotEmpty();
        RuleFor(x => x.Host).NotEmpty().MaximumLength(255);
        RuleFor(x => x.Port).InclusiveBetween(1, 65535);
        RuleForEach(x => x.TagNames).NotEmpty().MaximumLength(128);
    }
}
```

```csharp
// src/Modules/Proxies/Modules.Proxies/Features/v1/ManualProxies/UpdateManualProxy/UpdateManualProxyCommandHandler.cs
using FSH.Framework.Core.Exceptions;
using FSH.Modules.Proxies.Contracts.v1.ManualProxies;
using FSH.Modules.Proxies.Data;
using FSH.Modules.Proxies.Domain;
using FSH.Modules.Proxies.Features.v1.ManualProxies.CreateManualProxy;
using FSH.Modules.Proxies.Services;
using Mediator;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FSH.Modules.Proxies.Features.v1.ManualProxies.UpdateManualProxy;

public sealed class UpdateManualProxyCommandHandler(
    ProxiesDbContext dbContext, [FromKeyedServices("proxy-password")] IProxySecretProtector proxyPasswordProtector)
    : ICommandHandler<UpdateManualProxyCommand>
{
    public async ValueTask<Unit> Handle(UpdateManualProxyCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var proxy = await dbContext.Proxies.Include(x => x.TagAssignments)
            .FirstOrDefaultAsync(x => x.Id == command.Id && x.ProviderAccountId == ManualProviderAccount.Id, cancellationToken).ConfigureAwait(false)
            ?? throw new NotFoundException($"Manual proxy {command.Id} not found.");

        string? protectedPassword = string.IsNullOrWhiteSpace(command.PlaintextPassword)
            ? null : proxyPasswordProtector.Protect(command.PlaintextPassword);

        var newTagIds = await CreateManualProxyCommandHandler.ResolveTagIdsAsync(dbContext, command.TagNames, cancellationToken).ConfigureAwait(false);
        foreach (var tagId in proxy.TagAssignments.Select(a => a.TagId).Except(newTagIds).ToList())
        {
            proxy.UnassignTag(tagId);
        }
        foreach (var tagId in newTagIds)
        {
            proxy.AssignTag(tagId);
        }

        // Host/port/protocol/username/password are re-created via the Domain layer's private
        // setters not being reachable here — Proxy needs an explicit Update method; add it to
        // src/Modules/Proxies/Modules.Proxies/Domain/Proxy.cs alongside SetStatus/MarkRenewed:
        //   public void UpdateConnection(string host, int port, ProxyProtocol protocol, string? username, string? protectedPassword)
        //   { Host = host.Trim(); Port = port; Protocol = protocol; Username = username; ProtectedPassword = protectedPassword; }
        proxy.UpdateConnection(command.Host, command.Port, command.Protocol, command.Username, protectedPassword);

        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return Unit.Value;
    }
}
```

Add the `UpdateConnection` method to `Proxy` (Task 2's file) now:

```csharp
// add to src/Modules/Proxies/Modules.Proxies/Domain/Proxy.cs
public void UpdateConnection(string host, int port, ProxyProtocol protocol, string? username, string? protectedPassword)
{
    ArgumentException.ThrowIfNullOrWhiteSpace(host);
    Host = host.Trim();
    Port = port;
    Protocol = protocol;
    Username = username;
    ProtectedPassword = protectedPassword;
}
```

```csharp
// src/Modules/Proxies/Modules.Proxies/Features/v1/ManualProxies/DeleteManualProxy/DeleteManualProxyCommandValidator.cs
using FluentValidation;
using FSH.Modules.Proxies.Contracts.v1.ManualProxies;

namespace FSH.Modules.Proxies.Features.v1.ManualProxies.DeleteManualProxy;

public sealed class DeleteManualProxyCommandValidator : AbstractValidator<DeleteManualProxyCommand>
{
    public DeleteManualProxyCommandValidator() => RuleFor(x => x.Id).NotEmpty();
}
```

```csharp
// src/Modules/Proxies/Modules.Proxies/Features/v1/ManualProxies/DeleteManualProxy/DeleteManualProxyCommandHandler.cs
using FSH.Framework.Core.Exceptions;
using FSH.Modules.Proxies.Contracts.v1.ManualProxies;
using FSH.Modules.Proxies.Data;
using FSH.Modules.Proxies.Domain;
using Mediator;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.Proxies.Features.v1.ManualProxies.DeleteManualProxy;

public sealed class DeleteManualProxyCommandHandler(ProxiesDbContext dbContext) : ICommandHandler<DeleteManualProxyCommand>
{
    public async ValueTask<Unit> Handle(DeleteManualProxyCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var proxy = await dbContext.Proxies
            .FirstOrDefaultAsync(x => x.Id == command.Id && x.ProviderAccountId == ManualProviderAccount.Id, cancellationToken).ConfigureAwait(false)
            ?? throw new NotFoundException($"Manual proxy {command.Id} not found.");

        dbContext.Proxies.Remove(proxy);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return Unit.Value;
    }
}
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test src/Tests/Proxies.Tests --filter "FullyQualifiedName~ManualProxy"`
Expected: PASS, 6 tests.

- [ ] **Step 6: Implement the endpoints**

```csharp
// src/Modules/Proxies/Modules.Proxies/Features/v1/ManualProxies/CreateManualProxy/CreateManualProxyEndpoint.cs
using FSH.Framework.Shared.Identity.Authorization;
using FSH.Modules.Proxies.Contracts.Authorization;
using FSH.Modules.Proxies.Contracts.v1.ManualProxies;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FSH.Modules.Proxies.Features.v1.ManualProxies.CreateManualProxy;

public static class CreateManualProxyEndpoint
{
    internal static RouteHandlerBuilder MapCreateManualProxyEndpoint(this IEndpointRouteBuilder endpoints)
    {
        return endpoints.MapPost("/manual-proxies",
                async (CreateManualProxyCommand command, IMediator mediator, CancellationToken ct) =>
                    Results.Ok(await mediator.Send(command, ct)))
            .WithName("CreateManualProxy")
            .WithSummary("Create a manually-hosted proxy")
            .RequirePermission(ProxiesPermissions.ManualProxies.Create);
    }
}
```

```csharp
// src/Modules/Proxies/Modules.Proxies/Features/v1/ManualProxies/UpdateManualProxy/UpdateManualProxyEndpoint.cs
using FSH.Framework.Shared.Identity.Authorization;
using FSH.Modules.Proxies.Contracts.Authorization;
using FSH.Modules.Proxies.Contracts.v1.ManualProxies;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FSH.Modules.Proxies.Features.v1.ManualProxies.UpdateManualProxy;

public static class UpdateManualProxyEndpoint
{
    internal static RouteHandlerBuilder MapUpdateManualProxyEndpoint(this IEndpointRouteBuilder endpoints)
    {
        return endpoints.MapPut("/manual-proxies/{id:guid}",
                async (Guid id, UpdateManualProxyBody body, IMediator mediator, CancellationToken ct) =>
                {
                    await mediator.Send(new UpdateManualProxyCommand(id, body.Host, body.Port, body.Protocol, body.Username, body.PlaintextPassword, body.TagNames), ct);
                    return Results.NoContent();
                })
            .WithName("UpdateManualProxy")
            .WithSummary("Update a manually-hosted proxy")
            .RequirePermission(ProxiesPermissions.ManualProxies.Update);
    }

    internal sealed record UpdateManualProxyBody(
        string Host, int Port, FSH.Modules.Proxies.Contracts.ProxyProtocol Protocol,
        string? Username, string? PlaintextPassword, IReadOnlyList<string> TagNames);
}
```

```csharp
// src/Modules/Proxies/Modules.Proxies/Features/v1/ManualProxies/DeleteManualProxy/DeleteManualProxyEndpoint.cs
using FSH.Framework.Shared.Identity.Authorization;
using FSH.Modules.Proxies.Contracts.Authorization;
using FSH.Modules.Proxies.Contracts.v1.ManualProxies;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FSH.Modules.Proxies.Features.v1.ManualProxies.DeleteManualProxy;

public static class DeleteManualProxyEndpoint
{
    internal static RouteHandlerBuilder MapDeleteManualProxyEndpoint(this IEndpointRouteBuilder endpoints)
    {
        return endpoints.MapDelete("/manual-proxies/{id:guid}",
                async (Guid id, IMediator mediator, CancellationToken ct) =>
                {
                    await mediator.Send(new DeleteManualProxyCommand(id), ct);
                    return Results.NoContent();
                })
            .WithName("DeleteManualProxy")
            .WithSummary("Delete a manually-hosted proxy")
            .RequirePermission(ProxiesPermissions.ManualProxies.Delete);
    }
}
```

- [ ] **Step 7: Wire endpoints and the concrete-protector DI bindings**

```csharp
// inside ProxiesModule.MapEndpoints, after the ProviderAccounts group
group.MapCreateManualProxyEndpoint();
group.MapUpdateManualProxyEndpoint();
group.MapDeleteManualProxyEndpoint();
```

The Create/Update handlers above take `IProxySecretProtector` by constructor parameter (via `[FromKeyedServices("proxy-password")]`, already shown in Step 4's handler code) but must each resolve to the `ProxyPasswordProtector` concrete instance specifically — not `ProviderAccountCredentialProtector`, which Task 7's handlers use. **Do not** replace Task 5's plain `AddSingleton<ProviderAccountCredentialProtector>()`/`AddSingleton<ProxyPasswordProtector>()` registrations — Task 7's handlers and Task 16/19/20's services all depend on being able to inject those two concrete types directly (unkeyed), and removing the unkeyed registration would break every one of them. Instead, *add* two keyed registrations that delegate to the same singleton instances, purely so this task's handlers can resolve the shared interface unambiguously:

```csharp
// add to ProxiesModule.ConfigureServices, alongside (not instead of) Task 5's two AddSingleton calls
builder.Services.AddKeyedSingleton<IProxySecretProtector>("provider-account", (sp, _) => sp.GetRequiredService<ProviderAccountCredentialProtector>());
builder.Services.AddKeyedSingleton<IProxySecretProtector>("proxy-password", (sp, _) => sp.GetRequiredService<ProxyPasswordProtector>());
```

No change is needed to Task 7's `CreateProviderAccountCommandHandler`/`UpdateProviderAccountCommandHandler` (they keep taking the concrete `ProviderAccountCredentialProtector` type unchanged) or to Task 16/19/20's services (same).

- [ ] **Step 8: Build and run the full Proxies test suite**

Run: `dotnet build src/FS.Proxy.slnx && dotnet test src/Tests/Proxies.Tests`
Expected: PASS.

- [ ] **Step 9: Commit**

```bash
git add src/Modules/Proxies src/Tests/Proxies.Tests
git commit -m "feat(proxies): add Manual proxy CRUD and seed the well-known Manual provider account"
```

### Task 9: Tag CRUD

**Files:**
- Create: `src/Modules/Proxies/Modules.Proxies.Contracts/Dtos/TagDto.cs`
- Create: `src/Modules/Proxies/Modules.Proxies.Contracts/v1/Tags/CreateTagCommand.cs`
- Create: `src/Modules/Proxies/Modules.Proxies.Contracts/v1/Tags/DeleteTagCommand.cs`
- Create: `src/Modules/Proxies/Modules.Proxies.Contracts/v1/Tags/ListTagsQuery.cs`
- Create: `src/Modules/Proxies/Modules.Proxies/Features/v1/Tags/CreateTag/CreateTagCommandValidator.cs`
- Create: `src/Modules/Proxies/Modules.Proxies/Features/v1/Tags/CreateTag/CreateTagCommandHandler.cs`
- Create: `src/Modules/Proxies/Modules.Proxies/Features/v1/Tags/CreateTag/CreateTagEndpoint.cs`
- Create: `src/Modules/Proxies/Modules.Proxies/Features/v1/Tags/DeleteTag/DeleteTagCommandValidator.cs`
- Create: `src/Modules/Proxies/Modules.Proxies/Features/v1/Tags/DeleteTag/DeleteTagCommandHandler.cs`
- Create: `src/Modules/Proxies/Modules.Proxies/Features/v1/Tags/DeleteTag/DeleteTagEndpoint.cs`
- Create: `src/Modules/Proxies/Modules.Proxies/Features/v1/Tags/ListTags/ListTagsQueryHandler.cs`
- Create: `src/Modules/Proxies/Modules.Proxies/Features/v1/Tags/ListTags/ListTagsEndpoint.cs`
- Modify: `src/Modules/Proxies/Modules.Proxies/ProxiesModule.cs`
- Test: `src/Tests/Proxies.Tests/Handlers/TagHandlerTests.cs`

**Interfaces:**
- Produces: `TagDto(Guid Id, string Name, Guid? PolicyProfileId, string? PolicyProfileName, Guid? HealthCheckTargetId, string? HealthCheckTargetName)` — the `PolicyProfileId`/`HealthCheckTargetId` columns start `null` here and are populated by Tasks 10/11's assign endpoints. `ListTagsQuery : IQuery<IReadOnlyList<TagDto>>` (unpaged — tag counts are small, and the admin UI's tag picker (Task 26) needs the full list for autocomplete, not a page of it).

- [ ] **Step 1: Define the DTO and commands**

```csharp
// src/Modules/Proxies/Modules.Proxies.Contracts/Dtos/TagDto.cs
namespace FSH.Modules.Proxies.Contracts.Dtos;

public sealed record TagDto(Guid Id, string Name, Guid? PolicyProfileId, string? PolicyProfileName, Guid? HealthCheckTargetId, string? HealthCheckTargetName);
```

```csharp
// src/Modules/Proxies/Modules.Proxies.Contracts/v1/Tags/CreateTagCommand.cs
using Mediator;

namespace FSH.Modules.Proxies.Contracts.v1.Tags;

public sealed record CreateTagCommand(string Name) : ICommand<Guid>;
```

```csharp
// src/Modules/Proxies/Modules.Proxies.Contracts/v1/Tags/DeleteTagCommand.cs
using Mediator;

namespace FSH.Modules.Proxies.Contracts.v1.Tags;

public sealed record DeleteTagCommand(Guid Id) : ICommand;
```

```csharp
// src/Modules/Proxies/Modules.Proxies.Contracts/v1/Tags/ListTagsQuery.cs
using FSH.Modules.Proxies.Contracts.Dtos;
using Mediator;

namespace FSH.Modules.Proxies.Contracts.v1.Tags;

public sealed record ListTagsQuery : IQuery<IReadOnlyList<TagDto>>;
```

- [ ] **Step 2: Write the failing handler test**

```csharp
// src/Tests/Proxies.Tests/Handlers/TagHandlerTests.cs
using FSH.Modules.Proxies.Contracts.v1.Tags;
using FSH.Modules.Proxies.Domain;
using FSH.Modules.Proxies.Features.v1.Tags.CreateTag;
using FSH.Modules.Proxies.Features.v1.Tags.DeleteTag;
using FSH.Modules.Proxies.Features.v1.Tags.ListTags;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using Xunit;

namespace Proxies.Tests.Handlers;

public sealed class TagHandlerTests
{
    private static Proxies.Tests.TestProxiesDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<FSH.Modules.Proxies.Data.ProxiesDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    [Fact]
    public async Task Create_Should_NormalizeName()
    {
        await using var db = CreateDb();
        var sut = new CreateTagCommandHandler(db);

        var id = await sut.Handle(new CreateTagCommand("  PAIS:CL  "), CancellationToken.None);

        (await db.Tags.SingleAsync(x => x.Id == id)).Name.ShouldBe("pais:cl");
    }

    [Fact]
    public async Task Delete_Should_RemoveTag()
    {
        await using var db = CreateDb();
        var tag = Tag.Create("pais:pe");
        db.Tags.Add(tag);
        await db.SaveChangesAsync();
        var sut = new DeleteTagCommandHandler(db);

        await sut.Handle(new DeleteTagCommand(tag.Id), CancellationToken.None);

        (await db.Tags.AnyAsync(x => x.Id == tag.Id)).ShouldBeFalse();
    }

    [Fact]
    public async Task List_Should_ReturnAllTags_OrderedByName()
    {
        await using var db = CreateDb();
        db.Tags.AddRange(Tag.Create("zeta"), Tag.Create("alpha"));
        await db.SaveChangesAsync();
        var sut = new ListTagsQueryHandler(db);

        var result = await sut.Handle(new ListTagsQuery(), CancellationToken.None);

        result.Select(x => x.Name).ShouldBe(["alpha", "zeta"]);
    }
}
```

- [ ] **Step 3: Run to verify failure, then implement**

Run: `dotnet test src/Tests/Proxies.Tests --filter "FullyQualifiedName~TagHandlerTests"` — expect compile failure.

```csharp
// src/Modules/Proxies/Modules.Proxies/Features/v1/Tags/CreateTag/CreateTagCommandValidator.cs
using FluentValidation;
using FSH.Modules.Proxies.Contracts.v1.Tags;

namespace FSH.Modules.Proxies.Features.v1.Tags.CreateTag;

public sealed class CreateTagCommandValidator : AbstractValidator<CreateTagCommand>
{
    public CreateTagCommandValidator() => RuleFor(x => x.Name).NotEmpty().MaximumLength(128);
}
```

```csharp
// src/Modules/Proxies/Modules.Proxies/Features/v1/Tags/CreateTag/CreateTagCommandHandler.cs
using FSH.Modules.Proxies.Contracts.v1.Tags;
using FSH.Modules.Proxies.Data;
using FSH.Modules.Proxies.Domain;
using Mediator;

namespace FSH.Modules.Proxies.Features.v1.Tags.CreateTag;

public sealed class CreateTagCommandHandler(ProxiesDbContext dbContext) : ICommandHandler<CreateTagCommand, Guid>
{
    public async ValueTask<Guid> Handle(CreateTagCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var tag = Tag.Create(command.Name);
        dbContext.Tags.Add(tag);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return tag.Id;
    }
}
```

```csharp
// src/Modules/Proxies/Modules.Proxies/Features/v1/Tags/DeleteTag/DeleteTagCommandValidator.cs
using FluentValidation;
using FSH.Modules.Proxies.Contracts.v1.Tags;

namespace FSH.Modules.Proxies.Features.v1.Tags.DeleteTag;

public sealed class DeleteTagCommandValidator : AbstractValidator<DeleteTagCommand>
{
    public DeleteTagCommandValidator() => RuleFor(x => x.Id).NotEmpty();
}
```

```csharp
// src/Modules/Proxies/Modules.Proxies/Features/v1/Tags/DeleteTag/DeleteTagCommandHandler.cs
using FSH.Framework.Core.Exceptions;
using FSH.Modules.Proxies.Contracts.v1.Tags;
using FSH.Modules.Proxies.Data;
using Mediator;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.Proxies.Features.v1.Tags.DeleteTag;

public sealed class DeleteTagCommandHandler(ProxiesDbContext dbContext) : ICommandHandler<DeleteTagCommand>
{
    public async ValueTask<Unit> Handle(DeleteTagCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var tag = await dbContext.Tags.FirstOrDefaultAsync(x => x.Id == command.Id, cancellationToken).ConfigureAwait(false)
            ?? throw new NotFoundException($"Tag {command.Id} not found.");
        dbContext.Tags.Remove(tag);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return Unit.Value;
    }
}
```

```csharp
// src/Modules/Proxies/Modules.Proxies/Features/v1/Tags/ListTags/ListTagsQueryHandler.cs
using FSH.Modules.Proxies.Contracts.Dtos;
using FSH.Modules.Proxies.Contracts.v1.Tags;
using FSH.Modules.Proxies.Data;
using Mediator;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.Proxies.Features.v1.Tags.ListTags;

public sealed class ListTagsQueryHandler(ProxiesDbContext dbContext) : IQueryHandler<ListTagsQuery, IReadOnlyList<TagDto>>
{
    public async ValueTask<IReadOnlyList<TagDto>> Handle(ListTagsQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var policyAssignments = await dbContext.Set<Domain.TagPolicyAssignment>().AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
        var targetAssignments = await dbContext.Set<Domain.TagHealthCheckTargetAssignment>().AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
        var policies = await dbContext.PolicyProfiles.AsNoTracking().ToDictionaryAsync(x => x.Id, cancellationToken).ConfigureAwait(false);
        var targets = await dbContext.HealthCheckTargets.AsNoTracking().ToDictionaryAsync(x => x.Id, cancellationToken).ConfigureAwait(false);

        var tags = await dbContext.Tags.AsNoTracking().OrderBy(x => x.Name).ToListAsync(cancellationToken).ConfigureAwait(false);

        return [.. tags.Select(tag =>
        {
            var policyId = policyAssignments.FirstOrDefault(a => a.TagId == tag.Id)?.PolicyProfileId;
            var targetId = targetAssignments.FirstOrDefault(a => a.TagId == tag.Id)?.HealthCheckTargetId;
            return new TagDto(
                tag.Id, tag.Name,
                policyId, policyId is { } pid ? policies.GetValueOrDefault(pid)?.Name : null,
                targetId, targetId is { } tid ? targets.GetValueOrDefault(tid)?.Name : null);
        })];
    }
}
```

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test src/Tests/Proxies.Tests --filter "FullyQualifiedName~TagHandlerTests"`
Expected: PASS, 3 tests.

- [ ] **Step 5: Implement the endpoints**

```csharp
// src/Modules/Proxies/Modules.Proxies/Features/v1/Tags/CreateTag/CreateTagEndpoint.cs
using FSH.Framework.Shared.Identity.Authorization;
using FSH.Modules.Proxies.Contracts.Authorization;
using FSH.Modules.Proxies.Contracts.v1.Tags;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FSH.Modules.Proxies.Features.v1.Tags.CreateTag;

public static class CreateTagEndpoint
{
    internal static RouteHandlerBuilder MapCreateTagEndpoint(this IEndpointRouteBuilder endpoints)
    {
        return endpoints.MapPost("/tags",
                async (CreateTagCommand command, IMediator mediator, CancellationToken ct) => Results.Ok(await mediator.Send(command, ct)))
            .WithName("CreateTag")
            .WithSummary("Create a proxy tag")
            .RequirePermission(ProxiesPermissions.Tags.Create);
    }
}
```

```csharp
// src/Modules/Proxies/Modules.Proxies/Features/v1/Tags/DeleteTag/DeleteTagEndpoint.cs
using FSH.Framework.Shared.Identity.Authorization;
using FSH.Modules.Proxies.Contracts.Authorization;
using FSH.Modules.Proxies.Contracts.v1.Tags;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FSH.Modules.Proxies.Features.v1.Tags.DeleteTag;

public static class DeleteTagEndpoint
{
    internal static RouteHandlerBuilder MapDeleteTagEndpoint(this IEndpointRouteBuilder endpoints)
    {
        return endpoints.MapDelete("/tags/{id:guid}",
                async (Guid id, IMediator mediator, CancellationToken ct) => { await mediator.Send(new DeleteTagCommand(id), ct); return Results.NoContent(); })
            .WithName("DeleteTag")
            .WithSummary("Delete a proxy tag")
            .RequirePermission(ProxiesPermissions.Tags.Delete);
    }
}
```

```csharp
// src/Modules/Proxies/Modules.Proxies/Features/v1/Tags/ListTags/ListTagsEndpoint.cs
using FSH.Framework.Shared.Identity.Authorization;
using FSH.Modules.Proxies.Contracts.Authorization;
using FSH.Modules.Proxies.Contracts.v1.Tags;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FSH.Modules.Proxies.Features.v1.Tags.ListTags;

public static class ListTagsEndpoint
{
    internal static RouteHandlerBuilder MapListTagsEndpoint(this IEndpointRouteBuilder endpoints)
    {
        return endpoints.MapGet("/tags", (IMediator mediator, CancellationToken ct) => mediator.Send(new ListTagsQuery(), ct))
            .WithName("ListTags")
            .WithSummary("List all proxy tags")
            .RequirePermission(ProxiesPermissions.Tags.View);
    }
}
```

- [ ] **Step 6: Wire endpoints, build, test, commit**

```csharp
// inside ProxiesModule.MapEndpoints
group.MapCreateTagEndpoint();
group.MapDeleteTagEndpoint();
group.MapListTagsEndpoint();
```

Run: `dotnet build src/FS.Proxy.slnx && dotnet test src/Tests/Proxies.Tests`
Expected: PASS.

```bash
git add src/Modules/Proxies src/Tests/Proxies.Tests
git commit -m "feat(proxies): add Tag CRUD slice"
```

### Task 10: PolicyProfile CRUD and tag assignment

**Files:**
- Create: `src/Modules/Proxies/Modules.Proxies.Contracts/Dtos/PolicyProfileDto.cs`
- Create: `src/Modules/Proxies/Modules.Proxies.Contracts/PolicyProfileType.cs`
- Create: `src/Modules/Proxies/Modules.Proxies.Contracts/v1/Policies/CreatePolicyProfileCommand.cs`
- Create: `src/Modules/Proxies/Modules.Proxies.Contracts/v1/Policies/UpdatePolicyProfileCommand.cs`
- Create: `src/Modules/Proxies/Modules.Proxies.Contracts/v1/Policies/DeletePolicyProfileCommand.cs`
- Create: `src/Modules/Proxies/Modules.Proxies.Contracts/v1/Policies/AssignPolicyToTagCommand.cs`
- Create: `src/Modules/Proxies/Modules.Proxies.Contracts/v1/Policies/UnassignPolicyFromTagCommand.cs`
- Create: `src/Modules/Proxies/Modules.Proxies.Contracts/v1/Policies/ListPolicyProfilesQuery.cs`
- Create: `src/Modules/Proxies/Modules.Proxies/Features/v1/Policies/CreatePolicyProfile/CreatePolicyProfileCommandValidator.cs`
- Create: `src/Modules/Proxies/Modules.Proxies/Features/v1/Policies/CreatePolicyProfile/CreatePolicyProfileCommandHandler.cs`
- Create: `src/Modules/Proxies/Modules.Proxies/Features/v1/Policies/CreatePolicyProfile/CreatePolicyProfileEndpoint.cs`
- Create: `src/Modules/Proxies/Modules.Proxies/Features/v1/Policies/UpdatePolicyProfile/UpdatePolicyProfileCommandValidator.cs`
- Create: `src/Modules/Proxies/Modules.Proxies/Features/v1/Policies/UpdatePolicyProfile/UpdatePolicyProfileCommandHandler.cs`
- Create: `src/Modules/Proxies/Modules.Proxies/Features/v1/Policies/UpdatePolicyProfile/UpdatePolicyProfileEndpoint.cs`
- Create: `src/Modules/Proxies/Modules.Proxies/Features/v1/Policies/DeletePolicyProfile/DeletePolicyProfileCommandValidator.cs`
- Create: `src/Modules/Proxies/Modules.Proxies/Features/v1/Policies/DeletePolicyProfile/DeletePolicyProfileCommandHandler.cs`
- Create: `src/Modules/Proxies/Modules.Proxies/Features/v1/Policies/DeletePolicyProfile/DeletePolicyProfileEndpoint.cs`
- Create: `src/Modules/Proxies/Modules.Proxies/Features/v1/Policies/AssignPolicyToTag/AssignPolicyToTagCommandValidator.cs`
- Create: `src/Modules/Proxies/Modules.Proxies/Features/v1/Policies/AssignPolicyToTag/AssignPolicyToTagCommandHandler.cs`
- Create: `src/Modules/Proxies/Modules.Proxies/Features/v1/Policies/AssignPolicyToTag/AssignPolicyToTagEndpoint.cs`
- Create: `src/Modules/Proxies/Modules.Proxies/Features/v1/Policies/UnassignPolicyFromTag/UnassignPolicyFromTagCommandValidator.cs`
- Create: `src/Modules/Proxies/Modules.Proxies/Features/v1/Policies/UnassignPolicyFromTag/UnassignPolicyFromTagCommandHandler.cs`
- Create: `src/Modules/Proxies/Modules.Proxies/Features/v1/Policies/UnassignPolicyFromTag/UnassignPolicyFromTagEndpoint.cs`
- Create: `src/Modules/Proxies/Modules.Proxies/Features/v1/Policies/ListPolicyProfiles/ListPolicyProfilesQueryHandler.cs`
- Create: `src/Modules/Proxies/Modules.Proxies/Features/v1/Policies/ListPolicyProfiles/ListPolicyProfilesEndpoint.cs`
- Modify: `src/Modules/Proxies/Modules.Proxies/ProxiesModule.cs`
- Test: `src/Tests/Proxies.Tests/Handlers/PolicyProfileHandlerTests.cs`

**Interfaces:**
- Produces: `PolicyProfileDto(Guid Id, string Name, PolicyProfileType Type, int FailureThreshold, int WindowMinutes, int MinDistinctReporters)`; `AssignPolicyToTagCommand(Guid TagId, Guid PolicyProfileId) : ICommand` (replaces any existing assignment for that tag — enforced by the `TagId`-only PK from Task 2); `UnassignPolicyFromTagCommand(Guid TagId) : ICommand`. Consumed directly by Task 18's policy engine (`TagPolicyAssignment` resolution).

`PolicyProfileType` must live in Contracts (commands reference it) — move the Task 2 declaration:

```csharp
// src/Modules/Proxies/Modules.Proxies.Contracts/PolicyProfileType.cs (new file)
namespace FSH.Modules.Proxies.Contracts;

public enum PolicyProfileType { Manual, AutoDisable, AutoDisableAndRenew }
```

Delete the `enum PolicyProfileType` line from `src/Modules/Proxies/Modules.Proxies/Domain/PolicyProfile.cs` and add `using FSH.Modules.Proxies.Contracts;` there instead.

- [ ] **Step 1: Define the DTO and commands**

```csharp
// src/Modules/Proxies/Modules.Proxies.Contracts/Dtos/PolicyProfileDto.cs
namespace FSH.Modules.Proxies.Contracts.Dtos;

public sealed record PolicyProfileDto(Guid Id, string Name, PolicyProfileType Type, int FailureThreshold, int WindowMinutes, int MinDistinctReporters);
```

```csharp
// src/Modules/Proxies/Modules.Proxies.Contracts/v1/Policies/CreatePolicyProfileCommand.cs
using Mediator;

namespace FSH.Modules.Proxies.Contracts.v1.Policies;

public sealed record CreatePolicyProfileCommand(
    string Name, PolicyProfileType Type, int FailureThreshold, int WindowMinutes, int MinDistinctReporters) : ICommand<Guid>;
```

```csharp
// src/Modules/Proxies/Modules.Proxies.Contracts/v1/Policies/UpdatePolicyProfileCommand.cs
using Mediator;

namespace FSH.Modules.Proxies.Contracts.v1.Policies;

public sealed record UpdatePolicyProfileCommand(
    Guid Id, string Name, PolicyProfileType Type, int FailureThreshold, int WindowMinutes, int MinDistinctReporters) : ICommand;
```

```csharp
// src/Modules/Proxies/Modules.Proxies.Contracts/v1/Policies/DeletePolicyProfileCommand.cs
using Mediator;

namespace FSH.Modules.Proxies.Contracts.v1.Policies;

public sealed record DeletePolicyProfileCommand(Guid Id) : ICommand;
```

```csharp
// src/Modules/Proxies/Modules.Proxies.Contracts/v1/Policies/AssignPolicyToTagCommand.cs
using Mediator;

namespace FSH.Modules.Proxies.Contracts.v1.Policies;

public sealed record AssignPolicyToTagCommand(Guid TagId, Guid PolicyProfileId) : ICommand;
```

```csharp
// src/Modules/Proxies/Modules.Proxies.Contracts/v1/Policies/UnassignPolicyFromTagCommand.cs
using Mediator;

namespace FSH.Modules.Proxies.Contracts.v1.Policies;

public sealed record UnassignPolicyFromTagCommand(Guid TagId) : ICommand;
```

```csharp
// src/Modules/Proxies/Modules.Proxies.Contracts/v1/Policies/ListPolicyProfilesQuery.cs
using FSH.Modules.Proxies.Contracts.Dtos;
using Mediator;

namespace FSH.Modules.Proxies.Contracts.v1.Policies;

public sealed record ListPolicyProfilesQuery : IQuery<IReadOnlyList<PolicyProfileDto>>;
```

- [ ] **Step 2: Write the failing handler tests**

```csharp
// src/Tests/Proxies.Tests/Handlers/PolicyProfileHandlerTests.cs
using FSH.Modules.Proxies.Contracts;
using FSH.Modules.Proxies.Contracts.v1.Policies;
using FSH.Modules.Proxies.Domain;
using FSH.Modules.Proxies.Features.v1.Policies.AssignPolicyToTag;
using FSH.Modules.Proxies.Features.v1.Policies.CreatePolicyProfile;
using FSH.Modules.Proxies.Features.v1.Policies.UnassignPolicyFromTag;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using Xunit;

namespace Proxies.Tests.Handlers;

public sealed class PolicyProfileHandlerTests
{
    private static Proxies.Tests.TestProxiesDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<FSH.Modules.Proxies.Data.ProxiesDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    [Fact]
    public async Task Create_Should_Persist()
    {
        await using var db = CreateDb();
        var sut = new CreatePolicyProfileCommandHandler(db);

        var id = await sut.Handle(new CreatePolicyProfileCommand("critical", PolicyProfileType.AutoDisableAndRenew, 3, 30, 2), CancellationToken.None);

        (await db.PolicyProfiles.SingleAsync(x => x.Id == id)).Type.ShouldBe(PolicyProfileType.AutoDisableAndRenew);
    }

    [Fact]
    public async Task AssignToTag_Should_ReplaceExistingAssignment()
    {
        await using var db = CreateDb();
        var tag = Tag.Create("pais:cl");
        var policyA = PolicyProfile.Create("a", PolicyProfileType.Manual, 1, 1, 1);
        var policyB = PolicyProfile.Create("b", PolicyProfileType.AutoDisable, 3, 30, 2);
        db.Tags.Add(tag);
        db.PolicyProfiles.AddRange(policyA, policyB);
        await db.SaveChangesAsync();
        var sut = new AssignPolicyToTagCommandHandler(db);
        await sut.Handle(new AssignPolicyToTagCommand(tag.Id, policyA.Id), CancellationToken.None);

        await sut.Handle(new AssignPolicyToTagCommand(tag.Id, policyB.Id), CancellationToken.None);

        var assignment = await db.Set<TagPolicyAssignment>().SingleAsync(x => x.TagId == tag.Id);
        assignment.PolicyProfileId.ShouldBe(policyB.Id);
    }

    [Fact]
    public async Task Unassign_Should_RemoveAssignment()
    {
        await using var db = CreateDb();
        var tag = Tag.Create("pais:pe");
        var policy = PolicyProfile.Create("a", PolicyProfileType.Manual, 1, 1, 1);
        db.Tags.Add(tag);
        db.PolicyProfiles.Add(policy);
        db.Set<TagPolicyAssignment>().Add(TagPolicyAssignment.Create(tag.Id, policy.Id));
        await db.SaveChangesAsync();
        var sut = new UnassignPolicyFromTagCommandHandler(db);

        await sut.Handle(new UnassignPolicyFromTagCommand(tag.Id), CancellationToken.None);

        (await db.Set<TagPolicyAssignment>().AnyAsync(x => x.TagId == tag.Id)).ShouldBeFalse();
    }
}
```

- [ ] **Step 3: Run to verify failure, then implement**

Run: `dotnet test src/Tests/Proxies.Tests --filter "FullyQualifiedName~PolicyProfileHandlerTests"` — expect compile failure.

```csharp
// src/Modules/Proxies/Modules.Proxies/Features/v1/Policies/CreatePolicyProfile/CreatePolicyProfileCommandValidator.cs
using FluentValidation;
using FSH.Modules.Proxies.Contracts.v1.Policies;

namespace FSH.Modules.Proxies.Features.v1.Policies.CreatePolicyProfile;

public sealed class CreatePolicyProfileCommandValidator : AbstractValidator<CreatePolicyProfileCommand>
{
    public CreatePolicyProfileCommandValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(128);
        RuleFor(x => x.Type).IsInEnum();
        RuleFor(x => x.FailureThreshold).GreaterThan(0);
        RuleFor(x => x.WindowMinutes).GreaterThan(0);
        RuleFor(x => x.MinDistinctReporters).GreaterThan(0);
    }
}
```

```csharp
// src/Modules/Proxies/Modules.Proxies/Features/v1/Policies/CreatePolicyProfile/CreatePolicyProfileCommandHandler.cs
using FSH.Modules.Proxies.Contracts.v1.Policies;
using FSH.Modules.Proxies.Data;
using FSH.Modules.Proxies.Domain;
using Mediator;

namespace FSH.Modules.Proxies.Features.v1.Policies.CreatePolicyProfile;

public sealed class CreatePolicyProfileCommandHandler(ProxiesDbContext dbContext) : ICommandHandler<CreatePolicyProfileCommand, Guid>
{
    public async ValueTask<Guid> Handle(CreatePolicyProfileCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var profile = PolicyProfile.Create(command.Name, command.Type, command.FailureThreshold, command.WindowMinutes, command.MinDistinctReporters);
        dbContext.PolicyProfiles.Add(profile);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return profile.Id;
    }
}
```

```csharp
// src/Modules/Proxies/Modules.Proxies/Features/v1/Policies/UpdatePolicyProfile/UpdatePolicyProfileCommandValidator.cs
using FluentValidation;
using FSH.Modules.Proxies.Contracts.v1.Policies;

namespace FSH.Modules.Proxies.Features.v1.Policies.UpdatePolicyProfile;

public sealed class UpdatePolicyProfileCommandValidator : AbstractValidator<UpdatePolicyProfileCommand>
{
    public UpdatePolicyProfileCommandValidator()
    {
        RuleFor(x => x.Id).NotEmpty();
        RuleFor(x => x.Name).NotEmpty().MaximumLength(128);
        RuleFor(x => x.Type).IsInEnum();
        RuleFor(x => x.FailureThreshold).GreaterThan(0);
        RuleFor(x => x.WindowMinutes).GreaterThan(0);
        RuleFor(x => x.MinDistinctReporters).GreaterThan(0);
    }
}
```

```csharp
// src/Modules/Proxies/Modules.Proxies/Features/v1/Policies/UpdatePolicyProfile/UpdatePolicyProfileCommandHandler.cs
using FSH.Framework.Core.Exceptions;
using FSH.Modules.Proxies.Contracts.v1.Policies;
using FSH.Modules.Proxies.Data;
using Mediator;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.Proxies.Features.v1.Policies.UpdatePolicyProfile;

public sealed class UpdatePolicyProfileCommandHandler(ProxiesDbContext dbContext) : ICommandHandler<UpdatePolicyProfileCommand>
{
    public async ValueTask<Unit> Handle(UpdatePolicyProfileCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var profile = await dbContext.PolicyProfiles.FirstOrDefaultAsync(x => x.Id == command.Id, cancellationToken).ConfigureAwait(false)
            ?? throw new NotFoundException($"Policy profile {command.Id} not found.");
        profile.Update(command.Name, command.Type, command.FailureThreshold, command.WindowMinutes, command.MinDistinctReporters);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return Unit.Value;
    }
}
```

```csharp
// src/Modules/Proxies/Modules.Proxies/Features/v1/Policies/DeletePolicyProfile/DeletePolicyProfileCommandValidator.cs
using FluentValidation;
using FSH.Modules.Proxies.Contracts.v1.Policies;

namespace FSH.Modules.Proxies.Features.v1.Policies.DeletePolicyProfile;

public sealed class DeletePolicyProfileCommandValidator : AbstractValidator<DeletePolicyProfileCommand>
{
    public DeletePolicyProfileCommandValidator() => RuleFor(x => x.Id).NotEmpty();
}
```

```csharp
// src/Modules/Proxies/Modules.Proxies/Features/v1/Policies/DeletePolicyProfile/DeletePolicyProfileCommandHandler.cs
using FSH.Framework.Core.Exceptions;
using FSH.Modules.Proxies.Contracts.v1.Policies;
using FSH.Modules.Proxies.Data;
using Mediator;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.Proxies.Features.v1.Policies.DeletePolicyProfile;

public sealed class DeletePolicyProfileCommandHandler(ProxiesDbContext dbContext) : ICommandHandler<DeletePolicyProfileCommand>
{
    public async ValueTask<Unit> Handle(DeletePolicyProfileCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var profile = await dbContext.PolicyProfiles.FirstOrDefaultAsync(x => x.Id == command.Id, cancellationToken).ConfigureAwait(false)
            ?? throw new NotFoundException($"Policy profile {command.Id} not found.");
        // Restrict-delete FK (Task 2) means this throws a DbUpdateException if any TagPolicyAssignment
        // still references it — surface that as a 409 rather than a raw 500.
        bool inUse = await dbContext.Set<Domain.TagPolicyAssignment>().AnyAsync(x => x.PolicyProfileId == command.Id, cancellationToken).ConfigureAwait(false);
        if (inUse)
        {
            throw new CustomException("This policy profile is still assigned to at least one tag. Unassign it first.", (IEnumerable<string>?)null, System.Net.HttpStatusCode.Conflict);
        }
        dbContext.PolicyProfiles.Remove(profile);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return Unit.Value;
    }
}
```

```csharp
// src/Modules/Proxies/Modules.Proxies/Features/v1/Policies/AssignPolicyToTag/AssignPolicyToTagCommandValidator.cs
using FluentValidation;
using FSH.Modules.Proxies.Contracts.v1.Policies;

namespace FSH.Modules.Proxies.Features.v1.Policies.AssignPolicyToTag;

public sealed class AssignPolicyToTagCommandValidator : AbstractValidator<AssignPolicyToTagCommand>
{
    public AssignPolicyToTagCommandValidator()
    {
        RuleFor(x => x.TagId).NotEmpty();
        RuleFor(x => x.PolicyProfileId).NotEmpty();
    }
}
```

```csharp
// src/Modules/Proxies/Modules.Proxies/Features/v1/Policies/AssignPolicyToTag/AssignPolicyToTagCommandHandler.cs
using FSH.Framework.Core.Exceptions;
using FSH.Modules.Proxies.Contracts.v1.Policies;
using FSH.Modules.Proxies.Data;
using FSH.Modules.Proxies.Domain;
using Mediator;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.Proxies.Features.v1.Policies.AssignPolicyToTag;

public sealed class AssignPolicyToTagCommandHandler(ProxiesDbContext dbContext) : ICommandHandler<AssignPolicyToTagCommand>
{
    public async ValueTask<Unit> Handle(AssignPolicyToTagCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        bool tagExists = await dbContext.Tags.AnyAsync(x => x.Id == command.TagId, cancellationToken).ConfigureAwait(false);
        if (!tagExists) throw new NotFoundException($"Tag {command.TagId} not found.");
        bool policyExists = await dbContext.PolicyProfiles.AnyAsync(x => x.Id == command.PolicyProfileId, cancellationToken).ConfigureAwait(false);
        if (!policyExists) throw new NotFoundException($"Policy profile {command.PolicyProfileId} not found.");

        var existing = await dbContext.Set<TagPolicyAssignment>().FirstOrDefaultAsync(x => x.TagId == command.TagId, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            dbContext.Set<TagPolicyAssignment>().Remove(existing);
        }
        dbContext.Set<TagPolicyAssignment>().Add(TagPolicyAssignment.Create(command.TagId, command.PolicyProfileId));

        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return Unit.Value;
    }
}
```

```csharp
// src/Modules/Proxies/Modules.Proxies/Features/v1/Policies/UnassignPolicyFromTag/UnassignPolicyFromTagCommandValidator.cs
using FluentValidation;
using FSH.Modules.Proxies.Contracts.v1.Policies;

namespace FSH.Modules.Proxies.Features.v1.Policies.UnassignPolicyFromTag;

public sealed class UnassignPolicyFromTagCommandValidator : AbstractValidator<UnassignPolicyFromTagCommand>
{
    public UnassignPolicyFromTagCommandValidator() => RuleFor(x => x.TagId).NotEmpty();
}
```

```csharp
// src/Modules/Proxies/Modules.Proxies/Features/v1/Policies/UnassignPolicyFromTag/UnassignPolicyFromTagCommandHandler.cs
using FSH.Modules.Proxies.Contracts.v1.Policies;
using FSH.Modules.Proxies.Data;
using FSH.Modules.Proxies.Domain;
using Mediator;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.Proxies.Features.v1.Policies.UnassignPolicyFromTag;

public sealed class UnassignPolicyFromTagCommandHandler(ProxiesDbContext dbContext) : ICommandHandler<UnassignPolicyFromTagCommand>
{
    public async ValueTask<Unit> Handle(UnassignPolicyFromTagCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var existing = await dbContext.Set<TagPolicyAssignment>().FirstOrDefaultAsync(x => x.TagId == command.TagId, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            dbContext.Set<TagPolicyAssignment>().Remove(existing);
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        return Unit.Value;
    }
}
```

```csharp
// src/Modules/Proxies/Modules.Proxies/Features/v1/Policies/ListPolicyProfiles/ListPolicyProfilesQueryHandler.cs
using FSH.Modules.Proxies.Contracts.Dtos;
using FSH.Modules.Proxies.Contracts.v1.Policies;
using FSH.Modules.Proxies.Data;
using Mediator;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.Proxies.Features.v1.Policies.ListPolicyProfiles;

public sealed class ListPolicyProfilesQueryHandler(ProxiesDbContext dbContext) : IQueryHandler<ListPolicyProfilesQuery, IReadOnlyList<PolicyProfileDto>>
{
    public async ValueTask<IReadOnlyList<PolicyProfileDto>> Handle(ListPolicyProfilesQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        return await dbContext.PolicyProfiles.AsNoTracking().OrderBy(x => x.Name)
            .Select(x => new PolicyProfileDto(x.Id, x.Name, x.Type, x.FailureThreshold, x.WindowMinutes, x.MinDistinctReporters))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
    }
}
```

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test src/Tests/Proxies.Tests --filter "FullyQualifiedName~PolicyProfileHandlerTests"`
Expected: PASS, 3 tests.

- [ ] **Step 5: Implement the endpoints (same shape as Tasks 7–9 — `MapPost("/policies", ...)`, `MapPut("/policies/{id:guid}", ...)`, `MapDelete("/policies/{id:guid}", ...)`, `MapPost("/tags/{tagId:guid}/policy/{policyProfileId:guid}", ...)` for assign, `MapDelete("/tags/{tagId:guid}/policy", ...)` for unassign, `MapGet("/policies", ...)` for list, each `.RequirePermission(ProxiesPermissions.Policies.*)`)**

```csharp
// src/Modules/Proxies/Modules.Proxies/Features/v1/Policies/CreatePolicyProfile/CreatePolicyProfileEndpoint.cs
using FSH.Framework.Shared.Identity.Authorization;
using FSH.Modules.Proxies.Contracts.Authorization;
using FSH.Modules.Proxies.Contracts.v1.Policies;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FSH.Modules.Proxies.Features.v1.Policies.CreatePolicyProfile;

public static class CreatePolicyProfileEndpoint
{
    internal static RouteHandlerBuilder MapCreatePolicyProfileEndpoint(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapPost("/policies", async (CreatePolicyProfileCommand command, IMediator mediator, CancellationToken ct) => Results.Ok(await mediator.Send(command, ct)))
            .WithName("CreatePolicyProfile").WithSummary("Create a policy profile")
            .RequirePermission(ProxiesPermissions.Policies.Create);
}
```

```csharp
// src/Modules/Proxies/Modules.Proxies/Features/v1/Policies/UpdatePolicyProfile/UpdatePolicyProfileEndpoint.cs
using FSH.Framework.Shared.Identity.Authorization;
using FSH.Modules.Proxies.Contracts.Authorization;
using FSH.Modules.Proxies.Contracts;
using FSH.Modules.Proxies.Contracts.v1.Policies;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FSH.Modules.Proxies.Features.v1.Policies.UpdatePolicyProfile;

public static class UpdatePolicyProfileEndpoint
{
    internal static RouteHandlerBuilder MapUpdatePolicyProfileEndpoint(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapPut("/policies/{id:guid}", async (Guid id, UpdatePolicyProfileBody body, IMediator mediator, CancellationToken ct) =>
            {
                await mediator.Send(new UpdatePolicyProfileCommand(id, body.Name, body.Type, body.FailureThreshold, body.WindowMinutes, body.MinDistinctReporters), ct);
                return Results.NoContent();
            })
            .WithName("UpdatePolicyProfile").WithSummary("Update a policy profile")
            .RequirePermission(ProxiesPermissions.Policies.Update);

    internal sealed record UpdatePolicyProfileBody(string Name, PolicyProfileType Type, int FailureThreshold, int WindowMinutes, int MinDistinctReporters);
}
```

```csharp
// src/Modules/Proxies/Modules.Proxies/Features/v1/Policies/DeletePolicyProfile/DeletePolicyProfileEndpoint.cs
using FSH.Framework.Shared.Identity.Authorization;
using FSH.Modules.Proxies.Contracts.Authorization;
using FSH.Modules.Proxies.Contracts.v1.Policies;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FSH.Modules.Proxies.Features.v1.Policies.DeletePolicyProfile;

public static class DeletePolicyProfileEndpoint
{
    internal static RouteHandlerBuilder MapDeletePolicyProfileEndpoint(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapDelete("/policies/{id:guid}", async (Guid id, IMediator mediator, CancellationToken ct) => { await mediator.Send(new DeletePolicyProfileCommand(id), ct); return Results.NoContent(); })
            .WithName("DeletePolicyProfile").WithSummary("Delete a policy profile")
            .RequirePermission(ProxiesPermissions.Policies.Delete);
}
```

```csharp
// src/Modules/Proxies/Modules.Proxies/Features/v1/Policies/AssignPolicyToTag/AssignPolicyToTagEndpoint.cs
using FSH.Framework.Shared.Identity.Authorization;
using FSH.Modules.Proxies.Contracts.Authorization;
using FSH.Modules.Proxies.Contracts.v1.Policies;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FSH.Modules.Proxies.Features.v1.Policies.AssignPolicyToTag;

public static class AssignPolicyToTagEndpoint
{
    internal static RouteHandlerBuilder MapAssignPolicyToTagEndpoint(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapPost("/tags/{tagId:guid}/policy/{policyProfileId:guid}", async (Guid tagId, Guid policyProfileId, IMediator mediator, CancellationToken ct) =>
            { await mediator.Send(new AssignPolicyToTagCommand(tagId, policyProfileId), ct); return Results.NoContent(); })
            .WithName("AssignPolicyToTag").WithSummary("Assign a policy profile to a tag")
            .RequirePermission(ProxiesPermissions.Policies.Update);
}
```

```csharp
// src/Modules/Proxies/Modules.Proxies/Features/v1/Policies/UnassignPolicyFromTag/UnassignPolicyFromTagEndpoint.cs
using FSH.Framework.Shared.Identity.Authorization;
using FSH.Modules.Proxies.Contracts.Authorization;
using FSH.Modules.Proxies.Contracts.v1.Policies;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FSH.Modules.Proxies.Features.v1.Policies.UnassignPolicyFromTag;

public static class UnassignPolicyFromTagEndpoint
{
    internal static RouteHandlerBuilder MapUnassignPolicyFromTagEndpoint(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapDelete("/tags/{tagId:guid}/policy", async (Guid tagId, IMediator mediator, CancellationToken ct) =>
            { await mediator.Send(new UnassignPolicyFromTagCommand(tagId), ct); return Results.NoContent(); })
            .WithName("UnassignPolicyFromTag").WithSummary("Unassign the policy profile from a tag")
            .RequirePermission(ProxiesPermissions.Policies.Update);
}
```

```csharp
// src/Modules/Proxies/Modules.Proxies/Features/v1/Policies/ListPolicyProfiles/ListPolicyProfilesEndpoint.cs
using FSH.Framework.Shared.Identity.Authorization;
using FSH.Modules.Proxies.Contracts.Authorization;
using FSH.Modules.Proxies.Contracts.v1.Policies;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FSH.Modules.Proxies.Features.v1.Policies.ListPolicyProfiles;

public static class ListPolicyProfilesEndpoint
{
    internal static RouteHandlerBuilder MapListPolicyProfilesEndpoint(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapGet("/policies", (IMediator mediator, CancellationToken ct) => mediator.Send(new ListPolicyProfilesQuery(), ct))
            .WithName("ListPolicyProfiles").WithSummary("List policy profiles")
            .RequirePermission(ProxiesPermissions.Policies.View);
}
```

- [ ] **Step 6: Wire, build, test, commit**

```csharp
// inside ProxiesModule.MapEndpoints
group.MapCreatePolicyProfileEndpoint();
group.MapUpdatePolicyProfileEndpoint();
group.MapDeletePolicyProfileEndpoint();
group.MapAssignPolicyToTagEndpoint();
group.MapUnassignPolicyFromTagEndpoint();
group.MapListPolicyProfilesEndpoint();
```

Run: `dotnet build src/FS.Proxy.slnx && dotnet test src/Tests/Proxies.Tests`
Expected: PASS.

```bash
git add src/Modules/Proxies src/Tests/Proxies.Tests
git commit -m "feat(proxies): add PolicyProfile CRUD and tag assignment"
```

### Task 11: HealthCheckTarget CRUD and tag assignment

Structurally identical to Task 10 (a CRUD aggregate plus a "replace the tag's single assignment" command), applied to `HealthCheckTarget`/`TagHealthCheckTargetAssignment` instead of `PolicyProfile`/`TagPolicyAssignment`.

**Files:**
- Create: `src/Modules/Proxies/Modules.Proxies.Contracts/Dtos/HealthCheckTargetDto.cs`
- Create: `src/Modules/Proxies/Modules.Proxies.Contracts/v1/HealthCheckTargets/CreateHealthCheckTargetCommand.cs`
- Create: `src/Modules/Proxies/Modules.Proxies.Contracts/v1/HealthCheckTargets/UpdateHealthCheckTargetCommand.cs`
- Create: `src/Modules/Proxies/Modules.Proxies.Contracts/v1/HealthCheckTargets/DeleteHealthCheckTargetCommand.cs`
- Create: `src/Modules/Proxies/Modules.Proxies.Contracts/v1/HealthCheckTargets/AssignHealthCheckTargetToTagCommand.cs`
- Create: `src/Modules/Proxies/Modules.Proxies.Contracts/v1/HealthCheckTargets/UnassignHealthCheckTargetFromTagCommand.cs`
- Create: `src/Modules/Proxies/Modules.Proxies.Contracts/v1/HealthCheckTargets/ListHealthCheckTargetsQuery.cs`
- Create: `src/Modules/Proxies/Modules.Proxies/Features/v1/HealthCheckTargets/CreateHealthCheckTarget/CreateHealthCheckTargetCommandValidator.cs`
- Create: `src/Modules/Proxies/Modules.Proxies/Features/v1/HealthCheckTargets/CreateHealthCheckTarget/CreateHealthCheckTargetCommandHandler.cs`
- Create: `src/Modules/Proxies/Modules.Proxies/Features/v1/HealthCheckTargets/CreateHealthCheckTarget/CreateHealthCheckTargetEndpoint.cs`
- Create: `src/Modules/Proxies/Modules.Proxies/Features/v1/HealthCheckTargets/UpdateHealthCheckTarget/UpdateHealthCheckTargetCommandValidator.cs`
- Create: `src/Modules/Proxies/Modules.Proxies/Features/v1/HealthCheckTargets/UpdateHealthCheckTarget/UpdateHealthCheckTargetCommandHandler.cs`
- Create: `src/Modules/Proxies/Modules.Proxies/Features/v1/HealthCheckTargets/UpdateHealthCheckTarget/UpdateHealthCheckTargetEndpoint.cs`
- Create: `src/Modules/Proxies/Modules.Proxies/Features/v1/HealthCheckTargets/DeleteHealthCheckTarget/DeleteHealthCheckTargetCommandValidator.cs`
- Create: `src/Modules/Proxies/Modules.Proxies/Features/v1/HealthCheckTargets/DeleteHealthCheckTarget/DeleteHealthCheckTargetCommandHandler.cs`
- Create: `src/Modules/Proxies/Modules.Proxies/Features/v1/HealthCheckTargets/DeleteHealthCheckTarget/DeleteHealthCheckTargetEndpoint.cs`
- Create: `src/Modules/Proxies/Modules.Proxies/Features/v1/HealthCheckTargets/AssignHealthCheckTargetToTag/AssignHealthCheckTargetToTagCommandValidator.cs`
- Create: `src/Modules/Proxies/Modules.Proxies/Features/v1/HealthCheckTargets/AssignHealthCheckTargetToTag/AssignHealthCheckTargetToTagCommandHandler.cs`
- Create: `src/Modules/Proxies/Modules.Proxies/Features/v1/HealthCheckTargets/AssignHealthCheckTargetToTag/AssignHealthCheckTargetToTagEndpoint.cs`
- Create: `src/Modules/Proxies/Modules.Proxies/Features/v1/HealthCheckTargets/UnassignHealthCheckTargetFromTag/UnassignHealthCheckTargetFromTagCommandValidator.cs`
- Create: `src/Modules/Proxies/Modules.Proxies/Features/v1/HealthCheckTargets/UnassignHealthCheckTargetFromTag/UnassignHealthCheckTargetFromTagCommandHandler.cs`
- Create: `src/Modules/Proxies/Modules.Proxies/Features/v1/HealthCheckTargets/UnassignHealthCheckTargetFromTag/UnassignHealthCheckTargetFromTagEndpoint.cs`
- Create: `src/Modules/Proxies/Modules.Proxies/Features/v1/HealthCheckTargets/ListHealthCheckTargets/ListHealthCheckTargetsQueryHandler.cs`
- Create: `src/Modules/Proxies/Modules.Proxies/Features/v1/HealthCheckTargets/ListHealthCheckTargets/ListHealthCheckTargetsEndpoint.cs`
- Modify: `src/Modules/Proxies/Modules.Proxies/ProxiesModule.cs`
- Test: `src/Tests/Proxies.Tests/Handlers/HealthCheckTargetHandlerTests.cs`

**Interfaces:**
- Produces: `HealthCheckTargetDto(Guid Id, string Name, string TestUrl, int? ExpectedStatusCode, string? ExpectedBodyKeyword, int TimeoutMs)`. Consumed directly by Task 20's target-resolution service.

- [ ] **Step 1: Define the DTO and commands**

```csharp
// src/Modules/Proxies/Modules.Proxies.Contracts/Dtos/HealthCheckTargetDto.cs
namespace FSH.Modules.Proxies.Contracts.Dtos;

public sealed record HealthCheckTargetDto(Guid Id, string Name, string TestUrl, int? ExpectedStatusCode, string? ExpectedBodyKeyword, int TimeoutMs);
```

```csharp
// src/Modules/Proxies/Modules.Proxies.Contracts/v1/HealthCheckTargets/CreateHealthCheckTargetCommand.cs
using Mediator;

namespace FSH.Modules.Proxies.Contracts.v1.HealthCheckTargets;

public sealed record CreateHealthCheckTargetCommand(
    string Name, string TestUrl, int? ExpectedStatusCode, string? ExpectedBodyKeyword, int TimeoutMs) : ICommand<Guid>;
```

```csharp
// src/Modules/Proxies/Modules.Proxies.Contracts/v1/HealthCheckTargets/UpdateHealthCheckTargetCommand.cs
using Mediator;

namespace FSH.Modules.Proxies.Contracts.v1.HealthCheckTargets;

public sealed record UpdateHealthCheckTargetCommand(
    Guid Id, string Name, string TestUrl, int? ExpectedStatusCode, string? ExpectedBodyKeyword, int TimeoutMs) : ICommand;
```

```csharp
// src/Modules/Proxies/Modules.Proxies.Contracts/v1/HealthCheckTargets/DeleteHealthCheckTargetCommand.cs
using Mediator;

namespace FSH.Modules.Proxies.Contracts.v1.HealthCheckTargets;

public sealed record DeleteHealthCheckTargetCommand(Guid Id) : ICommand;
```

```csharp
// src/Modules/Proxies/Modules.Proxies.Contracts/v1/HealthCheckTargets/AssignHealthCheckTargetToTagCommand.cs
using Mediator;

namespace FSH.Modules.Proxies.Contracts.v1.HealthCheckTargets;

public sealed record AssignHealthCheckTargetToTagCommand(Guid TagId, Guid HealthCheckTargetId) : ICommand;
```

```csharp
// src/Modules/Proxies/Modules.Proxies.Contracts/v1/HealthCheckTargets/UnassignHealthCheckTargetFromTagCommand.cs
using Mediator;

namespace FSH.Modules.Proxies.Contracts.v1.HealthCheckTargets;

public sealed record UnassignHealthCheckTargetFromTagCommand(Guid TagId) : ICommand;
```

```csharp
// src/Modules/Proxies/Modules.Proxies.Contracts/v1/HealthCheckTargets/ListHealthCheckTargetsQuery.cs
using FSH.Modules.Proxies.Contracts.Dtos;
using Mediator;

namespace FSH.Modules.Proxies.Contracts.v1.HealthCheckTargets;

public sealed record ListHealthCheckTargetsQuery : IQuery<IReadOnlyList<HealthCheckTargetDto>>;
```

- [ ] **Step 2: Write the failing handler tests**

```csharp
// src/Tests/Proxies.Tests/Handlers/HealthCheckTargetHandlerTests.cs
using FSH.Modules.Proxies.Contracts.v1.HealthCheckTargets;
using FSH.Modules.Proxies.Domain;
using FSH.Modules.Proxies.Features.v1.HealthCheckTargets.AssignHealthCheckTargetToTag;
using FSH.Modules.Proxies.Features.v1.HealthCheckTargets.CreateHealthCheckTarget;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using Xunit;

namespace Proxies.Tests.Handlers;

public sealed class HealthCheckTargetHandlerTests
{
    private static Proxies.Tests.TestProxiesDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<FSH.Modules.Proxies.Data.ProxiesDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    [Fact]
    public async Task Create_Should_Persist()
    {
        await using var db = CreateDb();
        var sut = new CreateHealthCheckTargetCommandHandler(db);

        var id = await sut.Handle(new CreateHealthCheckTargetCommand("Mercado Publico", "https://www.mercadopublico.cl", 200, null, 5000), CancellationToken.None);

        (await db.HealthCheckTargets.SingleAsync(x => x.Id == id)).TestUrl.ShouldBe("https://www.mercadopublico.cl");
    }

    [Fact]
    public async Task AssignToTag_Should_ReplaceExistingAssignment()
    {
        await using var db = CreateDb();
        var tag = Tag.Create("pais:cl");
        var targetA = HealthCheckTarget.Create("a", "https://a.example", 200, null, 5000);
        var targetB = HealthCheckTarget.Create("b", "https://b.example", 200, null, 5000);
        db.Tags.Add(tag);
        db.HealthCheckTargets.AddRange(targetA, targetB);
        await db.SaveChangesAsync();
        var sut = new AssignHealthCheckTargetToTagCommandHandler(db);
        await sut.Handle(new AssignHealthCheckTargetToTagCommand(tag.Id, targetA.Id), CancellationToken.None);

        await sut.Handle(new AssignHealthCheckTargetToTagCommand(tag.Id, targetB.Id), CancellationToken.None);

        var assignment = await db.Set<TagHealthCheckTargetAssignment>().SingleAsync(x => x.TagId == tag.Id);
        assignment.HealthCheckTargetId.ShouldBe(targetB.Id);
    }
}
```

- [ ] **Step 3: Run to verify failure, then implement (mirror Task 10's handlers exactly, `PolicyProfile`→`HealthCheckTarget`, `TagPolicyAssignment`→`TagHealthCheckTargetAssignment`)**

```csharp
// src/Modules/Proxies/Modules.Proxies/Features/v1/HealthCheckTargets/CreateHealthCheckTarget/CreateHealthCheckTargetCommandValidator.cs
using FluentValidation;
using FSH.Modules.Proxies.Contracts.v1.HealthCheckTargets;

namespace FSH.Modules.Proxies.Features.v1.HealthCheckTargets.CreateHealthCheckTarget;

public sealed class CreateHealthCheckTargetCommandValidator : AbstractValidator<CreateHealthCheckTargetCommand>
{
    public CreateHealthCheckTargetCommandValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(128);
        RuleFor(x => x.TestUrl).NotEmpty().MaximumLength(2048).Must(u => Uri.TryCreate(u, UriKind.Absolute, out _)).WithMessage("TestUrl must be an absolute URL.");
        RuleFor(x => x.TimeoutMs).InclusiveBetween(500, 30000);
    }
}
```

```csharp
// src/Modules/Proxies/Modules.Proxies/Features/v1/HealthCheckTargets/CreateHealthCheckTarget/CreateHealthCheckTargetCommandHandler.cs
using FSH.Modules.Proxies.Contracts.v1.HealthCheckTargets;
using FSH.Modules.Proxies.Data;
using FSH.Modules.Proxies.Domain;
using Mediator;

namespace FSH.Modules.Proxies.Features.v1.HealthCheckTargets.CreateHealthCheckTarget;

public sealed class CreateHealthCheckTargetCommandHandler(ProxiesDbContext dbContext) : ICommandHandler<CreateHealthCheckTargetCommand, Guid>
{
    public async ValueTask<Guid> Handle(CreateHealthCheckTargetCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var target = HealthCheckTarget.Create(command.Name, command.TestUrl, command.ExpectedStatusCode, command.ExpectedBodyKeyword, command.TimeoutMs);
        dbContext.HealthCheckTargets.Add(target);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return target.Id;
    }
}
```

```csharp
// src/Modules/Proxies/Modules.Proxies/Features/v1/HealthCheckTargets/UpdateHealthCheckTarget/UpdateHealthCheckTargetCommandValidator.cs
using FluentValidation;
using FSH.Modules.Proxies.Contracts.v1.HealthCheckTargets;

namespace FSH.Modules.Proxies.Features.v1.HealthCheckTargets.UpdateHealthCheckTarget;

public sealed class UpdateHealthCheckTargetCommandValidator : AbstractValidator<UpdateHealthCheckTargetCommand>
{
    public UpdateHealthCheckTargetCommandValidator()
    {
        RuleFor(x => x.Id).NotEmpty();
        RuleFor(x => x.Name).NotEmpty().MaximumLength(128);
        RuleFor(x => x.TestUrl).NotEmpty().MaximumLength(2048).Must(u => Uri.TryCreate(u, UriKind.Absolute, out _)).WithMessage("TestUrl must be an absolute URL.");
        RuleFor(x => x.TimeoutMs).InclusiveBetween(500, 30000);
    }
}
```

```csharp
// src/Modules/Proxies/Modules.Proxies/Features/v1/HealthCheckTargets/UpdateHealthCheckTarget/UpdateHealthCheckTargetCommandHandler.cs
using FSH.Framework.Core.Exceptions;
using FSH.Modules.Proxies.Contracts.v1.HealthCheckTargets;
using FSH.Modules.Proxies.Data;
using Mediator;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.Proxies.Features.v1.HealthCheckTargets.UpdateHealthCheckTarget;

public sealed class UpdateHealthCheckTargetCommandHandler(ProxiesDbContext dbContext) : ICommandHandler<UpdateHealthCheckTargetCommand>
{
    public async ValueTask<Unit> Handle(UpdateHealthCheckTargetCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var target = await dbContext.HealthCheckTargets.FirstOrDefaultAsync(x => x.Id == command.Id, cancellationToken).ConfigureAwait(false)
            ?? throw new NotFoundException($"Health check target {command.Id} not found.");
        target.Update(command.Name, command.TestUrl, command.ExpectedStatusCode, command.ExpectedBodyKeyword, command.TimeoutMs);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return Unit.Value;
    }
}
```

```csharp
// src/Modules/Proxies/Modules.Proxies/Features/v1/HealthCheckTargets/DeleteHealthCheckTarget/DeleteHealthCheckTargetCommandValidator.cs
using FluentValidation;
using FSH.Modules.Proxies.Contracts.v1.HealthCheckTargets;

namespace FSH.Modules.Proxies.Features.v1.HealthCheckTargets.DeleteHealthCheckTarget;

public sealed class DeleteHealthCheckTargetCommandValidator : AbstractValidator<DeleteHealthCheckTargetCommand>
{
    public DeleteHealthCheckTargetCommandValidator() => RuleFor(x => x.Id).NotEmpty();
}
```

```csharp
// src/Modules/Proxies/Modules.Proxies/Features/v1/HealthCheckTargets/DeleteHealthCheckTarget/DeleteHealthCheckTargetCommandHandler.cs
using FSH.Framework.Core.Exceptions;
using FSH.Modules.Proxies.Contracts.v1.HealthCheckTargets;
using FSH.Modules.Proxies.Data;
using Mediator;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.Proxies.Features.v1.HealthCheckTargets.DeleteHealthCheckTarget;

public sealed class DeleteHealthCheckTargetCommandHandler(ProxiesDbContext dbContext) : ICommandHandler<DeleteHealthCheckTargetCommand>
{
    public async ValueTask<Unit> Handle(DeleteHealthCheckTargetCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var target = await dbContext.HealthCheckTargets.FirstOrDefaultAsync(x => x.Id == command.Id, cancellationToken).ConfigureAwait(false)
            ?? throw new NotFoundException($"Health check target {command.Id} not found.");
        bool inUse = await dbContext.Set<Domain.TagHealthCheckTargetAssignment>().AnyAsync(x => x.HealthCheckTargetId == command.Id, cancellationToken).ConfigureAwait(false);
        if (inUse)
        {
            throw new CustomException("This health check target is still assigned to at least one tag. Unassign it first.", (IEnumerable<string>?)null, System.Net.HttpStatusCode.Conflict);
        }
        dbContext.HealthCheckTargets.Remove(target);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return Unit.Value;
    }
}
```

```csharp
// src/Modules/Proxies/Modules.Proxies/Features/v1/HealthCheckTargets/AssignHealthCheckTargetToTag/AssignHealthCheckTargetToTagCommandValidator.cs
using FluentValidation;
using FSH.Modules.Proxies.Contracts.v1.HealthCheckTargets;

namespace FSH.Modules.Proxies.Features.v1.HealthCheckTargets.AssignHealthCheckTargetToTag;

public sealed class AssignHealthCheckTargetToTagCommandValidator : AbstractValidator<AssignHealthCheckTargetToTagCommand>
{
    public AssignHealthCheckTargetToTagCommandValidator()
    {
        RuleFor(x => x.TagId).NotEmpty();
        RuleFor(x => x.HealthCheckTargetId).NotEmpty();
    }
}
```

```csharp
// src/Modules/Proxies/Modules.Proxies/Features/v1/HealthCheckTargets/AssignHealthCheckTargetToTag/AssignHealthCheckTargetToTagCommandHandler.cs
using FSH.Framework.Core.Exceptions;
using FSH.Modules.Proxies.Contracts.v1.HealthCheckTargets;
using FSH.Modules.Proxies.Data;
using FSH.Modules.Proxies.Domain;
using Mediator;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.Proxies.Features.v1.HealthCheckTargets.AssignHealthCheckTargetToTag;

public sealed class AssignHealthCheckTargetToTagCommandHandler(ProxiesDbContext dbContext) : ICommandHandler<AssignHealthCheckTargetToTagCommand>
{
    public async ValueTask<Unit> Handle(AssignHealthCheckTargetToTagCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        bool tagExists = await dbContext.Tags.AnyAsync(x => x.Id == command.TagId, cancellationToken).ConfigureAwait(false);
        if (!tagExists) throw new NotFoundException($"Tag {command.TagId} not found.");
        bool targetExists = await dbContext.HealthCheckTargets.AnyAsync(x => x.Id == command.HealthCheckTargetId, cancellationToken).ConfigureAwait(false);
        if (!targetExists) throw new NotFoundException($"Health check target {command.HealthCheckTargetId} not found.");

        var existing = await dbContext.Set<TagHealthCheckTargetAssignment>().FirstOrDefaultAsync(x => x.TagId == command.TagId, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            dbContext.Set<TagHealthCheckTargetAssignment>().Remove(existing);
        }
        dbContext.Set<TagHealthCheckTargetAssignment>().Add(TagHealthCheckTargetAssignment.Create(command.TagId, command.HealthCheckTargetId));

        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return Unit.Value;
    }
}
```

```csharp
// src/Modules/Proxies/Modules.Proxies/Features/v1/HealthCheckTargets/UnassignHealthCheckTargetFromTag/UnassignHealthCheckTargetFromTagCommandValidator.cs
using FluentValidation;
using FSH.Modules.Proxies.Contracts.v1.HealthCheckTargets;

namespace FSH.Modules.Proxies.Features.v1.HealthCheckTargets.UnassignHealthCheckTargetFromTag;

public sealed class UnassignHealthCheckTargetFromTagCommandValidator : AbstractValidator<UnassignHealthCheckTargetFromTagCommand>
{
    public UnassignHealthCheckTargetFromTagCommandValidator() => RuleFor(x => x.TagId).NotEmpty();
}
```

```csharp
// src/Modules/Proxies/Modules.Proxies/Features/v1/HealthCheckTargets/UnassignHealthCheckTargetFromTag/UnassignHealthCheckTargetFromTagCommandHandler.cs
using FSH.Modules.Proxies.Contracts.v1.HealthCheckTargets;
using FSH.Modules.Proxies.Data;
using FSH.Modules.Proxies.Domain;
using Mediator;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.Proxies.Features.v1.HealthCheckTargets.UnassignHealthCheckTargetFromTag;

public sealed class UnassignHealthCheckTargetFromTagCommandHandler(ProxiesDbContext dbContext) : ICommandHandler<UnassignHealthCheckTargetFromTagCommand>
{
    public async ValueTask<Unit> Handle(UnassignHealthCheckTargetFromTagCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var existing = await dbContext.Set<TagHealthCheckTargetAssignment>().FirstOrDefaultAsync(x => x.TagId == command.TagId, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            dbContext.Set<TagHealthCheckTargetAssignment>().Remove(existing);
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        return Unit.Value;
    }
}
```

```csharp
// src/Modules/Proxies/Modules.Proxies/Features/v1/HealthCheckTargets/ListHealthCheckTargets/ListHealthCheckTargetsQueryHandler.cs
using FSH.Modules.Proxies.Contracts.Dtos;
using FSH.Modules.Proxies.Contracts.v1.HealthCheckTargets;
using FSH.Modules.Proxies.Data;
using Mediator;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.Proxies.Features.v1.HealthCheckTargets.ListHealthCheckTargets;

public sealed class ListHealthCheckTargetsQueryHandler(ProxiesDbContext dbContext) : IQueryHandler<ListHealthCheckTargetsQuery, IReadOnlyList<HealthCheckTargetDto>>
{
    public async ValueTask<IReadOnlyList<HealthCheckTargetDto>> Handle(ListHealthCheckTargetsQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        return await dbContext.HealthCheckTargets.AsNoTracking().OrderBy(x => x.Name)
            .Select(x => new HealthCheckTargetDto(x.Id, x.Name, x.TestUrl, x.ExpectedStatusCode, x.ExpectedBodyKeyword, x.TimeoutMs))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
    }
}
```

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test src/Tests/Proxies.Tests --filter "FullyQualifiedName~HealthCheckTargetHandlerTests"`
Expected: PASS, 2 tests.

- [ ] **Step 5: Implement the endpoints (identical route shape to Task 10, base path `/health-check-targets`, assign/unassign under `/tags/{tagId:guid}/health-check-target[/{healthCheckTargetId:guid}]`, each `.RequirePermission(ProxiesPermissions.HealthCheckTargets.*)`)**

```csharp
// src/Modules/Proxies/Modules.Proxies/Features/v1/HealthCheckTargets/CreateHealthCheckTarget/CreateHealthCheckTargetEndpoint.cs
using FSH.Framework.Shared.Identity.Authorization;
using FSH.Modules.Proxies.Contracts.Authorization;
using FSH.Modules.Proxies.Contracts.v1.HealthCheckTargets;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FSH.Modules.Proxies.Features.v1.HealthCheckTargets.CreateHealthCheckTarget;

public static class CreateHealthCheckTargetEndpoint
{
    internal static RouteHandlerBuilder MapCreateHealthCheckTargetEndpoint(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapPost("/health-check-targets", async (CreateHealthCheckTargetCommand command, IMediator mediator, CancellationToken ct) => Results.Ok(await mediator.Send(command, ct)))
            .WithName("CreateHealthCheckTarget").WithSummary("Create a health check target")
            .RequirePermission(ProxiesPermissions.HealthCheckTargets.Create);
}
```

```csharp
// src/Modules/Proxies/Modules.Proxies/Features/v1/HealthCheckTargets/UpdateHealthCheckTarget/UpdateHealthCheckTargetEndpoint.cs
using FSH.Framework.Shared.Identity.Authorization;
using FSH.Modules.Proxies.Contracts.Authorization;
using FSH.Modules.Proxies.Contracts.v1.HealthCheckTargets;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FSH.Modules.Proxies.Features.v1.HealthCheckTargets.UpdateHealthCheckTarget;

public static class UpdateHealthCheckTargetEndpoint
{
    internal static RouteHandlerBuilder MapUpdateHealthCheckTargetEndpoint(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapPut("/health-check-targets/{id:guid}", async (Guid id, UpdateHealthCheckTargetBody body, IMediator mediator, CancellationToken ct) =>
            {
                await mediator.Send(new UpdateHealthCheckTargetCommand(id, body.Name, body.TestUrl, body.ExpectedStatusCode, body.ExpectedBodyKeyword, body.TimeoutMs), ct);
                return Results.NoContent();
            })
            .WithName("UpdateHealthCheckTarget").WithSummary("Update a health check target")
            .RequirePermission(ProxiesPermissions.HealthCheckTargets.Update);

    internal sealed record UpdateHealthCheckTargetBody(string Name, string TestUrl, int? ExpectedStatusCode, string? ExpectedBodyKeyword, int TimeoutMs);
}
```

```csharp
// src/Modules/Proxies/Modules.Proxies/Features/v1/HealthCheckTargets/DeleteHealthCheckTarget/DeleteHealthCheckTargetEndpoint.cs
using FSH.Framework.Shared.Identity.Authorization;
using FSH.Modules.Proxies.Contracts.Authorization;
using FSH.Modules.Proxies.Contracts.v1.HealthCheckTargets;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FSH.Modules.Proxies.Features.v1.HealthCheckTargets.DeleteHealthCheckTarget;

public static class DeleteHealthCheckTargetEndpoint
{
    internal static RouteHandlerBuilder MapDeleteHealthCheckTargetEndpoint(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapDelete("/health-check-targets/{id:guid}", async (Guid id, IMediator mediator, CancellationToken ct) => { await mediator.Send(new DeleteHealthCheckTargetCommand(id), ct); return Results.NoContent(); })
            .WithName("DeleteHealthCheckTarget").WithSummary("Delete a health check target")
            .RequirePermission(ProxiesPermissions.HealthCheckTargets.Delete);
}
```

```csharp
// src/Modules/Proxies/Modules.Proxies/Features/v1/HealthCheckTargets/AssignHealthCheckTargetToTag/AssignHealthCheckTargetToTagEndpoint.cs
using FSH.Framework.Shared.Identity.Authorization;
using FSH.Modules.Proxies.Contracts.Authorization;
using FSH.Modules.Proxies.Contracts.v1.HealthCheckTargets;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FSH.Modules.Proxies.Features.v1.HealthCheckTargets.AssignHealthCheckTargetToTag;

public static class AssignHealthCheckTargetToTagEndpoint
{
    internal static RouteHandlerBuilder MapAssignHealthCheckTargetToTagEndpoint(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapPost("/tags/{tagId:guid}/health-check-target/{healthCheckTargetId:guid}", async (Guid tagId, Guid healthCheckTargetId, IMediator mediator, CancellationToken ct) =>
            { await mediator.Send(new AssignHealthCheckTargetToTagCommand(tagId, healthCheckTargetId), ct); return Results.NoContent(); })
            .WithName("AssignHealthCheckTargetToTag").WithSummary("Assign a health check target to a tag")
            .RequirePermission(ProxiesPermissions.HealthCheckTargets.Update);
}
```

```csharp
// src/Modules/Proxies/Modules.Proxies/Features/v1/HealthCheckTargets/UnassignHealthCheckTargetFromTag/UnassignHealthCheckTargetFromTagEndpoint.cs
using FSH.Framework.Shared.Identity.Authorization;
using FSH.Modules.Proxies.Contracts.Authorization;
using FSH.Modules.Proxies.Contracts.v1.HealthCheckTargets;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FSH.Modules.Proxies.Features.v1.HealthCheckTargets.UnassignHealthCheckTargetFromTag;

public static class UnassignHealthCheckTargetFromTagEndpoint
{
    internal static RouteHandlerBuilder MapUnassignHealthCheckTargetFromTagEndpoint(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapDelete("/tags/{tagId:guid}/health-check-target", async (Guid tagId, IMediator mediator, CancellationToken ct) =>
            { await mediator.Send(new UnassignHealthCheckTargetFromTagCommand(tagId), ct); return Results.NoContent(); })
            .WithName("UnassignHealthCheckTargetFromTag").WithSummary("Unassign the health check target from a tag")
            .RequirePermission(ProxiesPermissions.HealthCheckTargets.Update);
}
```

```csharp
// src/Modules/Proxies/Modules.Proxies/Features/v1/HealthCheckTargets/ListHealthCheckTargets/ListHealthCheckTargetsEndpoint.cs
using FSH.Framework.Shared.Identity.Authorization;
using FSH.Modules.Proxies.Contracts.Authorization;
using FSH.Modules.Proxies.Contracts.v1.HealthCheckTargets;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FSH.Modules.Proxies.Features.v1.HealthCheckTargets.ListHealthCheckTargets;

public static class ListHealthCheckTargetsEndpoint
{
    internal static RouteHandlerBuilder MapListHealthCheckTargetsEndpoint(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapGet("/health-check-targets", (IMediator mediator, CancellationToken ct) => mediator.Send(new ListHealthCheckTargetsQuery(), ct))
            .WithName("ListHealthCheckTargets").WithSummary("List health check targets")
            .RequirePermission(ProxiesPermissions.HealthCheckTargets.View);
}
```

- [ ] **Step 6: Wire, build, test, commit**

```csharp
// inside ProxiesModule.MapEndpoints
group.MapCreateHealthCheckTargetEndpoint();
group.MapUpdateHealthCheckTargetEndpoint();
group.MapDeleteHealthCheckTargetEndpoint();
group.MapAssignHealthCheckTargetToTagEndpoint();
group.MapUnassignHealthCheckTargetFromTagEndpoint();
group.MapListHealthCheckTargetsEndpoint();
```

Run: `dotnet build src/FS.Proxy.slnx && dotnet test src/Tests/Proxies.Tests`
Expected: PASS.

```bash
git add src/Modules/Proxies src/Tests/Proxies.Tests
git commit -m "feat(proxies): add HealthCheckTarget CRUD and tag assignment"
```

### Task 12: Proxy list/filter and enable/disable (single + bulk)

**Files:**
- Create: `src/Modules/Proxies/Modules.Proxies.Contracts/Dtos/ProxyDto.cs`
- Create: `src/Modules/Proxies/Modules.Proxies.Contracts/v1/Proxies/ListProxiesQuery.cs`
- Create: `src/Modules/Proxies/Modules.Proxies.Contracts/v1/Proxies/SetProxiesStatusCommand.cs`
- Create: `src/Modules/Proxies/Modules.Proxies/Features/v1/Proxies/ListProxies/ListProxiesQueryValidator.cs`
- Create: `src/Modules/Proxies/Modules.Proxies/Features/v1/Proxies/ListProxies/ListProxiesQueryHandler.cs`
- Create: `src/Modules/Proxies/Modules.Proxies/Features/v1/Proxies/ListProxies/ListProxiesEndpoint.cs`
- Create: `src/Modules/Proxies/Modules.Proxies/Features/v1/Proxies/SetProxiesStatus/SetProxiesStatusCommandValidator.cs`
- Create: `src/Modules/Proxies/Modules.Proxies/Features/v1/Proxies/SetProxiesStatus/SetProxiesStatusCommandHandler.cs`
- Create: `src/Modules/Proxies/Modules.Proxies/Features/v1/Proxies/EnableProxies/EnableProxiesEndpoint.cs`
- Create: `src/Modules/Proxies/Modules.Proxies/Features/v1/Proxies/DisableProxies/DisableProxiesEndpoint.cs`
- Modify: `src/Modules/Proxies/Modules.Proxies/ProxiesModule.cs`
- Test: `src/Tests/Proxies.Tests/Handlers/ProxyStatusHandlerTests.cs`
- Test: `src/Tests/Proxies.Tests/Handlers/ListProxiesHandlerTests.cs`

**Interfaces:**
- Produces: `ProxyDto(Guid Id, string Host, int Port, ProxyProtocol Protocol, ProxyStatus Status, Guid ProviderAccountId, string ProviderAccountName, ProxyProviderType ProviderType, IReadOnlyList<string> Tags, DateTime CreatedAtUtc, DateTime? LastRenewedAtUtc)`; `ListProxiesQuery(IReadOnlyList<string>? Tags, ProxyStatus? Status, Guid? ProviderAccountId, int PageNumber, int PageSize) : IQuery<PagedResponse<ProxyDto>>`; `SetProxiesStatusCommand(IReadOnlyList<Guid>? ProxyIds, Guid? TagId, ProxyStatus Status) : ICommand<int>` — exactly one of `ProxyIds`/`TagId` must be set (validator-enforced XOR); a single-element `ProxyIds` list is how the "individual" enable/disable case is expressed, so there's one command for both the single and bulk cases. `ProxyStatus` needs to move to Contracts (same reasoning as `ProxyProtocol` in Task 8) — move it now:

```csharp
// src/Modules/Proxies/Modules.Proxies.Contracts/ProxyStatus.cs (new file)
namespace FSH.Modules.Proxies.Contracts;

public enum ProxyStatus { Active, Disabled, Banned, Testing, Retired }
```

Delete the `enum ProxyStatus` declaration from `src/Modules/Proxies/Modules.Proxies/Domain/Proxy.cs` and add `using FSH.Modules.Proxies.Contracts;` there instead (it already has that using from Task 8's `ProxyProtocol` move).

- [ ] **Step 1: Define the DTO, query, and command**

```csharp
// src/Modules/Proxies/Modules.Proxies.Contracts/Dtos/ProxyDto.cs
namespace FSH.Modules.Proxies.Contracts.Dtos;

public sealed record ProxyDto(
    Guid Id, string Host, int Port, ProxyProtocol Protocol, ProxyStatus Status,
    Guid ProviderAccountId, string ProviderAccountName, ProxyProviderType ProviderType,
    IReadOnlyList<string> Tags, DateTime CreatedAtUtc, DateTime? LastRenewedAtUtc);
```

```csharp
// src/Modules/Proxies/Modules.Proxies.Contracts/v1/Proxies/ListProxiesQuery.cs
using FSH.Framework.Shared.Persistence;
using FSH.Modules.Proxies.Contracts.Dtos;
using Mediator;

namespace FSH.Modules.Proxies.Contracts.v1.Proxies;

public sealed record ListProxiesQuery(
    IReadOnlyList<string>? Tags, ProxyStatus? Status, Guid? ProviderAccountId,
    int PageNumber = 1, int PageSize = 20) : IQuery<PagedResponse<ProxyDto>>;
```

```csharp
// src/Modules/Proxies/Modules.Proxies.Contracts/v1/Proxies/SetProxiesStatusCommand.cs
using Mediator;

namespace FSH.Modules.Proxies.Contracts.v1.Proxies;

public sealed record SetProxiesStatusCommand(IReadOnlyList<Guid>? ProxyIds, Guid? TagId, ProxyStatus Status) : ICommand<int>;
```

- [ ] **Step 2: Write the failing tests**

```csharp
// src/Tests/Proxies.Tests/Handlers/ProxyStatusHandlerTests.cs
using FSH.Modules.Proxies.Contracts;
using FSH.Modules.Proxies.Contracts.v1.Proxies;
using FSH.Modules.Proxies.Domain;
using FSH.Modules.Proxies.Features.v1.Proxies.SetProxiesStatus;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using Xunit;

namespace Proxies.Tests.Handlers;

public sealed class ProxyStatusHandlerTests
{
    private static Proxies.Tests.TestProxiesDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<FSH.Modules.Proxies.Data.ProxiesDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    [Fact]
    public async Task Handle_Should_SetStatus_ForExplicitIds()
    {
        await using var db = CreateDb();
        var p1 = Proxy.Create(ManualProviderAccount.Id, "1.1.1.1", 80, ProxyProtocol.Http, null, null, null);
        var p2 = Proxy.Create(ManualProviderAccount.Id, "2.2.2.2", 80, ProxyProtocol.Http, null, null, null);
        db.Proxies.AddRange(p1, p2);
        await db.SaveChangesAsync();
        var sut = new SetProxiesStatusCommandHandler(db);

        var affected = await sut.Handle(new SetProxiesStatusCommand([p1.Id], null, ProxyStatus.Disabled), CancellationToken.None);

        affected.ShouldBe(1);
        (await db.Proxies.SingleAsync(x => x.Id == p1.Id)).Status.ShouldBe(ProxyStatus.Disabled);
        (await db.Proxies.SingleAsync(x => x.Id == p2.Id)).Status.ShouldBe(ProxyStatus.Testing);
    }

    [Fact]
    public async Task Handle_Should_SetStatus_ForAllProxiesWithTag()
    {
        await using var db = CreateDb();
        var tag = Tag.Create("pais:cl");
        var p1 = Proxy.Create(ManualProviderAccount.Id, "1.1.1.1", 80, ProxyProtocol.Http, null, null, null);
        p1.AssignTag(tag.Id);
        var p2 = Proxy.Create(ManualProviderAccount.Id, "2.2.2.2", 80, ProxyProtocol.Http, null, null, null);
        db.Tags.Add(tag);
        db.Proxies.AddRange(p1, p2);
        await db.SaveChangesAsync();
        var sut = new SetProxiesStatusCommandHandler(db);

        var affected = await sut.Handle(new SetProxiesStatusCommand(null, tag.Id, ProxyStatus.Active), CancellationToken.None);

        affected.ShouldBe(1);
        (await db.Proxies.SingleAsync(x => x.Id == p1.Id)).Status.ShouldBe(ProxyStatus.Active);
        (await db.Proxies.SingleAsync(x => x.Id == p2.Id)).Status.ShouldBe(ProxyStatus.Testing);
    }
}
```

```csharp
// src/Tests/Proxies.Tests/Handlers/ListProxiesHandlerTests.cs
using FSH.Modules.Proxies.Contracts;
using FSH.Modules.Proxies.Contracts.v1.Proxies;
using FSH.Modules.Proxies.Domain;
using FSH.Modules.Proxies.Features.v1.Proxies.ListProxies;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using Xunit;

namespace Proxies.Tests.Handlers;

public sealed class ListProxiesHandlerTests
{
    private static Proxies.Tests.TestProxiesDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<FSH.Modules.Proxies.Data.ProxiesDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    [Fact]
    public async Task Handle_Should_FilterByTag()
    {
        await using var db = CreateDb();
        var tag = Tag.Create("pais:cl");
        var matching = Proxy.Create(ManualProviderAccount.Id, "1.1.1.1", 80, ProxyProtocol.Http, null, null, null);
        matching.AssignTag(tag.Id);
        var other = Proxy.Create(ManualProviderAccount.Id, "2.2.2.2", 80, ProxyProtocol.Http, null, null, null);
        db.Tags.Add(tag);
        db.Proxies.AddRange(matching, other);
        await db.SaveChangesAsync();
        var sut = new ListProxiesQueryHandler(db);

        var result = await sut.Handle(new ListProxiesQuery(["pais:cl"], null, null), CancellationToken.None);

        result.Items.Select(x => x.Id).ShouldBe([matching.Id]);
        result.Items.Single().Tags.ShouldBe(["pais:cl"]);
    }

    [Fact]
    public async Task Handle_Should_FilterByStatus()
    {
        await using var db = CreateDb();
        var active = Proxy.Create(ManualProviderAccount.Id, "1.1.1.1", 80, ProxyProtocol.Http, null, null, null);
        active.SetStatus(ProxyStatus.Active);
        var disabled = Proxy.Create(ManualProviderAccount.Id, "2.2.2.2", 80, ProxyProtocol.Http, null, null, null);
        disabled.SetStatus(ProxyStatus.Disabled);
        db.Proxies.AddRange(active, disabled);
        await db.SaveChangesAsync();
        var sut = new ListProxiesQueryHandler(db);

        var result = await sut.Handle(new ListProxiesQuery(null, ProxyStatus.Active, null), CancellationToken.None);

        result.Items.Select(x => x.Id).ShouldBe([active.Id]);
    }
}
```

- [ ] **Step 3: Run to verify failure, then implement**

Run: `dotnet test src/Tests/Proxies.Tests --filter "FullyQualifiedName~ProxyStatusHandlerTests|FullyQualifiedName~ListProxiesHandlerTests"` — expect compile failure.

```csharp
// src/Modules/Proxies/Modules.Proxies/Features/v1/Proxies/SetProxiesStatus/SetProxiesStatusCommandValidator.cs
using FluentValidation;
using FSH.Modules.Proxies.Contracts;
using FSH.Modules.Proxies.Contracts.v1.Proxies;

namespace FSH.Modules.Proxies.Features.v1.Proxies.SetProxiesStatus;

public sealed class SetProxiesStatusCommandValidator : AbstractValidator<SetProxiesStatusCommand>
{
    public SetProxiesStatusCommandValidator()
    {
        RuleFor(x => x)
            .Must(x => (x.ProxyIds is { Count: > 0 }) ^ x.TagId.HasValue)
            .WithMessage("Provide exactly one of ProxyIds or TagId.");
        RuleFor(x => x.Status).Must(s => s is ProxyStatus.Active or ProxyStatus.Disabled)
            .WithMessage("Status must be Active or Disabled — other statuses are system-managed.");
    }
}
```

```csharp
// src/Modules/Proxies/Modules.Proxies/Features/v1/Proxies/SetProxiesStatus/SetProxiesStatusCommandHandler.cs
using FSH.Modules.Proxies.Contracts.v1.Proxies;
using FSH.Modules.Proxies.Data;
using FSH.Modules.Proxies.Domain;
using Mediator;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.Proxies.Features.v1.Proxies.SetProxiesStatus;

public sealed class SetProxiesStatusCommandHandler(ProxiesDbContext dbContext) : ICommandHandler<SetProxiesStatusCommand, int>
{
    public async ValueTask<int> Handle(SetProxiesStatusCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        List<Proxy> targets;
        if (command.TagId is { } tagId)
        {
            var proxyIds = await dbContext.Set<ProxyTagAssignment>().Where(a => a.TagId == tagId).Select(a => a.ProxyId).ToListAsync(cancellationToken).ConfigureAwait(false);
            targets = await dbContext.Proxies.Where(p => proxyIds.Contains(p.Id)).ToListAsync(cancellationToken).ConfigureAwait(false);
        }
        else
        {
            targets = await dbContext.Proxies.Where(p => command.ProxyIds!.Contains(p.Id)).ToListAsync(cancellationToken).ConfigureAwait(false);
        }

        foreach (var proxy in targets)
        {
            proxy.SetStatus((ProxyStatus)command.Status);
        }

        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return targets.Count;
    }
}
```

```csharp
// src/Modules/Proxies/Modules.Proxies/Features/v1/Proxies/ListProxies/ListProxiesQueryValidator.cs
using FluentValidation;
using FSH.Modules.Proxies.Contracts.v1.Proxies;

namespace FSH.Modules.Proxies.Features.v1.Proxies.ListProxies;

public sealed class ListProxiesQueryValidator : AbstractValidator<ListProxiesQuery>
{
    public ListProxiesQueryValidator()
    {
        RuleFor(x => x.PageNumber).GreaterThanOrEqualTo(1);
        RuleFor(x => x.PageSize).InclusiveBetween(1, 200);
    }
}
```

```csharp
// src/Modules/Proxies/Modules.Proxies/Features/v1/Proxies/ListProxies/ListProxiesQueryHandler.cs
using FSH.Framework.Shared.Persistence;
using FSH.Modules.Proxies.Contracts.Dtos;
using FSH.Modules.Proxies.Contracts.v1.Proxies;
using FSH.Modules.Proxies.Data;
using FSH.Modules.Proxies.Domain;
using Mediator;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.Proxies.Features.v1.Proxies.ListProxies;

public sealed class ListProxiesQueryHandler(ProxiesDbContext dbContext) : IQueryHandler<ListProxiesQuery, PagedResponse<ProxyDto>>
{
    public async ValueTask<PagedResponse<ProxyDto>> Handle(ListProxiesQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var q = dbContext.Proxies.AsNoTracking().AsQueryable();

        if (query.Status is { } status) q = q.Where(p => p.Status == status); // ProxyStatus lives in Contracts as of this task's earlier step — no cast needed
        if (query.ProviderAccountId is { } accountId) q = q.Where(p => p.ProviderAccountId == accountId);
        if (query.Tags is { Count: > 0 })
        {
            var normalized = query.Tags.Select(Tag.Normalize).ToList();
            var matchingTagIds = await dbContext.Tags.Where(t => normalized.Contains(t.Name)).Select(t => t.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
            var proxyIdsWithAnyTag = dbContext.Set<ProxyTagAssignment>().Where(a => matchingTagIds.Contains(a.TagId)).Select(a => a.ProxyId);
            q = q.Where(p => proxyIdsWithAnyTag.Contains(p.Id));
        }

        long total = await q.LongCountAsync(cancellationToken).ConfigureAwait(false);
        var page = await q.OrderBy(p => p.Host).Skip((query.PageNumber - 1) * query.PageSize).Take(query.PageSize)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var accountNames = await dbContext.ProviderAccounts.AsNoTracking()
            .Where(a => page.Select(p => p.ProviderAccountId).Contains(a.Id))
            .ToDictionaryAsync(a => a.Id, a => (a.Name, a.ProviderType), cancellationToken).ConfigureAwait(false);
        var proxyIdsOnPage = page.Select(p => p.Id).ToList();
        var tagsByProxy = await dbContext.Set<ProxyTagAssignment>().AsNoTracking()
            .Where(a => proxyIdsOnPage.Contains(a.ProxyId))
            .Join(dbContext.Tags.AsNoTracking(), a => a.TagId, t => t.Id, (a, t) => new { a.ProxyId, t.Name })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var items = page.Select(p => new ProxyDto(
            p.Id, p.Host, p.Port, (FSH.Modules.Proxies.Contracts.ProxyProtocol)p.Protocol, (FSH.Modules.Proxies.Contracts.ProxyStatus)p.Status,
            p.ProviderAccountId, accountNames[p.ProviderAccountId].Name, accountNames[p.ProviderAccountId].ProviderType,
            tagsByProxy.Where(t => t.ProxyId == p.Id).Select(t => t.Name).ToList(),
            p.CreatedAtUtc, p.LastRenewedAtUtc)).ToList();

        return new PagedResponse<ProxyDto>
        {
            Items = items, PageNumber = query.PageNumber, PageSize = query.PageSize,
            TotalCount = total, TotalPages = (int)Math.Ceiling(total / (double)query.PageSize)
        };
    }
}
```

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test src/Tests/Proxies.Tests --filter "FullyQualifiedName~ProxyStatusHandlerTests|FullyQualifiedName~ListProxiesHandlerTests"`
Expected: PASS, 4 tests.

- [ ] **Step 5: Implement the endpoints**

```csharp
// src/Modules/Proxies/Modules.Proxies/Features/v1/Proxies/ListProxies/ListProxiesEndpoint.cs
using FSH.Framework.Shared.Identity.Authorization;
using FSH.Modules.Proxies.Contracts;
using FSH.Modules.Proxies.Contracts.Authorization;
using FSH.Modules.Proxies.Contracts.v1.Proxies;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FSH.Modules.Proxies.Features.v1.Proxies.ListProxies;

public static class ListProxiesEndpoint
{
    internal static RouteHandlerBuilder MapListProxiesEndpoint(this IEndpointRouteBuilder endpoints)
    {
        return endpoints.MapGet("/",
                (string[]? tags, ProxyStatus? status, Guid? providerAccountId, int pageNumber, int pageSize, IMediator mediator, CancellationToken ct) =>
                    mediator.Send(new ListProxiesQuery(tags, status, providerAccountId, pageNumber == 0 ? 1 : pageNumber, pageSize == 0 ? 20 : pageSize), ct))
            .WithName("ListProxies")
            .WithSummary("List proxies (paged, filterable by tags/status/provider account)")
            .RequirePermission(ProxiesPermissions.ProviderAccounts.View);
    }
}
```

Note this reuses `ProviderAccounts.View` rather than adding a seventh, redundant `Proxies.View` permission resource — an operator who can see provider accounts can see the inventory they hold. If that scoping turns out to be too coarse in practice, split it out as its own `ProxiesPermissions.Proxies.View` resource later; it's a one-line, backward-compatible addition.

```csharp
// src/Modules/Proxies/Modules.Proxies/Features/v1/Proxies/EnableProxies/EnableProxiesEndpoint.cs
using FSH.Framework.Shared.Identity.Authorization;
using FSH.Modules.Proxies.Contracts;
using FSH.Modules.Proxies.Contracts.Authorization;
using FSH.Modules.Proxies.Contracts.v1.Proxies;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FSH.Modules.Proxies.Features.v1.Proxies.EnableProxies;

public static class EnableProxiesEndpoint
{
    internal static RouteHandlerBuilder MapEnableProxiesEndpoint(this IEndpointRouteBuilder endpoints)
    {
        return endpoints.MapPost("/enable",
                (SetProxiesStatusBody body, IMediator mediator, CancellationToken ct) =>
                    mediator.Send(new SetProxiesStatusCommand(body.ProxyIds, body.TagId, ProxyStatus.Active), ct))
            .WithName("EnableProxies")
            .WithSummary("Enable one or more proxies, by id list or by tag")
            .RequirePermission(ProxiesPermissions.ManualProxies.Update);
    }

    internal sealed record SetProxiesStatusBody(IReadOnlyList<Guid>? ProxyIds, Guid? TagId);
}
```

```csharp
// src/Modules/Proxies/Modules.Proxies/Features/v1/Proxies/DisableProxies/DisableProxiesEndpoint.cs
using FSH.Framework.Shared.Identity.Authorization;
using FSH.Modules.Proxies.Contracts;
using FSH.Modules.Proxies.Contracts.Authorization;
using FSH.Modules.Proxies.Contracts.v1.Proxies;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FSH.Modules.Proxies.Features.v1.Proxies.DisableProxies;

public static class DisableProxiesEndpoint
{
    internal static RouteHandlerBuilder MapDisableProxiesEndpoint(this IEndpointRouteBuilder endpoints)
    {
        return endpoints.MapPost("/disable",
                (FSH.Modules.Proxies.Features.v1.Proxies.EnableProxies.EnableProxiesEndpoint.SetProxiesStatusBody body, IMediator mediator, CancellationToken ct) =>
                    mediator.Send(new SetProxiesStatusCommand(body.ProxyIds, body.TagId, ProxyStatus.Disabled), ct))
            .WithName("DisableProxies")
            .WithSummary("Disable one or more proxies, by id list or by tag")
            .RequirePermission(ProxiesPermissions.ManualProxies.Update);
    }
}
```

- [ ] **Step 6: Wire, build, test, commit**

```csharp
// inside ProxiesModule.MapEndpoints
group.MapListProxiesEndpoint();
group.MapEnableProxiesEndpoint();
group.MapDisableProxiesEndpoint();
```

Run: `dotnet build src/FS.Proxy.slnx && dotnet test src/Tests/Proxies.Tests`
Expected: PASS.

```bash
git add src/Modules/Proxies src/Tests/Proxies.Tests
git commit -m "feat(proxies): add proxy list/filter and enable/disable (single + bulk by tag)"
```

---

## Milestone D — Concrete Provider Adapters

A `ProviderAccount.ProtectedCredentials` field decrypts (Task 5's `ProviderAccountCredentialProtector`) to a small provider-specific JSON blob, not a single string — each adapter below defines and deserializes its own shape. All three tasks follow the identical registration pattern from the cross-cutting research (§3): a **named** `HttpClient` wrapped in `AddHeroResilience`, resolved via `IHttpClientFactory` inside the adapter — the exact structural mirror of `WebhooksModule`'s `"Webhooks"` client.

**Before writing each adapter's request/response mapping**, verify the exact field names against that provider's current API docs — the shapes below are accurate as of this plan's writing but proxy providers evolve their APIs independently of this repo:
- WebShare: `https://apidocs.webshare.io`
- Oxylabs: `https://developers.oxylabs.io`
- BrightData: `https://docs.brightdata.com/api-reference`

If a field name has drifted, the fix is isolated to that adapter's private mapping method — nothing else in the module depends on provider wire shapes.

### Task 13: WebShare adapter

**Files:**
- Create: `src/Modules/Proxies/Modules.Proxies/Providers/WebShare/WebShareCredentials.cs`
- Create: `src/Modules/Proxies/Modules.Proxies/Providers/WebShare/WebShareProxyListResponse.cs`
- Create: `src/Modules/Proxies/Modules.Proxies/Providers/WebShare/WebShareAdapter.cs`
- Modify: `src/Modules/Proxies/Modules.Proxies/ProxiesModule.cs`
- Test: `src/Tests/Proxies.Tests/Providers/WebShareAdapterTests.cs`

**Interfaces:**
- Consumes: `IProxyProviderAdapter` (Task 6).
- Produces: `WebShareAdapter : IProxyProviderAdapter` (`ProviderType = WebShare`, `SupportsSync = true`, `SupportsRenew = false` — WebShare's public API exposes proxy list/replace at the plan level, not a per-proxy "rotate this exact IP" call, so renewal for WebShare goes through the same admin-notification path as Manual until/unless a real rotate endpoint is confirmed against the docs above).

- [ ] **Step 1: Define the credential shape and API response DTOs**

```csharp
// src/Modules/Proxies/Modules.Proxies/Providers/WebShare/WebShareCredentials.cs
namespace FSH.Modules.Proxies.Providers.WebShare;

public sealed record WebShareCredentials(string ApiKey);
```

```csharp
// src/Modules/Proxies/Modules.Proxies/Providers/WebShare/WebShareProxyListResponse.cs
using System.Text.Json.Serialization;

namespace FSH.Modules.Proxies.Providers.WebShare;

public sealed record WebShareProxyListResponse(
    [property: JsonPropertyName("count")] int Count,
    [property: JsonPropertyName("next")] string? Next,
    [property: JsonPropertyName("results")] IReadOnlyList<WebShareProxyRecord> Results);

public sealed record WebShareProxyRecord(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("username")] string Username,
    [property: JsonPropertyName("password")] string Password,
    [property: JsonPropertyName("proxy_address")] string ProxyAddress,
    [property: JsonPropertyName("port")] int Port,
    [property: JsonPropertyName("valid")] bool Valid);
```

- [ ] **Step 2: Write the failing adapter test (HTTP mocked via a stub handler — the only established pattern in this repo for outbound-HTTP tests, per the cross-cutting research §5)**

```csharp
// src/Tests/Proxies.Tests/Providers/WebShareAdapterTests.cs
using System.Net;
using System.Net.Http.Json;
using FSH.Modules.Proxies.Contracts;
using FSH.Modules.Proxies.Domain;
using FSH.Modules.Proxies.Providers.WebShare;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Shouldly;
using Xunit;

namespace Proxies.Tests.Providers;

public sealed class WebShareAdapterTests
{
    private sealed class StubHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(response);
        }
    }

    private static (WebShareAdapter Adapter, StubHandler Handler) CreateSut(HttpResponseMessage response)
    {
        var handler = new StubHandler(response);
        var services = new ServiceCollection();
        services.AddHttpClient("ProxyProvider:WebShare").ConfigurePrimaryHttpMessageHandler(() => handler);
        var provider = services.BuildServiceProvider();
        return (new WebShareAdapter(provider.GetRequiredService<IHttpClientFactory>()), handler);
    }

    [Fact]
    public async Task SyncProxiesAsync_Should_MapResultsToProviderProxyRecords()
    {
        var payload = new WebShareProxyListResponse(1, null, [new WebShareProxyRecord("ext-1", "user", "pass", "1.2.3.4", 8080, true)]);
        var (sut, handler) = CreateSut(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(payload) });
        var account = ProviderAccount.Create("WebShare", ProxyProviderType.WebShare, "n/a");

        var result = await sut.SyncProxiesAsync(account, /* decrypted */ "{\"ApiKey\":\"key-123\"}", CancellationToken.None);

        result.Success.ShouldBeTrue();
        result.Proxies.Single().Host.ShouldBe("1.2.3.4");
        result.Proxies.Single().ExternalId.ShouldBe("ext-1");
        handler.LastRequest!.Headers.Authorization!.ToString().ShouldBe("Token key-123");
    }

    [Fact]
    public async Task SyncProxiesAsync_Should_ReturnFailure_When_ResponseIsNotSuccessful()
    {
        var (sut, _) = CreateSut(new HttpResponseMessage(HttpStatusCode.Unauthorized));
        var account = ProviderAccount.Create("WebShare", ProxyProviderType.WebShare, "n/a");

        var result = await sut.SyncProxiesAsync(account, "{\"ApiKey\":\"bad-key\"}", CancellationToken.None);

        result.Success.ShouldBeFalse();
        result.ErrorMessage.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public void SupportsRenew_Should_BeFalse() =>
        new WebShareAdapter(Substitute.For<IHttpClientFactory>()).SupportsRenew.ShouldBeFalse();
}
```

(add `using NSubstitute;` to the test file's usings for the third test)

- [ ] **Step 3: Run to verify failure, then implement**

Run: `dotnet test src/Tests/Proxies.Tests --filter "FullyQualifiedName~WebShareAdapterTests"` — expect compile failure.

```csharp
// src/Modules/Proxies/Modules.Proxies/Providers/WebShare/WebShareAdapter.cs
using System.Net.Http.Json;
using System.Text.Json;
using FSH.Modules.Proxies.Contracts;
using FSH.Modules.Proxies.Domain;

namespace FSH.Modules.Proxies.Providers.WebShare;

public sealed class WebShareAdapter(IHttpClientFactory httpClientFactory) : IProxyProviderAdapter
{
    private const string ClientName = "ProxyProvider:WebShare";

    public ProxyProviderType ProviderType => ProxyProviderType.WebShare;
    public bool SupportsSync => true;
    public bool SupportsRenew => false;

    public async Task<ProviderSyncResult> SyncProxiesAsync(ProviderAccount account, string decryptedCredentials, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(account);

        var credentials = JsonSerializer.Deserialize<WebShareCredentials>(decryptedCredentials)
            ?? throw new InvalidOperationException("WebShare credentials could not be parsed.");

        using var client = httpClientFactory.CreateClient(ClientName);
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://proxy.webshare.io/api/v2/proxy/list/?mode=direct&page_size=100");
        request.Headers.TryAddWithoutValidation("Authorization", $"Token {credentials.ApiKey}");

        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return ProviderSyncResult.Failed($"WebShare returned {(int)response.StatusCode} {response.ReasonPhrase}.");
        }

        var payload = await response.Content.ReadFromJsonAsync<WebShareProxyListResponse>(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("WebShare returned an empty proxy list response.");

        var proxies = payload.Results
            .Where(r => r.Valid)
            .Select(r => new ProviderProxyRecord(r.Id, r.ProxyAddress, r.Port, ProxyProtocol.Http, r.Username, r.Password, IsActive: true))
            .ToList();

        return ProviderSyncResult.Ok(proxies);
    }

    public Task<ProviderRenewResult> RenewProxyAsync(ProviderAccount account, string decryptedCredentials, Proxy proxy, CancellationToken cancellationToken) =>
        Task.FromResult(ProviderRenewResult.Unsupported());
}
```

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test src/Tests/Proxies.Tests --filter "FullyQualifiedName~WebShareAdapterTests"`
Expected: PASS, 3 tests.

- [ ] **Step 5: Register the named HttpClient and the adapter**

```csharp
// add inside ProxiesModule.ConfigureServices
builder.Services.AddHttpClient("ProxyProvider:WebShare")
    .AddHeroResilience(builder.Configuration);
builder.Services.AddScoped<IProxyProviderAdapter, FSH.Modules.Proxies.Providers.WebShare.WebShareAdapter>();
```

- [ ] **Step 6: Build and commit**

Run: `dotnet build src/FS.Proxy.slnx && dotnet test src/Tests/Proxies.Tests`
Expected: PASS.

```bash
git add src/Modules/Proxies src/Tests/Proxies.Tests
git commit -m "feat(proxies): add WebShare provider adapter"
```

### Task 14: Oxylabs adapter

**Files:**
- Create: `src/Modules/Proxies/Modules.Proxies/Providers/Oxylabs/OxylabsCredentials.cs`
- Create: `src/Modules/Proxies/Modules.Proxies/Providers/Oxylabs/OxylabsProxyListResponse.cs`
- Create: `src/Modules/Proxies/Modules.Proxies/Providers/Oxylabs/OxylabsAdapter.cs`
- Modify: `src/Modules/Proxies/Modules.Proxies/ProxiesModule.cs`
- Test: `src/Tests/Proxies.Tests/Providers/OxylabsAdapterTests.cs`

**Interfaces:**
- Produces: `OxylabsAdapter : IProxyProviderAdapter` (`ProviderType = Oxylabs`, `SupportsSync = true`, `SupportsRenew = false` — same reasoning as WebShare: Oxylabs' dedicated/ISP proxy list endpoint is confirmed; a documented per-IP rotate call isn't, so it falls back to the notification path until verified).

Oxylabs authenticates with HTTP Basic auth (account username/password), not a bearer token — this is the one adapter of the three with a materially different auth shape, worth building carefully rather than copy-pasting WebShare's header line.

- [ ] **Step 1: Define the credential shape and API response DTOs**

```csharp
// src/Modules/Proxies/Modules.Proxies/Providers/Oxylabs/OxylabsCredentials.cs
namespace FSH.Modules.Proxies.Providers.Oxylabs;

public sealed record OxylabsCredentials(string Username, string Password);
```

```csharp
// src/Modules/Proxies/Modules.Proxies/Providers/Oxylabs/OxylabsProxyListResponse.cs
using System.Text.Json.Serialization;

namespace FSH.Modules.Proxies.Providers.Oxylabs;

public sealed record OxylabsProxyListResponse(
    [property: JsonPropertyName("results")] IReadOnlyList<OxylabsProxyRecord> Results);

public sealed record OxylabsProxyRecord(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("ip")] string Ip,
    [property: JsonPropertyName("port")] int Port,
    [property: JsonPropertyName("username")] string Username,
    [property: JsonPropertyName("password")] string Password,
    [property: JsonPropertyName("status")] string Status);
```

- [ ] **Step 2: Write the failing adapter test**

```csharp
// src/Tests/Proxies.Tests/Providers/OxylabsAdapterTests.cs
using System.Net;
using System.Net.Http.Json;
using FSH.Modules.Proxies.Contracts;
using FSH.Modules.Proxies.Domain;
using FSH.Modules.Proxies.Providers.Oxylabs;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Proxies.Tests.Providers;

public sealed class OxylabsAdapterTests
{
    private sealed class StubHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(response);
        }
    }

    private static (OxylabsAdapter Adapter, StubHandler Handler) CreateSut(HttpResponseMessage response)
    {
        var handler = new StubHandler(response);
        var services = new ServiceCollection();
        services.AddHttpClient("ProxyProvider:Oxylabs").ConfigurePrimaryHttpMessageHandler(() => handler);
        var provider = services.BuildServiceProvider();
        return (new OxylabsAdapter(provider.GetRequiredService<IHttpClientFactory>()), handler);
    }

    [Fact]
    public async Task SyncProxiesAsync_Should_MapResultsToProviderProxyRecords_And_UseBasicAuth()
    {
        var payload = new OxylabsProxyListResponse([new OxylabsProxyRecord("ext-9", "5.6.7.8", 60000, "oxy-user", "oxy-pass", "active")]);
        var (sut, handler) = CreateSut(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(payload) });
        var account = ProviderAccount.Create("Oxylabs", ProxyProviderType.Oxylabs, "n/a");

        var result = await sut.SyncProxiesAsync(account, "{\"Username\":\"acct\",\"Password\":\"secret\"}", CancellationToken.None);

        result.Success.ShouldBeTrue();
        result.Proxies.Single().Host.ShouldBe("5.6.7.8");
        handler.LastRequest!.Headers.Authorization!.Scheme.ShouldBe("Basic");
    }

    [Fact]
    public async Task SyncProxiesAsync_Should_ExcludeNonActiveProxies()
    {
        var payload = new OxylabsProxyListResponse([
            new OxylabsProxyRecord("ext-1", "1.1.1.1", 60000, "u", "p", "active"),
            new OxylabsProxyRecord("ext-2", "2.2.2.2", 60000, "u", "p", "suspended")]);
        var (sut, _) = CreateSut(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(payload) });
        var account = ProviderAccount.Create("Oxylabs", ProxyProviderType.Oxylabs, "n/a");

        var result = await sut.SyncProxiesAsync(account, "{\"Username\":\"acct\",\"Password\":\"secret\"}", CancellationToken.None);

        result.Proxies.Select(p => p.ExternalId).ShouldBe(["ext-1"]);
    }
}
```

- [ ] **Step 3: Run to verify failure, then implement**

Run: `dotnet test src/Tests/Proxies.Tests --filter "FullyQualifiedName~OxylabsAdapterTests"` — expect compile failure.

```csharp
// src/Modules/Proxies/Modules.Proxies/Providers/Oxylabs/OxylabsAdapter.cs
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FSH.Modules.Proxies.Contracts;
using FSH.Modules.Proxies.Domain;

namespace FSH.Modules.Proxies.Providers.Oxylabs;

public sealed class OxylabsAdapter(IHttpClientFactory httpClientFactory) : IProxyProviderAdapter
{
    private const string ClientName = "ProxyProvider:Oxylabs";

    public ProxyProviderType ProviderType => ProxyProviderType.Oxylabs;
    public bool SupportsSync => true;
    public bool SupportsRenew => false;

    public async Task<ProviderSyncResult> SyncProxiesAsync(ProviderAccount account, string decryptedCredentials, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(account);

        var credentials = JsonSerializer.Deserialize<OxylabsCredentials>(decryptedCredentials)
            ?? throw new InvalidOperationException("Oxylabs credentials could not be parsed.");

        using var client = httpClientFactory.CreateClient(ClientName);
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.oxylabs.io/v1/proxies");
        var basicToken = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{credentials.Username}:{credentials.Password}"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", basicToken);

        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return ProviderSyncResult.Failed($"Oxylabs returned {(int)response.StatusCode} {response.ReasonPhrase}.");
        }

        var payload = await response.Content.ReadFromJsonAsync<OxylabsProxyListResponse>(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Oxylabs returned an empty proxy list response.");

        var proxies = payload.Results
            .Where(r => string.Equals(r.Status, "active", StringComparison.OrdinalIgnoreCase))
            .Select(r => new ProviderProxyRecord(r.Id, r.Ip, r.Port, ProxyProtocol.Http, r.Username, r.Password, IsActive: true))
            .ToList();

        return ProviderSyncResult.Ok(proxies);
    }

    public Task<ProviderRenewResult> RenewProxyAsync(ProviderAccount account, string decryptedCredentials, Proxy proxy, CancellationToken cancellationToken) =>
        Task.FromResult(ProviderRenewResult.Unsupported());
}
```

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test src/Tests/Proxies.Tests --filter "FullyQualifiedName~OxylabsAdapterTests"`
Expected: PASS, 2 tests.

- [ ] **Step 5: Register and commit**

```csharp
// add inside ProxiesModule.ConfigureServices
builder.Services.AddHttpClient("ProxyProvider:Oxylabs")
    .AddHeroResilience(builder.Configuration);
builder.Services.AddScoped<IProxyProviderAdapter, FSH.Modules.Proxies.Providers.Oxylabs.OxylabsAdapter>();
```

Run: `dotnet build src/FS.Proxy.slnx && dotnet test src/Tests/Proxies.Tests`
Expected: PASS.

```bash
git add src/Modules/Proxies src/Tests/Proxies.Tests
git commit -m "feat(proxies): add Oxylabs provider adapter"
```

### Task 15: BrightData adapter

**Files:**
- Create: `src/Modules/Proxies/Modules.Proxies/Providers/BrightData/BrightDataCredentials.cs`
- Create: `src/Modules/Proxies/Modules.Proxies/Providers/BrightData/BrightDataZoneIpsResponse.cs`
- Create: `src/Modules/Proxies/Modules.Proxies/Providers/BrightData/BrightDataAdapter.cs`
- Modify: `src/Modules/Proxies/Modules.Proxies/ProxiesModule.cs`
- Test: `src/Tests/Proxies.Tests/Providers/BrightDataAdapterTests.cs`

**Interfaces:**
- Produces: `BrightDataAdapter : IProxyProviderAdapter` (`ProviderType = BrightData`, `SupportsSync = true`, `SupportsRenew = false`, same rationale). Credentials are `{ApiToken, Zone}` — BrightData scopes its IP list per "zone", so the zone name is part of the account's stored credentials, not a separate concept the rest of the module needs to know about.

- [ ] **Step 1: Define the credential shape and API response DTOs**

```csharp
// src/Modules/Proxies/Modules.Proxies/Providers/BrightData/BrightDataCredentials.cs
namespace FSH.Modules.Proxies.Providers.BrightData;

public sealed record BrightDataCredentials(string ApiToken, string Zone);
```

```csharp
// src/Modules/Proxies/Modules.Proxies/Providers/BrightData/BrightDataZoneIpsResponse.cs
using System.Text.Json.Serialization;

namespace FSH.Modules.Proxies.Providers.BrightData;

public sealed record BrightDataZoneIpsResponse(
    [property: JsonPropertyName("ips")] IReadOnlyList<BrightDataIpRecord> Ips);

public sealed record BrightDataIpRecord(
    [property: JsonPropertyName("ip")] string Ip,
    [property: JsonPropertyName("port")] int Port,
    [property: JsonPropertyName("customer")] string Customer,
    [property: JsonPropertyName("zone")] string Zone,
    [property: JsonPropertyName("password")] string Password);
```

- [ ] **Step 2: Write the failing adapter test**

```csharp
// src/Tests/Proxies.Tests/Providers/BrightDataAdapterTests.cs
using System.Net;
using System.Net.Http.Json;
using FSH.Modules.Proxies.Contracts;
using FSH.Modules.Proxies.Domain;
using FSH.Modules.Proxies.Providers.BrightData;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Proxies.Tests.Providers;

public sealed class BrightDataAdapterTests
{
    private sealed class StubHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(response);
        }
    }

    private static (BrightDataAdapter Adapter, StubHandler Handler) CreateSut(HttpResponseMessage response)
    {
        var handler = new StubHandler(response);
        var services = new ServiceCollection();
        services.AddHttpClient("ProxyProvider:BrightData").ConfigurePrimaryHttpMessageHandler(() => handler);
        var provider = services.BuildServiceProvider();
        return (new BrightDataAdapter(provider.GetRequiredService<IHttpClientFactory>()), handler);
    }

    [Fact]
    public async Task SyncProxiesAsync_Should_MapIpsToProviderProxyRecords_And_UseBearerToken()
    {
        var payload = new BrightDataZoneIpsResponse([new BrightDataIpRecord("9.9.9.9", 22225, "cust1", "residential_cl", "zone-pass")]);
        var (sut, handler) = CreateSut(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(payload) });
        var account = ProviderAccount.Create("BrightData", ProxyProviderType.BrightData, "n/a");

        var result = await sut.SyncProxiesAsync(account, "{\"ApiToken\":\"token-1\",\"Zone\":\"residential_cl\"}", CancellationToken.None);

        result.Success.ShouldBeTrue();
        result.Proxies.Single().Host.ShouldBe("9.9.9.9");
        result.Proxies.Single().Username.ShouldBe("cust1-zone-residential_cl");
        handler.LastRequest!.Headers.Authorization!.ToString().ShouldBe("Bearer token-1");
        handler.LastRequest!.RequestUri!.Query.ShouldContain("zone=residential_cl");
    }

    [Fact]
    public async Task SyncProxiesAsync_Should_ReturnFailure_When_ResponseIsNotSuccessful()
    {
        var (sut, _) = CreateSut(new HttpResponseMessage(HttpStatusCode.Forbidden));
        var account = ProviderAccount.Create("BrightData", ProxyProviderType.BrightData, "n/a");

        var result = await sut.SyncProxiesAsync(account, "{\"ApiToken\":\"bad\",\"Zone\":\"z\"}", CancellationToken.None);

        result.Success.ShouldBeFalse();
    }
}
```

- [ ] **Step 3: Run to verify failure, then implement**

Run: `dotnet test src/Tests/Proxies.Tests --filter "FullyQualifiedName~BrightDataAdapterTests"` — expect compile failure.

```csharp
// src/Modules/Proxies/Modules.Proxies/Providers/BrightData/BrightDataAdapter.cs
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Web;
using FSH.Modules.Proxies.Contracts;
using FSH.Modules.Proxies.Domain;

namespace FSH.Modules.Proxies.Providers.BrightData;

public sealed class BrightDataAdapter(IHttpClientFactory httpClientFactory) : IProxyProviderAdapter
{
    private const string ClientName = "ProxyProvider:BrightData";

    public ProxyProviderType ProviderType => ProxyProviderType.BrightData;
    public bool SupportsSync => true;
    public bool SupportsRenew => false;

    public async Task<ProviderSyncResult> SyncProxiesAsync(ProviderAccount account, string decryptedCredentials, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(account);

        var credentials = JsonSerializer.Deserialize<BrightDataCredentials>(decryptedCredentials)
            ?? throw new InvalidOperationException("BrightData credentials could not be parsed.");

        using var client = httpClientFactory.CreateClient(ClientName);
        var query = HttpUtility.ParseQueryString(string.Empty);
        query["zone"] = credentials.Zone;
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.brightdata.com/zone/ips?{query}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credentials.ApiToken);

        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return ProviderSyncResult.Failed($"BrightData returned {(int)response.StatusCode} {response.ReasonPhrase}.");
        }

        var payload = await response.Content.ReadFromJsonAsync<BrightDataZoneIpsResponse>(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("BrightData returned an empty zone IPs response.");

        var proxies = payload.Ips
            .Select(ip => new ProviderProxyRecord(
                ExternalId: $"{ip.Zone}:{ip.Ip}:{ip.Port}",
                Host: ip.Ip,
                Port: ip.Port,
                Protocol: ProxyProtocol.Http,
                Username: $"{ip.Customer}-zone-{ip.Zone}",
                Password: ip.Password,
                IsActive: true))
            .ToList();

        return ProviderSyncResult.Ok(proxies);
    }

    public Task<ProviderRenewResult> RenewProxyAsync(ProviderAccount account, string decryptedCredentials, Proxy proxy, CancellationToken cancellationToken) =>
        Task.FromResult(ProviderRenewResult.Unsupported());
}
```

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test src/Tests/Proxies.Tests --filter "FullyQualifiedName~BrightDataAdapterTests"`
Expected: PASS, 2 tests.

- [ ] **Step 5: Register and commit**

```csharp
// add inside ProxiesModule.ConfigureServices
builder.Services.AddHttpClient("ProxyProvider:BrightData")
    .AddHeroResilience(builder.Configuration);
builder.Services.AddScoped<IProxyProviderAdapter, FSH.Modules.Proxies.Providers.BrightData.BrightDataAdapter>();
```

Run: `dotnet build src/FS.Proxy.slnx && dotnet test src/Tests/Proxies.Tests`
Expected: PASS.

```bash
git add src/Modules/Proxies src/Tests/Proxies.Tests
git commit -m "feat(proxies): add BrightData provider adapter"
```

---

## Milestone E — Sync Orchestration

### Task 16: Provider account sync (reconciliation service, sync-now endpoint, periodic job)

**Files:**
- Create: `src/Modules/Proxies/Modules.Proxies/Services/IProviderAccountSyncService.cs`
- Create: `src/Modules/Proxies/Modules.Proxies/Services/ProviderAccountSyncService.cs`
- Create: `src/Modules/Proxies/Modules.Proxies.Contracts/v1/ProviderAccounts/SyncProviderAccountNowCommand.cs`
- Create: `src/Modules/Proxies/Modules.Proxies/Features/v1/ProviderAccounts/SyncProviderAccountNow/SyncProviderAccountNowCommandValidator.cs`
- Create: `src/Modules/Proxies/Modules.Proxies/Features/v1/ProviderAccounts/SyncProviderAccountNow/SyncProviderAccountNowCommandHandler.cs`
- Create: `src/Modules/Proxies/Modules.Proxies/Features/v1/ProviderAccounts/SyncProviderAccountNow/SyncProviderAccountNowEndpoint.cs`
- Create: `src/Modules/Proxies/Modules.Proxies/Jobs/ProviderAccountSyncJob.cs`
- Modify: `src/Modules/Proxies/Modules.Proxies/ProxiesModule.cs`
- Test: `src/Tests/Proxies.Tests/Services/ProviderAccountSyncServiceTests.cs`

**Interfaces:**
- Consumes: `IProxyProviderAdapterFactory` (Task 6), `ProviderAccountCredentialProtector` (Task 5).
- Produces: `IProviderAccountSyncService.SyncAsync(Guid providerAccountId, CancellationToken) : Task<int>` (returns the number of proxy rows touched — created, updated, or retired) — consumed by both the sync-now command handler and `ProviderAccountSyncJob`, so the reconciliation logic exists in exactly one place.

- [ ] **Step 1: Write the failing reconciliation service test**

```csharp
// src/Tests/Proxies.Tests/Services/ProviderAccountSyncServiceTests.cs
using FSH.Modules.Proxies.Contracts;
using FSH.Modules.Proxies.Domain;
using FSH.Modules.Proxies.Providers;
using FSH.Modules.Proxies.Services;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Proxies.Tests.Services;

public sealed class ProviderAccountSyncServiceTests
{
    private static Proxies.Tests.TestProxiesDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<FSH.Modules.Proxies.Data.ProxiesDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private sealed class FakeProtector : IProxySecretProtector
    {
        public string Protect(string plaintext) => plaintext;
        public string Unprotect(string ciphertext) => ciphertext;
    }

    [Fact]
    public async Task SyncAsync_Should_CreateNewProxy_UpdateExisting_And_RetireMissing()
    {
        await using var db = CreateDb();
        var account = ProviderAccount.Create("WebShare", ProxyProviderType.WebShare, "{}");
        var staleProxy = Proxy.Create(account.Id, "old-host", 1111, ProxyProtocol.Http, null, null, "ext-stale");
        var updatingProxy = Proxy.Create(account.Id, "old-ip", 2222, ProxyProtocol.Http, null, null, "ext-existing");
        db.ProviderAccounts.Add(account);
        db.Proxies.AddRange(staleProxy, updatingProxy);
        await db.SaveChangesAsync();

        var adapter = Substitute.For<IProxyProviderAdapter>();
        adapter.ProviderType.Returns(ProxyProviderType.WebShare);
        adapter.SyncProxiesAsync(Arg.Any<ProviderAccount>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ProviderSyncResult.Ok([
                new ProviderProxyRecord("ext-existing", "new-ip", 3333, ProxyProtocol.Http, "u", "p", true),
                new ProviderProxyRecord("ext-new", "9.9.9.9", 4444, ProxyProtocol.Http, "u2", "p2", true)]));
        var factory = Substitute.For<IProxyProviderAdapterFactory>();
        factory.GetAdapter(ProxyProviderType.WebShare).Returns(adapter);

        var sut = new ProviderAccountSyncService(db, factory, new FakeProtector());

        var touched = await sut.SyncAsync(account.Id, CancellationToken.None);

        touched.ShouldBe(3);
        (await db.Proxies.SingleAsync(p => p.ExternalId == "ext-stale")).Status.ShouldBe(ProxyStatus.Retired);
        (await db.Proxies.SingleAsync(p => p.ExternalId == "ext-existing")).Host.ShouldBe("new-ip");
        (await db.Proxies.SingleAsync(p => p.ExternalId == "ext-new")).Host.ShouldBe("9.9.9.9");
        (await db.ProviderAccounts.SingleAsync(a => a.Id == account.Id)).LastSyncStatus.ShouldNotBeNull();
    }

    [Fact]
    public async Task SyncAsync_Should_RecordFailure_When_AdapterReportsFailure()
    {
        await using var db = CreateDb();
        var account = ProviderAccount.Create("Oxylabs", ProxyProviderType.Oxylabs, "{}");
        db.ProviderAccounts.Add(account);
        await db.SaveChangesAsync();

        var adapter = Substitute.For<IProxyProviderAdapter>();
        adapter.ProviderType.Returns(ProxyProviderType.Oxylabs);
        adapter.SyncProxiesAsync(Arg.Any<ProviderAccount>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ProviderSyncResult.Failed("401 Unauthorized"));
        var factory = Substitute.For<IProxyProviderAdapterFactory>();
        factory.GetAdapter(ProxyProviderType.Oxylabs).Returns(adapter);

        var sut = new ProviderAccountSyncService(db, factory, new FakeProtector());

        var touched = await sut.SyncAsync(account.Id, CancellationToken.None);

        touched.ShouldBe(0);
        var stored = await db.ProviderAccounts.SingleAsync(a => a.Id == account.Id);
        stored.ConsecutiveSyncFailures.ShouldBe(1);
        stored.LastSyncStatus.ShouldContain("401");
    }
}
```

- [ ] **Step 2: Run to verify failure, then implement**

Run: `dotnet test src/Tests/Proxies.Tests --filter "FullyQualifiedName~ProviderAccountSyncServiceTests"` — expect compile failure.

```csharp
// src/Modules/Proxies/Modules.Proxies/Services/IProviderAccountSyncService.cs
namespace FSH.Modules.Proxies.Services;

public interface IProviderAccountSyncService
{
    Task<int> SyncAsync(Guid providerAccountId, CancellationToken cancellationToken);
}
```

```csharp
// src/Modules/Proxies/Modules.Proxies/Services/ProviderAccountSyncService.cs
using FSH.Framework.Core.Exceptions;
using FSH.Modules.Proxies.Data;
using FSH.Modules.Proxies.Domain;
using FSH.Modules.Proxies.Providers;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.Proxies.Services;

public sealed class ProviderAccountSyncService(
    ProxiesDbContext dbContext, Providers.IProxyProviderAdapterFactory adapterFactory, ProviderAccountCredentialProtector protector)
    : IProviderAccountSyncService
{
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
            return 0;
        }

        var existingProxies = await dbContext.Proxies
            .Where(p => p.ProviderAccountId == providerAccountId && p.ExternalId != null)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var byExternalId = existingProxies.ToDictionary(p => p.ExternalId!);
        var incomingExternalIds = result.Proxies.Select(p => p.ExternalId).ToHashSet();

        int touched = 0;
        foreach (var record in result.Proxies)
        {
            if (byExternalId.TryGetValue(record.ExternalId, out var existing))
            {
                existing.UpdateConnection(record.Host, record.Port, record.Protocol, record.Username, record.Password is null ? null : protector.Protect(record.Password));
            }
            else
            {
                var created = Proxy.Create(providerAccountId, record.Host, record.Port, record.Protocol, record.Username,
                    record.Password is null ? null : protector.Protect(record.Password), record.ExternalId);
                dbContext.Proxies.Add(created);
            }
            touched++;
        }

        foreach (var stale in existingProxies.Where(p => !incomingExternalIds.Contains(p.ExternalId!) && p.Status != ProxyStatus.Retired))
        {
            stale.SetStatus(ProxyStatus.Retired);
            touched++;
        }

        account.RecordSyncResult(success: true, statusMessage: $"Synced {result.Proxies.Count} proxies.");
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return touched;
    }
}
```

Note: `ProtectedPassword` is re-encrypted here with `ProviderAccountCredentialProtector`, not `ProxyPasswordProtector` — provider-sourced proxy passwords travel through the account-credential trust boundary, not the manual-proxy one, so this reuses the protector already injected into this service rather than adding a third dependency for a single field. If that distinction ever needs to be sharper (e.g. a security audit wants every `Proxy.ProtectedPassword` column encrypted under one purpose string regardless of origin), swap this one call to inject `ProxyPasswordProtector` instead — it's a one-line change.

- [ ] **Step 3: Run to verify pass**

Run: `dotnet test src/Tests/Proxies.Tests --filter "FullyQualifiedName~ProviderAccountSyncServiceTests"`
Expected: PASS, 2 tests.

- [ ] **Step 4: Sync-now command, handler, endpoint**

```csharp
// src/Modules/Proxies/Modules.Proxies.Contracts/v1/ProviderAccounts/SyncProviderAccountNowCommand.cs
using Mediator;

namespace FSH.Modules.Proxies.Contracts.v1.ProviderAccounts;

public sealed record SyncProviderAccountNowCommand(Guid ProviderAccountId) : ICommand<int>;
```

```csharp
// src/Modules/Proxies/Modules.Proxies/Features/v1/ProviderAccounts/SyncProviderAccountNow/SyncProviderAccountNowCommandValidator.cs
using FluentValidation;
using FSH.Modules.Proxies.Contracts.v1.ProviderAccounts;

namespace FSH.Modules.Proxies.Features.v1.ProviderAccounts.SyncProviderAccountNow;

public sealed class SyncProviderAccountNowCommandValidator : AbstractValidator<SyncProviderAccountNowCommand>
{
    public SyncProviderAccountNowCommandValidator() => RuleFor(x => x.ProviderAccountId).NotEmpty();
}
```

```csharp
// src/Modules/Proxies/Modules.Proxies/Features/v1/ProviderAccounts/SyncProviderAccountNow/SyncProviderAccountNowCommandHandler.cs
using FSH.Modules.Proxies.Contracts.v1.ProviderAccounts;
using FSH.Modules.Proxies.Services;
using Mediator;

namespace FSH.Modules.Proxies.Features.v1.ProviderAccounts.SyncProviderAccountNow;

public sealed class SyncProviderAccountNowCommandHandler(IProviderAccountSyncService syncService)
    : ICommandHandler<SyncProviderAccountNowCommand, int>
{
    public async ValueTask<int> Handle(SyncProviderAccountNowCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        return await syncService.SyncAsync(command.ProviderAccountId, cancellationToken).ConfigureAwait(false);
    }
}
```

```csharp
// src/Modules/Proxies/Modules.Proxies/Features/v1/ProviderAccounts/SyncProviderAccountNow/SyncProviderAccountNowEndpoint.cs
using FSH.Framework.Shared.Identity.Authorization;
using FSH.Modules.Proxies.Contracts.Authorization;
using FSH.Modules.Proxies.Contracts.v1.ProviderAccounts;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FSH.Modules.Proxies.Features.v1.ProviderAccounts.SyncProviderAccountNow;

public static class SyncProviderAccountNowEndpoint
{
    internal static RouteHandlerBuilder MapSyncProviderAccountNowEndpoint(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapPost("/provider-accounts/{id:guid}/sync",
                (Guid id, IMediator mediator, CancellationToken ct) => mediator.Send(new SyncProviderAccountNowCommand(id), ct))
            .WithName("SyncProviderAccountNow")
            .WithSummary("Trigger an immediate sync for a provider account")
            .RequirePermission(ProxiesPermissions.ProviderAccounts.Update);
}
```

- [ ] **Step 5: The periodic sync job**

```csharp
// src/Modules/Proxies/Modules.Proxies/Jobs/ProviderAccountSyncJob.cs
using FSH.Modules.Proxies.Data;
using FSH.Modules.Proxies.Services;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FSH.Modules.Proxies.Jobs;

public sealed class ProviderAccountSyncJob(ProxiesDbContext dbContext, IProviderAccountSyncService syncService, ILogger<ProviderAccountSyncJob> logger)
{
    [AutomaticRetry(Attempts = 2, DelaysInSeconds = [60, 300])]
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var enabledAccountIds = await dbContext.ProviderAccounts
            .Where(a => a.IsEnabled)
            .Select(a => a.Id)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        foreach (var accountId in enabledAccountIds)
        {
            try
            {
                int touched = await syncService.SyncAsync(accountId, cancellationToken).ConfigureAwait(false);
                logger.LogInformation("Synced provider account {ProviderAccountId}: {Touched} proxies touched.", accountId, touched);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One account's failure must not abort the sync of the rest — mirrors WebhookFanoutHandler's
                // "one enqueue throws must not abort fan-out to the rest" resilience pattern.
                logger.LogError(ex, "Provider account sync failed for {ProviderAccountId}.", accountId);
            }
        }
    }
}
```

- [ ] **Step 6: Wire endpoint, register the job, and register `IProviderAccountSyncService`**

```csharp
// inside ProxiesModule.MapEndpoints
group.MapSyncProviderAccountNowEndpoint();

// hourly periodic sync — mirrors Files' PurgeOrphanedFilesJob registration exactly
var jobManager = endpoints.ServiceProvider.GetService<Hangfire.IRecurringJobManager>();
if (jobManager is not null)
{
    jobManager.AddOrUpdate<FSH.Modules.Proxies.Jobs.ProviderAccountSyncJob>(
        "proxies-provider-account-sync",
        j => j.RunAsync(CancellationToken.None),
        "0 * * * *",
        new Hangfire.RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });
}
```

```csharp
// inside ProxiesModule.ConfigureServices
builder.Services.AddScoped<IProviderAccountSyncService, ProviderAccountSyncService>();
```

- [ ] **Step 7: Build, test, commit**

Run: `dotnet build src/FS.Proxy.slnx && dotnet test src/Tests/Proxies.Tests`
Expected: PASS.

```bash
git add src/Modules/Proxies src/Tests/Proxies.Tests
git commit -m "feat(proxies): add provider account sync reconciliation, sync-now endpoint, and hourly sync job"
```

---

## Milestone F — Admin Notifications

### Task 17: Integration events for admin attention, consumed by Notifications

Per the cross-cutting research (§4), `Notifications` never exposes a "send notification" command — it only reacts to integration events it already knows about. So this task (a) defines two new events in `Modules.Proxies.Contracts`, (b) publishes them via the outbox from the two places that need them, and (c) adds the reacting handlers *inside* `Modules.Notifications` — the sanctioned cross-module pattern, not a `BuildingBlocks` change.

**Files:**
- Create: `src/Modules/Proxies/Modules.Proxies.Contracts/Events/ManualProxyNeedsAttentionIntegrationEvent.cs`
- Create: `src/Modules/Proxies/Modules.Proxies.Contracts/Events/ProviderAccountSyncFailedIntegrationEvent.cs`
- Modify: `src/Modules/Proxies/Modules.Proxies/Services/ProviderAccountSyncService.cs`
- Modify: `src/Modules/Proxies/Modules.Proxies/ProxiesModule.cs` (nothing to add here — `IOutboxWriter` is already available via the `Persistence`/`Eventing` package reference from Task 1)
- Modify: `src/Modules/Notifications/Modules.Notifications/Modules.Notifications.csproj`
- Create: `src/Modules/Notifications/Modules.Notifications/IntegrationEventHandlers/ManualProxyNeedsAttentionIntegrationEventHandler.cs`
- Create: `src/Modules/Notifications/Modules.Notifications/IntegrationEventHandlers/ProviderAccountSyncFailedIntegrationEventHandler.cs`
- Test: `src/Tests/Proxies.Tests/Services/ProviderAccountSyncServiceNotificationTests.cs`
- Test: `src/Tests/Notifications.Tests/IntegrationEventHandlers/ManualProxyNeedsAttentionIntegrationEventHandlerTests.cs`

**Interfaces:**
- Produces: `ManualProxyNeedsAttentionIntegrationEvent(Guid Id, DateTime OccurredOnUtc, string? TenantId, string CorrelationId, string Source, Guid ProxyId, string Host) : IIntegrationEvent`; `ProviderAccountSyncFailedIntegrationEvent(Guid Id, DateTime OccurredOnUtc, string? TenantId, string CorrelationId, string Source, Guid ProviderAccountId, string ProviderAccountName, int ConsecutiveFailures, string? LastErrorMessage) : IIntegrationEvent`. `TenantId` is always `null` here — every `Proxies` entity is `IGlobalEntity`, so there is no tenant to stamp.
- Consumes: `IOutboxWriter.AddAsync<T>(T @event, CancellationToken)` (from `FSH.Framework.Eventing.Abstractions`, already referenced transitively per Task 1's `Persistence`/`Jobs` references — add an explicit `ProjectReference` to `..\..\..\BuildingBlocks\Eventing.Abstractions\Eventing.Abstractions.csproj` in `Modules.Proxies.csproj` if the build doesn't already resolve `IOutboxWriter`).

- [ ] **Step 1: Define the two events**

```csharp
// src/Modules/Proxies/Modules.Proxies.Contracts/Events/ManualProxyNeedsAttentionIntegrationEvent.cs
using FSH.Framework.Eventing.Abstractions;

namespace FSH.Modules.Proxies.Contracts.Events;

public sealed record ManualProxyNeedsAttentionIntegrationEvent(
    Guid Id, DateTime OccurredOnUtc, string? TenantId, string CorrelationId, string Source,
    Guid ProxyId, string Host) : IIntegrationEvent;
```

```csharp
// src/Modules/Proxies/Modules.Proxies.Contracts/Events/ProviderAccountSyncFailedIntegrationEvent.cs
using FSH.Framework.Eventing.Abstractions;

namespace FSH.Modules.Proxies.Contracts.Events;

public sealed record ProviderAccountSyncFailedIntegrationEvent(
    Guid Id, DateTime OccurredOnUtc, string? TenantId, string CorrelationId, string Source,
    Guid ProviderAccountId, string ProviderAccountName, int ConsecutiveFailures, string? LastErrorMessage) : IIntegrationEvent;
```

Add `<PackageReference Include="FSH.Framework.Eventing.Abstractions" />`-equivalent `ProjectReference` (`..\..\..\BuildingBlocks\Eventing.Abstractions\Eventing.Abstractions.csproj`) to `Modules.Proxies.Contracts.csproj`.

- [ ] **Step 2: Write the failing publish test**

```csharp
// src/Tests/Proxies.Tests/Services/ProviderAccountSyncServiceNotificationTests.cs
using FSH.Framework.Eventing.Abstractions;
using FSH.Modules.Proxies.Contracts;
using FSH.Modules.Proxies.Contracts.Events;
using FSH.Modules.Proxies.Domain;
using FSH.Modules.Proxies.Providers;
using FSH.Modules.Proxies.Services;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Proxies.Tests.Services;

public sealed class ProviderAccountSyncServiceNotificationTests
{
    private static Proxies.Tests.TestProxiesDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<FSH.Modules.Proxies.Data.ProxiesDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private sealed class FakeProtector : IProxySecretProtector
    {
        public string Protect(string plaintext) => plaintext;
        public string Unprotect(string ciphertext) => ciphertext;
    }

    [Fact]
    public async Task SyncAsync_Should_PublishSyncFailedEvent_When_FailureThresholdReached()
    {
        await using var db = CreateDb();
        var account = ProviderAccount.Create("Oxylabs", ProxyProviderType.Oxylabs, "{}");
        db.ProviderAccounts.Add(account);
        await db.SaveChangesAsync();

        var adapter = Substitute.For<IProxyProviderAdapter>();
        adapter.ProviderType.Returns(ProxyProviderType.Oxylabs);
        adapter.SyncProxiesAsync(Arg.Any<ProviderAccount>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ProviderSyncResult.Failed("401"));
        var factory = Substitute.For<IProxyProviderAdapterFactory>();
        factory.GetAdapter(ProxyProviderType.Oxylabs).Returns(adapter);
        var outbox = Substitute.For<IOutboxWriter>();

        var sut = new ProviderAccountSyncService(db, factory, new FakeProtector(), outbox);

        // Third consecutive failure crosses the threshold (>=3).
        await sut.SyncAsync(account.Id, CancellationToken.None);
        await sut.SyncAsync(account.Id, CancellationToken.None);
        await sut.SyncAsync(account.Id, CancellationToken.None);

        await outbox.Received(1).AddAsync(Arg.Any<ProviderAccountSyncFailedIntegrationEvent>(), Arg.Any<CancellationToken>());
    }
}
```

- [ ] **Step 3: Run to verify failure, then modify `ProviderAccountSyncService` to publish**

Run: `dotnet test src/Tests/Proxies.Tests --filter "FullyQualifiedName~ProviderAccountSyncServiceNotificationTests"` — expect compile failure (constructor shape changed).

```csharp
// replace ProviderAccountSyncService's constructor and the failure branch of SyncAsync (Task 16's file)
public sealed class ProviderAccountSyncService(
    ProxiesDbContext dbContext, Providers.IProxyProviderAdapterFactory adapterFactory,
    ProviderAccountCredentialProtector protector, FSH.Framework.Eventing.Abstractions.IOutboxWriter outboxWriter)
    : IProviderAccountSyncService
{
    private const int SyncFailureNotificationThreshold = 3;

    public async Task<int> SyncAsync(Guid providerAccountId, CancellationToken cancellationToken)
    {
        var account = await dbContext.ProviderAccounts.FirstOrDefaultAsync(x => x.Id == providerAccountId, cancellationToken).ConfigureAwait(false)
            ?? throw new FSH.Framework.Core.Exceptions.NotFoundException($"Provider account {providerAccountId} not found.");

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
                    new FSH.Modules.Proxies.Contracts.Events.ProviderAccountSyncFailedIntegrationEvent(
                        Guid.CreateVersion7(), DateTime.UtcNow, TenantId: null, Guid.NewGuid().ToString(), "Proxies",
                        account.Id, account.Name, account.ConsecutiveSyncFailures, result.ErrorMessage),
                    cancellationToken).ConfigureAwait(false);
            }
            return 0;
        }

        // ... rest of the method (proxy upsert/retire loop) is unchanged from Task 16.
        return 0; // placeholder marker for the plan doc only — the real body keeps Task 16's loop verbatim here.
    }
}
```

The `return 0; // placeholder marker...` line above is a plan-authoring note, not code to type in — when editing the real file, keep Task 16's existing upsert/retire loop and `account.RecordSyncResult(success: true, ...)` call exactly as already written; only the constructor and the failure branch change.

- [ ] **Step 4: Register `IOutboxWriter` resolution (it's already registered by the framework's eventing setup — this module just needs the DI graph to resolve it, which it will automatically once the `Eventing.Abstractions` reference from Step 1 is in place) and run the test**

Run: `dotnet test src/Tests/Proxies.Tests --filter "FullyQualifiedName~ProviderAccountSyncServiceNotificationTests"`
Expected: PASS.

- [ ] **Step 5: Also publish `ManualProxyNeedsAttentionIntegrationEvent` — defer the actual call site to Task 19 (renewal orchestration), which is the only place that knows a Manual proxy's renewal was attempted and unsupported. No code changes here; this step is a pointer so the two events aren't confused as both belonging to this task's call sites.**

- [ ] **Step 6: Add the Notifications-side handlers**

```xml
<!-- add to src/Modules/Notifications/Modules.Notifications/Modules.Notifications.csproj -->
<ProjectReference Include="..\..\Proxies\Modules.Proxies.Contracts\Modules.Proxies.Contracts.csproj" />
```

```csharp
// src/Modules/Notifications/Modules.Notifications/IntegrationEventHandlers/ManualProxyNeedsAttentionIntegrationEventHandler.cs
using FSH.Framework.Eventing.Abstractions;
using FSH.Modules.Notifications.Domain;
using FSH.Modules.Proxies.Contracts.Events;
using Microsoft.Extensions.Logging;

namespace FSH.Modules.Notifications.IntegrationEventHandlers;

public sealed class ManualProxyNeedsAttentionIntegrationEventHandler(
    NotificationsDbContext db, ILogger<ManualProxyNeedsAttentionIntegrationEventHandler> logger)
    : IIntegrationEventHandler<ManualProxyNeedsAttentionIntegrationEvent>
{
    public async Task HandleAsync(ManualProxyNeedsAttentionIntegrationEvent @event, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(@event);

        var notification = Notification.Create(
            userId: null,
            type: "proxies.manual-needs-attention",
            title: "Manual proxy needs replacement",
            body: $"Proxy {@event.Host} was disabled by policy and has no automated renewal. Replace it manually.",
            link: $"/proxies?highlight={@event.ProxyId}",
            source: @event.Source,
            metadata: new { @event.ProxyId, @event.Host });

        db.Notifications.Add(notification);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        logger.LogInformation("Recorded manual-proxy-needs-attention notification for proxy {ProxyId}.", @event.ProxyId);
    }
}
```

```csharp
// src/Modules/Notifications/Modules.Notifications/IntegrationEventHandlers/ProviderAccountSyncFailedIntegrationEventHandler.cs
using FSH.Framework.Eventing.Abstractions;
using FSH.Modules.Notifications.Domain;
using FSH.Modules.Proxies.Contracts.Events;
using Microsoft.Extensions.Logging;

namespace FSH.Modules.Notifications.IntegrationEventHandlers;

public sealed class ProviderAccountSyncFailedIntegrationEventHandler(
    NotificationsDbContext db, ILogger<ProviderAccountSyncFailedIntegrationEventHandler> logger)
    : IIntegrationEventHandler<ProviderAccountSyncFailedIntegrationEvent>
{
    public async Task HandleAsync(ProviderAccountSyncFailedIntegrationEvent @event, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(@event);

        var notification = Notification.Create(
            userId: null,
            type: "proxies.provider-sync-failed",
            title: $"Provider account '{@event.ProviderAccountName}' sync is failing",
            body: $"{@event.ConsecutiveFailures} consecutive sync failures. Last error: {@event.LastErrorMessage ?? "unknown"}.",
            link: $"/proxies/provider-accounts/{@event.ProviderAccountId}",
            source: @event.Source,
            metadata: new { @event.ProviderAccountId, @event.ConsecutiveFailures });

        db.Notifications.Add(notification);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        logger.LogWarning("Recorded provider-sync-failed notification for account {ProviderAccountId}.", @event.ProviderAccountId);
    }
}
```

Verify the exact `Notification.Create(...)` signature against the real file (`src/Modules/Notifications/Modules.Notifications/Domain/Notification.cs`) before typing this — it's shown here matching the shape used by `MentionedInChannelIntegrationEventHandler` in the cross-cutting research, but confirm parameter names/order and whether `userId` accepts `null` for a broadcast-style admin notification (if it doesn't, check whether Notifications has a "for all admins" broadcast helper, or whether every current recipient is a specific user and this needs a different distribution mechanism — e.g. iterating current users holding `ProxiesPermissions.ProviderAccounts.View`).

- [ ] **Step 7: Write and pass a Notifications-side handler test**

```csharp
// src/Tests/Notifications.Tests/IntegrationEventHandlers/ManualProxyNeedsAttentionIntegrationEventHandlerTests.cs
using FSH.Modules.Notifications.IntegrationEventHandlers;
using FSH.Modules.Proxies.Contracts.Events;
using Shouldly;
using Xunit;

namespace Notifications.Tests.IntegrationEventHandlers;

public sealed class ManualProxyNeedsAttentionIntegrationEventHandlerTests
{
    // Follow this test project's existing pattern for constructing a NotificationsDbContext test
    // double (see Notifications.Tests' existing handler tests for the exact fixture helper name)
    // and assert a single Notification row is created with Type == "proxies.manual-needs-attention".
    [Fact(Skip = "Fill in using this project's existing NotificationsDbContext test fixture helper.")]
    public Task HandleAsync_Should_CreateNotification() => Task.CompletedTask;
}
```

Replace the `[Fact(Skip = ...)]` placeholder with a real assertion once you've located `Notifications.Tests`' existing DbContext test-double helper (used by its own handler tests) — reuse it rather than inventing a second one; this is the one deliberate exception to "no placeholders" in this plan, because the exact fixture helper name isn't known until this file is opened, and guessing it wrong would produce code that doesn't compile against the real test project.

- [ ] **Step 8: Build and run both test projects**

Run: `dotnet build src/FS.Proxy.slnx && dotnet test src/Tests/Proxies.Tests && dotnet test src/Tests/Notifications.Tests`
Expected: PASS (the Notifications test is `Skip`ped until Step 7's follow-up is done — do that follow-up now, before committing, rather than leaving a skipped test in the codebase).

- [ ] **Step 9: Commit**

```bash
git add src/Modules/Proxies src/Modules/Notifications src/Tests/Proxies.Tests src/Tests/Notifications.Tests
git commit -m "feat(proxies): publish admin-attention integration events, handled by Notifications"
```

---

## Milestone G — Policy Engine, Renewal, and Health Checks

### Task 18: Policy evaluation service

**Files:**
- Create: `src/Modules/Proxies/Modules.Proxies/Services/IPolicyEvaluationService.cs`
- Create: `src/Modules/Proxies/Modules.Proxies/Services/PolicyEvaluationService.cs`
- Test: `src/Tests/Proxies.Tests/Services/PolicyEvaluationServiceTests.cs`

**Interfaces:**
- Produces: `IPolicyEvaluationService.EvaluateAsync(Guid proxyId, CancellationToken) : Task` — called inline, immediately after any `ProxyUsageEvent` is persisted (Task 20's health-check job, Task 24's feedback endpoint). Internally may call `IProxyRenewalService.TriggerAsync` (Task 19) — that interface is defined next, so this task takes a forward dependency on it (both land in the same milestone; the two tasks are reviewed together).

- [ ] **Step 1: Write the failing tests**

```csharp
// src/Tests/Proxies.Tests/Services/PolicyEvaluationServiceTests.cs
using FSH.Modules.Proxies.Contracts;
using FSH.Modules.Proxies.Domain;
using FSH.Modules.Proxies.Services;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Proxies.Tests.Services;

public sealed class PolicyEvaluationServiceTests
{
    private static Proxies.Tests.TestProxiesDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<FSH.Modules.Proxies.Data.ProxiesDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static async Task<(Proxy Proxy, Tag Tag, PolicyProfile Policy)> SeedProxyWithPolicyAsync(
        Proxies.Tests.TestProxiesDbContext db, PolicyProfileType type, int threshold, int minReporters)
    {
        var tag = Tag.Create("pais:cl");
        var policy = PolicyProfile.Create("critical", type, threshold, windowMinutes: 60, minDistinctReporters: minReporters);
        var proxy = Proxy.Create(ManualProviderAccount.Id, "1.1.1.1", 8080, ProxyProtocol.Http, null, null, null);
        proxy.AssignTag(tag.Id);
        db.Tags.Add(tag);
        db.PolicyProfiles.Add(policy);
        db.Proxies.Add(proxy);
        db.Set<TagPolicyAssignment>().Add(TagPolicyAssignment.Create(tag.Id, policy.Id));
        await db.SaveChangesAsync();
        return (proxy, tag, policy);
    }

    [Fact]
    public async Task EvaluateAsync_Should_Disable_When_ThresholdAndReportersReached()
    {
        await using var db = CreateDb();
        var (proxy, _, _) = await SeedProxyWithPolicyAsync(db, PolicyProfileType.AutoDisable, threshold: 2, minReporters: 2);
        var reporterA = ApiClient.Create("scraper-a", "hash-a");
        var reporterB = ApiClient.Create("scraper-b", "hash-b");
        db.ApiClients.AddRange(reporterA, reporterB);
        db.ProxyUsageEvents.AddRange(
            ProxyUsageEvent.Create(proxy.Id, UsageEventSource.ConsumerFeedback, UsageEventOutcome.Banned, null, reporterA.Id, null),
            ProxyUsageEvent.Create(proxy.Id, UsageEventSource.ConsumerFeedback, UsageEventOutcome.Banned, null, reporterB.Id, null));
        await db.SaveChangesAsync();
        var renewalService = Substitute.For<IProxyRenewalService>();
        var sut = new PolicyEvaluationService(db, renewalService);

        await sut.EvaluateAsync(proxy.Id, CancellationToken.None);

        (await db.Proxies.SingleAsync(p => p.Id == proxy.Id)).Status.ShouldBe(ProxyStatus.Disabled);
        await renewalService.DidNotReceive().TriggerAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EvaluateAsync_Should_TriggerRenewal_When_PolicyIsAutoDisableAndRenew()
    {
        await using var db = CreateDb();
        var (proxy, _, _) = await SeedProxyWithPolicyAsync(db, PolicyProfileType.AutoDisableAndRenew, threshold: 1, minReporters: 1);
        var reporter = ApiClient.Create("scraper-a", "hash-a");
        db.ApiClients.Add(reporter);
        db.ProxyUsageEvents.Add(ProxyUsageEvent.Create(proxy.Id, UsageEventSource.ConsumerFeedback, UsageEventOutcome.Failure, null, reporter.Id, null));
        await db.SaveChangesAsync();
        var renewalService = Substitute.For<IProxyRenewalService>();
        var sut = new PolicyEvaluationService(db, renewalService);

        await sut.EvaluateAsync(proxy.Id, CancellationToken.None);

        await renewalService.Received(1).TriggerAsync(proxy.Id, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EvaluateAsync_Should_DoNothing_When_ThresholdNotReached()
    {
        await using var db = CreateDb();
        var (proxy, _, _) = await SeedProxyWithPolicyAsync(db, PolicyProfileType.AutoDisable, threshold: 5, minReporters: 1);
        var reporter = ApiClient.Create("scraper-a", "hash-a");
        db.ApiClients.Add(reporter);
        db.ProxyUsageEvents.Add(ProxyUsageEvent.Create(proxy.Id, UsageEventSource.ConsumerFeedback, UsageEventOutcome.Failure, null, reporter.Id, null));
        await db.SaveChangesAsync();
        var sut = new PolicyEvaluationService(db, Substitute.For<IProxyRenewalService>());

        await sut.EvaluateAsync(proxy.Id, CancellationToken.None);

        (await db.Proxies.SingleAsync(p => p.Id == proxy.Id)).Status.ShouldBe(ProxyStatus.Testing);
    }

    [Fact]
    public async Task EvaluateAsync_Should_DoNothing_When_PolicyIsManual()
    {
        await using var db = CreateDb();
        var (proxy, _, _) = await SeedProxyWithPolicyAsync(db, PolicyProfileType.Manual, threshold: 1, minReporters: 1);
        var reporter = ApiClient.Create("scraper-a", "hash-a");
        db.ApiClients.Add(reporter);
        db.ProxyUsageEvents.Add(ProxyUsageEvent.Create(proxy.Id, UsageEventSource.ConsumerFeedback, UsageEventOutcome.Banned, null, reporter.Id, null));
        await db.SaveChangesAsync();
        var sut = new PolicyEvaluationService(db, Substitute.For<IProxyRenewalService>());

        await sut.EvaluateAsync(proxy.Id, CancellationToken.None);

        (await db.Proxies.SingleAsync(p => p.Id == proxy.Id)).Status.ShouldBe(ProxyStatus.Testing);
    }

    [Fact]
    public async Task EvaluateAsync_Should_DoNothing_When_NoPolicyAssigned()
    {
        await using var db = CreateDb();
        var proxy = Proxy.Create(ManualProviderAccount.Id, "1.1.1.1", 8080, ProxyProtocol.Http, null, null, null);
        db.Proxies.Add(proxy);
        db.ProxyUsageEvents.Add(ProxyUsageEvent.Create(proxy.Id, UsageEventSource.ConsumerFeedback, UsageEventOutcome.Banned, null, null, null));
        await db.SaveChangesAsync();
        var sut = new PolicyEvaluationService(db, Substitute.For<IProxyRenewalService>());

        await sut.EvaluateAsync(proxy.Id, CancellationToken.None);

        (await db.Proxies.SingleAsync(p => p.Id == proxy.Id)).Status.ShouldBe(ProxyStatus.Testing);
    }
}
```

- [ ] **Step 2: Run to verify failure, then implement**

Run: `dotnet test src/Tests/Proxies.Tests --filter "FullyQualifiedName~PolicyEvaluationServiceTests"` — expect compile failure (`IProxyRenewalService`, `PolicyEvaluationService` don't exist).

```csharp
// src/Modules/Proxies/Modules.Proxies/Services/IPolicyEvaluationService.cs
namespace FSH.Modules.Proxies.Services;

public interface IPolicyEvaluationService
{
    Task EvaluateAsync(Guid proxyId, CancellationToken cancellationToken);
}
```

```csharp
// src/Modules/Proxies/Modules.Proxies/Services/PolicyEvaluationService.cs
using FSH.Modules.Proxies.Data;
using FSH.Modules.Proxies.Domain;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.Proxies.Services;

public sealed class PolicyEvaluationService(ProxiesDbContext dbContext, IProxyRenewalService renewalService) : IPolicyEvaluationService
{
    public async Task EvaluateAsync(Guid proxyId, CancellationToken cancellationToken)
    {
        var proxy = await dbContext.Proxies.FirstOrDefaultAsync(p => p.Id == proxyId, cancellationToken).ConfigureAwait(false);
        if (proxy is null) return;

        var tagIds = await dbContext.Set<ProxyTagAssignment>().Where(a => a.ProxyId == proxyId).Select(a => a.TagId).ToListAsync(cancellationToken).ConfigureAwait(false);
        if (tagIds.Count == 0) return;

        // Most-restrictive-wins conflict rule from the spec: rank AutoDisableAndRenew(2) > AutoDisable(1) > Manual(0).
        var policy = await dbContext.Set<TagPolicyAssignment>()
            .Where(a => tagIds.Contains(a.TagId))
            .Join(dbContext.PolicyProfiles, a => a.PolicyProfileId, p => p.Id, (a, p) => p)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var resolved = policy.OrderByDescending(p => p.RestrictivenessRank).FirstOrDefault();
        if (resolved is null || resolved.Type == PolicyProfileType.Manual) return;

        var windowStart = DateTime.UtcNow.AddMinutes(-resolved.WindowMinutes);
        var negativeEvents = await dbContext.ProxyUsageEvents
            .Where(e => e.ProxyId == proxyId && e.OccurredAtUtc >= windowStart && e.Outcome != UsageEventOutcome.Success)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        int failureCount = negativeEvents.Count;
        int distinctReporters = negativeEvents
            .Select(e => e.Source == UsageEventSource.SystemHealthCheck ? "system" : e.ReportedByApiClientId?.ToString() ?? "unknown")
            .Distinct()
            .Count();

        if (failureCount < resolved.FailureThreshold || distinctReporters < resolved.MinDistinctReporters)
        {
            return;
        }

        proxy.SetStatus(ProxyStatus.Disabled);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        if (resolved.Type == PolicyProfileType.AutoDisableAndRenew)
        {
            await renewalService.TriggerAsync(proxyId, cancellationToken).ConfigureAwait(false);
        }
    }
}
```

- [ ] **Step 3: Run to verify pass**

Run: `dotnet test src/Tests/Proxies.Tests --filter "FullyQualifiedName~PolicyEvaluationServiceTests"`
Expected: PASS, 5 tests (this compiles against `IProxyRenewalService` from Task 19 below — write that interface first if executing tasks strictly in order, or implement both tasks in the same sitting since they're interdependent).

- [ ] **Step 4: Register and commit (deferred to the end of Task 19, since both services are registered together there)**

### Task 19: Renewal orchestration

**Files:**
- Create: `src/Modules/Proxies/Modules.Proxies/Services/IProxyRenewalService.cs`
- Create: `src/Modules/Proxies/Modules.Proxies/Services/ProxyRenewalService.cs`
- Modify: `src/Modules/Proxies/Modules.Proxies/ProxiesModule.cs`
- Test: `src/Tests/Proxies.Tests/Services/ProxyRenewalServiceTests.cs`

**Interfaces:**
- Consumes: `IProxyProviderAdapterFactory` (Task 6), `ProviderAccountCredentialProtector`/`ProxyPasswordProtector` (Task 5), `IOutboxWriter` (Task 17).
- Produces: `IProxyRenewalService.TriggerAsync(Guid proxyId, CancellationToken) : Task` — this is the interface Task 18 already depends on.

- [ ] **Step 1: Write the failing tests**

```csharp
// src/Tests/Proxies.Tests/Services/ProxyRenewalServiceTests.cs
using FSH.Framework.Eventing.Abstractions;
using FSH.Modules.Proxies.Contracts;
using FSH.Modules.Proxies.Contracts.Events;
using FSH.Modules.Proxies.Domain;
using FSH.Modules.Proxies.Providers;
using FSH.Modules.Proxies.Services;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Proxies.Tests.Services;

public sealed class ProxyRenewalServiceTests
{
    private static Proxies.Tests.TestProxiesDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<FSH.Modules.Proxies.Data.ProxiesDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private sealed class FakeProtector : IProxySecretProtector
    {
        public string Protect(string plaintext) => plaintext;
        public string Unprotect(string ciphertext) => ciphertext;
    }

    [Fact]
    public async Task TriggerAsync_Should_UpdateProxyAndMarkRenewed_When_AdapterSupportsRenewAndSucceeds()
    {
        await using var db = CreateDb();
        var account = ProviderAccount.Create("WebShare", ProxyProviderType.WebShare, "{}");
        var proxy = Proxy.Create(account.Id, "old-ip", 1111, ProxyProtocol.Http, "u", "p", "ext-1");
        proxy.SetStatus(ProxyStatus.Disabled);
        db.ProviderAccounts.Add(account);
        db.Proxies.Add(proxy);
        await db.SaveChangesAsync();

        var adapter = Substitute.For<IProxyProviderAdapter>();
        adapter.SupportsRenew.Returns(true);
        adapter.RenewProxyAsync(Arg.Any<ProviderAccount>(), Arg.Any<string>(), Arg.Any<Proxy>(), Arg.Any<CancellationToken>())
            .Returns(ProviderRenewResult.Ok(new ProviderProxyRecord("ext-1", "new-ip", 2222, ProxyProtocol.Http, "u2", "p2", true)));
        var factory = Substitute.For<IProxyProviderAdapterFactory>();
        factory.GetAdapter(ProxyProviderType.WebShare).Returns(adapter);
        var outbox = Substitute.For<IOutboxWriter>();

        var sut = new ProxyRenewalService(db, factory, new FakeProtector(), outbox);

        await sut.TriggerAsync(proxy.Id, CancellationToken.None);

        var stored = await db.Proxies.SingleAsync(p => p.Id == proxy.Id);
        stored.Host.ShouldBe("new-ip");
        stored.Status.ShouldBe(ProxyStatus.Testing);
        stored.LastRenewedAtUtc.ShouldNotBeNull();
        await outbox.DidNotReceive().AddAsync(Arg.Any<ManualProxyNeedsAttentionIntegrationEvent>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TriggerAsync_Should_PublishNeedsAttentionEvent_When_AdapterDoesNotSupportRenew()
    {
        await using var db = CreateDb();
        var proxy = Proxy.Create(ManualProviderAccount.Id, "1.1.1.1", 1111, ProxyProtocol.Http, null, null, null);
        db.Proxies.Add(proxy);
        await db.SaveChangesAsync();

        var adapter = Substitute.For<IProxyProviderAdapter>();
        adapter.SupportsRenew.Returns(false);
        var factory = Substitute.For<IProxyProviderAdapterFactory>();
        factory.GetAdapter(ProxyProviderType.Manual).Returns(adapter);
        var outbox = Substitute.For<IOutboxWriter>();

        var sut = new ProxyRenewalService(db, factory, new FakeProtector(), outbox);

        await sut.TriggerAsync(proxy.Id, CancellationToken.None);

        await outbox.Received(1).AddAsync(
            Arg.Is<ManualProxyNeedsAttentionIntegrationEvent>(e => e.ProxyId == proxy.Id && e.Host == "1.1.1.1"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TriggerAsync_Should_PublishNeedsAttentionEvent_When_RenewalAttemptFails()
    {
        await using var db = CreateDb();
        var account = ProviderAccount.Create("WebShare", ProxyProviderType.WebShare, "{}");
        var proxy = Proxy.Create(account.Id, "1.1.1.1", 1111, ProxyProtocol.Http, null, null, "ext-1");
        db.ProviderAccounts.Add(account);
        db.Proxies.Add(proxy);
        await db.SaveChangesAsync();

        var adapter = Substitute.For<IProxyProviderAdapter>();
        adapter.SupportsRenew.Returns(true);
        adapter.RenewProxyAsync(Arg.Any<ProviderAccount>(), Arg.Any<string>(), Arg.Any<Proxy>(), Arg.Any<CancellationToken>())
            .Returns(ProviderRenewResult.Failed("provider rejected the rotation request"));
        var factory = Substitute.For<IProxyProviderAdapterFactory>();
        factory.GetAdapter(ProxyProviderType.WebShare).Returns(adapter);
        var outbox = Substitute.For<IOutboxWriter>();

        var sut = new ProxyRenewalService(db, factory, new FakeProtector(), outbox);

        await sut.TriggerAsync(proxy.Id, CancellationToken.None);

        await outbox.Received(1).AddAsync(Arg.Any<ManualProxyNeedsAttentionIntegrationEvent>(), Arg.Any<CancellationToken>());
    }
}
```

- [ ] **Step 2: Run to verify failure, then implement**

Run: `dotnet test src/Tests/Proxies.Tests --filter "FullyQualifiedName~ProxyRenewalServiceTests"` — expect compile failure.

```csharp
// src/Modules/Proxies/Modules.Proxies/Services/IProxyRenewalService.cs
namespace FSH.Modules.Proxies.Services;

public interface IProxyRenewalService
{
    Task TriggerAsync(Guid proxyId, CancellationToken cancellationToken);
}
```

```csharp
// src/Modules/Proxies/Modules.Proxies/Services/ProxyRenewalService.cs
using FSH.Framework.Eventing.Abstractions;
using FSH.Modules.Proxies.Contracts.Events;
using FSH.Modules.Proxies.Data;
using FSH.Modules.Proxies.Providers;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.Proxies.Services;

/// <summary>
/// Handles both the "no automated renewal exists" case (Manual proxies, or any adapter that
/// doesn't support it) and the "renewal was attempted but failed" case identically — in both,
/// an admin needs to look at the proxy by hand, so both raise the same
/// ManualProxyNeedsAttentionIntegrationEvent regardless of which provider it came from.
/// </summary>
public sealed class ProxyRenewalService(
    ProxiesDbContext dbContext, IProxyProviderAdapterFactory adapterFactory,
    ProviderAccountCredentialProtector protector, IOutboxWriter outboxWriter)
    : IProxyRenewalService
{
    public async Task TriggerAsync(Guid proxyId, CancellationToken cancellationToken)
    {
        var proxy = await dbContext.Proxies.FirstOrDefaultAsync(p => p.Id == proxyId, cancellationToken).ConfigureAwait(false);
        if (proxy is null) return;
        var account = await dbContext.ProviderAccounts.FirstOrDefaultAsync(a => a.Id == proxy.ProviderAccountId, cancellationToken).ConfigureAwait(false);
        if (account is null) return;

        var adapter = adapterFactory.GetAdapter(account.ProviderType);

        if (!adapter.SupportsRenew)
        {
            await PublishNeedsAttentionAsync(proxy.Id, proxy.Host, cancellationToken).ConfigureAwait(false);
            return;
        }

        var decrypted = protector.Unprotect(account.ProtectedCredentials);
        var result = await adapter.RenewProxyAsync(account, decrypted, proxy, cancellationToken).ConfigureAwait(false);

        if (!result.Success)
        {
            await PublishNeedsAttentionAsync(proxy.Id, proxy.Host, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (result.UpdatedProxy is { } updated)
        {
            proxy.UpdateConnection(updated.Host, updated.Port, updated.Protocol, updated.Username,
                updated.Password is null ? null : protector.Protect(updated.Password));
        }
        proxy.MarkRenewed();
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task PublishNeedsAttentionAsync(Guid proxyId, string host, CancellationToken cancellationToken) =>
        await outboxWriter.AddAsync(
            new ManualProxyNeedsAttentionIntegrationEvent(Guid.CreateVersion7(), DateTime.UtcNow, TenantId: null, Guid.NewGuid().ToString(), "Proxies", proxyId, host),
            cancellationToken).ConfigureAwait(false);
}
```

- [ ] **Step 3: Run to verify pass**

Run: `dotnet test src/Tests/Proxies.Tests --filter "FullyQualifiedName~ProxyRenewalServiceTests"`
Expected: PASS, 3 tests.

- [ ] **Step 4: Register both Task 18 and Task 19's services**

```csharp
// add inside ProxiesModule.ConfigureServices
builder.Services.AddScoped<IProxyRenewalService, ProxyRenewalService>();
builder.Services.AddScoped<IPolicyEvaluationService, PolicyEvaluationService>();
```

- [ ] **Step 5: Run the full Proxies test suite (this also finally confirms Task 18's tests pass against the real `ProxyRenewalService`, not just a compiled interface)**

Run: `dotnet build src/FS.Proxy.slnx && dotnet test src/Tests/Proxies.Tests`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/Modules/Proxies src/Tests/Proxies.Tests
git commit -m "feat(proxies): add policy evaluation and renewal orchestration services"
```

### Task 20: Health check target resolution, password resolution, and the active health-check job

**Files:**
- Create: `src/Modules/Proxies/Modules.Proxies/Options/ProxiesOptions.cs`
- Create: `src/Modules/Proxies/Modules.Proxies/Services/IProxyPasswordResolver.cs`
- Create: `src/Modules/Proxies/Modules.Proxies/Services/ProxyPasswordResolver.cs`
- Create: `src/Modules/Proxies/Modules.Proxies/Services/ResolvedHealthCheckTarget.cs`
- Create: `src/Modules/Proxies/Modules.Proxies/Services/IHealthCheckTargetResolver.cs`
- Create: `src/Modules/Proxies/Modules.Proxies/Services/HealthCheckTargetResolver.cs`
- Create: `src/Modules/Proxies/Modules.Proxies/Jobs/ProxyHealthCheckOutcomeClassifier.cs`
- Create: `src/Modules/Proxies/Modules.Proxies/Jobs/ProxyActiveHealthCheckJob.cs`
- Modify: `src/Modules/Proxies/Modules.Proxies/ProxiesModule.cs`
- Modify: `src/Host/FS.Proxy.Api/appsettings.json`
- Test: `src/Tests/Proxies.Tests/Services/HealthCheckTargetResolverTests.cs`
- Test: `src/Tests/Proxies.Tests/Services/ProxyPasswordResolverTests.cs`
- Test: `src/Tests/Proxies.Tests/Jobs/ProxyHealthCheckOutcomeClassifierTests.cs`

**Interfaces:**
- Produces: `ResolvedHealthCheckTarget(Guid? TargetId, string TestUrl, int? ExpectedStatusCode, string? ExpectedBodyKeyword, int TimeoutMs)`; `IHealthCheckTargetResolver.ResolveTargetsAsync(Guid proxyId, CancellationToken) : Task<IReadOnlyList<ResolvedHealthCheckTarget>>` (never empty — falls back to the configured global default); `IProxyPasswordResolver.Decrypt(Proxy proxy) : string?` (picks `ProxyPasswordProtector` for proxies attached to the well-known Manual account, `ProviderAccountCredentialProtector` for every other proxy — reused by Task 23's consumer-facing request endpoint, which needs the same decryption logic to hand real credentials back to a scraper); `ProxyHealthCheckOutcomeClassifier.Classify(bool timedOut, System.Net.HttpStatusCode? statusCode, string? body, int? expectedStatusCode, string? expectedBodyKeyword) : UsageEventOutcome` (pure function, no I/O — this is what actually gets unit-tested; the job's real HTTP-through-proxy call is I/O-bound and is instead validated by running the AppHost dev stack against a real proxy, per Step 6 below).

- [ ] **Step 1: Options and the global default target**

```csharp
// src/Modules/Proxies/Modules.Proxies/Options/ProxiesOptions.cs
using System.ComponentModel.DataAnnotations;

namespace FSH.Modules.Proxies.Options;

public sealed class ProxiesOptions
{
    [Required, Url]
    public string DefaultHealthCheckTargetUrl { get; set; } = "https://www.google.com/generate_204";

    [Range(500, 30000)]
    public int DefaultHealthCheckTimeoutMs { get; set; } = 5000;

    [Range(1, 1440)]
    public int HealthCheckIntervalMinutes { get; set; } = 15;
}
```

```json
// add to src/Host/FS.Proxy.Api/appsettings.json, top-level alongside the other *Options sections
"ProxiesOptions": {
  "DefaultHealthCheckTargetUrl": "https://www.google.com/generate_204",
  "DefaultHealthCheckTimeoutMs": 5000,
  "HealthCheckIntervalMinutes": 15
}
```

```csharp
// add inside ProxiesModule.ConfigureServices
builder.Services.AddOptions<FSH.Modules.Proxies.Options.ProxiesOptions>()
    .BindConfiguration(nameof(FSH.Modules.Proxies.Options.ProxiesOptions))
    .ValidateDataAnnotations()
    .ValidateOnStart();
```

- [ ] **Step 2: Write the failing resolver tests**

```csharp
// src/Tests/Proxies.Tests/Services/HealthCheckTargetResolverTests.cs
using FSH.Modules.Proxies.Contracts;
using FSH.Modules.Proxies.Domain;
using FSH.Modules.Proxies.Options;
using FSH.Modules.Proxies.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace Proxies.Tests.Services;

public sealed class HealthCheckTargetResolverTests
{
    private static Proxies.Tests.TestProxiesDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<FSH.Modules.Proxies.Data.ProxiesDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static IOptions<ProxiesOptions> DefaultOptions() => Options.Create(new ProxiesOptions());

    [Fact]
    public async Task ResolveTargetsAsync_Should_ReturnDistinctTargets_FromProxyTags()
    {
        await using var db = CreateDb();
        var tagCl = Tag.Create("pais:cl");
        var tagLicitaciones = Tag.Create("funcionalidad:licitaciones");
        var mercadoPublico = HealthCheckTarget.Create("Mercado Publico", "https://www.mercadopublico.cl", 200, null, 5000);
        var proxy = Proxy.Create(ManualProviderAccount.Id, "1.1.1.1", 80, ProxyProtocol.Http, null, null, null);
        proxy.AssignTag(tagCl.Id);
        proxy.AssignTag(tagLicitaciones.Id);
        db.Tags.AddRange(tagCl, tagLicitaciones);
        db.HealthCheckTargets.Add(mercadoPublico);
        db.Proxies.Add(proxy);
        db.Set<TagHealthCheckTargetAssignment>().Add(TagHealthCheckTargetAssignment.Create(tagCl.Id, mercadoPublico.Id));
        await db.SaveChangesAsync();
        var sut = new HealthCheckTargetResolver(db, DefaultOptions());

        var result = await sut.ResolveTargetsAsync(proxy.Id, CancellationToken.None);

        result.ShouldHaveSingleItem();
        result[0].TestUrl.ShouldBe("https://www.mercadopublico.cl");
        result[0].TargetId.ShouldBe(mercadoPublico.Id);
    }

    [Fact]
    public async Task ResolveTargetsAsync_Should_FallBackToGlobalDefault_When_NoTagHasATarget()
    {
        await using var db = CreateDb();
        var proxy = Proxy.Create(ManualProviderAccount.Id, "1.1.1.1", 80, ProxyProtocol.Http, null, null, null);
        db.Proxies.Add(proxy);
        await db.SaveChangesAsync();
        var sut = new HealthCheckTargetResolver(db, DefaultOptions());

        var result = await sut.ResolveTargetsAsync(proxy.Id, CancellationToken.None);

        result.ShouldHaveSingleItem();
        result[0].TargetId.ShouldBeNull();
        result[0].TestUrl.ShouldBe("https://www.google.com/generate_204");
    }
}
```

```csharp
// src/Tests/Proxies.Tests/Services/ProxyPasswordResolverTests.cs
using FSH.Modules.Proxies.Contracts;
using FSH.Modules.Proxies.Domain;
using FSH.Modules.Proxies.Services;
using Shouldly;
using Xunit;

namespace Proxies.Tests.Services;

public sealed class ProxyPasswordResolverTests
{
    private sealed class TaggedProtector(string tag) : IProxySecretProtector
    {
        public string Protect(string plaintext) => $"{tag}:{plaintext}";
        public string Unprotect(string ciphertext) => $"decrypted-by-{tag}";
    }

    [Fact]
    public void Decrypt_Should_UseManualProtector_ForManualProxies()
    {
        var sut = new ProxyPasswordResolver(new TaggedProtector("provider"), new TaggedProtector("manual"));
        var proxy = Proxy.Create(ManualProviderAccount.Id, "1.1.1.1", 80, ProxyProtocol.Http, "u", "cipher", null);

        sut.Decrypt(proxy).ShouldBe("decrypted-by-manual");
    }

    [Fact]
    public void Decrypt_Should_UseProviderProtector_ForProviderSourcedProxies()
    {
        var sut = new ProxyPasswordResolver(new TaggedProtector("provider"), new TaggedProtector("manual"));
        var proxy = Proxy.Create(Guid.NewGuid(), "1.1.1.1", 80, ProxyProtocol.Http, "u", "cipher", "ext-1");

        sut.Decrypt(proxy).ShouldBe("decrypted-by-provider");
    }

    [Fact]
    public void Decrypt_Should_ReturnNull_When_ProxyHasNoPassword()
    {
        var sut = new ProxyPasswordResolver(new TaggedProtector("provider"), new TaggedProtector("manual"));
        var proxy = Proxy.Create(ManualProviderAccount.Id, "1.1.1.1", 80, ProxyProtocol.Http, null, null, null);

        sut.Decrypt(proxy).ShouldBeNull();
    }
}
```

```csharp
// src/Tests/Proxies.Tests/Jobs/ProxyHealthCheckOutcomeClassifierTests.cs
using System.Net;
using FSH.Modules.Proxies.Domain;
using FSH.Modules.Proxies.Jobs;
using Shouldly;
using Xunit;

namespace Proxies.Tests.Jobs;

public sealed class ProxyHealthCheckOutcomeClassifierTests
{
    [Fact]
    public void Classify_Should_ReturnTimeout_When_RequestTimedOut() =>
        ProxyHealthCheckOutcomeClassifier.Classify(timedOut: true, null, null, null, null).ShouldBe(UsageEventOutcome.Timeout);

    [Fact]
    public void Classify_Should_ReturnSuccess_When_NoExpectationsAndStatusIs2xx() =>
        ProxyHealthCheckOutcomeClassifier.Classify(false, HttpStatusCode.NoContent, null, null, null).ShouldBe(UsageEventOutcome.Success);

    [Fact]
    public void Classify_Should_ReturnFailure_When_StatusDoesNotMatchExpected() =>
        ProxyHealthCheckOutcomeClassifier.Classify(false, HttpStatusCode.NotFound, null, 200, null).ShouldBe(UsageEventOutcome.Failure);

    [Fact]
    public void Classify_Should_ReturnSuccess_When_StatusAndBodyKeywordMatch() =>
        ProxyHealthCheckOutcomeClassifier.Classify(false, HttpStatusCode.OK, "licitaciones publicas activas", 200, "licitaciones").ShouldBe(UsageEventOutcome.Success);

    [Fact]
    public void Classify_Should_ReturnFailure_When_BodyKeywordMissing() =>
        ProxyHealthCheckOutcomeClassifier.Classify(false, HttpStatusCode.OK, "pagina de error", 200, "licitaciones").ShouldBe(UsageEventOutcome.Failure);
}
```

- [ ] **Step 3: Run to verify failure, then implement**

Run: `dotnet test src/Tests/Proxies.Tests --filter "FullyQualifiedName~HealthCheckTargetResolverTests|FullyQualifiedName~ProxyPasswordResolverTests|FullyQualifiedName~ProxyHealthCheckOutcomeClassifierTests"` — expect compile failure.

```csharp
// src/Modules/Proxies/Modules.Proxies/Services/IProxyPasswordResolver.cs
using FSH.Modules.Proxies.Domain;

namespace FSH.Modules.Proxies.Services;

public interface IProxyPasswordResolver
{
    string? Decrypt(Proxy proxy);
}
```

```csharp
// src/Modules/Proxies/Modules.Proxies/Services/ProxyPasswordResolver.cs
using FSH.Modules.Proxies.Domain;

namespace FSH.Modules.Proxies.Services;

public sealed class ProxyPasswordResolver(ProviderAccountCredentialProtector providerProtector, ProxyPasswordProtector manualProtector)
    : IProxyPasswordResolver
{
    public string? Decrypt(Proxy proxy)
    {
        ArgumentNullException.ThrowIfNull(proxy);
        if (proxy.ProtectedPassword is null) return null;
        IProxySecretProtector protector = proxy.ProviderAccountId == ManualProviderAccount.Id ? manualProtector : providerProtector;
        return protector.Unprotect(proxy.ProtectedPassword);
    }
}
```

```csharp
// src/Modules/Proxies/Modules.Proxies/Services/ResolvedHealthCheckTarget.cs
namespace FSH.Modules.Proxies.Services;

public sealed record ResolvedHealthCheckTarget(Guid? TargetId, string TestUrl, int? ExpectedStatusCode, string? ExpectedBodyKeyword, int TimeoutMs);
```

```csharp
// src/Modules/Proxies/Modules.Proxies/Services/IHealthCheckTargetResolver.cs
namespace FSH.Modules.Proxies.Services;

public interface IHealthCheckTargetResolver
{
    Task<IReadOnlyList<ResolvedHealthCheckTarget>> ResolveTargetsAsync(Guid proxyId, CancellationToken cancellationToken);
}
```

```csharp
// src/Modules/Proxies/Modules.Proxies/Services/HealthCheckTargetResolver.cs
using FSH.Modules.Proxies.Data;
using FSH.Modules.Proxies.Domain;
using FSH.Modules.Proxies.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace FSH.Modules.Proxies.Services;

public sealed class HealthCheckTargetResolver(ProxiesDbContext dbContext, IOptions<ProxiesOptions> options) : IHealthCheckTargetResolver
{
    public async Task<IReadOnlyList<ResolvedHealthCheckTarget>> ResolveTargetsAsync(Guid proxyId, CancellationToken cancellationToken)
    {
        var tagIds = await dbContext.Set<ProxyTagAssignment>().Where(a => a.ProxyId == proxyId).Select(a => a.TagId).ToListAsync(cancellationToken).ConfigureAwait(false);

        var targets = await dbContext.Set<TagHealthCheckTargetAssignment>()
            .Where(a => tagIds.Contains(a.TagId))
            .Join(dbContext.HealthCheckTargets, a => a.HealthCheckTargetId, t => t.Id, (a, t) => t)
            .Distinct()
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        if (targets.Count > 0)
        {
            return [.. targets.Select(t => new ResolvedHealthCheckTarget(t.Id, t.TestUrl, t.ExpectedStatusCode, t.ExpectedBodyKeyword, t.TimeoutMs))];
        }

        var defaults = options.Value;
        return [new ResolvedHealthCheckTarget(null, defaults.DefaultHealthCheckTargetUrl, null, null, defaults.DefaultHealthCheckTimeoutMs)];
    }
}
```

```csharp
// src/Modules/Proxies/Modules.Proxies/Jobs/ProxyHealthCheckOutcomeClassifier.cs
using System.Net;
using FSH.Modules.Proxies.Domain;

namespace FSH.Modules.Proxies.Jobs;

public static class ProxyHealthCheckOutcomeClassifier
{
    public static UsageEventOutcome Classify(bool timedOut, HttpStatusCode? statusCode, string? body, int? expectedStatusCode, string? expectedBodyKeyword)
    {
        if (timedOut || statusCode is null) return UsageEventOutcome.Timeout;

        bool statusOk = expectedStatusCode is { } expected ? (int)statusCode == expected : (int)statusCode is >= 200 and < 400;
        if (!statusOk) return UsageEventOutcome.Failure;

        if (!string.IsNullOrEmpty(expectedBodyKeyword) &&
            !(body?.Contains(expectedBodyKeyword, StringComparison.OrdinalIgnoreCase) ?? false))
        {
            return UsageEventOutcome.Failure;
        }

        return UsageEventOutcome.Success;
    }
}
```

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test src/Tests/Proxies.Tests --filter "FullyQualifiedName~HealthCheckTargetResolverTests|FullyQualifiedName~ProxyPasswordResolverTests|FullyQualifiedName~ProxyHealthCheckOutcomeClassifierTests"`
Expected: PASS, 10 tests.

- [ ] **Step 5: The active health-check job (real HTTP-through-proxy I/O — not unit-tested here; validated per Step 6)**

```csharp
// src/Modules/Proxies/Modules.Proxies/Jobs/ProxyActiveHealthCheckJob.cs
using System.Net;
using FSH.Modules.Proxies.Data;
using FSH.Modules.Proxies.Domain;
using FSH.Modules.Proxies.Services;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FSH.Modules.Proxies.Jobs;

public sealed class ProxyActiveHealthCheckJob(
    ProxiesDbContext dbContext, IHealthCheckTargetResolver targetResolver, IProxyPasswordResolver passwordResolver,
    IPolicyEvaluationService policyEvaluationService, ILogger<ProxyActiveHealthCheckJob> logger)
{
    [AutomaticRetry(Attempts = 0)] // a single proxy's connectivity failure IS the signal being measured — never retry the batch
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var activeProxyIds = await dbContext.Proxies
            .Where(p => p.Status == ProxyStatus.Active)
            .Select(p => p.Id)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        foreach (var proxyId in activeProxyIds)
        {
            try
            {
                await CheckOneProxyAsync(proxyId, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Active health check failed unexpectedly for proxy {ProxyId}.", proxyId);
            }
        }
    }

    private async Task CheckOneProxyAsync(Guid proxyId, CancellationToken cancellationToken)
    {
        var proxy = await dbContext.Proxies.FirstOrDefaultAsync(p => p.Id == proxyId, cancellationToken).ConfigureAwait(false);
        if (proxy is null) return;

        var targets = await targetResolver.ResolveTargetsAsync(proxyId, cancellationToken).ConfigureAwait(false);
        var password = passwordResolver.Decrypt(proxy);

        foreach (var target in targets)
        {
            var (outcome, detail) = await ProbeAsync(proxy, password, target, cancellationToken).ConfigureAwait(false);

            dbContext.ProxyUsageEvents.Add(ProxyUsageEvent.Create(
                proxyId, UsageEventSource.SystemHealthCheck, outcome, target.TargetId, reportedByApiClientId: null, detail));
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            await policyEvaluationService.EvaluateAsync(proxyId, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<(UsageEventOutcome Outcome, string? Detail)> ProbeAsync(
        Proxy proxy, string? password, ResolvedHealthCheckTarget target, CancellationToken cancellationToken)
    {
        var webProxy = new WebProxy($"{(proxy.Protocol == ProxyProtocol.Https ? "https" : "http")}://{proxy.Host}:{proxy.Port}");
        if (!string.IsNullOrEmpty(proxy.Username))
        {
            webProxy.Credentials = new NetworkCredential(proxy.Username, password);
        }
        using var handler = new SocketsHttpHandler { Proxy = webProxy, UseProxy = true };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromMilliseconds(target.TimeoutMs) };

        try
        {
            using var response = await client.GetAsync(target.TestUrl, cancellationToken).ConfigureAwait(false);
            string body = target.ExpectedBodyKeyword is null ? string.Empty : await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var outcome = ProxyHealthCheckOutcomeClassifier.Classify(false, response.StatusCode, body, target.ExpectedStatusCode, target.ExpectedBodyKeyword);
            return (outcome, outcome == UsageEventOutcome.Success ? null : $"HTTP {(int)response.StatusCode}");
        }
        catch (TaskCanceledException)
        {
            return (UsageEventOutcome.Timeout, "Request timed out");
        }
        catch (HttpRequestException ex)
        {
            return (UsageEventOutcome.Failure, ex.Message);
        }
    }
}
```

- [ ] **Step 6: Register the job and the two resolver services; validate manually against the dev stack**

```csharp
// add inside ProxiesModule.ConfigureServices
builder.Services.AddScoped<IHealthCheckTargetResolver, HealthCheckTargetResolver>();
builder.Services.AddScoped<IProxyPasswordResolver, ProxyPasswordResolver>();

// add inside ProxiesModule.MapEndpoints, alongside the sync job registration
if (jobManager is not null)
{
    jobManager.AddOrUpdate<FSH.Modules.Proxies.Jobs.ProxyActiveHealthCheckJob>(
        "proxies-active-health-check",
        j => j.RunAsync(CancellationToken.None),
        "*/15 * * * *",
        new Hangfire.RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });
}
```

Run the whole stack (`dotnet run --project src/Host/FS.Proxy.AppHost`), create a manual proxy pointed at a real reachable HTTP proxy you control (or a public test proxy you trust), enable it, and either wait for the 15-minute cadence or trigger the job manually from the Hangfire dashboard at `/jobs` (Basic Auth per `jobs.md`). Confirm a `ProxyUsageEvent` row appears with a plausible `Outcome`.

- [ ] **Step 7: Build, run the full unit test suite, commit**

Run: `dotnet build src/FS.Proxy.slnx && dotnet test src/Tests/Proxies.Tests`
Expected: PASS.

```bash
git add src/Modules/Proxies src/Host/FS.Proxy.Api/appsettings.json src/Tests/Proxies.Tests
git commit -m "feat(proxies): add health-check target resolution and the active health-check job"
```

---

## Milestone H — Consumer-Facing API (Dual Authentication)

### Task 21: ApiClient issuance (admin-only; no dedicated UI in v1 — see Milestone I)

The v1 admin UI scope you chose (Milestone I) covers proxy listing, enable/disable, provider account ABM, and manual proxy ABM — **not** a dedicated API-client management screen. This task still builds the backend endpoints, since TAG and the legacy scrapers need *some* way to get a key; until a UI lands (phase 2), an admin issues keys via the Scalar API explorer at `/scalar` using their own JWT.

**Files:**
- Create: `src/Modules/Proxies/Modules.Proxies.Contracts/Dtos/ApiClientDto.cs`
- Create: `src/Modules/Proxies/Modules.Proxies.Contracts/Dtos/CreateApiClientResult.cs`
- Create: `src/Modules/Proxies/Modules.Proxies.Contracts/v1/ApiClients/CreateApiClientCommand.cs`
- Create: `src/Modules/Proxies/Modules.Proxies.Contracts/v1/ApiClients/DeleteApiClientCommand.cs`
- Create: `src/Modules/Proxies/Modules.Proxies.Contracts/v1/ApiClients/ListApiClientsQuery.cs`
- Create: `src/Modules/Proxies/Modules.Proxies/Features/v1/ApiClients/CreateApiClient/CreateApiClientCommandValidator.cs`
- Create: `src/Modules/Proxies/Modules.Proxies/Features/v1/ApiClients/CreateApiClient/CreateApiClientCommandHandler.cs`
- Create: `src/Modules/Proxies/Modules.Proxies/Features/v1/ApiClients/CreateApiClient/CreateApiClientEndpoint.cs`
- Create: `src/Modules/Proxies/Modules.Proxies/Features/v1/ApiClients/DeleteApiClient/DeleteApiClientCommandValidator.cs`
- Create: `src/Modules/Proxies/Modules.Proxies/Features/v1/ApiClients/DeleteApiClient/DeleteApiClientCommandHandler.cs`
- Create: `src/Modules/Proxies/Modules.Proxies/Features/v1/ApiClients/DeleteApiClient/DeleteApiClientEndpoint.cs`
- Create: `src/Modules/Proxies/Modules.Proxies/Features/v1/ApiClients/ListApiClients/ListApiClientsQueryHandler.cs`
- Create: `src/Modules/Proxies/Modules.Proxies/Features/v1/ApiClients/ListApiClients/ListApiClientsEndpoint.cs`
- Modify: `src/Modules/Proxies/Modules.Proxies/ProxiesModule.cs`
- Test: `src/Tests/Proxies.Tests/Handlers/ApiClientHandlerTests.cs`

**Interfaces:**
- Produces: `ApiClientDto(Guid Id, string Name, bool IsEnabled, DateTime CreatedAtUtc, DateTime? LastUsedAtUtc)`; `CreateApiClientResult(Guid Id, string PlaintextKey)` (the only time the raw key is ever returned); `CreateApiClientCommand(string Name) : ICommand<CreateApiClientResult>`.

- [ ] **Step 1: DTOs and commands**

```csharp
// src/Modules/Proxies/Modules.Proxies.Contracts/Dtos/ApiClientDto.cs
namespace FSH.Modules.Proxies.Contracts.Dtos;

public sealed record ApiClientDto(Guid Id, string Name, bool IsEnabled, DateTime CreatedAtUtc, DateTime? LastUsedAtUtc);
```

```csharp
// src/Modules/Proxies/Modules.Proxies.Contracts/Dtos/CreateApiClientResult.cs
namespace FSH.Modules.Proxies.Contracts.Dtos;

public sealed record CreateApiClientResult(Guid Id, string PlaintextKey);
```

```csharp
// src/Modules/Proxies/Modules.Proxies.Contracts/v1/ApiClients/CreateApiClientCommand.cs
using FSH.Modules.Proxies.Contracts.Dtos;
using Mediator;

namespace FSH.Modules.Proxies.Contracts.v1.ApiClients;

public sealed record CreateApiClientCommand(string Name) : ICommand<CreateApiClientResult>;
```

```csharp
// src/Modules/Proxies/Modules.Proxies.Contracts/v1/ApiClients/DeleteApiClientCommand.cs
using Mediator;

namespace FSH.Modules.Proxies.Contracts.v1.ApiClients;

public sealed record DeleteApiClientCommand(Guid Id) : ICommand;
```

```csharp
// src/Modules/Proxies/Modules.Proxies.Contracts/v1/ApiClients/ListApiClientsQuery.cs
using FSH.Modules.Proxies.Contracts.Dtos;
using Mediator;

namespace FSH.Modules.Proxies.Contracts.v1.ApiClients;

public sealed record ListApiClientsQuery : IQuery<IReadOnlyList<ApiClientDto>>;
```

- [ ] **Step 2: Write the failing handler tests**

```csharp
// src/Tests/Proxies.Tests/Handlers/ApiClientHandlerTests.cs
using FSH.Modules.Proxies.Contracts.v1.ApiClients;
using FSH.Modules.Proxies.Domain;
using FSH.Modules.Proxies.Features.v1.ApiClients.CreateApiClient;
using FSH.Modules.Proxies.Features.v1.ApiClients.DeleteApiClient;
using FSH.Modules.Proxies.Services;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using Xunit;

namespace Proxies.Tests.Handlers;

public sealed class ApiClientHandlerTests
{
    private static Proxies.Tests.TestProxiesDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<FSH.Modules.Proxies.Data.ProxiesDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    [Fact]
    public async Task Create_Should_StoreOnlyTheHash_And_ReturnThePlaintextKeyOnce()
    {
        await using var db = CreateDb();
        var sut = new CreateApiClientCommandHandler(db, new ApiKeyHasher());

        var result = await sut.Handle(new CreateApiClientCommand("TAG"), CancellationToken.None);

        var stored = await db.ApiClients.SingleAsync(x => x.Id == result.Id);
        stored.ApiKeyHash.ShouldNotBe(result.PlaintextKey);
        stored.ApiKeyHash.ShouldBe(new ApiKeyHasher().Hash(result.PlaintextKey));
    }

    [Fact]
    public async Task Delete_Should_RemoveApiClient()
    {
        await using var db = CreateDb();
        var client = ApiClient.Create("legacy-scraper", "hash");
        db.ApiClients.Add(client);
        await db.SaveChangesAsync();
        var sut = new DeleteApiClientCommandHandler(db);

        await sut.Handle(new DeleteApiClientCommand(client.Id), CancellationToken.None);

        (await db.ApiClients.AnyAsync(x => x.Id == client.Id)).ShouldBeFalse();
    }
}
```

- [ ] **Step 3: Run to verify failure, then implement**

Run: `dotnet test src/Tests/Proxies.Tests --filter "FullyQualifiedName~ApiClientHandlerTests"` — expect compile failure.

```csharp
// src/Modules/Proxies/Modules.Proxies/Features/v1/ApiClients/CreateApiClient/CreateApiClientCommandValidator.cs
using FluentValidation;
using FSH.Modules.Proxies.Contracts.v1.ApiClients;

namespace FSH.Modules.Proxies.Features.v1.ApiClients.CreateApiClient;

public sealed class CreateApiClientCommandValidator : AbstractValidator<CreateApiClientCommand>
{
    public CreateApiClientCommandValidator() => RuleFor(x => x.Name).NotEmpty().MaximumLength(128);
}
```

```csharp
// src/Modules/Proxies/Modules.Proxies/Features/v1/ApiClients/CreateApiClient/CreateApiClientCommandHandler.cs
using FSH.Modules.Proxies.Contracts.Dtos;
using FSH.Modules.Proxies.Contracts.v1.ApiClients;
using FSH.Modules.Proxies.Data;
using FSH.Modules.Proxies.Domain;
using FSH.Modules.Proxies.Services;
using Mediator;

namespace FSH.Modules.Proxies.Features.v1.ApiClients.CreateApiClient;

public sealed class CreateApiClientCommandHandler(ProxiesDbContext dbContext, IApiKeyHasher hasher)
    : ICommandHandler<CreateApiClientCommand, CreateApiClientResult>
{
    public async ValueTask<CreateApiClientResult> Handle(CreateApiClientCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var (plaintextKey, hash) = hasher.GenerateKey();
        var client = ApiClient.Create(command.Name, hash);
        dbContext.ApiClients.Add(client);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return new CreateApiClientResult(client.Id, plaintextKey);
    }
}
```

```csharp
// src/Modules/Proxies/Modules.Proxies/Features/v1/ApiClients/DeleteApiClient/DeleteApiClientCommandValidator.cs
using FluentValidation;
using FSH.Modules.Proxies.Contracts.v1.ApiClients;

namespace FSH.Modules.Proxies.Features.v1.ApiClients.DeleteApiClient;

public sealed class DeleteApiClientCommandValidator : AbstractValidator<DeleteApiClientCommand>
{
    public DeleteApiClientCommandValidator() => RuleFor(x => x.Id).NotEmpty();
}
```

```csharp
// src/Modules/Proxies/Modules.Proxies/Features/v1/ApiClients/DeleteApiClient/DeleteApiClientCommandHandler.cs
using FSH.Framework.Core.Exceptions;
using FSH.Modules.Proxies.Contracts.v1.ApiClients;
using FSH.Modules.Proxies.Data;
using Mediator;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.Proxies.Features.v1.ApiClients.DeleteApiClient;

public sealed class DeleteApiClientCommandHandler(ProxiesDbContext dbContext) : ICommandHandler<DeleteApiClientCommand>
{
    public async ValueTask<Unit> Handle(DeleteApiClientCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var client = await dbContext.ApiClients.FirstOrDefaultAsync(x => x.Id == command.Id, cancellationToken).ConfigureAwait(false)
            ?? throw new NotFoundException($"API client {command.Id} not found.");
        dbContext.ApiClients.Remove(client);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return Unit.Value;
    }
}
```

```csharp
// src/Modules/Proxies/Modules.Proxies/Features/v1/ApiClients/ListApiClients/ListApiClientsQueryHandler.cs
using FSH.Modules.Proxies.Contracts.Dtos;
using FSH.Modules.Proxies.Contracts.v1.ApiClients;
using FSH.Modules.Proxies.Data;
using Mediator;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.Proxies.Features.v1.ApiClients.ListApiClients;

public sealed class ListApiClientsQueryHandler(ProxiesDbContext dbContext) : IQueryHandler<ListApiClientsQuery, IReadOnlyList<ApiClientDto>>
{
    public async ValueTask<IReadOnlyList<ApiClientDto>> Handle(ListApiClientsQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        return await dbContext.ApiClients.AsNoTracking().OrderBy(x => x.Name)
            .Select(x => new ApiClientDto(x.Id, x.Name, x.IsEnabled, x.CreatedAtUtc, x.LastUsedAtUtc))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
    }
}
```

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test src/Tests/Proxies.Tests --filter "FullyQualifiedName~ApiClientHandlerTests"`
Expected: PASS, 2 tests.

- [ ] **Step 5: Endpoints**

```csharp
// src/Modules/Proxies/Modules.Proxies/Features/v1/ApiClients/CreateApiClient/CreateApiClientEndpoint.cs
using FSH.Framework.Shared.Identity.Authorization;
using FSH.Modules.Proxies.Contracts.Authorization;
using FSH.Modules.Proxies.Contracts.v1.ApiClients;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FSH.Modules.Proxies.Features.v1.ApiClients.CreateApiClient;

public static class CreateApiClientEndpoint
{
    internal static RouteHandlerBuilder MapCreateApiClientEndpoint(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapPost("/api-clients", async (CreateApiClientCommand command, IMediator mediator, CancellationToken ct) => Results.Ok(await mediator.Send(command, ct)))
            .WithName("CreateApiClient").WithSummary("Issue a new API key for a scraper/service consumer — the key is shown only in this response")
            .RequirePermission(ProxiesPermissions.ApiClients.Create);
}
```

```csharp
// src/Modules/Proxies/Modules.Proxies/Features/v1/ApiClients/DeleteApiClient/DeleteApiClientEndpoint.cs
using FSH.Framework.Shared.Identity.Authorization;
using FSH.Modules.Proxies.Contracts.Authorization;
using FSH.Modules.Proxies.Contracts.v1.ApiClients;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FSH.Modules.Proxies.Features.v1.ApiClients.DeleteApiClient;

public static class DeleteApiClientEndpoint
{
    internal static RouteHandlerBuilder MapDeleteApiClientEndpoint(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapDelete("/api-clients/{id:guid}", async (Guid id, IMediator mediator, CancellationToken ct) => { await mediator.Send(new DeleteApiClientCommand(id), ct); return Results.NoContent(); })
            .WithName("DeleteApiClient").WithSummary("Revoke an API key")
            .RequirePermission(ProxiesPermissions.ApiClients.Delete);
}
```

```csharp
// src/Modules/Proxies/Modules.Proxies/Features/v1/ApiClients/ListApiClients/ListApiClientsEndpoint.cs
using FSH.Framework.Shared.Identity.Authorization;
using FSH.Modules.Proxies.Contracts.Authorization;
using FSH.Modules.Proxies.Contracts.v1.ApiClients;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FSH.Modules.Proxies.Features.v1.ApiClients.ListApiClients;

public static class ListApiClientsEndpoint
{
    internal static RouteHandlerBuilder MapListApiClientsEndpoint(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapGet("/api-clients", (IMediator mediator, CancellationToken ct) => mediator.Send(new ListApiClientsQuery(), ct))
            .WithName("ListApiClients").WithSummary("List API clients (keys never included)")
            .RequirePermission(ProxiesPermissions.ApiClients.View);
}
```

- [ ] **Step 6: Wire, build, test, commit**

```csharp
// inside ProxiesModule.MapEndpoints
group.MapCreateApiClientEndpoint();
group.MapDeleteApiClientEndpoint();
group.MapListApiClientsEndpoint();
```

Run: `dotnet build src/FS.Proxy.slnx && dotnet test src/Tests/Proxies.Tests`
Expected: PASS.

```bash
git add src/Modules/Proxies src/Tests/Proxies.Tests
git commit -m "feat(proxies): add ApiClient issuance endpoints"
```

### Task 22: Dual authentication (API Key + JWT)

No prior precedent exists in this repo (per the cross-cutting research §5) — this task builds genuinely new infrastructure rather than mirroring an existing pattern. The design: an `AuthenticationHandler` for a new `"ApiKey"` scheme, added *alongside* the JWT scheme Identity already registers (never replacing it), and a dedicated authorization policy that accepts either scheme — used only by the two consumer endpoints in Tasks 23–24, not by any admin endpoint (those keep using the app-wide default `RequirePermission` policy, JWT-only, unchanged).

The DB lookup logic is deliberately extracted into a plain, fully unit-testable `IApiKeyAuthenticator` — the `AuthenticationHandler` itself is thin ASP.NET Core glue with no branching logic worth unit-testing in isolation; it's exercised end-to-end once Tasks 23–24 add real endpoints behind it.

**Files:**
- Create: `src/Modules/Proxies/Modules.Proxies/Authentication/IApiKeyAuthenticator.cs`
- Create: `src/Modules/Proxies/Modules.Proxies/Authentication/ApiKeyAuthenticator.cs`
- Create: `src/Modules/Proxies/Modules.Proxies/Authentication/ApiKeyAuthenticationOptions.cs`
- Create: `src/Modules/Proxies/Modules.Proxies/Authentication/ApiKeyAuthenticationHandler.cs`
- Create: `src/Modules/Proxies/Modules.Proxies/Authentication/ApiKeyAuthenticationDefaults.cs`
- Modify: `src/Modules/Proxies/Modules.Proxies/ProxiesModule.cs`
- Test: `src/Tests/Proxies.Tests/Authentication/ApiKeyAuthenticatorTests.cs`

**Interfaces:**
- Produces: `IApiKeyAuthenticator.AuthenticateAsync(string? apiKey, CancellationToken) : Task<ApiClient?>` (returns `null` for missing/unknown/disabled keys — never throws for a bad key, since "not authenticated" is an expected outcome, not an error); the `"ApiKey"` authentication scheme name (`ApiKeyAuthenticationDefaults.SchemeName`) and the `"ProxiesConsumerAccess"` authorization policy name — both consumed by Tasks 23–24's endpoint registration.

- [ ] **Step 1: Write the failing authenticator test**

```csharp
// src/Tests/Proxies.Tests/Authentication/ApiKeyAuthenticatorTests.cs
using FSH.Modules.Proxies.Authentication;
using FSH.Modules.Proxies.Domain;
using FSH.Modules.Proxies.Services;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using Xunit;

namespace Proxies.Tests.Authentication;

public sealed class ApiKeyAuthenticatorTests
{
    private static Proxies.Tests.TestProxiesDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<FSH.Modules.Proxies.Data.ProxiesDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    [Fact]
    public async Task AuthenticateAsync_Should_ReturnClient_When_KeyIsValidAndEnabled()
    {
        await using var db = CreateDb();
        var hasher = new ApiKeyHasher();
        var (plaintextKey, hash) = hasher.GenerateKey();
        var client = ApiClient.Create("TAG", hash);
        db.ApiClients.Add(client);
        await db.SaveChangesAsync();
        var sut = new ApiKeyAuthenticator(db, hasher);

        var result = await sut.AuthenticateAsync(plaintextKey, CancellationToken.None);

        result.ShouldNotBeNull();
        result!.Id.ShouldBe(client.Id);
        (await db.ApiClients.SingleAsync(x => x.Id == client.Id)).LastUsedAtUtc.ShouldNotBeNull();
    }

    [Fact]
    public async Task AuthenticateAsync_Should_ReturnNull_When_KeyIsUnknown()
    {
        await using var db = CreateDb();
        var sut = new ApiKeyAuthenticator(db, new ApiKeyHasher());

        (await sut.AuthenticateAsync("not-a-real-key", CancellationToken.None)).ShouldBeNull();
    }

    [Fact]
    public async Task AuthenticateAsync_Should_ReturnNull_When_ClientIsDisabled()
    {
        await using var db = CreateDb();
        var hasher = new ApiKeyHasher();
        var (plaintextKey, hash) = hasher.GenerateKey();
        var client = ApiClient.Create("TAG", hash);
        client.SetEnabled(false);
        db.ApiClients.Add(client);
        await db.SaveChangesAsync();
        var sut = new ApiKeyAuthenticator(db, hasher);

        (await sut.AuthenticateAsync(plaintextKey, CancellationToken.None)).ShouldBeNull();
    }

    [Fact]
    public async Task AuthenticateAsync_Should_ReturnNull_When_KeyIsNullOrWhitespace()
    {
        await using var db = CreateDb();
        var sut = new ApiKeyAuthenticator(db, new ApiKeyHasher());

        (await sut.AuthenticateAsync(null, CancellationToken.None)).ShouldBeNull();
        (await sut.AuthenticateAsync("  ", CancellationToken.None)).ShouldBeNull();
    }
}
```

- [ ] **Step 2: Run to verify failure, then implement the authenticator**

Run: `dotnet test src/Tests/Proxies.Tests --filter "FullyQualifiedName~ApiKeyAuthenticatorTests"` — expect compile failure.

```csharp
// src/Modules/Proxies/Modules.Proxies/Authentication/IApiKeyAuthenticator.cs
using FSH.Modules.Proxies.Domain;

namespace FSH.Modules.Proxies.Authentication;

public interface IApiKeyAuthenticator
{
    Task<ApiClient?> AuthenticateAsync(string? apiKey, CancellationToken cancellationToken);
}
```

```csharp
// src/Modules/Proxies/Modules.Proxies/Authentication/ApiKeyAuthenticator.cs
using FSH.Modules.Proxies.Data;
using FSH.Modules.Proxies.Services;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.Proxies.Authentication;

public sealed class ApiKeyAuthenticator(ProxiesDbContext dbContext, IApiKeyHasher hasher) : IApiKeyAuthenticator
{
    public async Task<Domain.ApiClient?> AuthenticateAsync(string? apiKey, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(apiKey)) return null;

        var hash = hasher.Hash(apiKey);
        var client = await dbContext.ApiClients.FirstOrDefaultAsync(c => c.ApiKeyHash == hash, cancellationToken).ConfigureAwait(false);
        if (client is null || !client.IsEnabled) return null;

        client.RecordUsage();
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return client;
    }
}
```

- [ ] **Step 3: Run to verify pass**

Run: `dotnet test src/Tests/Proxies.Tests --filter "FullyQualifiedName~ApiKeyAuthenticatorTests"`
Expected: PASS, 4 tests.

- [ ] **Step 4: The `AuthenticationHandler` glue (not unit-tested — see the task's opening note)**

```csharp
// src/Modules/Proxies/Modules.Proxies/Authentication/ApiKeyAuthenticationDefaults.cs
namespace FSH.Modules.Proxies.Authentication;

public static class ApiKeyAuthenticationDefaults
{
    public const string SchemeName = "ApiKey";
    public const string HeaderName = "X-Api-Key";
    public const string ConsumerPolicyName = "ProxiesConsumerAccess";
}
```

```csharp
// src/Modules/Proxies/Modules.Proxies/Authentication/ApiKeyAuthenticationOptions.cs
using Microsoft.AspNetCore.Authentication;

namespace FSH.Modules.Proxies.Authentication;

public sealed class ApiKeyAuthenticationOptions : AuthenticationSchemeOptions;
```

```csharp
// src/Modules/Proxies/Modules.Proxies/Authentication/ApiKeyAuthenticationHandler.cs
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FSH.Modules.Proxies.Authentication;

public sealed class ApiKeyAuthenticationHandler(
    IOptionsMonitor<ApiKeyAuthenticationOptions> options, ILoggerFactory logger, UrlEncoder encoder,
    IApiKeyAuthenticator authenticator)
    : AuthenticationHandler<ApiKeyAuthenticationOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(ApiKeyAuthenticationDefaults.HeaderName, out var headerValues))
        {
            return AuthenticateResult.NoResult();
        }

        var client = await authenticator.AuthenticateAsync(headerValues.ToString(), Context.RequestAborted).ConfigureAwait(false);
        if (client is null)
        {
            return AuthenticateResult.Fail("Invalid or disabled API key.");
        }

        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, client.Id.ToString()), new Claim("proxies:client_name", client.Name)],
            Scheme.Name);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name);
        return AuthenticateResult.Success(ticket);
    }
}
```

- [ ] **Step 5: Register the scheme and the consumer authorization policy**

```csharp
// add inside ProxiesModule.ConfigureServices
builder.Services.AddScoped<FSH.Modules.Proxies.Authentication.IApiKeyAuthenticator, FSH.Modules.Proxies.Authentication.ApiKeyAuthenticator>();

builder.Services.AddAuthentication()
    .AddScheme<FSH.Modules.Proxies.Authentication.ApiKeyAuthenticationOptions, FSH.Modules.Proxies.Authentication.ApiKeyAuthenticationHandler>(
        FSH.Modules.Proxies.Authentication.ApiKeyAuthenticationDefaults.SchemeName, _ => { });

builder.Services.AddAuthorizationBuilder()
    .AddPolicy(FSH.Modules.Proxies.Authentication.ApiKeyAuthenticationDefaults.ConsumerPolicyName, policy =>
        policy
            .AddAuthenticationSchemes(FSH.Modules.Proxies.Authentication.ApiKeyAuthenticationDefaults.SchemeName, Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerDefaults.AuthenticationScheme)
            .RequireAuthenticatedUser());
```

This calls the parameterless `AddAuthentication()` overload deliberately — it registers the new scheme into the same authentication builder Identity already configured (JWT stays the default scheme for every endpoint that doesn't explicitly ask for something else); it does not touch `DefaultAuthenticateScheme`/`DefaultChallengeScheme`. If `Microsoft.AspNetCore.Authentication.JwtBearer` doesn't resolve, it's part of the ASP.NET Core shared framework already available transitively through the `Web` building block reference (Task 1) — no new package reference should be needed, but if the compiler disagrees, add `<FrameworkReference Include="Microsoft.AspNetCore.App" />` to `Modules.Proxies.csproj` rather than a `PackageReference`.

- [ ] **Step 6: Build and run the full Proxies test suite**

Run: `dotnet build src/FS.Proxy.slnx && dotnet test src/Tests/Proxies.Tests`
Expected: PASS.

- [ ] **Step 7: Commit**

```bash
git add src/Modules/Proxies src/Tests/Proxies.Tests
git commit -m "feat(proxies): add dual authentication (API Key + JWT) for consumer endpoints"
```

### Task 23: Consumer request endpoint (`POST /api/proxies/request`)

**Files:**
- Create: `src/Modules/Proxies/Modules.Proxies.Contracts/ProxySelectionStrategy.cs`
- Create: `src/Modules/Proxies/Modules.Proxies.Contracts/Dtos/ProxyConnectionDto.cs`
- Create: `src/Modules/Proxies/Modules.Proxies.Contracts/v1/Proxies/RequestProxiesQuery.cs`
- Create: `src/Modules/Proxies/Modules.Proxies/Features/v1/Proxies/RequestProxies/RequestProxiesQueryValidator.cs`
- Create: `src/Modules/Proxies/Modules.Proxies/Features/v1/Proxies/RequestProxies/RequestProxiesQueryHandler.cs`
- Create: `src/Modules/Proxies/Modules.Proxies/Features/v1/Proxies/RequestProxies/RequestProxiesEndpoint.cs`
- Modify: `src/Modules/Proxies/Modules.Proxies/ProxiesModule.cs`
- Test: `src/Tests/Proxies.Tests/Handlers/RequestProxiesHandlerTests.cs`

**Interfaces:**
- Consumes: `IProxyPasswordResolver` (Task 20), `HybridCache` (framework — already available via the `Caching` building block reference from Task 1).
- Produces: `ProxyConnectionDto(Guid Id, string Host, int Port, ProxyProtocol Protocol, string? Username, string? Password)` (the one place in the whole API surface that returns a decrypted password — this is what a scraper needs to actually connect); `RequestProxiesQuery(IReadOnlyList<string> Tags, int Count, ProxySelectionStrategy Strategy, string? SessionId) : IQuery<IReadOnlyList<ProxyConnectionDto>>`.

**Tag matching here is AND, not OR** — unlike Task 12's admin list filter (which is a browse convenience: "show me proxies with any of these tags"), a scraper's request like `tags=[pais:cl, funcionalidad:licitaciones]` means "a proxy that has both", matching the example you gave during brainstorming. Don't copy Task 12's `ListProxiesQueryHandler` tag-matching logic here — it's deliberately different.

- [ ] **Step 1: Define the strategy enum, DTO, and query**

```csharp
// src/Modules/Proxies/Modules.Proxies.Contracts/ProxySelectionStrategy.cs
namespace FSH.Modules.Proxies.Contracts;

public enum ProxySelectionStrategy { RoundRobin, Random, Sequential, Sticky }
```

```csharp
// src/Modules/Proxies/Modules.Proxies.Contracts/Dtos/ProxyConnectionDto.cs
namespace FSH.Modules.Proxies.Contracts.Dtos;

public sealed record ProxyConnectionDto(Guid Id, string Host, int Port, ProxyProtocol Protocol, string? Username, string? Password);
```

```csharp
// src/Modules/Proxies/Modules.Proxies.Contracts/v1/Proxies/RequestProxiesQuery.cs
using FSH.Modules.Proxies.Contracts.Dtos;
using Mediator;

namespace FSH.Modules.Proxies.Contracts.v1.Proxies;

public sealed record RequestProxiesQuery(
    IReadOnlyList<string> Tags, int Count, ProxySelectionStrategy Strategy, string? SessionId)
    : IQuery<IReadOnlyList<ProxyConnectionDto>>;
```

- [ ] **Step 2: Write the failing handler tests**

```csharp
// src/Tests/Proxies.Tests/Handlers/RequestProxiesHandlerTests.cs
using FSH.Modules.Proxies.Contracts;
using FSH.Modules.Proxies.Contracts.v1.Proxies;
using FSH.Modules.Proxies.Domain;
using FSH.Modules.Proxies.Features.v1.Proxies.RequestProxies;
using FSH.Modules.Proxies.Options;
using FSH.Modules.Proxies.Services;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using Xunit;

namespace Proxies.Tests.Handlers;

public sealed class RequestProxiesHandlerTests
{
    private sealed class PassthroughPasswordResolver : IProxyPasswordResolver
    {
        public string? Decrypt(Proxy proxy) => proxy.ProtectedPassword is null ? null : $"decrypted:{proxy.ProtectedPassword}";
    }

    private static Proxies.Tests.TestProxiesDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<FSH.Modules.Proxies.Data.ProxiesDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static HybridCache CreateCache() =>
        new ServiceCollection().AddHybridCache().Services.BuildServiceProvider().GetRequiredService<HybridCache>();

    private static RequestProxiesQueryHandler CreateSut(Proxies.Tests.TestProxiesDbContext db) =>
        new(db, CreateCache(), new PassthroughPasswordResolver(), Options.Create(new ProxiesOptions()));

    private static async Task<(Proxy Matches, Proxy PartialMatch, Proxy Other)> SeedAsync(Proxies.Tests.TestProxiesDbContext db)
    {
        var tagCl = Tag.Create("pais:cl");
        var tagLicitaciones = Tag.Create("funcionalidad:licitaciones");
        var matches = Proxy.Create(ManualProviderAccount.Id, "1.1.1.1", 80, ProxyProtocol.Http, "u", "p", null);
        matches.SetStatus(ProxyStatus.Active);
        matches.AssignTag(tagCl.Id);
        matches.AssignTag(tagLicitaciones.Id);
        var partialMatch = Proxy.Create(ManualProviderAccount.Id, "2.2.2.2", 80, ProxyProtocol.Http, null, null, null);
        partialMatch.SetStatus(ProxyStatus.Active);
        partialMatch.AssignTag(tagCl.Id);
        var other = Proxy.Create(ManualProviderAccount.Id, "3.3.3.3", 80, ProxyProtocol.Http, null, null, null);
        other.SetStatus(ProxyStatus.Active);
        db.Tags.AddRange(tagCl, tagLicitaciones);
        db.Proxies.AddRange(matches, partialMatch, other);
        await db.SaveChangesAsync();
        return (matches, partialMatch, other);
    }

    [Fact]
    public async Task Handle_Should_RequireAllTags_NotAny()
    {
        await using var db = CreateDb();
        var (matches, _, _) = await SeedAsync(db);
        var sut = CreateSut(db);

        var result = await sut.Handle(new RequestProxiesQuery(["pais:cl", "funcionalidad:licitaciones"], 5, ProxySelectionStrategy.Sequential, null), CancellationToken.None);

        result.Select(x => x.Id).ShouldBe([matches.Id]);
    }

    [Fact]
    public async Task Handle_Should_ExcludeInactiveProxies()
    {
        await using var db = CreateDb();
        var tag = Tag.Create("pais:pe");
        var disabled = Proxy.Create(ManualProviderAccount.Id, "9.9.9.9", 80, ProxyProtocol.Http, null, null, null);
        disabled.AssignTag(tag.Id);
        disabled.SetStatus(ProxyStatus.Disabled);
        db.Tags.Add(tag);
        db.Proxies.Add(disabled);
        await db.SaveChangesAsync();
        var sut = CreateSut(db);

        await Should.ThrowAsync<FSH.Framework.Core.Exceptions.NotFoundException>(() =>
            sut.Handle(new RequestProxiesQuery(["pais:pe"], 1, ProxySelectionStrategy.Sequential, null), CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task Handle_Should_ReturnDecryptedPassword()
    {
        await using var db = CreateDb();
        var (matches, _, _) = await SeedAsync(db);
        var sut = CreateSut(db);

        var result = await sut.Handle(new RequestProxiesQuery(["pais:cl", "funcionalidad:licitaciones"], 1, ProxySelectionStrategy.Sequential, null), CancellationToken.None);

        result.Single().Password.ShouldBe("decrypted:p");
    }

    [Fact]
    public async Task Handle_Should_ReturnSameProxy_ForRepeatedStickySessionCalls()
    {
        await using var db = CreateDb();
        var tag = Tag.Create("pais:cl");
        var a = Proxy.Create(ManualProviderAccount.Id, "1.1.1.1", 80, ProxyProtocol.Http, null, null, null);
        a.SetStatus(ProxyStatus.Active); a.AssignTag(tag.Id);
        var b = Proxy.Create(ManualProviderAccount.Id, "2.2.2.2", 80, ProxyProtocol.Http, null, null, null);
        b.SetStatus(ProxyStatus.Active); b.AssignTag(tag.Id);
        db.Tags.Add(tag);
        db.Proxies.AddRange(a, b);
        await db.SaveChangesAsync();
        var cache = CreateCache();
        var sut = new RequestProxiesQueryHandler(db, cache, new PassthroughPasswordResolver(), Options.Create(new ProxiesOptions()));
        var query = new RequestProxiesQuery(["pais:cl"], 1, ProxySelectionStrategy.Sticky, "session-42");

        var first = await sut.Handle(query, CancellationToken.None);
        var second = await sut.Handle(query, CancellationToken.None);

        first.Single().Id.ShouldBe(second.Single().Id);
    }
}
```

- [ ] **Step 3: Run to verify failure, then implement**

Run: `dotnet test src/Tests/Proxies.Tests --filter "FullyQualifiedName~RequestProxiesHandlerTests"` — expect compile failure.

```csharp
// src/Modules/Proxies/Modules.Proxies/Features/v1/Proxies/RequestProxies/RequestProxiesQueryValidator.cs
using FluentValidation;
using FSH.Modules.Proxies.Contracts;
using FSH.Modules.Proxies.Contracts.v1.Proxies;

namespace FSH.Modules.Proxies.Features.v1.Proxies.RequestProxies;

public sealed class RequestProxiesQueryValidator : AbstractValidator<RequestProxiesQuery>
{
    public RequestProxiesQueryValidator()
    {
        RuleForEach(x => x.Tags).NotEmpty();
        RuleFor(x => x.Count).InclusiveBetween(1, 50);
        RuleFor(x => x.Strategy).IsInEnum();
        RuleFor(x => x.SessionId).NotEmpty().When(x => x.Strategy == ProxySelectionStrategy.Sticky)
            .WithMessage("SessionId is required when Strategy is Sticky.");
    }
}
```

```csharp
// src/Modules/Proxies/Modules.Proxies/Features/v1/Proxies/RequestProxies/RequestProxiesQueryHandler.cs
using FSH.Framework.Core.Exceptions;
using FSH.Modules.Proxies.Contracts;
using FSH.Modules.Proxies.Contracts.Dtos;
using FSH.Modules.Proxies.Contracts.v1.Proxies;
using FSH.Modules.Proxies.Data;
using FSH.Modules.Proxies.Domain;
using FSH.Modules.Proxies.Options;
using FSH.Modules.Proxies.Services;
using Mediator;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Options;

namespace FSH.Modules.Proxies.Features.v1.Proxies.RequestProxies;

public sealed class RequestProxiesQueryHandler(
    ProxiesDbContext dbContext, HybridCache cache, IProxyPasswordResolver passwordResolver, IOptions<ProxiesOptions> options)
    : IQueryHandler<RequestProxiesQuery, IReadOnlyList<ProxyConnectionDto>>
{
    public async ValueTask<IReadOnlyList<ProxyConnectionDto>> Handle(RequestProxiesQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var candidates = await ResolveCandidatesAsync(query.Tags, cancellationToken).ConfigureAwait(false);
        if (candidates.Count == 0)
        {
            throw new NotFoundException("No active proxies match the requested tags.");
        }

        List<Proxy> selected = query.Strategy switch
        {
            ProxySelectionStrategy.Sticky => [await ResolveStickyAsync(query, candidates, cancellationToken).ConfigureAwait(false)],
            ProxySelectionStrategy.Random => [.. candidates.OrderBy(_ => Random.Shared.Next()).Take(query.Count)],
            ProxySelectionStrategy.Sequential => [.. candidates.Take(query.Count)],
            _ => await ResolveRoundRobinAsync(query.Tags, candidates, query.Count, cancellationToken).ConfigureAwait(false),
        };

        return [.. selected.Select(p => new ProxyConnectionDto(p.Id, p.Host, p.Port, (ProxyProtocol)p.Protocol, p.Username, passwordResolver.Decrypt(p)))];
    }

    private async Task<List<Proxy>> ResolveCandidatesAsync(IReadOnlyList<string> tags, CancellationToken cancellationToken)
    {
        var query = dbContext.Proxies.Where(p => p.Status == ProxyStatus.Active);

        foreach (var tagName in tags.Select(Tag.Normalize).Distinct())
        {
            var proxyIdsWithThisTag = dbContext.Tags.Where(t => t.Name == tagName)
                .Join(dbContext.Set<ProxyTagAssignment>(), t => t.Id, a => a.TagId, (t, a) => a.ProxyId);
            query = query.Where(p => proxyIdsWithThisTag.Contains(p.Id));
        }

        return await query.OrderBy(p => p.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<Proxy> ResolveStickyAsync(RequestProxiesQuery query, List<Proxy> candidates, CancellationToken cancellationToken)
    {
        string cacheKey = $"proxies:session:{query.SessionId}:{string.Join(',', query.Tags.Select(Tag.Normalize).OrderBy(t => t))}";

        var cachedId = await cache.GetOrCreateAsync(cacheKey, candidates,
            (state, _) => ValueTask.FromResult(state[0].Id),
            new HybridCacheEntryOptions { Expiration = TimeSpan.FromMinutes(30), LocalCacheExpiration = TimeSpan.FromMinutes(2) },
            cancellationToken: cancellationToken).ConfigureAwait(false);

        var stillActive = candidates.FirstOrDefault(p => p.Id == cachedId);
        if (stillActive is not null) return stillActive;

        // The cached proxy is no longer in the active candidate set (disabled/retired since last pinned) — evict and re-pick.
        await cache.RemoveAsync(cacheKey, cancellationToken).ConfigureAwait(false);
        var freshId = await cache.GetOrCreateAsync(cacheKey, candidates,
            (state, _) => ValueTask.FromResult(state[0].Id),
            new HybridCacheEntryOptions { Expiration = TimeSpan.FromMinutes(30), LocalCacheExpiration = TimeSpan.FromMinutes(2) },
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return candidates.First(p => p.Id == freshId);
    }

    private async Task<List<Proxy>> ResolveRoundRobinAsync(IReadOnlyList<string> tags, List<Proxy> candidates, int count, CancellationToken cancellationToken)
    {
        string cursorKey = $"proxies:round-robin-cursor:{string.Join(',', tags.Select(Tag.Normalize).OrderBy(t => t))}";
        int cursor = await cache.GetOrCreateAsync(cursorKey, 0, (seed, _) => ValueTask.FromResult(seed),
            new HybridCacheEntryOptions { Expiration = TimeSpan.FromDays(1) }, cancellationToken: cancellationToken).ConfigureAwait(false);

        var result = new List<Proxy>(Math.Min(count, candidates.Count));
        for (int i = 0; i < count && i < candidates.Count; i++)
        {
            result.Add(candidates[(cursor + i) % candidates.Count]);
        }

        await cache.RemoveAsync(cursorKey, cancellationToken).ConfigureAwait(false);
        await cache.GetOrCreateAsync(cursorKey, cursor + result.Count, (seed, _) => ValueTask.FromResult(seed),
            new HybridCacheEntryOptions { Expiration = TimeSpan.FromDays(1) }, cancellationToken: cancellationToken).ConfigureAwait(false);

        return result;
    }
}
```

The round-robin cursor's remove-then-recreate is a simple, not-perfectly-atomic increment — acceptable at the scale this spec targets (hundreds of proxies, tens of requests/second); if usage ever grows enough for the race to matter, replace it with a `IConnectionMultiplexer.GetDatabase().StringIncrementAsync` call instead of layering it on `HybridCache`, which isn't built for atomic counters.

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test src/Tests/Proxies.Tests --filter "FullyQualifiedName~RequestProxiesHandlerTests"`
Expected: PASS, 4 tests.

- [ ] **Step 5: The endpoint — behind the dual-auth consumer policy from Task 22, not `RequirePermission`**

```csharp
// src/Modules/Proxies/Modules.Proxies/Features/v1/Proxies/RequestProxies/RequestProxiesEndpoint.cs
using FSH.Modules.Proxies.Authentication;
using FSH.Modules.Proxies.Contracts;
using FSH.Modules.Proxies.Contracts.v1.Proxies;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FSH.Modules.Proxies.Features.v1.Proxies.RequestProxies;

public static class RequestProxiesEndpoint
{
    internal static RouteHandlerBuilder MapRequestProxiesEndpoint(this IEndpointRouteBuilder endpoints)
    {
        return endpoints.MapPost("/request",
                (RequestProxiesBody body, IMediator mediator, CancellationToken ct) =>
                    mediator.Send(new RequestProxiesQuery(body.Tags, body.Count <= 0 ? 1 : body.Count, body.Strategy, body.SessionId), ct))
            .WithName("RequestProxies")
            .WithSummary("Request one or more proxies matching all given tags")
            .RequireAuthorization(ApiKeyAuthenticationDefaults.ConsumerPolicyName);
    }

    internal sealed record RequestProxiesBody(IReadOnlyList<string> Tags, int Count, ProxySelectionStrategy Strategy, string? SessionId);
}
```

Note this endpoint is mapped on the module's shared `group` (`api/v{version}/proxies`), which carries `.RequireAuthorization()` (no scheme specified — the app-wide default policy) from `ProxiesModule.MapEndpoints`. `.RequireAuthorization(ConsumerPolicyName)` on the individual route **replaces** that default policy for this endpoint (ASP.NET Core's per-endpoint `RequireAuthorization` overrides rather than adds), so this correctly ends up gated by the dual-scheme consumer policy and not by the default JWT-only admin policy — verify this is still true if the way `group.RequireAuthorization()` composes changes elsewhere in the codebase between when this plan was written and when this task executes.

**On rate limiting** (spec's Security section): this endpoint and Task 24's feedback endpoint don't need a bespoke rate-limit policy added here — `security.md`'s global Tenant/User/IP fixed-window limiter (Task cross-cutting research §5's `RateLimitingOptions`) already wraps every endpoint in the app, this one included. Worth noting explicitly: an API-Key-authenticated request carries no ASP.NET Identity `User`, so the User-scoped tier of that global limiter has nothing to partition on for these calls and only the IP-scoped tier actually applies to them — acceptable at the spec's stated scale (tens of requests/second), but if a specific misbehaving scraper ever needs throttling independent of its IP, that's a `security.md`-level enhancement (a fourth partition keyed on the authenticated `ApiClient` id), not something to build speculatively now.

- [ ] **Step 6: Wire, build, test, commit**

```csharp
// inside ProxiesModule.MapEndpoints
group.MapRequestProxiesEndpoint();
```

Run: `dotnet build src/FS.Proxy.slnx && dotnet test src/Tests/Proxies.Tests`
Expected: PASS.

```bash
git add src/Modules/Proxies src/Tests/Proxies.Tests
git commit -m "feat(proxies): add the consumer-facing proxy request endpoint"
```

### Task 24: Consumer feedback endpoint (`POST /api/proxies/{id}/feedback`)

**Files:**
- Create: `src/Modules/Proxies/Modules.Proxies.Contracts/UsageEventOutcome.cs`
- Create: `src/Modules/Proxies/Modules.Proxies.Contracts/v1/Proxies/ReportProxyFeedbackCommand.cs`
- Create: `src/Modules/Proxies/Modules.Proxies/Features/v1/Proxies/ReportProxyFeedback/ReportProxyFeedbackCommandValidator.cs`
- Create: `src/Modules/Proxies/Modules.Proxies/Features/v1/Proxies/ReportProxyFeedback/ReportProxyFeedbackCommandHandler.cs`
- Create: `src/Modules/Proxies/Modules.Proxies/Features/v1/Proxies/ReportProxyFeedback/ReportProxyFeedbackEndpoint.cs`
- Modify: `src/Modules/Proxies/Modules.Proxies/ProxiesModule.cs`
- Test: `src/Tests/Proxies.Tests/Handlers/ReportProxyFeedbackHandlerTests.cs`

**Interfaces:**
- Consumes: `IPolicyEvaluationService` (Task 18).
- Produces: `ReportProxyFeedbackCommand(Guid ProxyId, UsageEventOutcome Outcome, string? Detail, string? ReporterIdentifier) : ICommand`. `ReporterIdentifier` carries the raw `ClaimTypes.NameIdentifier` claim value from either auth scheme — the handler resolves it to a real `ApiClient` when it can (legacy scrapers authenticated via API Key, where distinct-reporter counting matters most for the policy engine's false-positive protection) and otherwise records no reporter identity (TAG and any JWT-authenticated caller collapse into a shared "trusted caller" bucket — acceptable, since TAG is a single system, not many independent scrapers).

`UsageEventOutcome` needs to move to Contracts (the command references it):

```csharp
// src/Modules/Proxies/Modules.Proxies.Contracts/UsageEventOutcome.cs (new file)
namespace FSH.Modules.Proxies.Contracts;

public enum UsageEventOutcome { Success, Failure, Banned, Timeout }
```

Delete the `enum UsageEventOutcome` declaration from `src/Modules/Proxies/Modules.Proxies/Domain/ProxyUsageEvent.cs`, add `using FSH.Modules.Proxies.Contracts;` there, and update `ProxyUsageEvent.Create`'s `outcome` parameter type accordingly — every call site across Tasks 18–20 that constructs a `ProxyUsageEvent` with a `Domain.UsageEventOutcome` literal (e.g. `UsageEventOutcome.Banned` in the Task 18 tests) keeps compiling unchanged since the enum's members and underlying values are identical, only its namespace moved.

- [ ] **Step 1: Define the command**

```csharp
// src/Modules/Proxies/Modules.Proxies.Contracts/v1/Proxies/ReportProxyFeedbackCommand.cs
using Mediator;

namespace FSH.Modules.Proxies.Contracts.v1.Proxies;

public sealed record ReportProxyFeedbackCommand(Guid ProxyId, UsageEventOutcome Outcome, string? Detail, string? ReporterIdentifier) : ICommand;
```

- [ ] **Step 2: Write the failing handler tests**

```csharp
// src/Tests/Proxies.Tests/Handlers/ReportProxyFeedbackHandlerTests.cs
using FSH.Modules.Proxies.Contracts;
using FSH.Modules.Proxies.Contracts.v1.Proxies;
using FSH.Modules.Proxies.Domain;
using FSH.Modules.Proxies.Features.v1.Proxies.ReportProxyFeedback;
using FSH.Modules.Proxies.Services;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Proxies.Tests.Handlers;

public sealed class ReportProxyFeedbackHandlerTests
{
    private static Proxies.Tests.TestProxiesDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<FSH.Modules.Proxies.Data.ProxiesDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    [Fact]
    public async Task Handle_Should_RecordEvent_And_ResolveKnownApiClient()
    {
        await using var db = CreateDb();
        var proxy = Proxy.Create(ManualProviderAccount.Id, "1.1.1.1", 80, ProxyProtocol.Http, null, null, null);
        var reporter = ApiClient.Create("legacy-scraper", "hash");
        db.Proxies.Add(proxy);
        db.ApiClients.Add(reporter);
        await db.SaveChangesAsync();
        var policyService = Substitute.For<IPolicyEvaluationService>();
        var sut = new ReportProxyFeedbackCommandHandler(db, policyService);

        await sut.Handle(new ReportProxyFeedbackCommand(proxy.Id, UsageEventOutcome.Banned, "banned by mercadopublico.cl", reporter.Id.ToString()), CancellationToken.None);

        var stored = await db.ProxyUsageEvents.SingleAsync(e => e.ProxyId == proxy.Id);
        stored.Outcome.ShouldBe(UsageEventOutcome.Banned); // UsageEventOutcome now lives in Contracts (moved earlier in this task) — the `using FSH.Modules.Proxies.Contracts;` above already resolves it
        stored.ReportedByApiClientId.ShouldBe(reporter.Id);
        await policyService.Received(1).EvaluateAsync(proxy.Id, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_Should_RecordEvent_WithNoReporterId_When_IdentifierIsNotAKnownApiClient()
    {
        await using var db = CreateDb();
        var proxy = Proxy.Create(ManualProviderAccount.Id, "1.1.1.1", 80, ProxyProtocol.Http, null, null, null);
        db.Proxies.Add(proxy);
        await db.SaveChangesAsync();
        var sut = new ReportProxyFeedbackCommandHandler(db, Substitute.For<IPolicyEvaluationService>());

        await sut.Handle(new ReportProxyFeedbackCommand(proxy.Id, UsageEventOutcome.Success, null, "some-tag-jwt-user-id"), CancellationToken.None);

        var stored = await db.ProxyUsageEvents.SingleAsync(e => e.ProxyId == proxy.Id);
        stored.ReportedByApiClientId.ShouldBeNull();
    }

    [Fact]
    public async Task Handle_Should_Throw_When_ProxyNotFound()
    {
        await using var db = CreateDb();
        var sut = new ReportProxyFeedbackCommandHandler(db, Substitute.For<IPolicyEvaluationService>());

        await Should.ThrowAsync<FSH.Framework.Core.Exceptions.NotFoundException>(() =>
            sut.Handle(new ReportProxyFeedbackCommand(Guid.NewGuid(), UsageEventOutcome.Success, null, null), CancellationToken.None).AsTask());
    }
}
```

- [ ] **Step 3: Run to verify failure, then implement**

Run: `dotnet test src/Tests/Proxies.Tests --filter "FullyQualifiedName~ReportProxyFeedbackHandlerTests"` — expect compile failure.

```csharp
// src/Modules/Proxies/Modules.Proxies/Features/v1/Proxies/ReportProxyFeedback/ReportProxyFeedbackCommandValidator.cs
using FluentValidation;
using FSH.Modules.Proxies.Contracts.v1.Proxies;

namespace FSH.Modules.Proxies.Features.v1.Proxies.ReportProxyFeedback;

public sealed class ReportProxyFeedbackCommandValidator : AbstractValidator<ReportProxyFeedbackCommand>
{
    public ReportProxyFeedbackCommandValidator()
    {
        RuleFor(x => x.ProxyId).NotEmpty();
        RuleFor(x => x.Outcome).IsInEnum();
        RuleFor(x => x.Detail).MaximumLength(2048);
    }
}
```

```csharp
// src/Modules/Proxies/Modules.Proxies/Features/v1/Proxies/ReportProxyFeedback/ReportProxyFeedbackCommandHandler.cs
using FSH.Framework.Core.Exceptions;
using FSH.Modules.Proxies.Contracts.v1.Proxies;
using FSH.Modules.Proxies.Data;
using FSH.Modules.Proxies.Domain;
using FSH.Modules.Proxies.Services;
using Mediator;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.Proxies.Features.v1.Proxies.ReportProxyFeedback;

public sealed class ReportProxyFeedbackCommandHandler(ProxiesDbContext dbContext, IPolicyEvaluationService policyEvaluationService)
    : ICommandHandler<ReportProxyFeedbackCommand>
{
    public async ValueTask<Unit> Handle(ReportProxyFeedbackCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        bool proxyExists = await dbContext.Proxies.AnyAsync(p => p.Id == command.ProxyId, cancellationToken).ConfigureAwait(false);
        if (!proxyExists)
        {
            throw new NotFoundException($"Proxy {command.ProxyId} not found.");
        }

        Guid? reporterId = null;
        if (Guid.TryParse(command.ReporterIdentifier, out var parsed) &&
            await dbContext.ApiClients.AnyAsync(c => c.Id == parsed, cancellationToken).ConfigureAwait(false))
        {
            reporterId = parsed;
        }

        var usageEvent = ProxyUsageEvent.Create(
            command.ProxyId, UsageEventSource.ConsumerFeedback, (UsageEventOutcome)command.Outcome,
            healthCheckTargetId: null, reporterId, command.Detail);
        dbContext.ProxyUsageEvents.Add(usageEvent);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        await policyEvaluationService.EvaluateAsync(command.ProxyId, cancellationToken).ConfigureAwait(false);
        return Unit.Value;
    }
}
```

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test src/Tests/Proxies.Tests --filter "FullyQualifiedName~ReportProxyFeedbackHandlerTests"`
Expected: PASS, 3 tests.

- [ ] **Step 5: The endpoint**

```csharp
// src/Modules/Proxies/Modules.Proxies/Features/v1/Proxies/ReportProxyFeedback/ReportProxyFeedbackEndpoint.cs
using System.Security.Claims;
using FSH.Modules.Proxies.Authentication;
using FSH.Modules.Proxies.Contracts;
using FSH.Modules.Proxies.Contracts.v1.Proxies;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FSH.Modules.Proxies.Features.v1.Proxies.ReportProxyFeedback;

public static class ReportProxyFeedbackEndpoint
{
    internal static RouteHandlerBuilder MapReportProxyFeedbackEndpoint(this IEndpointRouteBuilder endpoints)
    {
        return endpoints.MapPost("/{id:guid}/feedback",
                async (Guid id, ReportProxyFeedbackBody body, ClaimsPrincipal user, IMediator mediator, CancellationToken ct) =>
                {
                    string? reporterIdentifier = user.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                    await mediator.Send(new ReportProxyFeedbackCommand(id, body.Outcome, body.Detail, reporterIdentifier), ct);
                    return Results.NoContent();
                })
            .WithName("ReportProxyFeedback")
            .WithSummary("Report the outcome of using a proxy")
            .RequireAuthorization(ApiKeyAuthenticationDefaults.ConsumerPolicyName);
    }

    internal sealed record ReportProxyFeedbackBody(UsageEventOutcome Outcome, string? Detail);
}
```

- [ ] **Step 6: Wire, build, test, commit**

```csharp
// inside ProxiesModule.MapEndpoints
group.MapReportProxyFeedbackEndpoint();
```

Run: `dotnet build src/FS.Proxy.slnx && dotnet test src/Tests/Proxies.Tests`
Expected: PASS. This is also a natural point to run the **entire** backend test suite once, since Milestones A–H are now all in place:

Run: `dotnet test src/FS.Proxy.slnx`
Expected: PASS across every test project (`Proxies.Tests`, `Notifications.Tests`, `Architecture.Tests`, and all pre-existing module test projects — nothing here should have broken any of them).

```bash
git add src/Modules/Proxies src/Tests/Proxies.Tests
git commit -m "feat(proxies): add the consumer-facing proxy feedback endpoint"
```

---

## Milestone I — Admin UI (`clients/admin`)

### Task 25: API client layer, permissions mirror, and route registration

**Files:**
- Create: `clients/admin/src/api/proxies.ts`
- Create: `clients/admin/src/api/provider-accounts.ts`
- Create: `clients/admin/src/api/manual-proxies.ts`
- Create: `clients/admin/src/api/proxy-tags.ts`
- Modify: `clients/admin/src/lib/permissions.ts`
- Modify: `clients/admin/src/routes.tsx`

**Interfaces:**
- Produces: `listProxies`, `setProxiesStatus`, `listProviderAccounts`, `createProviderAccount`, `updateProviderAccount`, `deleteProviderAccount`, `syncProviderAccountNow`, `createManualProxy`, `updateManualProxy`, `deleteManualProxy`, `listProxyTags` — thin `apiFetch` wrappers, following `src/api/tenants.ts`'s exact pattern (Task 26–28 build their pages on these). `ProxiesPermissions` mirrors the server-side `ProxiesPermissions` (backend Task 4) as a frozen object, consumed by every `RouteGuard` and permission-gated button in Tasks 26–28.

- [ ] **Step 1: `src/api/proxies.ts`**

```ts
// clients/admin/src/api/proxies.ts
import { apiFetch } from "@/lib/api-client";
import type { PagedResponse } from "@/lib/api-types";

const BASE = "/api/v1/proxies";

export type ProxyProtocol = "Http" | "Https" | "Socks5";
export type ProxyStatus = "Active" | "Disabled" | "Banned" | "Testing" | "Retired";

export type ProxyDto = {
  id: string;
  host: string;
  port: number;
  protocol: ProxyProtocol;
  status: ProxyStatus;
  providerAccountId: string;
  providerAccountName: string;
  providerType: string;
  tags: string[];
  createdAtUtc: string;
  lastRenewedAtUtc: string | null;
};

export type ListProxiesParams = {
  tags?: string[];
  status?: ProxyStatus;
  providerAccountId?: string;
  pageNumber?: number;
  pageSize?: number;
};

export async function listProxies(params: ListProxiesParams = {}): Promise<PagedResponse<ProxyDto>> {
  const query = new URLSearchParams();
  query.set("pageNumber", String(params.pageNumber ?? 1));
  query.set("pageSize", String(params.pageSize ?? 20));
  if (params.status) query.set("status", params.status);
  if (params.providerAccountId) query.set("providerAccountId", params.providerAccountId);
  for (const tag of params.tags ?? []) query.append("tags", tag);
  return apiFetch<PagedResponse<ProxyDto>>(`${BASE}/?${query.toString()}`);
}

export type SetProxiesStatusInput = { proxyIds?: string[]; tagId?: string };

export async function enableProxies(input: SetProxiesStatusInput): Promise<void> {
  await apiFetch<void>(`${BASE}/enable`, {
    method: "POST",
    body: JSON.stringify({ proxyIds: input.proxyIds ?? null, tagId: input.tagId ?? null }),
  });
}

export async function disableProxies(input: SetProxiesStatusInput): Promise<void> {
  await apiFetch<void>(`${BASE}/disable`, {
    method: "POST",
    body: JSON.stringify({ proxyIds: input.proxyIds ?? null, tagId: input.tagId ?? null }),
  });
}
```

- [ ] **Step 2: `src/api/provider-accounts.ts`**

```ts
// clients/admin/src/api/provider-accounts.ts
import { apiFetch } from "@/lib/api-client";
import type { PagedResponse } from "@/lib/api-types";

const BASE = "/api/v1/proxies/provider-accounts";

export type ProviderType = "WebShare" | "Oxylabs" | "BrightData" | "Manual";

export type ProviderAccountDto = {
  id: string;
  name: string;
  providerType: ProviderType;
  isEnabled: boolean;
  lastSyncedAtUtc: string | null;
  lastSyncStatus: string | null;
  consecutiveSyncFailures: number;
};

export async function listProviderAccounts(pageNumber = 1, pageSize = 20): Promise<PagedResponse<ProviderAccountDto>> {
  const query = new URLSearchParams({ pageNumber: String(pageNumber), pageSize: String(pageSize) });
  return apiFetch<PagedResponse<ProviderAccountDto>>(`${BASE}?${query.toString()}`);
}

export type CreateProviderAccountInput = { name: string; providerType: ProviderType; plaintextCredentials: string };

export async function createProviderAccount(input: CreateProviderAccountInput): Promise<string> {
  return apiFetch<string>(`${BASE}`, { method: "POST", body: JSON.stringify(input) });
}

export type UpdateProviderAccountInput = { id: string; name: string; plaintextCredentials?: string; isEnabled: boolean };

export async function updateProviderAccount(input: UpdateProviderAccountInput): Promise<void> {
  await apiFetch<void>(`${BASE}/${input.id}`, {
    method: "PUT",
    body: JSON.stringify({ name: input.name, plaintextCredentials: input.plaintextCredentials ?? null, isEnabled: input.isEnabled }),
  });
}

export async function deleteProviderAccount(id: string): Promise<void> {
  await apiFetch<void>(`${BASE}/${id}`, { method: "DELETE" });
}

export async function syncProviderAccountNow(id: string): Promise<number> {
  return apiFetch<number>(`${BASE}/${id}/sync`, { method: "POST" });
}
```

- [ ] **Step 3: `src/api/manual-proxies.ts`**

```ts
// clients/admin/src/api/manual-proxies.ts
import { apiFetch } from "@/lib/api-client";
import type { ProxyProtocol } from "./proxies";

const BASE = "/api/v1/proxies/manual-proxies";

export type CreateManualProxyInput = {
  host: string;
  port: number;
  protocol: ProxyProtocol;
  username?: string;
  plaintextPassword?: string;
  tagNames: string[];
};

export async function createManualProxy(input: CreateManualProxyInput): Promise<string> {
  return apiFetch<string>(`${BASE}`, { method: "POST", body: JSON.stringify(input) });
}

export type UpdateManualProxyInput = CreateManualProxyInput & { id: string };

export async function updateManualProxy(input: UpdateManualProxyInput): Promise<void> {
  const { id, ...body } = input;
  await apiFetch<void>(`${BASE}/${id}`, { method: "PUT", body: JSON.stringify(body) });
}

export async function deleteManualProxy(id: string): Promise<void> {
  await apiFetch<void>(`${BASE}/${id}`, { method: "DELETE" });
}
```

- [ ] **Step 4: `src/api/proxy-tags.ts`**

```ts
// clients/admin/src/api/proxy-tags.ts
import { apiFetch } from "@/lib/api-client";

const BASE = "/api/v1/proxies/tags";

export type TagDto = {
  id: string;
  name: string;
  policyProfileId: string | null;
  policyProfileName: string | null;
  healthCheckTargetId: string | null;
  healthCheckTargetName: string | null;
};

export async function listProxyTags(): Promise<TagDto[]> {
  return apiFetch<TagDto[]>(`${BASE}`);
}
```

- [ ] **Step 5: Mirror the permissions**

```ts
// add to clients/admin/src/lib/permissions.ts, alongside MultitenancyPermissions etc.
export const ProxiesPermissions = Object.freeze({
  ProviderAccounts: {
    View: "Permissions.Proxies.ProviderAccounts.View",
    Create: "Permissions.Proxies.ProviderAccounts.Create",
    Update: "Permissions.Proxies.ProviderAccounts.Update",
    Delete: "Permissions.Proxies.ProviderAccounts.Delete",
  },
  ManualProxies: {
    View: "Permissions.Proxies.ManualProxies.View",
    Create: "Permissions.Proxies.ManualProxies.Create",
    Update: "Permissions.Proxies.ManualProxies.Update",
    Delete: "Permissions.Proxies.ManualProxies.Delete",
  },
} as const);
```

If this file also maintains a `PERMISSION_CATALOG` (or similarly named list feeding the role editor UI), append the same eight permission strings there too, following whatever entry shape the existing `MultitenancyPermissions`/`CatalogPermissions` rows use.

- [ ] **Step 6: Register the routes**

```tsx
// add to clients/admin/src/routes.tsx, alongside the other lazyNamed page imports
const ProxiesListPage = lazyNamed(() => import("@/pages/proxies/list"), "ProxiesListPage");
const ProviderAccountsListPage = lazyNamed(() => import("@/pages/proxies/provider-accounts"), "ProviderAccountsListPage");
const ManualProxiesListPage = lazyNamed(() => import("@/pages/proxies/manual-proxies"), "ManualProxiesListPage");

// add alongside the other route entries
{
  path: "proxies",
  element: (
    <RouteGuard perms={[ProxiesPermissions.ProviderAccounts.View]}>
      <ProxiesListPage />
    </RouteGuard>
  ),
},
{
  path: "proxies/provider-accounts",
  element: (
    <RouteGuard perms={[ProxiesPermissions.ProviderAccounts.View]}>
      <ProviderAccountsListPage />
    </RouteGuard>
  ),
},
{
  path: "proxies/manual",
  element: (
    <RouteGuard perms={[ProxiesPermissions.ManualProxies.View]}>
      <ManualProxiesListPage />
    </RouteGuard>
  ),
},
```

(add `import { ProxiesPermissions } from "@/lib/permissions";` — or extend whatever existing combined import line already pulls in `MultitenancyPermissions` etc.)

- [ ] **Step 7: Confirm the app still builds**

Run: `cd clients/admin && npm run build`
Expected: succeeds (these are new files/routes only — nothing existing changed shape).

- [ ] **Step 8: Commit**

```bash
git add clients/admin/src/api/proxies.ts clients/admin/src/api/provider-accounts.ts clients/admin/src/api/manual-proxies.ts clients/admin/src/api/proxy-tags.ts clients/admin/src/lib/permissions.ts clients/admin/src/routes.tsx
git commit -m "feat(admin): add Proxies API client layer, permissions, and routes"
```

### Task 26: Proxies list page (filter, table, enable/disable — single and bulk)

Modeled on `src/pages/users/list.tsx` (debounced filters, `Segmented`/`Select`, hand-rolled responsive table, `keepPreviousData`) rather than the simpler `tenants/list.tsx`, since this page has more filter dimensions.

**Files:**
- Create: `clients/admin/src/pages/proxies/list.tsx`

**Interfaces:**
- Consumes: `listProxies`, `enableProxies`, `disableProxies` (Task 25), `listProviderAccounts` (Task 25, for the provider filter dropdown), `EntityPageHeader`/`ErrorBand`/`LoadingRow`/`Pagination`/`Select` (`@/components/list`), `Segmented` (mirrors the local component `src/pages/users/list.tsx` already defines — either import it if it's been extracted to a shared location, or copy its ~15-line implementation locally the same way `users/list.tsx` does, since no shared `Segmented` currently lives in `@/components/list` per the frontend research).

- [ ] **Step 1: Implement the page**

```tsx
// clients/admin/src/pages/proxies/list.tsx
import { useEffect, useState } from "react";
import { keepPreviousData, useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { EntityPageHeader, ErrorBand, LoadingRow, Pagination, Select } from "@/components/list";
import { ApiRequestError } from "@/lib/api-client";
import { ProxiesPermissions } from "@/lib/permissions";
import { useAuth } from "@/auth/use-auth";
import {
  disableProxies, enableProxies, listProxies,
  type ProxyDto, type ProxyStatus,
} from "@/api/proxies";
import { listProviderAccounts } from "@/api/provider-accounts";

const PAGE_SIZE = 20;
const STATUS_OPTIONS: { value: ProxyStatus | ""; label: string }[] = [
  { value: "", label: "Any status" },
  { value: "Active", label: "Active" },
  { value: "Disabled", label: "Disabled" },
  { value: "Testing", label: "Testing" },
  { value: "Banned", label: "Banned" },
  { value: "Retired", label: "Retired" },
];

function describeError(err: unknown): string {
  return err instanceof ApiRequestError ? (err.problem?.detail ?? err.problem?.title ?? err.message) : (err as Error).message;
}

export function ProxiesListPage() {
  const { user } = useAuth();
  const queryClient = useQueryClient();
  const [pageNumber, setPageNumber] = useState(1);
  const [tagsInput, setTagsInput] = useState("");
  const [tags, setTags] = useState<string[]>([]);
  const [status, setStatus] = useState<ProxyStatus | "">("");
  const [providerAccountId, setProviderAccountId] = useState("");
  const [selected, setSelected] = useState<Set<string>>(new Set());

  useEffect(() => {
    const t = setTimeout(() => {
      setTags(tagsInput.split(",").map((s) => s.trim()).filter(Boolean));
      setPageNumber(1);
    }, 300);
    return () => clearTimeout(t);
  }, [tagsInput]);

  useEffect(() => setPageNumber(1), [status, providerAccountId]);

  const canUpdate = user?.permissions.includes(ProxiesPermissions.ManualProxies.Update) ?? false;

  const providerAccountsQuery = useQuery({
    queryKey: ["proxies", "provider-accounts", "all"],
    queryFn: () => listProviderAccounts(1, 100),
  });

  const proxiesQuery = useQuery({
    queryKey: ["proxies", "list", { pageNumber, tags, status, providerAccountId }],
    queryFn: () =>
      listProxies({
        pageNumber, pageSize: PAGE_SIZE,
        tags: tags.length > 0 ? tags : undefined,
        status: status || undefined,
        providerAccountId: providerAccountId || undefined,
      }),
    placeholderData: keepPreviousData,
  });

  const enableMutation = useMutation({
    mutationFn: (input: { proxyIds?: string[]; tagId?: string }) => enableProxies(input),
    onSuccess: () => {
      toast.success("Proxies enabled");
      setSelected(new Set());
      void queryClient.invalidateQueries({ queryKey: ["proxies", "list"] });
    },
    onError: (err) => toast.error("Enable failed", { description: describeError(err) }),
  });

  const disableMutation = useMutation({
    mutationFn: (input: { proxyIds?: string[]; tagId?: string }) => disableProxies(input),
    onSuccess: () => {
      toast.success("Proxies disabled");
      setSelected(new Set());
      void queryClient.invalidateQueries({ queryKey: ["proxies", "list"] });
    },
    onError: (err) => toast.error("Disable failed", { description: describeError(err) }),
  });

  const items = proxiesQuery.data?.items ?? [];

  function toggleSelected(id: string) {
    setSelected((prev) => {
      const next = new Set(prev);
      if (next.has(id)) next.delete(id); else next.add(id);
      return next;
    });
  }

  return (
    <div className="space-y-4 sm:space-y-6">
      <EntityPageHeader
        icon="Globe"
        title="Proxies"
        count={proxiesQuery.data?.totalCount}
        description="Inventory of proxies across all providers and manual entries."
      />

      <div className="flex flex-wrap items-end gap-3">
        <div className="flex flex-col gap-1">
          <label htmlFor="px-tags" className="text-xs font-mono uppercase tracking-wide text-[var(--color-muted-foreground)]">Tags</label>
          <input
            id="px-tags"
            type="search"
            placeholder="pais:cl, funcionalidad:licitaciones"
            value={tagsInput}
            onChange={(e) => setTagsInput(e.target.value)}
            className="h-9 w-72 rounded-md border border-[var(--color-border)] bg-[var(--color-card)] px-3 text-sm"
          />
        </div>
        <Select
          label="Status"
          value={status}
          onChange={(v) => setStatus(v as ProxyStatus | "")}
          options={STATUS_OPTIONS}
          minWidth="9rem"
        />
        <Select
          label="Provider account"
          value={providerAccountId}
          onChange={setProviderAccountId}
          options={[{ value: "", label: "Any account" }, ...(providerAccountsQuery.data?.items ?? []).map((a) => ({ value: a.id, label: a.name }))]}
          minWidth="12rem"
        />
        {canUpdate && selected.size > 0 && (
          <div className="ml-auto flex gap-2">
            <button
              type="button"
              onClick={() => enableMutation.mutate({ proxyIds: [...selected] })}
              className="h-9 rounded-md bg-[var(--color-accent-signal)] px-3 text-sm font-medium"
            >
              Enable selected ({selected.size})
            </button>
            <button
              type="button"
              onClick={() => disableMutation.mutate({ proxyIds: [...selected] })}
              className="h-9 rounded-md border border-[var(--color-border)] px-3 text-sm font-medium"
            >
              Disable selected
            </button>
          </div>
        )}
      </div>

      {proxiesQuery.isLoading && <LoadingRow label="Loading proxies" />}
      {proxiesQuery.isError && <ErrorBand message={describeError(proxiesQuery.error)} />}

      {!proxiesQuery.isLoading && !proxiesQuery.isError && items.length === 0 && (
        <div className="py-16 text-center">
          <p className="font-display text-2xl">No proxies match these filters.</p>
          <p className="mt-2 text-sm text-[var(--color-muted-foreground)]">Try clearing the tag or status filter.</p>
        </div>
      )}

      {!proxiesQuery.isLoading && items.length > 0 && (
        <div className="overflow-hidden rounded-xl border border-[var(--color-border)] bg-[var(--color-card)] shadow-xs">
          <div className="grid grid-cols-[24px_1.4fr_100px_1.2fr_1.4fr_140px] items-center gap-3 border-b border-[var(--color-border)] bg-[var(--color-muted)]/40 px-4 py-2.5 text-xs font-mono uppercase tracking-wide text-[var(--color-muted-foreground)]">
            <span />
            <span>Host</span>
            <span>Status</span>
            <span>Provider</span>
            <span>Tags</span>
            <span>Actions</span>
          </div>
          <ol className="divide-y divide-[var(--color-border)]">
            {items.map((proxy: ProxyDto) => (
              <li key={proxy.id} className="grid grid-cols-[24px_1.4fr_100px_1.2fr_1.4fr_140px] items-center gap-3 px-4 py-2.5 text-sm">
                <input
                  type="checkbox"
                  checked={selected.has(proxy.id)}
                  onChange={() => toggleSelected(proxy.id)}
                  aria-label={`Select ${proxy.host}`}
                />
                <span className="font-mono">{proxy.host}:{proxy.port}</span>
                <span className="text-xs font-mono uppercase">{proxy.status}</span>
                <span>{proxy.providerAccountName} <span className="text-[var(--color-muted-foreground)]">({proxy.providerType})</span></span>
                <span className="truncate text-[var(--color-muted-foreground)]">{proxy.tags.join(", ") || "—"}</span>
                {canUpdate ? (
                  proxy.status === "Active" ? (
                    <button type="button" onClick={() => disableMutation.mutate({ proxyIds: [proxy.id] })} className="text-sm underline">Disable</button>
                  ) : (
                    <button type="button" onClick={() => enableMutation.mutate({ proxyIds: [proxy.id] })} className="text-sm underline">Enable</button>
                  )
                ) : null}
              </li>
            ))}
          </ol>
        </div>
      )}

      {proxiesQuery.data && (
        <Pagination
          pageNumber={proxiesQuery.data.pageNumber}
          totalPages={proxiesQuery.data.totalPages}
          onChange={setPageNumber}
        />
      )}
    </div>
  );
}
```

Verify the exact prop names for `EntityPageHeader`, `Select`, and `Pagination` against `@/components/list`'s real source before typing this — they're used here matching the shapes implied by the `tenants`/`users` list pages in the frontend research, but confirm exact prop names (e.g. whether `EntityPageHeader`'s icon prop takes a Lucide component reference rather than a string) before compiling.

- [ ] **Step 2: Run the dev server and manually verify the page renders**

Run: `cd clients/admin && npm run dev`, sign in, navigate to `/proxies`.
Expected: the page loads, filters work, and (once Task 27/28 create some real proxies) enable/disable toggles the row's status.

- [ ] **Step 3: Commit**

```bash
git add clients/admin/src/pages/proxies/list.tsx
git commit -m "feat(admin): add the Proxies list page with filters and enable/disable"
```

### Task 27: Provider account ABM page

Modeled on `create-tenant-dialog.tsx` for the create/edit form (zod + react-hook-form + `mutate(values)`).

**Files:**
- Create: `clients/admin/src/pages/proxies/provider-accounts.tsx`
- Create: `clients/admin/src/components/proxies/provider-account-dialog.tsx`

**Interfaces:**
- Consumes: `listProviderAccounts`, `createProviderAccount`, `updateProviderAccount`, `deleteProviderAccount`, `syncProviderAccountNow` (Task 25).

- [ ] **Step 1: The create/edit dialog**

```tsx
// clients/admin/src/components/proxies/provider-account-dialog.tsx
import { useEffect } from "react";
import { Controller, useForm } from "react-hook-form";
import { zodResolver } from "@hookform/resolvers/zod";
import { z } from "zod";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { Loader2 } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Dialog } from "@/components/ui/dialog";
import { Input } from "@/components/ui/input";
import { Field, Select } from "@/components/list";
import { ApiRequestError } from "@/lib/api-client";
import {
  createProviderAccount, updateProviderAccount,
  type ProviderAccountDto, type ProviderType,
} from "@/api/provider-accounts";

const PROVIDER_OPTIONS: { value: ProviderType; label: string }[] = [
  { value: "WebShare", label: "WebShare" },
  { value: "Oxylabs", label: "Oxylabs" },
  { value: "BrightData", label: "BrightData" },
];

const schema = z.object({
  name: z.string().trim().min(2, "At least 2 characters.").max(128),
  providerType: z.enum(["WebShare", "Oxylabs", "BrightData"]),
  plaintextCredentials: z.string().trim().min(1, "Required."),
  isEnabled: z.boolean(),
});

type FormValues = z.infer<typeof schema>;

export function ProviderAccountDialog({ open, onClose, account }: { open: boolean; onClose: () => void; account?: ProviderAccountDto }) {
  const queryClient = useQueryClient();
  const isEdit = Boolean(account);
  const { register, handleSubmit, control, reset, formState: { errors, isSubmitting } } = useForm<FormValues>({
    resolver: zodResolver(schema),
    defaultValues: { name: "", providerType: "WebShare", plaintextCredentials: "", isEnabled: true },
  });

  useEffect(() => {
    if (account) {
      reset({ name: account.name, providerType: account.providerType as "WebShare" | "Oxylabs" | "BrightData", plaintextCredentials: "", isEnabled: account.isEnabled });
    } else {
      reset({ name: "", providerType: "WebShare", plaintextCredentials: "", isEnabled: true });
    }
  }, [account, reset]);

  const mutation = useMutation({
    mutationFn: (values: FormValues) =>
      isEdit
        ? updateProviderAccount({ id: account!.id, name: values.name, plaintextCredentials: values.plaintextCredentials || undefined, isEnabled: values.isEnabled })
        : createProviderAccount({ name: values.name, providerType: values.providerType, plaintextCredentials: values.plaintextCredentials }),
    onSuccess: () => {
      toast.success(isEdit ? "Provider account updated" : "Provider account created");
      void queryClient.invalidateQueries({ queryKey: ["proxies", "provider-accounts"] });
      onClose();
    },
    onError: (err) => {
      const detail = err instanceof ApiRequestError ? (err.problem?.detail ?? err.problem?.title ?? err.message) : (err as Error).message;
      toast.error(isEdit ? "Update failed" : "Create failed", { description: detail });
    },
  });

  const submitting = isSubmitting || mutation.isPending;

  return (
    <Dialog open={open} onOpenChange={(o) => !o && onClose()} title={isEdit ? "Edit provider account" : "New provider account"}>
      <form onSubmit={handleSubmit((values) => mutation.mutate(values))} className="space-y-4">
        <Field id="pa-name" label="Name" required error={errors.name?.message}>
          <Input id="pa-name" autoComplete="off" placeholder="WebShare — main" {...register("name")} />
        </Field>
        {!isEdit && (
          <Field id="pa-provider" label="Provider" required error={errors.providerType?.message}>
            <Controller control={control} name="providerType" render={({ field }) => (
              <Select id="pa-provider" value={field.value} onValueChange={field.onChange} options={PROVIDER_OPTIONS} />
            )} />
          </Field>
        )}
        <Field id="pa-credentials" label={isEdit ? "Replace credentials (leave blank to keep current)" : "Credentials (JSON)"} error={errors.plaintextCredentials?.message}>
          <Input id="pa-credentials" autoComplete="off" placeholder='{"ApiKey":"..."}' {...register("plaintextCredentials")} />
        </Field>
        <div className="flex justify-end gap-2 pt-2">
          <Button type="button" variant="outline" onClick={onClose} disabled={submitting}>Cancel</Button>
          <Button type="submit" disabled={submitting} className="min-w-[8.5rem]">
            {submitting ? (<><Loader2 className="size-4 animate-spin" aria-hidden /><span>Saving…</span></>) : "Save"}
          </Button>
        </div>
      </form>
    </Dialog>
  );
}
```

- [ ] **Step 2: The list page**

```tsx
// clients/admin/src/pages/proxies/provider-accounts.tsx
import { useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { EntityPageHeader, ErrorBand, LoadingRow } from "@/components/list";
import { Button } from "@/components/ui/button";
import { ApiRequestError } from "@/lib/api-client";
import { ProxiesPermissions } from "@/lib/permissions";
import { useAuth } from "@/auth/use-auth";
import { deleteProviderAccount, listProviderAccounts, syncProviderAccountNow, type ProviderAccountDto } from "@/api/provider-accounts";
import { ProviderAccountDialog } from "@/components/proxies/provider-account-dialog";

function describeError(err: unknown): string {
  return err instanceof ApiRequestError ? (err.problem?.detail ?? err.problem?.title ?? err.message) : (err as Error).message;
}

export function ProviderAccountsListPage() {
  const { user } = useAuth();
  const queryClient = useQueryClient();
  const [dialogState, setDialogState] = useState<{ open: boolean; account?: ProviderAccountDto }>({ open: false });

  const canCreate = user?.permissions.includes(ProxiesPermissions.ProviderAccounts.Create) ?? false;
  const canUpdate = user?.permissions.includes(ProxiesPermissions.ProviderAccounts.Update) ?? false;
  const canDelete = user?.permissions.includes(ProxiesPermissions.ProviderAccounts.Delete) ?? false;

  const accountsQuery = useQuery({ queryKey: ["proxies", "provider-accounts"], queryFn: () => listProviderAccounts(1, 100) });

  const syncMutation = useMutation({
    mutationFn: (id: string) => syncProviderAccountNow(id),
    onSuccess: (touched) => {
      toast.success(`Sync complete — ${touched} proxies touched`);
      void queryClient.invalidateQueries({ queryKey: ["proxies", "provider-accounts"] });
      void queryClient.invalidateQueries({ queryKey: ["proxies", "list"] });
    },
    onError: (err) => toast.error("Sync failed", { description: describeError(err) }),
  });

  const deleteMutation = useMutation({
    mutationFn: (id: string) => deleteProviderAccount(id),
    onSuccess: () => {
      toast.success("Provider account deleted");
      void queryClient.invalidateQueries({ queryKey: ["proxies", "provider-accounts"] });
    },
    onError: (err) => toast.error("Delete failed", { description: describeError(err) }),
  });

  const items = accountsQuery.data?.items ?? [];

  return (
    <div className="space-y-4 sm:space-y-6">
      <EntityPageHeader
        icon="Server"
        title="Provider accounts"
        count={accountsQuery.data?.totalCount}
        description="WebShare, Oxylabs, and BrightData accounts synced into the proxy pool."
        actions={canCreate ? (
          <Button type="button" onClick={() => setDialogState({ open: true })}>New provider account</Button>
        ) : undefined}
      />

      {accountsQuery.isLoading && <LoadingRow label="Loading provider accounts" />}
      {accountsQuery.isError && <ErrorBand message={describeError(accountsQuery.error)} />}

      {!accountsQuery.isLoading && items.length === 0 && (
        <div className="py-16 text-center">
          <p className="font-display text-2xl">No provider accounts yet.</p>
          <p className="mt-2 text-sm text-[var(--color-muted-foreground)]">Add the first account to start syncing proxies.</p>
        </div>
      )}

      {items.length > 0 && (
        <div className="overflow-hidden rounded-xl border border-[var(--color-border)] bg-[var(--color-card)] shadow-xs">
          <ol className="divide-y divide-[var(--color-border)]">
            {items.map((account: ProviderAccountDto) => (
              <li key={account.id} className="flex flex-wrap items-center gap-3 px-4 py-3 text-sm">
                <span className="min-w-[10rem] font-medium">{account.name}</span>
                <span className="text-xs font-mono uppercase text-[var(--color-muted-foreground)]">{account.providerType}</span>
                <span className="text-xs">{account.isEnabled ? "Enabled" : "Disabled"}</span>
                <span className="text-xs text-[var(--color-muted-foreground)]">
                  {account.lastSyncedAtUtc ? `Last sync: ${new Date(account.lastSyncedAtUtc).toLocaleString()}` : "Never synced"}
                  {account.consecutiveSyncFailures > 0 ? ` · ${account.consecutiveSyncFailures} consecutive failures` : ""}
                </span>
                <div className="ml-auto flex gap-3">
                  {canUpdate && (
                    <button type="button" className="text-sm underline" onClick={() => syncMutation.mutate(account.id)} disabled={syncMutation.isPending}>
                      Sync now
                    </button>
                  )}
                  {canUpdate && (
                    <button type="button" className="text-sm underline" onClick={() => setDialogState({ open: true, account })}>Edit</button>
                  )}
                  {canDelete && (
                    <button type="button" className="text-sm text-[var(--color-destructive)] underline" onClick={() => deleteMutation.mutate(account.id)}>Delete</button>
                  )}
                </div>
              </li>
            ))}
          </ol>
        </div>
      )}

      <ProviderAccountDialog open={dialogState.open} account={dialogState.account} onClose={() => setDialogState({ open: false })} />
    </div>
  );
}
```

Verify `EntityPageHeader`'s exact prop names (`actions` may be named differently — check the real component) and `Dialog`'s exact prop contract in `@/components/ui/dialog` before typing this file.

- [ ] **Step 3: Manual verification and commit**

Run: `cd clients/admin && npm run dev`, navigate to `/proxies/provider-accounts`, create a WebShare account with a throwaway JSON credential, confirm it appears, click "Sync now" and confirm it doesn't crash (a real sync will fail against a fake key — confirm the failure surfaces as a toast, not a blank page).

```bash
git add clients/admin/src/pages/proxies/provider-accounts.tsx clients/admin/src/components/proxies/provider-account-dialog.tsx
git commit -m "feat(admin): add the Provider Accounts ABM page"
```

### Task 28: Manual proxy ABM page

Same shape as Task 27 (list + create/edit dialog), applied to `manual-proxies.ts`. The dialog additionally has a free-text tags field (comma-separated), matching the list page's tag filter input from Task 26.

**Files:**
- Create: `clients/admin/src/pages/proxies/manual-proxies.tsx`
- Create: `clients/admin/src/components/proxies/manual-proxy-dialog.tsx`

**Interfaces:**
- Consumes: `createManualProxy`, `updateManualProxy`, `deleteManualProxy` (Task 25), `listProxies` filtered to the well-known Manual provider account (the frontend doesn't know the backend's fixed `ManualProviderAccount.Id` GUID — instead, this page calls `listProviderAccounts` once to find the row whose `providerType === "Manual"` and uses *its* id as the `providerAccountId` filter for `listProxies`).

- [ ] **Step 1: The create/edit dialog**

```tsx
// clients/admin/src/components/proxies/manual-proxy-dialog.tsx
import { useEffect } from "react";
import { useForm } from "react-hook-form";
import { zodResolver } from "@hookform/resolvers/zod";
import { z } from "zod";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { Loader2 } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Dialog } from "@/components/ui/dialog";
import { Input } from "@/components/ui/input";
import { Field } from "@/components/list";
import { ApiRequestError } from "@/lib/api-client";
import { createManualProxy, updateManualProxy } from "@/api/manual-proxies";
import type { ProxyDto } from "@/api/proxies";

const schema = z.object({
  host: z.string().trim().min(1, "Required.").max(255),
  port: z.coerce.number().int().min(1).max(65535),
  username: z.string().trim().optional(),
  plaintextPassword: z.string().trim().optional(),
  tagsInput: z.string().trim(),
});

type FormValues = z.infer<typeof schema>;

export function ManualProxyDialog({ open, onClose, proxy }: { open: boolean; onClose: () => void; proxy?: ProxyDto }) {
  const queryClient = useQueryClient();
  const isEdit = Boolean(proxy);
  const { register, handleSubmit, reset, formState: { errors, isSubmitting } } = useForm<FormValues>({
    resolver: zodResolver(schema),
    defaultValues: { host: "", port: 3128, username: "", plaintextPassword: "", tagsInput: "" },
  });

  useEffect(() => {
    reset(proxy
      ? { host: proxy.host, port: proxy.port, username: "", plaintextPassword: "", tagsInput: proxy.tags.join(", ") }
      : { host: "", port: 3128, username: "", plaintextPassword: "", tagsInput: "" });
  }, [proxy, reset]);

  const mutation = useMutation({
    mutationFn: (values: FormValues) => {
      const tagNames = values.tagsInput.split(",").map((t) => t.trim()).filter(Boolean);
      return isEdit
        ? updateManualProxy({ id: proxy!.id, host: values.host, port: values.port, protocol: "Http", username: values.username || undefined, plaintextPassword: values.plaintextPassword || undefined, tagNames })
        : createManualProxy({ host: values.host, port: values.port, protocol: "Http", username: values.username || undefined, plaintextPassword: values.plaintextPassword || undefined, tagNames });
    },
    onSuccess: () => {
      toast.success(isEdit ? "Manual proxy updated" : "Manual proxy created");
      void queryClient.invalidateQueries({ queryKey: ["proxies", "list"] });
      onClose();
    },
    onError: (err) => {
      const detail = err instanceof ApiRequestError ? (err.problem?.detail ?? err.problem?.title ?? err.message) : (err as Error).message;
      toast.error(isEdit ? "Update failed" : "Create failed", { description: detail });
    },
  });

  const submitting = isSubmitting || mutation.isPending;

  return (
    <Dialog open={open} onOpenChange={(o) => !o && onClose()} title={isEdit ? "Edit manual proxy" : "New manual proxy"}>
      <form onSubmit={handleSubmit((values) => mutation.mutate(values))} className="space-y-4">
        <div className="grid grid-cols-[1fr_120px] gap-3">
          <Field id="mp-host" label="Host" required error={errors.host?.message}>
            <Input id="mp-host" autoComplete="off" placeholder="10.0.0.5" {...register("host")} />
          </Field>
          <Field id="mp-port" label="Port" required error={errors.port?.message}>
            <Input id="mp-port" type="number" {...register("port")} />
          </Field>
        </div>
        <Field id="mp-username" label="Username" error={errors.username?.message}>
          <Input id="mp-username" autoComplete="off" {...register("username")} />
        </Field>
        <Field id="mp-password" label={isEdit ? "Replace password (leave blank to keep current)" : "Password"} error={errors.plaintextPassword?.message}>
          <Input id="mp-password" type="password" autoComplete="new-password" {...register("plaintextPassword")} />
        </Field>
        <Field id="mp-tags" label="Tags (comma-separated)" error={errors.tagsInput?.message}>
          <Input id="mp-tags" autoComplete="off" placeholder="pais:cl, funcionalidad:licitaciones" {...register("tagsInput")} />
        </Field>
        <div className="flex justify-end gap-2 pt-2">
          <Button type="button" variant="outline" onClick={onClose} disabled={submitting}>Cancel</Button>
          <Button type="submit" disabled={submitting} className="min-w-[8.5rem]">
            {submitting ? (<><Loader2 className="size-4 animate-spin" aria-hidden /><span>Saving…</span></>) : "Save"}
          </Button>
        </div>
      </form>
    </Dialog>
  );
}
```

- [ ] **Step 2: The list page**

```tsx
// clients/admin/src/pages/proxies/manual-proxies.tsx
import { useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { EntityPageHeader, ErrorBand, LoadingRow } from "@/components/list";
import { Button } from "@/components/ui/button";
import { ApiRequestError } from "@/lib/api-client";
import { ProxiesPermissions } from "@/lib/permissions";
import { useAuth } from "@/auth/use-auth";
import { listProviderAccounts } from "@/api/provider-accounts";
import { listProxies, type ProxyDto } from "@/api/proxies";
import { deleteManualProxy } from "@/api/manual-proxies";
import { ManualProxyDialog } from "@/components/proxies/manual-proxy-dialog";

function describeError(err: unknown): string {
  return err instanceof ApiRequestError ? (err.problem?.detail ?? err.problem?.title ?? err.message) : (err as Error).message;
}

export function ManualProxiesListPage() {
  const { user } = useAuth();
  const queryClient = useQueryClient();
  const [dialogState, setDialogState] = useState<{ open: boolean; proxy?: ProxyDto }>({ open: false });

  const canCreate = user?.permissions.includes(ProxiesPermissions.ManualProxies.Create) ?? false;
  const canUpdate = user?.permissions.includes(ProxiesPermissions.ManualProxies.Update) ?? false;
  const canDelete = user?.permissions.includes(ProxiesPermissions.ManualProxies.Delete) ?? false;

  const manualAccountQuery = useQuery({
    queryKey: ["proxies", "provider-accounts", "manual-account-id"],
    queryFn: async () => (await listProviderAccounts(1, 100)).items.find((a) => a.providerType === "Manual"),
  });

  const proxiesQuery = useQuery({
    queryKey: ["proxies", "manual-list", manualAccountQuery.data?.id],
    queryFn: () => listProxies({ providerAccountId: manualAccountQuery.data!.id, pageSize: 100 }),
    enabled: Boolean(manualAccountQuery.data?.id),
  });

  const deleteMutation = useMutation({
    mutationFn: (id: string) => deleteManualProxy(id),
    onSuccess: () => {
      toast.success("Manual proxy deleted");
      void queryClient.invalidateQueries({ queryKey: ["proxies", "manual-list"] });
    },
    onError: (err) => toast.error("Delete failed", { description: describeError(err) }),
  });

  const items = proxiesQuery.data?.items ?? [];

  return (
    <div className="space-y-4 sm:space-y-6">
      <EntityPageHeader
        icon="Server"
        title="Manual proxies"
        count={proxiesQuery.data?.totalCount}
        description="Self-hosted proxies with no provider API to sync from."
        actions={canCreate ? (
          <Button type="button" onClick={() => setDialogState({ open: true })}>New manual proxy</Button>
        ) : undefined}
      />

      {(proxiesQuery.isLoading || manualAccountQuery.isLoading) && <LoadingRow label="Loading manual proxies" />}
      {proxiesQuery.isError && <ErrorBand message={describeError(proxiesQuery.error)} />}

      {!proxiesQuery.isLoading && items.length === 0 && (
        <div className="py-16 text-center">
          <p className="font-display text-2xl">No manual proxies yet.</p>
          <p className="mt-2 text-sm text-[var(--color-muted-foreground)]">Add a self-hosted proxy to get started.</p>
        </div>
      )}

      {items.length > 0 && (
        <div className="overflow-hidden rounded-xl border border-[var(--color-border)] bg-[var(--color-card)] shadow-xs">
          <ol className="divide-y divide-[var(--color-border)]">
            {items.map((proxy: ProxyDto) => (
              <li key={proxy.id} className="flex flex-wrap items-center gap-3 px-4 py-3 text-sm">
                <span className="font-mono">{proxy.host}:{proxy.port}</span>
                <span className="text-xs font-mono uppercase text-[var(--color-muted-foreground)]">{proxy.status}</span>
                <span className="truncate text-[var(--color-muted-foreground)]">{proxy.tags.join(", ") || "—"}</span>
                <div className="ml-auto flex gap-3">
                  {canUpdate && <button type="button" className="text-sm underline" onClick={() => setDialogState({ open: true, proxy })}>Edit</button>}
                  {canDelete && <button type="button" className="text-sm text-[var(--color-destructive)] underline" onClick={() => deleteMutation.mutate(proxy.id)}>Delete</button>}
                </div>
              </li>
            ))}
          </ol>
        </div>
      )}

      <ManualProxyDialog open={dialogState.open} proxy={dialogState.proxy} onClose={() => setDialogState({ open: false })} />
    </div>
  );
}
```

- [ ] **Step 3: Manual verification and commit**

Run: `cd clients/admin && npm run dev`, navigate to `/proxies/manual`, create a manual proxy with a tag, confirm it shows up both here and in the main `/proxies` list with that tag.

```bash
git add clients/admin/src/pages/proxies/manual-proxies.tsx clients/admin/src/components/proxies/manual-proxy-dialog.tsx
git commit -m "feat(admin): add the Manual Proxies ABM page"
```

### Task 29: Playwright tests

Follows `tests/tenants/tenants-list.spec.ts`'s exact pattern: `seedAuthedSession` → `installAdminShellMocks` → `page.route` the specific API → `page.goto` → assertions. Adds the eight new `Permissions.Proxies.*` strings to `ADMIN_PERMS` in the shared shell-mocks helper first, or every one of these tests fails `RouteGuard` before it can assert anything.

**Files:**
- Modify: `clients/admin/tests/helpers/shell-mocks.ts`
- Create: `clients/admin/tests/proxies/proxies-list.spec.ts`
- Create: `clients/admin/tests/proxies/provider-accounts.spec.ts`
- Create: `clients/admin/tests/proxies/manual-proxies.spec.ts`

- [ ] **Step 1: Add the new permission strings to `ADMIN_PERMS`**

```ts
// add to the ADMIN_PERMS array in clients/admin/tests/helpers/shell-mocks.ts
"Permissions.Proxies.ProviderAccounts.View", "Permissions.Proxies.ProviderAccounts.Create",
"Permissions.Proxies.ProviderAccounts.Update", "Permissions.Proxies.ProviderAccounts.Delete",
"Permissions.Proxies.ManualProxies.View", "Permissions.Proxies.ManualProxies.Create",
"Permissions.Proxies.ManualProxies.Update", "Permissions.Proxies.ManualProxies.Delete",
```

- [ ] **Step 2: Proxies list page test**

```ts
// clients/admin/tests/proxies/proxies-list.spec.ts
import { expect, test } from "@playwright/test";
import { seedAuthedSession, TEST_USER } from "../helpers/auth-seed";
import { installAdminShellMocks, ADMIN_PERMS, paged } from "../helpers/shell-mocks";

const PROXY_CL = {
  id: "11111111-1111-1111-1111-111111111111",
  host: "10.0.0.5", port: 3128, protocol: "Http", status: "Active",
  providerAccountId: "acc-1", providerAccountName: "Manual", providerType: "Manual",
  tags: ["pais:cl"], createdAtUtc: "2026-01-01T00:00:00Z", lastRenewedAtUtc: null,
};

test.beforeEach(async ({ page }) => {
  await seedAuthedSession(page, { ...TEST_USER, permissions: [...ADMIN_PERMS] });
  await installAdminShellMocks(page);
  await page.route("**/api/v1/proxies/provider-accounts*", async (route) => {
    await route.fulfill({ status: 200, headers: { "Content-Type": "application/json" }, body: JSON.stringify(paged([])) });
  });
});

test.describe("proxies list", () => {
  test("renders a proxy row from the mock", async ({ page }) => {
    await page.route("**/api/v1/proxies/?*", async (route) => {
      await route.fulfill({ status: 200, headers: { "Content-Type": "application/json" }, body: JSON.stringify(paged([PROXY_CL])) });
    });

    await page.goto("/proxies");

    await expect(page.getByRole("heading", { name: "Proxies", exact: true })).toBeVisible({ timeout: 10_000 });
    await expect(page.getByText("10.0.0.5:3128", { exact: true })).toBeVisible();
  });

  test("shows the empty state when no proxies match", async ({ page }) => {
    await page.route("**/api/v1/proxies/?*", async (route) => {
      await route.fulfill({ status: 200, headers: { "Content-Type": "application/json" }, body: JSON.stringify(paged([])) });
    });

    await page.goto("/proxies");

    await expect(page.getByText("No proxies match these filters.", { exact: true })).toBeVisible({ timeout: 10_000 });
  });

  test("calls the disable endpoint when clicking Disable on an active proxy", async ({ page }) => {
    await page.route("**/api/v1/proxies/?*", async (route) => {
      await route.fulfill({ status: 200, headers: { "Content-Type": "application/json" }, body: JSON.stringify(paged([PROXY_CL])) });
    });
    let disableCalled = false;
    await page.route("**/api/v1/proxies/disable", async (route) => {
      disableCalled = true;
      await route.fulfill({ status: 204 });
    });

    await page.goto("/proxies");
    await page.getByRole("button", { name: "Disable", exact: true }).click();

    await expect.poll(() => disableCalled).toBe(true);
  });
});
```

- [ ] **Step 3: Provider accounts page test**

```ts
// clients/admin/tests/proxies/provider-accounts.spec.ts
import { expect, test } from "@playwright/test";
import { seedAuthedSession, TEST_USER } from "../helpers/auth-seed";
import { installAdminShellMocks, ADMIN_PERMS, paged } from "../helpers/shell-mocks";

test.beforeEach(async ({ page }) => {
  await seedAuthedSession(page, { ...TEST_USER, permissions: [...ADMIN_PERMS] });
  await installAdminShellMocks(page);
});

test.describe("provider accounts", () => {
  test("creates a new provider account", async ({ page }) => {
    await page.route("**/api/v1/proxies/provider-accounts*", async (route) => {
      if (route.request().method() === "GET") {
        await route.fulfill({ status: 200, headers: { "Content-Type": "application/json" }, body: JSON.stringify(paged([])) });
      } else {
        await route.fulfill({ status: 200, headers: { "Content-Type": "application/json" }, body: JSON.stringify("new-id") });
      }
    });

    await page.goto("/proxies/provider-accounts");
    await page.getByRole("button", { name: "New provider account", exact: true }).click();
    await page.getByLabel("Name").fill("WebShare - test");
    await page.getByLabel(/Credentials/).fill('{"ApiKey":"key-123"}');
    await page.getByRole("button", { name: "Save", exact: true }).click();

    await expect(page.getByText("Provider account created", { exact: true })).toBeVisible({ timeout: 10_000 });
  });
});
```

- [ ] **Step 4: Manual proxies page test**

```ts
// clients/admin/tests/proxies/manual-proxies.spec.ts
import { expect, test } from "@playwright/test";
import { seedAuthedSession, TEST_USER } from "../helpers/auth-seed";
import { installAdminShellMocks, ADMIN_PERMS, paged } from "../helpers/shell-mocks";

test.beforeEach(async ({ page }) => {
  await seedAuthedSession(page, { ...TEST_USER, permissions: [...ADMIN_PERMS] });
  await installAdminShellMocks(page);
  await page.route("**/api/v1/proxies/provider-accounts*", async (route) => {
    await route.fulfill({
      status: 200, headers: { "Content-Type": "application/json" },
      body: JSON.stringify(paged([{ id: "manual-acct", name: "Manual", providerType: "Manual", isEnabled: true, lastSyncedAtUtc: null, lastSyncStatus: null, consecutiveSyncFailures: 0 }])),
    });
  });
});

test.describe("manual proxies", () => {
  test("shows the empty state before any manual proxy exists", async ({ page }) => {
    await page.route("**/api/v1/proxies/?*", async (route) => {
      await route.fulfill({ status: 200, headers: { "Content-Type": "application/json" }, body: JSON.stringify(paged([])) });
    });

    await page.goto("/proxies/manual");

    await expect(page.getByText("No manual proxies yet.", { exact: true })).toBeVisible({ timeout: 10_000 });
  });
});
```

- [ ] **Step 5: Run the Playwright suite**

Run: `cd clients/admin && npx playwright test tests/proxies`
Expected: all pass.

- [ ] **Step 6: Commit**

```bash
git add clients/admin/tests/helpers/shell-mocks.ts clients/admin/tests/proxies
git commit -m "test(admin): add Playwright coverage for the Proxies admin pages"
```

---

## Milestone J — Full-Suite Verification

### Task 30: End-to-end verification

**Files:** none (verification only).

- [ ] **Step 1: Full backend build and test suite**

Run: `dotnet build src/FS.Proxy.slnx`
Expected: 0 warnings, 0 errors (`TreatWarningsAsErrors` is on — golden rule).

Run: `dotnet test src/FS.Proxy.slnx`
Expected: every test project passes, including `Architecture.Tests` (confirms `Modules.Proxies`/`Modules.Proxies.Contracts` respect module boundaries, layering, tenant-isolation-or-`IGlobalEntity`, handler/validator pairing, and the endpoint verb allow-list extended in Task 4) and `Notifications.Tests` (Task 17's handler test).

- [ ] **Step 2: Integration tests (requires Docker)**

Run: `dotnet test src/Tests/Integration.Tests`
Expected: PASS. If Proxies-specific integration tests weren't added as part of Tasks 1–24 (this plan deliberately kept integration coverage to the unit level throughout, per each task's own test steps — the repo's `integration-testing.md` pattern from `WebhookDeliveryTests.cs` is the model to reach for if end-to-end HTTP coverage through the real Testcontainers stack is wanted later), this step just confirms nothing else broke.

- [ ] **Step 3: Migrations apply cleanly from scratch**

Run: `dotnet run --project src/Host/FS.Proxy.DbMigrator -- apply`
Expected: exits 0. Then `dotnet run --project src/Host/FS.Proxy.DbMigrator -- list-pending` shows nothing pending for Proxies.

- [ ] **Step 4: Boot the whole stack and smoke-test the consumer API**

Run: `dotnet run --project src/Host/FS.Proxy.AppHost`

Once it's up:
1. Sign into `clients/admin`, create a Manual provider account note (seeded automatically per Task 8), create one manual proxy tagged `smoke-test`, enable it.
2. Via `/scalar` (JWT-authenticated), create an `ApiClient` and copy its plaintext key.
3. `curl` the consumer endpoints with the API key:
   ```bash
   curl -X POST https://localhost:7030/api/v1/proxies/request \
     -H "X-Api-Key: <the key from step 2>" -H "Content-Type: application/json" \
     -d '{"tags":["smoke-test"],"count":1,"strategy":"Sequential"}'
   ```
   Expected: `200 OK` with one proxy's connection details, including a decrypted `password`.
   ```bash
   curl -X POST https://localhost:7030/api/v1/proxies/<id-from-above>/feedback \
     -H "X-Api-Key: <the key>" -H "Content-Type: application/json" \
     -d '{"outcome":"Success"}'
   ```
   Expected: `204 No Content`.
4. Confirm a `ProxyUsageEvent` row exists for that proxy (via `psql` against the `proxies` schema, or a temporary admin query) with `Source = ConsumerFeedback`.
5. Repeat the `request` call with a JWT `Authorization: Bearer` header instead of `X-Api-Key` (grab a token the same way `clients/admin`'s sign-in does) — confirm it also succeeds, proving the dual-scheme policy from Task 22 actually accepts both.

- [ ] **Step 5: Frontend build and lint**

Run: `cd clients/admin && npm run build && npm run lint`
Expected: both succeed.

- [ ] **Step 6: Final commit (only if Step 4's manual smoke test surfaced any fixes)**

If every prior task's automated tests already passed and Step 4 needed no code changes, there's nothing to commit here — the plan is done. Otherwise, commit whatever fix was needed with a message describing what the smoke test caught.

---

## Deferred to Phase 2 (tracked here, not built in this plan)

Per the approved spec's "Scope" section — explicitly out of scope for this plan, listed here so nothing gets silently dropped:
- Decodo and Soax provider adapters (same `IProxyProviderAdapter` pattern as Tasks 13–15).
- Bulk "renew by group" button in the admin UI (the automated backend renewal from Task 19 already exists).
- Health/stats dashboard page.
- Advanced multi-step health checks (headless-browser or multi-request sequences) behind the `HealthCheckTarget` abstraction.
- Wiring TAG or the legacy .NET Framework 4.8/.NET 5 scrapers to actually consume this service.
- A dedicated `ApiClient` management UI page (Task 21 built the backend only — admins use `/scalar` for now).
